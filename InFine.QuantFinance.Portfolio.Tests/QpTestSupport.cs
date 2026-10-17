using System;
using System.Collections.Generic;
using Xunit;

namespace InFine.QuantFinance.Portfolio.Tests
{
    /// <summary>Independent references for the quadratic-programming tests: dense solves, KKT residuals, brute force.</summary>
    internal static class QpTestSupport
    {
        /// <summary>Solves a x = b by Gaussian elimination with partial pivoting; null when (numerically) singular.</summary>
        public static double[]? SolveLinear(double[,] a, double[] b)
        {
            int n = b.Length;
            var m = (double[,])a.Clone();
            var x = (double[])b.Clone();
            double scale = 0.0;
            foreach (double v in a) scale = Math.Max(scale, Math.Abs(v));
            for (int k = 0; k < n; k++)
            {
                int piv = k;
                for (int i = k + 1; i < n; i++)
                    if (Math.Abs(m[i, k]) > Math.Abs(m[piv, k])) piv = i;
                if (Math.Abs(m[piv, k]) <= 1e-13 * scale) return null;
                if (piv != k)
                {
                    for (int j = 0; j < n; j++) (m[k, j], m[piv, j]) = (m[piv, j], m[k, j]);
                    (x[k], x[piv]) = (x[piv], x[k]);
                }
                for (int i = k + 1; i < n; i++)
                {
                    double f = m[i, k] / m[k, k];
                    if (f == 0.0) continue;
                    for (int j = k; j < n; j++) m[i, j] -= f * m[k, j];
                    x[i] -= f * x[k];
                }
            }
            for (int k = n - 1; k >= 0; k--)
            {
                double s = x[k];
                for (int j = k + 1; j < n; j++) s -= m[k, j] * x[j];
                x[k] = s / m[k, k];
            }
            return x;
        }

        public static double[] SolveOrThrow(double[,] a, double[] b) =>
            SolveLinear(a, b) ?? throw new InvalidOperationException("Singular system in the test reference.");

        public static double[] Multiply(double[,] a, double[] x)
        {
            int rows = a.GetLength(0), cols = a.GetLength(1);
            var y = new double[rows];
            for (int i = 0; i < rows; i++)
                for (int k = 0; k < cols; k++) y[i] += a[i, k] * x[k];
            return y;
        }

        public static double Dot(double[] a, double[] b)
        {
            double s = 0.0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        public static double[] Ones(int n)
        {
            var v = new double[n];
            Array.Fill(v, 1.0);
            return v;
        }

        public static void Close(double[] expected, double[] actual, double tol, string what = "")
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert.True(Math.Abs(expected[i] - actual[i]) <= tol,
                    $"{what}[{i}]: expected {expected[i]:R}, got {actual[i]:R} (diff {Math.Abs(expected[i] - actual[i]):E2} > {tol:E1})");
        }

        /// <summary>Largest KKT residuals of <paramref name="r"/> for <paramref name="p"/> (with the reported ridge added to Q).</summary>
        public sealed record Kkt(double Stationarity, double Primal, double Dual, double Complementarity)
        {
            public void AssertBelow(double tol)
            {
                Assert.True(Stationarity <= tol, $"stationarity {Stationarity:E2}");
                Assert.True(Primal <= tol, $"primal feasibility {Primal:E2}");
                Assert.True(Dual <= tol, $"dual feasibility {Dual:E2}");
                Assert.True(Complementarity <= tol, $"complementary slackness {Complementarity:E2}");
            }
        }

        public static Kkt Residuals(QuadraticProgram p, QuadraticProgramResult r)
        {
            int n = p.Dimension;
            double[] x = r.X;
            var g = new double[n];
            for (int i = 0; i < n; i++)
            {
                g[i] = p.C[i] + r.Regularization * x[i];
                for (int k = 0; k < n; k++) g[i] += p.Q[i, k] * x[k];
            }
            double primal = 0.0, dual = 0.0, comp = 0.0;
            if (p.EqualityMatrix != null)
                for (int e = 0; e < p.EqualityVector!.Length; e++)
                {
                    double ax = 0.0;
                    for (int k = 0; k < n; k++) { ax += p.EqualityMatrix[e, k] * x[k]; g[k] += p.EqualityMatrix[e, k] * r.EqualityMultipliers[e]; }
                    primal = Math.Max(primal, Math.Abs(ax - p.EqualityVector[e]));
                }
            if (p.InequalityMatrix != null)
                for (int e = 0; e < p.InequalityVector!.Length; e++)
                {
                    double gx = 0.0, z = r.InequalityMultipliers[e];
                    for (int k = 0; k < n; k++) { gx += p.InequalityMatrix[e, k] * x[k]; g[k] += p.InequalityMatrix[e, k] * z; }
                    double slack = gx - p.InequalityVector[e];
                    primal = Math.Max(primal, slack);
                    dual = Math.Max(dual, -z);
                    comp = Math.Max(comp, Math.Abs(z * slack));
                }
            for (int k = 0; k < n; k++)
            {
                double zl = r.LowerBoundMultipliers[k], zu = r.UpperBoundMultipliers[k];
                g[k] += zu - zl;
                dual = Math.Max(dual, Math.Max(-zl, -zu));
                if (p.LowerBounds != null && !double.IsNegativeInfinity(p.LowerBounds[k]))
                {
                    primal = Math.Max(primal, p.LowerBounds[k] - x[k]);
                    comp = Math.Max(comp, Math.Abs(zl * (x[k] - p.LowerBounds[k])));
                }
                else Assert.Equal(0.0, zl);
                if (p.UpperBounds != null && !double.IsPositiveInfinity(p.UpperBounds[k]))
                {
                    primal = Math.Max(primal, x[k] - p.UpperBounds[k]);
                    comp = Math.Max(comp, Math.Abs(zu * (p.UpperBounds[k] - x[k])));
                }
                else Assert.Equal(0.0, zu);
            }
            double stat = 0.0;
            foreach (double v in g) stat = Math.Max(stat, Math.Abs(v));
            return new Kkt(stat, primal, dual, comp);
        }

