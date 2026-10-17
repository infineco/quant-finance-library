using System;
using System.Collections.Generic;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Random;
using InFine.QuantFinance.Random.DotNet;
using InFine.QuantFinance.Random.QuasiRandom;
using Xunit;

namespace InFine.QuantFinance.Risk.Tests
{
    /// <summary>
    /// Monte Carlo VaR by full revaluation: the scenario generator against the library's generators, the simulated
    /// distributions against closed forms (delta-normal for linear books, the exact distribution of one option under a
    /// one-factor move), determinism across chunkings, Sobol against Philox, and the Euler decomposition.
    /// </summary>
    /// <remarks>
    /// Statistical assertions are stated in standard errors of the Monte Carlo estimator (4 or 5 SE with fixed seeds,
    /// so they are deterministic and fail only if the estimator is biased). For an alpha-quantile of a sample of M,
    /// <c>SE(VaR) = sqrt(alpha (1 - alpha) / M) / f(VaR)</c> with <c>f</c> the loss density; for the tail average,
    /// <c>SE(ES) = sqrt((Var(L | L &gt;= VaR) + alpha (ES - VaR)^2) / (M (1 - alpha)))</c>.
    /// </remarks>
    public class FullRevaluationTests
    {
        const double Alpha = 0.99;

        // ---- scenario generator ----------------------------------------------------------------------------------

        [Fact]
        public void PhiloxNormalsAreThePhiloxGeneratorInverseCdfStream()
        {
            // L = I, additive factors at 0: the scenario levels are the normals z themselves.
            const int f = 5;
            const ulong seed = 0xDEADBEEF12345678UL;
            double[] l = Identity(f);
            var kind = new int[f];
            for (int j = 0; j < f; j++) kind[j] = FullRevaluationKernels.AdditiveFactor;
            var levels = new double[f + 3];
            var expected = new double[f];
            foreach (int s in new[] { 0, 1, 7, 123456 })
            {
                FullRevaluationKernels.GenerateScenario(s, FullRevaluationKernels.PhiloxNormals, seed, FullRevaluationKernels.DefaultPhiloxStream,
                    Array.Empty<uint>(), Array.Empty<uint>(), f, l, kind, new double[f], new double[f], levels, 3);
                new PhiloxGenerator(seed, (uint)s, FullRevaluationKernels.DefaultPhiloxStream)
                    .GenerateGaussians(GaussianTransformEnum.InverseCumulativeDistributionFunction, 0.0, 1.0, f, expected);
                for (int j = 0; j < f; j++) Assert.Equal(expected[j], levels[3 + j]);
            }
        }

        [Fact]
        public void SobolNormalsAreTheScrambledSobolGeneratorPoints()
        {
            const int f = 6;
            var factors = new RiskFactor[f];
            for (int j = 0; j < f; j++) factors[j] = new RiskFactor("x" + j, 0.0, RiskFactorKind.Additive);
            var engine = new FullRevaluationEngine(factors, IdentityMatrix(f), new[] { new SpotPosition(0, 1.0) });
            var options = new MonteCarloVarOptions { Generator = ScenarioGenerator.ScrambledSobol, Seed = 99, SobolReplication = 3, Scenarios = 64 };
            FullRevaluationBatch batch = engine.CreateBatch(options);

            // The host generator walks the Gray-code sequence incrementally; the kernel uses random access.
            var reference = new SobolGenerator(f, SobolScramblingKind.LinearMatrixAndDigitalShift, 99, 3, skipZeroPoint: false);
            var expected = new double[f];
            var levels = new double[f];
            for (int s = 0; s < 64; s++)
            {
                reference.NextGaussians(expected);
                FullRevaluationKernels.GenerateScenario(s, batch.Generator, batch.Seed, batch.Stream, batch.SobolDirections, batch.SobolShifts,
                    f, batch.CholeskyLower, batch.FactorKind, batch.BaseLevel, batch.HalfVariance, levels, 0);
                for (int j = 0; j < f; j++) Assert.Equal(expected[j], levels[j]);
            }
        }

