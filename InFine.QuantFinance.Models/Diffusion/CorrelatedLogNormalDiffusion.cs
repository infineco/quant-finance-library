using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Scheduling;
using System;

namespace InFine.QuantFinance.Models.Diffusion
{
    /// <summary>
    /// Hybridizer-free arithmetic of the correlated multi-asset log-normal diffusion, shared by the kernel and the tests.
    /// <para>
    /// Multi-asset storage convention (one path, D assets): the value of asset <c>d</c> at pillar <c>t</c> is column
    /// <c>t * D + d</c>, i.e. columns are time-major and asset-minor. Combined with the library's structure-of-arrays rule
    /// <c>index = simId + simCount * column</c>, consecutive simulations stay contiguous for every (pillar, asset), so a
    /// multi-asset spot is an ordinary <see cref="ILogSpot"/> / <see cref="ISpot"/> with <c>(steps + 1) * D</c> columns and
    /// its Brownian an ordinary <see cref="IBrownian"/> with <c>steps * D</c> columns (draw of asset d on step t in column
    /// <c>t * D + d</c>). <see cref="Column"/> computes the column.
    /// </para>
    /// </summary>
    public static class CorrelatedLogNormalMath
    {
        /// <summary>Column of asset <paramref name="asset"/> at pillar or step <paramref name="timeIndex"/> for D = <paramref name="assetCount"/>.</summary>
        [Kernel]
        public static int Column(int timeIndex, int asset, int assetCount)
        {
            return timeIndex * assetCount + asset;
        }

        /// <summary>
        /// Exact log-normal step of one asset: <c>ln S + (r - q - sigma^2 / 2) dt + sigma sqrt(dt) y</c>, with
        /// <paramref name="correlatedNormal"/> = y the asset's component of <c>L z</c>.
        /// </summary>
        [Kernel]
        public static double LogStep(double logSpot, double rate, double dividendYield, double sigma, double dt, double sqrtDt, double correlatedNormal)
        {
            return logSpot + (rate - dividendYield - 0.5 * sigma * sigma) * dt + sigma * sqrtDt * correlatedNormal;
        }

        /// <summary>
        /// Host reference of the kernel for one path: fills <paramref name="logPath"/> (columns <c>t * D + d</c>,
        /// <c>t = 0 .. steps</c>) from the initial log-spots, the per-step forward rates and the independent normals
        /// (<c>normals[t * D + d]</c>, <c>t = 0 .. steps - 1</c>), with the same operations in the same order as the kernel.
        /// </summary>
        /// <param name="logSpot0">ln S_d(0), D entries.</param>
        /// <param name="sigma">Volatilities, D entries.</param>
        /// <param name="dividendYield">Continuous dividend yields, D entries.</param>
        /// <param name="lower">Row-major lower Cholesky factor of the correlation (see <see cref="CholeskyCorrelation"/>).</param>
        /// <param name="assetCount">D.</param>
        /// <param name="dt">Step lengths, <paramref name="steps"/> entries.</param>
        /// <param name="stepRates">Drift rate of each step (the forward over the step), <paramref name="steps"/> entries.</param>
        /// <param name="steps">Number of steps.</param>
        /// <param name="normals">Independent standard normals, <c>steps * D</c> entries.</param>
        /// <param name="logPath">Output, <c>(steps + 1) * D</c> entries.</param>
        public static void DiffusePath(double[] logSpot0, double[] sigma, double[] dividendYield, double[] lower, int assetCount,
            double[] dt, double[] stepRates, int steps, double[] normals, double[] logPath)
        {
            for (int d = 0; d < assetCount; ++d)
                logPath[d] = logSpot0[d];
            for (int t = 0; t < steps; ++t)
            {
                double sqrtDt = Math.Sqrt(dt[t]);
                int from = t * assetCount, to = (t + 1) * assetCount;
                for (int i = 0; i < assetCount; ++i)
                {
                    double y = CholeskyCorrelation.Component(lower, assetCount, i, normals, from);
                    logPath[to + i] = LogStep(logPath[from + i], stepRates[t], dividendYield[i], sigma[i], dt[t], sqrtDt, y);
                }
            }
        }
    }

