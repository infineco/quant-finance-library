using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Models.Concepts;
using System;

namespace InFine.QuantFinance.Models.Diffusion
{
    /// <summary>
    /// Hand-written adjoint (reverse mode) of the single-asset log-normal diffusion with a term structure: one volatility
    /// <c>sigma_k</c>, rate <c>r_k</c> and dividend yield <c>q_k</c> per step. One backward sweep per path gives the
    /// pathwise derivative of a discounted payoff with respect to the spot and to every volatility bucket (bucketed vegas),
    /// rate bucket (bucketed rhos) and dividend-yield bucket, at a cost that does not depend on the number of buckets
    /// (Giles and Glasserman, "Smoking adjoints", Risk, 2006). Hybridizer-free (plain arrays with offsets) and
    /// kernel-compatible: no allocation, no exception, the caller passes the scratch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Model, on pillars <c>t_0 = 0 &lt; t_1 &lt; ... &lt; t_n</c> with steps <c>dt_k = t_{k+1} - t_k</c> and
    /// <c>x_k = ln S(t_k)</c>, the exact step of <see cref="CorrelatedLogNormalMath.LogStep"/> with piecewise-constant
    /// parameters; the rates drive the drift and the discount factor, so the discounted spot is a martingale:
    /// $$ x_{k+1} = x_k + (r_k - q_k - \sigma_k^2 / 2) dt_k + \sigma_k \sqrt{dt_k} z_k, \qquad P = e^{-\sum_k r_k dt_k} g(S_0, \dots, S_n) $$
    /// </para>
    /// <para>
    /// Per path: <see cref="Forward"/> stores the log path and returns the discount factor; a payoff adjoint
    /// (<c>AdjointGreeks</c> in InFine.QuantFinance.Payoffs) returns <c>P</c> and writes the direct derivatives
    /// <c>dP/dx_k</c> of the payoff; <see cref="Reverse"/> then runs the steps backwards with the adjoint
    /// <c>xbar_k = xbar_{k+1} + dP/dx_k</c> of the state and accumulates
    /// <c>dP/dsigma_k += xbar_{k+1} (sqrt(dt_k) z_k - sigma_k dt_k)</c>, <c>dP/dr_k += (xbar_{k+1} - P) dt_k</c>
    /// (drift and discount factor), <c>dP/dq_k -= xbar_{k+1} dt_k</c> and <c>dP/dS_0 += xbar_0 / S_0</c>.
    /// The step has a unit Jacobian in the state, so the backward sweep reads the draws but not the path.
    /// </para>
    /// <para>
    /// Gradient layout (one path, or a sum over paths), n = steps: <c>[0]</c> spot, <c>[1 + k]</c> sigma_k,
    /// <c>[1 + n + k]</c> r_k, <c>[1 + 2 n + k]</c> q_k (<see cref="SpotIndex"/>, <see cref="SigmaIndex"/>,
    /// <see cref="RateIndex"/>, <see cref="DividendIndex"/>, <see cref="GradientLength"/>). <see cref="Reverse"/> adds to
    /// the gradient, so a kernel can accumulate its paths in place and the host divides by the path count.
    /// </para>
    /// </remarks>
    public static class LogNormalAdjoint
    {
        /// <summary>Gradient index of the initial spot.</summary>
        public const int SpotIndex = 0;

        /// <summary>Gradient index of the volatility of step <paramref name="step"/>.</summary>
        [Kernel]
        public static int SigmaIndex(int step)
        {
            return 1 + step;
        }

        /// <summary>Gradient index of the rate of step <paramref name="step"/>, for <paramref name="steps"/> steps.</summary>
        [Kernel]
        public static int RateIndex(int steps, int step)
        {
            return 1 + steps + step;
        }

        /// <summary>Gradient index of the dividend yield of step <paramref name="step"/>, for <paramref name="steps"/> steps.</summary>
        [Kernel]
        public static int DividendIndex(int steps, int step)
        {
            return 1 + 2 * steps + step;
        }

        /// <summary>Number of gradient entries, <c>1 + 3 steps</c>.</summary>
        [Kernel]
        public static int GradientLength(int steps)
        {
            return 1 + 3 * steps;
        }

