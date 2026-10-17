using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Kernel-compatible closed forms for geometric-average Asian options under Black-Scholes-Merton with a
    /// continuous dividend yield q (cost of carry b = r - q). No allocation, no exceptions, only <c>Math.*</c>,
    /// <see cref="NormalDistribution"/> and <see cref="Black76Kernels"/> calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The geometric average G = exp(1/n sum_i ln S(t_i)) of a geometric Brownian motion is lognormal: ln G is Gaussian
    /// with mean m = ln S0 + (b - sigma^2/2) (sum_i t_i) / n and variance
    /// v = sigma^2 / n^2 sum_i sum_j min(t_i, t_j) = sigma^2 / n^2 sum_i (2 (n - i) - 1) t_i for ascending fixing
    /// times t_0 &lt; ... &lt; t_(n-1) (i counted from 0; each pair (i, j) with i &lt; j contributes t_i twice, the diagonal
    /// once). An option on G is therefore a Black-76 option on the forward E[G] = exp(m + v/2) with total standard
    /// deviation sqrt(v) (Kemna and Vorst, 1990, for equally spaced fixings), discounted from the payment date.
    /// </para>
    /// <para>
    /// The average-strike options pay max(sign (S_T - G), 0) at T &gt;= t_(n-1). S_T and G are jointly lognormal with
    /// Cov(ln S_T, ln G) = sigma^2 (sum_i t_i) / n, so the payoff is an exchange option (Margrabe): Black-76 with
    /// forward E[S_T], strike E[G] and total standard deviation sqrt(Var(ln S_T - ln G)).
    /// </para>
    /// </remarks>
    public static class AsianKernels
    {
        /// <summary>
        /// Mean and variance of ln G, the log of the geometric average of the spot at the given ascending fixing times.
        /// </summary>
        /// <param name="fixingTimes">ascending fixing times in years (&gt; 0); the first <paramref name="count"/> are used.</param>
        /// <param name="count">number of fixings n &gt;= 1.</param>
        [Kernel]
        public static void GeometricLogMoments(double[] fixingTimes, int count, double spot,
            double rate, double dividendYield, double sigma, out double meanLog, out double varLog)
        {
            double sumT = 0.0;
            double sumWeighted = 0.0;
            for (int i = 0; i < count; ++i)
            {
                sumT += fixingTimes[i];
                sumWeighted += (2 * (count - i) - 1) * fixingTimes[i];
            }
            meanLog = Math.Log(spot) + (rate - dividendYield - 0.5 * sigma * sigma) * sumT / count;
            varLog = sigma * sigma * sumWeighted / ((double)count * count);
        }

        /// <summary>
        /// Price of a discretely monitored geometric-average-price option, payoff max(sign (G - K), 0) paid at
        /// <paramref name="maturity"/>.
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        /// <param name="maturity">payment date in years (&gt;= last fixing).</param>
        /// <param name="fixingTimes">ascending fixing times in years (&gt; 0).</param>
        /// <param name="count">number of fixings.</param>
        [Kernel]
        public static double GeometricPrice(int sign, double spot, double strike, double maturity,
            double[] fixingTimes, int count, double rate, double dividendYield, double sigma)
        {
            double m, v;
            GeometricLogMoments(fixingTimes, count, spot, rate, dividendYield, sigma, out m, out v);
            return Math.Exp(-rate * maturity) * Black76Kernels.NormalisedPrice(sign, Math.Exp(m + 0.5 * v), strike, Math.Sqrt(v));
        }

        /// <summary>
        /// Kemna-Vorst price of a geometric-average-price option with <paramref name="fixings"/> equally spaced fixings
        /// t_k = k T / n, k = 1..n, paid at T: ln G has mean ln S0 + (b - sigma^2/2) T (n + 1) / (2 n) and variance
        /// sigma^2 T (n + 1) (2 n + 1) / (6 n^2).
        /// </summary>
        [Kernel]
        public static double GeometricPriceEquallySpaced(int sign, double spot, double strike, double maturity,
            int fixings, double rate, double dividendYield, double sigma)
        {
            double n = fixings;
            double m = Math.Log(spot) + (rate - dividendYield - 0.5 * sigma * sigma) * maturity * (n + 1.0) / (2.0 * n);
            double v = sigma * sigma * maturity * (n + 1.0) * (2.0 * n + 1.0) / (6.0 * n * n);
            return Math.Exp(-rate * maturity) * Black76Kernels.NormalisedPrice(sign, Math.Exp(m + 0.5 * v), strike, Math.Sqrt(v));
        }

        /// <summary>
        /// Price of a continuously monitored geometric-average-price option (average over [0, T], paid at T): ln G has mean
        /// ln S0 + (b - sigma^2/2) T / 2 and variance sigma^2 T / 3 (the n -&gt; infinity limit of Kemna-Vorst).
        /// </summary>
        [Kernel]
        public static double GeometricPriceContinuous(int sign, double spot, double strike, double maturity,
            double rate, double dividendYield, double sigma)
        {
            double m = Math.Log(spot) + (rate - dividendYield - 0.5 * sigma * sigma) * maturity * 0.5;
            double v = sigma * sigma * maturity / 3.0;
            return Math.Exp(-rate * maturity) * Black76Kernels.NormalisedPrice(sign, Math.Exp(m + 0.5 * v), strike, Math.Sqrt(v));
        }

        /// <summary>
        /// Price of a discretely monitored geometric-average-strike option, payoff max(sign (S_T - G), 0) paid at
        /// <paramref name="maturity"/> (a call pays S_T - G when positive, a put G - S_T).
        /// </summary>
        [Kernel]
        public static double GeometricAverageStrikePrice(int sign, double spot, double maturity,
            double[] fixingTimes, int count, double rate, double dividendYield, double sigma)
        {
            double m, v;
            GeometricLogMoments(fixingTimes, count, spot, rate, dividendYield, sigma, out m, out v);
            double sumT = 0.0;
            for (int i = 0; i < count; ++i)
                sumT += fixingTimes[i];
            double covariance = sigma * sigma * sumT / count;
            double varDifference = sigma * sigma * maturity + v - 2.0 * covariance;
            double forwardSpot = spot * Math.Exp((rate - dividendYield) * maturity);
            return Math.Exp(-rate * maturity) * Black76Kernels.NormalisedPrice(
                sign, forwardSpot, Math.Exp(m + 0.5 * v), Math.Sqrt(Math.Max(varDifference, 0.0)));
        }

        /// <summary>
        /// Price of a continuously monitored geometric-average-strike option (average over [0, T], paid at T):
        /// Var(ln S_T - ln G) = sigma^2 T / 3.
        /// </summary>
        [Kernel]
        public static double GeometricAverageStrikePriceContinuous(int sign, double spot, double maturity,
            double rate, double dividendYield, double sigma)
        {
            double m = Math.Log(spot) + (rate - dividendYield - 0.5 * sigma * sigma) * maturity * 0.5;
            double v = sigma * sigma * maturity / 3.0;
            double forwardSpot = spot * Math.Exp((rate - dividendYield) * maturity);
            return Math.Exp(-rate * maturity) * Black76Kernels.NormalisedPrice(
                sign, forwardSpot, Math.Exp(m + 0.5 * v), sigma * Math.Sqrt(maturity / 3.0));
        }
    }
}
