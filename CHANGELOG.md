# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Versions come from git tags (`vMAJOR.MINOR.PATCH`), see
[docs/packaging.md](docs/packaging.md). While the major version is 0, the public API may change between minor versions.

## [Unreleased]

## [0.1.0] - 2026-10-17

First public release. The library was extracted from an internal research codebase; this entry
summarises the feature set by area. Everything runs on plain .NET 8; the Hybridizer paths (CUDA) are optional and are
described in [docs/gpu.md](docs/gpu.md).

### Added

- **Packaging.** Fourteen NuGet packages (`InFine.QuantFinance.*`, see [docs/packaging.md](docs/packaging.md)) with symbol
  packages (`.snupkg`), SourceLink to the exact commit, XML documentation, README and icon, versioned from git tags with
  MinVer. A release workflow builds, tests, packs, publishes to nuget.org and creates the GitHub Release.
- **Kernel-compatible foundations.** Executors (`SequentialExecutor`, `SingleBlockParallelExecutor`,
  `MultiBlockParallelExecutor`), `alignedindex` and reductions that run the same loop sequentially, on threads or as a
  CUDA kernel through Hybridizer (`Scheduling`). Concept interfaces (spot, Brownian, schedule, discounting, rate,
  volatility) composed through generics, so each combination is specialised without virtual calls (`Models`).
- **Closed-form pricing** (`ClosedForm`). Black-Scholes-Merton and Black-76 prices with analytic Greeks (delta, gamma,
  vega, theta, rho, dividend rho, vanna, volga, dual delta), robust normal distribution (Cody erfc, AS241 inverse),
  implied volatility, barrier, geometric Asian and lookback options, Heston by Fourier cosine with calibration,
  Hull-White zero-coupon bond options, caps/floors and swaptions (Jamshidian) with calibration, multi-asset references
  (Margrabe, Stulz, geometric basket).
- **Monte Carlo** (`Random`, `Models`, `Payoffs`, `LongstaffSchwartz`). MRG32K3A, Philox and scrambled Sobol generators
  (Joe-Kuo direction numbers), Brownian bridge, MKL and cuRAND wrappers. Black-Scholes-Merton, Heston, correlated
  multi-asset log-normal and one-factor Hull-White diffusions, curve-based discounting. Payoffs: European, Asian,
  barrier and lookback options, variance swaps, baskets, best-of / worst-of and Athena / Phoenix autocallables with a
  control variate for Asians. Longstaff-Schwartz pricing of American options.
- **Sensitivities** (`Arithmetic`, `Models`, `Payoffs`, `ClosedForm`). Forward-mode dual numbers (`Dual`, `Dual4`) and
  dual counterparts of the Black-Scholes pricers and diffusions; pathwise Monte Carlo Greeks; hand-written adjoint
  (reverse-mode) Greeks for log-normal, correlated log-normal and Heston paths, which give every first-order
  sensitivity in one backward sweep (measured cost 1.4 valuations for 110 sensitivities).
- **Market objects** (`Market`). Dates, holiday calendars (weekends, TARGET, NYSE, London, joint), day counters,
  schedule generation, deposits, FRAs, swaps, bonds, bootstrapped and interpolated yield curves, and the compilation of
  a curve and a schedule onto a simulation grid for the kernels.
- **Volatility surfaces** (`Volatility`). Raw SVI and SSVI slices, butterfly and calendar arbitrage checks, calibration
  of one expiry or a whole surface (arbitrage-free by construction with SSVI), surfaces with Black-76 prices.
- **Risk** (`Risk`). Returns, sample / EWMA / Ledoit-Wolf covariance, historical and parametric VaR and ES with Euler
  decomposition, Monte Carlo VaR by full revaluation (host and GPU kernels, Philox or Sobol), scenario stress tests with
  attribution and reverse stress.
- **Portfolio construction** (`Portfolio`). Risk parity and risk budgeting, a Goldfarb-Idnani quadratic-programming
  solver, mean-variance optimisation with constraints (frontier, maximum Sharpe, tracking error), Black-Litterman.
- **Time series** (`TimeSeries`). Performance statistics (return, volatility, Sharpe, Sortino, drawdown, Calmar,
  rolling measures), GARCH(1,1) maximum-likelihood estimation with normal or Student-t innovations, forecasts and
  simulation, factor regression and factor covariance models.
- **Backtesting** (`Backtesting`). A return panel with no look-ahead, built-in strategies, a rebalancing engine with
  costs, turnover, cash and leverage, and a parallel stationary block bootstrap (also as a GPU kernel).
- **Numerical building blocks** (`Numerics`). Interpolation kernels, Levenberg-Marquardt, BFGS, finite differences,
  Cholesky (plain, pivoted, robust), symmetric eigen-decomposition, nearest correlation matrix.
- **Excel add-in** (not a NuGet package). `QF.*` worksheet functions for pricing, implied volatility, dates, curves,
  SVI fitting and portfolio risk, shipped as an `.xll`; see [docs/excel.md](docs/excel.md).
- **GPU.** On-demand CUDA satellite generation with Hybridizer on Windows and Linux, GPU = CPU checks of every kernel
  entry point, nightly GPU CI; see [docs/gpu.md](docs/gpu.md).

### Known limitations

- Only equity-style products and a short-rate model exist; no multi-curve rates, credit or FX coverage.
- The CUDA satellite (`InFine.QuantFinance_CUDA.dll`) is generated from the repository and is not part of the NuGet
  packages. The packages reference `Hybridizer.Runtime.CUDAImports` 3.7.1, an AMD64-stamped assembly: code that touches
  Hybridizer types needs an x64 process.
- Longstaff-Schwartz cannot run on the device yet (it hands host-created delegates to its kernels).
- AVX / OMP binaries are not produced.
- XML documentation is incomplete: some public members have no summary and some parameters are undocumented.

[Unreleased]: https://github.com/infineco/quant-finance-library/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/infineco/quant-finance-library/releases/tag/v0.1.0
