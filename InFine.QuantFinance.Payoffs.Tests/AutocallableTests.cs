using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Payoffs.Equity;
using InFine.QuantFinance.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace InFine.QuantFinance.Payoffs.Tests
{
    public class AutocallMathTests
    {
        // one asset, reference 100, quarterly observations at pillars 1..4, no discounting
        static readonly double[] Discount = { 1, 1, 1, 1, 1 };
        static readonly int[] Observations = { 1, 2, 3, 4 };
        static readonly double[] Autocall = { 1.0, 1.0, 1.0, 1.0 };
        static readonly double[] CouponBarrier = { 0.7, 0.7, 0.7, 0.7 };
        static readonly double[] Coupons = { 0.02, 0.02, 0.02, 0.02 };

        static double Run(double[] path, bool memory, int[] knockIn, out int exit)
        {
            return AutocallMath.PathPremium(path, 1, new[] { 100.0 }, Discount, Observations, Autocall, CouponBarrier, Coupons,
                memory, knockIn, 0.6, 1.0, out exit);
        }

        [Fact]
        public void MemoryCouponPaysTheMissedCouponsAtTheNextCouponDate()
        {
            // below the coupon barrier at dates 0 and 1, back above it at date 2, never called, par at maturity
            double[] path = { 100, 65, 60, 80, 90 };
            double withMemory = Run(path, true, new[] { 4 }, out int exit);
            Assert.Equal(3, exit);
            Assert.Equal(0.06 + 0.02 + 1.0, withMemory, 14); // three coupons at date 2, one at maturity, par
            double without = Run(path, false, new[] { 4 }, out exit);
            Assert.Equal(0.02 + 0.02 + 1.0, without, 14);
        }

        [Fact]
        public void EarlyRedemptionStopsTheCashFlows()
        {
            double[] path = { 100, 95, 101, 40, 30 };
            Assert.Equal(0.02 + 0.02 + 1.0, Run(path, true, new[] { 4 }, out int exit), 14);
            Assert.Equal(1, exit);
        }

        [Fact]
        public void KnockInAtMaturityPaysThePerformanceAndDiscreteMonitoringRemembersADip()
        {
            double[] lost = { 100, 90, 80, 75, 50 };
            Assert.Equal(0.02 * 3 + 0.5, Run(lost, false, new[] { 4 }, out int exit), 14);
            Assert.Equal(3, exit);
            // dipped to 55 at pillar 2 then recovered to 85: European barrier not hit, discrete one hit (85 < 100 strike)
            double[] dip = { 100, 90, 55, 75, 85 };
            Assert.Equal(0.02 + 0.04 + 0.02 + 1.0, Run(dip, true, new[] { 4 }, out exit), 14);
            Assert.Equal(0.02 + 0.04 + 0.02 + 0.85, Run(dip, true, new[] { 1, 2, 3, 4 }, out exit), 14);
        }
    }

    public class AutocallableMonteCarloTests
    {
        const double R = 0.03;
        const int Paths = 50000;
        readonly ITestOutputHelper _output;
        public AutocallableMonteCarloTests(ITestOutputHelper output) { _output = output; }

        static readonly double[] Spots = { 100.0, 80.0, 120.0 };
        static readonly double[] Sigma = { 0.2, 0.3, 0.25 };
        static readonly double[] Dividend = { 0.01, 0.0, 0.02 };
        static readonly double[,] Correlation = { { 1.0, 0.5, 0.3 }, { 0.5, 1.0, 0.6 }, { 0.3, 0.6, 1.0 } };

        // semi-annual observations over 3 years on a quarterly grid: pillars 2, 4, ..., 12
        static readonly double[] Dates = McPaths.Dates(12, 3.0);
        static readonly int[] Observations = { 2, 4, 6, 8, 10, 12 };

        static void Price(MultiAssetPaths mc, double[] autocall, double[] couponBarrier, double coupon, bool memory, int[] knockIn,
            double knockInBarrier, double[] premiums, double[] exits)
        {
            var path = mc.NewPath();
            double[] coupons = MultiAssetPaths.Repeat(Observations.Length, coupon);
            for (int p = 0; p < premiums.Length; ++p)
            {
                mc.Fill((uint)p, path);
                premiums[p] = AutocallMath.PathPremium(path, mc.AssetCount, Spots, mc.Discounts, Observations, autocall, couponBarrier,
                    coupons, memory, knockIn, knockInBarrier, 1.0, out int exit);
                exits[p] = exit;
            }
        }

        static double[] ObservationTimes()
        {
            var t = new double[Observations.Length];
            for (int i = 0; i < t.Length; ++i) t[i] = Dates[Observations[i]];
            return t;
        }

        [Fact]
        public void ZeroAutocallBarrierRedeemsEveryPathAtTheFirstDate()
        {
            var mc = new MultiAssetPaths(Spots, Sigma, Dividend, Correlation, R, Dates);
            var premiums = new double[2000];
            var exits = new double[2000];
            double[] zero = MultiAssetPaths.Repeat(6, 0.0);
            Price(mc, zero, zero, 0.035, true, new[] { 12 }, 0.6, premiums, exits);
            double df = mc.Discounts[2];
            foreach (double p in premiums) Assert.Equal(0.035 * df + df, p, 15);
            var summary = AutocallSummary.Create(premiums, exits, ObservationTimes());
            Assert.Equal(new[] { 1.0, 0, 0, 0, 0, 0 }, summary.RedemptionProbabilities);
            Assert.Equal(0.5, summary.ExpectedLife, 15);
            Assert.True(Math.Abs((1.0 + 0.035) * df - summary.Price) < 1e-13); // summation rounding over the paths
            Assert.True(summary.StandardError < 1e-6);
        }

        [Fact]
        public void InfiniteBarriersLeaveAZeroCouponBondMinusTheWorstOfDownAndInPut()
        {
            var mc = new MultiAssetPaths(Spots, Sigma, Dividend, Correlation, R, Dates);
            double[] never = MultiAssetPaths.Repeat(6, double.PositiveInfinity);
            foreach (int[] knockIn in new[] { new[] { 12 }, McPaths.Range(1, 12) })
            {
                var path = mc.NewPath();
                double[] coupons = MultiAssetPaths.Repeat(6, 0.05);
                double df = mc.Discounts[12];
                for (int p = 0; p < 3000; ++p)
                {
                    mc.Fill((uint)p, path);
                    double note = AutocallMath.PathPremium(path, 3, Spots, mc.Discounts, Observations, never, never, coupons, true,
                        knockIn, 0.65, 1.0, out int exit);
                    double put = df * MultiAssetMath.WorstOfDownAndInPutPathPayoff(path, 3, knockIn, 12, Spots, 1.0, 0.65);
                    Assert.Equal(5, exit);
                    Assert.True(Math.Abs(df - put - note) < 1e-14, $"path {p}: note {note}, bond - put {df - put}");
                }
            }
        }

        [Fact]
        public void OneAssetNoteWithInfiniteBarriersMatchesTheBondMinusTheClosedFormKnockInPut()
        {
            const double sigma = 0.25, q = 0.01, h = 0.7;
            var mc = new MultiAssetPaths(new[] { 100.0 }, new[] { sigma }, new[] { q }, new double[,] { { 1.0 } }, R, Dates);
            double[] never = MultiAssetPaths.Repeat(6, double.PositiveInfinity);
            var path = mc.NewPath();
            var premiums = new double[Paths];
            for (int p = 0; p < Paths; ++p)
            {
                mc.Fill((uint)p, path);
                premiums[p] = AutocallMath.PathPremium(path, 1, new[] { 100.0 }, mc.Discounts, Observations, never, never,
                    MultiAssetPaths.Repeat(6, 0.05), false, new[] { 12 }, h, 1.0, out _);
            }
            McPaths.MeanAndError(premiums, out double mean, out double se);
            const double T = 3.0;
            double d2 = (Math.Log(1.0 / h) + (R - q - 0.5 * sigma * sigma) * T) / (sigma * Math.Sqrt(T));
            double put = BlackScholesKernels.Price(-1, 1.0, h, T, R, q, sigma) + (1.0 - h) * Math.Exp(-R * T) * NormalDistribution.Cdf(-d2);
            double exact = Math.Exp(-R * T) - put;
            Assert.True(Math.Abs(mean - exact) < 3.0 * se, $"MC {mean} +- {se}, bond - put {exact}");
        }

        [Fact]
        public void PhoenixSummaryIsConsistent()
        {
            var mc = new MultiAssetPaths(Spots, Sigma, Dividend, Correlation, R, Dates);
            var premiums = new double[Paths];
            var exits = new double[Paths];
            double[] autocall = { 1.0, 1.0, 0.95, 0.95, 0.9, 0.9 };
            double[] couponBarrier = MultiAssetPaths.Repeat(6, 0.7);
            Price(mc, autocall, couponBarrier, 0.04, true, McPaths.Range(1, 12), 0.6, premiums, exits);
            var summary = AutocallSummary.Create(premiums, exits, ObservationTimes());
            double total = 0.0;
            foreach (double p in summary.RedemptionProbabilities) { Assert.True(p >= 0.0); total += p; }
            _output.WriteLine($"price {summary.Price:F4} +- {summary.StandardError:F4}, life {summary.ExpectedLife:F3}, p = [{string.Join(", ", summary.RedemptionProbabilities)}]");
            Assert.Equal(1.0, total, 12);
            Assert.InRange(summary.ExpectedLife, 0.5, 3.0);
            Assert.True(summary.RedemptionProbabilities[0] > 0.2, "an at-the-money first autocall should trigger often");
            Assert.InRange(summary.Price, 0.5, 1.0 + 6 * 0.04);

            // memory coupons and a European knock-in can only help the investor, path by path
            var noMemory = new double[Paths];
            var european = new double[Paths];
            var scratch = new double[Paths];
            Price(mc, autocall, couponBarrier, 0.04, false, McPaths.Range(1, 12), 0.6, noMemory, scratch);
            Price(mc, autocall, couponBarrier, 0.04, true, new[] { 12 }, 0.6, european, scratch);
            for (int p = 0; p < Paths; ++p)
            {
                Assert.True(noMemory[p] <= premiums[p] + 1e-15);
                Assert.True(premiums[p] <= european[p] + 1e-15);
            }
        }

        [Fact]
        public void SummaryRejectsInconsistentInputs()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AutocallSummary.Create(new[] { 1.0, 1.0 }, new[] { 0.0, 2.0 }, new[] { 0.5, 1.0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => AutocallSummary.Create(new[] { 1.0, 1.0 }, new[] { 0.5, 1.0 }, new[] { 0.5, 1.0 }));
            Assert.Throws<ArgumentException>(() => AutocallSummary.Create(new[] { 1.0, 1.0 }, new[] { 0.0 }, new[] { 0.5, 1.0 }));
        }
    }

    public class AutocallableKernelTests
    {
        [HybridizerFact]
        public void KernelMatchesThePerPathHelper()
        {
            // 2 assets, pillars 0..4; paths chosen to hit early redemption, memory coupons and knock-in
            double[][] paths =
            {
                new double[] { 100, 50, 95, 52, 101, 51, 80, 40, 90, 45 },
                new double[] { 100, 50, 65, 40, 90, 46, 80, 41, 70, 25 },
                new double[] { 100, 50, 60, 52, 80, 30, 85, 44, 95, 48 },
            };
            double[] dates = { 0.0, 0.25, 0.5, 0.75, 1.0 };
            var schedule = new PrecalculatedSchedule(dates);
            var discounting = new ConstantRateDiscounting(0.03, schedule);
            double[] df = McPaths.Discounts(dates, 0.03);
            double[] reference = { 100.0, 50.0 };
            int[] observations = { 1, 2, 3, 4 };
            double[] autocall = { 1.0, 1.0, 0.95, 0.95 }, couponBarrier = { 0.7, 0.7, 0.7, 0.7 }, coupons = { 0.02, 0.02, 0.03, 0.03 };
            var spot = new LogBasedSpot<ArrayDoubleSpot>(new ArrayDoubleSpot(paths.Length, 10));
            for (int p = 0; p < paths.Length; ++p)
                for (int k = 0; k < 10; ++k) spot[p, k] = paths[p][k];
            foreach (bool memory in new[] { false, true })
                foreach (int[] knockIn in new[] { new[] { 4 }, new[] { 1, 2, 3, 4 } })
                {
                    var premium = new ArrayPremium(paths.Length);
                    var exit = new ArrayPremium(paths.Length);
                    new Autocallable<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                        spot, discounting, schedule, premium, exit, 2, reference, observations, autocall, couponBarrier, coupons,
                        memory, knockIn, 0.6, 1.0).Simulate(0, paths.Length);
                    for (int p = 0; p < paths.Length; ++p)
                    {
                        double expected = AutocallMath.PathPremium(paths[p], 2, reference, df, observations, autocall, couponBarrier,
                            coupons, memory, knockIn, 0.6, 1.0, out int expectedExit);
                        Assert.Equal(expected, premium[p], 12);
                        Assert.Equal(expectedExit, exit[p]);
                    }
                }
        }
    }
}
