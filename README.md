# InFine.QuantFinance

**GPU-accelerated quantitative finance in C# — powered by [Hybridizer](https://www.altimesh.com/hybridizer-essentials/).**

Write a pricer, a risk engine or a backtest once in C#. It runs on the plain .NET runtime, and Hybridizer compiles the
very same code to CUDA kernels for NVIDIA GPUs.

[![CI](https://github.com/infineco/quant-finance-library/actions/workflows/ci.yml/badge.svg)](https://github.com/infineco/quant-finance-library/actions/workflows/ci.yml)
[![CodeQL](https://github.com/infineco/quant-finance-library/actions/workflows/codeql.yml/badge.svg)](https://github.com/infineco/quant-finance-library/actions/workflows/codeql.yml)
[![GPU (nightly)](https://github.com/infineco/quant-finance-library/actions/workflows/gpu.yml/badge.svg)](docs/gpu-ci.md)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/InFine.QuantFinance.ClosedForm.svg?label=NuGet)](https://www.nuget.org/packages?q=InFine.QuantFinance)
[![Docs](https://img.shields.io/badge/docs-GitHub%20Pages-blue)](https://infineco.github.io/quant-finance-library/)


## Why this library exists

It was born as an educational project for the consultants of In Fine (quants and quant IT
engineers), to show them how quantitative-finance computations can be accelerated: in C# on the CPU, and on the GPU
through Hybridizer. It grew into a library that is now used in several financial institutions, and it is released
here under the MIT licence so that anyone can read it, use it and learn from it.

If you want to see what accelerating a Monte Carlo VaR, a Heston calibration or a bootstrap backtest looks like in
plain C#, with the CPU and GPU results checked against each other, this repository is meant to be that example.

## What is in it

- **Pricing and Greeks.** Black-Scholes-Merton and Black-76 with analytic Greeks (delta, gamma, vega, theta, rho,
  dividend rho, vanna, volga), implied volatility, barrier, geometric Asian and lookback options, Heston by Fourier
  cosine with calibration. Algorithmic differentiation: forward-mode duals, pathwise Monte Carlo Greeks and hand-written
  adjoint (reverse-mode) Greeks (110 sensitivities for 1.36 valuations in the [benchmark](docs/architecture.md)).
- **Volatility.** SVI and SSVI slices and surfaces, butterfly and calendar arbitrage checks, calibration of one expiry
  or of a whole surface that is arbitrage-free by construction (SSVI).
- **Rates and curves.** Dates, holiday calendars (TARGET, NYSE, London), day counters, schedules, deposits, FRAs, swaps,
  bonds, bootstrapped yield curves; one-factor Hull-White with caps, floors, swaptions (Jamshidian) and calibration.
- **Exotics and multi-asset.** Monte Carlo (MRG32K3A, Philox, scrambled Sobol, Brownian bridge) for European, Asian,
  barrier and lookback options, variance swaps, baskets, best-of / worst-of, Athena and Phoenix autocallables; correlated
  log-normal and Heston diffusions; Longstaff-Schwartz American options; Margrabe, Stulz and geometric-basket references.
- **Risk.** Sample, EWMA and Ledoit-Wolf covariance, historical and parametric VaR and ES with Euler decomposition,
  Monte Carlo VaR by full revaluation, scenario stress tests with attribution and reverse stress.
- **Portfolio construction.** Risk parity and risk budgeting, a Goldfarb-Idnani quadratic-programming solver,
  mean-variance optimisation with constraints (frontier, maximum Sharpe, tracking error), Black-Litterman.
- **Time series and backtesting.** Performance statistics, GARCH(1,1) by maximum likelihood, factor regression; a
  backtesting engine without look-ahead (costs, turnover, cash, leverage) and a parallel stationary block bootstrap.
- **Excel add-in.** `QF.*` worksheet functions for pricing, implied volatility, dates, curves, SVI fitting, VaR / ES and
  Monte Carlo VaR, shipped as an `.xll` ([guide](docs/excel.md)).

Details by area: [docs/architecture.md](docs/architecture.md). What each release adds: [CHANGELOG.md](CHANGELOG.md).

## Same C# on CPU and GPU

The compute kernels are ordinary C# (no GPU-specific language): no allocation, no exceptions, behaviour composed through
generics. Hybridizer turns them into a CUDA satellite library, and every GPU entry point is tested against its CPU
counterpart (for example Black-Scholes prices on 100,003 options agree to 2.7e-13 relative; the Philox random words and
the bootstrap results match bit for bit). The full suite gives 1556 passed, 10 skipped, 0 failed on both a Windows and a
Linux GPU machine ([how](docs/gpu.md)).

Measured speed-up on heavy workloads, GPU end to end (host-device copies included) against all CPU cores, BenchmarkDotNet
0.15.8, .NET 8, double precision:

| Workload | CPU (all cores) | GPU | GPU / CPU |
| --- | ---: | ---: | ---: |
| Full-revaluation VaR, 20M scenarios x 100 options x 101 factors | 12.96 s | 5.73 s | x2.3 |
| Bootstrap backtest, 1M paths x 2520 days x 10 assets | 5.27 s | 2.62 s | x2.0 |
| Heston COS, 2M options | 8.67 s | 3.88 s | x2.2 |
| Black-Scholes, 1M options (transfer-bound) | 5.9 ms | 23.9 ms | slower on GPU |

Hardware: Ryzen 9 7845HX (24 threads) with an RTX 4070 Laptop (8 GB), Windows. On a Ryzen 7 5800X (16 threads) with an
RTX 3060 Ti (8 GB), Linux, the ratios are x2.2, x2.5 and x3.4 for the three heavy workloads
([full tables](docs/gpu.md#performance)).

Read these numbers honestly. The kernels are double precision, and a consumer GPU computes double at 1/64 of its
single-precision rate, so the gain is x2 to x3.4, not an order of magnitude. A data-centre GPU or single precision would
change the ratio; neither was measured. Small, transfer-bound workloads can run faster on the CPU.

## Quick start

You need the [.NET SDK 8](https://dotnet.microsoft.com/download) (`global.json` pins 8.0.100 and rolls forward). Nothing
else is required to build, test and run the samples on the CPU.

```sh
git clone https://github.com/infineco/quant-finance-library.git && cd quant-finance-library
dotnet build QuantFinance.sln -c Release
dotnet test QuantFinance.sln -c Release          # GPU/MKL tests are skipped, not failed, when the hardware is absent
dotnet run -c Release --project samples/01-OptionPricing
```

[`samples/`](samples/README.md) has five console programs with expected output: option pricing and Greeks, a yield-curve
bootstrap, a volatility surface, Monte Carlo VaR (CPU, and GPU when available) and a backtest.

The same price from your own code:

```csharp
using InFine.QuantFinance.ClosedForm;

OptionGreeks call = BlackScholes.Greeks(OptionType.Call, spot: 100, strike: 100, maturity: 1.0,
                                        rate: 0.025, dividendYield: 0.01, volatility: 0.20);
double vol = ImpliedVolatility.BlackScholes(100, 100, 1.0, 0.025, 0.01, call.Price, OptionType.Call);
// call.Price = 8.586530, call.Delta = 0.563794, vol = 0.20 (to machine precision)
```

### On a GPU (Docker, Linux)

With an NVIDIA GPU and the NVIDIA Container Toolkit, one command generates the CUDA kernels with Hybridizer 7.6.3, builds
the solution and runs the whole suite on the device:

```sh
docker build -t qfl-cuda build/hybridizer/linux
docker run --rm --gpus all -v "$PWD":/work -w /work qfl-cuda bash -lc \
  "dotnet build InFine.QuantFinance.Cuda/InFine.QuantFinance.Cuda.csproj -c Release -p:HybridizeCuda=true \
   && dotnet build QuantFinance.sln -c Release && dotnet test QuantFinance.sln -c Release --no-build"
```

A prebuilt image is also published (see [docs/gpu-ci.md](docs/gpu-ci.md)). The device code is compiled for the GPU
present at build time, so build and run on the same GPU model. On Windows, and for requirements, the benchmarks and known
issues, see the [GPU guide](docs/gpu.md). Sample 04 then shows the CPU/GPU comparison on your machine.

### In Excel (Windows, 64-bit Excel)

```sh
dotnet build InFine.QuantFinance.Excel/InFine.QuantFinance.Excel.csproj -c Release
```

Load the packed `.xll` from `InFine.QuantFinance.Excel/bin/Release/net8.0-windows/publish/` (*File > Options > Add-ins >
Go... > Browse*; the .NET 8 Desktop Runtime x64 is required), then open the five ready-made workbooks in
[`samples/excel`](samples/excel): pricing and Greeks, an implied-volatility smile with an SVI fit, a bootstrapped yield
curve, portfolio VaR / ES, and Monte Carlo VaR. If cells show `#NAME?`, press Ctrl+Alt+F9. Install notes, conventions and
the function reference: [docs/excel.md](docs/excel.md).

## NuGet packages

Fourteen libraries are packaged as `InFine.QuantFinance.<Name>` (`Arithmetic`, `Scheduling`, `Numerics`, `Random`,
`ClosedForm`, `Models`, `Payoffs`, `LongstaffSchwartz`, `Market`, `Volatility`, `Risk`, `Portfolio`, `TimeSeries`,
`Backtesting`), versioned from git tags and published on [nuget.org](https://www.nuget.org/packages?q=InFine.QuantFinance) (the
`.nupkg` files are also attached to each [GitHub release](https://github.com/infineco/quant-finance-library/releases)):

```sh
dotnet add package InFine.QuantFinance.ClosedForm
```

Dependencies, contents and the release procedure: [docs/packaging.md](docs/packaging.md). The packages run on plain .NET;
the CUDA satellite and the Excel add-in are not NuGet packages.

## Project map

All projects are at the repository root unless a folder is given. `research/` is prototype material, not built by the
solution.

| Project | Content |
| --- | --- |
| `Arithmetic` | Forward-mode dual numbers (`Dual`, `Dual4`). |
| `Scheduling` | Executors (sequential, single- and multi-block parallel), `alignedindex`, reductions. |
| `Numerics` | Allocation-free interpolation and curve kernels, Levenberg-Marquardt, BFGS, Cholesky, eigen-decomposition, nearest correlation matrix. |
| `Random` | MRG32K3A, Philox, scrambled Sobol, Brownian bridge, MKL and cuRAND wrappers. |
| `ClosedForm` | Black-Scholes-Merton, Black-76, implied volatility, Heston, Hull-White, barrier / Asian / lookback, multi-asset references. |
| `Models` | Kernel concepts and diffusions (Black-Scholes-Merton, Heston, correlated log-normal, Hull-White) with their adjoints. |
| `Payoffs` | Monte Carlo payoffs, pathwise and adjoint Greeks. |
| `LongstaffSchwartz` | Least-squares Monte Carlo for American options. |
| `Market` | Host-side dates, calendars, day counters, schedules, yield curves. |
| `Volatility` | SVI / SSVI surfaces, arbitrage checks, calibration. |
| `Risk` | Covariance estimators, VaR / ES, Monte Carlo VaR, stress tests. |
| `Portfolio` | Risk parity, quadratic programming, mean-variance, Black-Litterman. |
| `TimeSeries` | Performance statistics, GARCH, factor regression. |
| `Backtesting` | Backtesting engine, strategies, stationary bootstrap. |
| `Cuda` | Builds the native CUDA satellite with Hybridizer, on demand (`-p:HybridizeCuda=true`). |
| `Excel`, `Excel.Core` | The Excel-DNA add-in and the cross-platform logic behind its `QF.*` functions. |
| `TestSupport`, `*.Tests`, `Coherence.Tests` | xUnit tests, including the GPU = CPU checks; tests needing a GPU or MKL are skipped when absent. |
| [`benchmarks/`](benchmarks/InFine.QuantFinance.Benchmarks/README.md) | BenchmarkDotNet suites (closed form, diffusion, adjoint Greeks, CPU vs GPU). |
| [`samples/`](samples/README.md) | Console samples and Excel workbooks. |
| [`tools/`](tools) | Generator of the Excel sample workbooks. |

All project names start with `InFine.QuantFinance.`. The design (concepts pattern, executors, memory layout, kernels
versus host code) is described in [docs/architecture.md](docs/architecture.md).

## Status and known limitations

Pre-1.0: the public API may change between minor versions. Equity-style products and a short-rate model are covered;
there is no multi-curve rates, credit or FX coverage. See the [roadmap](ROADMAP.md).

- **Hybridizer community edition.** It is free to use; for commercial use, contact the publisher
  ([Altimesh](https://www.altimesh.com/hybridizer-essentials/)). Its limits: CUDA only (no AVX or OMP binaries), and no
  delegate support, so Longstaff-Schwartz runs on the CPU only for now (a functor-based rewrite is planned).
- **Runtime on arm64.** `Hybridizer.Runtime.CUDAImports` 3.7.1 is stamped AMD64 and does not load in an arm64 process
  (for example Apple Silicon). Code that touches Hybridizer types needs an x64 process; the other code, and the tests
  that need nothing from Hybridizer, run natively on arm64.
- **CUDA only, one architecture per build,** and the GPU runtime has known races on host copies that the library works
  around; the full list is in the [GPU guide](docs/gpu.md#known-issues).
- Only two GPUs were tested (RTX 4070 Laptop, RTX 3060 Ti). XML documentation is incomplete.

## Contributing, security, conduct

Contributions are welcome: read [CONTRIBUTING.md](CONTRIBUTING.md) first. Report vulnerabilities privately as described in
[SECURITY.md](SECURITY.md), not in public issues. All project spaces follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Licence

The library is released under the [MIT licence](LICENSE). Third-party components and their licences:
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Hybridizer is a separate product from Altimesh with its own terms: the community edition is free to use, and commercial
use requires contacting the publisher (<https://www.altimesh.com/hybridizer-essentials/>).

## Acknowledgements

- [Hybridizer](https://www.altimesh.com/hybridizer-essentials/) by Altimesh, which makes the C#-to-CUDA path possible.
- [Excel-DNA](https://excel-dna.net) for the add-in host, [BenchmarkDotNet](https://benchmarkdotnet.org) for the
  measurements, [MinVer](https://github.com/adamralph/minver) for versioning.
- The authors of the algorithms the code implements and cites in its documentation, among them Jaeckel ("Let's Be
  Rational"), Gatheral and Jacquier (arbitrage-free SVI), Giles and Glasserman ("Smoking adjoints"), Ledoit and Wolf,
  Higham, Spinu, Goldfarb and Idnani, and Politis and Romano.
