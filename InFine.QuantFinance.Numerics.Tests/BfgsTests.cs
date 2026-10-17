using System;
using InFine.QuantFinance.Numerics;
using Xunit;

namespace InFine.QuantFinance.Numerics.Tests
{
    /// <summary>BFGS and finite differences against functions with known minimisers, derivatives and Hessians.</summary>
    public class BfgsTests
    {
        static double Rosenbrock(double[] x) => 100.0 * Math.Pow(x[1] - x[0] * x[0], 2) + Math.Pow(1.0 - x[0], 2);

        static void RosenbrockGradient(double[] x, double[] g)
        {
            g[0] = -400.0 * x[0] * (x[1] - x[0] * x[0]) - 2.0 * (1.0 - x[0]);
            g[1] = 200.0 * (x[1] - x[0] * x[0]);
        }

        [Fact]
        public void RosenbrockWithAnalyticGradientReachesTheMinimum()
        {
            var r = Bfgs.Minimize(Rosenbrock, new[] { -1.2, 1.0 }, gradient: RosenbrockGradient);
            Assert.True(r.Converged, r.Status.ToString());
            Assert.Equal(1.0, r.X[0], 7);
            Assert.Equal(1.0, r.X[1], 7);
            Assert.True(r.Value < 1e-13);
            Assert.True(r.Iterations < 60, $"iterations: {r.Iterations}");
        }

        [Fact]
        public void RosenbrockWithFiniteDifferenceGradientReachesTheMinimum()
        {
            var r = Bfgs.Minimize(Rosenbrock, new[] { -1.2, 1.0 });
            Assert.True(r.Converged, r.Status.ToString());
            Assert.Equal(1.0, r.X[0], 5);
            Assert.Equal(1.0, r.X[1], 5);
        }

        [Fact]
        public void IllConditionedQuadraticConvergesToTheExactMinimiser()
        {
            // f = 1/2 sum d_i (x_i - c_i)^2 with condition number 1e6.
            double[] d = { 1.0, 1e3, 1e6, 10.0 };
            double[] c = { 3.0, -2.0, 0.5, 7.0 };
            double F(double[] x) { double s = 0; for (int i = 0; i < 4; i++) s += 0.5 * d[i] * (x[i] - c[i]) * (x[i] - c[i]); return s; }
            void G(double[] x, double[] g) { for (int i = 0; i < 4; i++) g[i] = d[i] * (x[i] - c[i]); }
            var r = Bfgs.Minimize(F, new[] { 0.0, 0.0, 0.0, 0.0 }, gradient: G);
            Assert.True(r.Converged, r.Status.ToString());
            for (int i = 0; i < 4; i++) Assert.Equal(c[i], r.X[i], 6);
        }

        [Fact]
        public void InverseHessianApproximatesTheTrueOneOnAQuadratic()
        {
            double[,] a = { { 4.0, 1.0 }, { 1.0, 3.0 } };
            double F(double[] x) => 0.5 * (a[0, 0] * x[0] * x[0] + 2 * a[0, 1] * x[0] * x[1] + a[1, 1] * x[1] * x[1]) - x[0] - 2.0 * x[1];
            void G(double[] x, double[] g) { g[0] = a[0, 0] * x[0] + a[0, 1] * x[1] - 1.0; g[1] = a[0, 1] * x[0] + a[1, 1] * x[1] - 2.0; }
            var r = Bfgs.Minimize(F, new[] { 5.0, -5.0 }, gradient: G);
            double det = a[0, 0] * a[1, 1] - a[0, 1] * a[0, 1];
            Assert.Equal(a[1, 1] / det, r.InverseHessian[0, 0], 4);
            Assert.Equal(-a[0, 1] / det, r.InverseHessian[0, 1], 4);
            Assert.Equal(a[0, 0] / det, r.InverseHessian[1, 1], 4);
        }

        [Fact]
        public void NonFiniteRegionsAreBacktrackedAwayFrom()
        {
            // f = x - ln x defined for x > 0 only; minimum at x = 1. A first step of -g would leave the domain.
            double F(double[] x) => x[0] > 0.0 ? x[0] - Math.Log(x[0]) : double.NaN;
            var r = Bfgs.Minimize(F, new[] { 0.05 });
            Assert.True(r.Converged, r.Status.ToString());
            Assert.Equal(1.0, r.X[0], 6);
        }

        [Fact]
        public void StartingPointMustBeFinite()
        {
            Assert.Throws<ArgumentException>(() => Bfgs.Minimize(_ => double.NaN, new[] { 0.0 }));
            Assert.Throws<ArgumentException>(() => Bfgs.Minimize(x => x[0], Array.Empty<double>()));
        }

        [Fact]
        public void FiniteDifferenceGradientAndHessianMatchAnalyticDerivatives()
        {
            double F(double[] x) => Math.Exp(x[0]) * Math.Sin(x[1]) + x[0] * x[0] * x[1] + 1e-9 * Math.Pow(x[2], 3);
            double[] x0 = { 0.3, 1.1, 2.0e3 };
            var g = FiniteDifferences.Gradient(F, x0, 1e-5);
            Assert.Equal(Math.Exp(0.3) * Math.Sin(1.1) + 2 * 0.3 * 1.1, g[0], 6);
            Assert.Equal(Math.Exp(0.3) * Math.Cos(1.1) + 0.09, g[1], 6);
            Assert.Equal(3e-9 * 4e6, g[2], 5);

            var h = FiniteDifferences.Hessian(F, x0, 1e-4);
            Assert.Equal(Math.Exp(0.3) * Math.Sin(1.1) + 2 * 1.1, h[0, 0], 4);
            Assert.Equal(-Math.Exp(0.3) * Math.Sin(1.1), h[1, 1], 4);
            Assert.Equal(6e-9 * 2000.0, h[2, 2], 5);
            Assert.Equal(Math.Exp(0.3) * Math.Cos(1.1) + 2 * 0.3, h[0, 1], 5);
            Assert.Equal(h[0, 1], h[1, 0]);
            Assert.Equal(0.0, h[0, 2], 6);
        }
    }
}
