using System;
using InFine.QuantFinance.ClosedForm;
using Xunit;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    /// <summary>
    /// Cody's erf, erfc and erfcx, and the centred inverse normal CDF, against values computed with 300-digit
    /// decimal arithmetic (Python decimal: erf Maclaurin series, Laplace continued fraction for erfc in the tail).
    /// </summary>
    public class ErrorFunctionTests
    {
        static void Close(double expected, double actual, double rel, string what)
        {
            Assert.True(Math.Abs(expected - actual) <= rel * Math.Abs(expected),
                $"{what}: expected {expected:R}, got {actual:R}, relative error {Math.Abs(expected - actual) / Math.Abs(expected):E2}");
        }

        [Theory]
        [InlineData(-3.0, 1.9999779095030015)]
        [InlineData(-0.3, 1.3286267594591274)]
        [InlineData(0.1, 0.887537083981715)]
        [InlineData(0.46875, 0.507386526782062)]
        [InlineData(0.5, 0.4795001221869535)]
        [InlineData(2.0, 0.004677734981047266)]
        [InlineData(4.0, 1.541725790028002e-08)]
        [InlineData(4.5, 1.9661604415428876e-10)]
        [InlineData(10.0, 2.088487583762545e-45)]
        [InlineData(26.0, 5.663192408856143e-296)]
        public void ErfcMatchesHighPrecisionValues(double x, double expected)
        {
            Close(expected, NormalDistribution.Erfc(x), 1e-14, $"erfc({x})");
        }

        [Theory]
        [InlineData(-5.0, 144009798674.66104)]
        [InlineData(-0.3, 1.4537492328427655)]
        [InlineData(0.2, 0.8090195199015807)]
        [InlineData(1.0, 0.427583576155807)]
        [InlineData(3.0, 0.17900115118138996)]
        [InlineData(5.0, 0.11070463773306863)]
        [InlineData(30.0, 0.01879588886141675)]
        [InlineData(100.0, 0.005641613782989433)]
        public void ErfcxMatchesHighPrecisionValues(double x, double expected)
        {
            Close(expected, NormalDistribution.Erfcx(x), 1e-14, $"erfcx({x})");
        }

        [Theory]
        [InlineData(1e-10, 1.1283791670955126e-10)]
        [InlineData(0.3, 0.3286267594591274)]
        [InlineData(1.0, 0.8427007929497149)]
        [InlineData(3.0, 0.9999779095030014)]
        public void ErfMatchesHighPrecisionValuesAndIsOdd(double x, double expected)
        {
            Close(expected, NormalDistribution.Erf(x), 1e-15, $"erf({x})");
            Assert.Equal(-NormalDistribution.Erf(x), NormalDistribution.Erf(-x));
        }

        [Fact]
        public void ErfcxLimitsAndConsistency()
        {
            // erfcx(x) ~ 1/(x sqrt(pi)) (1 - 1/(2x^2)) for large x; no overflow or underflow.
            double x = 1e6;
            Close(1.0 / (x * Math.Sqrt(Math.PI)) * (1 - 0.5 / (x * x)), NormalDistribution.Erfcx(x), 1e-15, "erfcx(1e6)");
            Assert.Equal(double.MaxValue, NormalDistribution.Erfcx(-27.0));
            Assert.Equal(1.0, NormalDistribution.Erfcx(0.0));
            Assert.Equal(0.0, NormalDistribution.Erfc(27.0));
            Assert.True(double.IsNaN(NormalDistribution.Erfcx(double.NaN)));
            Assert.True(double.IsNaN(NormalDistribution.Erfc(double.NaN)));
            // Cdf(x) = erfc(-x / sqrt 2) / 2 on both sides (moderate x: the rounding of x / sqrt 2 is amplified by x^2)
            foreach (double z in new[] { -5.0, -0.2, 0.0, 0.7, 3.0 })
                Close(NormalDistribution.Cdf(z), 0.5 * NormalDistribution.Erfc(-z / Math.Sqrt(2.0)), 2e-15, $"Phi({z})");
        }

        [Fact]
        public void InverseCdfCentredIsAccurateForTinyOffsets()
        {
            // Phi^-1(1/2 + q) = sqrt(2 pi) q (1 + pi q^2 / 3 + ...): exact to double for |q| <= 1e-6.
            foreach (double q in new[] { 1e-300, 1e-20, 1e-12, -3e-8, 1e-6 })
                Close(Math.Sqrt(2 * Math.PI) * q * (1 + Math.PI * q * q / 3), NormalDistribution.InverseCdfCentred(q), 5e-16, $"q={q}");
            // Inverse of erf(x / sqrt 2) / 2 = Phi(x) - 1/2 across the whole range, both branches.
            foreach (double q in new[] { 1e-3, 0.1, 0.3, 0.425, -0.43, 0.45, 0.49, 0.4999999 })
            {
                double x = NormalDistribution.InverseCdfCentred(q);
                Close(q, 0.5 * NormalDistribution.Erf(x / Math.Sqrt(2.0)), 4e-15, $"q={q}");
            }
            Assert.Equal(double.PositiveInfinity, NormalDistribution.InverseCdfCentred(0.5));
            Assert.Equal(double.NegativeInfinity, NormalDistribution.InverseCdfCentred(-0.5));
            Assert.True(double.IsNaN(NormalDistribution.InverseCdfCentred(0.6)));
            Assert.True(double.IsNaN(NormalDistribution.InverseCdfCentred(double.NaN)));
        }
    }
}
