using System;
using System.Threading.Tasks;
using InFine.QuantFinance.TimeSeries;

namespace InFine.QuantFinance.Backtesting
{
    /// <summary>Settings of a stationary-bootstrap run.</summary>
    public sealed record BootstrapOptions
    {
        /// <summary>Number of resampled histories. Default 1000.</summary>
        public int PathCount { get; init; } = 1000;

        /// <summary>Mean block length L &gt;= 1 (restart probability 1 / L); 1 is the i.i.d. bootstrap. Default 20.</summary>
        public double MeanBlockLength { get; init; } = 20.0;

        /// <summary>Philox key. Default 42.</summary>
        public ulong Seed { get; init; } = 42;

        /// <summary>Philox stream tag. Default <see cref="BacktestKernels.DefaultBootstrapStream"/>.</summary>
        public uint Stream { get; init; } = BacktestKernels.DefaultBootstrapStream;

        /// <summary>Periods per year used to annualise the path statistics. Default <see cref="Periodicity.Daily"/>.</summary>
        public double PeriodsPerYear { get; init; } = Periodicity.Daily;

        /// <summary>Annual risk-free rate in the Sharpe ratios (see <see cref="PerformanceStatistics.SharpeRatio"/>). Default 0.</summary>
        public double RiskFreeRate { get; init; }

        /// <summary>Run the path chunks concurrently (<c>Parallel.For</c>). Default true.</summary>
        public bool Parallel { get; init; } = true;

        /// <summary>Paths per chunk, at least 1. Results do not depend on it.</summary>
        public int ChunkSize { get; init; } = 64;

        /// <summary>Maximum number of concurrent chunks; -1 (default) lets the runtime decide.</summary>
        public int MaxDegreeOfParallelism { get; init; } = -1;

