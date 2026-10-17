using System;

namespace InFine.QuantFinance.Numerics
{
    /// <summary>Objective function <c>f(x)</c> of an unconstrained minimisation. Return NaN or infinity where it is undefined.</summary>
    /// <param name="x">The point (length n). A scratch array: do not keep or modify it.</param>
    public delegate double ObjectiveFunction(double[] x);

    /// <summary>Gradient of an <see cref="ObjectiveFunction"/>: writes <c>grad f(x)</c> into <paramref name="gradient"/>.</summary>
    /// <param name="x">The point (length n). A scratch array: do not keep or modify it.</param>
    /// <param name="gradient">Output, length n.</param>
    public delegate void GradientFunction(double[] x, double[] gradient);

    /// <summary>Why <see cref="Bfgs.Minimize"/> stopped.</summary>
    public enum BfgsStatus
    {
        /// <summary>The gradient is small: <c>max |g_i| &lt;= GradientTolerance * max(1, |f|)</c>.</summary>
        GradientConverged,

        /// <summary>The last accepted step changed neither the point (relative <c>StepTolerance</c>) nor the value (relative <c>FunctionTolerance</c>) meaningfully.</summary>
        StepConverged,

        /// <summary><c>MaxIterations</c> or <c>MaxFunctionEvaluations</c> was reached.</summary>
        MaxIterations,

        /// <summary>No step satisfying the Wolfe conditions was found along a steepest-descent direction (typically a noisy or non-smooth objective).</summary>
        LineSearchFailed,
    }

    /// <summary>Settings of <see cref="Bfgs.Minimize"/>.</summary>
    public sealed class BfgsOptions
    {
        /// <summary>Stop when <c>max |g_i| &lt;= GradientTolerance * max(1, |f|)</c>. Default 1e-8.</summary>
        public double GradientTolerance { get; init; } = 1e-8;

        /// <summary>Stop when an accepted step reduces <c>f</c> by at most <c>FunctionTolerance * max(1, |f|)</c>. Default 1e-14.</summary>
        public double FunctionTolerance { get; init; } = 1e-14;

        /// <summary>Stop when an accepted step has <c>max |s_i| &lt;= StepTolerance * max(1, |x_i|)</c>. Default 1e-12.</summary>
        public double StepTolerance { get; init; } = 1e-12;

        /// <summary>Maximum number of iterations (accepted steps). Default 500.</summary>
        public int MaxIterations { get; init; } = 500;

        /// <summary>Maximum number of objective evaluations, those spent on finite-difference gradients included. Default 100000.</summary>
        public int MaxFunctionEvaluations { get; init; } = 100000;

        /// <summary>
        /// Relative step of the central-difference gradient used when none is supplied: <c>x_i</c> is bumped by
        /// <c>step * max(1, |x_i|)</c>. Default eps^(1/3), about 6e-6, which balances truncation and rounding error.
        /// </summary>
        public double FiniteDifferenceStep { get; init; } = 6.0554544523933395e-6;

        /// <summary>Sufficient-decrease constant c1 of the Wolfe conditions. Default 1e-4.</summary>
        public double WolfeC1 { get; init; } = 1e-4;

        /// <summary>Curvature constant c2 of the strong Wolfe condition, <c>c1 &lt; c2 &lt; 1</c>. Default 0.9 (quasi-Newton).</summary>
        public double WolfeC2 { get; init; } = 0.9;
    }

    /// <summary>Outcome of <see cref="Bfgs.Minimize"/>.</summary>
    public sealed class BfgsResult
    {
        internal BfgsResult(double[] x, double value, double[] gradient, double[,] inverseHessian, int iterations,
            int functionEvaluations, BfgsStatus status)
        {
            X = x;
            Value = value;
            Gradient = gradient;
            InverseHessian = inverseHessian;
            Iterations = iterations;
            FunctionEvaluations = functionEvaluations;
            Status = status;
        }

        /// <summary>The best point found.</summary>
        public double[] X { get; }

        /// <summary>The objective at <see cref="X"/>.</summary>
        public double Value { get; }

        /// <summary>The gradient at <see cref="X"/> (finite-difference unless one was supplied).</summary>
        public double[] Gradient { get; }

        /// <summary>The final BFGS approximation of the inverse Hessian. A rough guide only: use a finite-difference Hessian for standard errors.</summary>
        public double[,] InverseHessian { get; }

