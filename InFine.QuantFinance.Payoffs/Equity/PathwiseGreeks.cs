using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Arithmetic.AutoDiff;

namespace InFine.QuantFinance.Payoffs.Equity
{
    /// <summary>
    /// Per-path discounted premiums on <see cref="Dual4"/> paths (<c>DualLogNormalMath.Path</c>):
    /// the pathwise (infinitesimal perturbation) Monte Carlo estimator of first-order Greeks. Averaging the tangents of the
    /// premium over paths estimates the derivative of the price along each seeded input, with the same paths as the price
    /// and in the same pass. Hybridizer-free (plain arrays), kernel-compatible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pathwise estimator is unbiased when the discounted payoff is Lipschitz in the inputs (a.s. differentiable, with an
    /// integrable bound): European and Asian calls and puts qualify, their kink being hit with probability 0. It does not
    /// apply to discontinuous payoffs (digitals, barriers: the derivative of the indicator is 0 almost everywhere and the
    /// estimator silently returns the wrong Greek) nor, for the same reason, to gamma of a call (the delta payoff is a
    /// step). Those need likelihood-ratio weights, smoothing of the payoff, or second-order duals on a smooth payoff; all are
    /// out of scope here.
    /// </para>
    /// <para>
    /// Usual seeding: direction 0 on the spot (delta), 1 on the volatility (vega), 2 on the rate (rho, through both the
    /// drift and the discount factor), 3 free (dividend yield, or unused).
    /// </para>
    /// </remarks>
    public static class PathwiseGreeks
    {
        /// <summary>
        /// Discounted European payoff <c>exp(-r T) max(sign (S_T - K), 0)</c> of a dual path, S_T =
        /// <paramref name="path"/>[<paramref name="maturityIndex"/>]. At S_T = K the derivative is that of the exercised
        /// branch (<see cref="Dual4.Max"/> convention).
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        [Kernel]
        public static Dual4 EuropeanPremium(int sign, double strike, Dual4 rate, double maturity, Dual4[] path, int maturityIndex)
        {
            return Dual4.Exp(-rate * maturity) * Dual4.Max(sign * (path[maturityIndex] - strike), 0.0);
        }

        /// <summary>
        /// Discounted Asian payoff <c>exp(-r T) </c><see cref="AsianMath.PathPayoff(Dual4[], int[], int, int, bool, bool, double)"/>
        /// of a dual path.
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        public static Dual4 AsianPremium(int sign, bool geometric, bool averageStrike, double strike, Dual4 rate, double maturity,
            Dual4[] path, int[] fixings, int maturityIndex)
        {
            return Dual4.Exp(-rate * maturity)
                * AsianMath.PathPayoff(path, fixings, maturityIndex, sign, geometric, averageStrike, strike);
        }
    }
}
