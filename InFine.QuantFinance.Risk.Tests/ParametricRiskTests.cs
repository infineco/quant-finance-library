using System;
using InFine.QuantFinance.ClosedForm;
using Xunit;

namespace InFine.QuantFinance.Risk.Tests
{
    /// <summary>Delta-normal and Cornish-Fisher VaR / ES against closed forms, and the parametric Euler decomposition.</summary>
    public class ParametricRiskTests
    {
        // Reference values from Python (statistics.NormalDist), independent of the library's normal functions.
        const double Z99 = 2.326347874040841;
        const double Es99 = 2.665214220345808;     // phi(Z99) / 0.01

        [Fact]
        public void StandardNormalValuesMatchTheClosedForms()
        {
            VarEs m = ParametricRisk.Normal(1.0, 0.99);
            Assert.Equal(Z99, m.Var, 14);
            Assert.Equal(Es99, m.ExpectedShortfall, 13);
            Assert.Equal(Z99, NormalDistribution.InverseCdf(0.99), 14);
            // Other levels: VaR95 = 1.6448536269514722, ES95 = phi(1.6448536...)/0.05 = 2.0627128075074306.
            VarEs m95 = ParametricRisk.Normal(1.0, 0.95);
            Assert.Equal(1.6448536269514722, m95.Var, 14);
            Assert.Equal(2.0627128075074306, m95.ExpectedShortfall, 13);
        }

        [Fact]
        public void VolatilityMeanAndHorizonScaleAsStated()
        {
            // VaR = z sigma sqrt(h) - mu h ; ES = sigma sqrt(h) phi(z)/(1-alpha) - mu h.
            VarEs m = ParametricRisk.Normal(0.02, 0.99, mean: 0.001, horizon: 10);
            Assert.Equal(Z99 * 0.02 * Math.Sqrt(10) - 0.01, m.Var, 14);
            Assert.Equal(Es99 * 0.02 * Math.Sqrt(10) - 0.01, m.ExpectedShortfall, 14);
            Assert.True(ParametricRisk.Normal(0.0, 0.99).Var == 0.0);
            // A positive drift can make VaR negative (reported unclamped).
            Assert.True(ParametricRisk.Normal(0.01, 0.99, mean: 0.1).Var < 0);
        }

        [Fact]
        public void EsExceedsVarAndBothGrowWithConfidence()
        {
            double previousVar = 0, previousEs = 0;
            foreach (double alpha in new[] { 0.9, 0.95, 0.975, 0.99, 0.999 })
            {
                VarEs m = ParametricRisk.Normal(1.0, alpha);
                Assert.True(m.ExpectedShortfall > m.Var && m.Var > previousVar && m.ExpectedShortfall > previousEs);
                (previousVar, previousEs) = (m.Var, m.ExpectedShortfall);
            }
        }

        [Fact]
        public void DeltaNormalByHand()
        {
            // w = (100, 50), Sigma = [[4e-4, 1e-4], [1e-4, 9e-4]]: w'Sigma w = 4 + 2 * 0.5 + 2.25 = 7.25 ... (10^4 * 4e-4 + 2 * 5000 * 1e-4 + 2500 * 9e-4)
            double[,] cov = { { 4e-4, 1e-4 }, { 1e-4, 9e-4 } };
            double[] w = { 100, 50 };
            Assert.Equal(Math.Sqrt(7.25), ParametricRisk.PortfolioVolatility(w, cov), 14);
            VarEs m = ParametricRisk.DeltaNormal(w, cov, 0.99);
            Assert.Equal(6.263883350218453, m.Var, 12);   // Python: z99 * sqrt(7.25)
            Assert.Equal(Es99 * Math.Sqrt(7.25), m.ExpectedShortfall, 12);
            // With expected returns mu = (0.001, 0.002): mu_p = 0.1 + 0.1 = 0.2.
            VarEs withMean = ParametricRisk.DeltaNormal(w, cov, 0.99, new[] { 0.001, 0.002 });
            Assert.Equal(m.Var - 0.2, withMean.Var, 12);
            Assert.Equal(m.ExpectedShortfall - 0.2, withMean.ExpectedShortfall, 12);
        }

        // ---- Cornish-Fisher ---------------------------------------------------------------------------------------

        [Fact]
        public void CornishFisherReducesToTheNormalWithZeroSkewAndExcessKurtosis()
        {
            foreach (double alpha in new[] { 0.9, 0.95, 0.99, 0.999 })
            {
                VarEs n = ParametricRisk.Normal(0.03, alpha, 0.002, 5);
                VarEs cf = ParametricRisk.CornishFisher(0.03, 0.0, 0.0, alpha, 0.002, 5);
                Assert.Equal(n.Var, cf.Var, 14);
                Assert.Equal(n.ExpectedShortfall, cf.ExpectedShortfall, 13);
            }
        }

