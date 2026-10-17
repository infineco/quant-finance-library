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
    /// <summary>
    /// Basket, rainbow and worst-of barrier payoffs on correlated Philox paths, against the closed forms of
    /// InFine.QuantFinance.ClosedForm (within 3 standard errors) and exact pathwise identities.
    /// </summary>
    public class MultiAssetOptionMonteCarloTests
    {
        const double R = 0.03, T = 1.0;
        const int Paths = 100000;
        readonly ITestOutputHelper _output;
        public MultiAssetOptionMonteCarloTests(ITestOutputHelper output) { _output = output; }

        static void AssertWithin(double mean, double se, double exact, string what, double k = 3.0)
        {
            Assert.True(Math.Abs(mean - exact) < k * se, $"{what}: MC {mean} +- {se}, closed form {exact}");
        }

        [Fact]
        public void OneAssetBasketIsTheBlackScholesVanilla()
        {
            var mc = new MultiAssetPaths(new[] { 100.0 }, new[] { 0.3 }, new[] { 0.02 }, new double[,] { { 1.0 } }, R, new[] { 0.0, T });
            var path = mc.NewPath();
            var call = new double[Paths];
            var put = new double[Paths];
            double[] w = { 1.0 }, reference = { 1.0 };
            for (int p = 0; p < Paths; ++p)
            {
                mc.Fill((uint)p, path);
                call[p] = mc.Discounts[1] * MultiAssetMath.BasketPathPayoff(path, 1, 1, w, reference, 1, false, 105.0);
                put[p] = mc.Discounts[1] * MultiAssetMath.BasketPathPayoff(path, 1, 1, w, reference, -1, false, 105.0);
            }
            McPaths.MeanAndError(call, out double cm, out double cse);
            McPaths.MeanAndError(put, out double pm, out double pse);
            AssertWithin(cm, cse, BlackScholesKernels.Price(1, 100.0, 105.0, T, R, 0.02, 0.3), "call");
            AssertWithin(pm, pse, BlackScholesKernels.Price(-1, 100.0, 105.0, T, R, 0.02, 0.3), "put");
        }

        [Fact]
        public void PerfectlyCorrelatedIdenticalAssetsGiveTheSingleAssetOptionExactlyPathByPath()
        {
            double[] dates = { 0.0, 0.25, 0.5, 1.0 };
            var mc = new MultiAssetPaths(new[] { 100.0, 100.0 }, new[] { 0.25, 0.25 }, new[] { 0.01, 0.01 }, MultiAssetPaths.Uniform(2, 1.0), R, dates);
            var path = mc.NewPath();
            double[] w = { 0.5, 0.5 }, reference = { 100.0, 100.0 }, one = { 100.0 }, unit = { 1.0 };
            for (int p = 0; p < 2000; ++p)
            {
                mc.Fill((uint)p, path);
                double[] single = mc.Column(path, 0);
                for (int t = 0; t < dates.Length; ++t)
                    Assert.Equal(path[2 * t], path[2 * t + 1]);
                foreach (int sign in new[] { 1, -1 })
                {
                    double vanilla = MultiAssetMath.BasketPathPayoff(single, 1, 3, unit, one, sign, false, 1.02);
                    Assert.Equal(vanilla, MultiAssetMath.BasketPathPayoff(path, 2, 3, w, reference, sign, false, 1.02));
                    Assert.Equal(vanilla, MultiAssetMath.RainbowPathPayoff(path, 2, 3, reference, sign, false, 1.02));
                    Assert.Equal(vanilla, MultiAssetMath.RainbowPathPayoff(path, 2, 3, reference, sign, true, 1.02));
                }
            }
        }

        [Fact]
        public void ExchangeOptionByMonteCarloMatchesMargrabe()
        {
            // basket S1 - S2 with strike 0 is the exchange option
            var mc = new MultiAssetPaths(new[] { 100.0, 95.0 }, new[] { 0.3, 0.2 }, new[] { 0.02, 0.0 },
                new double[,] { { 1.0, 0.4 }, { 0.4, 1.0 } }, R, new[] { 0.0, T });
            var path = mc.NewPath();
            var premium = new double[Paths];
            double[] w = { 1.0, -1.0 }, reference = { 1.0, 1.0 };
            for (int p = 0; p < Paths; ++p)
            {
                mc.Fill((uint)p, path);
                premium[p] = mc.Discounts[1] * MultiAssetMath.BasketPathPayoff(path, 2, 1, w, reference, 1, false, 0.0);
            }
            McPaths.MeanAndError(premium, out double mean, out double se);
            double margrabe = MultiAssetKernels.ExchangePrice(100.0, 95.0, T, 0.02, 0.0, 0.3, 0.2, 0.4);
            _output.WriteLine($"exchange: MC {mean:F4} +- {se:F4}, Margrabe {margrabe:F4}");
            AssertWithin(mean, se, margrabe, "exchange");
        }

        static readonly double[] Spots3 = { 100.0, 80.0, 120.0 };
        static readonly double[] Sigma3 = { 0.2, 0.3, 0.25 };
        static readonly double[] Dividend3 = { 0.01, 0.0, 0.02 };
        static readonly double[,] Correlation3 = { { 1.0, 0.5, 0.3 }, { 0.5, 1.0, 0.6 }, { 0.3, 0.6, 1.0 } };

        [Fact]
        public void GeometricBasketMatchesItsClosedFormAndControlsTheArithmeticBasket()
        {
            var mc = new MultiAssetPaths(Spots3, Sigma3, Dividend3, Correlation3, R, new[] { 0.0, T });
            var path = mc.NewPath();
            double[] w = { 0.4, 0.35, 0.25 };
            var geometric = new double[Paths];
            var arithmetic = new double[Paths];
            for (int p = 0; p < Paths; ++p)
            {
                mc.Fill((uint)p, path);
                geometric[p] = mc.Discounts[1] * MultiAssetMath.BasketPathPayoff(path, 3, 1, w, Spots3, 1, true, 1.0);
                arithmetic[p] = mc.Discounts[1] * MultiAssetMath.BasketPathPayoff(path, 3, 1, w, Spots3, 1, false, 1.0);
            }
            double exact = MultiAssetKernels.GeometricBasketPrice(1, 3, new[] { 1.0, 1.0, 1.0 }, w, Dividend3, Sigma3,
                MultiAssetPaths.Flatten(Correlation3), 1.0, T, R);
            McPaths.MeanAndError(geometric, out double gm, out double gse);
            AssertWithin(gm, gse, exact, "geometric basket");

            ControlVariateEstimate cv = AsianControlVariate.Estimate(arithmetic, geometric, exact);
            _output.WriteLine($"arithmetic basket: plain {cv.PlainValue:F5} +- {cv.PlainStandardError:F5}, CV {cv.Value:F5} +- {cv.StandardError:F5}, variance reduction {cv.VarianceReduction:F0}");
            Assert.True(cv.VarianceReduction > 50.0, $"variance reduction {cv.VarianceReduction}");
            Assert.True(Math.Abs(cv.Value - cv.PlainValue) < 3.0 * cv.PlainStandardError);
            // arithmetic >= geometric average, so the arithmetic call is worth more
            Assert.True(cv.Value > exact);
        }

        [Fact]
        public void WorstOfIsBelowAndBestOfAboveEverySingleAssetOptionPathByPath()
        {
            var mc = new MultiAssetPaths(Spots3, Sigma3, Dividend3, Correlation3, R, new[] { 0.0, 0.5, T });
            var path = mc.NewPath();
            double[] unit = { 1.0 };
            for (int p = 0; p < 5000; ++p)
            {
                mc.Fill((uint)p, path);
                foreach (double k in new[] { 0.8, 1.0, 1.2 })
                {
                    double worstCall = MultiAssetMath.RainbowPathPayoff(path, 3, 2, Spots3, 1, false, k);
                    double bestCall = MultiAssetMath.RainbowPathPayoff(path, 3, 2, Spots3, 1, true, k);
                    double worstPut = MultiAssetMath.RainbowPathPayoff(path, 3, 2, Spots3, -1, false, k);
                    double bestPut = MultiAssetMath.RainbowPathPayoff(path, 3, 2, Spots3, -1, true, k);
                    for (int d = 0; d < 3; ++d)
                    {
                        double[] single = mc.Column(path, d);
                        double call = MultiAssetMath.BasketPathPayoff(single, 1, 2, unit, new[] { Spots3[d] }, 1, false, k);
                        double put = MultiAssetMath.BasketPathPayoff(single, 1, 2, unit, new[] { Spots3[d] }, -1, false, k);
                        Assert.True(worstCall <= call && call <= bestCall);
                        Assert.True(bestPut <= put && put <= worstPut);
                    }
                }
            }
        }

        [Theory]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(-1, false)]
        [InlineData(-1, true)]
        public void TwoAssetRainbowMatchesStulz(int sign, bool best)
        {
            var mc = new MultiAssetPaths(new[] { 100.0, 110.0 }, new[] { 0.25, 0.35 }, new[] { 0.02, 0.0 },
                new double[,] { { 1.0, 0.5 }, { 0.5, 1.0 } }, R, new[] { 0.0, T });
            var path = mc.NewPath();
            var premium = new double[Paths];
            double[] reference = { 1.0, 1.0 };
            for (int p = 0; p < Paths; ++p)
            {
                mc.Fill((uint)p, path);
                premium[p] = mc.Discounts[1] * MultiAssetMath.RainbowPathPayoff(path, 2, 1, reference, sign, best, 100.0);
            }
            McPaths.MeanAndError(premium, out double mean, out double se);
            double stulz = MultiAssetKernels.MinMaxPrice(sign, best, 100.0, 110.0, 100.0, T, R, 0.02, 0.0, 0.25, 0.35, 0.5);
            _output.WriteLine($"sign {sign} best {best}: MC {mean:F4} +- {se:F4}, Stulz {stulz:F4}");
            AssertWithin(mean, se, stulz, $"sign {sign}, best {best}");
        }

        [Fact]
        public void OneAssetEuropeanDownAndInPutIsAPutPlusADigital()
        {
            // (K - P) 1{P <= H} = (H - P)^+ + (K - H) 1{P <= H} on the performance P = S_T / S_0
            const double k = 1.0, h = 0.7, sigma = 0.3, q = 0.01;
            var mc = new MultiAssetPaths(new[] { 100.0 }, new[] { sigma }, new[] { q }, new double[,] { { 1.0 } }, R, new[] { 0.0, 0.5, T });
            var path = mc.NewPath();
            var premium = new double[Paths];
            for (int p = 0; p < Paths; ++p)
            {
                mc.Fill((uint)p, path);
                premium[p] = mc.Discounts[2] * MultiAssetMath.WorstOfDownAndInPutPathPayoff(path, 1, new[] { 2 }, 2, new[] { 100.0 }, k, h);
            }
            McPaths.MeanAndError(premium, out double mean, out double se);
            double d2 = (Math.Log(1.0 / h) + (R - q - 0.5 * sigma * sigma) * T) / (sigma * Math.Sqrt(T));
            double exact = BlackScholesKernels.Price(-1, 1.0, h, T, R, q, sigma) + (k - h) * Math.Exp(-R * T) * NormalDistribution.Cdf(-d2);
            AssertWithin(mean, se, exact, "European down-and-in put");
        }

        [Fact]
        public void DiscretelyMonitoredKnockInIsWorthMoreThanTheEuropeanOneAndBoundedByThePut()
        {
            double[] dates = McPaths.Dates(12, T);
            var mc = new MultiAssetPaths(Spots3, Sigma3, Dividend3, Correlation3, R, dates);
            var path = mc.NewPath();
            int[] every = McPaths.Range(1, 12), atMaturity = { 12 };
            for (int p = 0; p < 5000; ++p)
            {
                mc.Fill((uint)p, path);
                double european = MultiAssetMath.WorstOfDownAndInPutPathPayoff(path, 3, atMaturity, 12, Spots3, 1.0, 0.7);
                double american = MultiAssetMath.WorstOfDownAndInPutPathPayoff(path, 3, every, 12, Spots3, 1.0, 0.7);
                double put = MultiAssetMath.RainbowPathPayoff(path, 3, 12, Spots3, -1, false, 1.0);
                Assert.True(european <= american && american <= put);
            }
        }
    }

    public class MultiAssetOptionKernelTests
    {
        // 2 assets, pillars 0..3; asset d at pillar t in column t * 2 + d
        static readonly double[] Path = { 100, 50, 104, 46, 97, 52, 111, 34 };
        static readonly double[] Reference = { 100.0, 50.0 };
        static readonly double[] Dates = { 0.0, 0.25, 0.5, 1.0 };

        static LogBasedSpot<ArrayDoubleSpot> Spot()
        {
            var spot = new LogBasedSpot<ArrayDoubleSpot>(new ArrayDoubleSpot(1, Path.Length));
            for (int k = 0; k < Path.Length; ++k) spot[0, k] = Path[k];
            return spot;
        }

        [HybridizerFact]
        public void KernelsMatchThePerPathHelpers()
        {
            var schedule = new PrecalculatedSchedule(Dates);
            var discounting = new ConstantRateDiscounting(0.03, schedule);
            double df = McPaths.Discounts(Dates, 0.03)[3];
            var spot = Spot();
            double[] w = { 0.6, 0.4 };
            foreach (int sign in new[] { 1, -1 })
                foreach (bool flag in new[] { false, true })
                {
                    var basket = new ArrayPremium(1);
                    new BasketOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                        spot, discounting, schedule, basket, 2, w, Reference, 3, 0.9, sign, flag).Simulate(0, 1);
                    Assert.Equal(df * MultiAssetMath.BasketPathPayoff(Path, 2, 3, w, Reference, sign, flag, 0.9), basket[0], 12);

                    var rainbow = new ArrayPremium(1);
                    new RainbowOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                        spot, discounting, schedule, rainbow, 2, Reference, 3, 0.9, sign, flag).Simulate(0, 1);
                    Assert.Equal(df * MultiAssetMath.RainbowPathPayoff(Path, 2, 3, Reference, sign, flag, 0.9), rainbow[0], 12);
                }
            foreach (int[] monitor in new[] { new[] { 3 }, new[] { 1, 2, 3 } })
            {
                var put = new ArrayPremium(1);
                new WorstOfDownAndInPut<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                    spot, discounting, schedule, put, 2, Reference, monitor, 3, 1.0, 0.75).Simulate(0, 1);
                Assert.Equal(df * MultiAssetMath.WorstOfDownAndInPutPathPayoff(Path, 2, monitor, 3, Reference, 1.0, 0.75), put[0], 12);
            }
        }
    }
}
