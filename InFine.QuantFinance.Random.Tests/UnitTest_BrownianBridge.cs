using System;
using InFine.QuantFinance.Random.DotNet;
using Xunit;

namespace InFine.QuantFinance.Random.Tests
{
    public class UnitTest_BrownianBridge
    {
        // Non-uniform grid, not a power of two.
        private static readonly double[] Times = { 0.05, 0.1, 0.3, 0.35, 0.8, 1.0, 1.7, 2.5, 2.6, 4.0, 4.25 };

        // Columns of the linear map z -> W: column j is the path built from the unit vector e_j.
        private static double[,] BridgeMatrix(BrownianBridge bridge)
        {
            int n = bridge.Size;
            var a = new double[n, n];
            var z = new double[n];
            var w = new double[n];
            for (int j = 0; j < n; ++j)
            {
                Array.Clear(z);
                z[j] = 1.0;
                bridge.BuildPath(z, w);
                for (int i = 0; i < n; ++i) a[i, j] = w[i];
            }
            return a;
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.5)]
        public void Covariance_IsMinOfTimes_Exactly(double origin)
        {
            // W = A z with z ~ N(0, I): Cov(W) = A A^T must be min(t_i, t_j) - origin.
            var bridge = new BrownianBridge(Times, origin);
            double[,] a = BridgeMatrix(bridge);
            int n = Times.Length;
            for (int i = 0; i < n; ++i)
                for (int k = 0; k < n; ++k)
                {
                    double cov = 0.0;
                    for (int j = 0; j < n; ++j) cov += a[i, j] * a[k, j];
                    Assert.Equal(Math.Min(Times[i], Times[k]) - origin, cov, 12);
                }
        }

        [Fact]
        public void Covariance_IsMinOfTimes_OverManyDraws()
        {
            const int paths = 200_000;
            int n = Times.Length;
            var bridge = new BrownianBridge(Times);
            var gen = new PhiloxGenerator(20261011);
            var z = new double[n];
            var w = new double[n];
            var sum = new double[n, n];
            for (int p = 0; p < paths; ++p)
            {
                gen.GenerateGaussians(GaussianTransformEnum.InverseCumulativeDistributionFunction, 0.0, 1.0, n, z);
                bridge.BuildPath(z, w);
                for (int i = 0; i < n; ++i)
                    for (int k = 0; k <= i; ++k) sum[i, k] += w[i] * w[k];
            }
            for (int i = 0; i < n; ++i)
                for (int k = 0; k <= i; ++k)
                {
                    // E[W_i W_k] = c = min(t_i, t_k); Var(W_i W_k) = t_i t_k + c^2 for jointly Gaussian centred W.
                    double c = Math.Min(Times[i], Times[k]);
                    double sd = Math.Sqrt((Times[i] * Times[k] + c * c) / paths);
                    Assert.InRange(sum[i, k] / paths, c - 5 * sd, c + 5 * sd);
                }
        }

