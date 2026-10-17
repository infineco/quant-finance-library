using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>One-dimensional root finders.</summary>
    public static class Solvers
    {
        /// <summary>
        /// Brent's method (Brent 1973, "Algorithms for Minimization without Derivatives", ch. 4): inverse quadratic
        /// interpolation or secant steps, falling back to bisection whenever they would not shrink the bracket fast
        /// enough, so it converges whenever <paramref name="f"/> changes sign on the bracket, superlinearly on smooth
        /// functions. The returned <c>b</c> satisfies <c>|b - root| &lt;= tolerance + 4 eps |b|</c> (the final bracket is
        /// <c>[b, c]</c> with <c>|c - b| &lt;= 2 (2 eps |b| + tolerance / 2)</c>), or <c>f(b) = 0</c> exactly.
        /// </summary>
        /// <param name="f">The function; it must return finite values.</param>
        /// <param name="lower">One end of the bracket.</param>
        /// <param name="upper">The other end; <c>f(lower)</c> and <c>f(upper)</c> must not have the same strict sign.</param>
        /// <param name="tolerance">Absolute tolerance on the root, positive.</param>
        /// <param name="maxIterations">Maximum number of function evaluations after the two end points.</param>
        /// <exception cref="ArgumentException">The ends do not bracket a root, or the arguments are invalid.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="f"/> returned NaN or infinity, or no convergence within <paramref name="maxIterations"/>.</exception>
        public static double Brent(Func<double, double> f, double lower, double upper, double tolerance = 1e-14, int maxIterations = 200)
        {
            ArgumentNullException.ThrowIfNull(f);
            if (!double.IsFinite(lower) || !double.IsFinite(upper))
                throw new ArgumentException("The bracket must be finite.", nameof(lower));
            if (!(tolerance > 0.0))
                throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The tolerance must be positive.");

            double a = lower, b = upper;
            var fa = Evaluate(f, a);
            var fb = Evaluate(f, b);
            if (fa == 0.0) return a;
            if (fb == 0.0) return b;
            if (SameSign(fa, fb))
                throw new ArgumentException($"The interval [{lower}, {upper}] does not bracket a root (f = {fa} and {fb}).", nameof(upper));

            double c = b, fc = fb, d = b - a, e = d;
            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                if (SameSign(fb, fc))
                {
                    // Keep the root between b and c.
                    c = a;
                    fc = fa;
                    d = e = b - a;
                }
                if (Math.Abs(fc) < Math.Abs(fb))
                {
                    // b is the best estimate so far.
                    a = b; b = c; c = a;
                    fa = fb; fb = fc; fc = fa;
                }
                var tol1 = 2.0 * Eps * Math.Abs(b) + 0.5 * tolerance;
                var xm = 0.5 * (c - b);
                if (Math.Abs(xm) <= tol1 || fb == 0.0)
                    return b;
                if (Math.Abs(e) >= tol1 && Math.Abs(fa) > Math.Abs(fb))
                {
                    double p, q;
                    var s = fb / fa;
                    if (a == c)
                    {
                        // Secant.
                        p = 2.0 * xm * s;
                        q = 1.0 - s;
                    }
                    else
                    {
                        // Inverse quadratic interpolation.
                        var qa = fa / fc;
                        var r = fb / fc;
                        p = s * (2.0 * xm * qa * (qa - r) - (b - a) * (r - 1.0));
                        q = (qa - 1.0) * (r - 1.0) * (s - 1.0);
                    }
                    if (p > 0.0) q = -q;
                    p = Math.Abs(p);
                    var min1 = 3.0 * xm * q - Math.Abs(tol1 * q);
                    var min2 = Math.Abs(e * q);
                    if (2.0 * p < Math.Min(min1, min2))
                    {
                        e = d;
                        d = p / q;
                    }
                    else
                    {
                        d = xm;
                        e = d;
                    }
                }
                else
                {
                    d = xm;
                    e = d;
                }
                a = b;
                fa = fb;
                b += Math.Abs(d) > tol1 ? d : (xm > 0.0 ? tol1 : -tol1);
                fb = Evaluate(f, b);
            }
            throw new InvalidOperationException($"Brent did not converge in {maxIterations} iterations (last estimate {b}).");
        }

        /// <summary>
        /// Widens <c>[lower, upper]</c> geometrically (by a factor 1.6 on the side where <c>|f|</c> is smaller, as in
        /// Numerical Recipes' <c>zbrac</c>) until <paramref name="f"/> changes sign on it.
        /// </summary>
        /// <returns>True when a bracket was found; the bounds then hold it.</returns>
        /// <exception cref="ArgumentException"><paramref name="lower"/> is not below <paramref name="upper"/>.</exception>
        public static bool TryBracket(Func<double, double> f, ref double lower, ref double upper, int maxExpansions = 60)
        {
            ArgumentNullException.ThrowIfNull(f);
            if (!(lower < upper))
                throw new ArgumentException($"lower ({lower}) must be below upper ({upper}).", nameof(lower));
            var fl = Evaluate(f, lower);
            var fu = Evaluate(f, upper);
            for (var j = 0; j < maxExpansions; j++)
            {
                if (!SameSign(fl, fu)) return true;
                if (Math.Abs(fl) < Math.Abs(fu))
                {
                    lower += Expansion * (lower - upper);
                    fl = Evaluate(f, lower);
                }
                else
                {
                    upper += Expansion * (upper - lower);
                    fu = Evaluate(f, upper);
                }
            }
            return !SameSign(fl, fu);
        }

        /// <summary>
        /// Brackets a root starting from <c>[guess - step, guess + step]</c> with <see cref="TryBracket"/>, then solves it
        /// with <see cref="Brent"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">No sign change was found.</exception>
        public static double BracketAndBrent(Func<double, double> f, double guess, double step, double tolerance = 1e-14, int maxIterations = 200)
        {
            if (!(step > 0.0))
                throw new ArgumentOutOfRangeException(nameof(step), step, "The step must be positive.");
            double lower = guess - step, upper = guess + step;
            if (!TryBracket(f, ref lower, ref upper))
                throw new InvalidOperationException($"No sign change found around {guess} (last bracket [{lower}, {upper}]).");
            return Brent(f, lower, upper, tolerance, maxIterations);
        }

        private const double Eps = 2.220446049250313e-16; // double machine epsilon (2^-52)
        private const double Expansion = 1.6;

        private static double Evaluate(Func<double, double> f, double x)
        {
            var y = f(x);
            if (!double.IsFinite(y))
                throw new InvalidOperationException($"The function returned {y} at {x}.");
            return y;
        }

        private static bool SameSign(double a, double b) => (a > 0.0 && b > 0.0) || (a < 0.0 && b < 0.0);
    }
}
