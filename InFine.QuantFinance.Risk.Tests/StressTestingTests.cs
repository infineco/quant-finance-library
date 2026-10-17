using System;
using System.Collections.Generic;
using InFine.QuantFinance.ClosedForm;
using Xunit;

namespace InFine.QuantFinance.Risk.Tests
{
    /// <summary>
    /// Scenario stress tests against an independent repricing with <see cref="BlackScholes.Price(OptionType, double, double, double, double, double, double)"/>
    /// written out by hand in the test, the precedence rules, the attribution identity, theta, and the full-revaluation engine.
    /// </summary>
    public class StressTestingTests
    {
        const double Tol = 1e-10;
        const double S0 = 100.0, Vol0 = 0.20, R0 = 0.03, Q = 0.01;

        static RiskFactor[] Factors() => new[]
        {
            new RiskFactor("SPX", S0, RiskFactorKind.LogNormal) { Group = "equity" },
            new RiskFactor("SX5E", 50.0, RiskFactorKind.LogNormal) { Group = "equity" },
            new RiskFactor("SPXVOL", Vol0, RiskFactorKind.Additive) { Group = "vol" },
            new RiskFactor("USD", R0, RiskFactorKind.Additive) { Group = "rates" },
        };

        // 10 shares of SPX, 5 SPX calls (K 100, 1y), -3 SPX puts (K 95, 0.5y), 4 shares of SX5E.
        static PortfolioPosition[] Portfolio() => new PortfolioPosition[]
        {
            new SpotPosition(0, 10.0),
            new EuropeanOptionPosition(OptionType.Call, 0, 2, 3, 100.0, 1.0, 5.0, Q),
            new EuropeanOptionPosition(OptionType.Put, 0, 2, 3, 95.0, 0.5, -3.0, Q),
            new SpotPosition(1, 4.0),
        };

        static StressScenario Scenario(string name, double decay, params StressShock[] shocks) => new(name, "", shocks, decay);

        /// <summary>Independent repricing of the toy book at the given SPX, SX5E, vol, rate and time decay.</summary>
        static double ExpectedPnl(double spx, double sx5e, double vol, double rate, double decay = 0.0)
        {
            double Value(double s, double x, double v, double r, double d) =>
                10.0 * s
                + 5.0 * BlackScholes.Price(OptionType.Call, s, 100.0, Math.Max(1.0 - d, 0.0), r, Q, v)
                - 3.0 * BlackScholes.Price(OptionType.Put, s, 95.0, Math.Max(0.5 - d, 0.0), r, Q, v)
                + 4.0 * x;
            return Value(spx, sx5e, vol, rate, decay) - Value(S0, 50.0, Vol0, R0, 0.0);
        }

        static StressScenarioResult RunOne(StressScenario scenario) =>
            StressTestEngine.Run(Portfolio(), Factors(), new[] { scenario }).Results[0];

        [Fact]
        public void RelativeShockOnOneFactorMatchesIndependentRepricing()
        {
            StressScenarioResult r = RunOne(Scenario("rel", 0.0, StressShock.ForFactor("SPX", StressShockMode.Relative, -0.15)));
            Assert.Equal(ExpectedPnl(85.0, 50.0, Vol0, R0), r.TotalPnl, Tol);
            Assert.Equal(85.0, r.ShockedLevels[0], 12);
        }

        [Fact]
        public void AbsoluteShockOnVolatilityMatchesIndependentRepricing()
        {
            StressScenarioResult r = RunOne(Scenario("abs", 0.0, StressShock.ForFactor("SPXVOL", StressShockMode.Absolute, 0.05)));
            Assert.Equal(ExpectedPnl(S0, 50.0, 0.25, R0), r.TotalPnl, Tol);
        }

        [Fact]
        public void LogReturnShockMatchesIndependentRepricing()
        {
            StressScenarioResult r = RunOne(Scenario("log", 0.0, StressShock.ForFactor("SPX", StressShockMode.LogReturn, -0.1)));
            Assert.Equal(ExpectedPnl(S0 * Math.Exp(-0.1), 50.0, Vol0, R0), r.TotalPnl, Tol);
        }

        [Fact]
        public void SetShockMatchesIndependentRepricing()
        {
            StressScenarioResult r = RunOne(Scenario("set", 0.0,
                StressShock.ForFactor("USD", StressShockMode.Set, 0.0),
                StressShock.ForFactor("SPX", StressShockMode.Set, 120.0)));
            Assert.Equal(ExpectedPnl(120.0, 50.0, Vol0, 0.0), r.TotalPnl, Tol);
        }

