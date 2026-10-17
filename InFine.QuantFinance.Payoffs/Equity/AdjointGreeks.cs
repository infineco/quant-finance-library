using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Payoffs.Equity
{
    /// <summary>
    /// Payoff adjoints for reverse-mode (adjoint) pathwise Greeks: each function evaluates the discounted payoff of one log
    /// path and writes its direct derivatives with respect to every log-spot, the input of the backward sweep of the model
    /// (<c>LogNormalAdjoint</c>, <c>CorrelatedLogNormalAdjoint</c>, <c>HestonAdjoint</c> in InFine.QuantFinance.Models).
    /// Hybridizer-free (plain arrays with offsets) and kernel-compatible: no allocation, no exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One path, all first-order Greeks (Giles and Glasserman, "Smoking adjoints", Risk, 2006), hand-written so that it
    /// stays kernel code (no tape). With <c>path</c> and <c>adjoint</c> the offsets of two ranges of n + 1 entries of a
    /// per-thread scratch array:
    /// <code>
    /// double df = LogNormalAdjoint.Forward(spot, rate, dividend, sigma, dt, n, z, zOffset, scratch, path);
    /// double premium = AdjointGreeks.European(sign, strike, df, scratch, path, n, scratch, adjoint);
    /// LogNormalAdjoint.Reverse(spot, sigma, dt, n, z, zOffset, premium, scratch, adjoint, gradient, gradientOffset);
    /// </code>
    /// The forward sweep costs one valuation, the backward sweep a few multiply-adds per step and asset whatever the
    /// number of inputs; averaging the gradients of the paths gives the Monte Carlo Greeks
    /// (<see cref="AdjointMonteCarlo"/> does it on the host).
    /// </para>
    /// <para>
    /// Paths are log paths in the multi-asset layout (asset d at pillar t in column <c>t * D + d</c>, D = 1 for one
    /// asset), pillars 0..<c>steps</c>, the payoff paid at the last pillar and <c>discountFactor</c> the
    /// discount factor to it, as returned by the forward sweep. The spots are <c>exp</c> of the log path. Each function
    /// overwrites the whole adjoint range (zero where the payoff does not look), so the scratch needs no clearing.
    /// </para>
    /// <para>
    /// Validity is that of the pathwise estimator: the discounted payoff must be Lipschitz in the inputs. Calls, puts,
    /// arithmetic Asians and baskets qualify, and so do worst-of and best-of options (a minimum or maximum of smooth
    /// functions is Lipschitz, differentiable except where two performances tie, which has probability zero unless two
    /// assets are perfectly correlated with identical parameters). Kinks follow the dual-number convention, so the adjoint
    /// equals the tangent of <c>Dual4</c> code on every path: an intrinsic value of exactly 0 is differentiated as
    /// exercised (<c>Dual4.Max(intrinsic, 0)</c>) and on a tie the extreme is the asset seen first. Digitals and barriers
    /// are not Lipschitz; their pathwise derivative is silently wrong.
    /// </para>
    /// </remarks>
    public static class AdjointGreeks
    {
        /// <summary>
        /// European option paid at the last pillar: returns <c>DF max(sign (S_n - K), 0)</c> and writes
        /// <c>dP/dx_n = DF sign S_n</c> when exercised (0 otherwise, and 0 at the other pillars).
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        /// <param name="strike">Strike K.</param>
        /// <param name="discountFactor">Discount factor DF to the last pillar.</param>
        /// <param name="logPath">Log path, <c>steps + 1</c> entries from <paramref name="pathOffset"/>.</param>
        /// <param name="pathOffset">Index of ln S_0.</param>
        /// <param name="steps">Last pillar n.</param>
        /// <param name="logSpotAdjoint">Output, <c>steps + 1</c> entries from <paramref name="adjointOffset"/>.</param>
        /// <param name="adjointOffset">Index of the pillar-0 entry.</param>
        [Kernel]
        public static double European(int sign, double strike, double discountFactor, double[] logPath, int pathOffset, int steps,
            double[] logSpotAdjoint, int adjointOffset)
        {
            for (int k = 0; k < steps; ++k)
                logSpotAdjoint[adjointOffset + k] = 0.0;
            double spot = Math.Exp(logPath[pathOffset + steps]);
            double intrinsic = sign * (spot - strike);
            logSpotAdjoint[adjointOffset + steps] = intrinsic >= 0.0 ? discountFactor * sign * spot : 0.0;
            return discountFactor * (intrinsic > 0.0 ? intrinsic : 0.0);
        }

        /// <summary>
        /// Arithmetic average-price Asian option paid at the last pillar: returns <c>DF max(sign (A - K), 0)</c>,
        /// <c>A = (1/m) sum_j S(f_j)</c> over the fixing pillars (the arithmetic of <see cref="AsianMath.PathPayoff(double[], int[], int, int, bool, bool, double)"/>),
        /// and writes <c>dP/dx_k = DF sign S_k c_k / m</c> when exercised, c_k the number of fixings on pillar k.
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        /// <param name="strike">Strike K.</param>
        /// <param name="fixings">Fixing pillars, each in [0, steps]; repeats count twice.</param>
        /// <param name="fixingCount">Number m of fixings, at least 1.</param>
        /// <param name="discountFactor">Discount factor DF to the last pillar.</param>
        /// <param name="logPath">Log path, <c>steps + 1</c> entries from <paramref name="pathOffset"/>.</param>
        /// <param name="pathOffset">Index of ln S_0.</param>
        /// <param name="steps">Last pillar n.</param>
        /// <param name="logSpotAdjoint">Output, <c>steps + 1</c> entries from <paramref name="adjointOffset"/>.</param>
        /// <param name="adjointOffset">Index of the pillar-0 entry.</param>
        [Kernel]
        public static double ArithmeticAsian(int sign, double strike, int[] fixings, int fixingCount, double discountFactor,
            double[] logPath, int pathOffset, int steps, double[] logSpotAdjoint, int adjointOffset)
        {
            for (int k = 0; k <= steps; ++k)
                logSpotAdjoint[adjointOffset + k] = 0.0;
            double sum = 0.0;
            for (int j = 0; j < fixingCount; ++j)
            {
                int pillar = fixings[j];
                double spot = Math.Exp(logPath[pathOffset + pillar]);
                sum += spot;
                logSpotAdjoint[adjointOffset + pillar] += spot; // m dA/dx_k, before scaling
            }
            double intrinsic = sign * (sum / fixingCount - strike);
            double scale = intrinsic >= 0.0 ? discountFactor * sign / fixingCount : 0.0;
            for (int k = 0; k <= steps; ++k)
                logSpotAdjoint[adjointOffset + k] *= scale;
            return discountFactor * (intrinsic > 0.0 ? intrinsic : 0.0);
        }

        /// <summary>
        /// Arithmetic basket option paid at the last pillar: returns <c>DF max(sign (B - K), 0)</c>,
        /// <c>B = sum_d w_d S_d(T) / reference_d</c> (the arithmetic of
        /// <see cref="MultiAssetMath.BasketPathPayoff(double[], int, int, double[], double[], int, bool, double)"/>), and
        /// writes <c>dP/dx^d_n = DF sign w_d S_d(T) / reference_d</c> when exercised (0 elsewhere).
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        /// <param name="strike">Strike, in basket units.</param>
        /// <param name="weights">Weights, D entries (any sign).</param>
        /// <param name="reference">Reference levels, D positive entries.</param>
        /// <param name="assetCount">D.</param>
        /// <param name="discountFactor">Discount factor DF to the last pillar.</param>
        /// <param name="logPath">Log path, <c>(steps + 1) D</c> entries from <paramref name="pathOffset"/>.</param>
        /// <param name="pathOffset">Index of column 0.</param>
        /// <param name="steps">Last pillar n.</param>
        /// <param name="logSpotAdjoint">Output, <c>(steps + 1) D</c> entries from <paramref name="adjointOffset"/>.</param>
        /// <param name="adjointOffset">Index of column 0.</param>
        [Kernel]
        public static double Basket(int sign, double strike, double[] weights, double[] reference, int assetCount, double discountFactor,
            double[] logPath, int pathOffset, int steps, double[] logSpotAdjoint, int adjointOffset)
        {
            int last = steps * assetCount;
            for (int c = 0; c < last; ++c)
                logSpotAdjoint[adjointOffset + c] = 0.0;
            double sum = 0.0;
            for (int d = 0; d < assetCount; ++d)
            {
                double term = weights[d] * (Math.Exp(logPath[pathOffset + last + d]) / reference[d]);
                sum += term;
                logSpotAdjoint[adjointOffset + last + d] = term; // dB/dx^d, before scaling
            }
            double intrinsic = sign * (sum - strike);
            double scale = intrinsic >= 0.0 ? discountFactor * sign : 0.0;
            for (int d = 0; d < assetCount; ++d)
                logSpotAdjoint[adjointOffset + last + d] *= scale;
            return discountFactor * (intrinsic > 0.0 ? intrinsic : 0.0);
        }

        /// <summary>
        /// Best-of or worst-of option paid at the last pillar: returns <c>DF max(sign (E - K), 0)</c>, E the largest
        /// (<paramref name="best"/>) or smallest performance <c>S_d(T) / reference_d</c> (the arithmetic of
        /// <see cref="MultiAssetMath.RainbowPathPayoff(double[], int, int, double[], int, bool, double)"/>), and writes
        /// <c>dP/dx^e_n = DF sign E</c> for the asset e attaining the extreme when exercised (0 elsewhere). On a tie e is the
        /// first such asset.
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        /// <param name="best">true for a best-of, false for a worst-of.</param>
        /// <param name="strike">Strike, in performance units.</param>
        /// <param name="reference">Reference levels, D positive entries.</param>
        /// <param name="assetCount">D.</param>
        /// <param name="discountFactor">Discount factor DF to the last pillar.</param>
        /// <param name="logPath">Log path, <c>(steps + 1) D</c> entries from <paramref name="pathOffset"/>.</param>
        /// <param name="pathOffset">Index of column 0.</param>
        /// <param name="steps">Last pillar n.</param>
        /// <param name="logSpotAdjoint">Output, <c>(steps + 1) D</c> entries from <paramref name="adjointOffset"/>.</param>
        /// <param name="adjointOffset">Index of column 0.</param>
        [Kernel]
        public static double Rainbow(int sign, bool best, double strike, double[] reference, int assetCount, double discountFactor,
            double[] logPath, int pathOffset, int steps, double[] logSpotAdjoint, int adjointOffset)
        {
            int last = steps * assetCount;
            for (int c = 0; c < last + assetCount; ++c)
                logSpotAdjoint[adjointOffset + c] = 0.0;
            double extreme = Math.Exp(logPath[pathOffset + last]) / reference[0];
            int selected = 0;
            for (int d = 1; d < assetCount; ++d)
            {
                double performance = Math.Exp(logPath[pathOffset + last + d]) / reference[d];
                if (best ? performance > extreme : performance < extreme)
                {
                    extreme = performance;
                    selected = d;
                }
            }
            double intrinsic = sign * (extreme - strike);
            if (intrinsic >= 0.0)
                logSpotAdjoint[adjointOffset + last + selected] = discountFactor * sign * extreme;
            return discountFactor * (intrinsic > 0.0 ? intrinsic : 0.0);
        }
    }
}
