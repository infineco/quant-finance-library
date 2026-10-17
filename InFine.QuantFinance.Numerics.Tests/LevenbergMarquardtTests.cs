using System;
using InFine.QuantFinance.Numerics;
using Xunit;

namespace InFine.QuantFinance.Numerics.Tests
{
    /// <summary>
    /// Levenberg-Marquardt against problems with known solutions: Rosenbrock, NIST StRD certified regressions
    /// (Misra1a, lower difficulty; BoxBOD, higher difficulty), an exact exponential fit, and bounded problems whose
    /// solution sits on a bound.
    /// </summary>
    public class LevenbergMarquardtTests
    {
        static void Close(double expected, double actual, double rel, double abs, string what)
        {
            Assert.True(Math.Abs(expected - actual) <= abs + rel * Math.Abs(expected),
                $"{what}: expected {expected:R}, got {actual:R}");
        }

        // ---- Rosenbrock: r = (10 (p1 - p0^2), 1 - p0), minimum 0 at (1, 1) --------------------------------------

        static void RosenbrockResiduals(double[] p, double[] r)
        {
            r[0] = 10.0 * (p[1] - p[0] * p[0]);
            r[1] = 1.0 - p[0];
        }

        static void RosenbrockJacobian(double[] p, double[,] j)
        {
            j[0, 0] = -20.0 * p[0]; j[0, 1] = 10.0;
            j[1, 0] = -1.0; j[1, 1] = 0.0;
        }

        [Theory]
        [InlineData("analytic")]
        [InlineData("forward")]
        [InlineData("central")]
        public void RosenbrockFromTheStandardStart(string jacobian)
        {
            var options = new LevenbergMarquardtOptions
            {
                FiniteDifference = jacobian == "central" ? FiniteDifferenceScheme.Central : FiniteDifferenceScheme.Forward,
            };
            var result = LevenbergMarquardt.Minimize(RosenbrockResiduals, new[] { -1.2, 1.0 }, 2,
                jacobian == "analytic" ? RosenbrockJacobian : null, options);
            Assert.True(result.Converged, result.Status.ToString());
            Close(1.0, result.Parameters[0], 0, 1e-10, "p0");
            Close(1.0, result.Parameters[1], 0, 1e-10, "p1");
            Assert.True(result.Cost < 1e-20, $"cost {result.Cost}");
            Assert.True(result.Iterations < 50, $"{result.Iterations} iterations");
            // Jacobian at the solution: [[-20, 10], [-1, 0]]
            Close(-20.0, result.Jacobian[0, 0], 0, jacobian == "analytic" ? 1e-9 : 1e-6, "J00");
            Close(10.0, result.Jacobian[0, 1], 0, 1e-6, "J01");
            Close(-1.0, result.Jacobian[1, 0], 0, 1e-6, "J10");
        }

        // ---- NIST StRD -------------------------------------------------------------------------------------------

        // Misra1a, https://www.itl.nist.gov/div898/strd/nls/data/misra1a.shtml (file Misra1a.dat, SHA-256
        // 9d3194d5...130b98e2): y = b1 (1 - exp(-b2 x)), 14 observations.
        static readonly double[] MisraY = { 10.07, 14.73, 17.94, 23.93, 29.61, 35.18, 40.02, 44.82, 50.76, 55.05, 61.01, 66.40, 75.47, 81.78 };
        static readonly double[] MisraX = { 77.6, 114.9, 141.1, 190.8, 239.9, 289.0, 332.8, 378.4, 434.8, 477.3, 536.8, 593.1, 689.1, 760.0 };

        // BoxBOD, https://www.itl.nist.gov/div898/strd/nls/data/boxbod.shtml (file BoxBOD.dat, SHA-256
        // fc44fbed...ecd77e21): same model, 6 observations, higher difficulty.
        static readonly double[] BoxY = { 109, 149, 149, 191, 213, 224 };
        static readonly double[] BoxX = { 1, 2, 3, 5, 7, 10 };

        static ResidualFunction ExponentialRise(double[] x, double[] y) => (b, r) =>
        {
            for (int i = 0; i < x.Length; i++) r[i] = y[i] - b[0] * (1.0 - Math.Exp(-b[1] * x[i]));
        };