        [Fact]
        public void SpecificShockOverridesGroupShock()
        {
            StressScenarioResult r = RunOne(Scenario("prec", 0.0,
                StressShock.ForGroup("equity", StressShockMode.Relative, -0.20),
                StressShock.ForFactor("SX5E", StressShockMode.Relative, -0.05)));
            Assert.Equal(80.0, r.ShockedLevels[0], 12);   // group
            Assert.Equal(47.5, r.ShockedLevels[1], 12);   // specific wins over the group
            Assert.Equal(Vol0, r.ShockedLevels[2]);
            Assert.Equal(ExpectedPnl(80.0, 47.5, Vol0, R0), r.TotalPnl, Tol);
        }

        [Fact]
        public void GroupShockFullyOverriddenContributesNothingAndGroupWithoutMembersIsIgnored()
        {
            StressScenarioResult r = RunOne(Scenario("x", 0.0,
                StressShock.ForGroup("vol", StressShockMode.Absolute, 0.1),
                StressShock.ForFactor("SPXVOL", StressShockMode.Absolute, 0.02),
                StressShock.ForGroup("credit", StressShockMode.Relative, 0.5)));
            Assert.Equal(0.22, r.ShockedLevels[2], 12);
            Assert.Equal(0.0, r.Attribution[0].Pnl);
            Assert.Equal(0.0, r.Attribution[2].Pnl);
            Assert.Equal(ExpectedPnl(S0, 50.0, 0.22, R0), r.TotalPnl, Tol);
        }

        [Fact]
        public void AttributionPlusInteractionEqualsTotalAndInteractionIsNonZero()
        {
            StressScenarioResult r = RunOne(Scenario("combo", 0.1,
                StressShock.ForGroup("equity", StressShockMode.Relative, -0.20),
                StressShock.ForGroup("vol", StressShockMode.Absolute, 0.10),
                StressShock.ForGroup("rates", StressShockMode.Absolute, -0.005)));
            Assert.Equal(ExpectedPnl(80.0, 40.0, 0.30, 0.025, 0.1), r.TotalPnl, Tol);
            Assert.Equal(4, r.Attribution.Count);
            Assert.Equal(StressTestEngine.TimeDecayLabel, r.Attribution[0].Label);
            double sum = r.Interaction;
            foreach (StressContribution c in r.Attribution) sum += c.Pnl;
            Assert.Equal(r.TotalPnl, sum, 12);
            Assert.True(Math.Abs(r.Interaction) > 1e-3, "spot and vol shocks interact through the options");
            double byInstrument = 0.0;
            foreach (double p in r.InstrumentPnl) byInstrument += p;
            Assert.Equal(r.TotalPnl, byInstrument, 12);
            // One-at-a-time contribution of the equity block against independent repricing.
            double theta = ExpectedPnl(S0, 50.0, Vol0, R0, 0.1);
            Assert.Equal(ExpectedPnl(80.0, 40.0, Vol0, R0, 0.1) - theta, r.Attribution[1].Pnl, Tol);
            Assert.Equal(theta, r.Attribution[0].Pnl, Tol);
        }

        [Fact]
        public void ZeroShockGivesExactlyZeroAndTimeDecayAloneGivesTheta()
        {
            Assert.Equal(0.0, RunOne(Scenario("none", 0.0)).TotalPnl);
            Assert.Equal(0.0, RunOne(Scenario("zero rel", 0.0, StressShock.ForGroup("equity", StressShockMode.Relative, 0.0))).TotalPnl);
            Assert.Equal(0.0, RunOne(Scenario("zero log", 0.0, StressShock.ForFactor("SPX", StressShockMode.LogReturn, 0.0))).TotalPnl);

            double decay = 1.0 / 52.0;
            StressScenarioResult r = RunOne(Scenario("theta", decay));
            Assert.Equal(ExpectedPnl(S0, 50.0, Vol0, R0, decay), r.TotalPnl, Tol);
            Assert.True(r.TotalPnl < 0.0, "net long options lose time value");
            Assert.Equal(0.0, r.Interaction, 12);
        }