        internal void Validate()
        {
            if (PathCount < 1) throw new ArgumentOutOfRangeException(nameof(PathCount));
            if (!(MeanBlockLength >= 1.0) || double.IsInfinity(MeanBlockLength)) throw new ArgumentOutOfRangeException(nameof(MeanBlockLength), "The mean block length must be finite and at least 1.");
            if (!(PeriodsPerYear > 0.0) || double.IsInfinity(PeriodsPerYear)) throw new ArgumentOutOfRangeException(nameof(PeriodsPerYear));
            if (!double.IsFinite(RiskFreeRate)) throw new ArgumentOutOfRangeException(nameof(RiskFreeRate));
            if (ChunkSize < 1) throw new ArgumentOutOfRangeException(nameof(ChunkSize));
            if (MaxDegreeOfParallelism == 0 || MaxDegreeOfParallelism < -1) throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism));
        }
    }

    /// <summary>Per-path statistics of a bootstrap run; every array has one entry per path, in path order.</summary>
    public sealed class BootstrapResult
    {
        internal BootstrapResult(int pathCount)
        {
            TerminalWealth = new double[pathCount];
            MeanReturn = new double[pathCount];
            AnnualisedReturn = new double[pathCount];
            Volatility = new double[pathCount];
            SharpeRatio = new double[pathCount];
            MaxDrawdown = new double[pathCount];
            Turnover = new double[pathCount];
            Costs = new double[pathCount];
        }

        /// <summary>Final over initial value.</summary>
        public double[] TerminalWealth { get; }

        /// <summary>Arithmetic mean of the period returns (not annualised).</summary>
        public double[] MeanReturn { get; }

        /// <summary>Geometric annualised return <c>TerminalWealth^(k / T) - 1</c> (-1 after a wipe-out).</summary>
        public double[] AnnualisedReturn { get; }

        /// <summary>Annualised volatility of the period returns (sample standard deviation times sqrt(k)).</summary>
        public double[] Volatility { get; }

        /// <summary>Annualised Sharpe ratio, as <see cref="PerformanceStatistics.SharpeRatio"/>.</summary>
        public double[] SharpeRatio { get; }

        /// <summary>Maximum drawdown depth (non-negative fraction).</summary>
        public double[] MaxDrawdown { get; }

        /// <summary>Total turnover (sum over rebalances of traded notional over pre-trade value).</summary>
        public double[] Turnover { get; }

        /// <summary>Total transaction costs, in units of the initial value.</summary>
        public double[] Costs { get; }

        /// <summary>Number of paths.</summary>
        public int PathCount => TerminalWealth.Length;

        /// <summary>
        /// Empirical quantile of <paramref name="values"/> at <paramref name="probability"/> in [0, 1], interpolating linearly
        /// between order statistics (Hyndman-Fan type 7, the Excel / numpy default). NaN values are not allowed.
        /// </summary>
        public static double Quantile(double[] values, double probability)
        {
            ArgumentNullException.ThrowIfNull(values);
            if (values.Length == 0) throw new ArgumentException("At least one value is needed.", nameof(values));
            if (!(probability >= 0.0 && probability <= 1.0)) throw new ArgumentOutOfRangeException(nameof(probability));
            var sorted = (double[])values.Clone();
            foreach (double v in sorted) if (double.IsNaN(v)) throw new ArgumentException("Values must not be NaN.", nameof(values));
            Array.Sort(sorted);
            double h = (sorted.Length - 1) * probability;
            int lo = (int)Math.Floor(h);
            int hi = Math.Min(lo + 1, sorted.Length - 1);
            return sorted[lo] + (h - lo) * (sorted[hi] - sorted[lo]);
        }
    }

    /// <summary>
    /// Stationary block bootstrap of a return panel (Politis and Romano, 1994) and strategy backtests on the resampled
    /// histories, run in parallel. See <see cref="BacktestKernels"/> for the resampling scheme and its determinism.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A resampled history has the panel's length and calendar; its row <c>s</c> is source row <c>I_s</c> (asset and cash
    /// returns together, so the cross-section and the short-range serial dependence inside a block are preserved).
    /// </para>
    /// <para>
    /// Two runners. <see cref="RunConstantMix"/> evaluates a constant-mix strategy entirely inside the kernel core
    /// (<see cref="BacktestKernels.EvaluateConstantMixPath"/>: flat arrays, no allocation per path), which is also what a
    /// device launch of <see cref="BacktestKernels.SimulateConstantMixPaths"/> runs. <see cref="Run"/> accepts any
    /// <see cref="IStrategy"/> and runs <see cref="BacktestEngine"/> on each materialised history; it is host-only, because
    /// the optimiser-based strategies (risk parity, mean-variance, covariance estimation) allocate, throw and dispatch
    /// through interfaces. Both runners use the same resampled rows and the same accounting steps, so a constant mix gives
    /// the same paths either way; both cut the path range in chunks run sequentially or with <c>Parallel.For</c>, and the
    /// outputs are bit-identical for any chunk size or degree of parallelism.
    /// </para>
    /// </remarks>
    public static class StationaryBootstrap
    {
        /// <summary>The source rows <c>I_0 .. I_(T-1)</c> of bootstrap path <paramref name="path"/>.</summary>
        public static int[] Rows(int path, int periodCount, double meanBlockLength, ulong seed, uint stream = BacktestKernels.DefaultBootstrapStream)
        {
            if (path < 0) throw new ArgumentOutOfRangeException(nameof(path));
            if (periodCount < 1) throw new ArgumentOutOfRangeException(nameof(periodCount));
            if (!(meanBlockLength >= 1.0) || double.IsInfinity(meanBlockLength)) throw new ArgumentOutOfRangeException(nameof(meanBlockLength));
            double p = 1.0 / meanBlockLength;
            var rows = new int[periodCount];
            int row = 0;
            for (int s = 0; s < periodCount; s++)
            {
                row = BacktestKernels.BootstrapRow(path, s, row, periodCount, p, seed, stream);
                rows[s] = row;
            }
            return rows;
        }

        /// <summary>The resampled history of path <paramref name="path"/>.</summary>
        public static ReturnPanel Resample(ReturnPanel panel, int path, BootstrapOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(panel);
            options ??= new BootstrapOptions();
            options.Validate();
            return panel.Resample(Rows(path, panel.PeriodCount, options.MeanBlockLength, options.Seed, options.Stream));
        }

        /// <summary>
        /// Runs <paramref name="strategy"/> with <see cref="BacktestEngine"/> on every resampled history (host-side, any
        /// strategy; the strategy must be thread-safe when <see cref="BootstrapOptions.Parallel"/> is set).
        /// </summary>
        /// <exception cref="AggregateException">A path failed in parallel mode (the inner exception says why).</exception>
        public static BootstrapResult Run(ReturnPanel panel, IStrategy strategy, BacktestOptions? backtest = null, BootstrapOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(panel);
            ArgumentNullException.ThrowIfNull(strategy);
            backtest ??= new BacktestOptions();
            backtest.Validate();
            options ??= new BootstrapOptions();
            options.Validate();
            var result = new BootstrapResult(options.PathCount);
            double k = options.PeriodsPerYear;
            ForEachChunk(options, (from, to) =>
            {
                for (int path = from; path < to; path++)
                {
                    ReturnPanel history = panel.Resample(Rows(path, panel.PeriodCount, options.MeanBlockLength, options.Seed, options.Stream));
                    BacktestResult run = BacktestEngine.Run(history, strategy, backtest);
                    double[] r = run.Returns;
                    result.TerminalWealth[path] = run.TerminalWealth;
                    result.MeanReturn[path] = PerformanceStatistics.Mean(r);
                    result.AnnualisedReturn[path] = PerformanceStatistics.AnnualisedReturn(r, k);
                    result.Volatility[path] = r.Length > 1 ? PerformanceStatistics.Volatility(r, k) : 0.0;
                    result.SharpeRatio[path] = r.Length > 1 ? PerformanceStatistics.SharpeRatio(r, k, options.RiskFreeRate) : double.NaN;
                    result.MaxDrawdown[path] = run.MaxDrawdown().Depth;
                    result.Turnover[path] = run.TotalTurnover;
                    result.Costs[path] = run.TotalCosts / backtest.InitialValue;
                }
            });
            return result;
        }

        /// <summary>
        /// Constant-mix strategy (<paramref name="weights"/>, decisions every <paramref name="rebalanceEvery"/> periods from
        /// the first) on every resampled history, through the kernel core <see cref="BacktestKernels.EvaluateConstantMixPath"/>.
        /// Costs, gross-exposure cap and trade tolerance come from <paramref name="backtest"/>; its schedule and start period
        /// are ignored (the kernel trades from period 0). Fixed costs are expressed relative to its initial value.
        /// </summary>
        public static BootstrapResult RunConstantMix(ReturnPanel panel, double[] weights, int rebalanceEvery = 1,
            BacktestOptions? backtest = null, BootstrapOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(panel);
            double[] target = Strategies.CheckWeights(weights);
            if (target.Length != panel.AssetCount) throw new ArgumentException($"Expected {panel.AssetCount} weights.", nameof(weights));
            if (rebalanceEvery < 1) throw new ArgumentOutOfRangeException(nameof(rebalanceEvery));
            backtest ??= new BacktestOptions();
            backtest.Validate();
            options ??= new BootstrapOptions();
            options.Validate();

            double sumAbs = 0.0;
            foreach (double w in target) sumAbs += Math.Abs(w);
            if (sumAbs > backtest.MaxGrossExposure)
                for (int i = 0; i < target.Length; i++) target[i] *= backtest.MaxGrossExposure / sumAbs;

            int m = options.PathCount, periods = panel.PeriodCount, n = panel.AssetCount;
            double[] returns = panel.FlatReturns, cash = panel.FlatCash;
            double restart = 1.0 / options.MeanBlockLength;
            double proportional = backtest.ProportionalCostBps * 1e-4;
            double fixedCost = backtest.FixedCostPerTrade / backtest.InitialValue;
            var variance = new double[m];
            var result = new BootstrapResult(m);

            ForEachChunk(options, (from, to) =>
            {
                var scratch = new double[n];
                for (int path = from; path < to; path++)
                    BacktestKernels.EvaluateConstantMixPath(path, options.Seed, options.Stream, restart, periods, n, returns, cash,
                        target, rebalanceEvery, proportional, fixedCost, backtest.TradeTolerance, scratch, 0,
                        result.TerminalWealth, result.MeanReturn, variance, result.MaxDrawdown, result.Turnover, result.Costs);
            });

            double k = options.PeriodsPerYear, rf = options.RiskFreeRate / k;
            for (int path = 0; path < m; path++)
            {
                double wealth = result.TerminalWealth[path];
                double sd = Math.Sqrt(variance[path]);
                result.AnnualisedReturn[path] = wealth > 0.0 ? Math.Pow(wealth, k / periods) - 1.0 : -1.0;
                result.Volatility[path] = sd * Math.Sqrt(k);
                result.SharpeRatio[path] = periods > 1 ? (result.MeanReturn[path] - rf) / sd * Math.Sqrt(k) : double.NaN;
            }
            return result;
        }

        static void ForEachChunk(BootstrapOptions options, Action<int, int> body)
        {
            int m = options.PathCount, size = options.ChunkSize;
            int chunks = (int)(((long)m + size - 1) / size);
            void Chunk(int c)
            {
                int from = (int)((long)c * size);
                body(from, (int)Math.Min((long)from + size, m));
            }
            if (!options.Parallel)
            {
                for (int c = 0; c < chunks; c++) Chunk(c);
                return;
            }
            System.Threading.Tasks.Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, Chunk);
        }
    }
}
