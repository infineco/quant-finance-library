using System;
using Xunit;
using static InFine.QuantFinance.Risk.Tests.TestHelpers;

namespace InFine.QuantFinance.Risk.Tests
{
    /// <summary>Historical VaR / ES: hand-computed order statistics, weights, filtering and the Euler decomposition.</summary>
    public class HistoricalRiskTests
    {
        // Losses 1..20 in a scrambled order, as P&L (gain positive): pnl = -loss.
        static readonly double[] TwentyPnl = ScrambledPnl();

        static double[] ScrambledPnl()
        {
            int[] order = { 7, 19, 3, 12, 1, 16, 9, 20, 5, 14, 2, 18, 8, 11, 4, 15, 10, 6, 17, 13 };
            var pnl = new double[order.Length];
            for (int i = 0; i < pnl.Length; i++) pnl[i] = -order[i];
            return pnl;
        }

        // ---- quantile and Acerbi-Tasche ES by hand (n = 20, losses 1..20) -----------------------------------------

        [Theory]
        // alpha, VaR = L_(ceil(20 alpha)), ES: worst 20(1-alpha) observations, boundary observation fractionally weighted.
        [InlineData(0.95, 19.0, 20.0)]                   // n alpha = 19 exactly: tail mass is one observation, the worst
        [InlineData(0.90, 18.0, 19.5)]                   // n alpha = 18: tail is {19, 20}
        [InlineData(0.92, 19.0, (0.6 * 19 + 20) / 1.6)]  // n alpha = 18.4: L_(19) enters with weight 0.6 of 1.6 observations
        [InlineData(0.99, 20.0, 20.0)]                   // n alpha = 19.8: fraction 0.2 of one observation, all on the worst
        [InlineData(0.50, 10.0, 15.5)]                   // tail is 11..20
        [InlineData(0.975, 20.0, 20.0)]                  // n alpha = 19.5
        [InlineData(0.80, 16.0, 18.5)]                   // n alpha = 16: tail {17..20}
        public void QuantileAndFractionalWeightEsOnTwentySortedValues(double alpha, double var, double es)
        {
            VarEs m = HistoricalRisk.Measures(TwentyPnl, alpha);
            Assert.Equal(var, m.Var, 12);
            Assert.Equal(es, m.ExpectedShortfall, 12);
            Assert.Equal(var, HistoricalRisk.ValueAtRisk(TwentyPnl, alpha), 12);
            Assert.Equal(es, HistoricalRisk.ExpectedShortfall(TwentyPnl, alpha), 12);
        }

        [Fact]
        public void FractionalTailWeightMakesEsContinuousInConfidence()
        {
            // The naive "mean of losses beyond VaR" jumps; the Acerbi-Tasche ES moves continuously with alpha.
            double previous = HistoricalRisk.ExpectedShortfall(TwentyPnl, 0.50);
            for (double alpha = 0.51; alpha < 0.999; alpha += 0.01)
            {
                double es = HistoricalRisk.ExpectedShortfall(TwentyPnl, alpha);
                Assert.True(es >= previous - 1e-12 && es - previous < 0.5, $"alpha {alpha}: {previous} -> {es}");
                previous = es;
            }
        }

        [Fact]
        public void LinearQuantileInterpolatesOrderStatistics()
        {
            // Type 7: position (n-1) alpha = 18.05 (zero based) between L=19 and L=20 -> 19.05; alpha = 0.5: 9.5 -> 10.5.
            Assert.Equal(19.05, HistoricalRisk.ValueAtRisk(TwentyPnl, 0.95, QuantileMethod.Linear), 12);
            Assert.Equal(10.5, HistoricalRisk.ValueAtRisk(TwentyPnl, 0.50, QuantileMethod.Linear), 12);
            // ES is unaffected by the VaR definition.
            Assert.Equal(20.0, HistoricalRisk.Measures(TwentyPnl, 0.95, QuantileMethod.Linear).ExpectedShortfall, 12);
        }