        [Fact]
        public void LogNormalFactorsAreMartingalesWithTheRequestedLogCovariance()
        {
            // Unit P&L of a spot position is X - X0, so the scenario levels are recovered exactly.
            double[,] cov = { { 4e-4, 1.2e-4, -0.8e-4 }, { 1.2e-4, 2.25e-4, 0.5e-4 }, { -0.8e-4, 0.5e-4, 9e-4 } };
            double[] s0 = { 100.0, 50.0, 20.0 };
            const double h = 25.0;
            const int m = 100_000;
            var factors = new RiskFactor[3];
            var positions = new PortfolioPosition[3];
            for (int j = 0; j < 3; j++)
            {
                factors[j] = new RiskFactor("S" + j, s0[j], RiskFactorKind.LogNormal);
                positions[j] = new SpotPosition(j, 1.0);
            }
            MonteCarloVarResult r = new FullRevaluationEngine(factors, cov, positions)
                .Run(new MonteCarloVarOptions { Scenarios = m, Horizon = h, Seed = 5, KeepInstrumentPnl = true });
            double[,] unit = r.InstrumentPnl!;

            var logs = new double[m, 3];
            for (int j = 0; j < 3; j++)
            {
                double mean = 0.0, sq = 0.0;
                for (int s = 0; s < m; s++)
                {
                    double ratio = (s0[j] + unit[s, j]) / s0[j];
                    mean += ratio;
                    sq += ratio * ratio;
                    logs[s, j] = Math.Log(ratio);
                }
                mean /= m;
                double se = Math.Sqrt((sq / m - mean * mean) / m);
                Assert.True(Math.Abs(mean - 1.0) < 4.0 * se, $"E[S{j}/S0] = {mean}, SE {se}");
            }
            double[,] sample = Covariance.Sample(logs);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double target = h * cov[i, j];
                    double se = Math.Sqrt((h * cov[i, i] * h * cov[j, j] + target * target) / m);
                    Assert.True(Math.Abs(sample[i, j] - target) < 4.0 * se, $"cov[{i},{j}] = {sample[i, j]}, target {target}, SE {se}");
                }
        }

        // ---- linear books against the delta-normal model ---------------------------------------------------------

        [Theory]
        [InlineData(ScenarioGenerator.Philox)]
        [InlineData(ScenarioGenerator.ScrambledSobol)]
        public void LinearBookConvergesToDeltaNormal(ScenarioGenerator generator)
        {
            // Simple-return factors: P&L = sum_j q_j S_j r_j is exactly normal with sd sqrt(w' h Sigma w), w_j = q_j S_j.
            double[] vols = { 0.012, 0.02, 0.009, 0.015 };
            double[,] corr = { { 1.0, 0.6, 0.3, -0.2 }, { 0.6, 1.0, 0.4, 0.1 }, { 0.3, 0.4, 1.0, 0.0 }, { -0.2, 0.1, 0.0, 1.0 } };
            double[,] cov = Covariance.FromCorrelation(corr, vols);
            double[] spots = { 100.0, 40.0, 250.0, 75.0 };
            double[] qty = { 1000.0, -2500.0, 300.0, 800.0 };
            const double h = 10.0;
            const int m = 1 << 18;
            var factors = new RiskFactor[4];
            var positions = new PortfolioPosition[4];
            var w = new double[4];
            for (int j = 0; j < 4; j++)
            {
                factors[j] = new RiskFactor("S" + j, spots[j], RiskFactorKind.SimpleReturn);
                positions[j] = new SpotPosition(j, qty[j]);
                w[j] = qty[j] * spots[j];
            }
            VarEs exact = ParametricRisk.DeltaNormal(w, cov, Alpha, horizon: h);
            double sigma = ParametricRisk.PortfolioVolatility(w, cov) * Math.Sqrt(h);

            VarEs mc = new FullRevaluationEngine(factors, cov, positions)
                .Run(new MonteCarloVarOptions { Scenarios = m, Horizon = h, Generator = generator, Seed = 11 })
                .Measures(Alpha);

            NormalStandardErrors(sigma, m, out double seVar, out double seEs);
            Assert.True(Math.Abs(mc.Var - exact.Var) < 4.0 * seVar, $"VaR {mc.Var} vs {exact.Var}, SE {seVar}");
            Assert.True(Math.Abs(mc.ExpectedShortfall - exact.ExpectedShortfall) < 4.0 * seEs, $"ES {mc.ExpectedShortfall} vs {exact.ExpectedShortfall}, SE {seEs}");
        }

