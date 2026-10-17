using System;
using System.Collections.Generic;
using InFine.QuantFinance.TimeSeries;

namespace InFine.QuantFinance.Backtesting
{
    /// <summary>Settings of a <see cref="BacktestEngine"/> run.</summary>
    public sealed record BacktestOptions
    {
        /// <summary>When the strategy is consulted; default every period.</summary>
        public RebalanceSchedule Schedule { get; init; } = RebalanceSchedule.EveryPeriod;

        /// <summary>Proportional transaction cost in basis points of traded notional (10 = 0.1%). Default 0.</summary>
        public double ProportionalCostBps { get; init; }

        /// <summary>Fixed cost per traded asset per rebalance, in money (same unit as <see cref="InitialValue"/>). Default 0.</summary>
        public double FixedCostPerTrade { get; init; }

        /// <summary>Starting wealth, held in cash until the first decision. Default 1.</summary>
        public double InitialValue { get; init; } = 1.0;

        /// <summary>First period traded (the effective start is at least the strategy's minimum history). Default 0.</summary>
        public int StartPeriod { get; init; }

        /// <summary>
        /// Cap on the gross exposure <c>sum |w_i|</c> of a target; larger targets are scaled down to it (the rest stays in
        /// cash). Default +infinity (no cap).
        /// </summary>
        public double MaxGrossExposure { get; init; } = double.PositiveInfinity;

        /// <summary>A trade of at most <c>TradeTolerance |V|</c> is skipped (no cost, no fixed fee). Default 1e-12.</summary>
        public double TradeTolerance { get; init; } = 1e-12;

        internal void Validate()
        {
            if (Schedule == null) throw new ArgumentException("A rebalance schedule is required.", nameof(Schedule));
            if (!(ProportionalCostBps >= 0.0) || double.IsInfinity(ProportionalCostBps)) throw new ArgumentOutOfRangeException(nameof(ProportionalCostBps));
            if (!(FixedCostPerTrade >= 0.0) || double.IsInfinity(FixedCostPerTrade)) throw new ArgumentOutOfRangeException(nameof(FixedCostPerTrade));
            if (!(InitialValue > 0.0) || double.IsInfinity(InitialValue)) throw new ArgumentOutOfRangeException(nameof(InitialValue));
            if (StartPeriod < 0) throw new ArgumentOutOfRangeException(nameof(StartPeriod));
            if (!(MaxGrossExposure > 0.0)) throw new ArgumentOutOfRangeException(nameof(MaxGrossExposure));
            if (!(TradeTolerance >= 0.0) || double.IsInfinity(TradeTolerance)) throw new ArgumentOutOfRangeException(nameof(TradeTolerance));
        }
    }

