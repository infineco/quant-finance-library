using System;
using System.Linq;
using InFine.QuantFinance.ClosedForm;
using Xunit;
using static InFine.QuantFinance.Volatility.Tests.TestHelpers;

namespace InFine.QuantFinance.Volatility.Tests
{
    /// <summary>
    /// Raw SVI slice formulas and the butterfly / calendar diagnostics, against finite differences, Black-76 prices and
    /// Gatheral-Jacquier's published arbitrage-violating example.
    /// </summary>
    public class SviSliceTests
    {
        static readonly SviRawParameters Typical = new SviRawParameters(0.01, 0.08, -0.4, 0.05, 0.2);

        [Fact]
        public void DerivativesMatchCentralDifferences()
        {
            // h = 1e-4: truncation O(h^2 w''') ~ 1e-9 for the first derivative; the second difference carries
            // rounding eps w / h^2 ~ 1e-9 plus truncation O(h^2) ~ 1e-7 relative.
            foreach (var svi in new[] { Typical, Vogt })
                for (double k = -1.5; k <= 1.5; k += 0.125)
                {
                    var (first, second) = CentralDifferences(svi.TotalVariance, k, 1e-4);
                    Close(first, svi.TotalVarianceDerivative(k), 0, 1e-8, $"w'({k})");
                    Close(second, svi.TotalVarianceSecondDerivative(k), 1e-6, 1e-7, $"w''({k})");
                }
        }

        [Fact]
        public void MinimumAndWingsFollowTheClosedForms()
        {
            var s = Typical;
            double kStar = s.M - s.Rho * s.Sigma / Math.Sqrt(1 - s.Rho * s.Rho);
            Close(s.MinimumTotalVariance, s.TotalVariance(kStar), 0, 1e-15, "w(k*)");
            Assert.True(Math.Abs(s.TotalVarianceDerivative(kStar)) < 1e-15);
            // Asymptotic slopes b (1 -+ rho): w(k) / |k| at |k| = 1e6 within O(1/k).
            Close(s.LeftWingSlope, s.TotalVariance(-1e6) / 1e6, 0, 1e-6, "left wing");
            Close(s.RightWingSlope, s.TotalVariance(1e6) / 1e6, 0, 1e-6, "right wing");
            Assert.True(s.HasAdmissibleWings);
            Assert.False(new SviRawParameters(0.01, 1.6, 0.3, 0, 0.1).HasAdmissibleWings); // 1.6 * 1.3 > 2
        }

        [Fact]
        public void DomainIsValidated()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SviRawParameters(0.01, -0.1, 0, 0, 0.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SviRawParameters(0.01, 0.1, 1.0, 0, 0.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SviRawParameters(0.01, 0.1, 0, 0, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SviRawParameters(double.NaN, 0.1, 0, 0, 0.1));
            // a + b sigma sqrt(1 - rho^2) = -0.05 + 0.1 * 0.2 = -0.03 < 0
            Assert.Throws<ArgumentOutOfRangeException>(() => new SviRawParameters(-0.05, 0.1, 0, 0, 0.2));
            // exactly zero minimum is allowed
            var zeroMin = new SviRawParameters(-0.02, 0.1, 0, 0, 0.2);
            Close(0.0, zeroMin.MinimumTotalVariance, 0, 1e-17, "min");
        }

        [Fact]
        public void DensityMatchesTheSecondStrikeDerivativeOfBlackPrices()
        {
            // Lemma 2.2's proof: p(k) is the density of k = ln(S_T / F), so the strike density d^2 C / dK^2 at
            // K = e^k (unit forward, undiscounted) is p(k) / K. Independent reference: central second difference of
            // Black-76 prices with the slice's vols, h = 1e-4 in strike. Prices are
            // ~0.1 and exact to ~1e-16, so rounding is 1e-16 / h^2 ~ 1e-8; truncation O(h^2 C'''') ~ 1e-8.
            foreach (var svi in new[] { Typical, Vogt })
                foreach (double k in new[] { -1.0, -0.4, 0.0, 0.3, 0.7, 0.88, 1.1, 1.4 })
                {
                    double strike = Math.Exp(k), h = 1e-4;
                    double C(double K) => Black76Kernels.NormalisedPrice(1, 1.0, K, Math.Sqrt(svi.TotalVariance(Math.Log(K))));
                    double fd = (C(strike + h) - 2 * C(strike) + C(strike - h)) / (h * h);
                    Close(fd * strike, ArbitrageChecks.RiskNeutralDensity(svi, k), 0, 5e-7, $"p({k}) for {svi}");
                }
        }

        [Fact]
        public void VogtSliceHasTheNegativeDensityOfGatheralJacquierFigure1()
        {
            // Example 3.1 / Figure 1 of Gatheral-Jacquier: g < 0 on a band right of the money. Independently located
            // by bisection on g (Python, double precision): g < 0 exactly on (0.642408, 1.256913), minimum
            // g = -0.032864 at k = 0.8793. Grid spacing 1e-3, so the first/last violations sit within 1e-3 of the roots.
            var grid = ArbitrageChecks.Grid(-1.5, 1.5, 3001);
            var result = ArbitrageChecks.CheckButterfly(Vogt, grid);
            Assert.False(result.IsArbitrageFree);
            Close(0.642408, result.Violations.First(), 0, 1e-3, "first violation");
            Close(1.256913, result.Violations.Last(), 0, 1e-3, "last violation");
            Assert.Equal(result.Violations.Count, grid.Count(k => k > 0.642408 && k < 1.256913));
            Close(-0.032864, result.MinimumDensity, 0, 1e-5, "min g");
            Close(0.8793, result.ArgMinimum, 0, 1e-3, "argmin g");

            // The negative density is a real arbitrage: a butterfly of Black prices at k = 0.88 has negative value.
            double K = Math.Exp(0.88), dK = 0.02;
            double Call(double strike) => Black76Kernels.NormalisedPrice(1, 1.0, strike, Math.Sqrt(Vogt.TotalVariance(Math.Log(strike))));
            Assert.True(Call(K - dK) - 2 * Call(K) + Call(K + dK) < 0.0);

            // ... and the typical slice is clean on the same grid.
            Assert.True(ArbitrageChecks.CheckButterfly(Typical, grid).IsArbitrageFree);
        }

        [Fact]
        public void CalendarCheckFindsCrossingSlices()
        {
            var grid = ArbitrageChecks.Grid(-2, 2, 401);
            var earlier = Typical;
            var later = new SviRawParameters(0.03, 0.1, -0.35, 0.05, 0.25);
            Assert.True(ArbitrageChecks.CheckCalendar(earlier, later, grid).IsArbitrageFree);

            // Same ATM level but a flatter smile: the later slice dips below the earlier one in the wings.
            var flatter = new SviRawParameters(0.03, 0.02, -0.4, 0.05, 0.2);
            var crossing = ArbitrageChecks.CheckCalendar(earlier, flatter, grid);
            Assert.False(crossing.IsArbitrageFree);
            Assert.True(crossing.MinimumDifference < 0);
            foreach (double k in crossing.Violations) Assert.True(flatter.TotalVariance(k) < earlier.TotalVariance(k));
        }
    }
}