        // ---- one option against its exact one-factor distribution ------------------------------------------------

        public static IEnumerable<object[]> OptionCases()
        {
            // option type, quantity, which factor moves (0 spot, 1 vol, 2 rate), daily sd of that factor, time decay (years)
            yield return new object[] { OptionType.Call, 100.0, 0, 0.015, 0.0 };
            yield return new object[] { OptionType.Put, -100.0, 0, 0.015, 10.0 / 252.0 };
            yield return new object[] { OptionType.Call, 100.0, 1, 0.01, 0.0 };
            yield return new object[] { OptionType.Put, 100.0, 1, 0.01, 0.0 };
            yield return new object[] { OptionType.Call, -100.0, 2, 0.001, 0.0 };
        }

        [Theory]
        [MemberData(nameof(OptionCases))]
        public void SingleOptionMatchesTheExactDistributionOfItsPrice(OptionType type, double quantity, int moving, double dailySd, double timeDecay)
        {
            // Spot log-normal, volatility and rate additive; only factor `moving` has variance. The P&L is a monotone
            // function g of the single normal move, so its alpha-quantile is g at the matching normal quantile and its
            // ES the integral of g over that tail.
            const double s0 = 100.0, vol0 = 0.25, r0 = 0.03, strike = 105.0, maturity = 0.5, q = 0.01, h = 10.0;
            const int m = 100_000;
            var factors = new[]
            {
                new RiskFactor("spot", s0, RiskFactorKind.LogNormal),
                new RiskFactor("vol", vol0, RiskFactorKind.Additive),
                new RiskFactor("rate", r0, RiskFactorKind.Additive),
            };
            var cov = new double[3, 3];
            cov[moving, moving] = dailySd * dailySd;
            var option = new EuropeanOptionPosition(type, 0, 1, 2, strike, maturity, quantity, q);
            var engine = new FullRevaluationEngine(factors, cov, new[] { option });
            MonteCarloVarResult result = engine.Run(new MonteCarloVarOptions { Scenarios = m, Horizon = h, TimeDecay = timeDecay, Seed = 2024 });
            VarEs mc = result.Measures(Alpha);

            double sd = dailySd * Math.Sqrt(h);
            double v0 = BlackScholesKernels.Price((int)type, s0, strike, maturity, r0, q, vol0);
            double tScenario = maturity - timeDecay;
            Func<double, double> g = zeta =>
            {
                double x = sd * zeta;
                double s = moving == 0 ? s0 * Math.Exp(x - 0.5 * sd * sd) : s0;
                double v = moving == 1 ? vol0 + x : vol0;
                double r = moving == 2 ? r0 + x : r0;
                return quantity * (BlackScholesKernels.Price((int)type, s, strike, tScenario, r, q, v) - v0);
            };
            bool increasing = g(1.0) > g(-1.0);
            double zTail = NormalDistribution.InverseCdf(increasing ? 1.0 - Alpha : Alpha);
            double exactVar = -g(zTail);

            // ES: substitute zeta = Phi^-1(u) over the tail of probability 1 - alpha (midpoint rule in u).
            const int nodes = 200_000;
            double tail = 0.0;
            for (int k = 0; k < nodes; k++)
            {
                double u = (k + 0.5) / nodes * (1.0 - Alpha);
                tail += g(NormalDistribution.InverseCdf(increasing ? u : 1.0 - u));
            }
            double exactEs = -tail / nodes;

            double slope = Math.Abs(g(zTail + 1e-4) - g(zTail - 1e-4)) / 2e-4;     // |dL/dzeta| at the quantile
            double density = NormalDistribution.Pdf(zTail) / slope;               // loss density at VaR
            double seVar = Math.Sqrt(Alpha * (1.0 - Alpha) / m) / density;
            double seEs = EmpiricalEsStandardError(result.Pnl, Alpha, mc);
            Assert.True(Math.Abs(mc.Var - exactVar) < 4.0 * seVar, $"VaR {mc.Var} vs exact {exactVar}, SE {seVar}");
            Assert.True(Math.Abs(mc.ExpectedShortfall - exactEs) < 4.0 * seEs, $"ES {mc.ExpectedShortfall} vs exact {exactEs}, SE {seEs}");
        }

