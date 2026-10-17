using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Random.DotNet;
using Xunit;

namespace InFine.QuantFinance.Random.Tests
{
    public class UnitTest_PhiloxInverseCdf
    {
        [Fact]
        public void Values_AreInverseCdfOf52BitUniforms()
        {
            const int count = 37; // odd counts are allowed
            var z = new double[count];
            new PhiloxGenerator(123, pathIndex: 4, stream: 2)
                .GenerateGaussians(GaussianTransformEnum.InverseCumulativeDistributionFunction, 0.25, 3.0, count, z);

            var words = new PhiloxGenerator(123, pathIndex: 4, stream: 2);
            for (int k = 0; k < count; ++k)
            {
                uint hi = words.NextUInt32();
                uint lo = words.NextUInt32();
                double expected = 0.25 + 3.0 * NormalDistribution.InverseCdf(Philox4x32x10.ToUniformOpen52(hi, lo));
                Assert.Equal(expected, z[k]);
            }
        }

        [Fact]
        public void Moments_AreStandardNormal()
        {
            const int n = 1_000_000;
            var z = new double[n];
            new PhiloxGenerator(20111112).GenerateGaussians(GaussianTransformEnum.InverseCumulativeDistributionFunction, 0.0, 1.0, n, z);

            double sum = 0.0, below = 0.0;
            foreach (double x in z)
            {
                Assert.False(double.IsNaN(x) || double.IsInfinity(x));
                sum += x;
                if (x < -1.0) below += 1.0;
            }
            double mean = sum / n;
            double ss = 0.0;
            foreach (double x in z) ss += (x - mean) * (x - mean);
            double variance = ss / (n - 1);

            // 4 sigma: sd(mean) = 1/sqrt(n); sd(var) = sqrt(2/n); sd(P[Z < -1]) = sqrt(p (1 - p) / n).
            Assert.InRange(mean, -4.0 / Math.Sqrt(n), 4.0 / Math.Sqrt(n));
            Assert.InRange(variance, 1.0 - 4 * Math.Sqrt(2.0 / n), 1.0 + 4 * Math.Sqrt(2.0 / n));
            double p = NormalDistribution.Cdf(-1.0);
            double sdP = Math.Sqrt(p * (1 - p) / n);
            Assert.InRange(below / n, p - 4 * sdP, p + 4 * sdP);
        }
    }
}
