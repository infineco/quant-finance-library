using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Closed-Form expression of Black-Scholes-Merton model,
    /// using a double-precision cumulative normal distribution. Without dividends; the dividend-yield version,
    /// the Greeks and Black-76 are in <see cref="BlackScholesKernels"/>, <see cref="Black76Kernels"/> and
    /// <see cref="BlackScholes"/>. These methods delegate to them.
    /// </summary>
    public class BlackScholesMertonPrimitive
    {
        /// <summary>
        /// Cumulative normal distribution (delegates to <see cref="NormalDistribution.Cdf"/>):
        /// $$
        /// CND(x) \tilda \int_{-\infinity}{d} \frac{1}{\sqrt{2\pi} \exp^{-\frac{x^2}{2}} dx
        /// $$
        /// Notable values are :
        /// $$ CND(-\infinity) = 0 $$, $$ CND(+\infinity) = 1 $$ and $$ CND(0) = 1/2 $$
        /// </summary>
        /// <param name="d">Where the cumulative normal should be evaluated.</param>
        /// <returns>Approximation of cumulative normal distribution.</returns>
        [Kernel]
        public static double CND(double d)
        {
            return NormalDistribution.Cdf(d);
        }

        /// <summary>
        /// Closed-form version of Black-Scholes-Merton European option valuation.
        /// </summary>
        /// <param name="call">output: call value.</param>
        /// <param name="put">output: put value.</param>
        /// <param name="spot">value of the underlying at option valuation date.</param>
        /// <param name="strike">stike of the option.</param>
        /// <param name="rate">constant risk-free rate.</param>
        /// <param name="sigma">constant volatility of the underlying.</param>
        /// <param name="maturity">maturity of the option, in years from the valuation date.</param>
        [Kernel]
        public static void CallPut(out double call, out double put,
            double spot, double strike,
            double rate, double sigma, double maturity)
        {
            BlackScholesKernels.CallPut(out call, out put, spot, strike, maturity, rate, 0.0, sigma);
        }

        /// <summary>
        /// Closed-form version of Black-Scholes-Merton European option valuation.
        /// </summary>
        /// <param name="index">index in the output arrays.</param>
        /// <param name="call">output: call value.</param>
        /// <param name="put">output: put value.</param>
        /// <param name="spot">value of the underlying at option valuation date.</param>
        /// <param name="strike">stike of the option.</param>
        /// <param name="rate">constant risk-free rate.</param>
        /// <param name="sigma">constant volatility of the underlying.</param>
        /// <param name="maturity">maturity of the option, in years from the valuation date.</param>
        [Kernel]
        public static void CallPut(int index, double[] call, double[] put, double spot, double strike, double rate, double sigma, double maturity)
        {
            double c, p;
            BlackScholesKernels.CallPut(out c, out p, spot, strike, maturity, rate, 0.0, sigma);
            call[index] = c;
            put[index] = p;
        }

        /// <summary>
        /// Evaluate a set of Call and Put options which parameters are given in arrays.
        /// </summary>
        /// <param name="count"></param>
        /// <param name="call"></param>
        /// <param name="put"></param>
        /// <param name="spot"></param>
        /// <param name="strike"></param>
        /// <param name="rate"></param>
        /// <param name="sigma"></param>
        /// <param name="maturity"></param>
        [EntryPoint("BlackScholesMertonPrimitive_CallPut_Array")]
        public static void CallPutArray(int count, double[] call, double[] put, double[] spot, double[] strike, double rate, double sigma, double[] maturity)
        {
            for (int k = threadIdx.x + blockDim.x * blockIdx.x; k < count; k += blockDim.x * gridDim.x)
            {
                CallPut(k, call, put, spot[k], strike[k], rate, sigma, maturity[k]);
            }
        }

        /// <summary>
        /// Evaluate a set of Call and Put options which parameters are given in arrays.
        /// </summary>
        /// <param name="count"></param>
        /// <param name="call"></param>
        /// <param name="put"></param>
        /// <param name="spot"></param>
        /// <param name="strike"></param>
        /// <param name="rate"></param>
        /// <param name="sigma"></param>
        /// <param name="maturity"></param>
        [EntryPoint("BlackScholesMertonPrimitive_CallPut_ArrayMulti")]
        public static void CallPutArrayMulti(int count, double[] call, double[] put, double[] spot, double[] strike, double[] rate, double[] sigma, double[] maturity)
        {
            for (int k = threadIdx.x + blockDim.x * blockIdx.x; k < count; k += blockDim.x * gridDim.x)
            {
                CallPut(k, call, put, spot[k], strike[k], rate[k], sigma[k], maturity[k]);
            }
        }
    }
}
