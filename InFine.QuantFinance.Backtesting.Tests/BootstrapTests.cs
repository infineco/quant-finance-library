using System;
using InFine.QuantFinance.Backtesting;
using InFine.QuantFinance.TimeSeries;
using Xunit;

namespace InFine.QuantFinance.Backtesting.Tests
{
    public class BootstrapTests
    {
        static readonly BacktestOptions Costs = new BacktestOptions { ProportionalCostBps = 10.0, FixedCostPerTrade = 1e-5 };

        [Fact]
        public void KernelResultsAreBitIdenticalForAnyChunking()
        {
            var panel = TestData.Panel(TestData.Returns(300, 3, seed: 11), cash: Cash(300));
            double[] w = { 0.5, 0.3, 0.3 };
            BootstrapResult Run(int chunk, bool parallel) => StationaryBootstrap.RunConstantMix(panel, w, 5, Costs,
                new BootstrapOptions { PathCount = 257, MeanBlockLength = 10, Seed = 7, ChunkSize = chunk, Parallel = parallel });

            BootstrapResult reference = Run(1, false);
            foreach ((int chunk, bool parallel) in new[] { (1, true), (7, true), (64, true), (1000, true), (13, false) })
                AssertIdentical(reference, Run(chunk, parallel));
        }

        [Fact]
        public void HostEngineResultsAreBitIdenticalForAnyChunking()
        {
            var panel = TestData.Panel(TestData.Returns(150, 3, seed: 12));
            var strategy = new EqualRiskContributionStrategy(CovarianceEstimator.LedoitWolf(40));
            var backtest = Costs with { Schedule = RebalanceSchedule.Every(10) };
            BootstrapResult Run(int chunk, bool parallel) => StationaryBootstrap.Run(panel, strategy, backtest,
                new BootstrapOptions { PathCount = 24, MeanBlockLength = 5, ChunkSize = chunk, Parallel = parallel });

            BootstrapResult reference = Run(24, false);
            AssertIdentical(reference, Run(1, true));
            AssertIdentical(reference, Run(5, true));
            foreach (double s in reference.SharpeRatio) Assert.True(double.IsFinite(s));
        }

        [Fact]
        public void KernelConstantMixReproducesTheHostEngineOnEveryPath()
        {
            var panel = TestData.Panel(TestData.Returns(250, 3, seed: 13), cash: Cash(250));
            double[] w = { 0.6, 0.5, -0.2 };
            var options = new BootstrapOptions { PathCount = 64, MeanBlockLength = 8, Seed = 99, RiskFreeRate = 0.02 };
            BootstrapResult kernel = StationaryBootstrap.RunConstantMix(panel, w, 7, Costs, options);
            BootstrapResult host = StationaryBootstrap.Run(panel, new ConstantMixStrategy(w),
                Costs with { Schedule = RebalanceSchedule.Every(7) }, options);

            // Same rows, same accounting steps in the same order: wealth, drawdown, turnover and costs agree exactly.
            Assert.Equal(host.TerminalWealth, kernel.TerminalWealth);
            Assert.Equal(host.MaxDrawdown, kernel.MaxDrawdown);
            Assert.Equal(host.Turnover, kernel.Turnover);
            Assert.Equal(host.Costs, kernel.Costs);
            // Moments: one-pass Welford (kernel) against two-pass PerformanceStatistics (host).
            for (int p = 0; p < 64; p++)
            {
                Assert.Equal(host.MeanReturn[p], kernel.MeanReturn[p], 1e-15);
                Assert.Equal(host.Volatility[p], kernel.Volatility[p], 1e-12);
                Assert.Equal(host.SharpeRatio[p], kernel.SharpeRatio[p], 1e-10);
                Assert.Equal(host.AnnualisedReturn[p], kernel.AnnualisedReturn[p], 1e-12);
            }
        }

        [Fact]
        public void KernelPathMatchesBacktestOfTheMaterialisedHistory()
        {
            var panel = TestData.Panel(TestData.Returns(120, 2, seed: 14));
            var options = new BootstrapOptions { PathCount = 5, MeanBlockLength = 4, Seed = 3 };
            BootstrapResult kernel = StationaryBootstrap.RunConstantMix(panel, new[] { 0.7, 0.3 }, 1, null, options);
            ReturnPanel history = StationaryBootstrap.Resample(panel, 4, options);
            BacktestResult run = BacktestEngine.Run(history, new ConstantMixStrategy(new[] { 0.7, 0.3 }));
            Assert.Equal(run.TerminalWealth, kernel.TerminalWealth[4]);
            Assert.Equal(PerformanceStatistics.SharpeRatio(run.Returns, Periodicity.Daily), kernel.SharpeRatio[4], 1e-10);
        }

