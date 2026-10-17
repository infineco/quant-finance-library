using System;
using Xunit;
using static InFine.QuantFinance.Risk.Tests.TestHelpers;

namespace InFine.QuantFinance.Risk.Tests
{
    /// <summary>Sample moments, correlation conversions, annualisation and returns from prices.</summary>
    public class CovarianceTests
    {
        [Fact]
        public void HandComputedSampleCovariance()
        {
            // y = 2x exactly: x = 1,2,3 -> var 1 (unbiased) or 2/3 (MLE); y = 2,4,6 -> 4 or 8/3; cov 2 or 4/3.
            var r = new double[,] { { 1, 2 }, { 2, 4 }, { 3, 6 } };
            Assert.Equal(new[] { 2.0, 4.0 }, Covariance.Mean(r));
            Close(new double[,] { { 1, 2 }, { 2, 4 } }, Covariance.Sample(r), 1e-15, "unbiased");
            Close(new double[,] { { 2.0 / 3, 4.0 / 3 }, { 4.0 / 3, 8.0 / 3 } }, Covariance.Sample(r, unbiased: false), 1e-15, "mle");
            Covariance.Sample(r, out double[] mean);
            Assert.Equal(new[] { 2.0, 4.0 }, mean);
        }

        [Fact]
        public void SampleMatchesPythonReferenceForBothDivisors()
        {
            Close(ReferenceData.SampleUnbiased, Covariance.Sample(ReferenceData.Returns), 1e-13, "unbiased");
            Close(ReferenceData.SampleMle, Covariance.Sample(ReferenceData.Returns, unbiased: false), 1e-13, "mle");
        }

        [Fact]
        public void UnbiasedIsMleScaledByTOverTMinusOne()
        {
            double[,] x = RandomReturns(40, 5, 1);
            double[,] u = Covariance.Sample(x), m = Covariance.Sample(x, unbiased: false);
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 5; j++)
                    Assert.Equal(m[i, j] * 40.0 / 39.0, u[i, j], 15);
        }

        [Fact]
        public void SampleCovarianceIsAccurateWhenTheMeanDwarfsTheDispersion()
        {
            // Naive E[x^2] - E[x]^2 would lose everything at offset 1e9; the two-pass centring does not.
            var r = new double[,] { { 1e9 + 1 }, { 1e9 + 2 }, { 1e9 + 3 }, { 1e9 + 4 } };
            Assert.Equal(5.0 / 3.0, Covariance.Sample(r)[0, 0], 12);
        }

        [Theory]
        [InlineData(50, 6)]
        [InlineData(5, 12)] // N > T: rank deficient but still positive semi-definite
        public void SampleCovarianceIsSymmetricPositiveSemidefinite(int t, int n)
        {
            double[,] s = Covariance.Sample(RandomReturns(t, n, 7));
            Assert.Equal(0.0, InFine.QuantFinance.Numerics.Matrix.MaxAsymmetry(s));
            Assert.True(MinEigenvalue(s) >= -1e-18, $"min eigenvalue {MinEigenvalue(s):R}");
        }

        [Fact]
        public void CorrelationVolatilityAndBackRoundTrip()
        {
            var cov = new double[,] { { 4, 3, 0 }, { 3, 9, -1.5 }, { 0, -1.5, 1 } };
            double[] vols = Covariance.Volatilities(cov);
            Assert.Equal(new[] { 2.0, 3.0, 1.0 }, vols);
            double[,] corr = Covariance.ToCorrelation(cov);
            Close(new double[,] { { 1, 0.5, 0 }, { 0.5, 1, -0.5 }, { 0, -0.5, 1 } }, corr, 1e-15, "corr");
            Close(cov, Covariance.FromCorrelation(corr, vols), 1e-15, "round trip");
        }

        [Fact]
        public void CorrelationOfSampleCovarianceMatchesThePythonReference()
        {
            double[,] corr = Covariance.ToCorrelation(Covariance.Sample(ReferenceData.Returns));
            double[,] s = ReferenceData.SampleUnbiased;
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    Assert.Equal(s[i, j] / Math.Sqrt(s[i, i] * s[j, j]), corr[i, j], 13);
        }

