using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Kernel-compatible closed forms for options on several correlated Black-Scholes assets
    /// (<c>dS_i / S_i = (r - q_i) dt + sigma_i dW_i</c>, <c>d&lt;W_i, W_j&gt; = rho_ij dt</c>): Margrabe's exchange option,
    /// Stulz's options on the minimum or maximum of two assets and the geometric basket option, which is the control
    /// variate of the arithmetic basket. No allocation, no exceptions.
    /// </summary>
    public static class MultiAssetKernels
    {
        /// <summary>
        /// Margrabe (1978) exchange option, payoff <c>max(S1(T) - S2(T), 0)</c> paid at T:
        /// <c>S1 e^{-q1 T} N(d1) - S2 e^{-q2 T} N(d2)</c>, <c>d1,2 = (ln(S1/S2) + (q2 - q1 +- sigma^2/2) T) / (sigma sqrt(T))</c>,
        /// <c>sigma^2 = sigma1^2 + sigma2^2 - 2 rho sigma1 sigma2</c>. The rate cancels (S2 is the numeraire), hence no
        /// rate argument. Computed as a Black-76 option on the forward ratio, so a zero spread volatility or maturity gives
        /// the intrinsic value of the forwards.
        /// </summary>
        [Kernel]
        public static double ExchangePrice(double spot1, double spot2, double maturity,
            double dividendYield1, double dividendYield2, double sigma1, double sigma2, double rho)
        {
            double variance = sigma1 * sigma1 + sigma2 * sigma2 - 2.0 * rho * sigma1 * sigma2;
            double stdDev = Math.Sqrt(Math.Max(variance, 0.0) * Math.Max(maturity, 0.0));
            double asset1 = spot1 * Math.Exp(-dividendYield1 * maturity);
            double asset2 = spot2 * Math.Exp(-dividendYield2 * maturity);
            return Black76Kernels.NormalisedPrice(1, asset1, asset2, stdDev);
        }

        /// <summary>
        /// Stulz (1982) option on the minimum (<paramref name="maximum"/> false) or the maximum of two assets, payoff
        /// <c>max(sign (min(S1, S2) - K), 0)</c> or <c>max(sign (max(S1, S2) - K), 0)</c> paid at T. Calls as in Haug,
        /// "The Complete Guide to Option Pricing Formulas" (2nd ed., 4.11.1); puts from the parity
        /// <c>put = K e^{-rT} - PV(min or max) + call</c> with <c>PV(min) = S1 e^{-q1 T} - exchange</c> and
        /// <c>PV(max) = S2 e^{-q2 T} + exchange</c> (<see cref="ExchangePrice"/>).
        /// Requires T &gt; 0, sigma1, sigma2 &gt; 0, |rho| &lt; 1 and a non-zero spread volatility; T &lt;= 0 gives the
        /// intrinsic value.
        /// </summary>
        /// <param name="sign">+1 call, -1 put.</param>
        /// <param name="maximum">true for the best-of (maximum), false for the worst-of (minimum).</param>
        [Kernel]
        public static double MinMaxPrice(int sign, bool maximum, double spot1, double spot2, double strike, double maturity,
            double rate, double dividendYield1, double dividendYield2, double sigma1, double sigma2, double rho)
        {
            if (!(maturity > 0.0))
            {
                double extreme = maximum ? Math.Max(spot1, spot2) : Math.Min(spot1, spot2);
                return Math.Max(sign * (extreme - strike), 0.0);
            }
            double sqrtT = Math.Sqrt(maturity);
            double sigma = Math.Sqrt(sigma1 * sigma1 + sigma2 * sigma2 - 2.0 * rho * sigma1 * sigma2);
            double b1 = rate - dividendYield1, b2 = rate - dividendYield2;
            double d = (Math.Log(spot1 / spot2) + (b1 - b2 + 0.5 * sigma * sigma) * maturity) / (sigma * sqrtT);
            double y1 = (Math.Log(spot1 / strike) + (b1 + 0.5 * sigma1 * sigma1) * maturity) / (sigma1 * sqrtT);
            double y2 = (Math.Log(spot2 / strike) + (b2 + 0.5 * sigma2 * sigma2) * maturity) / (sigma2 * sqrtT);
            double rho1 = (sigma1 - rho * sigma2) / sigma;
            double rho2 = (sigma2 - rho * sigma1) / sigma;
            double asset1 = spot1 * Math.Exp(-dividendYield1 * maturity);
            double asset2 = spot2 * Math.Exp(-dividendYield2 * maturity);
            double cash = strike * Math.Exp(-rate * maturity);

            double call;
            if (maximum)
                call = asset1 * BivariateNormalDistribution.Cdf(y1, d, rho1)
                     + asset2 * BivariateNormalDistribution.Cdf(y2, -d + sigma * sqrtT, rho2)
                     - cash * (1.0 - BivariateNormalDistribution.Cdf(-y1 + sigma1 * sqrtT, -y2 + sigma2 * sqrtT, rho));
            else
                call = asset1 * BivariateNormalDistribution.Cdf(y1, -d, -rho1)
                     + asset2 * BivariateNormalDistribution.Cdf(y2, d - sigma * sqrtT, -rho2)
                     - cash * BivariateNormalDistribution.Cdf(y1 - sigma1 * sqrtT, y2 - sigma2 * sqrtT, rho);
            if (sign > 0)
                return call;
            double exchange = ExchangePrice(spot1, spot2, maturity, dividendYield1, dividendYield2, sigma1, sigma2, rho);
            double presentValue = maximum ? asset2 + exchange : asset1 - exchange;
            return cash - presentValue + call;
        }

        /// <summary>
        /// Mean and variance of <c>ln G</c>, <c>G = prod_i S_i(T)^{w_i}</c>, the weighted geometric basket at T:
        /// <c>mean = sum_i w_i (ln S_i + (r - q_i - sigma_i^2 / 2) T)</c>, <c>var = T sum_ij w_i w_j rho_ij sigma_i sigma_j</c>.
        /// </summary>
        /// <param name="count">Number of assets n.</param>
        /// <param name="spots">Initial levels (pass <c>S_i(0) / reference_i</c> for a basket of performances).</param>
        /// <param name="weights">Exponents w_i.</param>
        /// <param name="correlation">Row-major n x n correlation matrix.</param>
        [Kernel]
        public static void GeometricBasketLogMoments(int count, double[] spots, double[] weights, double[] dividendYields,
            double[] sigmas, double[] correlation, double maturity, double rate, out double meanLog, out double varLog)
        {
            double m = 0.0, v = 0.0;
            for (int i = 0; i < count; ++i)
            {
                m += weights[i] * (Math.Log(spots[i]) + (rate - dividendYields[i] - 0.5 * sigmas[i] * sigmas[i]) * maturity);
                for (int j = 0; j < count; ++j)
                    v += weights[i] * weights[j] * correlation[i * count + j] * sigmas[i] * sigmas[j];
            }
            meanLog = m;
            varLog = Math.Max(v, 0.0) * maturity;
        }

        /// <summary>
        /// Geometric basket option, payoff <c>max(sign (G - K), 0)</c> paid at T with <c>G = prod_i S_i(T)^{w_i}</c>:
        /// G is log-normal, so this is a Black-76 option on <c>E[G] = exp(mean + var / 2)</c> with total standard deviation
        /// <c>sqrt(var)</c> (<see cref="GeometricBasketLogMoments"/>), discounted at r.
        /// With weights summing to one, G is close to the arithmetic basket <c>sum_i w_i S_i(T)</c> and its premium is the
        /// usual control variate of the arithmetic basket option.
        /// </summary>
        [Kernel]
        public static double GeometricBasketPrice(int sign, int count, double[] spots, double[] weights, double[] dividendYields,
            double[] sigmas, double[] correlation, double strike, double maturity, double rate)
        {
            double m, v;
            GeometricBasketLogMoments(count, spots, weights, dividendYields, sigmas, correlation, maturity, rate, out m, out v);
            return Math.Exp(-rate * maturity) * Black76Kernels.NormalisedPrice(sign, Math.Exp(m + 0.5 * v), strike, Math.Sqrt(v));
        }
    }
}