        /// <summary>
        /// Forward sweep of one path: writes <c>ln S(t_k)</c>, k = 0..steps, to
        /// <paramref name="logPath"/>[<paramref name="pathOffset"/> + k] and returns the discount factor
        /// <c>exp(-sum_k r_k dt_k)</c> to the last pillar. Same operations as <see cref="CorrelatedLogNormalMath.LogStep"/>,
        /// so the values are those of the double diffusion.
        /// </summary>
        /// <param name="spot">Initial spot S_0 &gt; 0.</param>
        /// <param name="rate">Rate of each step, <paramref name="steps"/> entries.</param>
        /// <param name="dividendYield">Dividend yield of each step, <paramref name="steps"/> entries.</param>
        /// <param name="sigma">Volatility of each step, <paramref name="steps"/> entries.</param>
        /// <param name="dt">Step lengths, <paramref name="steps"/> positive entries.</param>
        /// <param name="steps">Number of steps n.</param>
        /// <param name="normals">Standard normal draws z_k at <paramref name="normalOffset"/> + k.</param>
        /// <param name="normalOffset">Index of z_0.</param>
        /// <param name="logPath">Output, <c>steps + 1</c> entries from <paramref name="pathOffset"/>.</param>
        /// <param name="pathOffset">Index of ln S_0 in <paramref name="logPath"/>.</param>
        [Kernel]
        public static double Forward(double spot, double[] rate, double[] dividendYield, double[] sigma, double[] dt, int steps,
            double[] normals, int normalOffset, double[] logPath, int pathOffset)
        {
            double logSpot = Math.Log(spot);
            double integratedRate = 0.0;
            logPath[pathOffset] = logSpot;
            for (int k = 0; k < steps; ++k)
            {
                logSpot = CorrelatedLogNormalMath.LogStep(logSpot, rate[k], dividendYield[k], sigma[k], dt[k], Math.Sqrt(dt[k]),
                    normals[normalOffset + k]);
                logPath[pathOffset + k + 1] = logSpot;
                integratedRate += rate[k] * dt[k];
            }
            return Math.Exp(-integratedRate);
        }

        /// <summary>
        /// Backward sweep of one path. On entry <paramref name="logSpotAdjoint"/>[<paramref name="adjointOffset"/> + k]
        /// holds the direct derivative <c>dP/dx_k</c> of the discounted payoff (through the payoff only), k = 0..steps; on
        /// return it holds the total derivative <c>dP/d ln S(t_k)</c>. The derivatives of <paramref name="premium"/> with
        /// respect to the inputs are added to <paramref name="gradient"/> from <paramref name="gradientOffset"/>, in the
        /// layout of <see cref="LogNormalAdjoint"/>.
        /// </summary>
        /// <param name="spot">Initial spot S_0, as given to <see cref="Forward"/>.</param>
        /// <param name="sigma">Volatility of each step, as given to <see cref="Forward"/>.</param>
        /// <param name="dt">Step lengths, as given to <see cref="Forward"/>.</param>
        /// <param name="steps">Number of steps n.</param>
        /// <param name="normals">The draws of the forward sweep.</param>
        /// <param name="normalOffset">Index of z_0.</param>
        /// <param name="premium">Discounted payoff P of the path (enters the rate derivatives through the discount factor).</param>
        /// <param name="logSpotAdjoint">Log-spot adjoints, <c>steps + 1</c> entries (input and output).</param>
        /// <param name="adjointOffset">Index of the pillar-0 entry.</param>
        /// <param name="gradient">Accumulator, <see cref="GradientLength"/> entries from <paramref name="gradientOffset"/>.</param>
        /// <param name="gradientOffset">Index of the spot entry.</param>
        [Kernel]
        public static void Reverse(double spot, double[] sigma, double[] dt, int steps, double[] normals, int normalOffset,
            double premium, double[] logSpotAdjoint, int adjointOffset, double[] gradient, int gradientOffset)
        {
            for (int k = steps - 1; k >= 0; --k)
            {
                double xBar = logSpotAdjoint[adjointOffset + k + 1]; // total: direct + carried from the later steps
                double h = dt[k];
                gradient[gradientOffset + 1 + k] += xBar * (Math.Sqrt(h) * normals[normalOffset + k] - sigma[k] * h);
                gradient[gradientOffset + 1 + steps + k] += (xBar - premium) * h;
                gradient[gradientOffset + 1 + 2 * steps + k] -= xBar * h;
                logSpotAdjoint[adjointOffset + k] += xBar;
            }
            gradient[gradientOffset] += logSpotAdjoint[adjointOffset] / spot;
        }
    }

