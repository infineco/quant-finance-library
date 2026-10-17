using System;
using InFine.QuantFinance.Backtesting;
using InFine.QuantFinance.Portfolio;
using InFine.QuantFinance.TimeSeries;

// Sample 5: equal risk contribution (ERC) vs long-only minimum variance vs a 60/40 constant mix on a synthetic panel of
// six assets, rebalanced every 21 days with 5 bp proportional costs; then a stationary bootstrap of each strategy gives the
// distribution of its Sharpe ratio, not just the single number the historical path happened to produce.
// Usage: dotnet run -c Release --project samples/05-Backtest [-- <bootstrap paths>]

int paths = args.Length > 0 ? int.Parse(args[0]) : 1000;
string[] assets = { "US equity", "EU equity", "EM equity", "Gov bonds", "Credit", "Gold" };
ReturnPanel panel = SyntheticPanel(days: 1512, seed: 7, assets);

var backtest = new BacktestOptions { Schedule = RebalanceSchedule.Every(21), ProportionalCostBps = 5.0 };
double[] sixtyForty = { 0.6, 0.0, 0.0, 0.4, 0.0, 0.0 };   // 60% US equity, 40% government bonds
var strategies = new (string Name, IStrategy Strategy)[]
{
    ("ERC (Ledoit-Wolf, 252d)", new EqualRiskContributionStrategy(CovarianceEstimator.LedoitWolf(252))),
    ("Min variance (long-only)", new MinimumVarianceStrategy(CovarianceEstimator.LedoitWolf(252), PortfolioConstraints.LongOnly(assets.Length))),
    ("60/40 constant mix", new ConstantMixStrategy(sixtyForty)),
};

Console.WriteLine($"== Panel: {panel.PeriodCount} daily returns, {string.Join(", ", assets)} ({panel.Date(0):yyyy-MM-dd} to {panel.Date(panel.PeriodCount - 1):yyyy-MM-dd}) ==");
Console.WriteLine();
Console.WriteLine("== Historical backtest (every strategy starts after the same 252-day estimation window) ==");
Console.WriteLine($"{"Strategy",-26}{"Ann. return",12}{"Ann. vol",10}{"Sharpe",8}{"Sortino",9}{"Max DD",9}{"Turnover/yr",13}{"Costs",9}");
var backtestFrom = backtest with { StartPeriod = 252 };
var runs = new BacktestResult[strategies.Length];
for (int i = 0; i < strategies.Length; i++)
{
    // The 60/40 mix has no history requirement, so StartPeriod aligns the three on the same dates.
    runs[i] = BacktestEngine.Run(panel, strategies[i].Strategy, backtestFrom);
    PerformanceSummary s = runs[i].Summarise(Periodicity.Daily);
    Console.WriteLine($"{strategies[i].Name,-26}{Pct(s.AnnualisedReturn),12}{Pct(s.AnnualisedVolatility),10}{s.SharpeRatio,8:F2}{s.SortinoRatio,9:F2}" +
                      $"{Pct(s.MaxDrawdown.Depth),9}{runs[i].AnnualisedTurnover(Periodicity.Daily),13:F2}{Pct(runs[i].TotalCosts / backtest.InitialValue),9}");
}

Console.WriteLine();
Console.WriteLine("Weights held in the last period (after the last rebalance, drifted by one month of returns):");
Console.WriteLine($"{"",-26}" + string.Join("", Array.ConvertAll(assets, a => $"{a,11}")));
for (int i = 0; i < strategies.Length; i++)
{
    Console.Write($"{strategies[i].Name,-26}");
    int last = runs[i].Returns.Length - 1;
    for (int j = 0; j < assets.Length; j++) Console.Write($"{Pct(runs[i].Weights[last, j]),11}");
    Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine($"== Stationary bootstrap: {paths} resampled histories, mean block length 20 days, same blocks for every strategy ==");
var bootstrap = new BootstrapOptions { PathCount = paths, MeanBlockLength = 20.0, Seed = 42, PeriodsPerYear = Periodicity.Daily };
var results = new BootstrapResult[strategies.Length];
for (int i = 0; i < strategies.Length; i++)
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    // The constant mix has a dedicated allocation-free path (the one the GPU runs); the other strategies replay the engine.
    results[i] = strategies[i].Strategy is ConstantMixStrategy mix
        ? StationaryBootstrap.RunConstantMix(panel, mix.Weights, rebalanceEvery: 21, backtest: backtestFrom, options: bootstrap)
        : StationaryBootstrap.Run(panel, strategies[i].Strategy, backtestFrom, bootstrap);
    Console.WriteLine($"  {strategies[i].Name,-26} {watch.Elapsed.TotalSeconds,6:F1} s");
}

