using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Kernel-compatible Black (1976) formulas for a European option on a forward F, and their analytic
    /// sensitivities. No allocation, no exceptions, only <c>Math.*</c> and <see cref="NormalDistribution"/> calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With v = sigma sqrt(T), d1 = (ln(F/K) + v^2/2) / v and d2 = d1 - v, the undiscounted price of an option of
    /// sign s (+1 call, -1 put) is <c>s (F Phi(s d1) - K Phi(s d2))</c> and the price is DF times that.
    /// Evaluating Phi at <c>s d</c> (the lower tail for out-of-the-money options) is what keeps deep
    /// out-of-the-money values accurate: a small probability is never obtained as 1 - Phi. The price is clamped
    /// at 0 to absorb the rounding of the difference of two nearly equal tails.
    /// </para>
    /// <para>
    /// Degenerate inputs (v = 0, that is sigma = 0 or T = 0, or a non-positive F or K) return the intrinsic value
    /// of the forward, <c>max(s (F - K), 0)</c>, never NaN; a NaN input gives NaN. In that limit the
    /// probabilities are step functions (0.5 exactly at F = K), gamma is 0 away from F = K and +Infinity at
    /// F = K, vanna and volga are 0, and vega is 0 unless F = K with T &gt; 0 and sigma = 0, where it takes its
    /// limit F phi(0) sqrt(T). The volatility-decay part of theta, vega sigma / (2 sqrt(T)), is dropped at T = 0 (it is
    /// infinite at the money and 0 elsewhere). Very large v (hundreds) is fine: the call tends to F and the put to K.
    /// v = +Infinity is not supported.
    /// </para>
    /// </remarks>
    // Not static so that a HybRunner can wrap an instance to reach the [EntryPoint] array kernels
    // (runner.Wrap(new Black76Kernels()).<EntryPoint>(...), as for FullRevaluationKernels); it has no state.
    public sealed class Black76Kernels
    {
        /// <summary>
        /// d1, d2 and the probabilities Phi(s d1), Phi(s d2), with the degenerate limits described on the class.
        /// </summary>
        [Kernel]
        public static void Terms(int sign, double forward, double strike, double stdDev,
            out double d1, out double d2, out double n1, out double n2)
        {
            if (double.IsNaN(stdDev) || double.IsNaN(forward) || double.IsNaN(strike))
            {
                d1 = double.NaN;
                d2 = double.NaN;
                n1 = double.NaN;
                n2 = double.NaN;
                return;
            }

            if (stdDev > 0.0 && forward > 0.0 && strike > 0.0)
            {
                double x = Math.Log(forward / strike) / stdDev;
                d1 = x + 0.5 * stdDev;
                d2 = x - 0.5 * stdDev;
            }
            else
            {
                double d = forward > strike ? double.PositiveInfinity
                         : (forward < strike ? double.NegativeInfinity : 0.0);
                d1 = d;
                d2 = d;
            }

            n1 = NormalDistribution.Cdf(sign * d1);
            n2 = NormalDistribution.Cdf(sign * d2);
        }

        /// <summary>
        /// Undiscounted Black price of a call (sign = +1) or put (sign = -1) as a function of the forward, the strike and
        /// the total standard deviation v = sigma sqrt(T). This is the normalised form used by implied-volatility solvers.
        /// </summary>
        [Kernel]
        public static double NormalisedPrice(int sign, double forward, double strike, double stdDev)
        {
            double d1, d2, n1, n2;
            Terms(sign, forward, strike, stdDev, out d1, out d2, out n1, out n2);
            return Math.Max(0.0, sign * (forward * n1 - strike * n2));
        }

        /// <summary>
        /// Derivative of <see cref="NormalisedPrice"/> with respect to the standard deviation v: F phi(d1). Identical for
        /// calls and puts. This is what an implied-volatility Newton step needs (the vega is DF sqrt(T) times this).
        /// </summary>
        [Kernel]
        public static double NormalisedVega(double forward, double strike, double stdDev)
        {
            double d1, d2, n1, n2;
            Terms(1, forward, strike, stdDev, out d1, out d2, out n1, out n2);
            return forward * NormalDistribution.Pdf(d1);
        }

        /// <summary>
        /// Call and put undiscounted Black prices.
        /// </summary>
        [Kernel]
        public static void NormalisedCallPut(out double call, out double put,
            double forward, double strike, double stdDev)
        {
            double d1, d2, n1, n2, m1, m2;
            Terms(1, forward, strike, stdDev, out d1, out d2, out n1, out n2);
            // Phi(-d) = 1 - Phi(d): the reflected tails are evaluated directly, not by subtraction.
            m1 = NormalDistribution.Cdf(-d1);
            m2 = NormalDistribution.Cdf(-d2);
            call = Math.Max(0.0, forward * n1 - strike * n2);
            put = Math.Max(0.0, strike * m2 - forward * m1);
        }

        /// <summary>
        /// Black-76 price: discountFactor times <see cref="NormalisedPrice"/> with v = sigma sqrt(maturity).
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        /// <param name="forward">forward of the underlying for the option expiry.</param>
        /// <param name="strike">strike.</param>
        /// <param name="discountFactor">discount factor from the payment date (the expiry) to the valuation date.</param>
        /// <param name="maturity">time to expiry in years.</param>
        /// <param name="sigma">lognormal volatility of the forward.</param>
        [Kernel]
        public static double Price(int sign, double forward, double strike, double discountFactor,
            double maturity, double sigma)
        {
            return discountFactor * NormalisedPrice(sign, forward, strike, sigma * SqrtMaturity(maturity));
        }

        /// <summary>
        /// Black-76 price and analytic Greeks. The discount factor is exp(-rate * maturity).
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        /// <param name="forward">forward of the underlying for the option expiry.</param>
        /// <param name="strike">strike.</param>
        /// <param name="rate">continuously compounded discount rate; DF = exp(-rate * maturity).</param>
        /// <param name="maturity">time to expiry in years.</param>
        /// <param name="sigma">lognormal volatility of the forward.</param>
        /// <param name="price">DF * Black price.</param>
        /// <param name="delta">dV/dF.</param>
        /// <param name="gamma">d2V/dF2.</param>
        /// <param name="vega">dV/dsigma.</param>
        /// <param name="theta">dV/dt = -dV/dT per year at fixed F and K: rate * price - vega * sigma / (2 sqrt(T)).</param>
        /// <param name="rho">dV/drate at fixed F: -maturity * price (the forward does not move with the rate).</param>
        /// <param name="vanna">d2V/(dF dsigma).</param>
        /// <param name="volga">d2V/dsigma2.</param>
        /// <param name="dualDelta">dV/dK.</param>
        [Kernel]
        public static void Greeks(int sign, double forward, double strike, double rate, double maturity, double sigma,
            out double price, out double delta, out double gamma, out double vega, out double theta,
            out double rho, out double vanna, out double volga, out double dualDelta)
        {
            double sqrtT = SqrtMaturity(maturity);
            double v = sigma * sqrtT;
            double df = Math.Exp(-rate * maturity);

            double d1, d2, n1, n2;
            Terms(sign, forward, strike, v, out d1, out d2, out n1, out n2);
            double pdf = NormalDistribution.Pdf(d1); // exp(-inf) = 0 in the degenerate limits

            price = df * Math.Max(0.0, sign * (forward * n1 - strike * n2));
            delta = df * sign * n1;
            dualDelta = -df * sign * n2;
            vega = df * forward * pdf * sqrtT;
            rho = -maturity * price;

            // vega * sigma / (2 sqrt(T)); at expiry (sqrt(T) = 0) the term is dropped, see the class remarks.
            double volTime = sqrtT > 0.0 ? 0.5 * df * forward * pdf * sigma / sqrtT : 0.0;
            theta = rate * price - volTime;

            if (v > 0.0 && forward > 0.0 && strike > 0.0)
            {
                gamma = df * pdf / (forward * v);
                vanna = -df * pdf * d2 / sigma;
                volga = vega * d1 * d2 / sigma;
            }
            else
            {
                gamma = forward == strike ? double.PositiveInfinity : 0.0;
                vanna = 0.0;
                volga = 0.0;
            }
        }

        /// <summary>
        /// Evaluates one option of a batch: reads element <paramref name="index"/> of the input arrays and writes
        /// the Black-76 price to <paramref name="price"/>.
        /// </summary>
        [Kernel]
        public static void PriceAt(int index, double[] price, int[] sign, double[] forward, double[] strike,
            double[] discountFactor, double[] maturity, double[] sigma)
        {
            price[index] = Price(sign[index], forward[index], strike[index], discountFactor[index],
                maturity[index], sigma[index]);
        }

        /// <summary>
        /// Evaluates a batch of Black-76 prices, one option per array element.
        /// </summary>
        [EntryPoint("Black76Kernels_Price_Array")]
        public static void PriceArray(int count, double[] price, int[] sign, double[] forward, double[] strike,
            double[] discountFactor, double[] maturity, double[] sigma)
        {
            for (int k = threadIdx.x + blockDim.x * blockIdx.x; k < count; k += blockDim.x * gridDim.x)
            {
                PriceAt(k, price, sign, forward, strike, discountFactor, maturity, sigma);
            }
        }

        /// <summary>
        /// Evaluates one option of a batch and writes its price and Greeks to the output arrays.
        /// </summary>
        [Kernel]
        public static void GreeksAt(int index, double[] price, double[] delta, double[] gamma, double[] vega,
            double[] theta, double[] rho, double[] vanna, double[] volga, double[] dualDelta,
            int[] sign, double[] forward, double[] strike, double[] rate, double[] maturity, double[] sigma)
        {
            double p, de, ga, ve, th, rh, va, vo, du;
            Greeks(sign[index], forward[index], strike[index], rate[index], maturity[index], sigma[index],
                out p, out de, out ga, out ve, out th, out rh, out va, out vo, out du);
            price[index] = p;
            delta[index] = de;
            gamma[index] = ga;
            vega[index] = ve;
            theta[index] = th;
            rho[index] = rh;
            vanna[index] = va;
            volga[index] = vo;
            dualDelta[index] = du;
        }

        /// <summary>
        /// Evaluates a batch of Black-76 prices and Greeks, one option per array element.
        /// </summary>
        [EntryPoint("Black76Kernels_Greeks_Array")]
        public static void GreeksArray(int count, double[] price, double[] delta, double[] gamma, double[] vega,
            double[] theta, double[] rho, double[] vanna, double[] volga, double[] dualDelta,
            int[] sign, double[] forward, double[] strike, double[] rate, double[] maturity, double[] sigma)
        {
            for (int k = threadIdx.x + blockDim.x * blockIdx.x; k < count; k += blockDim.x * gridDim.x)
            {
                GreeksAt(k, price, delta, gamma, vega, theta, rho, vanna, volga, dualDelta,
                    sign, forward, strike, rate, maturity, sigma);
            }
        }

        /// <summary>sqrt(maturity), with a non-positive maturity treated as 0 and NaN propagated.</summary>
        [Kernel]
        public static double SqrtMaturity(double maturity)
        {
            return maturity > 0.0 ? Math.Sqrt(maturity) : (double.IsNaN(maturity) ? double.NaN : 0.0);
        }
    }
}
