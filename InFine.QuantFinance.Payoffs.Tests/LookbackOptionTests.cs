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
    public class LookbackMathTests
    {
        static readonly double[] Path = { 100, 104, 95, 110, 102 };
        static readonly int[] Monitor = { 1, 2, 3, 4 };

        [Fact]
        public void FloatingStrikePayoffs()
        {
            // minimum over {100 (inception), 104, 95, 110, 102} = 95, maximum 110, S_T = 102
            Assert.Equal(102.0 - 95.0, LookbackMath.PathPayoff(Path, Monitor, 4, true, 1, 0.0, 0.0, 1.0), 12);
            Assert.Equal(110.0 - 102.0, LookbackMath.PathPayoff(Path, Monitor, 4, true, -1, 0.0, 0.0, 1.0), 12);
        }

        [Fact]
        public void FixedStrikePayoffs()
        {
            Assert.Equal(110.0 - 105.0, LookbackMath.PathPayoff(Path, Monitor, 4, false, 1, 105.0, 0.0, 1.0), 12);
            Assert.Equal(0.0, LookbackMath.PathPayoff(Path, Monitor, 4, false, 1, 115.0, 0.0, 1.0), 12);
            Assert.Equal(98.0 - 95.0, LookbackMath.PathPayoff(Path, Monitor, 4, false, -1, 98.0, 0.0, 1.0), 12);
            Assert.Equal(0.0, LookbackMath.PathPayoff(Path, Monitor, 4, false, -1, 90.0, 0.0, 1.0), 12);
        }

        [Fact]
        public void InceptionSpotIsPartOfTheExtremaEvenWhenNotMonitored()
        {
            double[] path = { 100, 120, 130 };
            // floating call: S_T - min(100, ...) = 130 - 100, because the spot at inception counts
            Assert.Equal(30.0, LookbackMath.PathPayoff(path, new[] { 1, 2 }, 2, true, 1, 0.0, 0.0, 1.0), 12);
        }

        [Fact]
        public void ObservedExtremumOnlyWidensTheRange()
        {
            // floating call with an already observed minimum of 90 (below the path minimum 95) and of 97 (above it)
            Assert.Equal(102.0 - 90.0, LookbackMath.PathPayoff(Path, Monitor, 4, true, 1, 0.0, 90.0, 1.0), 12);
            Assert.Equal(102.0 - 95.0, LookbackMath.PathPayoff(Path, Monitor, 4, true, 1, 0.0, 97.0, 1.0), 12);
            // fixed call uses an observed maximum: 120 above the path maximum 110
            Assert.Equal(120.0 - 105.0, LookbackMath.PathPayoff(Path, Monitor, 4, false, 1, 105.0, 120.0, 1.0), 12);
            // a fixed put ignores an observed maximum, a floating put ignores an observed minimum
            Assert.Equal(98.0 - 95.0, LookbackMath.PathPayoff(Path, Monitor, 4, false, -1, 98.0, 200.0, 1.0), 12);
            Assert.Equal(110.0 - 102.0, LookbackMath.PathPayoff(Path, Monitor, 4, true, -1, 0.0, 50.0, 1.0), 12);
        }

        [Fact]
        public void ContinuityShiftWidensTheSimulatedExtremaOnly()
        {
            double shift = LookbackMath.ContinuityShift(0.2, 0.01);
            Assert.Equal(Math.Exp(0.5826 * 0.2 * 0.1), shift, 14);
            Assert.Equal(102.0 - 95.0 / shift, LookbackMath.PathPayoff(Path, Monitor, 4, true, 1, 0.0, 0.0, shift), 12);
            Assert.Equal(110.0 * shift - 102.0, LookbackMath.PathPayoff(Path, Monitor, 4, true, -1, 0.0, 0.0, shift), 12);
            // the inception spot is exact and not shifted: it stays the minimum while the shifted monitored minimum
            // 120 / shift is above it
            double[] rising = { 100, 120, 130 };
            Assert.Equal(130.0 - 100.0, LookbackMath.PathPayoff(rising, new[] { 1, 2 }, 2, true, 1, 0.0, 0.0, shift), 12);
        }
    }

    public class LookbackMonteCarloTests
    {
        const double S = 100.0, R = 0.05, Q = 0.02, Sigma = 0.25, T = 1.0;
        const int Paths = 100000;
        readonly ITestOutputHelper _output;
        public LookbackMonteCarloTests(ITestOutputHelper output) { _output = output; }

        static void Run(int steps, bool floating, int sign, double strike, bool corrected, out double mean, out double se)
        {
            var path = new double[steps + 1];
            var normals = new double[steps];
            int[] monitor = McPaths.Range(1, steps);
            double shift = corrected ? LookbackMath.ContinuityShift(Sigma, T / steps) : 1.0;
            double df = Math.Exp(-R * T);
            var premium = new double[Paths];
            for (int p = 0; p < Paths; ++p)
            {
                McPaths.Gbm((uint)p, steps, S, R, Q, Sigma, T, path, normals);
                premium[p] = df * LookbackMath.PathPayoff(path, monitor, steps, floating, sign, strike, 0.0, shift);
            }
            McPaths.MeanAndError(premium, out mean, out se);
        }

        static double Continuous(bool floating, int sign, double strike)
        {
            return floating ? LookbackKernels.FloatingStrikePrice(sign, S, S, T, R, Q, Sigma)
                            : LookbackKernels.FixedStrikePrice(sign, S, strike, S, T, R, Q, Sigma);
        }

        [Fact]
        public void DiscreteLookbackIsBiasedLowByOrderSqrtDtAndTheContinuityShiftRemovesMostOfIt()
        {
            // Discrete extrema are inside the continuous ones, so all four prices are underestimated; the error
            // falls like sqrt(dt) (about 0.5826 sigma sqrt(dt) * S for a floating payoff) and, with the Broadie-Glasserman-Kou
            // shift, like dt.
            foreach (var (floating, sign, strike) in new[] { (true, 1, 0.0), (true, -1, 0.0), (false, 1, 100.0), (false, -1, 100.0) })
            {
                double exact = Continuous(floating, sign, strike);
                double previousPlainError = double.MaxValue;
                foreach (int steps in new[] { 12, 48, 192 })
                {
                    double plain, plainSe, corrected, correctedSe;
                    Run(steps, floating, sign, strike, false, out plain, out plainSe);
                    Run(steps, floating, sign, strike, true, out corrected, out correctedSe);
                    double plainError = exact - plain, correctedError = corrected - exact;
                    _output.WriteLine($"{(floating ? "floating" : "fixed")} sign {sign} steps {steps}: continuous {exact:F4}, discrete {plain:F4} (bias {-plainError:F4}), shifted {corrected:F4} (err {correctedError:F4}), se {plainSe:F4}");
                    Assert.True(plainError > 3 * plainSe, $"steps {steps}: discrete price must be below the continuous one");
                    Assert.True(plainError < previousPlainError, "bias should shrink with more dates");
                    // one quartering of dt halves the sqrt(dt) bias
                    if (previousPlainError < double.MaxValue)
                        Assert.InRange(plainError / previousPlainError, 0.35, 0.65);
                    previousPlainError = plainError;
                    Assert.True(Math.Abs(correctedError) < 0.3 * plainError || Math.Abs(correctedError) < 3.5 * correctedSe,
                        $"steps {steps}: shifted error {correctedError} vs discrete bias {plainError}");
                }
            }
        }
    }

    public class LookbackOptionKernelTests
    {
        [HybridizerFact]
        public void KernelMatchesThePerPathHelpers()
        {
            double[] spots = { 100, 104, 97, 108, 111, 103, 99 };
            int n = spots.Length - 1;
            var schedule = new PrecalculatedSchedule(McPaths.Dates(n, 1.0));
            var discounting = new ConstantRateDiscounting(0.03, schedule);
            var spot = new LogBasedSpot<ArrayDoubleSpot>(new ArrayDoubleSpot(1, spots.Length));
            for (int i = 0; i < spots.Length; ++i) spot[0, i] = spots[i];
            int[] monitor = McPaths.Range(1, n);
            foreach (bool floating in new[] { false, true })
                foreach (int sign in new[] { 1, -1 })
                    foreach (double observed in new[] { 0.0, 94.0, 115.0 })
                    {
                        var premium = new ArrayPremium(1);
                        new LookbackOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>(
                            spot, discounting, schedule, premium, monitor, n, 102.0, sign, floating, observed, 1.01).Simulate(0, 1);
                        double expected = LookbackMath.PathPayoff(spots, monitor, n, floating, sign, 102.0, observed, 1.01) * Math.Exp(-0.03);
                        Assert.Equal(expected, premium[0], 10);
                    }
        }
    }
}
