# InFine.QuantFinance

InFine.QuantFinance is a C# quantitative-finance library: closed-form pricers, Monte Carlo models,
Longstaff-Schwartz American option pricing, volatility surfaces, risk measures, portfolio analytics,
time series and backtesting. The numerical core is written once in C# and, through
[Hybridizer](https://hybridizer.io/), the same code is also compiled to CUDA kernels
to run on NVIDIA GPUs.

GPU execution uses the Hybridizer Essentials community edition, which is free. For commercial use of the
GPU path, contact the publisher (InFine).

## Where to start

- [Architecture](../docs/architecture.md): how the projects fit together and how code reaches the GPU.
- [GPU execution](../docs/gpu.md) and [GPU CI](../docs/gpu-ci.md): running on CUDA and testing it.
- [Excel add-in](../docs/excel.md): using the library from Excel workbooks.
- API reference (top navigation): every public namespace, type and member.
- Verification reports: [Linux and CUDA](../docs/verification-linux-cuda.md),
  [Windows and GPU](../docs/verification-windows-gpu.md).

## Samples and Excel workbooks

Runnable sample projects live in the [`samples/`](https://github.com/infineco/quant-finance-library/tree/main/samples)
folder, and ready-to-use Excel workbooks in
[`samples/excel`](https://github.com/infineco/quant-finance-library/tree/main/samples/excel).