    /// <summary>
    /// Runs a strategy over a <see cref="ReturnPanel"/>: rebalancing on a schedule, drift between rebalances, transaction
    /// costs paid from cash, turnover, cash residual and exposure tracking; statistics through
    /// <see cref="PerformanceStatistics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Timing.</b> The backtest starts at period <c>s = max(StartPeriod, strategy.MinimumHistory)</c> with the whole
    /// wealth in cash. Before period <c>t</c> (at the close of period <c>t - 1</c>), if the schedule says so, the strategy
    /// sees the window of periods <c>0 .. t-1</c> and returns target weights (or null to hold); the portfolio is traded,
    /// then earns the returns of row <c>t</c>. The decision at <c>s</c> is always taken (initial allocation).
    /// </para>
    /// <para>
    /// <b>Accounting.</b> Holdings are kept in money. A target <c>w</c> is traded against the pre-trade value <c>V</c>:
    /// asset <c>i</c> goes to <c>w_i V</c>, purchases and sales go through cash, and the cost
    /// <c>bps / 10^4 sum |trade| + fixed (number of trades)</c> is taken from cash, so the cash residual
    /// <c>1 - sum w</c> absorbs it (it is slightly negative for a fully invested target with costs). Turnover is
    /// <c>sum |trade| / V</c> (buys plus sells; the initial allocation counts). Weights between rebalances drift with prices;
    /// cash earns the panel's cash return (negative cash is borrowing at the same rate). The period return
    /// <c>V_end / V_start - 1</c> includes the costs, so the return series compounds to the value series. A value at or
    /// below zero wipes the portfolio out (value 0, later returns 0, no more decisions). The trading and growth steps are
    /// <see cref="BacktestKernels.Rebalance"/> and <see cref="BacktestKernels.Grow"/>, shared with the bootstrap kernel.
    /// </para>
    /// </remarks>
    public static class BacktestEngine
    {
        /// <summary>Runs <paramref name="strategy"/> over <paramref name="panel"/>.</summary>
        /// <exception cref="ArgumentException">Invalid options, a start past the end of the panel, or a target of the wrong length or non-finite.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The strategy read a period outside its window (look-ahead).</exception>
        public static BacktestResult Run(ReturnPanel panel, IStrategy strategy, BacktestOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(panel);
            ArgumentNullException.ThrowIfNull(strategy);
            options ??= new BacktestOptions();
            options.Validate();
            int n = panel.AssetCount, periods = panel.PeriodCount;
            int start = Math.Max(options.StartPeriod, strategy.MinimumHistory);
            if (start >= periods)
                throw new ArgumentException($"The backtest would start at period {start} but the panel has {periods} periods.", nameof(panel));

            DateOnly[] dates = panel.Dates;
            bool[] decide = options.Schedule.Flags(dates, start);
            double[] returns = panel.FlatReturns;
            double[] cashReturns = panel.FlatCash;
            double proportional = options.ProportionalCostBps * 1e-4;

            int count = periods - start;
            var values = new double[count + 1];
            var periodReturns = new double[count];
            var weights = new double[count, n];
            var turnover = new double[count];
            var costs = new double[count];
            var cashWeight = new double[count];
            var gross = new double[count];
            var net = new double[count];
            var rebalances = new List<int>();

            var holdings = new double[n];
            double cash = options.InitialValue, value = options.InitialValue;
            bool alive = true, first = true;
            values[0] = value;

            for (int t = start; t < periods; t++)
            {
                int p = t - start;
                if (alive && decide[t])
                {
                    var current = new double[n];
                    for (int i = 0; i < n; i++) current[i] = holdings[i] / value;
                    var context = new StrategyContext(t, panel.Window(t), current, cash / value, value, first);
                    double[]? target = strategy.TargetWeights(context);
                    first = false;
                    if (target != null)
                    {
                        target = CheckTarget(target, n, options.MaxGrossExposure, t);
                        cash = BacktestKernels.Rebalance(n, holdings, 0, target, 0, value, cash, proportional,
                            options.FixedCostPerTrade, options.TradeTolerance, out double notional, out double cost);
                        turnover[p] = notional / value;
                        costs[p] = cost;
                        rebalances.Add(t);
                    }
                }

                // Exposures after trading, before the period's returns.
                double invested = 0.0, absolute = 0.0;
                for (int i = 0; i < n; i++) { invested += holdings[i]; absolute += Math.Abs(holdings[i]); }
                double postTrade = invested + cash;
                if (alive && postTrade > 0.0)
                {
                    for (int i = 0; i < n; i++) weights[p, i] = holdings[i] / postTrade;
                    cashWeight[p] = cash / postTrade;
                    gross[p] = absolute / postTrade;
                    net[p] = invested / postTrade;
                }

                if (alive)
                {
                    double risky = BacktestKernels.Grow(n, holdings, 0, returns, t * n);
                    cash *= 1.0 + cashReturns[t];
                    double end = risky + cash;
                    if (end <= 0.0)
                    {
                        end = 0.0;
                        cash = 0.0;
                        Array.Clear(holdings);
                        alive = false;
                    }
                    periodReturns[p] = end / value - 1.0;
                    value = end;
                }
                values[p + 1] = value;
            }

            var periodDates = new DateOnly[count];
            Array.Copy(dates, start, periodDates, 0, count);
            return new BacktestResult(start, periodDates, values, periodReturns, weights, cashWeight, gross, net, turnover, costs, rebalances.ToArray());
        }