        [Fact]
        public void LossesAreReportedPositiveAndGainsNegative()
        {
            // Gains 1..4 are losses -1..-4, ascending -4, -3, -2, -1; k = ceil(4 * 0.75) = 3 -> VaR = -2; the tail is the worst one: -1.
            VarEs m = HistoricalRisk.Measures(new[] { 1.0, 2.0, 3.0, 4.0 }, 0.75);
            Assert.Equal(-2.0, m.Var, 15);
            Assert.Equal(-1.0, m.ExpectedShortfall, 15);
            // A pure loss is a positive number.
            Assert.Equal(5.0, HistoricalRisk.Measures(new[] { -5.0, -5.0 }, 0.9).Var, 15);
        }

        [Fact]
        public void EsIsAtLeastVarOnRandomSamples()
        {
            var rng = new System.Random(11);
            foreach (int n in new[] { 7, 20, 101, 250 })
                foreach (double alpha in new[] { 0.5, 0.9, 0.95, 0.975, 0.99 })
                {
                    var pnl = new double[n];
                    for (int i = 0; i < n; i++) pnl[i] = rng.NextDouble() - 0.45;
                    VarEs m = HistoricalRisk.Measures(pnl, alpha);
                    Assert.True(m.ExpectedShortfall >= m.Var - 1e-12, $"n {n} alpha {alpha}: ES {m.ExpectedShortfall} < VaR {m.Var}");
                }
        }

        // ---- weights ----------------------------------------------------------------------------------------------

        [Fact]
        public void UniformWeightsReproduceTheUnweightedMeasures()
        {
            double[] w = new double[20];
            Array.Fill(w, 3.7); // normalised internally
            foreach (double alpha in new[] { 0.5, 0.8, 0.9, 0.92, 0.95, 0.99 })
            {
                VarEs a = HistoricalRisk.Measures(TwentyPnl, alpha);
                VarEs b = HistoricalRisk.WeightedMeasures(TwentyPnl, w, alpha);
                Assert.Equal(a.Var, b.Var, 12);
                Assert.Equal(a.ExpectedShortfall, b.ExpectedShortfall, 11);
            }
        }

        [Fact]
        public void AgeWeightsAreTheBrwGeometricWeights()
        {
            // n = 3, lambda = 1/2: (1/4, 1/2, 1) / 1.75 = (1, 2, 4) / 7, newest last.
            double[] w = HistoricalRisk.AgeWeights(3, 0.5);
            Assert.Equal(1.0 / 7, w[0], 15);
            Assert.Equal(2.0 / 7, w[1], 15);
            Assert.Equal(4.0 / 7, w[2], 15);
            // Closed form lambda^(n-1-i) (1-lambda) / (1-lambda^n) on a longer window; lambda = 1 is uniform.
            double[] w250 = HistoricalRisk.AgeWeights(250, 0.98);
            for (int i = 0; i < 250; i++)
                Assert.Equal(Math.Pow(0.98, 249 - i) * 0.02 / (1 - Math.Pow(0.98, 250)), w250[i], 13);
            Assert.All(HistoricalRisk.AgeWeights(8, 1.0), x => Assert.Equal(0.125, x, 15));
        }

        [Theory]
        [InlineData(0.95, 10.0, 10.0)]
        [InlineData(0.90, 3.0, 23.0 / 3.0)]
        public void AgeWeightedMeasuresByHand(double alpha, double var, double es)
        {
            // Losses oldest first (10, 1, 2, 3), lambda = 1/2 -> weights (1, 2, 4, 8)/15. Ascending: 1 (2/15), 2 (4/15),
            // 3 (8/15), 10 (1/15); cumulative 2/15, 6/15, 14/15, 1.
            // alpha 0.95: first cumulative >= 0.95 is the loss 10; tail mass 0.05 sits entirely on it.
            // alpha 0.90: VaR is 3 (cumulative 0.9333); tail 0.1 = 0.0333 on loss 3 and 0.0667 on loss 10 -> (0.1 + 0.6667)/0.1.
            double[] pnl = { -10, -1, -2, -3 };
            VarEs m = HistoricalRisk.AgeWeightedMeasures(pnl, 0.5, alpha);
            Assert.Equal(var, m.Var, 12);
            Assert.Equal(es, m.ExpectedShortfall, 12);
        }

