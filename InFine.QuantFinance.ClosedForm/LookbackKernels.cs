using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Kernel-compatible closed forms for continuously monitored lookback options under Black-Scholes-Merton with a
    /// continuous dividend yield q (cost of carry b = r - q): floating strike (Goldman, Sosin and Gatto, 1979) and fixed
    /// strike (Conze and Viswanathan, 1991), in the form of Haug, "The Complete Guide to Option Pricing Formulas",
    /// section 4.19. Both accept an already observed extremum for seasoned options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Payoffs at expiry T, with M the maximum and m the minimum of the spot over the whole life (observed part included):
    /// floating-strike call S_T - m, floating-strike put M - S_T, fixed-strike call max(M - X, 0), fixed-strike put
    /// max(X - m, 0). A continuously monitored extremum is more extreme than any discretely monitored one: the discrete
    /// Monte Carlo estimate is biased low for all four payoffs, by O(sigma sqrt(dt)) in the extremum
    /// (Broadie, Glasserman and Kou, 1999).
    /// </para>
    /// <para>
    /// All formulas contain the term sigma^2 / (2 b) [ ... ], whose bracket vanishes as b -&gt; 0. For |b| &lt; 1e-5 the price is
    /// the average of the formula at b +- 2e-5 (the price is analytic in b; the error is of order 1e-9 relative, the
    /// round-off of the direct formula would otherwise be 1/b times 1e-16).
    /// </para>
    /// </remarks>
    public static class LookbackKernels
    {
        const double SmallCarry = 1e-5;
        const double CarryShift = 2e-5;

        /// <summary>
        /// Floating-strike lookback: a call pays S_T - min, a put pays max - S_T.
        /// </summary>
        /// <param name="sign">+1 for a call (pays S_T minus the running minimum), -1 for a put (running maximum minus S_T).</param>
        /// <param name="extremum">observed running minimum (call; clamped to at most the spot) or maximum (put; clamped to
        /// at least the spot) so far; pass the spot for a new option.</param>
        [Kernel]
        public static double FloatingStrikePrice(int sign, double spot, double extremum, double maturity,
            double rate, double dividendYield, double sigma)
        {
            if (!(sigma > 0.0) || !(spot > 0.0) || !(extremum > 0.0) || !(maturity > 0.0))
                return double.NaN;
            double y = sign > 0 ? Math.Min(extremum, spot) : Math.Max(extremum, spot);
            double carry = rate - dividendYield;
            if (Math.Abs(carry) < SmallCarry)
            {
                return 0.5 * (FloatingCore(sign, spot, y, maturity, rate, carry + CarryShift, sigma)
                            + FloatingCore(sign, spot, y, maturity, rate, carry - CarryShift, sigma));
            }
            return FloatingCore(sign, spot, y, maturity, rate, carry, sigma);
        }

        /// <summary>
        /// Fixed-strike lookback: a call pays max(max - X, 0), a put pays max(X - min, 0).
        /// </summary>
        /// <param name="sign">+1 for a call (on the running maximum), -1 for a put (on the running minimum).</param>
        /// <param name="strike">fixed strike X.</param>
        /// <param name="extremum">observed running maximum (call; clamped to at least the spot) or minimum (put; clamped to
        /// at most the spot) so far; pass the spot for a new option.</param>
        [Kernel]
        public static double FixedStrikePrice(int sign, double spot, double strike, double extremum, double maturity,
            double rate, double dividendYield, double sigma)
        {
            if (!(sigma > 0.0) || !(spot > 0.0) || !(strike > 0.0) || !(extremum > 0.0) || !(maturity > 0.0))
                return double.NaN;
            double observed = sign > 0 ? Math.Max(extremum, spot) : Math.Min(extremum, spot);
            double carry = rate - dividendYield;
            if (Math.Abs(carry) < SmallCarry)
            {
                return 0.5 * (FixedCore(sign, spot, strike, observed, maturity, rate, carry + CarryShift, sigma)
                            + FixedCore(sign, spot, strike, observed, maturity, rate, carry - CarryShift, sigma));
            }
            return FixedCore(sign, spot, strike, observed, maturity, rate, carry, sigma);
        }

        [Kernel]
        static double FloatingCore(int sign, double spot, double y, double maturity, double rate, double carry, double sigma)
        {
            double sigma2 = sigma * sigma;
            double sqrtT = Math.Sqrt(maturity);
            double v = sigma * sqrtT;
            double df = Math.Exp(-rate * maturity);
            double a1 = (Math.Log(spot / y) + (carry + 0.5 * sigma2) * maturity) / v;
            double a2 = a1 - v;
            double power = Math.Pow(spot / y, -2.0 * carry / sigma2);
            double shift = 2.0 * carry * sqrtT / sigma;
            double prefactor = spot * df * sigma2 / (2.0 * carry);
            double growth = Math.Exp((carry - rate) * maturity);

            if (sign > 0)
            {
                return spot * growth * NormalDistribution.Cdf(a1)
                     - y * df * NormalDistribution.Cdf(a2)
                     + prefactor * (power * NormalDistribution.Cdf(-a1 + shift) - Math.Exp(carry * maturity) * NormalDistribution.Cdf(-a1));
            }
            return y * df * NormalDistribution.Cdf(-a2)
                 - spot * growth * NormalDistribution.Cdf(-a1)
                 + prefactor * (-power * NormalDistribution.Cdf(a1 - shift) + Math.Exp(carry * maturity) * NormalDistribution.Cdf(a1));
        }

        [Kernel]
        static double FixedCore(int sign, double spot, double strike, double observed, double maturity, double rate, double carry, double sigma)
        {
            double sigma2 = sigma * sigma;
            double sqrtT = Math.Sqrt(maturity);
            double v = sigma * sqrtT;
            double df = Math.Exp(-rate * maturity);
            double growth = Math.Exp((carry - rate) * maturity);
            double shift = 2.0 * carry * sqrtT / sigma;
            double prefactor = spot * df * sigma2 / (2.0 * carry);

            if (sign > 0)
            {
                double y = Math.Max(strike, observed);
                double d1 = (Math.Log(spot / y) + (carry + 0.5 * sigma2) * maturity) / v;
                double d2 = d1 - v;
                double power = Math.Pow(spot / y, -2.0 * carry / sigma2);
                return df * (y - strike)
                     + spot * growth * NormalDistribution.Cdf(d1)
                     - y * df * NormalDistribution.Cdf(d2)
                     + prefactor * (-power * NormalDistribution.Cdf(d1 - shift) + Math.Exp(carry * maturity) * NormalDistribution.Cdf(d1));
            }
            else
            {
                double y = Math.Min(strike, observed);
                double f1 = (Math.Log(spot / y) + (carry + 0.5 * sigma2) * maturity) / v;
                double f2 = f1 - v;
                double power = Math.Pow(spot / y, -2.0 * carry / sigma2);
                return df * (strike - y)
                     - spot * growth * NormalDistribution.Cdf(-f1)
                     + y * df * NormalDistribution.Cdf(-f2)
                     + prefactor * (power * NormalDistribution.Cdf(-f1 + shift) - Math.Exp(carry * maturity) * NormalDistribution.Cdf(-f1));
            }
        }
    }
}
