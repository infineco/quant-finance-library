using System;

namespace InFine.QuantFinance.TimeSeries
{
    /// <summary>
    /// Tail probabilities of the Student-t and F distributions through the regularised incomplete beta function
    /// <c>I_x(a, b)</c> (continued fraction evaluated by the modified Lentz algorithm, Numerical Recipes section 6.4).
    /// Two-sided and upper tails are computed directly from <c>I_x</c>, never as <c>1 - cdf</c>, so tiny p-values keep
    /// their relative accuracy (about 1e-14).
    /// </summary>
    public static class StudentT
    {
        /// <summary>Regularised incomplete beta function <c>I_x(a, b)</c> for <c>a, b &gt; 0</c> and <c>0 &lt;= x &lt;= 1</c>.</summary>
        public static double RegularizedIncompleteBeta(double a, double b, double x)
        {
            if (!(a > 0.0) || !(b > 0.0)) throw new ArgumentOutOfRangeException(nameof(a), "a and b must be positive.");
            if (!(x >= 0.0 && x <= 1.0)) throw new ArgumentOutOfRangeException(nameof(x), "x must lie in [0, 1].");
            if (x == 0.0) return 0.0;
            if (x == 1.0) return 1.0;
            double logFront = SpecialFunctions.LogGamma(a + b) - SpecialFunctions.LogGamma(a) - SpecialFunctions.LogGamma(b)
                              + a * Math.Log(x) + b * Math.Log(1.0 - x);
            double front = Math.Exp(logFront);
            // The continued fraction converges fast for x below the mean; otherwise use the symmetry I_x(a,b) = 1 - I_{1-x}(b,a).
            if (x < (a + 1.0) / (a + b + 2.0)) return front * ContinuedFraction(a, b, x) / a;
            return 1.0 - front * ContinuedFraction(b, a, 1.0 - x) / b;
        }

        static double ContinuedFraction(double a, double b, double x)
        {
            const double tiny = 1e-300;
            double qab = a + b, qap = a + 1.0, qam = a - 1.0;
            double c = 1.0, d = 1.0 - qab * x / qap;
            if (Math.Abs(d) < tiny) d = tiny;
            d = 1.0 / d;
            double h = d;
            for (int m = 1; m <= 1000; m++)
            {
                int m2 = 2 * m;
                double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
                d = 1.0 + aa * d; if (Math.Abs(d) < tiny) d = tiny;
                c = 1.0 + aa / c; if (Math.Abs(c) < tiny) c = tiny;
                d = 1.0 / d;
                h *= d * c;
                aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
                d = 1.0 + aa * d; if (Math.Abs(d) < tiny) d = tiny;
                c = 1.0 + aa / c; if (Math.Abs(c) < tiny) c = tiny;
                d = 1.0 / d;
                double delta = d * c;
                h *= delta;
                if (Math.Abs(delta - 1.0) < 1e-16) break;
            }
            return h;
        }

        /// <summary>Two-sided tail <c>P(|T| &gt; |t|)</c> of a Student-t variable with <paramref name="degreesOfFreedom"/> degrees of freedom.</summary>
        public static double TwoSidedPValue(double t, double degreesOfFreedom)
        {
            if (!(degreesOfFreedom > 0.0)) throw new ArgumentOutOfRangeException(nameof(degreesOfFreedom));
            if (double.IsNaN(t)) return double.NaN;
            if (double.IsInfinity(t)) return 0.0;
            return RegularizedIncompleteBeta(degreesOfFreedom / 2.0, 0.5, degreesOfFreedom / (degreesOfFreedom + t * t));
        }

        /// <summary>Cumulative distribution function <c>P(T &lt;= t)</c>.</summary>
        public static double Cdf(double t, double degreesOfFreedom)
        {
            double half = 0.5 * TwoSidedPValue(t, degreesOfFreedom);
            return t > 0.0 ? 1.0 - half : half;
        }

        /// <summary>Upper tail <c>P(F &gt; f)</c> of an F variable with <paramref name="d1"/> and <paramref name="d2"/> degrees of freedom.</summary>
        public static double FSurvival(double f, double d1, double d2)
        {
            if (!(d1 > 0.0) || !(d2 > 0.0)) throw new ArgumentOutOfRangeException(nameof(d1));
            if (double.IsNaN(f)) return double.NaN;
            if (f <= 0.0) return 1.0;
            if (double.IsInfinity(f)) return 0.0;
            return RegularizedIncompleteBeta(d2 / 2.0, d1 / 2.0, d2 / (d2 + d1 * f));
        }
    }
}
