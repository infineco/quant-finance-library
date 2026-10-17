using System;
using Xunit;
using static InFine.QuantFinance.Risk.Tests.TestHelpers;

namespace InFine.QuantFinance.Risk.Tests
{
    /// <summary>RiskMetrics EWMA covariance: hand values, the closed-form weights, the Python reference and streaming.</summary>
    public class EwmaCovarianceTests
    {
        [Fact]
        public void TwoObservationsByHand()
        {
            // S1 = r1 r1^T, S2 = 0.94 S1 + 0.06 r2 r2^T with r1 = (1, 2), r2 = (3, -1).
            var r = new double[,] { { 1, 2 }, { 3, -1 } };
            double[,] s = EwmaCovariance.Estimate(r);
            Assert.Equal(0.94 * 1 + 0.06 * 9, s[0, 0], 15);
            Assert.Equal(0.94 * 2 + 0.06 * -3, s[0, 1], 15);
            Assert.Equal(s[0, 1], s[1, 0]);
            Assert.Equal(0.94 * 4 + 0.06 * 1, s[1, 1], 15);
        }

        [Fact]
        public void MatchesPythonReferenceForBothInitialisations()
        {
            Close(ReferenceData.Ewma094, EwmaCovariance.Estimate(ReferenceData.Returns), 1e-13, "first outer product");
            Close(ReferenceData.Ewma090Seed5, EwmaCovariance.Estimate(ReferenceData.Returns, 0.90, seedWindow: 5), 1e-13, "seed window 5");
        }

        [Theory]
        [InlineData(0.94, 0)]
        [InlineData(0.97, 0)]
        [InlineData(0.94, 20)]
        public void BatchEqualsTheClosedFormWeightedSum(double lambda, int seedWindow)
        {
            const int t = 120, n = 4;
            double[,] x = RandomReturns(t, n, 3);
            double[,] actual = EwmaCovariance.Estimate(x, lambda, seedWindow);

            // S_T = lambda^(T-k) S_k + (1 - lambda) sum_{s>k} lambda^(T-s) r_s r_s^T, S_k the seed (or the first outer product, k = 1).
            int k = seedWindow == 0 ? 1 : seedWindow;
            var expected = new double[n, n];
            for (int s = 0; s < k; s++)
                for (int a = 0; a < n; a++)
                    for (int b = 0; b < n; b++)
                        expected[a, b] += x[s, a] * x[s, b] / k;
            double w0 = Math.Pow(lambda, t - k);
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++) expected[a, b] *= w0;
            for (int s = k; s < t; s++)
            {
                double w = (1 - lambda) * Math.Pow(lambda, t - 1 - s);
                for (int a = 0; a < n; a++)
                    for (int b = 0; b < n; b++) expected[a, b] += w * x[s, a] * x[s, b];
            }
            Close(expected, actual, 1e-13, "closed form");
        }

        [Fact]
        public void StreamingUpdatesEqualTheBatchAtEveryDate()
        {
            const int t = 60, n = 3;
            double[,] x = RandomReturns(t, n, 11);
            var stream = new EwmaCovariance(n);
            Assert.False(stream.IsInitialised);
            for (int i = 0; i < t; i++)
            {
                stream.Update(Row(x, i));
                var head = new double[i + 1, n];
                for (int a = 0; a <= i; a++)
                    for (int b = 0; b < n; b++) head[a, b] = x[a, b];
                Close(EwmaCovariance.Estimate(head), stream.GetCovariance(), 1e-13, $"date {i}");
            }
            Assert.Equal(t, stream.Count);
        }

        [Fact]
        public void SeededStreamingEqualsSeededBatch()
        {
            double[,] x = RandomReturns(80, 3, 5);
            double[,] batch = EwmaCovariance.Estimate(x, 0.94, seedWindow: 10);

            var head = new double[10, 3];
            for (int i = 0; i < 10; i++)
                for (int j = 0; j < 3; j++) head[i, j] = x[i, j];
            var stream = new EwmaCovariance(3);
            stream.Initialize(ZeroMeanMoment(head));
            for (int i = 10; i < 80; i++) stream.Update(Row(x, i));
            Close(batch, stream.GetCovariance(), 1e-14, "seeded stream");
            Assert.Equal(70, stream.Count);
        }

        static double[,] ZeroMeanMoment(double[,] x)
        {
            int t = x.GetLength(0), n = x.GetLength(1);
            var s = new double[n, n];
            for (int i = 0; i < t; i++)
                for (int a = 0; a < n; a++)
                    for (int b = 0; b < n; b++) s[a, b] += x[i, a] * x[i, b] / t;
            return s;
        }

        [Fact]
        public void ConstantReturnsKeepTheOuterProductAndTheEstimateIsPositiveSemidefinite()
        {
            var r = new double[50, 2];
            for (int i = 0; i < 50; i++) { r[i, 0] = 0.01; r[i, 1] = -0.02; }
            double[,] s = EwmaCovariance.Estimate(r);
            Assert.Equal(1e-4, s[0, 0], 1e-18);
            Assert.Equal(-2e-4, s[0, 1], 1e-18);
            Assert.Equal(4e-4, s[1, 1], 1e-18);

            double[,] g = EwmaCovariance.Estimate(RandomReturns(200, 6, 2), 0.94, 30);
            Assert.True(MinEigenvalue(g) >= -1e-18);
            Assert.Equal(0.0, InFine.QuantFinance.Numerics.Matrix.MaxAsymmetry(g));
        }

        [Fact]
        public void LongRunEwmaConvergesToTheTrueVarianceOfIidData()
        {
            // For iid zero-mean data E[S_t] = variance; the estimator's own std is about sigma^2 sqrt((1-l)/(1+l)) ~ 0.18 sigma^2.
            double[,] x = RandomReturns(5000, 1, 4, scale: 1.0);
            double sigma2 = Covariance.Sample(x)[0, 0];
            double ewma = EwmaCovariance.Estimate(x)[0, 0];
            Assert.InRange(ewma / sigma2, 0.4, 1.8);
        }

        [Fact]
        public void InvalidUsageIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new EwmaCovariance(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EwmaCovariance(2, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EwmaCovariance(2, 0.0));
            var e = new EwmaCovariance(2);
            Assert.Throws<InvalidOperationException>(() => e.GetCovariance());
            Assert.Throws<ArgumentException>(() => e.Update(new[] { 1.0 }));
            Assert.Throws<ArgumentException>(() => e.Update(new[] { 1.0, double.NaN }));
            Assert.Throws<ArgumentException>(() => e.Initialize(new double[3, 3]));
            Assert.Throws<ArgumentException>(() => e.Initialize(new double[,] { { -1, 0 }, { 0, 1 } }));
            Assert.Throws<ArgumentOutOfRangeException>(() => EwmaCovariance.Estimate(new double[3, 2], 0.94, 4));
            Assert.Equal(0.94, EwmaCovariance.DefaultLambda);
        }
    }
}