        static JacobianFunction ExponentialRiseJacobian(double[] x) => (b, j) =>
        {
            for (int i = 0; i < x.Length; i++)
            {
                double e = Math.Exp(-b[1] * x[i]);
                j[i, 0] = -(1.0 - e);
                j[i, 1] = -b[0] * x[i] * e;
            }
        };

        /// <summary>Standard errors sqrt(diag(s^2 (J^T J)^-1)), s^2 = RSS / (m - 2), for a two-parameter fit.</summary>
        static (double, double) StandardErrors(LevenbergMarquardtResult result)
        {
            var j = result.Jacobian;
            int m = j.GetLength(0);
            double a = 0, b = 0, c = 0;
            for (int i = 0; i < m; i++)
            {
                a += j[i, 0] * j[i, 0];
                b += j[i, 0] * j[i, 1];
                c += j[i, 1] * j[i, 1];
            }
            double det = a * c - b * b, s2 = 2.0 * result.Cost / (m - 2);
            return (Math.Sqrt(s2 * c / det), Math.Sqrt(s2 * a / det));
        }

        [Theory]
        [InlineData(500.0, 1e-4, true)]
        [InlineData(250.0, 5e-4, true)]
        [InlineData(500.0, 1e-4, false)]
        [InlineData(250.0, 5e-4, false)]
        public void Misra1aMatchesNistCertifiedValues(double b1, double b2, bool analytic)
        {
            var result = LevenbergMarquardt.Minimize(ExponentialRise(MisraX, MisraY), new[] { b1, b2 }, MisraX.Length,
                analytic ? ExponentialRiseJacobian(MisraX) : null,
                new LevenbergMarquardtOptions { FiniteDifference = FiniteDifferenceScheme.Central });
            Assert.True(result.Converged, result.Status.ToString());
            // Certified to 11 significant digits.
            Close(2.3894212918E+02, result.Parameters[0], 1e-9, 0, "b1");
            Close(5.5015643181E-04, result.Parameters[1], 1e-9, 0, "b2");
            Close(1.2455138894E-01, 2.0 * result.Cost, 1e-9, 0, "residual sum of squares");
            var (se1, se2) = StandardErrors(result);
            Close(2.7070075241E+00, se1, 1e-6, 0, "sd(b1)");
            Close(7.2668688436E-06, se2, 1e-6, 0, "sd(b2)");
        }

        [Theory]
        [InlineData(1.0, 1.0)]
        [InlineData(100.0, 0.75)]
        public void BoxBodMatchesNistCertifiedValues(double b1, double b2)
        {
            var result = LevenbergMarquardt.Minimize(ExponentialRise(BoxX, BoxY), new[] { b1, b2 }, BoxX.Length,
                ExponentialRiseJacobian(BoxX));
            Assert.True(result.Converged, result.Status.ToString());
            Close(2.1380940889E+02, result.Parameters[0], 1e-9, 0, $"b1 ({result.Status}, {result.Iterations} iterations, cost {result.Cost:R})");
            Close(5.4723748542E-01, result.Parameters[1], 1e-9, 0, "b2");
            Close(1.1680088766E+03, 2.0 * result.Cost, 1e-9, 0, "residual sum of squares");
            var (se1, se2) = StandardErrors(result);
            Close(1.2354515176E+01, se1, 1e-6, 0, "sd(b1)");
            Close(1.0455993237E-01, se2, 1e-6, 0, "sd(b2)");
        }

        // ---- exponential fit with an exact solution --------------------------------------------------------------

        [Fact]
        public void ExponentialDecayIsFittedExactly()
        {
            // y(t) = a exp(-k t) + c sampled without noise: the fit must recover (a, k, c) to near machine precision.
            const double a = 2.5, k = 1.3, c = 0.4;
            var t = new double[30];
            var y = new double[30];
            for (int i = 0; i < t.Length; i++)
            {
                t[i] = 0.1 * i;
                y[i] = a * Math.Exp(-k * t[i]) + c;
            }
            void Residuals(double[] p, double[] r)
            {
                for (int i = 0; i < t.Length; i++) r[i] = p[0] * Math.Exp(-p[1] * t[i]) + p[2] - y[i];
            }
            foreach (var scheme in new[] { FiniteDifferenceScheme.Forward, FiniteDifferenceScheme.Central })
            {
                var result = LevenbergMarquardt.Minimize(Residuals, new[] { 1.0, 0.5, 0.0 }, t.Length, null,
                    new LevenbergMarquardtOptions { FiniteDifference = scheme });
                Assert.True(result.Converged, result.Status.ToString());
                Close(a, result.Parameters[0], 1e-10, 0, $"{scheme} a");
                Close(k, result.Parameters[1], 1e-10, 0, $"{scheme} k");
                Close(c, result.Parameters[2], 1e-10, 0, $"{scheme} c");
                Assert.True(result.Cost < 1e-25, $"{scheme} cost {result.Cost}");
            }
        }