        [Fact]
        public void CornishFisherValueMatchesTheClosedExpansionAndNumericalTailIntegral()
        {
            // S = -0.5, K = 1, sigma = 1, alpha 0.99: Python evaluation of the expansion and of a Simpson integral of g(v) phi(v).
            VarEs cf = ParametricRisk.CornishFisher(1.0, -0.5, 1.0, 0.99);
            Assert.Equal(2.8337090353393686, cf.Var, 12);
            Assert.Equal(3.490019401660212, cf.ExpectedShortfall, 8);

            // And the same ES by direct numerical integration here (Simpson, 40000 intervals over [-14, u]).
            double u = -Z99;
            double a = -14.0;
            const int n = 40000;
            double h = (u - a) / n, sum = 0.0;
            for (int i = 0; i <= n; i++)
            {
                double v = a + i * h;
                double g = v + (v * v - 1) * -0.5 / 6 + (v * v * v - 3 * v) * 1.0 / 24 - (2 * v * v * v - 5 * v) * 0.25 / 36;
                sum += (i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2) * g * NormalDistribution.Pdf(v);
            }
            Assert.Equal(-(sum * h / 3) / 0.01, cf.ExpectedShortfall, 9);
        }

        [Fact]
        public void NegativeSkewAndFatTailsRaiseTheModifiedVar()
        {
            double normal = ParametricRisk.Normal(1.0, 0.99).Var;
            Assert.True(ParametricRisk.CornishFisher(1.0, -0.5, 0.0, 0.99).Var > normal);   // left skew: bigger loss tail
            Assert.True(ParametricRisk.CornishFisher(1.0, 0.0, 1.0, 0.99).Var > normal);    // fat tails
            Assert.True(ParametricRisk.CornishFisher(1.0, 0.5, 0.0, 0.99).Var < normal);    // right skew: smaller
        }

        [Fact]
        public void CornishFisherScalesWithHorizonLikeIidPeriods()
        {
            VarEs a = ParametricRisk.CornishFisher(0.01, -0.6, 2.0, 0.99, 0.0005, 4);
            VarEs b = ParametricRisk.CornishFisher(0.02, -0.3, 0.5, 0.99, 0.002, 1);
            Assert.Equal(b.Var, a.Var, 13);
            Assert.Equal(b.ExpectedShortfall, a.ExpectedShortfall, 13);
        }

        [Theory]
        [InlineData(0.0, 0.0, 0.99, true)]
        [InlineData(-0.5, 1.0, 0.99, true)]
        [InlineData(-1.5, 6.0, 0.99, true)]
        [InlineData(0.0, -1.5, 0.99, true)]
        [InlineData(-2.0, 0.0, 0.99, false)]    // very skewed: g is decreasing near the quantile
        [InlineData(0.0, 8.0, 0.99, false)]     // g'(0) = 1 - K/8 = 0
        public void ValidityRegionOfTheExpansion(double skew, double kurt, double alpha, bool valid)
        {
            Assert.Equal(valid, ParametricRisk.IsCornishFisherValid(skew, kurt, alpha));
            if (!valid) Assert.Throws<ArgumentException>(() => ParametricRisk.CornishFisher(1.0, skew, kurt, alpha));
            else Assert.True(double.IsFinite(ParametricRisk.CornishFisher(1.0, skew, kurt, alpha).Var));
        }

        // ---- decomposition ----------------------------------------------------------------------------------------

        static double[,] Covariance(int n, int seed)
        {
            // A = random factors; Sigma = A A' / n + diag: symmetric positive definite.
            var rng = new System.Random(seed);
            var a = new double[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) a[i, j] = 0.02 * (rng.NextDouble() - 0.5);
            var s = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                    for (int k = 0; k < n; k++) s[i, j] += a[i, k] * a[j, k];
                s[i, i] += 1e-4 * (1 + i);
            }
            return s;
        }

        [Theory]
        [InlineData(0.99, 1.0, false)]
        [InlineData(0.95, 1.0, true)]
        [InlineData(0.975, 10.0, true)]
        public void ComponentsSumToTheTotalToMachinePrecision(double alpha, double horizon, bool withMean)
        {
            const int n = 6;
            double[,] cov = Covariance(n, 3);
            double[] w = { 1000, -300, 450, 20, -700, 150 };
            double[]? mu = withMean ? new[] { 0.0004, 0.0002, -0.0001, 0.0003, 0.0005, 0.0 } : null;
            RiskDecomposition d = ParametricRisk.Decompose(w, cov, alpha, mu, horizon);
            VarEs total = ParametricRisk.DeltaNormal(w, cov, alpha, mu, horizon);
            Assert.Equal(total.Var, d.Total.Var, 14);
            Assert.Equal(total.ExpectedShortfall, d.Total.ExpectedShortfall, 14);
            double sv = 0, se = 0, pv = 0, pe = 0;
            for (int i = 0; i < n; i++)
            {
                sv += d.ComponentVar[i];
                se += d.ComponentEs[i];
                pv += d.PercentVar[i];
                pe += d.PercentEs[i];
            }
            Assert.True(Math.Abs(sv - total.Var) <= 1e-12 * Math.Max(1.0, Math.Abs(total.Var)), $"VaR: {sv} vs {total.Var}");
            Assert.True(Math.Abs(se - total.ExpectedShortfall) <= 1e-12 * Math.Max(1.0, Math.Abs(total.ExpectedShortfall)));
            Assert.Equal(1.0, pv, 12);
            Assert.Equal(1.0, pe, 12);
        }