        /// <summary>Accepted iterations.</summary>
        public int Iterations { get; }

        /// <summary>Objective evaluations, including those of finite-difference gradients.</summary>
        public int FunctionEvaluations { get; }

        /// <summary>Why the iteration stopped.</summary>
        public BfgsStatus Status { get; }

        /// <summary>True when the stop is a convergence (gradient, step or function) rather than a budget or a line-search failure.</summary>
        public bool Converged => Status == BfgsStatus.GradientConverged || Status == BfgsStatus.StepConverged;
    }

    /// <summary>
    /// Unconstrained minimisation of a smooth function by the BFGS quasi-Newton method with a strong-Wolfe line search
    /// (Nocedal and Wright, <i>Numerical Optimization</i>, 2nd ed., Algorithms 6.1, 3.5 and 3.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Intended for maximum-likelihood problems, which Levenberg-Marquardt does not fit (a likelihood is not a sum of
    /// squares). Constraints are handled by the caller through a smooth reparametrisation to R^n. Host-only code: it
    /// validates with exceptions and allocates, so never call it from a kernel.
    /// </para>
    /// <para>
    /// BFGS needs only gradients and keeps the inverse-Hessian approximation positive definite as long as the line search
    /// returns steps with <c>s'y &gt; 0</c>, which the Wolfe curvature condition guarantees. Points where the objective is
    /// not finite are treated as infinitely bad, so the search backtracks away from them. The initial inverse Hessian is
    /// the identity, rescaled by <c>s'y / y'y</c> after the first step (N&amp;W eq. 6.20).
    /// </para>
    /// </remarks>
    public static class Bfgs
    {
        /// <summary>Minimises <paramref name="f"/> from <paramref name="x0"/>.</summary>
        /// <param name="f">Objective.</param>
        /// <param name="x0">Starting point; the objective must be finite there.</param>
        /// <param name="options">Settings; defaults when null.</param>
        /// <param name="gradient">Analytic gradient; central differences when null.</param>
        public static BfgsResult Minimize(ObjectiveFunction f, double[] x0, BfgsOptions? options = null, GradientFunction? gradient = null)
        {
            ArgumentNullException.ThrowIfNull(f);
            ArgumentNullException.ThrowIfNull(x0);
            options ??= new BfgsOptions();
            int n = x0.Length;
            if (n == 0) throw new ArgumentException("At least one variable is needed.", nameof(x0));
            if (!(options.WolfeC1 > 0.0 && options.WolfeC1 < options.WolfeC2 && options.WolfeC2 < 1.0))
                throw new ArgumentException("Wolfe constants must satisfy 0 < c1 < c2 < 1.");

            var search = new Search(f, gradient, options, n);
            var x = (double[])x0.Clone();
            double fx = search.Value(x);
            if (!double.IsFinite(fx)) throw new ArgumentException("The objective is not finite at the starting point.", nameof(x0));
            var g = new double[n];
            search.Gradient(x, g);
            var h = Matrix.Identity(n);
            bool firstUpdate = true;
            int iterations = 0;
            var p = new double[n];
            var xNew = new double[n];
            var gNew = new double[n];
            var s = new double[n];
            var y = new double[n];
            var status = BfgsStatus.MaxIterations;

            while (true)
            {
                if (MaxAbs(g) <= options.GradientTolerance * Math.Max(1.0, Math.Abs(fx))) { status = BfgsStatus.GradientConverged; break; }
                if (iterations >= options.MaxIterations || search.Evaluations >= options.MaxFunctionEvaluations) break;

                Direction(h, g, p);
                if (Dot(g, p) >= 0.0) { ResetToIdentity(h); firstUpdate = true; Direction(h, g, p); }

                double alpha0 = firstUpdate ? Math.Min(1.0, 1.0 / Math.Max(Norm(g), 1e-300)) : 1.0;
                double fNew;
                if (!search.StrongWolfe(x, fx, g, p, alpha0, xNew, gNew, out fNew))
                {
                    // A bad quasi-Newton direction is the usual culprit: retry along steepest descent once.
                    bool steepest = true;
                    for (int i = 0; i < n; i++) steepest &= p[i] == -g[i];
                    if (steepest) { status = BfgsStatus.LineSearchFailed; break; }
                    ResetToIdentity(h); firstUpdate = true;
                    for (int i = 0; i < n; i++) p[i] = -g[i];
                    alpha0 = Math.Min(1.0, 1.0 / Math.Max(Norm(g), 1e-300));
                    if (!search.StrongWolfe(x, fx, g, p, alpha0, xNew, gNew, out fNew)) { status = BfgsStatus.LineSearchFailed; break; }
                }

                for (int i = 0; i < n; i++) { s[i] = xNew[i] - x[i]; y[i] = gNew[i] - g[i]; }
                double sy = Dot(s, y);
                if (sy > 1e-12 * Norm(s) * Norm(y))
                {
                    if (firstUpdate) { ScaleIdentity(h, sy / Dot(y, y)); firstUpdate = false; }
                    Update(h, s, y, sy);
                }

                double decrease = fx - fNew;
                bool smallStep = true;
                for (int i = 0; i < n; i++) smallStep &= Math.Abs(s[i]) <= options.StepTolerance * Math.Max(1.0, Math.Abs(xNew[i]));
                Array.Copy(xNew, x, n);
                Array.Copy(gNew, g, n);
                double fOld = fx;
                fx = fNew;
                iterations++;
                if (smallStep || decrease <= options.FunctionTolerance * Math.Max(1.0, Math.Abs(fOld)))
                {
                    status = MaxAbs(g) <= options.GradientTolerance * Math.Max(1.0, Math.Abs(fx)) ? BfgsStatus.GradientConverged : BfgsStatus.StepConverged;
                    break;
                }
            }

            return new BfgsResult(x, fx, g, h, iterations, search.Evaluations, status);
        }