        [Fact]
        public void WithoutMovesThePnlIsZeroOrExactlyTheTimeDecay()
        {
            var factors = new[]
            {
                new RiskFactor("spot", 100.0, RiskFactorKind.LogNormal),
                new RiskFactor("vol", 0.2, RiskFactorKind.Additive),
                new RiskFactor("rate", 0.02, RiskFactorKind.Additive),
            };
            var positions = new PortfolioPosition[]
            {
                new EuropeanOptionPosition(OptionType.Call, 0, 1, 2, 100.0, 1.0, 10.0),
                new EuropeanOptionPosition(OptionType.Put, 0, 1, 2, 90.0, 0.02, -5.0, 0.01),
                new SpotPosition(0, -3.0),
            };
            var engine = new FullRevaluationEngine(factors, new double[3, 3], positions);
            Assert.All(engine.Run(new MonteCarloVarOptions { Scenarios = 100 }).Pnl, p => Assert.Equal(0.0, p));

            // Rolling expiries by 0.05y (past the expiry of the put, which is then worth its intrinsic value 0).
            double theta = 10.0 * (BlackScholes.Price(OptionType.Call, 100.0, 100.0, 0.95, 0.02, 0.0, 0.2) - BlackScholes.Price(OptionType.Call, 100.0, 100.0, 1.0, 0.02, 0.0, 0.2))
                - 5.0 * (BlackScholes.Price(OptionType.Put, 100.0, 90.0, 0.0, 0.02, 0.01, 0.2) - BlackScholes.Price(OptionType.Put, 100.0, 90.0, 0.02, 0.02, 0.01, 0.2));
            Assert.All(engine.Run(new MonteCarloVarOptions { Scenarios = 10, TimeDecay = 0.05 }).Pnl, p => Assert.Equal(theta, p, 12));
            Assert.Equal(10.0 * BlackScholes.Price(OptionType.Call, 100.0, 100.0, 1.0, 0.02, 0.0, 0.2)
                - 5.0 * BlackScholes.Price(OptionType.Put, 100.0, 90.0, 0.02, 0.02, 0.01, 0.2) - 300.0, engine.PortfolioValue, 12);
        }

        // ---- determinism, generators -----------------------------------------------------------------------------

        [Theory]
        [InlineData(ScenarioGenerator.Philox)]
        [InlineData(ScenarioGenerator.ScrambledSobol)]
        public void ResultsAreBitIdenticalWhateverTheChunkingAndParallelism(ScenarioGenerator generator)
        {
            FullRevaluationEngine engine = MixedBook();
            var baseOptions = new MonteCarloVarOptions { Scenarios = 5003, Generator = generator, Seed = 77, KeepInstrumentPnl = true, Horizon = 5, TimeDecay = 5.0 / 252 };
            MonteCarloVarResult reference = engine.Run(baseOptions with { Parallel = false, ChunkSize = 1 << 20 });
            foreach (var (parallel, chunk) in new[] { (false, 7), (true, 1), (true, 13), (true, 256), (true, 4096) })
            {
                MonteCarloVarResult r = engine.Run(baseOptions with { Parallel = parallel, ChunkSize = chunk });
                Assert.Equal(reference.Pnl, r.Pnl);
                Assert.Equal(reference.InstrumentPnl, r.InstrumentPnl);
            }
            // Without the per-instrument matrix the portfolio P&L is the same too.
            Assert.Equal(reference.Pnl, engine.Run(baseOptions with { KeepInstrumentPnl = false }).Pnl);
            Assert.Null(engine.Run(baseOptions with { KeepInstrumentPnl = false, Scenarios = 10 }).InstrumentPnl);
        }

