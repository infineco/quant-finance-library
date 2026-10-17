using System;
using InFine.QuantFinance.ClosedForm;

namespace InFine.QuantFinance.Risk
{
    /// <summary>
    /// The chi-squared distribution for the one and two degrees of freedom the backtesting statistics need. With one
    /// degree of freedom <c>X = Z^2</c> for a standard normal <c>Z</c>, so
    /// <c>P(X &lt;= x) = P(|Z| &lt;= sqrt(x)) = 1 - 2 Phi(-sqrt(x))</c>; with two it is exponential,
    /// <c>P(X &gt; x) = exp(-x/2)</c>.
    /// </summary>
    public static class ChiSquared
    {
        /// <summary>Cumulative distribution function of chi-squared with one degree of freedom (0 for negative <paramref name="x"/>).</summary>
        public static double Cdf1(double x)
        {
            if (double.IsNaN(x)) return double.NaN;
            if (x <= 0.0) return 0.0;
            return 1.0 - 2.0 * NormalDistribution.Cdf(-Math.Sqrt(x));
        }

        /// <summary>
        /// Survival function <c>P(X &gt; x)</c> of chi-squared with one degree of freedom, <c>2 Phi(-sqrt(x))</c>.
        /// Evaluated on the lower normal tail, so tiny p-values keep their relative accuracy (no <c>1 - Cdf</c>).
        /// </summary>
        public static double Survival1(double x)
        {
            if (double.IsNaN(x)) return double.NaN;
            if (x <= 0.0) return 1.0;
            return 2.0 * NormalDistribution.Cdf(-Math.Sqrt(x));
        }

        /// <summary>Survival function <c>P(X &gt; x) = exp(-x/2)</c> of chi-squared with two degrees of freedom.</summary>
        public static double Survival2(double x)
        {
            if (double.IsNaN(x)) return double.NaN;
            return x <= 0.0 ? 1.0 : Math.Exp(-0.5 * x);
        }
    }
}