    /// <summary>
    /// Hand-written adjoint (reverse mode) of the correlated multi-asset log-normal diffusion of
    /// <see cref="CorrelatedLogNormalDiffusion{Rate, LogSpot, Brownian, Schedule}"/>: one backward sweep per path gives the
    /// pathwise derivative of a discounted payoff with respect to the D initial spots, the D volatilities, the D dividend
    /// yields and the rate of every step, at a cost independent of D (no Cholesky product in the backward sweep).
    /// Hybridizer-free (plain arrays with offsets), kernel-compatible: no allocation, the caller passes the scratch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Model of <see cref="CorrelatedLogNormalMath.DiffusePath"/>: per step t, <c>y_t = L z_t</c> and, for each asset d,
    /// <c>x^d_{t+1} = x^d_t + (r_t - q_d - sigma_d^2 / 2) dt_t + sigma_d sqrt(dt_t) y^d_t</c>; the step rates also give the
    /// discount factor <c>exp(-sum_t r_t dt_t)</c>. Layout as everywhere for multi-asset paths: asset d at pillar t in
    /// column <c>t * D + d</c>, draws of step t in <c>t * D .. t * D + D - 1</c>.
    /// </para>
    /// <para>
    /// <see cref="Forward"/> stores the log path and the correlated draws <c>y</c> (so the backward sweep never repeats the
    /// O(D^2) product), a payoff adjoint writes the direct <c>dP/dx^d_t</c>, and <see cref="Reverse"/> accumulates
    /// <c>dP/dsigma_d += sum_t xbar^d_{t+1} (sqrt(dt_t) y^d_t - sigma_d dt_t)</c>, <c>dP/dq_d -= sum_t xbar^d_{t+1} dt_t</c>,
    /// <c>dP/dr_t += (sum_d xbar^d_{t+1} - P) dt_t</c> and <c>dP/dS_d(0) += xbar^d_0 / S_d(0)</c>. The correlation is not
    /// differentiated (its adjoint would need the reverse of the Cholesky factorisation).
    /// </para>
    /// <para>
    /// Gradient layout: <c>[d]</c> spot of asset d, <c>[D + d]</c> sigma_d, <c>[2 D + d]</c> q_d, <c>[3 D + t]</c> r_t
    /// (<see cref="SpotIndex"/>, <see cref="SigmaIndex"/>, <see cref="DividendIndex"/>, <see cref="RateIndex"/>,
    /// <see cref="GradientLength"/>).
    /// </para>
    /// </remarks>
    public static class CorrelatedLogNormalAdjoint
    {
        /// <summary>Gradient index of the initial spot of <paramref name="asset"/>.</summary>
        [Kernel]
        public static int SpotIndex(int asset)
        {
            return asset;
        }

        /// <summary>Gradient index of the volatility of <paramref name="asset"/> among <paramref name="assetCount"/>.</summary>
        [Kernel]
        public static int SigmaIndex(int assetCount, int asset)
        {
            return assetCount + asset;
        }

        /// <summary>Gradient index of the dividend yield of <paramref name="asset"/> among <paramref name="assetCount"/>.</summary>
        [Kernel]
        public static int DividendIndex(int assetCount, int asset)
        {
            return 2 * assetCount + asset;
        }

        /// <summary>Gradient index of the rate of step <paramref name="step"/> for <paramref name="assetCount"/> assets.</summary>
        [Kernel]
        public static int RateIndex(int assetCount, int step)
        {
            return 3 * assetCount + step;
        }

        /// <summary>Number of gradient entries, <c>3 D + steps</c>.</summary>
        [Kernel]
        public static int GradientLength(int assetCount, int steps)
        {
            return 3 * assetCount + steps;
        }

