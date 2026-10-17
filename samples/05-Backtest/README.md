# 05 - Backtest

Compares three allocation rules on a synthetic daily panel of six assets (US, EU and EM equities, government bonds, credit,
gold; 1512 days with a 60-day equity stress episode), rebalanced every 21 days with 5 bp proportional costs:

- **ERC**: equal risk contribution weights (`EqualRiskContributionStrategy`), covariance by Ledoit-Wolf shrinkage on the last
  252 days;
- **Min variance**: long-only minimum-variance portfolio (`MinimumVarianceStrategy`), same covariance estimator;
- **60/40**: constant mix of 60% US equity and 40% government bonds (`ConstantMixStrategy`).

`BacktestEngine.Run` gives the single historical path of each. `StationaryBootstrap` then resamples the history in random
blocks (mean length 20 days, Politis-Romano) and replays each strategy on every resampled history, which turns a single
Sharpe ratio into a distribution. All strategies see the same resampled histories (same seed, same path index), so the
"beats 60/40" column is a paired comparison. The 60/40 mix uses `RunConstantMix`, the allocation-free path that also has a GPU
version (see [docs/gpu.md](../../docs/gpu.md)).

The result is a property of the synthetic data (drifts, volatilities and the stress episode are chosen by hand), not a statement about the
strategies; replace `SyntheticPanel` by your own `ReturnPanel` (`ReturnPanel.FromPrices`) to test real assets.

## Run

```sh
dotnet run -c Release --project samples/05-Backtest
dotnet run -c Release --project samples/05-Backtest -- 5000    # 5000 bootstrap paths
```

## Expected output

The timings depend on the machine.

```text
== Panel: 1512 daily returns, US equity, EU equity, EM equity, Gov bonds, Credit, Gold (2019-01-01 to 2024-10-16) ==

== Historical backtest (every strategy starts after the same 252-day estimation window) ==
Strategy                   Ann. return  Ann. vol  Sharpe  Sortino   Max DD  Turnover/yr    Costs
ERC (Ledoit-Wolf, 252d)          3.69%     3.48%    1.06     1.57    5.12%         0.47    0.13%
Min variance (long-only)         2.30%     2.75%    0.84     1.21    6.04%         0.67    0.18%
60/40 constant mix              10.62%     9.94%    1.07     1.58    9.50%         0.42    0.12%

Weights held in the last period (after the last rebalance, drifted by one month of returns):
                            US equity  EU equity  EM equity  Gov bonds     Credit       Gold
ERC (Ledoit-Wolf, 252d)         6.00%      5.78%      4.23%     62.27%     13.11%      8.62%
Min variance (long-only)        7.66%      1.67%      0.00%     78.27%      9.14%      3.27%
60/40 constant mix             58.89%      0.00%      0.00%     41.11%      0.00%      0.00%

== Stationary bootstrap: 1000 resampled histories, mean block length 20 days, same blocks for every strategy ==
  ERC (Ledoit-Wolf, 252d)       0.5 s
  Min variance (long-only)      0.3 s
  60/40 constant mix            0.0 s

Distribution of the annualised Sharpe ratio over the resampled histories:
Strategy                        5%     25%  Median     75%     95%    Mean  P(Sharpe<0)  P(beats 60/40)
ERC (Ledoit-Wolf, 252d)       0.12    0.52    0.80    1.07    1.43    0.80        2.50%          31.80%
Min variance (long-only)     -0.16    0.33    0.61    0.92    1.33    0.61        8.70%          25.00%
60/40 constant mix            0.45    0.76    0.98    1.20    1.50    0.98        0.10%               -

Distribution of the maximum drawdown (median and 95th percentile):
ERC (Ledoit-Wolf, 252d)      4.57%   8.34%
Min variance (long-only)     4.62%   8.63%
60/40 constant mix          10.12%  16.61%
```
