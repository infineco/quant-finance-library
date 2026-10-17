using System;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Models.Tests
{
    public class UnitTest_LinearInterpolation
    {
        const double Tol = 1e-14;

        static readonly double[] Xs = { 0.5, 1.0, 3.0, 7.0 };
        static readonly double[] Ys = { 0.02, 0.03, 0.05, 0.04 };

        static double I(double x) => LinearInterpolation.Interpolate(Xs, Ys, Xs.Length, x);

        [Fact]
        public void ExactAtPillars()
        {
            for (int i = 0; i < Xs.Length; ++i)
                Assert.Equal(Ys[i], I(Xs[i]), Tol);
        }

        [Fact]
        public void MidpointIsAverageAndWeightsAreNotInverted()
        {
            Assert.Equal(0.025, I(0.75), Tol);
            Assert.Equal(0.04, I(2.0), Tol);
            // closer to the left pillar -> closer to the left value
            Assert.Equal(0.02 + 0.01 * 0.1, I(0.55), Tol);
        }

        [Fact]
        public void NonUniformSpacingMatchesFormula()
        {
            // between 3 and 7: 0.05 -> 0.04
            Assert.Equal(0.05 + (0.04 - 0.05) * (6.0 - 3.0) / 4.0, I(6.0), Tol);
            // between 1 and 3
            Assert.Equal(0.03 + (0.05 - 0.03) * (1.5 - 1.0) / 2.0, I(1.5), Tol);
        }

        [Fact]
        public void FlatExtrapolationOnBothSides()
        {
            Assert.Equal(0.02, I(0.0), Tol);
            Assert.Equal(0.02, I(-5.0), Tol);
            Assert.Equal(0.04, I(7.0001), Tol);
            Assert.Equal(0.04, I(100.0), Tol);
        }

        [Fact]
        public void SinglePillar()
        {
            double[] x = { 2.0 }, y = { 0.07 };
            Assert.Equal(0.07, LinearInterpolation.Interpolate(x, y, 1, 0.0), Tol);
            Assert.Equal(0.07, LinearInterpolation.Interpolate(x, y, 1, 2.0), Tol);
            Assert.Equal(0.07, LinearInterpolation.Interpolate(x, y, 1, 9.0), Tol);
        }

        [Fact]
        public void TwoPillars()
        {
            double[] x = { 1.0, 2.0 }, y = { 0.01, 0.03 };
            Assert.Equal(0.02, LinearInterpolation.Interpolate(x, y, 2, 1.5), Tol);
        }

        [Fact]
        public void CountLimitsTheUsedPoints()
        {
            // only the first 2 points are used: the 3rd is extrapolated flat from the 2nd
            Assert.Equal(0.03, LinearInterpolation.Interpolate(Xs, Ys, 2, 2.0), Tol);
            Assert.Equal(0.0, LinearInterpolation.Interpolate(Xs, Ys, 0, 2.0), Tol);
        }

        [Fact]
        public void DiscountFactorIsExpMinusRateTimesTime()
        {
            foreach (double t in new[] { 0.25, 0.5, 0.9, 2.0, 5.5, 7.0, 10.0 })
            {
                double expected = Math.Exp(-I(t) * t);
                double df = Math.Exp(-LinearInterpolation.Interpolate(Xs, Ys, Xs.Length, t) * t);
                Assert.Equal(expected, df, Tol);
                Assert.InRange(df, 0.0, 1.0);
            }
            // at a pillar the discount uses exactly that pillar's rate
            Assert.Equal(Math.Exp(-0.05 * 3.0), Math.Exp(-I(3.0) * 3.0), Tol);
        }

        [HybridizerFact]
        public void LinearRateDiscountingMatchesInterpolatedRate()
        {
            double[] sched = { 0.0, 0.5, 1.0, 2.0, 4.0 };
            var schedule = new PrecalculatedSchedule(sched);
            var disc = new LinearRateDiscounting(Xs, Ys, schedule);
            for (int k = 0; k < sched.Length; ++k)
            {
                double expected = Math.Exp(-I(sched[k]) * sched[k]);
                Assert.Equal(expected, disc[0, k], Tol);
                Assert.Equal(expected, disc[0, sched[k]], Tol);
            }
        }
    }
}