        [Fact]
        public void SobolAndPhiloxAgreeStatistically()
        {
            FullRevaluationEngine engine = MixedBook();
            const int m = 1 << 16;
            var options = new MonteCarloVarOptions { Scenarios = m, Horizon = 10, TimeDecay = 10.0 / 252, Seed = 3 };
            MonteCarloVarResult philox = engine.Run(options);
            MonteCarloVarResult sobol = engine.Run(options with { Generator = ScenarioGenerator.ScrambledSobol });
            VarEs p = philox.Measures(Alpha), s = sobol.Measures(Alpha);

            // VaR: distribution-free band of order statistics around the Philox quantile, +-5 binomial SE in rank.
            double[] losses = new double[m];
            for (int i = 0; i < m; i++) losses[i] = -philox.Pnl[i];
            Array.Sort(losses);
            int k = (int)Math.Ceiling(m * Alpha) - 1;
            int d = (int)Math.Ceiling(5.0 * Math.Sqrt(m * Alpha * (1.0 - Alpha)));
            Assert.InRange(s.Var, losses[k - d], losses[Math.Min(m - 1, k + d)]);
            Assert.True(Math.Abs(s.ExpectedShortfall - p.ExpectedShortfall) < 5.0 * EmpiricalEsStandardError(philox.Pnl, Alpha, p),
                $"ES Sobol {s.ExpectedShortfall} vs Philox {p.ExpectedShortfall}");

            double meanP = Mean(philox.Pnl, out double sdP), meanS = Mean(sobol.Pnl, out _);
            Assert.True(Math.Abs(meanS - meanP) < 5.0 * sdP / Math.Sqrt(m), $"mean Sobol {meanS} vs Philox {meanP}");
        }

        // ---- decomposition ---------------------------------------------------------------------------------------

        [Fact]
        public void DecompositionAddsUpAndMatchesTheParametricOneOnALinearBook()
        {
            // Mixed book: components add up exactly (ES) and by construction (VaR).
            MonteCarloVarResult mixed = MixedBook().Run(new MonteCarloVarOptions { Scenarios = 20_000, Horizon = 10, KeepInstrumentPnl = true });
            RiskDecomposition dm = mixed.Decompose(Alpha);
            Assert.Equal(mixed.Measures(Alpha), dm.Total);
            Assert.True(Math.Abs(Sum(dm.ComponentEs) - dm.Total.ExpectedShortfall) <= 1e-10 * Math.Abs(dm.Total.ExpectedShortfall));
            Assert.True(Math.Abs(Sum(dm.ComponentVar) - dm.Total.Var) <= 1e-10 * Math.Abs(dm.Total.Var));
            double[] q = mixed.Quantities;
            for (int i = 0; i < q.Length; i++) Assert.Equal(q[i] * dm.MarginalEs[i], dm.ComponentEs[i], 12);

            // Linear simple-return book: component ES converges to the delta-normal one, w_i (Sigma w)_i phi(z) / (sigma (1 - alpha)).
            double[] vols = { 0.01, 0.02, 0.015 };
            double[,] corr = { { 1.0, 0.5, 0.2 }, { 0.5, 1.0, -0.3 }, { 0.2, -0.3, 1.0 } };
            double[,] cov = Covariance.FromCorrelation(corr, vols);
            var factors = new[]
            {
                new RiskFactor("a", 50.0, RiskFactorKind.SimpleReturn),
                new RiskFactor("b", 20.0, RiskFactorKind.SimpleReturn),
                new RiskFactor("c", 80.0, RiskFactorKind.SimpleReturn),
            };
            var positions = new PortfolioPosition[] { new SpotPosition(0, 200.0), new SpotPosition(1, 400.0), new SpotPosition(2, -100.0) };
            double[] w = { 200.0 * 50.0, 400.0 * 20.0, -100.0 * 80.0 };
            MonteCarloVarResult linear = new FullRevaluationEngine(factors, cov, positions)
                .Run(new MonteCarloVarOptions { Scenarios = 1 << 18, KeepInstrumentPnl = true, Seed = 8 });
            RiskDecomposition mc = linear.Decompose(0.975);
            RiskDecomposition exact = ParametricRisk.Decompose(w, cov, 0.975);
            for (int i = 0; i < 3; i++)
                Assert.True(Math.Abs(mc.ComponentEs[i] - exact.ComponentEs[i]) < 0.03 * exact.Total.ExpectedShortfall,
                    $"component ES {i}: {mc.ComponentEs[i]} vs {exact.ComponentEs[i]}");
        }

