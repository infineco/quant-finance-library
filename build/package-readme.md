# InFine.QuantFinance

A C# quantitative-finance library for buy-side quants: pricing, Monte Carlo, risk, portfolio construction,
time series and backtesting. The same C# code runs on the plain .NET runtime and, compiled through
[Hybridizer](https://hybridizer.io/), on CUDA GPUs.

## What you get from this package

This package is one part of a set of libraries that share the `InFine.QuantFinance.` prefix and the same
version. Each package depends only on the ones it needs, so adding the most specific package pulls in the rest.

- Everything runs on plain .NET 8 on any operating system. Nothing requires a GPU, Hybridizer or a licence to use.
- GPU execution is optional. Kernel code is written so that Hybridizer can translate it to CUDA; the CUDA
  binary is generated from the repository (see the [GPU guide](https://github.com/infineco/quant-finance-library/blob/main/docs/gpu.md)),
  it is not shipped in the packages.
- The packages reference `Hybridizer.Runtime.CUDAImports`, a managed assembly that provides the
  kernel attributes and thread-index types. It is stamped AMD64: code that touches those types needs an x64
  process, the rest runs anywhere.

## Example

```csharp
using InFine.QuantFinance.ClosedForm;

var greeks = BlackScholes.Greeks(OptionType.Call,
    spot: 100.0, strike: 100.0, maturity: 1.0, rate: 0.05, dividendYield: 0.0, volatility: 0.2);
Console.WriteLine($"price = {greeks.Price:F4}, delta = {greeks.Delta:F4}");
```

## Links

- Source, documentation, GPU guide and samples: <https://github.com/infineco/quant-finance-library>
- Changelog: <https://github.com/infineco/quant-finance-library/blob/main/CHANGELOG.md>
- Issues: <https://github.com/infineco/quant-finance-library/issues>

MIT licence. The library is early (0.x): expect API changes between minor versions.
