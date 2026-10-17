using System;
using Xunit;
using static InFine.QuantFinance.Portfolio.Tests.QpTestSupport;

namespace InFine.QuantFinance.Portfolio.Tests
{
    /// <summary>Goldfarb-Idnani dual active-set QP: closed forms, a published example, KKT on random problems, brute force.</summary>
    public class QuadraticProgramSolverTests
    {
        [Fact]
        public void UnconstrainedMinimiserIsMinusQInverseC()
        {
            var rng = new System.Random(7);
            double[,] q = RandomSpd(12, rng);
            var c = new double[12];
            for (int i = 0; i < c.Length; i++) c[i] = rng.NextDouble() - 0.5;
            QuadraticProgramResult r = QuadraticProgramSolver.Solve(new QuadraticProgram(q, c));
            Assert.Equal(QuadraticProgramStatus.Optimal, r.Status);
            double[] neg = Array.ConvertAll(c, v => -v);
            Close(SolveOrThrow(q, neg), r.X, 1e-12, "x");
            Assert.Equal(0.0, r.Regularization);
        }

        [Fact]
        public void EqualityConstrainedProblemMatchesTheKktLinearSystem()
        {
            var rng = new System.Random(11);
            int n = 9, m = 3;
            double[,] q = RandomSpd(n, rng);
            var c = new double[n];
            var a = new double[m, n];
            var b = new double[m];
            for (int i = 0; i < n; i++) c[i] = rng.NextDouble() - 0.5;
            for (int e = 0; e < m; e++)
            {
                b[e] = rng.NextDouble();
                for (int k = 0; k < n; k++) a[e, k] = rng.NextDouble() - 0.5;
            }
            var p = new QuadraticProgram(q, c) { EqualityMatrix = a, EqualityVector = b };
            QuadraticProgramResult r = QuadraticProgramSolver.Solve(p);
            Assert.Equal(QuadraticProgramStatus.Optimal, r.Status);

            var kkt = new double[n + m, n + m];
            var rhs = new double[n + m];
            for (int i = 0; i < n; i++)
            {
                rhs[i] = -c[i];
                for (int k = 0; k < n; k++) kkt[i, k] = q[i, k];
            }
            for (int e = 0; e < m; e++)
            {
                rhs[n + e] = b[e];
                for (int k = 0; k < n; k++) { kkt[n + e, k] = a[e, k]; kkt[k, n + e] = a[e, k]; }
            }
            double[] sol = SolveOrThrow(kkt, rhs);
            Close(sol[..n], r.X, 1e-12, "x");
            Close(sol[n..], r.EqualityMultipliers, 1e-11, "y");
        }

