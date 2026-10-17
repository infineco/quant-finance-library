using System;
using System.Collections.Generic;
using InFine.QuantFinance.Backtesting;
using InFine.QuantFinance.Portfolio;
using InFine.QuantFinance.Risk;
using InFine.QuantFinance.TimeSeries;
using Xunit;

namespace InFine.QuantFinance.Backtesting.Tests
{
    public class BacktestEngineTests
    {
        [Fact]
        public void BuyAndHoldOfOneAssetIsExactlyItsCumulativeReturn()
        {
            double[,] r = TestData.Returns(500, 1, seed: 1);
            BacktestResult result = BacktestEngine.Run(TestData.Panel(r), new BuyAndHoldStrategy(new[] { 1.0 }));

            // Same multiplications in the same order: bit-identical, not merely close.
            double wealth = 1.0;
            Assert.Equal(1.0, result.Values[0]);
            for (int t = 0; t < 500; t++)
            {
                wealth *= 1.0 + r[t, 0];
                Assert.Equal(wealth, result.Values[t + 1]);
            }
            Assert.Equal(new[] { 0 }, result.RebalancePeriods);
            Assert.Equal(1.0, result.TotalTurnover);
            Assert.Equal(0.0, result.TotalCosts);
            Assert.Equal(PerformanceStatistics.TotalReturn(Column(r, 0)), result.TerminalWealth - 1.0);
        }

        [Fact]
        public void BuyAndHoldWeightsDriftWithValue()
        {
            const int T = 300;
            double[,] r = TestData.Returns(T, 3, seed: 2);
            double[] w = { 0.5, 0.3, 0.2 };
            BacktestResult result = BacktestEngine.Run(TestData.Panel(r), new BuyAndHoldStrategy(w),
                new BacktestOptions { Schedule = RebalanceSchedule.Every(5) });

            var growth = new double[] { 1.0, 1.0, 1.0 };
            for (int t = 0; t < T; t++)
            {
                double value = 0.0;
                for (int i = 0; i < 3; i++) value += w[i] * growth[i];
                // Value-weighted drift: w_i(t) = w_i G_i(t) / sum_j w_j G_j(t), with G the cumulative growth before period t.
                for (int i = 0; i < 3; i++)
                    Assert.Equal(w[i] * growth[i] / value, result.Weights[t, i], 1e-13);
                Assert.Equal(value, result.Values[t], 1e-13);
                for (int i = 0; i < 3; i++) growth[i] *= 1.0 + r[t, i];
            }
            double terminal = 0.0;
            for (int i = 0; i < 3; i++) terminal += w[i] * growth[i];
            Assert.Equal(terminal, result.Values[T], 1e-13);
            Assert.Equal(new[] { 0 }, result.RebalancePeriods); // null targets are not trades
        }

        [Fact]
        public void ConstantMixWithoutCostsMatchesHandComputedThreePeriods()
        {
            double[,] r = { { 0.10, -0.05 }, { -0.20, 0.10 }, { 0.05, 0.02 } };
            BacktestResult result = BacktestEngine.Run(TestData.Panel(r), new ConstantMixStrategy(new[] { 0.5, 0.5 }));

            // V1 = 1 (1 + 0.5 * 0.10 + 0.5 * -0.05) = 1.025
            // drifted to (0.55, 0.475), back to 0.5125 each: traded 0.0375 + 0.0375 = 0.075
            // V2 = 1.025 (1 + 0.5 * -0.20 + 0.5 * 0.10) = 0.97375
            // drifted to (0.41, 0.56375), back to 0.486875 each: traded 0.076875 + 0.076875 = 0.15375
            // V3 = 0.97375 (1 + 0.5 * 0.05 + 0.5 * 0.02) = 1.00783125
            Assert.Equal(new[] { 1.0, 1.025, 0.97375, 1.00783125 }, result.Values, new Tolerance(1e-15));
            Assert.Equal(new[] { 0.025, -0.05, 0.035 }, result.Returns, new Tolerance(1e-15));
            Assert.Equal(new[] { 1.0, 0.075 / 1.025, 0.15375 / 0.97375 }, result.Turnover, new Tolerance(1e-15));
            Assert.Equal(new[] { 0, 1, 2 }, result.RebalancePeriods);
            for (int p = 0; p < 3; p++)
            {
                Assert.Equal(0.5, result.Weights[p, 0], 1e-15);
                Assert.Equal(0.5, result.Weights[p, 1], 1e-15);
                Assert.Equal(0.0, result.CashWeights[p], 1e-15);
            }
        }

