using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Standard normal distribution functions, accurate to about double precision.
    /// Kernel-compatible: no allocation, no exceptions, only <c>Math.*</c> calls.
    /// </summary>
    /// <remarks>
    /// Phi(x) = erfc(-x / sqrt(2)) / 2, with erfc evaluated by W. J. Cody's rational Chebyshev
    /// approximations (CALERF, Argonne National Laboratory, 1990; coefficients taken from
    /// https://www.netlib.org/specfun/erf ). With z = |x| / sqrt(2) there are three fixed-degree
    /// ranges (z &lt;= 0.46875, z &lt;= 4, z &gt; 4); no data-dependent loops. The lower tail
    /// Phi(-|x|) is computed directly, the upper tail is 1 - tail, so there is no
    /// cancellation in the tails. exp(-x^2/2) is evaluated with a split argument (Cody, applied to x) to keep
    /// full relative accuracy in the far tail. The same three rational functions also give <see cref="Erf"/>,
    /// <see cref="Erfc"/> and the scaled <see cref="Erfcx"/> (exp(x^2) erfc(x)), which the implied-volatility
    /// solver needs to evaluate the Black function without cancellation in the wings.
    /// </remarks>
    public static class NormalDistribution
    {
        const double RSQRT2PI = 0.39894228040143267793994605993438;
        const double Cutoff = 38.6;   // Phi(-38.6) < 5e-325 underflows to 0
        const double RSQRT2 = 0.70710678118654752440;
        const double SQRPI = 5.6418958354775628695e-1; // 1/sqrt(pi)
        const double CodyThreshold = 0.46875;
        const double ErfcUnderflow = 26.543;          // erfc(26.543) < DBL_MIN (Cody's XBIG)
        const double ErfcxOverflow = -26.6287357137514; // 2 exp(x^2) = DBL_MAX (Cody's XNEG, refined by Jaeckel)

        // AS241 PPND16 coefficients (see InverseCdf).
        const double PpndA0 = 3.3871328727963666080e0;
        const double PpndA1 = 1.3314166789178437745e2;
        const double PpndA2 = 1.9715909503065514427e3;
        const double PpndA3 = 1.3731693765509461125e4;
        const double PpndA4 = 4.5921953931549871457e4;
        const double PpndA5 = 6.7265770927008700853e4;
        const double PpndA6 = 3.3430575583588128105e4;
        const double PpndA7 = 2.5090809287301226727e3;
        const double PpndB1 = 4.2313330701600911252e1;
        const double PpndB2 = 6.8718700749205790830e2;
        const double PpndB3 = 5.3941960214247511077e3;
        const double PpndB4 = 2.1213794301586595867e4;
        const double PpndB5 = 3.9307895800092710610e4;
        const double PpndB6 = 2.8729085735721942674e4;
        const double PpndB7 = 5.2264952788528545610e3;
        const double PpndC0 = 1.42343711074968357734e0;
        const double PpndC1 = 4.63033784615654529590e0;
        const double PpndC2 = 5.76949722146069140550e0;
        const double PpndC3 = 3.64784832476320460504e0;
        const double PpndC4 = 1.27045825245236838258e0;
        const double PpndC5 = 2.41780725177450611770e-1;
        const double PpndC6 = 2.27238449892691845833e-2;
        const double PpndC7 = 7.74545014278341407640e-4;
        const double PpndD1 = 2.05319162663775882187e0;
        const double PpndD2 = 1.67638483018380384940e0;
        const double PpndD3 = 6.89767334985100004550e-1;
        const double PpndD4 = 1.48103976427480074590e-1;
        const double PpndD5 = 1.51986665636164571966e-2;
        const double PpndD6 = 5.47593808499534494600e-4;
        const double PpndD7 = 1.05075007164441684324e-9;
        const double PpndE0 = 6.65790464350110377720e0;
        const double PpndE1 = 5.46378491116411436990e0;
        const double PpndE2 = 1.78482653991729133580e0;
        const double PpndE3 = 2.96560571828504891230e-1;
        const double PpndE4 = 2.65321895265761230930e-2;
        const double PpndE5 = 1.24266094738807843860e-3;
        const double PpndE6 = 2.71155556874348757815e-5;
        const double PpndE7 = 2.01033439929228813265e-7;
        const double PpndF1 = 5.99832206555887937690e-1;
        const double PpndF2 = 1.36929880922735805310e-1;
        const double PpndF3 = 1.48753612908506148525e-2;
        const double PpndF4 = 7.86869131145613259100e-4;
        const double PpndF5 = 1.84631831751005468180e-5;
        const double PpndF6 = 1.42151175831644588870e-7;
        const double PpndF7 = 2.04426310338993978564e-15;

        /// <summary>
        /// Probability density function of the standard normal distribution.
        /// </summary>
        [Kernel]
        public static double Pdf(double x)
        {
            return RSQRT2PI * Math.Exp(-0.5 * x * x);
        }

        /// <summary>
        /// Cumulative distribution function of the standard normal distribution.
        /// Relative error is below 1e-14 on the lower tail (down to the double
        /// underflow region, where it returns 0 below -38.6) and absolute error below 1e-16 near 1.
        /// </summary>
        [Kernel]
        public static double Cdf(double x)
        {
            double a = Math.Abs(x);
            double tail; // Phi(-|x|) = erfc(z) / 2

            if (a > Cutoff)
            {
                tail = 0.0;
            }
            else
            {
                double z = a * RSQRT2;
                if (z <= CodyThreshold)
                {
                    tail = 0.5 - 0.5 * (z * ErfSmallRatio(z * z));
                }
                else
                {
                    double r = z <= 4.0 ? ErfcxMid(z) : ErfcxLarge(z);
                    // exp(-z^2) = exp(-a^2/2), split on a (not z) so that the rounding of
                    // z = a / sqrt(2) cannot enter the exponent (it would cost ~1e-13 at a = 37).
                    double ah = Math.Floor(a * 16.0) / 16.0;
                    r = Math.Exp(-0.5 * ah * ah) * Math.Exp(-0.5 * (a - ah) * (a + ah)) * r;
                    tail = 0.5 * r;
                }
            }

            // NaN input: a is NaN, comparisons are false, tail is NaN and propagates.
            return x > 0.0 ? 1.0 - tail : tail;
        }

        /// <summary>
        /// Error function erf(x), Cody's CALERF (same rational functions as <see cref="Cdf"/>). Accurate to about
        /// double precision; erf(x) = x * R(x^2) for |x| &lt;= 0.46875 keeps full relative accuracy near 0.
        /// </summary>
        [Kernel]
        public static double Erf(double x)
        {
            double y = Math.Abs(x);
            if (y <= CodyThreshold) return x * ErfSmallRatio(y * y);
            double erfcAbs = ErfcAbove(y);
            return x < 0.0 ? erfcAbs - 1.0 : 1.0 - erfcAbs;
        }

        /// <summary>
        /// Complementary error function erfc(x) = 1 - erf(x), Cody's CALERF: full relative accuracy for x &gt; 0 down to
        /// underflow (0 from x &gt;= 26.543).
        /// </summary>
        [Kernel]
        public static double Erfc(double x)
        {
            double y = Math.Abs(x);
            if (y <= CodyThreshold) return 1.0 - x * ErfSmallRatio(y * y);
            double erfcAbs = ErfcAbove(y);
            return x < 0.0 ? 2.0 - erfcAbs : erfcAbs;
        }

        /// <summary>
        /// Scaled complementary error function erfcx(x) = exp(x^2) erfc(x), Cody's CALERF. For x &gt; 4 it needs no
        /// exponential and behaves like 1 / (x sqrt(pi)); it returns double.MaxValue below x = -26.6287 (overflow).
        /// </summary>
        [Kernel]
        public static double Erfcx(double x)
        {
            double y = Math.Abs(x);
            if (y <= CodyThreshold)
            {
                double z = y * y;
                return Math.Exp(z) * (1.0 - x * ErfSmallRatio(z));
            }
            if (x < ErfcxOverflow) return double.MaxValue;
            double result = y <= 4.0 ? ErfcxMid(y) : ErfcxLarge(y);
            if (x < 0.0)
            {
                // exp(x^2) with a split argument (Cody) so that the rounding of x^2 does not enter the exponent.
                double yh = Math.Floor(y * 16.0) / 16.0;
                double expx2 = Math.Exp(yh * yh) * Math.Exp((y - yh) * (y + yh));
                return (expx2 + expx2) - result;
            }
            return result;
        }

        /// <summary>erfc(y) for y &gt; 0.46875 (0 from y &gt;= 26.543).</summary>
        static double ErfcAbove(double y)
        {
            if (y >= ErfcUnderflow) return 0.0;
            double r = y <= 4.0 ? ErfcxMid(y) : ErfcxLarge(y);
            double yh = Math.Floor(y * 16.0) / 16.0;
            return Math.Exp(-yh * yh) * Math.Exp(-(y - yh) * (y + yh)) * r;
        }

        /// <summary>erf(z) / z for |z| &lt;= 0.46875, as a function of z^2 (Cody, first interval).</summary>
        static double ErfSmallRatio(double zsq)
        {
            double num = 1.85777706184603153e-1 * zsq;
            double den = zsq;
            num = (num + 3.16112374387056560e00) * zsq;
            den = (den + 2.36012909523441209e01) * zsq;
            num = (num + 1.13864154151050156e02) * zsq;
            den = (den + 2.44024637934444173e02) * zsq;
            num = (num + 3.77485237685302021e02) * zsq;
            den = (den + 1.28261652607737228e03) * zsq;
            return (num + 3.20937758913846947e03) / (den + 2.84423683343917062e03);
        }

        /// <summary>erfcx(z) for 0.46875 &lt; z &lt;= 4 (Cody, second interval).</summary>
        static double ErfcxMid(double z)
        {
            double num = 2.15311535474403846e-8 * z;
            double den = z;
            num = (num + 5.64188496988670089e-1) * z;
            den = (den + 1.57449261107098347e01) * z;
            num = (num + 8.88314979438837594e00) * z;
            den = (den + 1.17693950891312499e02) * z;
            num = (num + 6.61191906371416295e01) * z;
            den = (den + 5.37181101862009858e02) * z;
            num = (num + 2.98635138197400131e02) * z;
            den = (den + 1.62138957456669019e03) * z;
            num = (num + 8.81952221241769090e02) * z;
            den = (den + 3.29079923573345963e03) * z;
            num = (num + 1.71204761263407058e03) * z;
            den = (den + 4.36261909014324716e03) * z;
            num = (num + 2.05107837782607147e03) * z;
            den = (den + 3.43936767414372164e03) * z;
            return (num + 1.23033935479799725e03) / (den + 1.23033935480374942e03);
        }

        /// <summary>erfcx(z) for z &gt; 4 (Cody, third interval); no exponential.</summary>
        static double ErfcxLarge(double z)
        {
            double ysq = 1.0 / (z * z);
            double num = 1.63153871373020978e-2 * ysq;
            double den = ysq;
            num = (num + 3.05326634961232344e-1) * ysq;
            den = (den + 2.56852019228982242e00) * ysq;
            num = (num + 3.60344899949804439e-1) * ysq;
            den = (den + 1.87295284992346047e00) * ysq;
            num = (num + 1.25781726111229246e-1) * ysq;
            den = (den + 5.27905102951428412e-1) * ysq;
            num = (num + 1.60837851487422766e-2) * ysq;
            den = (den + 6.05183413124413191e-2) * ysq;
            double r = ysq * (num + 6.58749161529837803e-4) / (den + 2.33520497626869185e-3);
            return (SQRPI - r) / z;
        }

        /// <summary>
        /// Inverse of the standard normal CDF (quantile function, probit): returns x such that
        /// Cdf(x) = p. Returns -Infinity for p = 0, +Infinity for p = 1 and NaN outside [0, 1]
        /// (or for NaN input). Relative error is about 1e-16 over the whole range.
        /// </summary>
        /// <remarks>
        /// Wichura, M. J. (1988), "Algorithm AS 241: The Percentage Points of the Normal
        /// Distribution", Applied Statistics 37(3), 477-484, routine PPND16. Coefficients were
        /// extracted programmatically from the published Fortran at
        /// https://lib.stat.cmu.edu/apstat/241 . Three fixed-degree rational approximations:
        /// central (|p - 1/2| &lt;= 0.425, in 0.180625 - q^2), intermediate and tail (in
        /// sqrt(-ln min(p, 1-p))). No refinement step is applied: AS241 is already accurate to
        /// about 1 part in 1e16, a Newton/Halley step through Cdf would be limited by the
        /// conditioning of Cdf and cost an extra exp.
        /// </remarks>
        [Kernel]
        public static double InverseCdf(double p)
        {
            if (p == 0.0) return double.NegativeInfinity;
            if (p == 1.0) return double.PositiveInfinity;
            if (!(p > 0.0 && p < 1.0)) return double.NaN; // also catches NaN

            double q = p - 0.5;
            if (Math.Abs(q) <= 0.425) return InverseCdfCentral(q);

            double t = q < 0.0 ? p : 1.0 - p;
            double s = Math.Sqrt(-Math.Log(t));
            double x;
            if (s <= 5.0)
            {
                s -= 1.6;
                double num = ((((((PpndC7 * s + PpndC6) * s + PpndC5) * s + PpndC4) * s + PpndC3) * s + PpndC2) * s + PpndC1) * s + PpndC0;
                double den = ((((((PpndD7 * s + PpndD6) * s + PpndD5) * s + PpndD4) * s + PpndD3) * s + PpndD2) * s + PpndD1) * s + 1.0;
                x = num / den;
            }
            else
            {
                s -= 5.0;
                double num = ((((((PpndE7 * s + PpndE6) * s + PpndE5) * s + PpndE4) * s + PpndE3) * s + PpndE2) * s + PpndE1) * s + PpndE0;
                double den = ((((((PpndF7 * s + PpndF6) * s + PpndF5) * s + PpndF4) * s + PpndF3) * s + PpndF2) * s + PpndF1) * s + 1.0;
                x = num / den;
            }
            return q < 0.0 ? -x : x;
        }

        /// <summary>
        /// Phi^-1(1/2 + q) for q in (-1/2, 1/2), without forming 1/2 + q: full relative accuracy for small |q|, where
        /// <see cref="InverseCdf"/> would only see the rounded probability. Returns +-Infinity at q = +-1/2 and NaN
        /// outside.
        /// </summary>
        /// <remarks>
        /// AS241's central rational function is evaluated in q directly for |q| &lt;= 0.425; beyond, the tail
        /// probability 1/2 - |q| is exact (Sterbenz) and goes to <see cref="InverseCdf"/>.
        /// </remarks>
        [Kernel]
        public static double InverseCdfCentred(double q)
        {
            if (Math.Abs(q) <= 0.425) return InverseCdfCentral(q);
            return q > 0.0 ? -InverseCdf(0.5 - q) : InverseCdf(0.5 + q); // NaN falls through to InverseCdf: NaN
        }

        /// <summary>AS241 central region: Phi^-1(1/2 + q) for |q| &lt;= 0.425.</summary>
        static double InverseCdfCentral(double q)
        {
            double r = 0.180625 - q * q;
            double num = ((((((PpndA7 * r + PpndA6) * r + PpndA5) * r + PpndA4) * r + PpndA3) * r + PpndA2) * r + PpndA1) * r + PpndA0;
            double den = ((((((PpndB7 * r + PpndB6) * r + PpndB5) * r + PpndB4) * r + PpndB3) * r + PpndB2) * r + PpndB1) * r + 1.0;
            return q * num / den;
        }
    }
}