        // ---- line search -------------------------------------------------------------------------------------

        sealed class Search
        {
            readonly ObjectiveFunction _f;
            readonly GradientFunction? _grad;
            readonly BfgsOptions _o;
            readonly double[] _trial;
            readonly double[] _scratch;

            public Search(ObjectiveFunction f, GradientFunction? grad, BfgsOptions o, int n)
            {
                _f = f; _grad = grad; _o = o;
                _trial = new double[n];
                _scratch = new double[n];
            }

            public int Evaluations { get; private set; }

            public double Value(double[] x)
            {
                Evaluations++;
                double v = _f(x);
                return double.IsNaN(v) ? double.PositiveInfinity : v;
            }

            public void Gradient(double[] x, double[] g)
            {
                if (_grad != null) { _grad(x, g); return; }
                Array.Copy(x, _scratch, x.Length);
                for (int i = 0; i < x.Length; i++)
                {
                    double h = _o.FiniteDifferenceStep * Math.Max(1.0, Math.Abs(x[i]));
                    double xi = x[i];
                    _scratch[i] = xi + h; double fp = Value(_scratch);
                    _scratch[i] = xi - h; double fm = Value(_scratch);
                    _scratch[i] = xi;
                    // Both sides non-finite makes the slope unusable; report zero and let the line search fail loudly.
                    g[i] = double.IsFinite(fp) && double.IsFinite(fm) ? (fp - fm) / (2.0 * h) : 0.0;
                }
            }

            // phi(a) = f(x + a p) and phi'(a) = g(x + a p)'p; fills xa and ga.
            double Phi(double[] x, double[] p, double a, double[] xa, double[] ga, out double slope)
            {
                for (int i = 0; i < x.Length; i++) xa[i] = x[i] + a * p[i];
                double v = Value(xa);
                if (!double.IsFinite(v)) { slope = double.NaN; return double.PositiveInfinity; }
                Gradient(xa, ga);
                slope = Dot(ga, p);
                return v;
            }

            /// <summary>Strong-Wolfe step (N&amp;W Algorithm 3.5); on success fills <paramref name="xOut"/> and <paramref name="gOut"/>.</summary>
            public bool StrongWolfe(double[] x, double f0, double[] g0, double[] p, double alpha1, double[] xOut, double[] gOut, out double fOut)
            {
                int n = x.Length;
                double d0 = Dot(g0, p);
                double c1 = _o.WolfeC1, c2 = _o.WolfeC2;
                double aPrev = 0.0, fPrev = f0, dPrev = d0;
                double a = alpha1;
                var xa = new double[n];
                var ga = new double[n];
                const double aMax = 1e10;
                fOut = f0;
                for (int i = 1; i <= 60; i++)
                {
                    double fa = Phi(x, p, a, xa, ga, out double da);
                    if (fa > f0 + c1 * a * d0 || (i > 1 && fa >= fPrev) || !double.IsFinite(fa))
                        return Zoom(x, p, f0, d0, aPrev, fPrev, dPrev, a, fa, da, xOut, gOut, out fOut);
                    if (Math.Abs(da) <= -c2 * d0) { Array.Copy(xa, xOut, n); Array.Copy(ga, gOut, n); fOut = fa; return true; }
                    if (da >= 0.0) return Zoom(x, p, f0, d0, a, fa, da, aPrev, fPrev, dPrev, xOut, gOut, out fOut);
                    aPrev = a; fPrev = fa; dPrev = da;
                    a = Math.Min(2.0 * a, aMax);
                    if (a >= aMax) break;
                }
                return false;
            }