        [Fact]
        public void TransactionCostsArePaidFromCashAsInTheHandExample()
        {
            // 2 assets, 10 bps on notional, 0.001 per traded asset, cash earning 1% per period.
            double[,] r = { { 0.10, -0.05 }, { -0.20, 0.10 } };
            var panel = TestData.Panel(r, cash: new[] { 0.01, 0.01 });
            var options = new BacktestOptions { ProportionalCostBps = 10.0, FixedCostPerTrade = 0.001 };
            BacktestResult result = BacktestEngine.Run(panel, new ConstantMixStrategy(new[] { 0.5, 0.5 }), options);

            // t = 0: buy 0.5 + 0.5 from cash; cost 0.001 * 1.0 + 2 * 0.001 = 0.003; cash -0.003.
            // Period 0: holdings (0.55, 0.475), cash -0.003 * 1.01 = -0.00303; V1 = 1.02197.
            // t = 1: targets 0.510985 each; trades -0.039015 and +0.035985 (notional 0.075);
            //        cost 0.001 * 0.075 + 2 * 0.001 = 0.002075; cash -0.00303 + 0.039015 - 0.035985 - 0.002075 = -0.002075.
            // Period 1: holdings (0.408788, 0.5620835), cash -0.002075 * 1.01 = -0.00209575; V2 = 0.96877575.
            Assert.Equal(new[] { 0.003, 0.002075 }, result.Costs, new Tolerance(1e-15));
            Assert.Equal(0.005075, result.TotalCosts, 1e-15);
            Assert.Equal(new[] { 1.0, 1.02197, 0.96877575 }, result.Values, new Tolerance(1e-15));
            Assert.Equal(new[] { 1.0, 0.075 / 1.02197 }, result.Turnover, new Tolerance(1e-15));
            Assert.Equal(-0.003 / 0.997, result.CashWeights[0], 1e-15);
            Assert.Equal(1.0 / 0.997, result.GrossExposure[0], 1e-15);
            Assert.Equal(-0.002075 / (1.02197 - 0.002075), result.CashWeights[1], 1e-15);
            Assert.Equal(0.96877575 / 1.02197 - 1.0, result.Returns[1], 1e-15);
        }

        [Fact]
        public void TradesBelowTheToleranceAreSkippedAndPayNoFixedFee()
        {
            // Re-targeting the drifted weights asks for trades of rounding size only: none is executed or charged.
            double[,] r = TestData.Returns(30, 3, seed: 8);
            var options = new BacktestOptions { FixedCostPerTrade = 0.01, ProportionalCostBps = 10.0 };
            var hold = new DelegateStrategy(0, c => c.IsInitialAllocation ? new[] { 0.5, 0.3, 0.1 } : ToArray(c.CurrentWeights));
            BacktestResult result = BacktestEngine.Run(TestData.Panel(r), hold, options);
            Assert.Equal(0.03 + 0.0009, result.Costs[0], 1e-16);
            for (int p = 1; p < 30; p++)
            {
                Assert.Equal(0.0, result.Costs[p]);
                Assert.Equal(0.0, result.Turnover[p]);
            }
        }

        [Fact]
        public void StrategyThatReadsTheCurrentPeriodIsRejected()
        {
            double[,] r = TestData.Returns(50, 2, seed: 3);
            var peeking = new DelegateStrategy(0, c => new[] { c.Window[c.Period, 0] > 0 ? 1.0 : 0.0, 0.0 });
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => BacktestEngine.Run(TestData.Panel(r), peeking));
            Assert.Contains("look-ahead", ex.Message);

            var datePeek = new DelegateStrategy(0, c => { _ = c.Window.Date(c.Period); return null; });
            Assert.Throws<ArgumentOutOfRangeException>(() => BacktestEngine.Run(TestData.Panel(r), datePeek));

            var tailPeek = new DelegateStrategy(0, c => { _ = c.Window.Tail(c.Window.Count + 1); return null; });
            Assert.Throws<ArgumentOutOfRangeException>(() => BacktestEngine.Run(TestData.Panel(r), tailPeek));
        }

