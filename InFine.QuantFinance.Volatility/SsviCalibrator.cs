using System;
using System.Collections.Generic;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.Volatility
{
    /// <summary>Settings of <see cref="SsviCalibrator.Calibrate"/>.</summary>
    public sealed class SsviCalibrationOptions
    {
        /// <summary>Quote weighting (applied slice by slice). Default <see cref="SmileWeighting.AsGiven"/>.</summary>
        public SmileWeighting Weighting { get; init; } = SmileWeighting.AsGiven;

        /// <summary>
        /// Restrict the fit to Gatheral-Jacquier's sufficient no-arbitrage region, <c>0 &lt; gamma &lt;= 1/2</c> and
        /// <c>eta (1 + |rho|) &lt;= 2</c>, so that the calibrated surface is free of static arbitrage by construction.
        /// Default true. When false, <c>eta &gt; 0</c> and <c>gamma</c> in [0, 1] are the only constraints.
        /// </summary>
        public bool EnforceNoArbitrage { get; init; } = true;
    }

    /// <summary>Outcome of <see cref="SsviCalibrator.Calibrate"/>.</summary>
    public sealed class SsviCalibrationResult
    {
        internal SsviCalibrationResult(SsviParameters parameters, double[] maturities, double[] thetas, double rmse,
            double maxVolError, double cost, LevenbergMarquardtStatus status, int iterations)
        {
            Parameters = parameters; _maturities = maturities; _thetas = thetas;
            RootMeanSquareTotalVarianceError = rmse; MaxAbsoluteVolatilityError = maxVolError;
            WeightedCost = cost; Status = status; Iterations = iterations;
        }

        readonly double[] _maturities, _thetas;

        /// <summary>Fitted SSVI parameters.</summary>
        public SsviParameters Parameters { get; }

        /// <summary>Slice maturities, increasing.</summary>
        public IReadOnlyList<double> Maturities => _maturities;

        /// <summary>Fitted ATM total variance of each slice, non-decreasing.</summary>
        public IReadOnlyList<double> AtTheMoneyTotalVariances => _thetas;

        /// <summary>Unweighted root-mean-square total-variance error over all quotes of all slices.</summary>
        public double RootMeanSquareTotalVarianceError { get; }

        /// <summary>Largest absolute implied-volatility error over all quotes.</summary>
        public double MaxAbsoluteVolatilityError { get; }

        /// <summary>Half the weighted sum of squared total-variance errors (the minimised objective).</summary>
        public double WeightedCost { get; }

        /// <summary>Why the Levenberg-Marquardt fit stopped.</summary>
        public LevenbergMarquardtStatus Status { get; }

        /// <summary>True unless the fit hit an iteration or evaluation limit.</summary>
        public bool Converged => Status != LevenbergMarquardtStatus.MaxIterations
                                 && Status != LevenbergMarquardtStatus.MaxFunctionEvaluations;

        /// <summary>Accepted Levenberg-Marquardt steps.</summary>
        public int Iterations { get; }

        /// <summary>The calibrated surface on <paramref name="forwards"/>.</summary>
        public SsviVolatilitySurface CreateSurface(IForwardCurve forwards) =>
            new SsviVolatilitySurface(forwards, Parameters, _maturities, _thetas);
    }

    /// <summary>
    /// Global SSVI fit to several expiries at once: weighted least squares in total variance over all quotes,
    /// by the bounded Levenberg-Marquardt solver of <c>InFine.QuantFinance.Numerics</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unknowns: <c>rho</c>, the curvature pair and one ATM total variance per slice. <c>theta</c> is written
    /// <c>theta_1 + sum_{j &lt;= i} delta_j</c> with <c>delta_j &gt;= 0</c>, so the fitted ATM curve is non-decreasing
    /// (Theorem 4.1, condition 1) and every constraint is a box. With <see cref="SsviCalibrationOptions.EnforceNoArbitrage"/>
    /// the curvature level is <c>eta = 2 u / (1 + |rho|)</c> with <c>u</c> in (0, 1] and <c>gamma</c> in (0, 1/2],
    /// which is exactly the region of eq. (4.5); otherwise <c>eta</c> itself is the unknown.
    /// </para>
    /// <para>
    /// Start: each <c>theta_i</c> from the market ATM total variance (linear interpolation in <c>k</c>, then a running
    /// maximum), and <c>(rho, eta, gamma)</c> from the best points of a coarse grid; the three best grid points are
    /// polished and the lowest cost wins.
    /// </para>
    /// </remarks>
    public static class SsviCalibrator
    {
        const double RhoBound = 1.0 - 1e-9, MinGamma = 1e-6, MinCurvature = 1e-8, MinTheta = 1e-12;
        const int Starts = 3;
        static readonly double[] RhoGrid = { -0.8, -0.5, -0.2, 0.2, 0.5, 0.8 };
        static readonly double[] CurvatureGrid = { 0.1, 0.3, 0.6, 0.95 };
        static readonly double[] GammaGrid = { 0.1, 0.3, 0.5 };

        /// <summary>Fits SSVI to <paramref name="slices"/> (any order, distinct maturities).</summary>
        /// <exception cref="ArgumentException">No slices, repeated maturities, or fewer quotes with positive weight than
        /// unknowns.</exception>
        public static SsviCalibrationResult Calibrate(IReadOnlyList<SmileQuotes> slices, SsviCalibrationOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(slices);
            options ??= new SsviCalibrationOptions();
            if (slices.Count == 0) throw new ArgumentException("At least one slice is needed.", nameof(slices));
            var sorted = new List<SmileQuotes>(slices);
            foreach (var s in sorted) ArgumentNullException.ThrowIfNull(s, nameof(slices));
            sorted.Sort((x, y) => x.Maturity.CompareTo(y.Maturity));
            for (int i = 1; i < sorted.Count; i++)
                if (sorted[i].Maturity == sorted[i - 1].Maturity)
                    throw new ArgumentException($"Maturity {sorted[i].Maturity} is given twice.", nameof(slices));

            int sliceCount = sorted.Count, unknowns = 3 + sliceCount;
            var sliceOf = new List<int>();
            var kList = new List<double>();
            var wList = new List<double>();
            var sqrtWeights = new List<double>();
            int active = 0;
            for (int s = 0; s < sliceCount; s++)
            {
                double[] weights = sorted[s].EffectiveWeights(options.Weighting);
                for (int i = 0; i < sorted[s].Count; i++)
                {
                    sliceOf.Add(s);
                    kList.Add(sorted[s].LogMoneyness(i));
                    wList.Add(sorted[s].TotalVariance(i));
                    sqrtWeights.Add(Math.Sqrt(weights[i]));
                    if (weights[i] > 0.0) active++;
                }
            }
            if (active < unknowns)
                throw new ArgumentException($"SSVI on {sliceCount} slices has {unknowns} unknowns; {active} quotes have a positive weight.", nameof(slices));

            int m = kList.Count;
            int[] slice = sliceOf.ToArray();
            double[] k = kList.ToArray(), w = wList.ToArray(), sw = sqrtWeights.ToArray();
            bool enforce = options.EnforceNoArbitrage;
            var thetaBuffer = new double[sliceCount];

            void Residuals(double[] x, double[] r)
            {
                var p = ToParameters(x, enforce);
                Thetas(x, thetaBuffer);
                for (int i = 0; i < m; i++)
                {
                    double theta = thetaBuffer[slice[i]];
                    double phi = p.Phi(theta), y = phi * k[i] + p.Rho;
                    double model = 0.5 * theta * (1.0 + p.Rho * phi * k[i] + Math.Sqrt(y * y + 1.0 - p.Rho * p.Rho));
                    r[i] = sw[i] * (model - w[i]);
                }
            }

            // ---- start ------------------------------------------------------------------------------------------
            var x0 = new double[unknowns];
            double previous = 0.0;
            for (int s = 0; s < sliceCount; s++)
            {
                double theta = Math.Max(Math.Max(sorted[s].AtTheMoneyTotalVariance(), previous), MinTheta);
                x0[3 + s] = s == 0 ? theta : theta - previous;
                previous = theta;
            }
            var residualBuffer = new double[m];
            var starts = new List<(double Cost, double[] X)>();
            foreach (double rho in RhoGrid)
                foreach (double curvature in CurvatureGrid)
                    foreach (double gamma in GammaGrid)
                    {
                        var x = (double[])x0.Clone();
                        x[0] = rho;
                        x[1] = enforce ? curvature : 2.0 * curvature / (1.0 + Math.Abs(rho));
                        x[2] = gamma;
                        Residuals(x, residualBuffer);
                        double cost = 0.0;
                        foreach (double e in residualBuffer) cost += e * e;
                        if (!double.IsFinite(cost)) continue;
                        starts.Add((cost, x));
                    }
            starts.Sort((a, b) => a.Cost.CompareTo(b.Cost));

            // ---- fit --------------------------------------------------------------------------------------------
            var lower = new double[unknowns];
            var upper = new double[unknowns];
            lower[0] = -RhoBound; upper[0] = RhoBound;
            // u stops just short of 1 so that eta (1 + |rho|) <= 2 survives the rounding of eta = 2 u / (1 + |rho|).
            lower[1] = MinCurvature; upper[1] = enforce ? 1.0 - 1e-12 : double.PositiveInfinity;
            lower[2] = enforce ? MinGamma : 0.0; upper[2] = enforce ? 0.5 : 1.0;
            lower[3] = MinTheta; upper[3] = double.PositiveInfinity;
            for (int s = 1; s < sliceCount; s++) { lower[3 + s] = 0.0; upper[3 + s] = double.PositiveInfinity; }
            var lmOptions = new LevenbergMarquardtOptions
            {
                FiniteDifference = FiniteDifferenceScheme.Central,
                MaxIterations = 500,
                LowerBounds = lower,
                UpperBounds = upper,
            };
            LevenbergMarquardtResult? fit = null;
            for (int i = 0; i < Math.Min(Starts, starts.Count); i++)
            {
                var candidate = LevenbergMarquardt.Minimize(Residuals, starts[i].X, m, null, lmOptions);
                if (fit == null || candidate.Cost < fit.Cost) fit = candidate;
            }
            if (fit == null) throw new InvalidOperationException("No finite SSVI start was found.");

            var parameters = ToParameters(fit.Parameters, enforce);
            var thetas = new double[sliceCount];
            Thetas(fit.Parameters, thetas);
            var maturities = new double[sliceCount];
            for (int s = 0; s < sliceCount; s++) maturities[s] = sorted[s].Maturity;

            double sse = 0.0, maxVolError = 0.0;
            int index = 0;
            for (int s = 0; s < sliceCount; s++)
            {
                var ssvi = parameters.Slice(thetas[s]);
                for (int i = 0; i < sorted[s].Count; i++, index++)
                {
                    double model = ssvi.TotalVariance(k[index]);
                    sse += (model - w[index]) * (model - w[index]);
                    double vol = Math.Sqrt(model / sorted[s].Maturity);
                    maxVolError = Math.Max(maxVolError, Math.Abs(vol - sorted[s].Volatilities[i]));
                }
            }
            return new SsviCalibrationResult(parameters, maturities, thetas, Math.Sqrt(sse / m), maxVolError,
                fit.Cost, fit.Status, fit.Iterations);
        }

        static SsviParameters ToParameters(double[] x, bool enforce)
        {
            double rho = x[0];
            double eta = enforce ? 2.0 * x[1] / (1.0 + Math.Abs(rho)) : x[1];
            return new SsviParameters(rho, eta, x[2]);
        }

        static void Thetas(double[] x, double[] thetas)
        {
            double sum = 0.0;
            for (int s = 0; s < thetas.Length; s++) { sum += x[3 + s]; thetas[s] = sum; }
        }
    }
}
