using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Kernel-compatible generalised Black-Scholes-Merton formulas with a continuous dividend yield q, and their
    /// analytic Greeks. The option is a Black-76 option on the forward F = S exp((r - q) T) discounted at
    /// exp(-r T), so the degenerate-input behaviour is that of <see cref="Black76Kernels"/>: T = 0 or sigma = 0
    /// give the intrinsic value of the forward, discounted.
    /// </summary>
    /// <remarks>
    /// The Greeks follow from the Black-76 ones by the chain rule through F (dF/dS = exp((r-q)T)): delta, gamma and
    /// vanna are scaled by the carry factor, rho = T (F delta_F - V), dividend rho = -T F delta_F and
    /// theta = theta_F - (r - q) F delta_F, which gives the textbook closed forms, for example for a call
    /// theta = -S e^{-qT} phi(d1) sigma / (2 sqrt(T)) - r K e^{-rT} Phi(d2) + q S e^{-qT} Phi(d1).
    /// </remarks>
    // Not static so that a HybRunner can wrap an instance to reach the [EntryPoint] array kernels
    // (runner.Wrap(new BlackScholesKernels()).<EntryPoint>(...), as for FullRevaluationKernels); it has no state.
    public sealed class BlackScholesKernels
    {
        /// <summary>
        /// Generalised Black-Scholes-Merton price.
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        /// <param name="spot">value of the underlying.</param>
        /// <param name="strike">strike.</param>
        /// <param name="maturity">time to expiry in years.</param>
        /// <param name="rate">continuously compounded risk-free rate.</param>
        /// <param name="dividendYield">continuous dividend yield (or foreign rate, or convenience yield).</param>
        /// <param name="sigma">lognormal volatility.</param>
        [Kernel]
        public static double Price(int sign, double spot, double strike, double maturity,
            double rate, double dividendYield, double sigma)
        {
            double forward = spot * Math.Exp((rate - dividendYield) * maturity);
            return Black76Kernels.Price(sign, forward, strike, Math.Exp(-rate * maturity), maturity, sigma);
        }

        /// <summary>
        /// Call and put prices from one evaluation of d1 and d2.
        /// </summary>
        [Kernel]
        public static void CallPut(out double call, out double put, double spot, double strike, double maturity,
            double rate, double dividendYield, double sigma)
        {
            double forward = spot * Math.Exp((rate - dividendYield) * maturity);
            double df = Math.Exp(-rate * maturity);
            double c, p;
            Black76Kernels.NormalisedCallPut(out c, out p, forward, strike,
                sigma * Black76Kernels.SqrtMaturity(maturity));
            call = df * c;
            put = df * p;
        }

        /// <summary>
        /// Generalised Black-Scholes-Merton price and analytic Greeks (conventions on <see cref="OptionGreeks"/>).
        /// </summary>
        [Kernel]
        public static void Greeks(int sign, double spot, double strike, double maturity,
            double rate, double dividendYield, double sigma,
            out double price, out double delta, out double gamma, out double vega, out double theta,
            out double rho, out double dividendRho, out double vanna, out double volga, out double dualDelta)
        {
            double carry = Math.Exp((rate - dividendYield) * maturity); // dF/dS
            double forward = spot * carry;

            double deltaF, gammaF, thetaF, rhoF, vannaF;
            Black76Kernels.Greeks(sign, forward, strike, rate, maturity, sigma,
                out price, out deltaF, out gammaF, out vega, out thetaF, out rhoF, out vannaF, out volga, out dualDelta);

            delta = deltaF * carry;
            gamma = gammaF * carry * carry;
            vanna = vannaF * carry;
            double forwardDelta = forward * deltaF; // = S dV/dS
            rho = maturity * (forwardDelta - price);
            dividendRho = -maturity * forwardDelta;
            theta = thetaF - (rate - dividendYield) * forwardDelta;
        }

        /// <summary>
        /// Evaluates one option of a batch: reads element <paramref name="index"/> of the input arrays and writes
        /// the price to <paramref name="price"/>.
        /// </summary>
        [Kernel]
        public static void PriceAt(int index, double[] price, int[] sign, double[] spot, double[] strike,
            double[] maturity, double[] rate, double[] dividendYield, double[] sigma)
        {
            price[index] = Price(sign[index], spot[index], strike[index], maturity[index],
                rate[index], dividendYield[index], sigma[index]);
        }

        /// <summary>
        /// Evaluates a batch of generalised Black-Scholes-Merton prices, one option per array element.
        /// </summary>
        [EntryPoint("BlackScholesKernels_Price_Array")]
        public static void PriceArray(int count, double[] price, int[] sign, double[] spot, double[] strike,
            double[] maturity, double[] rate, double[] dividendYield, double[] sigma)
        {
            for (int k = threadIdx.x + blockDim.x * blockIdx.x; k < count; k += blockDim.x * gridDim.x)
            {
                PriceAt(k, price, sign, spot, strike, maturity, rate, dividendYield, sigma);
            }
        }

        /// <summary>
        /// Evaluates one option of a batch and writes its price and Greeks to the output arrays.
        /// </summary>
        [Kernel]
        public static void GreeksAt(int index, double[] price, double[] delta, double[] gamma, double[] vega,
            double[] theta, double[] rho, double[] dividendRho, double[] vanna, double[] volga, double[] dualDelta,
            int[] sign, double[] spot, double[] strike, double[] maturity,
            double[] rate, double[] dividendYield, double[] sigma)
        {
            double p, de, ga, ve, th, rh, dr, va, vo, du;
            Greeks(sign[index], spot[index], strike[index], maturity[index], rate[index], dividendYield[index],
                sigma[index], out p, out de, out ga, out ve, out th, out rh, out dr, out va, out vo, out du);
            price[index] = p;
            delta[index] = de;
            gamma[index] = ga;
            vega[index] = ve;
            theta[index] = th;
            rho[index] = rh;
            dividendRho[index] = dr;
            vanna[index] = va;
            volga[index] = vo;
            dualDelta[index] = du;
        }

        /// <summary>
        /// Evaluates a batch of generalised Black-Scholes-Merton prices and Greeks, one option per array element.
        /// </summary>
        [EntryPoint("BlackScholesKernels_Greeks_Array")]
        public static void GreeksArray(int count, double[] price, double[] delta, double[] gamma, double[] vega,
            double[] theta, double[] rho, double[] dividendRho, double[] vanna, double[] volga, double[] dualDelta,
            int[] sign, double[] spot, double[] strike, double[] maturity,
            double[] rate, double[] dividendYield, double[] sigma)
        {
            for (int k = threadIdx.x + blockDim.x * blockIdx.x; k < count; k += blockDim.x * gridDim.x)
            {
                GreeksAt(k, price, delta, gamma, vega, theta, rho, dividendRho, vanna, volga, dualDelta,
                    sign, spot, strike, maturity, rate, dividendYield, sigma);
            }
        }
    }
}