            bool Zoom(double[] x, double[] p, double f0, double d0, double lo, double fLo, double dLo, double hi, double fHi, double dHi,
                double[] xOut, double[] gOut, out double fOut)
            {
                int n = x.Length;
                double c1 = _o.WolfeC1, c2 = _o.WolfeC2;
                var xa = new double[n];
                var ga = new double[n];
                fOut = f0;
                for (int it = 0; it < 60; it++)
                {
                    double width = hi - lo;
                    // Quadratic interpolation through (lo, fLo, dLo) and (hi, fHi), safeguarded inside the bracket.
                    double a;
                    if (double.IsFinite(fHi))
                    {
                        double denom = 2.0 * (fHi - fLo - dLo * width);
                        a = denom > 0.0 ? lo - dLo * width * width / denom : lo + 0.5 * width;
                        double minA = Math.Min(lo, hi) + 0.1 * Math.Abs(width), maxA = Math.Max(lo, hi) - 0.1 * Math.Abs(width);
                        if (!(a >= minA && a <= maxA)) a = lo + 0.5 * width;
                    }
                    else a = lo + 0.5 * width;
                    if (Math.Abs(width) < 1e-16 * Math.Max(1.0, Math.Abs(lo))) break;

                    double fa = Phi(x, p, a, xa, ga, out double da);
                    if (fa > f0 + c1 * a * d0 || fa >= fLo || !double.IsFinite(fa))
                    {
                        hi = a; fHi = fa; dHi = da;
                    }
                    else
                    {
                        if (Math.Abs(da) <= -c2 * d0) { Array.Copy(xa, xOut, n); Array.Copy(ga, gOut, n); fOut = fa; return true; }
                        if (da * (hi - lo) >= 0.0) { hi = lo; fHi = fLo; dHi = dLo; }
                        lo = a; fLo = fa; dLo = da;
                    }
                }
                // Bracket collapsed: accept the best sufficient-decrease point when there is one.
                if (lo > 0.0 && fLo < f0)
                {
                    for (int i = 0; i < n; i++) xOut[i] = x[i] + lo * p[i];
                    Gradient(xOut, gOut);
                    fOut = fLo;
                    return true;
                }
                return false;
            }
        }

        // ---- small dense helpers -----------------------------------------------------------------------------

        static void Direction(double[,] h, double[] g, double[] p)
        {
            int n = g.Length;
            for (int i = 0; i < n; i++)
            {
                double sum = 0.0;
                for (int j = 0; j < n; j++) sum += h[i, j] * g[j];
                p[i] = -sum;
            }
        }

        static void ResetToIdentity(double[,] h)
        {
            int n = h.GetLength(0);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) h[i, j] = i == j ? 1.0 : 0.0;
        }

        static void ScaleIdentity(double[,] h, double scale)
        {
            int n = h.GetLength(0);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) h[i, j] = i == j ? scale : 0.0;
        }

        // H <- (I - rho s y') H (I - rho y s') + rho s s',  rho = 1 / s'y.
        static void Update(double[,] h, double[] s, double[] y, double sy)
        {
            int n = s.Length;
            double rho = 1.0 / sy;
            var hy = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0.0;
                for (int j = 0; j < n; j++) sum += h[i, j] * y[j];
                hy[i] = sum;
            }
            double yhy = Dot(y, hy);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    h[i, j] += (1.0 + rho * yhy) * rho * s[i] * s[j] - rho * (hy[i] * s[j] + s[i] * hy[j]);
        }

        static double Dot(double[] a, double[] b)
        {
            double sum = 0.0;
            for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
            return sum;
        }

        static double Norm(double[] a) => Math.Sqrt(Dot(a, a));

        static double MaxAbs(double[] a)
        {
            double m = 0.0;
            for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i]));
            return m;
        }
    }
}