        [Fact]
        public void ZeroWeightScenariosDoNotAffectTheMeasures()
        {
            double[] pnl = { -5, 100, -1, -2, -3 };
            double[] w = { 1, 0, 1, 1, 1 };
            VarEs a = HistoricalRisk.WeightedMeasures(pnl, w, 0.75);
            VarEs b = HistoricalRisk.Measures(new[] { -5.0, -1, -2, -3 }, 0.75);
            Assert.Equal(b.Var, a.Var, 12);
            Assert.Equal(b.ExpectedShortfall, a.ExpectedShortfall, 12);
        }

        // ---- scenarios x positions --------------------------------------------------------------------------------

        [Fact]
        public void PortfolioPnlIsPositionsTimesReturns()
        {
            var r = new double[,] { { 0.01, -0.02 }, { -0.03, 0.04 } };
            double[] pnl = HistoricalRisk.PortfolioPnl(r, new[] { 100.0, -50.0 });
            Assert.Equal(100 * 0.01 + 50 * 0.02, pnl[0], 14);
            Assert.Equal(-100 * 0.03 - 50 * 0.04, pnl[1], 14);
            VarEs m = HistoricalRisk.Measures(r, new[] { 100.0, -50.0 }, 0.5);
            Assert.Equal(HistoricalRisk.Measures(pnl, 0.5).Var, m.Var, 15);
        }

        // ---- volatility scaling (Hull-White) ----------------------------------------------------------------------

        [Fact]
        public void VolatilityScalingMatchesTheHandRecursion()
        {
            // r = (0.01, -0.02, 0.03, -0.01), lambda 0.9, seed = mean square of the first two returns.
            var r = new double[,] { { 0.01 }, { -0.02 }, { 0.03 }, { -0.01 } };
            double v0 = (1e-4 + 4e-4) / 2;                  // forecast variance for day 1
            double v1 = 0.9 * v0 + 0.1 * 1e-4;              // for day 2
            double v2 = 0.9 * v1 + 0.1 * 4e-4;              // for day 3
            double v3 = 0.9 * v2 + 0.1 * 9e-4;              // for day 4
            double v4 = 0.9 * v3 + 0.1 * 1e-4;              // for day 5 (tomorrow)
            double[,] s = HistoricalRisk.VolatilityScaledReturns(r, 0.9, 2);
            Assert.Equal(0.01 * Math.Sqrt(v4 / v0), s[0, 0], 15);
            Assert.Equal(-0.02 * Math.Sqrt(v4 / v1), s[1, 0], 15);
            Assert.Equal(0.03 * Math.Sqrt(v4 / v2), s[2, 0], 15);
            Assert.Equal(-0.01 * Math.Sqrt(v4 / v3), s[3, 0], 15);
        }

        [Fact]
        public void ConstantVolatilityIsLeftUnchangedAndScalingIsLinear()
        {
            var r = new double[40, 2];
            for (int i = 0; i < 40; i++)
            {
                r[i, 0] = (i % 2 == 0 ? 1 : -1) * 0.01;
                r[i, 1] = (i % 3 == 0 ? 1 : -1) * 0.02;
            }
            // Constant |r| keeps the EWMA variance at r^2 (also from the seed), so every ratio is 1.
            Close(r, HistoricalRisk.VolatilityScaledReturns(r, 0.94, 10), 1e-13, "constant vol");

            double[,] x = RandomReturns(80, 3, 5);
            var x3 = new double[80, 3];
            for (int i = 0; i < 80; i++) for (int j = 0; j < 3; j++) x3[i, j] = 3.0 * x[i, j];
            double[,] a = HistoricalRisk.VolatilityScaledReturns(x, 0.94, 20), b = HistoricalRisk.VolatilityScaledReturns(x3, 0.94, 20);
            for (int i = 0; i < 80; i++) for (int j = 0; j < 3; j++) Assert.Equal(3.0 * a[i, j], b[i, j], 13);
        }

        [Fact]
        public void RisingVolatilityRaisesTheFilteredVar()
        {
            // 150 calm days then 30 turbulent ones: raw history understates today's risk, filtering reflects it.
            var rng = new System.Random(2);
            var r = new double[180, 1];
            for (int i = 0; i < 180; i++) r[i, 0] = (i < 150 ? 0.005 : 0.03) * (rng.NextDouble() * 2 - 1) * Math.Sqrt(3);
            double[] pos = { 1.0 };
            double raw = HistoricalRisk.Measures(r, pos, 0.95).Var;
            double filtered = HistoricalRisk.VolatilityScaledMeasures(r, pos, 0.95, 0.94, 20).Var;
            Assert.True(filtered > 1.5 * raw, $"raw {raw}, filtered {filtered}");
            // Same call with age weighting stays well formed and finite.
            Assert.True(double.IsFinite(HistoricalRisk.VolatilityScaledMeasures(r, pos, 0.95, 0.94, 20, ageLambda: 0.99).Var));
        }

