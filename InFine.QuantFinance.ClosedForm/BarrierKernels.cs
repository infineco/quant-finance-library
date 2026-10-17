using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Kernel-compatible closed forms for continuously monitored single-barrier European options under
    /// Black-Scholes-Merton (Reiner and Rubinstein, 1991, in the form of Haug, "The Complete Guide to Option Pricing
    /// Formulas", section 4.17.1): the eight standard types (up/down, in/out, call/put) with a rebate. Cost of carry
    /// b = r - q.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rebate convention (that of the formulas): a knock-in option pays the rebate at expiry if the barrier was
    /// never hit; a knock-out option pays the rebate at the instant the barrier is hit. The vanilla payoff of an
    /// activated option is paid at expiry. Hence in(K, H, R_in) + out(K, H, R_out) = vanilla(K) + E + F, where E and F are
    /// the rebate terms: in-out parity is exact when both rebates are 0.
    /// </para>
    /// <para>
    /// With phi = +1 for a call and -1 for a put, eta = +1 for a down barrier and -1 for an up barrier,
    /// mu = (b - sigma^2/2) / sigma^2, lambda = sqrt(mu^2 + 2 r / sigma^2), v = sigma sqrt(T):
    /// x1 = ln(S/K)/v + (1+mu) v, x2 = ln(S/H)/v + (1+mu) v, y1 = ln(H^2/(S K))/v + (1+mu) v, y2 = ln(H/S)/v + (1+mu) v,
    /// z = ln(H/S)/v + lambda v; the six building blocks A..F of <see cref="Terms"/> combine as in
    /// <see cref="Price"/>.
    /// </para>
    /// <para>
    /// Inputs: spot, strike, barrier &gt; 0, sigma &gt; 0 (otherwise the result is NaN). A spot already beyond the barrier
    /// (down barrier: S &lt;= H; up barrier: S &gt;= H) is handled explicitly: the knock-in is the vanilla option, the
    /// knock-out is worth the rebate (paid immediately). At maturity = 0 with the barrier not breached the knock-in
    /// is worth its rebate and the knock-out the intrinsic value.
    /// </para>
    /// </remarks>
    public static class BarrierKernels
    {
        /// <summary>
        /// The six building blocks A..F of the Reiner-Rubinstein formulas (Haug's notation).
        /// </summary>
        /// <param name="phi">+1 call, -1 put.</param>
        /// <param name="eta">+1 down barrier, -1 up barrier.</param>
        [Kernel]
        public static void Terms(int phi, int eta, double spot, double strike, double barrier, double rebate,
            double maturity, double rate, double dividendYield, double sigma,
            out double a, out double b, out double c, out double d, out double e, out double f)
        {
            double carry = rate - dividendYield;
            double sigma2 = sigma * sigma;
            double v = sigma * Math.Sqrt(maturity);
            double mu = (carry - 0.5 * sigma2) / sigma2;
            double lambda = Math.Sqrt(mu * mu + 2.0 * rate / sigma2);

            double x1 = Math.Log(spot / strike) / v + (1.0 + mu) * v;
            double x2 = Math.Log(spot / barrier) / v + (1.0 + mu) * v;
            double y1 = Math.Log(barrier * barrier / (spot * strike)) / v + (1.0 + mu) * v;
            double y2 = Math.Log(barrier / spot) / v + (1.0 + mu) * v;
            double z = Math.Log(barrier / spot) / v + lambda * v;

            double df = Math.Exp(-rate * maturity);
            double growth = Math.Exp((carry - rate) * maturity); // e^{(b-r)T}
            double hs = barrier / spot;
            double hs2mu = Math.Pow(hs, 2.0 * mu);
            double hs2mu1 = Math.Pow(hs, 2.0 * (mu + 1.0));

            a = phi * spot * growth * NormalDistribution.Cdf(phi * x1)
                - phi * strike * df * NormalDistribution.Cdf(phi * x1 - phi * v);
            b = phi * spot * growth * NormalDistribution.Cdf(phi * x2)
                - phi * strike * df * NormalDistribution.Cdf(phi * x2 - phi * v);
            c = phi * spot * growth * hs2mu1 * NormalDistribution.Cdf(eta * y1)
                - phi * strike * df * hs2mu * NormalDistribution.Cdf(eta * y1 - eta * v);
            d = phi * spot * growth * hs2mu1 * NormalDistribution.Cdf(eta * y2)
                - phi * strike * df * hs2mu * NormalDistribution.Cdf(eta * y2 - eta * v);
            e = rebate * df * (NormalDistribution.Cdf(eta * x2 - eta * v)
                - hs2mu * NormalDistribution.Cdf(eta * y2 - eta * v));
            f = rebate * (Math.Pow(hs, mu + lambda) * NormalDistribution.Cdf(eta * z)
                + Math.Pow(hs, mu - lambda) * NormalDistribution.Cdf(eta * z - 2.0 * eta * lambda * v));
        }

        /// <summary>
        /// Price of a continuously monitored single-barrier option.
        /// </summary>
        /// <param name="sign">+1 for a call, -1 for a put.</param>
        /// <param name="isUp">true for an up barrier (H above the spot), false for a down barrier.</param>
        /// <param name="isIn">true for a knock-in, false for a knock-out.</param>
        /// <param name="spot">current spot S.</param>
        /// <param name="strike">strike K.</param>
        /// <param name="barrier">barrier H.</param>
        /// <param name="rebate">rebate R (see the class remarks for when it is paid).</param>
        /// <param name="maturity">time to expiry in years.</param>
        [Kernel]
        public static double Price(int sign, bool isUp, bool isIn, double spot, double strike, double barrier,
            double rebate, double maturity, double rate, double dividendYield, double sigma)
        {
            if (!(sigma > 0.0) || !(spot > 0.0) || !(strike > 0.0) || !(barrier > 0.0) || double.IsNaN(maturity) || maturity < 0.0)
                return double.NaN;

            bool breached = isUp ? spot >= barrier : spot <= barrier;
            if (breached)
            {
                // Knock-in already active: the vanilla option. Knock-out already hit: the rebate, paid now.
                return isIn ? BlackScholesKernels.Price(sign, spot, strike, maturity, rate, dividendYield, sigma) : rebate;
            }
            if (maturity == 0.0)
            {
                double intrinsic = Math.Max(0.0, sign * (spot - strike));
                return isIn ? rebate : intrinsic;
            }

            int eta = isUp ? -1 : 1;
            double a, b, c, d, e, f;
            Terms(sign, eta, spot, strike, barrier, rebate, maturity, rate, dividendYield, sigma,
                out a, out b, out c, out d, out e, out f);

            bool strikeAbove = strike >= barrier; // K >= H
            if (sign > 0)
            {
                if (!isUp)
                {
                    if (isIn) return strikeAbove ? c + e : a - b + d + e;      // down-and-in call
                    return strikeAbove ? a - c + f : b - d + f;                // down-and-out call
                }
                if (isIn) return strikeAbove ? a + e : b - c + d + e;          // up-and-in call
                return strikeAbove ? f : a - b + c - d + f;                    // up-and-out call
            }
            if (!isUp)
            {
                if (isIn) return strikeAbove ? b - c + d + e : a + e;          // down-and-in put
                return strikeAbove ? a - b + c - d + f : f;                    // down-and-out put
            }
            if (isIn) return strikeAbove ? a - b + d + e : c + e;              // up-and-in put
            return strikeAbove ? b - d + f : a - c + f;                        // up-and-out put
        }
    }
}