        [Fact]
        public void CorrelationIsClampedAndRejectsZeroVariance()
        {
            // Off-diagonal one ulp above sigma_i sigma_j must not produce a correlation above 1.
            double c = Covariance.ToCorrelation(new double[,] { { 1, 1.0000000000000002 }, { 1.0000000000000002, 1 } })[0, 1];
            Assert.Equal(1.0, c);
            Assert.Throws<ArgumentException>(() => Covariance.ToCorrelation(new double[,] { { 1, 0 }, { 0, 0 } }));
            Assert.Throws<ArgumentException>(() => Covariance.Volatilities(new double[,] { { -1, 0 }, { 0, 1 } }));
        }

        [Fact]
        public void AnnualisationScalesCovarianceByPeriodsAndVolatilityByItsSquareRoot()
        {
            var cov = new double[,] { { 1e-4, 2e-5 }, { 2e-5, 4e-4 } };
            double[,] a = Covariance.Annualise(cov, 252);
            Assert.Equal(252e-4, a[0, 0], 15);
            Assert.Equal(252 * 2e-5, a[0, 1], 15);
            Assert.Equal(0.01 * Math.Sqrt(252), Covariance.AnnualiseVolatility(0.01, 252), 15);
            double[] v = Covariance.AnnualiseVolatilities(Covariance.Volatilities(cov), 252);
            Assert.Equal(Math.Sqrt(a[1, 1]), v[1], 14);
            Assert.Throws<ArgumentOutOfRangeException>(() => Covariance.Annualise(cov, 0));
        }

        [Fact]
        public void InvalidReturnsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => Covariance.Sample(new double[1, 3]));
            Assert.Throws<ArgumentException>(() => Covariance.Sample(new double[,] { { 1, double.NaN }, { 2, 3 } }));
            Assert.Throws<ArgumentNullException>(() => Covariance.Sample(null!));
        }

        // ---- returns from prices --------------------------------------------------------------------------------

        [Fact]
        public void SimpleAndLogReturnsMatchHandValues()
        {
            var p = new double[,] { { 100, 50 }, { 110, 45 }, { 99, 45 } };
            double[,] s = Returns.Simple(p);
            Close(new double[,] { { 0.1, -0.1 }, { -0.1, 0.0 } }, s, 1e-15, "simple");
            double[,] l = Returns.Log(p);
            Assert.Equal(Math.Log(1.1), l[0, 0], 15);
            Assert.Equal(Math.Log(0.9), l[1, 0], 15);
            Assert.Equal(Math.Log(0.9), l[0, 1], 15);
            Assert.Equal(0.0, l[1, 1]);
            Assert.Equal(2, s.GetLength(0));
        }

        [Fact]
        public void LogReturnsSumToTheLogOfThePriceRatioAndSimpleReturnsCompound()
        {
            double[] p = { 100, 103, 99.5, 101.25, 98 };
            double sumLog = 0.0, growth = 1.0;
            foreach (double r in Returns.Log(p)) sumLog += r;
            foreach (double r in Returns.Simple(p)) growth *= 1.0 + r;
            Assert.Equal(Math.Log(98.0 / 100.0), sumLog, 14);
            Assert.Equal(0.98, growth, 14);
        }

        [Fact]
        public void ReturnsRejectNonPositivePricesAndShortSeries()
        {
            Assert.Throws<ArgumentException>(() => Returns.Log(new[] { 1.0, 0.0 }));
            Assert.Throws<ArgumentException>(() => Returns.Simple(new[] { 1.0, -2.0 }));
            Assert.Throws<ArgumentException>(() => Returns.Simple(new[] { 1.0 }));
            Assert.Throws<ArgumentException>(() => Returns.Log(new double[,] { { 1, 2 } }));
            Assert.Throws<ArgumentException>(() => Returns.Log(new double[,] { { 1, double.NaN }, { 1, 2 } }));
        }
    }
}
