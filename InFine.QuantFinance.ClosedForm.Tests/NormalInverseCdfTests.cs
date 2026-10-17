using System;
using InFine.QuantFinance.ClosedForm;
using Xunit;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    public class NormalInverseCdfTests
    {
        // Reference quantiles from 700-digit decimal bisection on the Taylor series of erf
        // (Python decimal), rounded to 17 significant digits. Tolerance 1e-15 relative (about 4 ulp).
        [Theory]
        [InlineData(0.975, 1.9599639845400543)]
        [InlineData(0.999, 3.0902323061678136)]
        [InlineData(1e-3, -3.0902323061678136)]
        [InlineData(1e-10, -6.3613409024040566)]
        [InlineData(1e-20, -9.2623400897984070)]
        [InlineData(1.85e-75, -18.343800917997132)]
        [InlineData(1e-100, -21.273453560965326)]
        [InlineData(1e-200, -30.205594179579641)]
        [InlineData(1e-300, -37.047096299361201)]
        public void KnownQuantiles(double p, double expected)
        {
            double x = NormalDistribution.InverseCdf(p);
            Assert.True(Math.Abs(x - expected) <= 1e-15 * Math.Abs(expected), $"p={p}: {x:R} vs {expected:R}");
        }

        [Fact]
        public void MedianIsZero()
        {
            Assert.Equal(0.0, NormalDistribution.InverseCdf(0.5));
        }

        // Cdf is accurate to ~1e-16 absolute near 1, so the round trip x -> p -> x is only
        // meaningful for x <= 8 on the upper side. Tolerance: 1e-12 relative to max(1,|x|)
        // (the lower tail amplifies the 1e-14 relative error of Cdf by x, hence 1e-12).
        [Fact]
        public void RoundTripInverseOfCdf()
        {
            for (double x = -37.0; x <= 8.0; x += 0.0625)
            {
                double back = NormalDistribution.InverseCdf(NormalDistribution.Cdf(x));
                // Upper side: Cdf(x) = 1 - tail has absolute error ~2e-16, which maps to
                // 2e-16 / Pdf(x) in x.
                double tol = 1e-12 * Math.Max(1.0, Math.Abs(x)) + (x > 0 ? 4e-16 / NormalDistribution.Pdf(x) : 0.0);
                Assert.True(Math.Abs(back - x) <= tol, $"x={x}: got {back:R}");
            }
        }

        // Relative 1e-14 on p (plus 1e-15 * x^2: an x error of relative size e moves p by
        // x^2 * e relative and AS241 reaches e ~ 1e-15 in practice, i.e. the conditioning of the
        // round trip in the deep tail) over a log-spaced grid, lower tail and mirrored upper side
        // (upper side: p near 1 cannot hold 1e-14 relative on 1-p, so compare p itself).
        [Fact]
        public void CdfOfInverseCdfRecoversP()
        {
            for (int k = 0; k <= 300; k++)
            {
                double p = Math.Pow(10.0, -k) * 0.5; // 0.5 .. 0.5e-300
                if (p == 0.0) continue;
                double x = NormalDistribution.InverseCdf(p);
                double back = NormalDistribution.Cdf(x);
                Assert.True(Math.Abs(back - p) <= (1e-14 + 1e-15 * x * x) * p, $"p={p:E}: rel={Math.Abs(back - p) / p:E}");
                // Intermediate points between decades.
                double p2 = p * 0.37;
                if (p2 > 0.0)
                {
                    double x2 = NormalDistribution.InverseCdf(p2);
                    double back2 = NormalDistribution.Cdf(x2);
                    Assert.True(Math.Abs(back2 - p2) <= (1e-14 + 1e-15 * x2 * x2) * p2, $"p={p2:E}: back={back2:R} rel={Math.Abs(back2 - p2) / p2:E}");
                }
                // Upper side: absolute 1e-15 (Cdf near 1 is absolute-accurate only).
                double pu = 1.0 - p;
                double backu = NormalDistribution.Cdf(NormalDistribution.InverseCdf(pu));
                Assert.True(Math.Abs(backu - pu) <= 2e-16, $"pu={pu:R}: back={backu:R}");
            }
        }

        [Fact]
        public void IsAntisymmetricAroundHalf()
        {
            foreach (double p in new[] { 1e-300, 1e-100, 1e-20, 1e-10, 1e-3, 0.02425, 0.1, 0.2, 0.3, 0.4, 0.5 })
            {
                double lo = NormalDistribution.InverseCdf(p);
                double hi = NormalDistribution.InverseCdf(1.0 - p);
                // 1 - p is exactly representable only for p >= ~0.07; for tiny p it rounds
                // to 1 or nearby, so check where it is exact enough, and always check sign flip.
                if (p >= 0.1) Assert.True(Math.Abs(lo + hi) <= 1e-15 * Math.Max(1.0, Math.Abs(lo)), $"p={p}: {lo:R} {hi:R}");
                else Assert.True(hi >= 0.0 && lo < 0.0);
            }
        }

        [Fact]
        public void IsStrictlyIncreasing()
        {
            double prev = double.NegativeInfinity;
            // Covers all three AS241 branches and the junctions at q=+-0.425 and r=5.
            for (int i = 1; i < 4000; i++)
            {
                double p = i / 4000.0;
                double x = NormalDistribution.InverseCdf(p);
                Assert.True(x > prev, $"p={p}");
                prev = x;
            }
            prev = double.NegativeInfinity;
            for (int k = 300; k >= 1; k--)
            {
                double x = NormalDistribution.InverseCdf(Math.Pow(10.0, -k));
                Assert.True(x > prev, $"k={k}");
                prev = x;
            }
        }

        [Fact]
        public void BranchJunctionsAreContinuous()
        {
            // p = 0.075 (|q| = 0.425) and p = exp(-25) (r = 5).
            foreach (double p in new[] { 0.075, Math.Exp(-25.0) })
            {
                double below = NormalDistribution.InverseCdf(Math.BitDecrement(p));
                double above = NormalDistribution.InverseCdf(Math.BitIncrement(p));
                Assert.True(Math.Abs(above - below) <= 1e-13, $"p={p}");
            }
        }

        [Fact]
        public void EdgeCases()
        {
            Assert.Equal(double.NegativeInfinity, NormalDistribution.InverseCdf(0.0));
            Assert.Equal(double.PositiveInfinity, NormalDistribution.InverseCdf(1.0));
            Assert.True(double.IsNaN(NormalDistribution.InverseCdf(-1e-300)));
            Assert.True(double.IsNaN(NormalDistribution.InverseCdf(-0.1)));
            Assert.True(double.IsNaN(NormalDistribution.InverseCdf(1.0000000000000002)));
            Assert.True(double.IsNaN(NormalDistribution.InverseCdf(2.0)));
            Assert.True(double.IsNaN(NormalDistribution.InverseCdf(double.NaN)));
            Assert.True(double.IsNaN(NormalDistribution.InverseCdf(double.PositiveInfinity)));
            Assert.True(double.IsNaN(NormalDistribution.InverseCdf(double.NegativeInfinity)));
            // Smallest subnormal stays finite.
            double x = NormalDistribution.InverseCdf(double.Epsilon);
            Assert.True(x < -38.0 && !double.IsInfinity(x));
        }
    }
}