        static double[] CheckTarget(double[] target, int n, double maxGross, int period)
        {
            if (target.Length != n)
                throw new ArgumentException($"The strategy returned {target.Length} weights for {n} assets at period {period}.");
            double sumAbs = 0.0;
            foreach (double w in target)
            {
                if (!double.IsFinite(w)) throw new ArgumentException($"The strategy returned a non-finite weight at period {period}.");
                sumAbs += Math.Abs(w);
            }
            if (sumAbs <= maxGross) return target;
            var scaled = new double[n];
            double scale = maxGross / sumAbs;
            for (int i = 0; i < n; i++) scaled[i] = target[i] * scale;
            return scaled;
        }
    }

    /// <summary>The outcome of a <see cref="BacktestEngine"/> run. Series are indexed by backtest period <c>p = t - StartPeriod</c>.</summary>
    public sealed class BacktestResult
    {
        internal BacktestResult(int startPeriod, DateOnly[] dates, double[] values, double[] returns, double[,] weights,
            double[] cashWeights, double[] grossExposure, double[] netExposure, double[] turnover, double[] costs, int[] rebalancePeriods)
        {
            StartPeriod = startPeriod;
            Dates = dates;
            Values = values;
            Returns = returns;
            Weights = weights;
            CashWeights = cashWeights;
            GrossExposure = grossExposure;
            NetExposure = netExposure;
            Turnover = turnover;
            Costs = costs;
            RebalancePeriods = rebalancePeriods;
        }

        /// <summary>Panel index of the first traded period.</summary>
        public int StartPeriod { get; }

        /// <summary>End date of each backtest period (P = T - StartPeriod entries).</summary>
        public DateOnly[] Dates { get; }

        /// <summary>Portfolio value: entry 0 is the initial value (before the first trade), entry p + 1 the value at the end of period p.</summary>
        public double[] Values { get; }

        /// <summary>Simple portfolio return of each period, net of costs, <c>Values[p + 1] / Values[p] - 1</c>.</summary>
        public double[] Returns { get; }

        /// <summary>P x N risky weights after trading, at the start of each period (fractions of the post-trade value).</summary>
        public double[,] Weights { get; }

        /// <summary>Cash weight after trading at the start of each period (the cash residual; negative when borrowing).</summary>
        public double[] CashWeights { get; }

        /// <summary>Gross exposure <c>sum |w_i|</c> after trading (the leverage).</summary>
        public double[] GrossExposure { get; }

        /// <summary>Net exposure <c>sum w_i</c> after trading.</summary>
        public double[] NetExposure { get; }

        /// <summary>Turnover of each period: traded notional over pre-trade value (0 without a trade).</summary>
        public double[] Turnover { get; }

        /// <summary>Transaction costs paid in each period, in money.</summary>
        public double[] Costs { get; }

        /// <summary>Panel indices of the periods where the strategy traded.</summary>
        public int[] RebalancePeriods { get; }

        /// <summary>Sum of <see cref="Turnover"/>.</summary>
        public double TotalTurnover => Sum(Turnover);

        /// <summary>Sum of <see cref="Costs"/>.</summary>
        public double TotalCosts => Sum(Costs);

        /// <summary>Final over initial value.</summary>
        public double TerminalWealth => Values[^1] / Values[0];

        /// <summary>Average turnover per year, <c>TotalTurnover periodsPerYear / P</c>.</summary>
        public double AnnualisedTurnover(double periodsPerYear) => TotalTurnover * periodsPerYear / Returns.Length;

        /// <summary>Maximum drawdown of <see cref="Values"/> (of the return series after a wipe-out).</summary>
        public DrawdownInfo MaxDrawdown()
        {
            foreach (double v in Values)
                if (!(v > 0.0)) return PerformanceStatistics.MaxDrawdown(Returns);
            return PerformanceStatistics.MaxDrawdownOfLevels(Values);
        }

        /// <summary><see cref="PerformanceStatistics.Summarise"/> of <see cref="Returns"/> (needs at least 4 periods).</summary>
        public PerformanceSummary Summarise(double periodsPerYear, double riskFreeRate = 0.0, double minimumAcceptableReturn = 0.0) =>
            PerformanceStatistics.Summarise(Returns, periodsPerYear, riskFreeRate, minimumAcceptableReturn);

        static double Sum(double[] x)
        {
            double s = 0.0;
            foreach (double v in x) s += v;
            return s;
        }
    }
}