        [Fact]
        public void StrategySeesExactlyThePastAtEachDecision()
        {
            double[,] r = TestData.Returns(40, 2, seed: 4);
            var panel = TestData.Panel(r);
            var seen = new List<int>();
            var recorder = new DelegateStrategy(3, c =>
            {
                Assert.Equal(c.Period, c.Window.Count);
                double[,] tail = c.Window.Tail(3);
                for (int k = 0; k < 3; k++)
                    for (int i = 0; i < 2; i++)
                        Assert.Equal(r[c.Period - 3 + k, i], tail[k, i]);
                Assert.Equal(panel.Date(c.Period - 1), c.Window.Date(c.Window.Count - 1));
                seen.Add(c.Period);
                return new[] { 0.5, 0.5 };
            });
            BacktestResult result = BacktestEngine.Run(panel, recorder, new BacktestOptions { Schedule = RebalanceSchedule.Every(10) });
            Assert.Equal(new[] { 3, 13, 23, 33 }, seen);
            Assert.Equal(3, result.StartPeriod);
            Assert.Equal(37, result.Returns.Length);
            Assert.Equal(panel.Date(3), result.Dates[0]);
        }

        [Fact]
        public void StatisticsAreThoseOfPerformanceStatisticsOnTheNetReturns()
        {
            double[,] r = TestData.Returns(750, 3, seed: 5);
            var options = new BacktestOptions { Schedule = RebalanceSchedule.Every(21), ProportionalCostBps = 5.0 };
            BacktestResult result = BacktestEngine.Run(TestData.Panel(r), new ConstantMixStrategy(new[] { 0.4, 0.4, 0.2 }), options);

            PerformanceSummary summary = result.Summarise(Periodicity.Daily, riskFreeRate: 0.01);
            Assert.Equal(PerformanceStatistics.Summarise(result.Returns, Periodicity.Daily, 0.01), summary);

            // The return series compounds to the value series.
            Assert.Equal(result.TerminalWealth - 1.0, summary.TotalReturn, 1e-12);
            Assert.Equal(Math.Pow(result.TerminalWealth, 252.0 / 750) - 1.0, summary.AnnualisedReturn, 1e-12);
            Assert.Equal(PerformanceStatistics.MaxDrawdown(result.Returns).Depth, result.MaxDrawdown().Depth, 1e-12);

            // Sharpe recomputed by hand (two-pass sample moments).
            double mean = 0.0;
            foreach (double x in result.Returns) mean += x;
            mean /= 750;
            double ss = 0.0;
            foreach (double x in result.Returns) ss += (x - mean) * (x - mean);
            double sharpe = (mean - 0.01 / 252) / Math.Sqrt(ss / 749) * Math.Sqrt(252);
            Assert.Equal(sharpe, summary.SharpeRatio, 1e-12);
            Assert.Equal(result.TotalTurnover * 252 / 750, result.AnnualisedTurnover(252), 1e-14);
        }

        [Fact]
        public void LeverageIsTrackedAndCapped()
        {
            double[,] r = TestData.Returns(20, 2, seed: 6);
            BacktestResult levered = BacktestEngine.Run(TestData.Panel(r), new ConstantMixStrategy(new[] { 0.8, 0.7 }));
            Assert.Equal(1.5, levered.GrossExposure[0], 1e-14);
            Assert.Equal(-0.5, levered.CashWeights[0], 1e-14);

            BacktestResult capped = BacktestEngine.Run(TestData.Panel(r), new ConstantMixStrategy(new[] { 1.0, -1.0 }),
                new BacktestOptions { MaxGrossExposure = 1.5 });
            Assert.Equal(1.5, capped.GrossExposure[0], 1e-14);
            Assert.Equal(0.0, capped.NetExposure[0], 1e-14);
            Assert.Equal(0.75, capped.Weights[0, 0], 1e-14);
            Assert.Equal(1.0, capped.CashWeights[0], 1e-14);
        }