        // ---- sub-additivity (the classic two-bond example) --------------------------------------------------------

        [Fact]
        public void VarIsNotSubadditiveButEsIsOnTheTwoBondExample()
        {
            // Each bond defaults independently with probability 4% (P&L -100) and otherwise pays +2.
            // Single bond at 95%: P(default) = 4% < 5%, so VaR = -2 (a gain), ES = (0.01 * -2 + 0.04 * 100)/0.05 = 79.6.
            double[] oneP = { -100, 2 };
            double[] oneW = { 0.04, 0.96 };
            VarEs one = HistoricalRisk.WeightedMeasures(oneP, oneW, 0.95);
            Assert.Equal(-2.0, one.Var, 12);
            Assert.Equal((0.01 * -2 + 0.04 * 100) / 0.05, one.ExpectedShortfall, 12);

            // Two bonds: outcomes (both default 0.0016, exactly one 0.0768, none 0.9216).
            double[] twoP = { -200, -98, -98, 4 };
            double[] twoW = { 0.04 * 0.04, 0.04 * 0.96, 0.96 * 0.04, 0.96 * 0.96 };
            VarEs two = HistoricalRisk.WeightedMeasures(twoP, twoW, 0.95);
            // P(at least one default) = 7.84% > 5%: VaR = 98, far above the sum of the stand-alone VaRs (-4).
            Assert.Equal(98.0, two.Var, 12);
            Assert.True(two.Var > 2 * one.Var + 50, "VaR must violate sub-additivity here");
            // ES: tail 0.05 = 0.0484 at loss 98 plus 0.0016 at loss 200.
            Assert.Equal((0.0484 * 98 + 0.0016 * 200) / 0.05, two.ExpectedShortfall, 11);
            Assert.True(two.ExpectedShortfall < 2 * one.ExpectedShortfall, "ES must be sub-additive");
        }

        [Fact]
        public void EsIsSubadditiveOnRandomPortfoliosWhileVarIsNotAlways()
        {
            // ES(a + b) <= ES(a) + ES(b) on the same scenario set, for any sample size (equal weights).
            var rng = new System.Random(21);
            foreach (int n in new[] { 13, 40, 97 })
                for (int trial = 0; trial < 20; trial++)
                {
                    var a = new double[n];
                    var b = new double[n];
                    var s = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        a[i] = rng.NextDouble() < 0.1 ? -rng.NextDouble() * 10 : rng.NextDouble();
                        b[i] = rng.NextDouble() < 0.1 ? -rng.NextDouble() * 10 : rng.NextDouble();
                        s[i] = a[i] + b[i];
                    }
                    foreach (double alpha in new[] { 0.9, 0.95, 0.99 })
                        Assert.True(HistoricalRisk.ExpectedShortfall(s, alpha) <=
                            HistoricalRisk.ExpectedShortfall(a, alpha) + HistoricalRisk.ExpectedShortfall(b, alpha) + 1e-12);
                }
        }

        // ---- Euler decomposition ----------------------------------------------------------------------------------

        [Fact]
        public void HistoricalComponentEsSumsExactlyToEs()
        {
            double[,] r = RandomReturns(500, 4, 17);
            double[] pos = { 1000, -400, 250, 700 };
            foreach (double alpha in new[] { 0.9, 0.95, 0.975, 0.99 })
            {
                RiskDecomposition d = HistoricalRisk.Decompose(r, pos, alpha);
                Assert.Equal(d.Total.ExpectedShortfall, Sum(d.ComponentEs), 10);
                Assert.Equal(HistoricalRisk.Measures(r, pos, alpha).ExpectedShortfall, d.Total.ExpectedShortfall, 12);
                Assert.Equal(1.0, Sum(d.PercentEs), 12);
                // component = position x marginal (the conditional tail expectation of the asset's own loss).
                for (int j = 0; j < 4; j++) Assert.Equal(pos[j] * d.MarginalEs[j], d.ComponentEs[j], 12);
            }
        }