        /// <summary>
        /// The example of R's quadprog::solve.QP (Turlach's port of Goldfarb-Idnani): min -(0,5,0)^T x + x^T x / 2
        /// s.t. -4x1 - 3x2 &gt;= -8, 2x1 + x2 &gt;= 2, -2x2 + x3 &gt;= 0. Exact solution (10, 22, 44) / 21, value -50/21,
        /// multipliers (0, 5/21, 44/21), constraints 2 and 3 active.
        /// </summary>
        [Fact]
        public void ReproducesTheQuadprogDocumentationExample()
        {
            var q = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            var p = new QuadraticProgram(q, new double[] { 0, -5, 0 })
            {
                InequalityMatrix = new double[,] { { 4, 3, 0 }, { -2, -1, 0 }, { 0, 2, -1 } },
                InequalityVector = new double[] { 8, -2, 0 },
            };
            QuadraticProgramResult r = QuadraticProgramSolver.Solve(p);
            Assert.Equal(QuadraticProgramStatus.Optimal, r.Status);
            Close(new[] { 10.0 / 21, 22.0 / 21, 44.0 / 21 }, r.X, 1e-14, "x");
            Close(new[] { 0.0, 5.0 / 21, 44.0 / 21 }, r.InequalityMultipliers, 1e-14, "z");
            Assert.Equal(-50.0 / 21, r.Objective, 14);
            Assert.Equal(new[] { 1, 2 }, r.ActiveInequalities);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void RandomConstrainedProblemsSatisfyKkt(int seed)
        {
            var rng = new System.Random(1000 + seed);
            int n = 5 + rng.Next(26);
            int m = rng.Next(3);
            int g = rng.Next(2 * n);
            double[,] q = RandomSpd(n, rng);
            var c = new double[n];
            var feasible = new double[n];
            for (int i = 0; i < n; i++)
            {
                c[i] = 4.0 * (rng.NextDouble() - 0.5);
                feasible[i] = rng.NextDouble() - 0.5;
            }
            var a = new double[m, n];
            var gm = new double[g, n];
            for (int e = 0; e < m; e++)
                for (int k = 0; k < n; k++) a[e, k] = rng.NextDouble() - 0.5;
            for (int e = 0; e < g; e++)
                for (int k = 0; k < n; k++) gm[e, k] = rng.NextDouble() - 0.5;
            double[] b = Multiply(a, feasible);
            double[] h = Multiply(gm, feasible);
            for (int e = 0; e < g; e++) h[e] += 0.3 * rng.NextDouble();
            var lo = new double[n];
            var hi = new double[n];
            for (int i = 0; i < n; i++)
            {
                lo[i] = rng.NextDouble() < 0.2 ? double.NegativeInfinity : feasible[i] - 0.4 * rng.NextDouble();
                hi[i] = rng.NextDouble() < 0.2 ? double.PositiveInfinity : feasible[i] + 0.4 * rng.NextDouble();
            }
            var p = new QuadraticProgram(q, c)
            {
                EqualityMatrix = m > 0 ? a : null,
                EqualityVector = m > 0 ? b : null,
                InequalityMatrix = g > 0 ? gm : null,
                InequalityVector = g > 0 ? h : null,
                LowerBounds = lo,
                UpperBounds = hi,
            };
            QuadraticProgramResult r = QuadraticProgramSolver.Solve(p);
            Assert.Equal(QuadraticProgramStatus.Optimal, r.Status);
            Residuals(p, r).AssertBelow(1e-9);
            // The problems are built so that the constraints matter.
            Assert.True(r.ActiveInequalities.Length + r.ActiveLowerBounds.Length + r.ActiveUpperBounds.Length > 0);
            // A feasible point never does better.
            Assert.True(p.Objective(feasible) >= r.Objective - 1e-12);
        }

        [Theory]
        [InlineData(21)]
        [InlineData(22)]
        [InlineData(23)]
        [InlineData(24)]
        public void MatchesBruteForceEnumerationOfActiveSets(int seed)
        {
            var rng = new System.Random(seed);
            int n = 5;
            double[,] q = RandomSpd(n, rng);
            var c = new double[n];
            for (int i = 0; i < n; i++) c[i] = 3.0 * (rng.NextDouble() - 0.5);
            var p = new QuadraticProgram(q, c)
            {
                EqualityMatrix = new double[,] { { 1, 1, 1, 1, 1 } },
                EqualityVector = new double[] { 1 },
                InequalityMatrix = new double[,] { { 1, 1, 0, 0, 0 }, { 0, 0, 1, -1, 2 } },
                InequalityVector = new double[] { 0.3, 0.4 },
                LowerBounds = new double[] { 0, 0, 0, -0.1, 0 },
                UpperBounds = new double[] { 0.5, 0.5, 0.4, 0.6, double.PositiveInfinity },
            };
            QuadraticProgramResult r = QuadraticProgramSolver.Solve(p);
            Assert.Equal(QuadraticProgramStatus.Optimal, r.Status);
            double[] reference = BruteForce(p, out double refObj);
            Close(reference, r.X, 1e-10, "x");
            Assert.Equal(refObj, r.Objective, 12);
            Residuals(p, r).AssertBelow(1e-12);
        }

        [Fact]
        public void ContradictoryBoundsAreInfeasible()
        {
            var p = new QuadraticProgram(new double[,] { { 1, 0 }, { 0, 1 } }, new double[2])
            {
                LowerBounds = new double[] { 0, 1 },
                UpperBounds = new double[] { 1, 0.5 },
            };
            Assert.Equal(QuadraticProgramStatus.Infeasible, QuadraticProgramSolver.Solve(p).Status);
        }

        [Fact]
        public void BoundsAgainstABudgetAreInfeasible()
        {
            // x_i in [0, 0.2], sum x = 1 with 3 variables.
            var p = new QuadraticProgram(new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }, new double[3])
            {
                EqualityMatrix = new double[,] { { 1, 1, 1 } },
                EqualityVector = new double[] { 1 },
                LowerBounds = new double[3],
                UpperBounds = new double[] { 0.2, 0.2, 0.2 },
            };
            Assert.Equal(QuadraticProgramStatus.Infeasible, QuadraticProgramSolver.Solve(p).Status);
        }