        [Fact]
        public void StressPnlEqualsFullRevaluationEnginePnlForTheSameMove()
        {
            // Engine: SPX moves (additive factor so the engine move is x itself), everything else constant.
            RiskFactor[] factors =
            {
                new("SPX", S0, RiskFactorKind.Additive), new("SX5E", 50.0, RiskFactorKind.Additive),
                new("SPXVOL", Vol0, RiskFactorKind.Additive), new("USD", R0, RiskFactorKind.Additive),
            };
            var cov = new double[4, 4];
            cov[0, 0] = 25.0;
            var options = new MonteCarloVarOptions { Scenarios = 16, Seed = 7, Parallel = false, KeepInstrumentPnl = true };
            MonteCarloVarResult book = new FullRevaluationEngine(factors, cov, Portfolio()).Run(options);
            MonteCarloVarResult shares = new FullRevaluationEngine(factors, cov, new PortfolioPosition[] { new SpotPosition(0, 1.0) }).Run(options);

            foreach (int s in new[] { 0, 3, 11 })
            {
                double move = shares.Pnl[s];   // the scenario's move of SPX, in index points
                Assert.NotEqual(0.0, move);
                StressScenarioResult r = RunOne(Scenario("same", 0.0, StressShock.ForFactor("SPX", StressShockMode.Absolute, move)));
                Assert.Equal(book.Pnl[s], r.TotalPnl, 10);
            }
        }

        [Fact]
        public void ReportFindsWorstScenario()
        {
            StressScenario[] scenarios =
            {
                Scenario("up", 0.0, StressShock.ForGroup("equity", StressShockMode.Relative, 0.1)),
                Scenario("crash", 0.0, StressShock.ForGroup("equity", StressShockMode.Relative, -0.3)),
                Scenario("flat", 0.0),
            };
            StressTestReport report = StressTestEngine.Run(Portfolio(), Factors(), scenarios);
            Assert.Equal("crash", report.Worst.Scenario.Name);
            Assert.Equal(3, report.Results.Count);
            Assert.True(report.PortfolioValue > 0.0);
        }

        [Fact]
        public void LibraryScenariosRunAndAreLabelledIllustrative()
        {
            StressTestReport report = StressTestEngine.Run(Portfolio(), Factors(), StressScenarioLibrary.All);
            Assert.Equal(StressScenarioLibrary.All.Count, report.Results.Count);
            foreach (StressScenario s in StressScenarioLibrary.All) Assert.Contains("Illustrative", s.Description);
            StressScenarioResult crash = report.Results[0];
            Assert.Equal(ExpectedPnl(80.0, 40.0, 0.30, 0.025), crash.TotalPnl, Tol);
        }

        [Fact]
        public void ReverseStressReachesTheLossThreshold()
        {
            StressShock[] direction =
            {
                StressShock.ForGroup("equity", StressShockMode.Relative, -0.10),
                StressShock.ForGroup("vol", StressShockMode.Absolute, 0.02),
            };
            const double threshold = 300.0;
            ReverseStressResult r = StressTestEngine.ReverseStress(Portfolio(), Factors(), direction, threshold);
            Assert.True(r.Reached);
            Assert.Equal(-threshold, r.Pnl, 1e-6);
            // Independent check at the returned scale.
            double k = r.Scale;
            Assert.Equal(ExpectedPnl(S0 * (1 - 0.10 * k), 50.0 * (1 - 0.10 * k), Vol0 + 0.02 * k, R0), r.Pnl, Tol);

            ReverseStressResult not = StressTestEngine.ReverseStress(Portfolio(), Factors(), direction, 1e9, maxScale: 2.0);
            Assert.False(not.Reached);
        }

        [Fact]
        public void InvalidInputsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => RunOne(Scenario("a", 0.0, StressShock.ForFactor("NOPE", StressShockMode.Relative, 0.1))));
            Assert.Throws<ArgumentException>(() => RunOne(Scenario("b", 0.0,
                StressShock.ForFactor("SPX", StressShockMode.Relative, 0.1), StressShock.ForFactor("SPX", StressShockMode.Absolute, 1.0))));
            Assert.Throws<ArgumentException>(() => RunOne(Scenario("c", 0.0,
                StressShock.ForGroup("vol", StressShockMode.Relative, 0.1), StressShock.ForGroup("vol", StressShockMode.Absolute, 1.0))));
            Assert.Throws<ArgumentException>(() => RunOne(Scenario("d", 0.0, new StressShock(StressShockMode.Relative, 0.1))));
            Assert.Throws<ArgumentException>(() => RunOne(Scenario("e", 0.0, StressShock.ForFactor("SPX", StressShockMode.Relative, double.NaN))));
            Assert.Throws<ArgumentException>(() => RunOne(Scenario("f", -1.0)));
            Assert.Throws<ArgumentException>(() => StressTestEngine.Run(Portfolio(), Factors(), Array.Empty<StressScenario>()));
        }
    }
}