        [Fact]
        public void CalendarScheduleRebalancesAfterTheLastDayOfEachMonth()
        {
            DateOnly[] dates = TestData.Weekdays(70);
            bool[] flags = RebalanceSchedule.Calendar(CalendarFrequency.Monthly).Flags(dates, 0);
            for (int t = 1; t < dates.Length; t++)
                Assert.Equal(dates[t].Month != dates[t - 1].Month, flags[t]);
            Assert.True(flags[0]);

            bool[] every = RebalanceSchedule.Every(4).Flags(dates, 2);
            for (int t = 0; t < dates.Length; t++) Assert.Equal(t >= 2 && (t - 2) % 4 == 0, every[t]);

            bool[] explicitPeriods = RebalanceSchedule.AtPeriods(new[] { 1, 10, 500 }).Flags(dates, 5);
            Assert.Equal(new[] { 5, 10 }, Indices(explicitPeriods));
        }

        [Fact]
        public void RiskBasedStrategiesUseTheTrailingCovarianceOnly()
        {
            const int lookback = 60;
            double[,] r = TestData.Returns(200, 4, seed: 7);
            var panel = TestData.Panel(r);
            var options = new BacktestOptions { Schedule = RebalanceSchedule.Every(20) };

            double[,] Trailing(int t)
            {
                var m = new double[lookback, 4];
                for (int k = 0; k < lookback; k++)
                    for (int i = 0; i < 4; i++) m[k, i] = r[t - lookback + k, i];
                return m;
            }

            void Check(IStrategy strategy, Func<double[,], double[]> expected)
            {
                BacktestResult result = BacktestEngine.Run(panel, strategy, options);
                Assert.Equal(lookback, result.StartPeriod);
                foreach (int t in result.RebalancePeriods)
                {
                    double[] w = expected(Trailing(t));
                    for (int i = 0; i < 4; i++) Assert.Equal(w[i], result.Weights[t - lookback, i], 1e-12);
                }
                Assert.Equal(new[] { 60, 80, 100, 120, 140, 160, 180 }, result.RebalancePeriods);
            }

            Check(new EqualRiskContributionStrategy(CovarianceEstimator.Sample(lookback)),
                m => RiskParityOptimizer.EqualRiskContribution(Covariance.Sample(m)).Weights);
            Check(new MinimumVarianceStrategy(CovarianceEstimator.LedoitWolf(lookback)),
                m => new MeanVarianceOptimizer(new double[4], LedoitWolf.ConstantCorrelation(m).Covariance).MinimumVariance().Weights);
            Check(new InverseVolatilityStrategy(CovarianceEstimator.Ewma(lookback, 0.97, seedWindow: 20)),
                m => RiskBudgeting.InverseVolatility(EwmaCovariance.Estimate(m, 0.97, 20)));
        }

        [Fact]
        public void PanelFromPricesUsesTheReturnDates()
        {
            DateOnly[] dates = TestData.Weekdays(4);
            double[,] prices = { { 100, 50 }, { 110, 50 }, { 99, 55 }, { 99, 44 } };
            var panel = ReturnPanel.FromPrices(dates, prices);
            Assert.Equal(3, panel.PeriodCount);
            Assert.Equal(dates[1], panel.Date(0));
            Assert.Equal(0.1, panel[0, 0], 1e-15);
            Assert.Equal(-0.2, panel[2, 1], 1e-15);
            Assert.False(panel.HasCash);
        }

        static int[] Indices(bool[] flags)
        {
            var list = new List<int>();
            for (int i = 0; i < flags.Length; i++) if (flags[i]) list.Add(i);
            return list.ToArray();
        }

        static double[] ToArray(IReadOnlyList<double> x)
        {
            var a = new double[x.Count];
            for (int i = 0; i < a.Length; i++) a[i] = x[i];
            return a;
        }

        static double[] Column(double[,] m, int j)
        {
            var c = new double[m.GetLength(0)];
            for (int i = 0; i < c.Length; i++) c[i] = m[i, j];
            return c;
        }

        sealed class DelegateStrategy : IStrategy
        {
            readonly Func<StrategyContext, double[]?> _f;
            public DelegateStrategy(int minimumHistory, Func<StrategyContext, double[]?> f) { MinimumHistory = minimumHistory; _f = f; }
            public int MinimumHistory { get; }
            public double[]? TargetWeights(StrategyContext context) => _f(context);
        }
    }

    sealed class Tolerance : IEqualityComparer<double>
    {
        readonly double _tol;
        public Tolerance(double tol) => _tol = tol;
        public bool Equals(double x, double y) => Math.Abs(x - y) <= _tol;
        public int GetHashCode(double obj) => 0;
    }
}