    /// <summary>
    /// Correlated multi-asset Black-Scholes diffusion of D assets:
    /// $$ dS_d / S_d = (r_t - q_d) dt + \sigma_d dW_d, \qquad d\langle W_i, W_j \rangle = C_{ij} dt $$
    /// with per-asset constant volatility and dividend yield, the drift rate read from <typeparamref name="Rate"/> (a
    /// <see cref="ConstantRate"/> or the step forwards of a <see cref="CurveRate"/>, so the discounted spots are
    /// martingales on the schedule), and the correlation applied as <c>y = L z</c> per (path, step) to D independent
    /// standard normals with the Cholesky factor <c>L</c> of <c>C</c> (<see cref="CholeskyCorrelation"/>).
    /// The log-normal step is exact, so the grid only needs the pillars the payoff observes.
    /// <para>
    /// Storage follows <see cref="CorrelatedLogNormalMath"/>: asset d at pillar t in column <c>t * D + d</c> of the log-spot,
    /// independent draws for step t in columns <c>t * D .. t * D + D - 1</c> of the Brownian (left unchanged: the
    /// correlated draws are formed on the fly, O(D^2 / 2) multiply-adds per step, with no scratch). The caller writes
    /// <c>ln S_d(0)</c> in columns <c>0 .. D - 1</c> (more generally, columns of <c>timeFrom</c>).
    /// </para>
    /// </summary>
    /// <typeparam name="Rate">Drift rate.</typeparam>
    /// <typeparam name="LogSpot">Log-spot storage, <c>(PilarCount + 1) * D</c> columns.</typeparam>
    /// <typeparam name="Brownian">Independent standard normals, <c>PilarCount * D</c> columns.</typeparam>
    /// <typeparam name="Schedule">Simulation schedule.</typeparam>
    [HybridRegisterTemplate(Specialize = typeof(CorrelatedLogNormalDiffusion<ConstantRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>))]
    [HybridRegisterTemplate(Specialize = typeof(CorrelatedLogNormalDiffusion<CurveRate, ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>))]
    public class CorrelatedLogNormalDiffusion<Rate, LogSpot, Brownian, Schedule> : ISimulatable
        where Rate : IRate
        where LogSpot : ILogSpot
        where Brownian : IBrownian
        where Schedule : ISchedule
    {
        int _assetCount;
        double[] _sigma;
        double[] _dividendYield;
        double[] _lower;

        Rate _rate;
        LogSpot _logSpot;
        Brownian _brownian;
        Schedule _schedule;

        /// <summary>
        /// Creates the diffusion.
        /// </summary>
        /// <param name="sigma">Volatility of each asset (D entries, non-negative).</param>
        /// <param name="dividendYield">Continuous dividend yield of each asset (D entries).</param>
        /// <param name="correlation">Factorised correlation of dimension D.</param>
        /// <param name="rate">Drift rate.</param>
        /// <param name="logSpot">Log-spot storage; columns 0 .. D - 1 hold ln S(0).</param>
        /// <param name="brownian">Independent standard normal draws.</param>
        /// <param name="schedule">Simulation schedule.</param>
        public CorrelatedLogNormalDiffusion(double[] sigma, double[] dividendYield, CholeskyCorrelation correlation,
            Rate rate, LogSpot logSpot, Brownian brownian, Schedule schedule)
        {
            if (sigma == null) throw new ArgumentNullException(nameof(sigma));
            if (dividendYield == null) throw new ArgumentNullException(nameof(dividendYield));
            if (correlation == null) throw new ArgumentNullException(nameof(correlation));
            int d = correlation.Dimension;
            if (sigma.Length != d) throw new ArgumentException($"Expected {d} volatilities.", nameof(sigma));
            if (dividendYield.Length != d) throw new ArgumentException($"Expected {d} dividend yields.", nameof(dividendYield));
            for (int i = 0; i < d; ++i)
                if (!(sigma[i] >= 0.0) || double.IsInfinity(sigma[i]))
                    throw new ArgumentOutOfRangeException(nameof(sigma), "Volatilities must be finite and non-negative.");
            _assetCount = d;
            _sigma = (double[])sigma.Clone();
            _dividendYield = (double[])dividendYield.Clone();
            _lower = correlation.Lower;
            _rate = rate;
            _logSpot = logSpot;
            _brownian = brownian;
            _schedule = schedule;
        }

        /// <summary>Number of assets D.</summary>
        public int AssetCount { get { return _assetCount; } }

        /// <summary>
        /// Diffuses the paths [simFrom, simTo) from pillar <paramref name="timeFrom"/> (known) to <paramref name="timeTo"/>
        /// (inclusive).
        /// </summary>
        [Kernel]
        public void Diffusion(int simFrom, int simTo, int timeFrom, int timeTo)
        {
            int count = _assetCount;
            for (alignedindex simId = simFrom + VectorUnit.ID; simId < simTo; simId += VectorUnit.Count)
            {
                for (int t = timeFrom; t < timeTo; ++t)
                {
                    double dt = _schedule.getDT(t);
                    double sqrtDt = _schedule.getSqrtDT(t);
                    double r = _rate[simId, t];
                    int from = t * count;
                    int to = from + count;
                    for (int i = 0; i < count; ++i)
                    {
                        double y = 0.0;
                        int row = i * count;
                        for (int j = 0; j <= i; ++j)
                            y += _lower[row + j] * _brownian[simId, from + j];
                        _logSpot[simId, to + i] = CorrelatedLogNormalMath.LogStep(
                            _logSpot[simId, from + i], r, _dividendYield[i], _sigma[i], dt, sqrtDt, y);
                    }
                }
            }
        }

        /// <summary>Diffuses the paths [simFrom, simTo) over the whole schedule.</summary>
        [Kernel]
        public void Simulate(int simFrom, int simTo)
        {
            Diffusion(simFrom, simTo, 0, _schedule.PilarCount);
        }
    }
}