        [Fact]
        public void TerminalValue_IsExactlySqrtTTimesFirstGaussian()
        {
            var bridge = new BrownianBridge(Times);
            var gen = new PhiloxGenerator(99);
            var z = new double[Times.Length];
            var w = new double[Times.Length];
            double sqrtT = Math.Sqrt(Times[^1]);
            for (int p = 0; p < 100; ++p)
            {
                gen.GenerateGaussians(GaussianTransformEnum.InverseCumulativeDistributionFunction, 0.0, 1.0, z.Length, z);
                bridge.BuildPath(z, w);
                Assert.Equal(sqrtT * z[0], w[^1]);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(7)]
        [InlineData(16)]
        [InlineData(365)]
        public void Construction_FillsEveryIndexOnceAfterItsNeighbours(int n)
        {
            var times = new double[n];
            for (int i = 0; i < n; ++i) times[i] = (i + 1) * (i + 1) / 1000.0; // non-uniform
            var bridge = new BrownianBridge(times);
            var filled = new bool[n];
            Assert.Equal(n - 1, bridge.BridgeIndex[0]);
            filled[n - 1] = true;
            for (int j = 1; j < n; ++j)
            {
                int m = bridge.BridgeIndex[j], l = bridge.LeftIndex[j], r = bridge.RightIndex[j];
                Assert.False(filled[m]);
                Assert.True(l < m && m < r);
                Assert.True(l < 0 || filled[l]);
                Assert.True(filled[r]);
                filled[m] = true;
            }
            Assert.All(filled, Assert.True);
            // Breadth first: step 1 (if any) is the middle index of the whole grid.
            if (n > 1) Assert.Equal(n / 2 - 1, bridge.BridgeIndex[1]); // middle of the open interval (origin, n - 1)
        }

        [Fact]
        public void StandardIncrements_AreIndependentStandardNormals()
        {
            // Increments scaled by 1/sqrt(dt) are B = D A z: B B^T must be the identity.
            var bridge = new BrownianBridge(Times);
            int n = Times.Length;
            var b = new double[n, n];
            var z = new double[n];
            var w = new double[n];
            for (int j = 0; j < n; ++j)
            {
                Array.Clear(z);
                z[j] = 1.0;
                bridge.BuildPath(z, w);
                BrownianBridge.PathToStandardIncrements(n, bridge.InverseSqrtDt, w, 0, 1);
                for (int i = 0; i < n; ++i) b[i, j] = w[i];
            }
            for (int i = 0; i < n; ++i)
                for (int k = 0; k < n; ++k)
                {
                    double c = 0.0;
                    for (int j = 0; j < n; ++j) c += b[i, j] * b[k, j];
                    Assert.Equal(i == k ? 1.0 : 0.0, c, 12);
                }
        }

        [Fact]
        public void StridedLayouts_MatchContiguous()
        {
            // Simulation-major storage (simId + simCount * timeIndex), as used by the IBrownian arrays.
            const int sims = 5;
            int n = Times.Length;
            var bridge = new BrownianBridge(Times);
            var gen = new PhiloxGenerator(3);
            var zAll = new double[sims * n];
            gen.GenerateGaussians(GaussianTransformEnum.InverseCumulativeDistributionFunction, 0.0, 1.0, zAll.Length, zAll);
            var wAll = new double[sims * n];
            for (int s = 0; s < sims; ++s)
            {
                BrownianBridge.BuildPath(n, bridge.BridgeIndex, bridge.LeftIndex, bridge.RightIndex, bridge.LeftWeight,
                                         bridge.RightWeight, bridge.StdDev, zAll, s, sims, wAll, s, sims);
                BrownianBridge.PathToStandardIncrements(n, bridge.InverseSqrtDt, wAll, s, sims);
            }

            var z = new double[n];
            var w = new double[n];
            for (int s = 0; s < sims; ++s)
            {
                for (int j = 0; j < n; ++j) z[j] = zAll[s + sims * j];
                bridge.BuildPath(z, w);
                double previous = 0.0, previousT = 0.0;
                for (int i = 0; i < n; ++i)
                {
                    double expected = (w[i] - previous) / Math.Sqrt(Times[i] - previousT);
                    Assert.Equal(expected, wAll[s + sims * i], 13);
                    previous = w[i];
                    previousT = Times[i];
                }
            }
        }

        [Fact]
        public void MultiFactor_ReadsStepMajorAndWritesFactorMajor()
        {
            const int factors = 3;
            int n = Times.Length;
            var bridge = new BrownianBridge(Times);
            var z = new double[factors * n];
            new PhiloxGenerator(11).GenerateGaussians(GaussianTransformEnum.InverseCumulativeDistributionFunction, 0.0, 1.0, z.Length, z);
            var w = new double[factors * n];
            bridge.BuildPaths(factors, z, w);

            var zf = new double[n];
            var wf = new double[n];
            for (int f = 0; f < factors; ++f)
            {
                for (int j = 0; j < n; ++j) zf[j] = z[j * factors + f];
                bridge.BuildPath(zf, wf);
                for (int i = 0; i < n; ++i) Assert.Equal(wf[i], w[f * n + i]);
                // The first F Gaussians drive the F terminal values.
                Assert.Equal(Math.Sqrt(Times[^1]) * z[f], w[f * n + n - 1]);
            }
        }

        [Fact]
        public void InvalidTimes_Throw()
        {
            Assert.Throws<ArgumentException>(() => new BrownianBridge(Array.Empty<double>()));
            Assert.Throws<ArgumentException>(() => new BrownianBridge(new[] { 0.0, 1.0 }));
            Assert.Throws<ArgumentException>(() => new BrownianBridge(new[] { 0.5, 0.5 }));
            Assert.Throws<ArgumentException>(() => new BrownianBridge(new[] { 0.5, double.NaN }));
            Assert.Throws<ArgumentException>(() => new BrownianBridge(new[] { 0.5, double.PositiveInfinity }));
        }
    }
}