        [Fact]
        public void DecomposeNeedsTheInstrumentPnl()
        {
            MonteCarloVarResult r = MixedBook().Run(new MonteCarloVarOptions { Scenarios = 100 });
            Assert.Throws<InvalidOperationException>(() => r.Decompose(Alpha));
        }

        // ---- validation ------------------------------------------------------------------------------------------

        [Fact]
        public void InvalidInputsAreRejected()
        {
            var factors = new[] { new RiskFactor("s", 100.0, RiskFactorKind.LogNormal), new RiskFactor("v", 0.2, RiskFactorKind.Additive) };
            var spot = new[] { new SpotPosition(0, 1.0) };
            double[,] cov = { { 1e-4, 0.0 }, { 0.0, 0.0 } };
            Assert.Throws<ArgumentException>(() => new FullRevaluationEngine(factors, new double[3, 3], spot));
            Assert.Throws<ArgumentException>(() => new FullRevaluationEngine(factors, cov, new[] { new SpotPosition(2, 1.0) }));
            Assert.Throws<ArgumentException>(() => new FullRevaluationEngine(factors, new double[,] { { 1e-4, 1e-5 }, { 1e-5, 0.0 } }, spot));
            Assert.Throws<ArgumentException>(() => new FullRevaluationEngine(factors, new double[,] { { -1e-4, 0.0 }, { 0.0, 0.0 } }, spot));
            Assert.Throws<ArgumentException>(() => new FullRevaluationEngine(factors, cov,
                new[] { new EuropeanOptionPosition(OptionType.Call, 0, 1, 1, 0.0, 1.0, 1.0) }));
            Assert.Throws<ArgumentException>(() => new FullRevaluationEngine(factors, cov,
                new[] { new EuropeanOptionPosition(OptionType.Call, 0, 5, 1, 100.0, 1.0, 1.0) }));
            var engine = new FullRevaluationEngine(factors, cov, spot);
            Assert.Throws<ArgumentOutOfRangeException>(() => engine.Run(new MonteCarloVarOptions { Scenarios = 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => engine.Run(new MonteCarloVarOptions { Horizon = 0.0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => engine.Run(new MonteCarloVarOptions { ChunkSize = 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => engine.Run(new MonteCarloVarOptions { TimeDecay = -1.0 }));
        }

        [Fact]
        public void AnIndefiniteCovarianceIsRepaired()
        {
            // Correlations 0.9, 0.9, -0.9 are not jointly possible.
            double[,] corr = { { 1.0, 0.9, 0.9 }, { 0.9, 1.0, -0.9 }, { 0.9, -0.9, 1.0 } };
            var factors = new[]
            {
                new RiskFactor("a", 1.0, RiskFactorKind.SimpleReturn),
                new RiskFactor("b", 1.0, RiskFactorKind.SimpleReturn),
                new RiskFactor("c", 1.0, RiskFactorKind.SimpleReturn),
            };
            var engine = new FullRevaluationEngine(factors, Covariance.FromCorrelation(corr, new[] { 0.01, 0.01, 0.01 }), new[] { new SpotPosition(0, 1.0) });
            Assert.True(engine.CovarianceRepaired);
            MonteCarloVarResult r = engine.Run(new MonteCarloVarOptions { Scenarios = 1000 });
            Assert.True(r.CovarianceRepaired);
            Assert.All(r.Pnl, p => Assert.True(double.IsFinite(p)));
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        /// <summary>Three equities, their implied vols and one rate; spots, options both ways, a vol and a rate move.</summary>
        internal static FullRevaluationEngine MixedBook()
        {
            var factors = new[]
            {
                new RiskFactor("S1", 100.0, RiskFactorKind.LogNormal),
                new RiskFactor("S2", 60.0, RiskFactorKind.LogNormal),
                new RiskFactor("S3", 150.0, RiskFactorKind.LogNormal),
                new RiskFactor("vol1", 0.22, RiskFactorKind.Additive),
                new RiskFactor("vol2", 0.30, RiskFactorKind.Additive),
                new RiskFactor("vol3", 0.18, RiskFactorKind.Additive),
                new RiskFactor("rate", 0.03, RiskFactorKind.Additive),
            };
            double[] sd = { 0.015, 0.02, 0.012, 0.008, 0.01, 0.006, 0.0005 };
            var corr = new double[7, 7];
            for (int i = 0; i < 7; i++)
                for (int j = 0; j < 7; j++)
                {
                    bool iSpot = i < 3, jSpot = j < 3;
                    corr[i, j] = i == j ? 1.0
                        : iSpot && jSpot ? 0.5
                        : (iSpot && j == i + 3) || (jSpot && i == j + 3) ? -0.6   // spot / own vol
                        : !iSpot && !jSpot && i < 6 && j < 6 ? 0.4
                        : 0.0;
                }
            var positions = new PortfolioPosition[]
            {
                new SpotPosition(0, 1000.0),
                new SpotPosition(1, -500.0),
                new EuropeanOptionPosition(OptionType.Put, 0, 3, 6, 95.0, 0.25, 2000.0),
                new EuropeanOptionPosition(OptionType.Call, 1, 4, 6, 65.0, 0.5, -1500.0, 0.01),
                new EuropeanOptionPosition(OptionType.Call, 2, 5, 6, 150.0, 1.0, 800.0, 0.02),
                new EuropeanOptionPosition(OptionType.Put, 2, 5, 6, 130.0, 0.08, -1200.0, 0.02),
            };
            return new FullRevaluationEngine(factors, Covariance.FromCorrelation(corr, sd), positions);
        }

        static void NormalStandardErrors(double sigma, int m, out double seVar, out double seEs)
        {
            double z = NormalDistribution.InverseCdf(Alpha);
            double lambda = NormalDistribution.Pdf(z) / (1.0 - Alpha);   // ES / sigma
            seVar = sigma * Math.Sqrt(Alpha * (1.0 - Alpha) / m) / NormalDistribution.Pdf(z);
            double tailVar = sigma * sigma * (1.0 + z * lambda - lambda * lambda);
            seEs = Math.Sqrt((tailVar + Alpha * sigma * sigma * (lambda - z) * (lambda - z)) / (m * (1.0 - Alpha)));
        }

        static double EmpiricalEsStandardError(double[] pnl, double alpha, VarEs measures)
        {
            int m = pnl.Length;
            double sum = 0.0, sq = 0.0;
            int count = 0;
            foreach (double p in pnl)
            {
                double loss = -p;
                if (loss < measures.Var) continue;
                sum += loss;
                sq += loss * loss;
                count++;
            }
            double mean = sum / count;
            double tailVar = Math.Max(0.0, sq / count - mean * mean);
            double gap = measures.ExpectedShortfall - measures.Var;
            return Math.Sqrt((tailVar + alpha * gap * gap) / (m * (1.0 - alpha)));
        }

        static double Mean(double[] x, out double sd)
        {
            double mean = 0.0, sq = 0.0;
            foreach (double v in x) mean += v;
            mean /= x.Length;
            foreach (double v in x) sq += (v - mean) * (v - mean);
            sd = Math.Sqrt(sq / (x.Length - 1));
            return mean;
        }

        static double Sum(double[] x)
        {
            double s = 0.0;
            foreach (double v in x) s += v;
            return s;
        }

        static double[] Identity(int n)
        {
            var l = new double[n * n];
            for (int i = 0; i < n; i++) l[i * n + i] = 1.0;
            return l;
        }

        static double[,] IdentityMatrix(int n)
        {
            var a = new double[n, n];
            for (int i = 0; i < n; i++) a[i, i] = 1.0;
            return a;
        }
    }
}
