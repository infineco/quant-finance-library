using System;
using InFine.QuantFinance.ClosedForm;
using Xunit;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    public class NormalDistributionTests
    {
        // Reference values of Phi(x) = erfc(-x/sqrt(2))/2 computed with 900-digit decimal
        // arithmetic (Python decimal, Taylor series of erf), rounded to 25 significant digits.
        public static readonly (double X, double Phi)[] Reference =
        {
            (-37, 5.7255712225245771049024018e-300),
            (-20, 2.7536241186062337371341782e-89),
            (-10, 7.6198530241605254505396775e-24),
            (-8, 6.2209605742717838743573061e-16),
            (-5, 2.8665157187919391185379502e-07),
            (-3, 1.3498980316300945771906816e-03),
            (-2, 2.2750131948179208585258948e-02),
            (-1, 1.5865525393145704646791216e-01),
            (-0.5, 3.0853753872598688179351711e-01),
            (0, 5.0e-01),
            (0.5, 6.9146246127401311820648289e-01),
            (1, 8.4134474606854292577651222e-01),
            (2, 9.7724986805182079141474105e-01),
            (3, 9.9865010196836989653235150e-01),
            (5, 9.9999971334842807646481333e-01),
            (8, 9.9999999999999933386618522e-01),
        };

        [Fact]
        public void LowerTailAndCentreHaveRelativeErrorBelow1e14()
        {
            foreach (var (x, phi) in Reference)
            {
                if (x > 0) continue;
                double got = NormalDistribution.Cdf(x);
                Assert.True(Math.Abs(got - phi) / phi < 1e-14, $"Cdf({x}) = {got:R}, expected {phi:R}");
            }
        }

        [Fact]
        public void UpperRegionHasAbsoluteErrorBelow1e16()
        {
            foreach (var (x, phi) in Reference)
            {
                if (x <= 0) continue;
                double got = NormalDistribution.Cdf(x);
                Assert.True(Math.Abs(got - phi) < 1e-16 + 1e-16 * phi, $"Cdf({x}) = {got:R}, expected {phi:R}");
            }
        }

        // Independent oracle (not kernel code): Maclaurin series of erf for |x| < 1.5,
        // Laplace continued fraction with many levels beyond; accurate to ~1e-15 relative.
        static double OracleLowerTail(double a)
        {
            const double R = 0.39894228040143267794;
            double pdf = R * Math.Exp(-0.5 * a * a);
            if (a < 1.5)
            {
                double term = a, sum = a;
                for (int k = 1; k < 60; ++k) { term *= a * a / (2 * k + 1); sum += term; }
                return 0.5 - pdf * sum;
            }
            int levels = 12 + (int)(400.0 / (a * a));
            double b = a;
            for (int k = levels; k >= 1; --k) b = a + k / b;
            return pdf / b;
        }

        [Fact]
        public void AgreesWithSeriesAndContinuedFractionOracleOnGrid()
        {
            for (double x = -37.5; x <= 0; x += 0.0625)
            {
                double expected = OracleLowerTail(-x);
                double got = NormalDistribution.Cdf(x);
                Assert.True(Math.Abs(got - expected) / expected < 1e-13, $"x={x}: {got:R} vs {expected:R}");
            }
        }

        [Fact]
        public void SymmetryHolds()
        {
            for (double x = -8; x <= 8; x += 0.0625)
                Assert.True(Math.Abs(NormalDistribution.Cdf(x) + NormalDistribution.Cdf(-x) - 1.0) < 1e-15, $"x={x}");
        }

        [Fact]
        public void IsMonotonicOnFineGrid()
        {
            double prev = NormalDistribution.Cdf(-40);
            for (double x = -39.99; x <= 40; x += 0.01)
            {
                double c = NormalDistribution.Cdf(x);
                Assert.True(c >= prev, $"not monotone at x={x}");
                prev = c;
            }
        }

        [Fact]
        public void LimitsAndSpecialValues()
        {
            Assert.Equal(0.0, NormalDistribution.Cdf(-40));
            Assert.Equal(1.0, NormalDistribution.Cdf(40));
            Assert.Equal(0.0, NormalDistribution.Cdf(double.NegativeInfinity));
            Assert.Equal(1.0, NormalDistribution.Cdf(double.PositiveInfinity));
            Assert.Equal(0.5, NormalDistribution.Cdf(0.0));
            Assert.True(double.IsNaN(NormalDistribution.Cdf(double.NaN)));
        }

        [Fact]
        public void PdfMatchesClosedFormAndIsDerivativeOfCdf()
        {
            Assert.Equal(0.3989422804014327, NormalDistribution.Pdf(0), 15);
            Assert.Equal(0.24197072451914337, NormalDistribution.Pdf(1), 15); // exp(-1/2)/sqrt(2 pi)
            const double h = 1e-5;
            for (double x = -4; x <= 4; x += 0.5)
            {
                double fd = (NormalDistribution.Cdf(x + h) - NormalDistribution.Cdf(x - h)) / (2 * h);
                Assert.True(Math.Abs(fd - NormalDistribution.Pdf(x)) < 1e-9, $"x={x}");
            }
        }

        [Fact]
        public void CndDelegatesToCdf()
        {
            foreach (var (x, _) in Reference)
                Assert.Equal(NormalDistribution.Cdf(x), BlackScholesMertonPrimitive.CND(x));
        }

        [Fact]
        public void CallPutSatisfiesPutCallParity()
        {
            double[] spots = { 50, 90, 100, 110, 200 };
            double[] strikes = { 80, 100, 120 };
            double[] rates = { -0.01, 0.0, 0.03, 0.10 };
            double[] sigmas = { 0.05, 0.2, 0.6 };
            double[] maturities = { 0.1, 1.0, 5.0 };
            foreach (double s in spots)
            foreach (double k in strikes)
            foreach (double r in rates)
            foreach (double v in sigmas)
            foreach (double t in maturities)
            {
                BlackScholesMertonPrimitive.CallPut(out double call, out double put, s, k, r, v, t);
                double parity = call - put - (s - k * Math.Exp(-r * t));
                Assert.True(Math.Abs(parity) < 1e-12, $"S={s} K={k} r={r} v={v} T={t}: {parity}");
                Assert.True(call >= 0 && put >= 0);
            }
        }
    }
}
