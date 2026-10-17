using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Arithmetic.AutoDiff;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Black-76 and generalised Black-Scholes-Merton prices on <see cref="Dual"/> inputs: the same formulas as
    /// <see cref="Black76Kernels"/> and <see cref="BlackScholesKernels"/>, so seeding one input (spot, strike, maturity,
    /// rate, dividend yield or volatility) returns the price and its derivative along that input, by forward-mode
    /// algorithmic differentiation instead of hand-written Greeks. Kernel-compatible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <see cref="Dual"/> pass gives one first-order sensitivity; seeding several inputs at once gives the directional
    /// derivative along that combination. The analytic kernels remain the production Greeks; this class is the template
    /// for differentiating pricing code that has no closed-form Greeks, and the cross-check of the AD layer.
    /// </para>
    /// <para>
    /// Regular inputs (v = sigma sqrt(T) &gt; 0, F &gt; 0, K &gt; 0) follow <see cref="Black76Kernels.Terms"/> operation by
    /// operation. Degenerate inputs return the intrinsic value of the forward, as the analytic kernels do, differentiated
    /// with the dual conventions: at F = K the derivative is the one-sided one of <c>max(s (F - K), 0)</c>
    /// (<see cref="Dual.Max"/> keeps its first argument on a tie, so a call has forward delta DF and a put -DF), whereas
    /// <see cref="Black76Kernels.Greeks"/> reports the symmetric limit DF s Phi(0) = DF s / 2. A NaN input gives NaN.
    /// </para>
    /// </remarks>
    public static class BlackScholesDual
    {
        /// <summary>
        /// Undiscounted Black price as a function of the forward, the strike and the total standard deviation
        /// v = sigma sqrt(T); dual counterpart of <see cref="Black76Kernels.NormalisedPrice"/>.
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        [Kernel]
        public static Dual NormalisedPrice(int sign, Dual forward, Dual strike, Dual stdDev)
        {
            if (double.IsNaN(stdDev.Value) || double.IsNaN(forward.Value) || double.IsNaN(strike.Value))
                return new Dual(double.NaN, double.NaN);

            if (stdDev.Value > 0.0 && forward.Value > 0.0 && strike.Value > 0.0)
            {
                Dual x = Dual.Log(forward / strike) / stdDev;
                Dual d1 = x + 0.5 * stdDev;
                Dual d2 = x - 0.5 * stdDev;
                Dual n1 = DualNormalDistribution.Cdf(sign * d1);
                Dual n2 = DualNormalDistribution.Cdf(sign * d2);
                return Dual.Max(sign * (forward * n1 - strike * n2), 0.0);
            }

            return Dual.Max(sign * (forward - strike), 0.0);
        }

        /// <summary>
        /// Black-76 price, discountFactor times <see cref="NormalisedPrice"/> with v = sigma sqrt(maturity); dual
        /// counterpart of <see cref="Black76Kernels.Price"/>.
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        [Kernel]
        public static Dual Black76Price(int sign, Dual forward, Dual strike, Dual discountFactor, Dual maturity, Dual sigma)
        {
            return discountFactor * NormalisedPrice(sign, forward, strike, sigma * SqrtMaturity(maturity));
        }

        /// <summary>
        /// Generalised Black-Scholes-Merton price, a Black-76 option on F = S exp((r - q) T) discounted at exp(-r T); dual
        /// counterpart of <see cref="BlackScholesKernels.Price"/>. With the maturity seeded the tangent is dV/dT, that is
        /// minus the calendar theta of <see cref="BlackScholesKernels.Greeks"/>.
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        [Kernel]
        public static Dual Price(int sign, Dual spot, Dual strike, Dual maturity, Dual rate, Dual dividendYield, Dual sigma)
        {
            Dual forward = spot * Dual.Exp((rate - dividendYield) * maturity);
            return Black76Price(sign, forward, strike, Dual.Exp(-rate * maturity), maturity, sigma);
        }

        /// <summary>sqrt(maturity), with a non-positive maturity treated as a constant 0 and NaN propagated
        /// (<see cref="Black76Kernels.SqrtMaturity"/>).</summary>
        [Kernel]
        public static Dual SqrtMaturity(Dual maturity)
        {
            if (maturity.Value > 0.0) return Dual.Sqrt(maturity);
            return double.IsNaN(maturity.Value) ? new Dual(double.NaN, double.NaN) : new Dual(0.0, 0.0);
        }
    }
}