        [Fact]
        public void ContradictoryInequalitiesAreInfeasible()
        {
            // x1 + x2 >= 2 and x1 + x2 <= 1.
            var p = new QuadraticProgram(new double[,] { { 2, 0.5 }, { 0.5, 1 } }, new double[] { 1, -1 })
            {
                InequalityMatrix = new double[,] { { -1, -1 }, { 1, 1 } },
                InequalityVector = new double[] { -2, 1 },
            };
            Assert.Equal(QuadraticProgramStatus.Infeasible, QuadraticProgramSolver.Solve(p).Status);
        }

        [Fact]
        public void RedundantConsistentEqualityIsDroppedAndInconsistentOneIsInfeasible()
        {
            double[,] q = { { 2, 0.3, 0 }, { 0.3, 1, 0.1 }, { 0, 0.1, 1.5 } };
            var c = new double[] { -1, 0.5, 0.2 };
            var single = new QuadraticProgram(q, c) { EqualityMatrix = new double[,] { { 1, 1, 1 } }, EqualityVector = new double[] { 1 } };
            var doubled = single with { EqualityMatrix = new double[,] { { 1, 1, 1 }, { 2, 2, 2 } }, EqualityVector = new double[] { 1, 2 } };
            QuadraticProgramResult r1 = QuadraticProgramSolver.Solve(single), r2 = QuadraticProgramSolver.Solve(doubled);
            Assert.Equal(QuadraticProgramStatus.Optimal, r2.Status);
            Close(r1.X, r2.X, 1e-14, "x");
            Assert.Equal(0.0, r2.EqualityMultipliers[1]);
            Residuals(doubled, r2).AssertBelow(1e-13);

            var inconsistent = single with { EqualityMatrix = new double[,] { { 1, 1, 1 }, { 2, 2, 2 } }, EqualityVector = new double[] { 1, 3 } };
            Assert.Equal(QuadraticProgramStatus.Infeasible, QuadraticProgramSolver.Solve(inconsistent).Status);
        }

        [Fact]
        public void DegenerateVertexWithRedundantActiveConstraintsIsSolved()
        {
            // Minimum at the corner x = (1, 1) where three constraints meet: x1 <= 1, x2 <= 1, x1 + x2 <= 2.
            var p = new QuadraticProgram(new double[,] { { 1, 0 }, { 0, 1 } }, new double[] { -3, -3 })
            {
                InequalityMatrix = new double[,] { { 1, 1 }, { 1, 0 }, { 0, 1 }, { 2, 2 } },
                InequalityVector = new double[] { 2, 1, 1, 4 },
                UpperBounds = new double[] { 1, 1 },
            };
            QuadraticProgramResult r = QuadraticProgramSolver.Solve(p);
            Assert.Equal(QuadraticProgramStatus.Optimal, r.Status);
            Close(new[] { 1.0, 1.0 }, r.X, 1e-14, "x");
            Residuals(p, r).AssertBelow(1e-13);
        }