        /// <summary>
        /// Forward sweep of one path, with the operations of <see cref="CorrelatedLogNormalMath.DiffusePath"/> in the same
        /// order: writes the log path (<c>(steps + 1) D</c> entries) and the correlated draws <c>y = L z</c>
        /// (<c>steps D</c> entries), and returns the discount factor <c>exp(-sum_t r_t dt_t)</c> to the last pillar.
        /// </summary>
        /// <param name="spot">Initial spots, D positive entries.</param>
        /// <param name="sigma">Volatilities, D entries.</param>
        /// <param name="dividendYield">Dividend yields, D entries.</param>
        /// <param name="lower">Row-major lower Cholesky factor of the correlation (<see cref="CholeskyCorrelation.Lower"/>).</param>
        /// <param name="assetCount">D.</param>
        /// <param name="dt">Step lengths, <paramref name="steps"/> positive entries.</param>
        /// <param name="stepRates">Rate of each step (drift and discounting), <paramref name="steps"/> entries.</param>
        /// <param name="steps">Number of steps.</param>
        /// <param name="normals">Independent standard normals, <c>steps D</c> entries from <paramref name="normalOffset"/>.</param>
        /// <param name="normalOffset">Index of the first draw.</param>
        /// <param name="logPath">Output log path from <paramref name="pathOffset"/>.</param>
        /// <param name="pathOffset">Index of column 0.</param>
        /// <param name="correlated">Output correlated draws from <paramref name="correlatedOffset"/>.</param>
        /// <param name="correlatedOffset">Index of the first correlated draw.</param>
        [Kernel]
        public static double Forward(double[] spot, double[] sigma, double[] dividendYield, double[] lower, int assetCount,
            double[] dt, double[] stepRates, int steps, double[] normals, int normalOffset,
            double[] logPath, int pathOffset, double[] correlated, int correlatedOffset)
        {
            for (int d = 0; d < assetCount; ++d)
                logPath[pathOffset + d] = Math.Log(spot[d]);
            double integratedRate = 0.0;
            for (int t = 0; t < steps; ++t)
            {
                double sqrtDt = Math.Sqrt(dt[t]);
                int from = t * assetCount, to = from + assetCount;
                for (int i = 0; i < assetCount; ++i)
                {
                    double y = CholeskyCorrelation.Component(lower, assetCount, i, normals, normalOffset + from);
                    correlated[correlatedOffset + from + i] = y;
                    logPath[pathOffset + to + i] = CorrelatedLogNormalMath.LogStep(logPath[pathOffset + from + i], stepRates[t],
                        dividendYield[i], sigma[i], dt[t], sqrtDt, y);
                }
                integratedRate += stepRates[t] * dt[t];
            }
            return Math.Exp(-integratedRate);
        }

        /// <summary>
        /// Backward sweep of one path. On entry <paramref name="logSpotAdjoint"/> holds, in the log-path layout, the direct
        /// derivatives <c>dP/dx^d_t</c> of the discounted payoff; on return the total derivatives <c>dP/d ln S_d(t)</c>.
        /// The derivatives of <paramref name="premium"/> are added to <paramref name="gradient"/> in the layout of
        /// <see cref="CorrelatedLogNormalAdjoint"/>.
        /// </summary>
        /// <param name="spot">Initial spots, as given to <see cref="Forward"/>.</param>
        /// <param name="sigma">Volatilities, as given to <see cref="Forward"/>.</param>
        /// <param name="assetCount">D.</param>
        /// <param name="dt">Step lengths, as given to <see cref="Forward"/>.</param>
        /// <param name="steps">Number of steps.</param>
        /// <param name="correlated">Correlated draws written by <see cref="Forward"/>.</param>
        /// <param name="correlatedOffset">Index of the first correlated draw.</param>
        /// <param name="premium">Discounted payoff P of the path.</param>
        /// <param name="logSpotAdjoint">Log-spot adjoints, <c>(steps + 1) D</c> entries (input and output).</param>
        /// <param name="adjointOffset">Index of column 0.</param>
        /// <param name="gradient">Accumulator, <see cref="GradientLength"/> entries from <paramref name="gradientOffset"/>.</param>
        /// <param name="gradientOffset">Index of the first entry.</param>
        [Kernel]
        public static void Reverse(double[] spot, double[] sigma, int assetCount, double[] dt, int steps,
            double[] correlated, int correlatedOffset, double premium,
            double[] logSpotAdjoint, int adjointOffset, double[] gradient, int gradientOffset)
        {
            for (int t = steps - 1; t >= 0; --t)
            {
                double h = dt[t];
                double sqrtH = Math.Sqrt(h);
                int from = t * assetCount, to = from + assetCount;
                double rateBar = -premium;
                for (int i = 0; i < assetCount; ++i)
                {
                    double xBar = logSpotAdjoint[adjointOffset + to + i];
                    gradient[gradientOffset + assetCount + i] += xBar * (sqrtH * correlated[correlatedOffset + from + i] - sigma[i] * h);
                    gradient[gradientOffset + 2 * assetCount + i] -= xBar * h;
                    rateBar += xBar;
                    logSpotAdjoint[adjointOffset + from + i] += xBar;
                }
                gradient[gradientOffset + 3 * assetCount + t] += rateBar * h;
            }
            for (int d = 0; d < assetCount; ++d)
                gradient[gradientOffset + d] += logSpotAdjoint[adjointOffset + d] / spot[d];
        }
    }
}
