using System;

namespace InFine.QuantFinance.Numerics
{
    /// <summary>
    /// Central finite-difference gradient and Hessian of a scalar function. Host-only (allocates, validates by
    /// exceptions). The step of coordinate <c>i</c> is <c>relativeStep * max(|x_i|, floor)</c>, so the scheme is
    /// invariant to the units of each variable (a variance near 1e-6 and a correlation near 1 are treated alike).
    /// </summary>
    public static class FiniteDifferences
    {
        /// <summary>Central-difference gradient, error O(h^2).</summary>
        /// <param name="f">The function.</param>
        /// <param name="x">The point.</param>
        /// <param name="relativeStep">Relative step; 1e-5 is a reasonable default.</param>
        /// <param name="floor">Absolute scale used when <c>|x_i|</c> is smaller; 0 means a zero coordinate gets a step of <paramref name="relativeStep"/> itself.</param>
        public static double[] Gradient(Func<double[], double> f, double[] x, double relativeStep = 1e-5, double floor = 0.0)
        {
            ArgumentNullException.ThrowIfNull(f);
            ArgumentNullException.ThrowIfNull(x);
            Check(relativeStep);
            int n = x.Length;
            var g = new double[n];
            var w = (double[])x.Clone();
            for (int i = 0; i < n; i++)
            {
                double h = Step(x[i], relativeStep, floor);
                w[i] = x[i] + h; double fp = f(w);
                w[i] = x[i] - h; double fm = f(w);
                w[i] = x[i];
                g[i] = (fp - fm) / (2.0 * h);
            }
            return g;
        }

        /// <summary>
        /// Central-difference Hessian, error O(h^2): diagonal <c>(f(x+h)-2f(x)+f(x-h))/h^2</c>, off-diagonal the
        /// four-point cross formula. Exactly symmetric. Costs <c>2 n^2 + 1</c> evaluations.
        /// </summary>
        /// <param name="f">The function.</param>
        /// <param name="x">The point.</param>
        /// <param name="relativeStep">Relative step; 1e-3 to 1e-4 balances truncation and rounding for a sum of many terms.</param>
        /// <param name="floor">Absolute scale used when <c>|x_i|</c> is smaller; 0 means a zero coordinate gets a step of <paramref name="relativeStep"/> itself.</param>
        public static double[,] Hessian(Func<double[], double> f, double[] x, double relativeStep = 1e-4, double floor = 0.0)
        {
            ArgumentNullException.ThrowIfNull(f);
            ArgumentNullException.ThrowIfNull(x);
            Check(relativeStep);
            int n = x.Length;
            var hess = new double[n, n];
            var w = (double[])x.Clone();
            var step = new double[n];
            for (int i = 0; i < n; i++) step[i] = Step(x[i], relativeStep, floor);
            double f0 = f(w);
            for (int i = 0; i < n; i++)
            {
                double hi = step[i];
                w[i] = x[i] + hi; double fp = f(w);
                w[i] = x[i] - hi; double fm = f(w);
                w[i] = x[i];
                hess[i, i] = (fp - 2.0 * f0 + fm) / (hi * hi);
                for (int j = i + 1; j < n; j++)
                {
                    double hj = step[j];
                    w[i] = x[i] + hi; w[j] = x[j] + hj; double fpp = f(w);
                    w[j] = x[j] - hj; double fpm = f(w);
                    w[i] = x[i] - hi; double fmm = f(w);
                    w[j] = x[j] + hj; double fmp = f(w);
                    w[i] = x[i]; w[j] = x[j];
                    hess[i, j] = hess[j, i] = (fpp - fpm - fmp + fmm) / (4.0 * hi * hj);
                }
            }
            return hess;
        }

        static double Step(double xi, double relativeStep, double floor)
        {
            double scale = Math.Max(Math.Abs(xi), floor);
            return relativeStep * (scale > 0.0 ? scale : 1.0);
        }

        static void Check(double relativeStep)
        {
            if (!(relativeStep > 0.0) || double.IsInfinity(relativeStep))
                throw new ArgumentOutOfRangeException(nameof(relativeStep), "The step must be positive and finite.");
        }
    }
}
