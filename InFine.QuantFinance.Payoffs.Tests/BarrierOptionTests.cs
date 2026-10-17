using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Payoffs.Equity;
using InFine.QuantFinance.Scheduling;
using InFine.QuantFinance.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace InFine.QuantFinance.Payoffs.Tests
{
    public class BarrierMathTests
    {
        [Fact]
        public void BreachIsOnOrBeyondTheBarrier()
        {
            Assert.True(BarrierMath.IsBreached(true, 120.0, 120.0));
            Assert.True(BarrierMath.IsBreached(true, 121.0, 120.0));
            Assert.False(BarrierMath.IsBreached(true, 119.99, 120.0));
            Assert.True(BarrierMath.IsBreached(false, 80.0, 80.0));
            Assert.False(BarrierMath.IsBreached(false, 80.01, 80.0));
        }

        [Fact]
        public void ShiftedBarrierMovesTowardTheSpotByBroadieGlassermanKou()
        {
            double shift = Math.Exp(0.5826 * 0.2 * Math.Sqrt(0.01));
            Assert.Equal(120.0 / shift, BarrierMath.ShiftedBarrier(true, 120.0, 0.2, 0.01), 12);
            Assert.Equal(80.0 * shift, BarrierMath.ShiftedBarrier(false, 80.0, 0.2, 0.01), 12);
            Assert.Equal(120.0, BarrierMath.ShiftedBarrier(true, 120.0, 0.2, 0.0), 15);
        }

        [Fact]
        public void BridgeCrossingProbabilityFollowsTheClosedFormAndItsLimits()
        {
            // S0 = 100, S1 = 105, H = 120, sigma = 0.3, dt = 0.25: p = exp(-2 ln(1.2) ln(120/105) / (0.09 * 0.25))
            double expected = Math.Exp(-2.0 * Math.Log(1.2) * Math.Log(120.0 / 105.0) / (0.09 * 0.25));
            Assert.Equal(expected, BarrierMath.CrossingProbability(BarrierMonitoring.BridgeProbability, true, 120.0, 100.0, 105.0, 0.3, 0.25), 14);
            // down barrier: symmetric
            double down = Math.Exp(-2.0 * Math.Log(80.0 / 100.0) * Math.Log(80.0 / 95.0) / (0.09 * 0.25));
            Assert.Equal(down, BarrierMath.CrossingProbability(BarrierMonitoring.BridgeProbability, false, 80.0, 100.0, 95.0, 0.3, 0.25), 14);
            // either end beyond the barrier: certain; both safe with dt -> 0: impossible; a long interval: nearly certain
            Assert.Equal(1.0, BarrierMath.CrossingProbability(BarrierMonitoring.BridgeProbability, true, 120.0, 100.0, 121.0, 0.3, 0.25));
            Assert.Equal(1.0, BarrierMath.CrossingProbability(BarrierMonitoring.BridgeProbability, true, 120.0, 125.0, 100.0, 0.3, 0.25));
            Assert.Equal(0.0, BarrierMath.CrossingProbability(BarrierMonitoring.BridgeProbability, true, 120.0, 100.0, 100.0, 0.3, 0.0));
            Assert.True(BarrierMath.CrossingProbability(BarrierMonitoring.BridgeProbability, true, 120.0, 100.0, 100.0, 0.3, 1e-6) < 1e-12);
            Assert.True(BarrierMath.CrossingProbability(BarrierMonitoring.BridgeProbability, true, 120.0, 119.0, 119.0, 0.3, 100.0) > 0.999);
        }

        [Fact]
        public void DiscreteAndContinuityCorrectedCrossingAreIndicators()
        {
            Assert.Equal(0.0, BarrierMath.CrossingProbability(BarrierMonitoring.Discrete, true, 120.0, 100.0, 119.0, 0.3, 0.1));
            Assert.Equal(1.0, BarrierMath.CrossingProbability(BarrierMonitoring.Discrete, true, 120.0, 100.0, 120.5, 0.3, 0.1));
            // the up barrier 120 is tested at 120 exp(-0.5826 * 0.3 * sqrt(0.1)) = 113.5
            Assert.Equal(0.0, BarrierMath.CrossingProbability(BarrierMonitoring.ContinuityCorrected, true, 120.0, 100.0, 113.0, 0.3, 0.1));
            Assert.Equal(1.0, BarrierMath.CrossingProbability(BarrierMonitoring.ContinuityCorrected, true, 120.0, 100.0, 114.0, 0.3, 0.1));
        }

        [Fact]
        public void KnockOutRebateIsPaidAtTheFirstCrossingAndKnockInRebateAtMaturityWhenNeverHit()
        {
            // pillars 0..4 at t = 0, .25, .5, .75, 1; path crosses the up barrier 110 at pillar 2 (and again at 3)
            double[] dates = { 0, 0.25, 0.5, 0.75, 1.0 };
            double[] df = McPaths.Discounts(dates, 0.04);
            int[] monitor = { 1, 2, 3, 4 };
            double[] hitPath = { 100, 105, 112, 115, 108 };
            double[] safePath = { 100, 101, 103, 102, 108 };
            // knock-out call K=100: hit at pillar 2 -> rebate 5 * df(0.5); no vanilla
            Assert.Equal(5.0 * df[2], BarrierMath.PathPremium(hitPath, dates, df, monitor, 4, 1, true, false, 100.0, 110.0, 5.0, 0.2, BarrierMonitoring.Discrete, null), 14);
            // knock-in call: hit -> vanilla (108 - 100) * df(1)
            Assert.Equal(8.0 * df[4], BarrierMath.PathPremium(hitPath, dates, df, monitor, 4, 1, true, true, 100.0, 110.0, 5.0, 0.2, BarrierMonitoring.Discrete, null), 14);
            // never hit: knock-out pays vanilla, knock-in pays rebate at maturity
            Assert.Equal(8.0 * df[4], BarrierMath.PathPremium(safePath, dates, df, monitor, 4, 1, true, false, 100.0, 110.0, 5.0, 0.2, BarrierMonitoring.Discrete, null), 14);
            Assert.Equal(5.0 * df[4], BarrierMath.PathPremium(safePath, dates, df, monitor, 4, 1, true, true, 100.0, 110.0, 5.0, 0.2, BarrierMonitoring.Discrete, null), 14);
        }

        [Fact]
        public void PathStartedOnTheBarrierIsHitAtInceptionOnlyIfPillarZeroIsMonitored()
        {
            double[] dates = { 0, 0.5, 1.0 };
            double[] df = McPaths.Discounts(dates, 0.0);
            double[] path = { 110, 100, 100 };
            Assert.Equal(7.0, BarrierMath.PathPremium(path, dates, df, new[] { 0, 1, 2 }, 2, 1, true, false, 100.0, 110.0, 7.0, 0.2, BarrierMonitoring.Discrete, null), 14);
            Assert.Equal(0.0, BarrierMath.PathPremium(path, dates, df, new[] { 1, 2 }, 2, 1, true, false, 100.0, 110.0, 7.0, 0.2, BarrierMonitoring.Discrete, null), 14);
        }
    }

    public class BarrierMonteCarloTests
    {
        const double S = 100.0, R = 0.05, Q = 0.02, Sigma = 0.25, T = 1.0;
        readonly ITestOutputHelper _output;
        public BarrierMonteCarloTests(ITestOutputHelper output) { _output = output; }

        /// <summary>Common-random-number Monte Carlo of one barrier contract in a given monitoring mode.</summary>
        static void Run(int paths, int steps, int sign, bool up, bool isIn, double strike, double barrier, double rebate, int mode,
            bool includeInception, bool sampled, out double mean, out double se)
        {
            var dates = McPaths.Dates(steps, T);
            var df = McPaths.Discounts(dates, R);
            int[] monitor = McPaths.Range(includeInception ? 0 : 1, steps);
            var path = new double[steps + 1];
            var normals = new double[steps];
            var uniforms = new double[steps + 1];
            var premium = new double[paths];
            for (int p = 0; p < paths; ++p)
            {
                McPaths.Gbm((uint)p, steps, S, R, Q, Sigma, T, path, normals);
                double[] u = null;
                if (sampled)
                {
                    McPaths.Uniforms((uint)p, McPaths.UniformStream, uniforms, monitor.Length);
                    u = uniforms;
                }
                premium[p] = BarrierMath.PathPremium(path, dates, df, monitor, steps, sign, up, isIn, strike, barrier, rebate, Sigma, mode, u);
            }
            McPaths.MeanAndError(premium, out mean, out se);
        }

        [Fact]
        public void DiscreteMonitoringIsBiasedAndBroadieGlassermanKouCorrectionRemovesMostOfIt()
        {
            // down-and-out call, K = 100, H = 90: discrete monitoring misses crossings between dates, so the knock-out is
            // worth more than the continuous one, by about 0.5826 sigma sqrt(dt) in barrier terms.
            double exact = BarrierKernels.Price(1, false, false, S, 100.0, 90.0, 0.0, T, R, Q, Sigma);
            double previousCorrectedError = double.MaxValue;
            double previousDiscreteError = double.MaxValue;
            foreach (int steps in new[] { 10, 40, 160 })
            {
                double plain, plainSe, corrected, correctedSe;
                Run(100000, steps, 1, false, false, 100.0, 90.0, 0.0, BarrierMonitoring.Discrete, true, false, out plain, out plainSe);
                Run(100000, steps, 1, false, false, 100.0, 90.0, 0.0, BarrierMonitoring.ContinuityCorrected, true, false, out corrected, out correctedSe);
                double plainError = plain - exact, correctedError = corrected - exact;
                _output.WriteLine($"steps {steps}: continuous {exact:F4}, discrete {plain:F4} (err {plainError:F4}), BGK {corrected:F4} (err {correctedError:F4}), se {correctedSe:F4}");
                Assert.True(plainError > 3 * plainSe, $"steps {steps}: the discrete price should exceed the continuous one significantly");
                // the correction removes most of the bias; what is left is O(dt) (about 1.8 se at 10 dates) plus noise
                Assert.True(Math.Abs(correctedError) < Math.Abs(plainError) * 0.25 || Math.Abs(correctedError) < 3 * correctedSe,
                    $"steps {steps}: BGK error {correctedError} not clearly below discrete error {plainError}");
                Assert.True(plainError < previousDiscreteError, "discrete error should shrink with more dates");
                previousDiscreteError = plainError;
                previousCorrectedError = Math.Abs(correctedError);
            }
            Assert.True(previousCorrectedError < 0.1, $"BGK error at 160 dates: {previousCorrectedError}");
        }

        // (sign, up, knock-in, strike, barrier): the eight types, strikes on both sides of the barrier where it makes sense
        static readonly (int Sign, bool Up, bool In, double K, double H)[] Contracts =
        {
            (1, false, false, 100.0, 90.0), (1, false, true, 100.0, 90.0), (1, true, false, 100.0, 125.0), (1, true, true, 100.0, 125.0),
            (-1, false, false, 100.0, 85.0), (-1, false, true, 100.0, 85.0), (-1, true, false, 100.0, 120.0), (-1, true, true, 100.0, 120.0),
            (1, false, true, 90.0, 95.0), (-1, true, false, 110.0, 108.0),
        };

        [Fact]
        public void BridgeWeightedMonitoringReproducesTheContinuousClosedFormOnAVeryCoarseGrid()
        {
            // The bridge probability is exact for the geometric Brownian motion between pillars, so four monitoring
            // dates (dt = 0.25) already give the continuous price up to Monte Carlo noise.
            foreach (var c in Contracts)
            {
                double mean, se;
                Run(100000, 4, c.Sign, c.Up, c.In, c.K, c.H, 0.0, BarrierMonitoring.BridgeProbability, true, false, out mean, out se);
                double exact = BarrierKernels.Price(c.Sign, c.Up, c.In, S, c.K, c.H, 0.0, T, R, Q, Sigma);
                _output.WriteLine($"{c}: weighted bridge {mean:F4} +- {se:F4}, continuous {exact:F4}");
                Assert.True(Math.Abs(mean - exact) < 3.5 * se, $"{c}: MC {mean} +- {se}, closed form {exact}");
            }
        }

        [Fact]
        public void BridgeSampledMonitoringReproducesTheContinuousClosedFormOnAVeryCoarseGrid()
        {
            foreach (var c in Contracts)
            {
                double mean, se;
                Run(100000, 4, c.Sign, c.Up, c.In, c.K, c.H, 0.0, BarrierMonitoring.BridgeProbability, true, true, out mean, out se);
                double exact = BarrierKernels.Price(c.Sign, c.Up, c.In, S, c.K, c.H, 0.0, T, R, Q, Sigma);
                _output.WriteLine($"{c}: sampled bridge {mean:F4} +- {se:F4}, continuous {exact:F4}");
                Assert.True(Math.Abs(mean - exact) < 3.5 * se, $"{c}: MC {mean} +- {se}, closed form {exact}");
            }
        }

        [Fact]
        public void WeightedBridgeHasLowerVarianceThanSamplingTheCrossings()
        {
            double m1, weightedSe, m2, sampledSe;
            Run(50000, 4, 1, false, false, 100.0, 90.0, 0.0, BarrierMonitoring.BridgeProbability, true, false, out m1, out weightedSe);
            Run(50000, 4, 1, false, false, 100.0, 90.0, 0.0, BarrierMonitoring.BridgeProbability, true, true, out m2, out sampledSe);
            Assert.True(weightedSe < sampledSe, $"weighted {weightedSe}, sampled {sampledSe}");
        }

        [Fact]
        public void KnockInRebatePaidAtMaturityMatchesTheClosedFormRebateTerm()
        {
            // the knock-in rebate (paid at expiry when the barrier is never hit) is exactly the closed-form E term
            double exact = BarrierKernels.Price(1, true, true, S, 100.0, 125.0, 6.0, T, R, Q, Sigma);
            double mean, se;
            Run(100000, 4, 1, true, true, 100.0, 125.0, 6.0, BarrierMonitoring.BridgeProbability, true, false, out mean, out se);
            Assert.True(Math.Abs(mean - exact) < 3.5 * se, $"MC {mean} +- {se}, closed form {exact}");
        }

        [Fact]
        public void InPlusOutIsPathwiseExactlyTheVanillaPremiumInEveryMonitoringMode()
        {
            const int steps = 12;
            var dates = McPaths.Dates(steps, T);
            var df = McPaths.Discounts(dates, R);
            int[] monitor = McPaths.Range(0, steps);
            var path = new double[steps + 1];
            var normals = new double[steps];
            var uniforms = new double[steps + 1];
            double worst = 0.0;
            for (int p = 0; p < 3000; ++p)
            {
                McPaths.Gbm((uint)p, steps, S, R, Q, Sigma, T, path, normals);
                McPaths.Uniforms((uint)p, McPaths.UniformStream, uniforms, monitor.Length);
                double vanilla = df[steps] * Math.Max(path[steps] - 100.0, 0.0);
                foreach (bool up in new[] { false, true })
                    foreach (int mode in new[] { BarrierMonitoring.Discrete, BarrierMonitoring.ContinuityCorrected, BarrierMonitoring.BridgeProbability })
                        foreach (bool sampled in new[] { false, true })
                        {
                            if (sampled && mode != BarrierMonitoring.BridgeProbability) continue;
                            double barrier = up ? 112.0 : 92.0;
                            double[] u = sampled ? uniforms : null;
                            double inn = BarrierMath.PathPremium(path, dates, df, monitor, steps, 1, up, true, 100.0, barrier, 0.0, Sigma, mode, u);
                            double @out = BarrierMath.PathPremium(path, dates, df, monitor, steps, 1, up, false, 100.0, barrier, 0.0, Sigma, mode, u);
                            worst = Math.Max(worst, Math.Abs(inn + @out - vanilla));
                        }
            }
            Assert.True(worst < 1e-12, $"worst |in + out - vanilla| = {worst}");
        }
    }

    public class BarrierOptionKernelTests
    {
        static readonly double[] Spots = { 100, 104, 97, 108, 111, 103, 99 };

        [HybridizerFact]
        public void KernelsMatchThePerPathHelpers()
        {
            int n = Spots.Length - 1;
            var dates = McPaths.Dates(n, 1.0);
            var schedule = new PrecalculatedSchedule(dates);
            var discounting = new ConstantRateDiscounting(0.03, schedule);
            var df = McPaths.Discounts(dates, 0.03);
            var spot = new LogBasedSpot<ArrayDoubleSpot>(new ArrayDoubleSpot(1, Spots.Length));
            for (int i = 0; i < Spots.Length; ++i) spot[0, i] = Spots[i];
            int[] monitor = McPaths.Range(0, n);
            var uniform = new ArrayBrownian(1, n + 1);
            double[] u = { 0.9, 0.2, 0.05, 0.6, 0.4, 0.7, 0.3 };
            for (int j = 0; j < u.Length; ++j) uniform[0, j] = u[j];
            foreach (bool up in new[] { false, true })
                foreach (bool isIn in new[] { false, true })
                {
                    double barrier = up ? 112.0 : 96.0;
                    foreach (int mode in new[] { BarrierMonitoring.Discrete, BarrierMonitoring.ContinuityCorrected, BarrierMonitoring.BridgeProbability })
                    {
                        var premium = new ArrayPremium(1);
                        new BarrierOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                            spot, discounting, schedule, premium, monitor, n, 100.0, 1, up, isIn, barrier, 2.0, 0.3, mode).Simulate(0, 1);
                        double expected = BarrierMath.PathPremium(Spots, dates, df, monitor, n, 1, up, isIn, 100.0, barrier, 2.0, 0.3, mode, null);
                        Assert.Equal(expected, premium[0], 10);
                    }
                    var sampled = new ArrayPremium(1);
                    new BridgeSampledBarrierOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium, ArrayBrownian>(
                        spot, discounting, schedule, sampled, uniform, monitor, n, 100.0, 1, up, isIn, barrier, 2.0, 0.3).Simulate(0, 1);
                    double expectedSampled = BarrierMath.PathPremium(Spots, dates, df, monitor, n, 1, up, isIn, 100.0, barrier, 2.0, 0.3, BarrierMonitoring.BridgeProbability, u);
                    Assert.Equal(expectedSampled, sampled[0], 10);
                }
        }
    }
}