        [Fact]
        public void ComponentEsIsTheConditionalTailExpectationOfEachPosition()
        {
            // 8 scenarios, alpha = 0.75: tail mass 2 scenarios = the two worst portfolio losses (no fractions).
            var r = new double[,]
            {
                { -0.02,  0.01 }, { 0.01, 0.00 }, { -0.05, -0.01 }, { 0.02, 0.02 },
                { -0.01, -0.04 }, { 0.00, 0.01 }, { 0.03, -0.02 }, { -0.03, 0.03 },
            };
            double[] pos = { 100, 200 };
            // Losses = -(100 r0 + 200 r1): 0, -1, 7, -6, 9, -2, 1, -3 -> the worst two are scenarios 4 (9) and 2 (7): ES = 8.
            RiskDecomposition d = HistoricalRisk.Decompose(r, pos, 0.75, bandwidth: 1.0); // narrow kernel: the sample is tiny
            Assert.Equal(8.0, d.Total.ExpectedShortfall, 12);
            Assert.Equal(0.5 * (100 * 0.01 + 100 * 0.05), d.ComponentEs[0], 12);   // asset 0 loses 1 and 5 in the tail scenarios
            Assert.Equal(0.5 * (200 * 0.04 + 200 * 0.01), d.ComponentEs[1], 12);
            Assert.Equal(1.0, d.Total.Var, 12);                                    // sorted losses -6 -3 -2 -1 0 1 7 9; k = ceil(8 * 0.75) = 6 -> 1
        }

        [Fact]
        public void ComponentVarSumsToVarAndSingleAssetGetsEverything()
        {
            double[,] r = RandomReturns(800, 3, 4);
            double[] pos = { 500, 300, -200 };
            foreach (double alpha in new[] { 0.9, 0.95, 0.99 })
            {
                RiskDecomposition d = HistoricalRisk.Decompose(r, pos, alpha);
                Assert.Equal(d.Total.Var, Sum(d.ComponentVar), 10);
                Assert.Equal(1.0, Sum(d.PercentVar), 12);
            }
            RiskDecomposition single = HistoricalRisk.Decompose(r, new[] { 1000.0, 0.0, 0.0 }, 0.95);
            Assert.Equal(single.Total.Var, single.ComponentVar[0], 10);
            Assert.Equal(0.0, single.ComponentVar[1], 12);
            Assert.Equal(single.Total.ExpectedShortfall, single.ComponentEs[0], 10);
        }

        [Fact]
        public void ComponentRisksAgreeWithTheDeltaNormalOnALargeGaussianSample()
        {
            // Large iid Gaussian sample with a known covariance: the sample Euler allocation converges to the parametric one
            // (statistical tolerances; deterministic seed).
            const int t = 60000;
            double[,] sigma = { { 0.0004, 0.0001, 0.0 }, { 0.0001, 0.0009, 0.0002 }, { 0.0, 0.0002, 0.0016 } };
            double[,] chol = InFine.QuantFinance.Numerics.Cholesky.Factor(sigma);
            var rng = new System.Random(123);
            var r = new double[t, 3];
            for (int i = 0; i < t; i++)
            {
                double g0 = Gauss(rng), g1 = Gauss(rng), g2 = Gauss(rng);
                r[i, 0] = chol[0, 0] * g0;
                r[i, 1] = chol[1, 0] * g0 + chol[1, 1] * g1;
                r[i, 2] = chol[2, 0] * g0 + chol[2, 1] * g1 + chol[2, 2] * g2;
            }
            double[] pos = { 1000, 600, 400 };
            const double alpha = 0.95;
            RiskDecomposition h = HistoricalRisk.Decompose(r, pos, alpha);
            RiskDecomposition p = ParametricRisk.Decompose(pos, sigma, alpha);
            Assert.True(Math.Abs(h.Total.Var / p.Total.Var - 1) < 0.02);
            Assert.True(Math.Abs(h.Total.ExpectedShortfall / p.Total.ExpectedShortfall - 1) < 0.02);
            for (int j = 0; j < 3; j++)
            {
                Assert.True(Math.Abs(h.ComponentEs[j] / p.ComponentEs[j] - 1) < 0.05, $"ES component {j}: {h.ComponentEs[j]} vs {p.ComponentEs[j]}");
                Assert.True(Math.Abs(h.ComponentVar[j] / p.ComponentVar[j] - 1) < 0.08, $"VaR component {j}: {h.ComponentVar[j]} vs {p.ComponentVar[j]}");
            }
        }