        /// <summary>
        /// Brute force: every combination of active bounds (lower / upper / free per variable) and active inequality
        /// rows, each solved as an equality-constrained QP through its KKT linear system; the best primal-feasible
        /// point is the minimiser of the convex programme. Exponential: for a handful of variables only.
        /// </summary>
        public static double[] BruteForce(QuadraticProgram p, out double objective)
        {
            int n = p.Dimension;
            int m = p.EqualityVector?.Length ?? 0;
            int g = p.InequalityVector?.Length ?? 0;
            double[]? best = null;
            double bestObj = double.PositiveInfinity;
            int boundCombos = (int)Math.Pow(3, n);
            for (int bc = 0; bc < boundCombos; bc++)
                for (int gc = 0; gc < (1 << g); gc++)
                {
                    var rows = new List<double[]>();
                    var rhs = new List<double>();
                    for (int e = 0; e < m; e++) { rows.Add(Row(p.EqualityMatrix!, e, n)); rhs.Add(p.EqualityVector![e]); }
                    int code = bc;
                    bool skip = false;
                    for (int k = 0; k < n; k++, code /= 3)
                    {
                        int state = code % 3;
                        if (state == 0) continue;
                        double bound = state == 1 ? (p.LowerBounds?[k] ?? double.NegativeInfinity) : (p.UpperBounds?[k] ?? double.PositiveInfinity);
                        if (double.IsInfinity(bound)) { skip = true; break; }
                        var e = new double[n];
                        e[k] = 1.0;
                        rows.Add(e);
                        rhs.Add(bound);
                    }
                    if (skip) continue;
                    for (int e = 0; e < g; e++)
                        if ((gc & (1 << e)) != 0) { rows.Add(Row(p.InequalityMatrix!, e, n)); rhs.Add(p.InequalityVector![e]); }
                    if (rows.Count > n) continue;
                    int size = n + rows.Count;
                    var kkt = new double[size, size];
                    var b = new double[size];
                    for (int i = 0; i < n; i++)
                    {
                        b[i] = -p.C[i];
                        for (int k = 0; k < n; k++) kkt[i, k] = p.Q[i, k];
                    }
                    for (int r = 0; r < rows.Count; r++)
                    {
                        b[n + r] = rhs[r];
                        for (int k = 0; k < n; k++) { kkt[n + r, k] = rows[r][k]; kkt[k, n + r] = rows[r][k]; }
                    }
                    double[]? sol = SolveLinear(kkt, b);
                    if (sol == null) continue;
                    var x = new double[n];
                    Array.Copy(sol, x, n);
                    if (!Feasible(p, x, 1e-12)) continue;
                    double obj = p.Objective(x);
                    if (obj < bestObj) { bestObj = obj; best = x; }
                }
            objective = bestObj;
            return best ?? throw new InvalidOperationException("Brute force found no feasible point.");
        }

        static double[] Row(double[,] a, int r, int n)
        {
            var v = new double[n];
            for (int k = 0; k < n; k++) v[k] = a[r, k];
            return v;
        }

        public static bool Feasible(QuadraticProgram p, double[] x, double tol)
        {
            int n = p.Dimension;
            if (p.EqualityMatrix != null)
                for (int e = 0; e < p.EqualityVector!.Length; e++)
                {
                    double s = 0.0;
                    for (int k = 0; k < n; k++) s += p.EqualityMatrix[e, k] * x[k];
                    if (Math.Abs(s - p.EqualityVector[e]) > tol) return false;
                }
            if (p.InequalityMatrix != null)
                for (int e = 0; e < p.InequalityVector!.Length; e++)
                {
                    double s = 0.0;
                    for (int k = 0; k < n; k++) s += p.InequalityMatrix[e, k] * x[k];
                    if (s - p.InequalityVector[e] > tol) return false;
                }
            for (int k = 0; k < n; k++)
            {
                if (p.LowerBounds != null && x[k] < p.LowerBounds[k] - tol) return false;
                if (p.UpperBounds != null && x[k] > p.UpperBounds[k] + tol) return false;
            }
            return true;
        }

        /// <summary>Random symmetric positive definite matrix <c>B B^T / n + floor I</c>, deterministic in the seed.</summary>
        public static double[,] RandomSpd(int n, System.Random rng, double floor = 0.05)
        {
            var b = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int k = 0; k < n; k++) b[i, k] = 2.0 * rng.NextDouble() - 1.0;
            var q = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int k = 0; k <= i; k++)
                {
                    double s = 0.0;
                    for (int j = 0; j < n; j++) s += b[i, j] * b[k, j];
                    s /= n;
                    if (i == k) s += floor;
                    q[i, k] = s;
                    q[k, i] = s;
                }
            return q;
        }
    }
}