        [Fact]
        public void BlockLengthOneIsTheIidBootstrap()
        {
            // Strongly autocorrelated source: AR(1) with phi = 0.7.
            const int T = 400, M = 3000;
            double[,] source = TestData.Ar1(T, 0.7, seed: 15);
            var panel = TestData.Panel(source);
            double sampleMean = 0.0, sampleVar = 0.0;
            for (int t = 0; t < T; t++) sampleMean += source[t, 0];
            sampleMean /= T;
            for (int t = 0; t < T; t++) sampleVar += (source[t, 0] - sampleMean) * (source[t, 0] - sampleMean);
            sampleVar /= T;
            double sourceAcf = Lag1Autocorrelation(source, Identity(T));
            Assert.True(sourceAcf > 0.6);

            // Mean of resampled means = sample mean: every resampled row is uniform on the source rows. The kernel path
            // (single asset fully invested, no costs) returns the asset's own returns.
            BootstrapResult iid = StationaryBootstrap.RunConstantMix(panel, new[] { 1.0 }, 1, null,
                new BootstrapOptions { PathCount = M, MeanBlockLength = 1.0, Seed = 1 });
            double meanOfMeans = 0.0;
            foreach (double m in iid.MeanReturn) meanOfMeans += m;
            meanOfMeans /= M;
            double standardError = Math.Sqrt(sampleVar / ((double)T * M));
            Assert.InRange(meanOfMeans - sampleMean, -4 * standardError, 4 * standardError);

            // With L = 1 every step restarts at an independent uniform row: no serial correlation survives
            // (sd of a lag-1 autocorrelation estimate over T * paths draws is about 1 / sqrt(400 * 200) = 0.0035).
            double acfIid = 0.0, acfBlocks = 0.0;
            const int P = 200;
            for (int p = 0; p < P; p++)
            {
                acfIid += Lag1Autocorrelation(source, StationaryBootstrap.Rows(p, T, 1.0, 5));
                acfBlocks += Lag1Autocorrelation(source, StationaryBootstrap.Rows(p, T, 50.0, 5));
            }
            acfIid /= P;
            acfBlocks /= P;
            Assert.InRange(acfIid, -0.015, 0.015);
            // Long blocks keep it: within a block pairs are true neighbours (probability 1 - 1/L).
            Assert.InRange(acfBlocks, sourceAcf - 0.08, sourceAcf + 0.02);
        }

        [Fact]
        public void BlocksHaveTheRequestedMeanLength()
        {
            const int T = 1000;
            const double L = 12.5;
            long steps = 0, breaks = 0;
            for (int p = 0; p < 200; p++)
            {
                int[] rows = StationaryBootstrap.Rows(p, T, L, 77);
                for (int s = 1; s < T; s++)
                {
                    steps++;
                    if (rows[s] != (rows[s - 1] + 1) % T) breaks++;
                }
            }
            // A restart lands on the continuation row with probability 1/T, so breaks / steps = (1/L)(1 - 1/T).
            double expected = (1.0 / L) * (1.0 - 1.0 / T);
            double sd = Math.Sqrt(expected * (1 - expected) / steps);
            Assert.InRange((double)breaks / steps, expected - 4 * sd, expected + 4 * sd);
        }

        [Fact]
        public void PathsArePureFunctionsOfSeedAndIndex()
        {
            int[] a = StationaryBootstrap.Rows(17, 250, 6.0, 123);
            Assert.Equal(a, StationaryBootstrap.Rows(17, 250, 6.0, 123));
            Assert.NotEqual(a, StationaryBootstrap.Rows(18, 250, 6.0, 123));
            Assert.NotEqual(a, StationaryBootstrap.Rows(17, 250, 6.0, 124));
            foreach (int r in a) Assert.InRange(r, 0, 249);
        }

        [Fact]
        public void QuantileInterpolatesBetweenOrderStatistics()
        {
            double[] x = { 4.0, 1.0, 3.0, 2.0 };
            Assert.Equal(1.0, BootstrapResult.Quantile(x, 0.0));
            Assert.Equal(2.5, BootstrapResult.Quantile(x, 0.5));
            Assert.Equal(3.7, BootstrapResult.Quantile(x, 0.9), 1e-14);
            Assert.Equal(4.0, BootstrapResult.Quantile(x, 1.0));
        }

        static double[] Cash(int t)
        {
            var c = new double[t];
            for (int i = 0; i < t; i++) c[i] = 0.0001 * (1 + (i % 3));
            return c;
        }

        static int[] Identity(int t)
        {
            var rows = new int[t];
            for (int i = 0; i < t; i++) rows[i] = i;
            return rows;
        }

        static double Lag1Autocorrelation(double[,] source, int[] rows)
        {
            int n = rows.Length;
            double mean = 0.0;
            foreach (int r in rows) mean += source[r, 0];
            mean /= n;
            double num = 0.0, den = 0.0;
            for (int s = 0; s < n; s++)
            {
                double d = source[rows[s], 0] - mean;
                den += d * d;
                if (s > 0) num += d * (source[rows[s - 1], 0] - mean);
            }
            return num / den;
        }

        static void AssertIdentical(BootstrapResult a, BootstrapResult b)
        {
            Assert.Equal(a.TerminalWealth, b.TerminalWealth);
            Assert.Equal(a.MeanReturn, b.MeanReturn);
            Assert.Equal(a.AnnualisedReturn, b.AnnualisedReturn);
            Assert.Equal(a.Volatility, b.Volatility);
            Assert.Equal(a.SharpeRatio, b.SharpeRatio);
            Assert.Equal(a.MaxDrawdown, b.MaxDrawdown);
            Assert.Equal(a.Turnover, b.Turnover);
            Assert.Equal(a.Costs, b.Costs);
        }
    }
}