        [Fact]
        public void HistoricalIncrementalRiskRecomputesWithoutThePosition()
        {
            double[,] r = RandomReturns(400, 3, 8);
            double[] pos = { 300, 500, -150 };
            RiskDecomposition d = HistoricalRisk.Decompose(r, pos, 0.95);
            for (int j = 0; j < 3; j++)
            {
                double[] reduced = (double[])pos.Clone();
                reduced[j] = 0.0;
                VarEs without = HistoricalRisk.Measures(r, reduced, 0.95);
                Assert.Equal(d.Total.Var - without.Var, d.IncrementalVar[j], 11);
                Assert.Equal(d.Total.ExpectedShortfall - without.ExpectedShortfall, d.IncrementalEs[j], 11);
            }
        }

        [Fact]
        public void WeightedDecompositionStillAddsUp()
        {
            double[,] r = RandomReturns(300, 3, 31);
            double[] pos = { 100, 200, 300 };
            double[] w = HistoricalRisk.AgeWeights(300, 0.99);
            RiskDecomposition d = HistoricalRisk.Decompose(r, pos, 0.975, w);
            VarEs expected = HistoricalRisk.WeightedMeasures(HistoricalRisk.PortfolioPnl(r, pos), w, 0.975);
            Assert.Equal(expected.Var, d.Total.Var, 12);
            Assert.Equal(expected.ExpectedShortfall, d.Total.ExpectedShortfall, 12);
            Assert.Equal(d.Total.ExpectedShortfall, Sum(d.ComponentEs), 10);
            Assert.Equal(d.Total.Var, Sum(d.ComponentVar), 10);
        }

        [Fact]
        public void ComponentVarNeedsALossAndAKernel()
        {
            var gains = new double[,] { { 0.01 }, { 0.02 }, { 0.03 }, { 0.04 } };
            Assert.Throws<InvalidOperationException>(() => HistoricalRisk.Decompose(gains, new[] { 1.0 }, 0.75));
            double[,] r = RandomReturns(50, 2, 1);
            Assert.Throws<ArgumentException>(() => HistoricalRisk.Decompose(r, new[] { 1.0, 1.0 }, 0.9, HistoricalRisk.AgeWeights(50, 0.99), QuantileMethod.Linear));
            Assert.Throws<ArgumentOutOfRangeException>(() => HistoricalRisk.Decompose(r, new[] { 1.0, 1.0 }, 0.9, bandwidth: 0.0));
        }

        // ---- validation -------------------------------------------------------------------------------------------

        [Fact]
        public void RejectsBadArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => HistoricalRisk.Measures(TwentyPnl, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => HistoricalRisk.Measures(TwentyPnl, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => HistoricalRisk.Measures(TwentyPnl, double.NaN));
            Assert.Throws<ArgumentException>(() => HistoricalRisk.Measures(Array.Empty<double>(), 0.99));
            Assert.Throws<ArgumentException>(() => HistoricalRisk.Measures(new[] { 1.0, double.NaN }, 0.99));
            Assert.Throws<ArgumentException>(() => HistoricalRisk.WeightedMeasures(TwentyPnl, new double[19], 0.9));
            Assert.Throws<ArgumentException>(() => HistoricalRisk.WeightedMeasures(TwentyPnl, new double[20], 0.9)); // zero sum
            Assert.Throws<ArgumentOutOfRangeException>(() => HistoricalRisk.AgeWeights(10, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => HistoricalRisk.AgeWeights(10, 1.1));
            Assert.Throws<ArgumentException>(() => HistoricalRisk.PortfolioPnl(new double[3, 2], new double[3]));
            Assert.Throws<ArgumentException>(() => HistoricalRisk.VolatilityScaledReturns(new double[5, 1], 0.94, 3)); // zero seed variance
        }

        static double Sum(double[] x)
        {
            double s = 0;
            foreach (double v in x) s += v;
            return s;
        }

        static double Gauss(System.Random rng) => Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble());
    }
}