        [Fact]
        public void MarginalRiskIsTheDerivativeWithRespectToThePosition()
        {
            const int n = 4;
            double[,] cov = Covariance(n, 9);
            double[] w = { 800, -250, 400, 100 };
            double[] mu = { 0.0003, 0.0001, 0.0002, -0.0002 };
            RiskDecomposition d = ParametricRisk.Decompose(w, cov, 0.99, mu, 3.0);
            for (int i = 0; i < n; i++)
            {
                double eps = 1e-3;
                double[] up = (double[])w.Clone(), dn = (double[])w.Clone();
                up[i] += eps;
                dn[i] -= eps;
                VarEs a = ParametricRisk.DeltaNormal(up, cov, 0.99, mu, 3.0), b = ParametricRisk.DeltaNormal(dn, cov, 0.99, mu, 3.0);
                Assert.Equal((a.Var - b.Var) / (2 * eps), d.MarginalVar[i], 7);
                Assert.Equal((a.ExpectedShortfall - b.ExpectedShortfall) / (2 * eps), d.MarginalEs[i], 7);
                Assert.Equal(w[i] * d.MarginalVar[i], d.ComponentVar[i], 12);
            }
        }

        [Fact]
        public void TwoAssetDecompositionAndIncrementalVarByHand()
        {
            // Uncorrelated assets, sigma = (2, 3) per unit, positions (10, 10): sigma_p = sqrt(400 + 900) = sqrt(1300).
            double[,] cov = { { 4, 0 }, { 0, 9 } };
            double[] w = { 10, 10 };
            RiskDecomposition d = ParametricRisk.Decompose(w, cov, 0.99);
            double sp = Math.Sqrt(1300);
            Assert.Equal(Z99 * 40 / sp, d.MarginalVar[0], 12);      // z (Sigma w)_0 / sigma_p = z * 40 / sp
            Assert.Equal(Z99 * 90 / sp, d.MarginalVar[1], 12);
            Assert.Equal(Z99 * 400 / sp, d.ComponentVar[0], 12);
            Assert.Equal(400 / 1300.0, d.PercentVar[0], 14);        // share of variance
            // Incremental: portfolio vs the other asset alone.
            Assert.Equal(Z99 * (sp - 30), d.IncrementalVar[0], 12);
            Assert.Equal(Z99 * (sp - 20), d.IncrementalVar[1], 12);
            Assert.Equal(Es99 * (sp - 30), d.IncrementalEs[0], 12);
            // Incremental VaR is not additive: it sums to less than the total here (sub-additive, diversification).
            Assert.True(d.IncrementalVar[0] + d.IncrementalVar[1] < d.Total.Var);
        }

        [Fact]
        public void PerfectlyCorrelatedPositionsShareRiskInProportionToTheirSize()
        {
            double[,] cov = { { 1, 1 }, { 1, 1 } };
            RiskDecomposition d = ParametricRisk.Decompose(new[] { 30.0, 10.0 }, cov, 0.99);
            Assert.Equal(0.75, d.PercentVar[0], 14);
            Assert.Equal(0.25, d.PercentEs[1], 14);
            Assert.Equal(Z99 * 40, d.Total.Var, 12);
        }

        [Fact]
        public void RejectsBadInput()
        {
            double[,] cov = { { 1, 0 }, { 0, 1 } };
            Assert.Throws<InvalidOperationException>(() => ParametricRisk.Decompose(new[] { 0.0, 0.0 }, cov, 0.99));
            Assert.Throws<ArgumentException>(() => ParametricRisk.DeltaNormal(new[] { 1.0, 2.0 }, new double[,] { { 1, 0 }, { 0, -1 } }, 0.99)); // w'Sigma w = -3
            Assert.Throws<ArgumentException>(() => ParametricRisk.DeltaNormal(new[] { 1.0, 1.0 }, new double[,] { { 1, 0.5 }, { 0, 1 } }, 0.99)); // asymmetric
            Assert.Throws<ArgumentException>(() => ParametricRisk.DeltaNormal(new[] { 1.0 }, cov, 0.99));
            Assert.Throws<ArgumentOutOfRangeException>(() => ParametricRisk.Normal(-1.0, 0.99));
            Assert.Throws<ArgumentOutOfRangeException>(() => ParametricRisk.Normal(1.0, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ParametricRisk.Normal(1.0, 0.99, 0.0, 0.0));
            Assert.Throws<ArgumentException>(() => ParametricRisk.DeltaNormal(new[] { 1.0, 1.0 }, cov, 0.99, new[] { 0.1 }));
        }
    }
}
