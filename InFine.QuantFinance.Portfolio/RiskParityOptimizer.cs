using System;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.Portfolio
{
    /// <summary>Algorithm used by <see cref="RiskParityOptimizer"/>.</summary>
    public enum RiskParityMethod
    {
        /// <summary>Damped Newton on the Spinu objective (default): quadratic convergence, O(n^3) per iteration.</summary>
        Newton,

        /// <summary>Cyclical coordinate descent (Griveau-Billion, Richard and Roncalli 2013): linear convergence, O(n^2) per sweep.</summary>
        CoordinateDescent,
    }

    /// <summary>Settings of <see cref="RiskParityOptimizer"/>.</summary>
    public sealed class RiskParityOptions
    {
        /// <summary>Algorithm to try first. Default <see cref="RiskParityMethod.Newton"/>.</summary>
        public RiskParityMethod Method { get; init; } = RiskParityMethod.Newton;

        /// <summary>
        /// Convergence threshold on <c>max_i |RC_i / b_i - 1|</c>, the worst relative deviation of a risk contribution from
        /// its budget. Default 1e-13, close to what double precision can certify.
        /// </summary>
        public double Tolerance { get; init; } = 1e-13;

        /// <summary>Maximum Newton iterations (each a Cholesky factorisation). Default 100.</summary>
        public int MaxNewtonIterations { get; init; } = 100;

        /// <summary>Maximum coordinate-descent sweeps (each updates every asset once). Default 100000.</summary>
        public int MaxSweeps { get; init; } = 100_000;

        /// <summary>
        /// When Newton fails (failed Hessian factorisation or line search, or iteration limit), run
        /// coordinate descent from the initial point before giving up. Default true.
        /// </summary>
        public bool FallbackToCoordinateDescent { get; init; } = true;
    }

    /// <summary>Result of <see cref="RiskParityOptimizer"/>.</summary>
    public sealed class RiskBudgetingResult
    {
        internal RiskBudgetingResult(double[] weights, RiskContributions contributions, RiskParityMethod method, int iterations, double residual)
        {
            Weights = weights;
            Contributions = contributions;
            Method = method;
            Iterations = iterations;
            Residual = residual;
        }

        /// <summary>Long-only weights, strictly positive, summing to one.</summary>
        public double[] Weights { get; }

        /// <summary>Risk contributions of <see cref="Weights"/>.</summary>
        public RiskContributions Contributions { get; }

        /// <summary>The algorithm that produced the result (differs from the requested one after a fallback).</summary>
        public RiskParityMethod Method { get; }

        /// <summary>Newton iterations or coordinate-descent sweeps of the algorithm that succeeded.</summary>
        public int Iterations { get; }

        /// <summary>Achieved <c>max_i |RC_i / b_i - 1|</c>.</summary>
        public double Residual { get; }
    }

    /// <summary>
    /// Long-only risk budgeting: weights whose relative risk contributions equal given budgets <c>b</c> (equal risk
    /// contribution, ERC, when <c>b_i = 1/n</c>). Host-side code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Formulation.</b> The first-order conditions <c>w_i (Sigma w)_i = b_i w' Sigma w</c> are the stationarity conditions of
    /// the strictly convex problem (Spinu 2013; Roncalli, "Introduction to Risk Parity and Budgeting", 2013)
    /// <c>min over y &gt; 0 of  f(y) = (1/2) y' Sigma y - sum_i b_i ln y_i</c>, whose unique minimiser satisfies
    /// <c>y_i (Sigma y)_i = b_i</c>; then <c>w = y / sum y</c>. The objective is strictly convex on the open orthant for any
    /// positive definite <c>Sigma</c> (the log barrier makes it strictly convex even when <c>Sigma</c> is only
    /// semi-definite), it blows up at the boundary so the minimiser is interior, and it is self-concordant-like, so
    /// damped Newton converges globally.
    /// </para>
    /// <para>
    /// <b>Newton</b> (default). Gradient <c>Sigma y - b/y</c>, Hessian <c>Sigma + diag(b / y^2)</c> (positive definite: Sigma
    /// plus a positive diagonal), solved by Cholesky; the step is shortened by backtracking until <c>y &gt; 0</c> and the
    /// Armijo condition hold. From the starting point <c>y_i proportional to sqrt(b_i)/sigma_i</c> (rescaled so that
    /// <c>y' Sigma y = sum b = 1</c>, the identity satisfied at the optimum) it needs about 5 to 15 iterations independent of n.
    /// <b>Coordinate descent</b> minimises f exactly in one coordinate at a time:
    /// <c>y_i = (-c_i + sqrt(c_i^2 + 4 Sigma_ii b_i)) / (2 Sigma_ii)</c> with <c>c_i = sum_{j != i} Sigma_ij y_j</c>, always
    /// positive. It is trivial, needs no factorisation and never leaves the orthant, but converges linearly, slowly when assets
    /// are highly correlated (it needs thousands of sweeps at tolerance 1e-13 for ill-conditioned matrices).
    /// </para>
    /// <para>
    /// <b>Why Newton by default.</b> Cost is dominated by the O(n^3/3) Cholesky, but the iteration count is small and
    /// independent of conditioning, so total work is a few factorisations; CCD costs O(n^2) per sweep and a sweep count that
    /// grows with correlation and with n. Newton is therefore both faster and more accurate at tight tolerances for any n a
    /// covariance matrix of this library is used with, and CCD remains the fallback when Newton stalls (a failed line
    /// search or Hessian factorisation on a nearly singular covariance) and a cross-check in the tests.
    /// </para>
    /// <para>
    /// Solutions are invariant to scaling the covariance (<c>y</c> scales by <c>1/sqrt(c)</c>, <c>w</c> not at all). Risk
    /// contribution conventions are those of <see cref="RiskBudgeting.Contributions"/>.
    /// </para>
    /// </remarks>
    public static class RiskParityOptimizer
    {
        /// <summary>Equal risk contribution portfolio: <see cref="Solve"/> with budgets <c>1/n</c>.</summary>
        public static RiskBudgetingResult EqualRiskContribution(double[,] covariance, RiskParityOptions? options = null)
        {
            RiskBudgetingArgs.Covariance(covariance);
            int n = covariance.GetLength(0);
            var b = new double[n];
            for (int i = 0; i < n; i++) b[i] = 1.0 / n;
            return Solve(covariance, b, options);
        }

        /// <summary>Long-only risk-budgeting portfolio for the given budgets.</summary>
        /// <param name="covariance">One-period covariance: symmetric, finite, positive definite (semi-definite is tolerated), positive variances.</param>
        /// <param name="budgets">Target relative risk contributions: finite, strictly positive, summing to 1 within 1e-9.</param>
        /// <param name="options">Algorithm and tolerances; null for the defaults.</param>
        /// <exception cref="ArgumentException">Invalid budgets, or a covariance that is not symmetric positive semi-definite (checked by a Cholesky factorisation of Sigma + 1e-9 mean(diag) I).</exception>
        /// <exception cref="InvalidOperationException">No algorithm reached the tolerance within the iteration limits.</exception>
        public static RiskBudgetingResult Solve(double[,] covariance, double[] budgets, RiskParityOptions? options = null)
        {
            RiskBudgetingArgs.Covariance(covariance);
            Budgets(budgets, covariance.GetLength(0));
            options ??= new RiskParityOptions();
            if (!(options.Tolerance > 0.0) || options.MaxNewtonIterations < 1 || options.MaxSweeps < 1)
                throw new ArgumentException("Tolerance must be positive and iteration limits at least 1.", nameof(options));

            int n = budgets.Length;
            RequirePositiveSemiDefinite(covariance);
            double[] y0 = InitialPoint(covariance, budgets);
            bool newtonFirst = options.Method == RiskParityMethod.Newton;
            double[]? y = null;
            int iterations = 0;
            RiskParityMethod used = options.Method;

            if (newtonFirst)
            {
                y = Newton(covariance, budgets, y0, options.Tolerance, options.MaxNewtonIterations, out iterations);
                if (y == null && options.FallbackToCoordinateDescent)
                {
                    y = CoordinateDescent(covariance, budgets, y0, options.Tolerance, options.MaxSweeps, out iterations);
                    used = RiskParityMethod.CoordinateDescent;
                }
            }
            else
            {
                y = CoordinateDescent(covariance, budgets, y0, options.Tolerance, options.MaxSweeps, out iterations);
                if (y == null && options.FallbackToCoordinateDescent)
                {
                    y = Newton(covariance, budgets, y0, options.Tolerance, options.MaxNewtonIterations, out iterations);
                    used = RiskParityMethod.Newton;
                }
            }
            if (y == null) throw new InvalidOperationException("Risk budgeting did not converge; check that the covariance matrix is positive semi-definite.");

            double sum = 0.0;
            for (int i = 0; i < n; i++) sum += y[i];
            var w = new double[n];
            for (int i = 0; i < n; i++) w[i] = y[i] / sum;
            RiskContributions rc = RiskBudgeting.Contributions(w, covariance);
            double residual = 0.0;
            for (int i = 0; i < n; i++) residual = Math.Max(residual, Math.Abs(rc.Relative[i] / budgets[i] - 1.0));
            return new RiskBudgetingResult(w, rc, used, iterations, residual);
        }

        /// <summary>Rejects a covariance with a clearly negative eigenvalue: Sigma + 1e-9 mean(diag) I must be positive definite.</summary>
        static void RequirePositiveSemiDefinite(double[,] cov)
        {
            int n = cov.GetLength(0);
            double eps = 0.0;
            for (int i = 0; i < n; i++) eps += cov[i, i];
            eps *= 1e-9 / n;
            var shifted = (double[,])cov.Clone();
            for (int i = 0; i < n; i++) shifted[i, i] += eps;
            if (!Cholesky.TryFactor(shifted, out _))
                throw new ArgumentException("Covariance must be positive semi-definite.", nameof(cov));
        }

        static void Budgets(double[] budgets, int n)
        {
            ArgumentNullException.ThrowIfNull(budgets);
            if (budgets.Length != n) throw new ArgumentException($"Expected {n} risk budgets, got {budgets.Length}.", nameof(budgets));
            double sum = 0.0;
            foreach (double b in budgets)
            {
                if (!double.IsFinite(b) || !(b > 0.0)) throw new ArgumentException("Risk budgets must be finite and strictly positive.", nameof(budgets));
                sum += b;
            }
            if (!(Math.Abs(sum - 1.0) <= 1e-9)) throw new ArgumentException($"Risk budgets must sum to 1 (sum is {sum:R}).", nameof(budgets));
        }

        /// <summary>y_i proportional to sqrt(b_i)/sigma_i, scaled so that y' Sigma y = 1.</summary>
        static double[] InitialPoint(double[,] cov, double[] b)
        {
            int n = b.Length;
            var y = new double[n];
            for (int i = 0; i < n; i++) y[i] = Math.Sqrt(b[i]) / Math.Sqrt(cov[i, i]);
            double[] sy = RiskBudgetingArgs.Multiply(cov, y);
            double q = 0.0;
            for (int i = 0; i < n; i++) q += y[i] * sy[i];
            double s = 1.0 / Math.Sqrt(q);
            for (int i = 0; i < n; i++) y[i] *= s;
            return y;
        }

        /// <summary>max_i |y_i (Sigma y)_i / b_i - 1|; scale-free. Fills <paramref name="sy"/> with Sigma y.</summary>
        static double Residual(double[,] cov, double[] b, double[] y, double[] sy)
        {
            int n = y.Length;
            double r = 0.0;
            for (int i = 0; i < n; i++)
            {
                double s = 0.0;
                for (int j = 0; j < n; j++) s += cov[i, j] * y[j];
                sy[i] = s;
                r = Math.Max(r, Math.Abs(y[i] * s / b[i] - 1.0));
            }
            return double.IsNaN(r) ? double.PositiveInfinity : r;
        }

        static double Objective(double[] y, double[] sy, double[] b)
        {
            double f = 0.0;
            for (int i = 0; i < y.Length; i++) f += 0.5 * y[i] * sy[i] - b[i] * Math.Log(y[i]);
            return f;
        }

        // ---- Newton ---------------------------------------------------------------------------------------------

        static double[]? Newton(double[,] cov, double[] b, double[] start, double tol, int maxIter, out int iterations)
        {
            int n = b.Length;
            var y = (double[])start.Clone();
            var sy = new double[n];
            var trial = new double[n];
            var syTrial = new double[n];
            var g = new double[n];
            var h = new double[n, n];
            double r = Residual(cov, b, y, sy);
            iterations = 0;
            while (true)
            {
                if (r <= tol) return y;
                if (iterations >= maxIter) return null;
                iterations++;

                for (int i = 0; i < n; i++)
                {
                    g[i] = sy[i] - b[i] / y[i];
                    for (int j = 0; j <= i; j++) h[i, j] = cov[i, j];
                    h[i, i] += b[i] / (y[i] * y[i]);
                }
                if (!Cholesky.TryFactor(h, out double[,] l)) return null;
                double[] d = SolveCholesky(l, g);
                double slope = 0.0;
                for (int i = 0; i < n; i++) { d[i] = -d[i]; slope += g[i] * d[i]; }
                if (!(slope < 0.0)) return null;

                // Backtracking: stay in the open orthant, then require sufficient decrease. The slack absorbs
                // the rounding noise of f near the optimum, where decreases fall below machine precision.
                double f0 = Objective(y, sy, b), slack = 8.0 * 2.220446049250313e-16 * (Math.Abs(f0) + 1.0);
                double t = 1.0;
                double rNew = double.PositiveInfinity;
                bool accepted = false;
                for (int k = 0; k < 60; k++, t *= 0.5)
                {
                    bool positive = true;
                    for (int i = 0; i < n && positive; i++)
                    {
                        trial[i] = y[i] + t * d[i];
                        positive = trial[i] > 0.0;
                    }
                    if (!positive) continue;
                    rNew = Residual(cov, b, trial, syTrial);
                    double f1 = Objective(trial, syTrial, b);
                    if (f1 <= f0 + 1e-4 * t * slope + slack) { accepted = true; break; }
                }
                if (!accepted) return null;
                Array.Copy(trial, y, n);
                Array.Copy(syTrial, sy, n);
                r = rNew;
            }
        }

        /// <summary>Solves L L' x = rhs for a lower Cholesky factor.</summary>
        static double[] SolveCholesky(double[,] l, double[] rhs)
        {
            int n = rhs.Length;
            var x = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = rhs[i];
                for (int k = 0; k < i; k++) s -= l[i, k] * x[k];
                x[i] = s / l[i, i];
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double s = x[i];
                for (int k = i + 1; k < n; k++) s -= l[k, i] * x[k];
                x[i] = s / l[i, i];
            }
            return x;
        }

        // ---- cyclical coordinate descent ------------------------------------------------------------------------

        static double[]? CoordinateDescent(double[,] cov, double[] b, double[] start, double tol, int maxSweeps, out int sweeps)
        {
            int n = b.Length;
            var y = (double[])start.Clone();
            var sy = new double[n];
            sweeps = 0;
            while (true)
            {
                if (Residual(cov, b, y, sy) <= tol) return y;
                if (sweeps >= maxSweeps) return null;
                sweeps++;
                for (int i = 0; i < n; i++)
                {
                    double sii = cov[i, i], c = 0.0;
                    for (int j = 0; j < n; j++)
                        if (j != i) c += cov[i, j] * y[j];
                    // Positive root of sii y^2 + c y - b_i = 0, written to avoid cancellation when c >= 0.
                    double disc = Math.Sqrt(c * c + 4.0 * sii * b[i]);
                    y[i] = c >= 0.0 ? 2.0 * b[i] / (c + disc) : (-c + disc) / (2.0 * sii);
                }
            }
        }
    }
}