        [Fact]
        public void ScalingMakesTheIterationInvariantToParameterUnits()
        {
            // Misra1a with b2 expressed in units of 1e-4: same fit, same number of iterations.
            ResidualFunction scaled = (b, r) =>
            {
                for (int i = 0; i < MisraX.Length; i++) r[i] = MisraY[i] - b[0] * (1.0 - Math.Exp(-1e-4 * b[1] * MisraX[i]));
            };
            var plain = LevenbergMarquardt.Minimize(ExponentialRise(MisraX, MisraY), new[] { 500.0, 1e-4 }, MisraX.Length, ExponentialRiseJacobian(MisraX));
            var rescaled = LevenbergMarquardt.Minimize(scaled, new[] { 500.0, 1.0 }, MisraX.Length);
            Close(plain.Parameters[1], 1e-4 * rescaled.Parameters[1], 1e-8, 0, "b2");
            Assert.InRange(rescaled.Iterations, plain.Iterations - 2, plain.Iterations + 2);
        }

        // ---- bounds --------------------------------------------------------------------------------------------

        [Fact]
        public void RosenbrockWithAnActiveUpperBound()
        {
            // p0 <= 0.5 excludes the minimum (1, 1); the constrained minimum is p0 = 0.5, p1 = 0.25, where
            // 100 (p1 - p0^2)^2 + (1 - p0)^2 is still decreasing in p0.
            var options = new LevenbergMarquardtOptions
            {
                LowerBounds = new[] { -2.0, double.NegativeInfinity },
                UpperBounds = new[] { 0.5, double.PositiveInfinity },
            };
            foreach (var analytic in new[] { true, false })
            {
                var result = LevenbergMarquardt.Minimize(RosenbrockResiduals, new[] { -1.2, 1.0 }, 2,
                    analytic ? RosenbrockJacobian : null, options);
                Assert.True(result.Converged, result.Status.ToString());
                Close(0.5, result.Parameters[0], 0, 1e-8, "p0 at the bound");
                Close(0.25, result.Parameters[1], 0, 1e-8, "p1");
                Assert.True(result.Parameters[0] <= 0.5);
                Close(0.125, result.Cost, 1e-7, 0, "cost = (1 - 0.5)^2 / 2");
            }
        }

        [Fact]
        public void OneSidedBoundsAreHonoured()
        {
            // r = (p0 + 1, p1 - 2, p2 - 3): unconstrained minimum (-1, 2, 3). With p0 >= 0 and p2 <= 1 the solution
            // is (0, 2, 1); every evaluated point must be feasible.
            void Residuals(double[] p, double[] r)
            {
                Assert.True(p[0] >= 0.0 && p[2] <= 1.0, $"infeasible point ({p[0]}, {p[2]})");
                r[0] = p[0] + 1.0;
                r[1] = p[1] - 2.0;
                r[2] = p[2] - 3.0;
            }
            var options = new LevenbergMarquardtOptions
            {
                LowerBounds = new[] { 0.0, double.NegativeInfinity, double.NegativeInfinity },
                UpperBounds = new[] { double.PositiveInfinity, double.PositiveInfinity, 1.0 },
            };
            // the starting point is on the bound of p0 and outside the bound of p2: both are moved inside
            var result = LevenbergMarquardt.Minimize(Residuals, new[] { 0.0, 0.0, 5.0 }, 3, null, options);
            Assert.True(result.Converged, result.Status.ToString());
            Close(0.0, result.Parameters[0], 0, 1e-8, "p0");
            Close(2.0, result.Parameters[1], 0, 1e-10, "p1");
            Close(1.0, result.Parameters[2], 0, 1e-8, "p2");
        }