Console.WriteLine();
Console.WriteLine("Distribution of the annualised Sharpe ratio over the resampled histories:");
Console.WriteLine($"{"Strategy",-26}{"5%",8}{"25%",8}{"Median",8}{"75%",8}{"95%",8}{"Mean",8}{"P(Sharpe<0)",13}{"P(beats 60/40)",16}");
double[] reference = results[2].SharpeRatio;
for (int i = 0; i < strategies.Length; i++)
{
    double[] sharpe = results[i].SharpeRatio;
    int negative = 0, wins = 0;
    for (int p = 0; p < sharpe.Length; p++)
    {
        if (sharpe[p] < 0.0) negative++;
        if (sharpe[p] > reference[p]) wins++;
    }
    Console.WriteLine($"{strategies[i].Name,-26}{Q(sharpe, 0.05),8:F2}{Q(sharpe, 0.25),8:F2}{Q(sharpe, 0.5),8:F2}{Q(sharpe, 0.75),8:F2}{Q(sharpe, 0.95),8:F2}" +
                      $"{Mean(sharpe),8:F2}{Pct((double)negative / sharpe.Length),13}{(i == 2 ? "-" : Pct((double)wins / sharpe.Length)),16}");
}

Console.WriteLine();
Console.WriteLine("Distribution of the maximum drawdown (median and 95th percentile):");
for (int i = 0; i < strategies.Length; i++)
    Console.WriteLine($"{strategies[i].Name,-26}{Pct(Q(results[i].MaxDrawdown, 0.5)),8}{Pct(Q(results[i].MaxDrawdown, 0.95)),8}");

static double Q(double[] values, double p) => BootstrapResult.Quantile(values, p);
static double Mean(double[] values) => PerformanceStatistics.Mean(values);
static string Pct(double x) => (x * 100.0).ToString("F2") + "%";

// Daily simple returns of six assets driven by an equity factor, a rates factor and idiosyncratic noise, with a 60-day
// stress episode in which equity volatility is multiplied by 2.5 and equities drift down. Deterministic for a given seed.
static ReturnPanel SyntheticPanel(int days, int seed, string[] names)
{
    var rng = new Random(seed);
    double Gauss()
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
    var dates = new DateOnly[days];
    var date = new DateOnly(2019, 1, 1);
    for (int t = 0; t < days; t++)
    {
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
        dates[t] = date;
        date = date.AddDays(1);
    }

    var returns = new double[days, 6];
    for (int t = 0; t < days; t++)
    {
        double stress = t >= 700 && t < 760 ? 2.5 : 1.0;
        double equity = (stress > 1.0 ? -0.0020 : 0.0) + 0.0095 * stress * Gauss();
        double rates = 0.0020 * Gauss() - 0.15 * equity;
        returns[t, 0] = 0.00025 + 1.00 * equity + 0.0040 * Gauss();
        returns[t, 1] = 0.00020 + 0.95 * equity + 0.0050 * Gauss();
        returns[t, 2] = 0.00020 + 1.20 * equity + 0.0090 * Gauss();
        returns[t, 3] = 0.00012 + 0.80 * rates + 0.0012 * Gauss();
        returns[t, 4] = 0.00022 + 0.40 * equity + 0.60 * rates + 0.0020 * Gauss();
        returns[t, 5] = 0.00015 + 0.10 * equity + 0.0090 * Gauss();
    }
    return new ReturnPanel(dates, returns, assetNames: names);
}
