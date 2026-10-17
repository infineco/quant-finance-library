using System;
using System.Collections.Generic;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.Portfolio
{
    /// <summary>
    /// Dense strictly convex quadratic programming by the dual active-set method of Goldfarb and Idnani ("A
    /// numerically stable dual method for solving strictly convex quadratic programs", Math. Programming 27, 1983).
    /// Host-side, no external dependency.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this method. Portfolio problems are small and dense (tens to a few hundred assets, a handful of
    /// equalities, bounds on every weight, a few group caps), the Hessian is a covariance matrix, and the answers are
    /// checked against closed forms and KKT conditions to 1e-10. An active-set method ends on an exact vertex of the
    /// KKT system, which first-order splitting methods such as ADMM (OSQP) only approach to their stopping tolerance.
    /// Among active-set methods the dual one needs no feasible starting point (no phase 1): it starts from the
    /// unconstrained minimiser <c>-Q^-1 c</c>, adds the most violated constraint at each step and keeps the
    /// multipliers dual feasible, so the dual objective increases monotonically and infeasibility shows up as an
    /// unbounded dual step. The factorisation <c>J = L^-T Q_A</c> (with <c>Q = L L^T</c> and <c>L^-1 N_A = Q_A R</c>)
    /// is updated by Givens reflections when a constraint enters or leaves, O(n^2) per change.
    /// </para>
    /// <para>
    /// The method needs <c>Q</c> positive definite; a semi-definite <c>Q</c> is ridged, see
    /// <see cref="QuadraticProgramOptions.RegularizationFloor"/>. A linear programme (<c>Q = 0</c>) is out of scope.
    /// On exit the solution and multipliers are recomputed once from the final factorisation
    /// (<c>x = J_1 R^-T b_A - J_2 J_2^T c</c>, <c>u = R^-1 J_1^T (Q x + c)</c>), which removes the rounding
    /// accumulated by the incremental steps. Equalities that are linear combinations of earlier ones are dropped when
    /// consistent (multiplier 0) and reported as infeasibility otherwise.
    /// </para>
    /// </remarks>
    public static class QuadraticProgramSolver
    {
        // A new constraint normal whose component outside the span of the active normals is below this fraction of
        // its norm (in the metric of Q) is treated as linearly dependent on them.
        const double DependenceTolerance = 1e-12;

        /// <summary>Solves <paramref name="problem"/>; never throws for infeasible or degenerate data, see the status.</summary>
        /// <exception cref="ArgumentException">Inconsistent dimensions or non-finite data.</exception>
        public static QuadraticProgramResult Solve(QuadraticProgram problem, QuadraticProgramOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(problem);
            options ??= new QuadraticProgramOptions();
            var solver = new Engine(problem, options);
            return solver.Run();
        }

        enum Kind { Equality, Inequality, Lower, Upper }

        sealed class Engine
        {
            readonly QuadraticProgram problem;
            readonly QuadraticProgramOptions options;
            readonly int n, m, p;
            readonly double[,] h;      // Hessian, ridged if needed
            readonly double[] c;
            double delta;

            // Constraints in the form normal^T x >= rhs (equalities: = rhs). Equalities come first.
            readonly List<Kind> kind = new();
            readonly List<int> origin = new();        // row of A or G, or variable index for a bound
            readonly List<double[]?> normal = new();  // dense normal, null for a bound
            readonly List<double> rhs = new();
            readonly List<double> normalNorm = new();

            double[,] j = new double[0, 0], r = new double[0, 0];
            int q;
            int[] active = Array.Empty<int>();
            double[] u = Array.Empty<double>();
            bool[] isActive = Array.Empty<bool>();
            readonly bool[] redundant;
            double[] x = Array.Empty<double>();
            int iterations;

            public Engine(QuadraticProgram problem, QuadraticProgramOptions options)
            {
                this.problem = problem;
                this.options = options;
                ArgumentNullException.ThrowIfNull(problem.Q, nameof(problem.Q));
                ArgumentNullException.ThrowIfNull(problem.C, nameof(problem.C));
                n = problem.C.Length;
                if (n == 0) throw new ArgumentException("The programme has no variable.", nameof(problem));
                if (problem.Q.GetLength(0) != n || problem.Q.GetLength(1) != n)
                    throw new ArgumentException($"Q must be {n}x{n}, got {problem.Q.GetLength(0)}x{problem.Q.GetLength(1)}.", nameof(problem));
                if (!Matrix.IsFinite(problem.Q)) throw new ArgumentException("Q must be finite.", nameof(problem));
                RequireFinite(problem.C, "C");
                if (!(options.FeasibilityTolerance >= 0.0) || !(options.PositiveDefiniteTolerance >= 0.0) || !(options.RegularizationFloor >= 0.0))
                    throw new ArgumentException("Tolerances must be non-negative.", nameof(options));

                m = Rows(problem.EqualityMatrix, problem.EqualityVector, "Equality");
                p = Rows(problem.InequalityMatrix, problem.InequalityVector, "Inequality");
                c = (double[])problem.C.Clone();
                h = Matrix.Symmetrize(problem.Q);
                double scale = 0.0;
                foreach (double v in problem.Q) scale = Math.Max(scale, Math.Abs(v));
                if (Matrix.MaxAsymmetry(problem.Q) > 1e-10 * Math.Max(scale, double.Epsilon))
                    throw new ArgumentException("Q must be symmetric.", nameof(problem));

                for (int i = 0; i < m; i++) AddDense(Kind.Equality, i, problem.EqualityMatrix!, problem.EqualityVector![i], 1.0);
                for (int i = 0; i < p; i++) AddDense(Kind.Inequality, i, problem.InequalityMatrix!, problem.InequalityVector![i], -1.0);
                double[]? lo = problem.LowerBounds, hi = problem.UpperBounds;
                if (lo != null && lo.Length != n) throw new ArgumentException($"LowerBounds must have length {n}.", nameof(problem));
                if (hi != null && hi.Length != n) throw new ArgumentException($"UpperBounds must have length {n}.", nameof(problem));
                for (int v = 0; v < n; v++)
                {
                    if (lo != null && (double.IsNaN(lo[v]) || lo[v] == double.PositiveInfinity)) throw new ArgumentException("Lower bounds must be numbers below +infinity.", nameof(problem));
                    if (hi != null && (double.IsNaN(hi[v]) || hi[v] == double.NegativeInfinity)) throw new ArgumentException("Upper bounds must be numbers above -infinity.", nameof(problem));
                }
                for (int v = 0; v < n; v++)
                    if (lo != null && !double.IsNegativeInfinity(lo[v])) AddBound(Kind.Lower, v, lo[v]);
                for (int v = 0; v < n; v++)
                    if (hi != null && !double.IsPositiveInfinity(hi[v])) AddBound(Kind.Upper, v, -hi[v]);
                redundant = new bool[m];
            }

            int Rows(double[,]? a, double[]? b, string name)
            {
                if (a == null && b == null) return 0;
                if (a == null || b == null) throw new ArgumentException($"{name}Matrix and {name}Vector must be given together.", nameof(problem));
                if (a.GetLength(1) != n || a.GetLength(0) != b.Length)
                    throw new ArgumentException($"{name}Matrix must be {b.Length}x{n}, got {a.GetLength(0)}x{a.GetLength(1)}.", nameof(problem));
                if (!Matrix.IsFinite(a)) throw new ArgumentException($"{name}Matrix must be finite.", nameof(problem));
                RequireFinite(b, name + "Vector");
                return b.Length;
            }

            static void RequireFinite(double[] v, string name)
            {
                foreach (double e in v)
                    if (!double.IsFinite(e)) throw new ArgumentException($"{name} must be finite.");
            }

            void AddDense(Kind k, int row, double[,] a, double b, double sign)
            {
                var nv = new double[n];
                double norm = 0.0;
                for (int v = 0; v < n; v++)
                {
                    nv[v] = sign * a[row, v];
                    norm += nv[v] * nv[v];
                }
                kind.Add(k);
                origin.Add(row);
                normal.Add(nv);
                rhs.Add(sign * b);
                normalNorm.Add(Math.Sqrt(norm));
            }

            void AddBound(Kind k, int v, double b)
            {
                kind.Add(k);
                origin.Add(v);
                normal.Add(null);
                rhs.Add(b);
                normalNorm.Add(1.0);
            }

            public QuadraticProgramResult Run()
            {
                double[]? lo = problem.LowerBounds, hi = problem.UpperBounds;
                if (lo != null && hi != null)
                    for (int v = 0; v < n; v++)
                        if (lo[v] > hi[v]) return Finish(QuadraticProgramStatus.Infeasible, new double[n]);

                if (!Factor()) return Finish(QuadraticProgramStatus.NotConvex, new double[n]);

                int nc = kind.Count;
                r = new double[n, n];
                active = new int[n];
                u = new double[n];
                isActive = new bool[nc];
                q = 0;

                // Unconstrained minimiser x = -J J^T c.
                x = new double[n];
                var jtc = new double[n];
                for (int k = 0; k < n; k++)
                {
                    double s = 0.0;
                    for (int i = 0; i <= k; i++) s += j[i, k] * c[i];
                    jtc[k] = s;
                }
                for (int i = 0; i < n; i++)
                {
                    double s = 0.0;
                    for (int k = i; k < n; k++) s += j[i, k] * jtc[k];
                    x[i] = -s;
                }

                var d = new double[n];
                var z = new double[n];
                var step = new double[n];
                int maxIterations = options.MaxIterations > 0 ? options.MaxIterations : Math.Max(1000, 20 * (n + nc));

                // Equalities: full steps, multipliers of either sign, never dropped.
                for (int i = 0; i < m; i++)
                {
                    iterations++;
                    double dnorm = Directions(i, d, z, step, out double d2sq);
                    double slack = Slack(i, x, out double scale);
                    if (Math.Sqrt(d2sq) <= DependenceTolerance * dnorm)
                    {
                        if (Math.Abs(slack) <= Tolerance(scale)) { redundant[i] = true; continue; }
                        return Finish(QuadraticProgramStatus.Infeasible, x);
                    }
                    double t = -slack / d2sq;
                    for (int v = 0; v < n; v++) x[v] += t * z[v];
                    for (int k = 0; k < q; k++) u[k] -= t * step[k];
                    if (!Add(i, t, d)) return Finish(QuadraticProgramStatus.Infeasible, x);
                }
                int equalities = q;

                while (true)
                {
                    int pc = MostViolated(nc);
                    if (pc < 0) break;
                    double uPlus = 0.0;
                    while (true)
                    {
                        if (++iterations > maxIterations) return Finish(QuadraticProgramStatus.MaxIterationsReached, x);
                        double dnorm = Directions(pc, d, z, step, out double d2sq);

                        // Largest dual step keeping the multipliers of the active inequalities non-negative.
                        double t1 = double.PositiveInfinity;
                        int drop = -1;
                        for (int k = equalities; k < q; k++)
                        {
                            if (step[k] <= 0.0) continue;
                            double ratio = u[k] / step[k];
                            if (ratio < t1) { t1 = ratio; drop = k; }
                        }
                        // Primal step that makes the constraint active.
                        bool noPrimal = Math.Sqrt(d2sq) <= DependenceTolerance * dnorm;
                        double t2 = noPrimal ? double.PositiveInfinity : -Slack(pc, x, out _) / d2sq;

                        if (noPrimal)
                        {
                            if (double.IsPositiveInfinity(t1)) return Finish(QuadraticProgramStatus.Infeasible, x);
                            for (int k = 0; k < q; k++) u[k] -= t1 * step[k];
                            uPlus += t1;
                            Drop(drop);
                            continue;
                        }

                        double t = Math.Min(t1, t2);
                        for (int v = 0; v < n; v++) x[v] += t * z[v];
                        for (int k = 0; k < q; k++) u[k] -= t * step[k];
                        uPlus += t;
                        if (t2 <= t1)
                        {
                            if (!Add(pc, uPlus, d)) return Finish(QuadraticProgramStatus.Infeasible, x);
                            break;
                        }
                        Drop(drop);
                    }
                }

                Refine();
                return Finish(QuadraticProgramStatus.Optimal, x);
            }

            // Cholesky of the (ridged) Hessian and J = L^-T.
            bool Factor()
            {
                double maxDiag = 0.0;
                for (int i = 0; i < n; i++) maxDiag = Math.Max(maxDiag, h[i, i]);
                if (!(maxDiag > 0.0)) return false;
                double[,] l;
                if (!TryFactorStrict(h, maxDiag, out l))
                {
                    if (options.RegularizationFloor <= 0.0) return false;
                    double ridge = options.RegularizationFloor * maxDiag;
                    bool ok = false;
                    for (int attempt = 0; attempt < 7 && !ok; attempt++, ridge *= 10.0)
                    {
                        var hr = (double[,])h.Clone();
                        for (int i = 0; i < n; i++) hr[i, i] += ridge;
                        if (TryFactorStrict(hr, maxDiag, out l))
                        {
                            ok = true;
                            delta = ridge;
                            for (int i = 0; i < n; i++) h[i, i] += ridge;
                        }
                    }
                    if (!ok) return false;
                }
                j = new double[n, n];
                // Columns of L^-1 by forward substitution; J = (L^-1)^T is upper triangular.
                for (int col = 0; col < n; col++)
                {
                    j[col, col] = 1.0 / l[col, col];
                    for (int row = col + 1; row < n; row++)
                    {
                        double s = 0.0;
                        for (int k = col; k < row; k++) s += l[row, k] * j[col, k];
                        j[col, row] = -s / l[row, row];
                    }
                }
                return true;
            }

            bool TryFactorStrict(double[,] a, double maxDiag, out double[,] l)
            {
                if (!Cholesky.TryFactor(a, out l)) return false;
                for (int i = 0; i < n; i++)
                    if (!(l[i, i] * l[i, i] > options.PositiveDefiniteTolerance * maxDiag)) return false;
                return true;
            }

            double Tolerance(double scale) => options.FeasibilityTolerance * Math.Max(1.0, scale);

            // normal_i^T x - rhs_i, and the size of the terms it is made of.
            double Slack(int i, double[] point, out double scale)
            {
                double b = rhs[i];
                double[]? nv = normal[i];
                double s, abs;
                if (nv == null)
                {
                    double sign = kind[i] == Kind.Lower ? 1.0 : -1.0;
                    s = sign * point[origin[i]];
                    abs = Math.Abs(s);
                }
                else
                {
                    s = 0.0;
                    abs = 0.0;
                    for (int v = 0; v < n; v++)
                    {
                        double t = nv[v] * point[v];
                        s += t;
                        abs += Math.Abs(t);
                    }
                }
                scale = Math.Max(abs, Math.Abs(b));
                return s - b;
            }

            int MostViolated(int nc)
            {
                int best = -1;
                double bestScore = 0.0;
                for (int i = m; i < nc; i++)
                {
                    if (isActive[i]) continue;
                    double s = Slack(i, x, out double scale);
                    if (s >= -Tolerance(scale)) continue;
                    double score = s / normalNorm[i];
                    if (best < 0 || score < bestScore) { best = i; bestScore = score; }
                }
                return best;
            }

            // d = J^T n_i; z = J_2 d_2 (primal direction); step = R^-1 d_1 (dual direction). Returns ||d||.
            double Directions(int i, double[] d, double[] z, double[] step, out double d2sq)
            {
                JtN(i, d);
                double dsq = 0.0;
                d2sq = 0.0;
                for (int k = 0; k < n; k++)
                {
                    dsq += d[k] * d[k];
                    if (k >= q) d2sq += d[k] * d[k];
                }
                for (int row = 0; row < n; row++)
                {
                    double s = 0.0;
                    for (int k = q; k < n; k++) s += j[row, k] * d[k];
                    z[row] = s;
                }
                for (int k = q - 1; k >= 0; k--)
                {
                    double s = d[k];
                    for (int l = k + 1; l < q; l++) s -= r[k, l] * step[l];
                    step[k] = s / r[k, k];
                }
                return Math.Sqrt(dsq);
            }

            void JtN(int i, double[] d)
            {
                double[]? nv = normal[i];
                if (nv == null)
                {
                    int v = origin[i];
                    double sign = kind[i] == Kind.Lower ? 1.0 : -1.0;
                    for (int k = 0; k < n; k++) d[k] = sign * j[v, k];
                    return;
                }
                for (int k = 0; k < n; k++)
                {
                    double s = 0.0;
                    for (int row = 0; row < n; row++) s += j[row, k] * nv[row];
                    d[k] = s;
                }
            }

            // Adds constraint i with multiplier mult: rotates d_2 onto its first component and appends a column to R.
            bool Add(int i, double mult, double[] d)
            {
                JtN(i, d);
                double dnorm = 0.0;
                for (int k = 0; k < n; k++) dnorm += d[k] * d[k];
                dnorm = Math.Sqrt(dnorm);
                for (int k = n - 1; k > q; k--)
                {
                    double a = d[k - 1], b = d[k];
                    if (b == 0.0) continue;
                    double hyp = Hypot(a, b);
                    double cs = a / hyp, sn = b / hyp;
                    d[k - 1] = hyp;
                    d[k] = 0.0;
                    for (int row = 0; row < n; row++)
                    {
                        double j1 = j[row, k - 1], j2 = j[row, k];
                        j[row, k - 1] = cs * j1 + sn * j2;
                        j[row, k] = sn * j1 - cs * j2;
                    }
                }
                if (q >= n || Math.Abs(d[q]) <= DependenceTolerance * dnorm) return false;
                for (int k = 0; k <= q; k++) r[k, q] = d[k];
                active[q] = i;
                u[q] = mult;
                isActive[i] = true;
                q++;
                return true;
            }

            // Removes the active constraint at position pos and restores the triangular form of R.
            void Drop(int pos)
            {
                isActive[active[pos]] = false;
                for (int k = pos; k < q - 1; k++)
                {
                    active[k] = active[k + 1];
                    u[k] = u[k + 1];
                    for (int row = 0; row <= k + 1; row++) r[row, k] = r[row, k + 1];
                }
                for (int row = 0; row < n; row++) r[row, q - 1] = 0.0;
                for (int k = pos; k < q - 1; k++)
                {
                    double a = r[k, k], b = r[k + 1, k];
                    if (b == 0.0) continue;
                    double hyp = Hypot(a, b);
                    double cs = a / hyp, sn = b / hyp;
                    for (int col = k; col < q - 1; col++)
                    {
                        double r1 = r[k, col], r2 = r[k + 1, col];
                        r[k, col] = cs * r1 + sn * r2;
                        r[k + 1, col] = sn * r1 - cs * r2;
                    }
                    r[k + 1, k] = 0.0;
                    for (int row = 0; row < n; row++)
                    {
                        double j1 = j[row, k], j2 = j[row, k + 1];
                        j[row, k] = cs * j1 + sn * j2;
                        j[row, k + 1] = sn * j1 - cs * j2;
                    }
                }
                q--;
                u[q] = 0.0;
            }

            // Solves the KKT system of the final active set from the factorisation: x = J1 R^-T b_A - J2 J2^T c,
            // u = R^-1 J1^T (H x + c).
            void Refine()
            {
                var v = new double[q];
                for (int k = 0; k < q; k++)
                {
                    double s = rhs[active[k]];
                    for (int l = 0; l < k; l++) s -= r[l, k] * v[l];
                    v[k] = s / r[k, k];
                }
                var w = new double[n];
                for (int k = q; k < n; k++)
                {
                    double s = 0.0;
                    for (int row = 0; row < n; row++) s += j[row, k] * c[row];
                    w[k] = s;
                }
                for (int row = 0; row < n; row++)
                {
                    double s = 0.0;
                    for (int k = 0; k < q; k++) s += j[row, k] * v[k];
                    for (int k = q; k < n; k++) s -= j[row, k] * w[k];
                    x[row] = s;
                }
                var g = new double[n];
                for (int row = 0; row < n; row++)
                {
                    double s = c[row];
                    for (int col = 0; col < n; col++) s += h[row, col] * x[col];
                    g[row] = s;
                }
                var e = new double[q];
                for (int k = 0; k < q; k++)
                {
                    double s = 0.0;
                    for (int row = 0; row < n; row++) s += j[row, k] * g[row];
                    e[k] = s;
                }
                for (int k = q - 1; k >= 0; k--)
                {
                    double s = e[k];
                    for (int l = k + 1; l < q; l++) s -= r[k, l] * u[l];
                    u[k] = s / r[k, k];
                }
            }

            QuadraticProgramResult Finish(QuadraticProgramStatus status, double[] point)
            {
                var y = new double[m];
                var zi = new double[p];
                var zl = new double[n];
                var zu = new double[n];
                var ai = new List<int>();
                var al = new List<int>();
                var au = new List<int>();
                for (int k = 0; k < q; k++)
                {
                    int i = active[k];
                    int o = origin[i];
                    switch (kind[i])
                    {
                        case Kind.Equality: y[o] = -u[k]; break;
                        case Kind.Inequality: zi[o] = u[k]; ai.Add(o); break;
                        case Kind.Lower: zl[o] = u[k]; al.Add(o); break;
                        case Kind.Upper: zu[o] = u[k]; au.Add(o); break;
                    }
                }
                ai.Sort();
                al.Sort();
                au.Sort();
                var xr = (double[])point.Clone();
                return new QuadraticProgramResult
                {
                    Status = status,
                    X = xr,
                    Objective = problem.Objective(xr),
                    EqualityMultipliers = y,
                    InequalityMultipliers = zi,
                    LowerBoundMultipliers = zl,
                    UpperBoundMultipliers = zu,
                    ActiveInequalities = ai.ToArray(),
                    ActiveLowerBounds = al.ToArray(),
                    ActiveUpperBounds = au.ToArray(),
                    Iterations = iterations,
                    Regularization = delta,
                };
            }

            static double Hypot(double a, double b)
            {
                double aa = Math.Abs(a), bb = Math.Abs(b);
                if (aa < bb) (aa, bb) = (bb, aa);
                if (aa == 0.0) return 0.0;
                double t = bb / aa;
                return aa * Math.Sqrt(1.0 + t * t);
            }
        }
    }
}
