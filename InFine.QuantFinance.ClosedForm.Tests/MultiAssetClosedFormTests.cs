using System;
using InFine.QuantFinance.ClosedForm;
using Xunit;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    /// <summary>
    /// Bivariate normal distribution and two-asset closed forms (Margrabe, Stulz, geometric basket) against exact
    /// identities, one-asset limits and numerical integration written independently in the tests.
    /// </summary>
    public class MultiAssetClosedFormTests
    {
        const double R = 0.03, T = 1.5;

        // -------------------------------------------------------------- bivariate normal

        /// <summary>M(a, b, rho) = int_{-inf}^{a} phi(x) Phi((b - rho x) / sqrt(1 - rho^2)) dx, composite Simpson.</summary>
        static double BivariateByQuadrature(double a, double b, double rho)
        {
            double lo = -40.0, hi = Math.Min(a, 40.0);
            if (hi <= lo) return 0.0;
            int n = 40000;
            double h = (hi - lo) / n, s = 0.0, sr = Math.Sqrt(1.0 - rho * rho);
            for (int i = 0; i <= n; ++i)
            {
                double x = lo + i * h;
                double f = NormalDistribution.Pdf(x) * NormalDistribution.Cdf((b - rho * x) / sr);
                s += (i == 0 || i == n ? 1.0 : (i % 2 == 1 ? 4.0 : 2.0)) * f;
            }
            return s * h / 3.0;
        }

        [Theory]
        [InlineData(-0.999)]
        [InlineData(-0.95)]
        [InlineData(-0.7)]
        [InlineData(-0.2)]
        [InlineData(0.0)]
        [InlineData(0.1)]
        [InlineData(0.5)]
        [InlineData(0.9)]
        [InlineData(0.93)]
        [InlineData(0.999)]
        public void BivariateNormalAtTheOriginIsSheppardsFormula(double rho)
        {
            Assert.Equal(0.25 + Math.Asin(rho) / (2.0 * Math.PI), BivariateNormalDistribution.Cdf(0.0, 0.0, rho), 14);
        }

        [Fact]
        public void BivariateNormalMatchesQuadratureAcrossTheCorrelationRegimes()
        {
            double[] points = { -3.0, -1.2, -0.3, 0.0, 0.4, 1.1, 2.5 };
            double[] rhos = { -0.99, -0.93, -0.8, -0.5, -0.1, 0.0, 0.2, 0.6, 0.92, 0.95, 0.995 };
            double worst = 0.0;
            foreach (double a in points)
                foreach (double b in points)
                    foreach (double rho in rhos)
                        worst = Math.Max(worst, Math.Abs(BivariateNormalDistribution.Cdf(a, b, rho) - BivariateByQuadrature(a, b, rho)));
            Assert.True(worst < 1e-12, $"worst absolute error {worst}");
        }

        [Fact]
        public void BivariateNormalLimitsAndSymmetries()
        {
            Assert.Equal(NormalDistribution.Cdf(0.3) * NormalDistribution.Cdf(-1.1), BivariateNormalDistribution.Cdf(0.3, -1.1, 0.0), 15);
            Assert.Equal(NormalDistribution.Cdf(-0.4), BivariateNormalDistribution.Cdf(0.7, -0.4, 1.0), 15);
            Assert.Equal(NormalDistribution.Cdf(0.7) - NormalDistribution.Cdf(0.4), BivariateNormalDistribution.Cdf(0.7, -0.4, -1.0), 15);
            Assert.Equal(0.0, BivariateNormalDistribution.Cdf(-0.7, -0.4, -1.0));
            // symmetric in (a, b); M(a, b, rho) + M(a, -b, -rho) = Phi(a)
            Assert.Equal(BivariateNormalDistribution.Cdf(0.2, -0.9, 0.6), BivariateNormalDistribution.Cdf(-0.9, 0.2, 0.6), 14);
            Assert.Equal(NormalDistribution.Cdf(0.2), BivariateNormalDistribution.Cdf(0.2, -0.9, 0.95) + BivariateNormalDistribution.Cdf(0.2, 0.9, -0.95), 14);
            // close to the degenerate limits
            Assert.Equal(NormalDistribution.Cdf(-0.4), BivariateNormalDistribution.Cdf(0.7, -0.4, 1.0 - 1e-12), 6);
        }

        // -------------------------------------------------------------- Margrabe

        [Fact]
        public void ExchangeWithADeterministicSecondAssetIsABlackScholesCall()
        {
            // sigma2 = 0: S2(T) = S2 exp((r - q2) T) is a fixed strike, whatever r
            foreach (double rate in new[] { -0.01, 0.0, 0.05 })
            {
                double strike = 95.0 * Math.Exp((rate - 0.01) * T);
                double bs = BlackScholesKernels.Price(1, 100.0, strike, T, rate, 0.02, 0.3);
                Assert.Equal(bs, MultiAssetKernels.ExchangePrice(100.0, 95.0, T, 0.02, 0.01, 0.3, 0.0, 0.7), 12);
            }
        }

        [Fact]
        public void ExchangeParityAndPerfectCorrelation()
        {
            // max(S1 - S2, 0) - max(S2 - S1, 0) = S1 - S2
            double a = MultiAssetKernels.ExchangePrice(100.0, 90.0, T, 0.02, 0.01, 0.25, 0.35, -0.3);
            double b = MultiAssetKernels.ExchangePrice(90.0, 100.0, T, 0.01, 0.02, 0.35, 0.25, -0.3);
            Assert.Equal(100.0 * Math.Exp(-0.02 * T) - 90.0 * Math.Exp(-0.01 * T), a - b, 12);
            // identical volatilities, correlation one: the forwards' difference
            Assert.Equal(Math.Max(100.0 * Math.Exp(-0.02 * T) - 90.0 * Math.Exp(-0.01 * T), 0.0),
                MultiAssetKernels.ExchangePrice(100.0, 90.0, T, 0.02, 0.01, 0.3, 0.3, 1.0), 12);
        }

        // -------------------------------------------------------------- Stulz

        /// <summary>
        /// E[e^{-rT} max(sign (extreme(S1, S2) - K), 0)] conditioning on the driver z1 of S1: given z1, S2 is log-normal and
        /// the payoff splits into vanillas on S2 (Black-76 on the conditional forward), e.g.
        /// (min(x1, S2) - K)^+ = 1{x1 &gt; K} [(S2 - K)^+ - (S2 - x1)^+]; the outer integral over z1 in [-10, 10] is a fine
        /// trapezoidal rule. No bivariate normal and no Stulz term is involved.
        /// </summary>
        static double MinMaxByQuadrature(int sign, bool maximum, double s1, double s2, double k, double q1, double q2,
            double sigma1, double sigma2, double rho)
        {
            int n = 200000;
            double lo = -10.0, h = 20.0 / n, total = 0.0, sq = Math.Sqrt(T);
            double m1 = Math.Log(s1) + (R - q1 - 0.5 * sigma1 * sigma1) * T, m2 = Math.Log(s2) + (R - q2 - 0.5 * sigma2 * sigma2) * T;
            double sd = sigma2 * sq * Math.Sqrt(1.0 - rho * rho);
            for (int i = 0; i <= n; ++i)
            {
                double z1 = lo + i * h;
                double x1 = Math.Exp(m1 + sigma1 * sq * z1);
                double forward = Math.Exp(m2 + sigma2 * sq * rho * z1 + 0.5 * sd * sd);
                double value;
                if (sign > 0 && !maximum)
                    value = x1 > k ? Black76Kernels.NormalisedPrice(1, forward, k, sd) - Black76Kernels.NormalisedPrice(1, forward, x1, sd) : 0.0;
                else if (sign > 0)
                    value = Math.Max(x1 - k, 0.0) + Black76Kernels.NormalisedPrice(1, forward, Math.Max(x1, k), sd);
                else if (!maximum)
                    value = Math.Max(k - x1, 0.0) + Black76Kernels.NormalisedPrice(-1, forward, Math.Min(x1, k), sd);
                else
                    value = x1 < k ? Black76Kernels.NormalisedPrice(-1, forward, k, sd) - Black76Kernels.NormalisedPrice(-1, forward, x1, sd) : 0.0;
                total += (i == 0 || i == n ? 0.5 : 1.0) * NormalDistribution.Pdf(z1) * value;
            }
            return Math.Exp(-R * T) * total * h;
        }

        [Theory]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(-1, false)]
        [InlineData(-1, true)]
        public void StulzMatchesTwoDimensionalQuadrature(int sign, bool maximum)
        {
            foreach (double rho in new[] { -0.6, 0.0, 0.5, 0.9 })
            {
                double exact = MinMaxByQuadrature(sign, maximum, 100.0, 110.0, 100.0, 0.02, 0.0, 0.25, 0.35, rho);
                double stulz = MultiAssetKernels.MinMaxPrice(sign, maximum, 100.0, 110.0, 100.0, T, R, 0.02, 0.0, 0.25, 0.35, rho);
                Assert.True(Math.Abs(exact - stulz) < 1e-6, $"sign {sign}, max {maximum}, rho {rho}: quadrature {exact}, Stulz {stulz}");
            }
        }

        [Fact]
        public void StulzMinPlusMaxIsTheSumOfTheVanillas()
        {
            // max(S1, S2) + min(S1, S2) = S1 + S2 pathwise, so the calls (and the puts) add up to the two vanillas
            foreach (int sign in new[] { 1, -1 })
                foreach (double rho in new[] { -0.8, 0.3, 0.95 })
                    foreach (double k in new[] { 80.0, 100.0, 125.0 })
                    {
                        double sum = MultiAssetKernels.MinMaxPrice(sign, true, 100.0, 95.0, k, T, R, 0.01, 0.03, 0.2, 0.4, rho)
                                   + MultiAssetKernels.MinMaxPrice(sign, false, 100.0, 95.0, k, T, R, 0.01, 0.03, 0.2, 0.4, rho);
                        double vanillas = BlackScholesKernels.Price(sign, 100.0, k, T, R, 0.01, 0.2) + BlackScholesKernels.Price(sign, 95.0, k, T, R, 0.03, 0.4);
                        Assert.Equal(vanillas, sum, 10);
                    }
        }

        [Fact]
        public void StulzWithAVanishingStrikeIsThePresentValueOfTheExtreme()
        {
            double exchange = MultiAssetKernels.ExchangePrice(100.0, 95.0, T, 0.01, 0.03, 0.2, 0.4, 0.3);
            double min = MultiAssetKernels.MinMaxPrice(1, false, 100.0, 95.0, 1e-8, T, R, 0.01, 0.03, 0.2, 0.4, 0.3);
            double max = MultiAssetKernels.MinMaxPrice(1, true, 100.0, 95.0, 1e-8, T, R, 0.01, 0.03, 0.2, 0.4, 0.3);
            Assert.Equal(100.0 * Math.Exp(-0.01 * T) - exchange, min, 7);
            Assert.Equal(95.0 * Math.Exp(-0.03 * T) + exchange, max, 7);
        }

        // -------------------------------------------------------------- geometric basket

        [Fact]
        public void OneAssetGeometricBasketIsBlackScholes()
        {
            foreach (int sign in new[] { 1, -1 })
                Assert.Equal(BlackScholesKernels.Price(sign, 100.0, 105.0, T, R, 0.02, 0.3),
                    MultiAssetKernels.GeometricBasketPrice(sign, 1, new[] { 100.0 }, new[] { 1.0 }, new[] { 0.02 }, new[] { 0.3 }, new[] { 1.0 }, 105.0, T, R), 12);
        }

        [Fact]
        public void GeometricBasketMomentsFollowTheCovariance()
        {
            double[] spots = { 1.0, 1.2, 0.9 }, w = { 0.5, 0.3, 0.2 }, q = { 0.0, 0.01, 0.02 }, s = { 0.2, 0.3, 0.25 };
            double[] c = { 1.0, 0.5, 0.2, 0.5, 1.0, -0.1, 0.2, -0.1, 1.0 };
            double m, v;
            MultiAssetKernels.GeometricBasketLogMoments(3, spots, w, q, s, c, T, R, out m, out v);
            double em = 0.0, ev = 0.0;
            for (int i = 0; i < 3; ++i)
            {
                em += w[i] * (Math.Log(spots[i]) + (R - q[i] - 0.5 * s[i] * s[i]) * T);
                for (int j = 0; j < 3; ++j) ev += w[i] * w[j] * c[3 * i + j] * s[i] * s[j] * T;
            }
            Assert.Equal(em, m, 14);
            Assert.Equal(ev, v, 14);
            // two identical, perfectly correlated assets with weights 1/2 each: the single asset
            Assert.Equal(BlackScholesKernels.Price(1, 100.0, 100.0, T, R, 0.01, 0.3),
                MultiAssetKernels.GeometricBasketPrice(1, 2, new[] { 100.0, 100.0 }, new[] { 0.5, 0.5 }, new[] { 0.01, 0.01 },
                    new[] { 0.3, 0.3 }, new[] { 1.0, 1.0, 1.0, 1.0 }, 100.0, T, R), 12);
        }
    }
}
