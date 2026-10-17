using System;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Payoffs.Equity;
using InFine.QuantFinance.Scheduling;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Payoffs.Tests
{
    public class VarianceSwapMathTests
    {
        [Fact]
        public void ConstantPathHasZeroRealisedVariance()
        {
            double[] path = { 100, 100, 100, 100, 100 };
            Assert.Equal(0.0, VarianceSwapMath.RealisedVariance(path, 4, 252.0, 0.04, 0.0), 15);
        }

        [Fact]
        public void KnownPathMatchesHandComputedValue()
        {
            // Returns ln(1.1), ln(1/1.1), ln(1.1) => each squared = ln(1.1)^2 = 0.0090842...
            // sigma^2 = 252/3 * 3 * ln(1.1)^2 = 252 * ln(1.1)^2
            double[] path = { 100, 110, 100, 110 };
            double l = Math.Log(1.1);
            Assert.Equal(252.0 * l * l, VarianceSwapMath.RealisedVariance(path, 3, 252.0, 0.04, 0.0), 12);
            Assert.Equal(2.2892, VarianceSwapMath.RealisedVariance(path, 3, 252.0, 0.04, 0.0), 3); // 252 * 0.00908403
            // only the first stepCount steps count, and AF is a parameter
            Assert.Equal(12.0 * l * l, VarianceSwapMath.RealisedVariance(path, 2, 12.0, 0.04, 0.0), 12);
        }

        [Fact]
        public void CapLimitsVarianceToCapSquaredTimesStrike()
        {
            double[] path = { 100, 110, 100, 110 };
            double strike = 0.04;
            double uncapped = VarianceSwapMath.RealisedVariance(path, 3, 252.0, strike, 0.0);
            Assert.True(uncapped > 6.25 * strike);
            Assert.Equal(6.25 * strike, VarianceSwapMath.RealisedVariance(path, 3, 252.0, strike, 2.5), 15);
            // cap above realised: no effect; negative cap: no cap
            Assert.Equal(uncapped, VarianceSwapMath.RealisedVariance(path, 3, 252.0, strike, 100.0), 15);
            Assert.Equal(uncapped, VarianceSwapMath.RealisedVariance(path, 3, 252.0, strike, -1.0), 15);
        }

        [Fact]
        public void PayoffIsVarianceMinusStrike()
        {
            Assert.Equal(0.01, VarianceSwapMath.Payoff(0.05, 0.04), 15);
            Assert.Equal(-0.04, VarianceSwapMath.Payoff(0.0, 0.04), 15);
        }

        [Fact]
        public void GbmRealisedVarianceMatchesExpectationWithinThreeStandardErrors()
        {
            // Log-returns are N(m dt, s^2 dt) with m = r - s^2/2. With dt = 1/AF and the zero-mean convention,
            // E[AF * x^2] = AF * (s^2 dt + m^2 dt^2) = s^2 + m^2 dt. Here m = 0.03, so the discretisation bias
            // is m^2 dt = 3.6e-6, about 0.14 standard errors (se ~ 2.5e-5 for 20k paths): negligible, but the
            // exact expectation is used as the reference anyway. Fixed seed, tolerance 3 standard errors.
            const int paths = 20000, steps = 252;
            const double r = 0.05, s = 0.2, af = 252.0, dt = 1.0 / 252.0;
            double m = r - 0.5 * s * s;
            var rng = new System.Random(20240607);
            var path = new double[steps + 1];
            double sum = 0, sumSq = 0;
            for (int p = 0; p < paths; ++p)
            {
                path[0] = 100.0;
                for (int i = 0; i < steps; ++i)
                    path[i + 1] = path[i] * Math.Exp(m * dt + s * Math.Sqrt(dt) * NextGaussian(rng));
                double v = VarianceSwapMath.RealisedVariance(path, steps, af, 0.04, 0.0);
                sum += v; sumSq += v * v;
            }
            double mean = sum / paths;
            double se = Math.Sqrt((sumSq / paths - mean * mean) / (paths - 1));
            double expected = s * s + m * m * dt;
            Assert.True(Math.Abs(mean - expected) < 3 * se, $"mean {mean}, expected {expected}, se {se}");
        }

        private static double NextGaussian(System.Random rng)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }

    public class VarianceSwapKernelTests
    {
        private static ArrayPremium Run(double[] spots, double strike, double cap, double rate, double af, bool defaultAf)
        {
            int n = spots.Length - 1;
            var dates = new double[spots.Length];
            for (int i = 0; i < dates.Length; ++i) dates[i] = i / 252.0;
            var schedule = new PrecalculatedSchedule(dates);
            var discounting = new ConstantRateDiscounting(rate, schedule);
            var log = new ArrayDoubleSpot(1, spots.Length);
            var spot = new LogBasedSpot<ArrayDoubleSpot>(log);
            for (int i = 0; i < spots.Length; ++i) spot[0, i] = spots[i];
            var premium = new ArrayPremium(1);
            var swap = defaultAf
                ? new VarianceSwap<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(spot, discounting, schedule, premium, n, strike, cap)
                : new VarianceSwap<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(spot, discounting, schedule, premium, n, strike, cap, af);
            swap.Simulate(0, 1);
            return premium;
        }

        [HybridizerFact]
        public void KernelDiscountsRealisedMinusStrike()
        {
            double[] path = { 100, 110, 100, 110 };
            double l = Math.Log(1.1);
            double df = Math.Exp(-0.03 * 3 / 252.0);
            var p = Run(path, 0.04, 0.0, 0.03, 0, true);
            Assert.Equal((252.0 * l * l - 0.04) * df, p[0], 10);
        }

        [HybridizerFact]
        public void KernelHonoursCapAndAnnualisationFactor()
        {
            double[] path = { 100, 110, 100, 110 };
            double l = Math.Log(1.1);
            var capped = Run(path, 0.04, 2.5, 0.0, 0, true);
            Assert.Equal(6.25 * 0.04 - 0.04, capped[0], 10);
            var af = Run(path, 0.04, 0.0, 0.0, 12.0, false);
            Assert.Equal(12.0 * l * l - 0.04, af[0], 10);
        }

        [HybridizerFact]
        public void KernelConstantPathPaysMinusStrike()
        {
            var p = Run(new double[] { 50, 50, 50 }, 0.04, 0.0, 0.0, 0, true);
            Assert.Equal(-0.04, p[0], 10);
        }
    }
}
