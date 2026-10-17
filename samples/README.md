# Samples

Small console programs that show the library at work. Each one is a self-contained `net8.0` project with its own
README (what it shows, how to run it, an excerpt of the expected output). They use deterministic seeds and synthetic
data, so a run reproduces the excerpts, and they need nothing but the .NET 8 SDK.

| Sample | Shows | Library projects used |
| --- | --- | --- |
| [01-OptionPricing](01-OptionPricing) | Black-Scholes-Merton price and Greeks, implied volatility round trip, Heston (COS) price, strike ladder | `ClosedForm` |
| [02-YieldCurve](02-YieldCurve) | EUR-like curve bootstrapped from deposits and swaps (TARGET, day counts), fixed-rate bond and swap pricing | `Market` |
| [03-VolatilitySurface](03-VolatilitySurface) | SVI fit to noisy smiles, butterfly and calendar arbitrage checks, interpolated surface, price from the surface | `Volatility`, `Market`, `ClosedForm` |
| [04-MonteCarloVaR](04-MonteCarloVaR) | Ledoit-Wolf covariance, full-revaluation Monte Carlo VaR / ES with decomposition on the CPU, optional GPU run through Hybridizer with CPU/GPU comparison | `Risk`, `ClosedForm` |
| [05-Backtest](05-Backtest) | ERC vs minimum variance vs 60/40 on a synthetic panel, stationary-bootstrap distribution of Sharpe ratios | `Backtesting`, `Portfolio`, `TimeSeries`, `Risk` |

Run any of them from the repository root:

```sh
dotnet run -c Release --project samples/01-OptionPricing
```

Samples 04 and 05 take an optional argument after `--` (number of scenarios, number of bootstrap paths).

Sample 04 is the one to read to see Hybridizer at work: the same scenarios run on the CPU and, when the CUDA satellite
`InFine.QuantFinance_CUDA.dll` is available, on an NVIDIA GPU (see [docs/gpu.md](../docs/gpu.md)). Without the satellite,
or on a machine where the Hybridizer runtime does not load (for example an Apple Silicon Mac), it prints why and carries on
with the CPU part.

The data are synthetic and the quotes illustrative: the samples show how to call the library, not market levels.
The samples are part of `QuantFinance.sln`, so CI builds them, but they are not packaged.