        [Fact]
        public void InactiveBoundsDoNotChangeTheSolution()
        {
            var options = new LevenbergMarquardtOptions
            {
                LowerBounds = new[] { 0.0, 0.0 },
                UpperBounds = new[] { 1000.0, 0.01 },
            };
            var result = LevenbergMarquardt.Minimize(ExponentialRise(MisraX, MisraY), new[] { 500.0, 1e-4 }, MisraX.Length,
                ExponentialRiseJacobian(MisraX), options);
            Assert.True(result.Converged, result.Status.ToString());
            Close(2.3894212918E+02, result.Parameters[0], 1e-9, 0, "b1");
            Close(5.5015643181E-04, result.Parameters[1], 1e-9, 0, "b2");
        }

        // ---- robustness and argument checking -------------------------------------------------------------------

        [Fact]
        public void NonFiniteTrialPointsShrinkTheStep()
        {
            // sqrt is NaN for p < 0: the model is undefined there, and the minimum (p = 0.01) is close to the edge.
            void Residuals(double[] p, double[] r)
            {
                r[0] = Math.Sqrt(p[0]) - 0.1;
                r[1] = 0.0;
            }
            var result = LevenbergMarquardt.Minimize(Residuals, new[] { 4.0 }, 2);
            Assert.True(result.Converged, result.Status.ToString());
            Close(0.01, result.Parameters[0], 1e-8, 0, "p");
        }

        [Fact]
        public void IterationLimitIsReported()
        {
            var result = LevenbergMarquardt.Minimize(RosenbrockResiduals, new[] { -1.2, 1.0 }, 2, RosenbrockJacobian,
                new LevenbergMarquardtOptions { MaxIterations = 2 });
            Assert.Equal(LevenbergMarquardtStatus.MaxIterations, result.Status);
            Assert.False(result.Converged);
            Assert.Equal(2, result.Iterations);
            var evaluations = LevenbergMarquardt.Minimize(RosenbrockResiduals, new[] { -1.2, 1.0 }, 2, null,
                new LevenbergMarquardtOptions { MaxFunctionEvaluations = 5 });
            Assert.Equal(LevenbergMarquardtStatus.MaxFunctionEvaluations, evaluations.Status);
        }

        [Fact]
        public void InvalidArgumentsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => LevenbergMarquardt.Minimize(RosenbrockResiduals, new[] { 1.0, 1.0 }, 1));
            Assert.Throws<ArgumentException>(() => LevenbergMarquardt.Minimize(RosenbrockResiduals, Array.Empty<double>(), 2));
            Assert.Throws<ArgumentException>(() => LevenbergMarquardt.Minimize(RosenbrockResiduals, new[] { double.NaN, 1.0 }, 2));
            Assert.Throws<ArgumentException>(() => LevenbergMarquardt.Minimize((p, r) => { r[0] = double.NaN; r[1] = 0; }, new[] { 1.0, 1.0 }, 2));
            Assert.Throws<ArgumentException>(() => LevenbergMarquardt.Minimize(RosenbrockResiduals, new[] { 1.0, 1.0 }, 2, null,
                new LevenbergMarquardtOptions { LowerBounds = new[] { 2.0, 0.0 }, UpperBounds = new[] { 1.0, 3.0 } }));
            Assert.Throws<ArgumentException>(() => LevenbergMarquardt.Minimize(RosenbrockResiduals, new[] { 1.0, 1.0 }, 2, null,
                new LevenbergMarquardtOptions { LowerBounds = new[] { 0.0 } }));
            Assert.Throws<ArgumentException>(() => LevenbergMarquardt.Minimize(RosenbrockResiduals, new[] { 1.0, 1.0 }, 2, null,
                new LevenbergMarquardtOptions { FunctionTolerance = -1 }));
            Assert.Throws<ArgumentException>(() => LevenbergMarquardt.Minimize(RosenbrockResiduals, new[] { 1.0, 1.0 }, 2, null,
                new LevenbergMarquardtOptions { ParameterScale = new[] { 1.0, 0.0 } }));
            Assert.Throws<ArgumentNullException>(() => LevenbergMarquardt.Minimize(null!, new[] { 1.0 }, 1));
        }

        [Fact]
        public void InitialGuessIsNotModified()
        {
            var guess = new[] { -1.2, 1.0 };
            LevenbergMarquardt.Minimize(RosenbrockResiduals, guess, 2);
            Assert.Equal(new[] { -1.2, 1.0 }, guess);
        }
    }
}