        [Fact]
        public void FixedVariableWithEqualBoundsIsHonoured()
        {
            var p = new QuadraticProgram(new double[,] { { 1, 0.2 }, { 0.2, 1 } }, new double[] { -1, -1 })
            {
                LowerBounds = new double[] { 0.25, double.NegativeInfinity },
                UpperBounds = new double[] { 0.25, double.PositiveInfinity },
            };
            QuadraticProgramResult r = QuadraticProgramSolver.Solve(p);
            Assert.Equal(QuadraticProgramStatus.Optimal, r.Status);
            Assert.Equal(0.25, r.X[0], 15);
            Assert.Equal(1.0 - 0.2 * 0.25, r.X[1], 14);
            Residuals(p, r).AssertBelow(1e-14);
        }

        [Fact]
        public void SemidefiniteHessianIsRidgedOrRefused()
        {
            // Rank-one Q: only the direction (1, 1) is penalised; the constraints pin the rest.
            var q = new double[,] { { 1, 1 }, { 1, 1 } };
            var p = new QuadraticProgram(q, new double[] { -1, 0 })
            {
                EqualityMatrix = new double[,] { { 1, 1 } },
                EqualityVector = new double[] { 1 },
                LowerBounds = new double[] { 0, 0 },
                UpperBounds = new double[] { 0.7, 1 },
            };
            QuadraticProgramResult r = QuadraticProgramSolver.Solve(p);
            Assert.Equal(QuadraticProgramStatus.Optimal, r.Status);
            Assert.True(r.Regularization > 0.0 && r.Regularization <= 1e-9);
            Close(new[] { 0.7, 0.3 }, r.X, 1e-12, "x");
            Residuals(p, r).AssertBelow(1e-12);

            QuadraticProgramResult refused = QuadraticProgramSolver.Solve(p, new QuadraticProgramOptions { RegularizationFloor = 0.0 });
            Assert.Equal(QuadraticProgramStatus.NotConvex, refused.Status);
            Assert.Equal(QuadraticProgramStatus.NotConvex, QuadraticProgramSolver.Solve(new QuadraticProgram(new double[2, 2], new double[2])).Status);
        }

        [Fact]
        public void IterationLimitIsReported()
        {
            var rng = new System.Random(5);
            int n = 20;
            var p = new QuadraticProgram(RandomSpd(n, rng), Array.ConvertAll(new double[n], _ => -1.0))
            {
                UpperBounds = Array.ConvertAll(new double[n], _ => 0.01),
            };
            QuadraticProgramResult r = QuadraticProgramSolver.Solve(p, new QuadraticProgramOptions { MaxIterations = 2 });
            Assert.Equal(QuadraticProgramStatus.MaxIterationsReached, r.Status);
        }

        [Fact]
        public void InvalidInputThrows()
        {
            var q = new double[,] { { 1, 0 }, { 0, 1 } };
            Assert.Throws<ArgumentException>(() => QuadraticProgramSolver.Solve(new QuadraticProgram(q, new double[3])));
            Assert.Throws<ArgumentException>(() => QuadraticProgramSolver.Solve(new QuadraticProgram(new double[,] { { 1, 2 }, { 0, 1 } }, new double[2])));
            Assert.Throws<ArgumentException>(() => QuadraticProgramSolver.Solve(new QuadraticProgram(q, new[] { double.NaN, 0 })));
            Assert.Throws<ArgumentException>(() => QuadraticProgramSolver.Solve(new QuadraticProgram(q, new double[2]) { EqualityMatrix = new double[1, 2] }));
            Assert.Throws<ArgumentException>(() => QuadraticProgramSolver.Solve(new QuadraticProgram(q, new double[2]) { LowerBounds = new double[3] }));
        }
    }
}
