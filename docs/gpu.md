# GPU guide

This guide explains what runs on an NVIDIA GPU in this library, how to build and run it, what it costs and gains, and
which limits to expect. All numbers come from the two verification reports,
[Windows](verification-windows-gpu.md) and [Linux](verification-linux-cuda.md) (both in French).

## What runs on the GPU, and how

- The compute kernels are plain C# (closed-form pricers, Heston COS, implied volatility, scenario diffusion, payoffs,
  full-revaluation VaR, bootstrap backtesting). [Hybridizer](https://hybridizer.io/) compiles
  them to CUDA.
- The result is a native **satellite library**, `InFine.QuantFinance_CUDA.dll` (an ELF shared object on Linux, same
  file name). It is built on demand by the `InFine.QuantFinance.Cuda` project and is not part of the normal build.
- At run time, `HybRunner.Cuda(path).SetDistrib(...).Wrap(kernelObject)` returns a proxy of a kernel class whose
  entry points execute on the device. In the tests, `CudaBackend.Launcher` is wired to such a runner by `CudaLauncher`
  in `InFine.QuantFinance.Coherence.Tests`, so the library simulatables run on the GPU. Load the satellite by its
  **full path** (`NativeEnvironment.CudaSatellitePath`): on Linux, `dlopen` of a bare name does not search the
  application folder.
- **Same results as the CPU.** Every GPU entry point is checked against its CPU counterpart. Measured differences:

| Entry point | Tolerance | Observed difference |
| --- | --- | --- |
| Black-Scholes / Black-76 prices (100,003 options) | 1e-12 relative | 2.7e-13 |
| Black-Scholes / Black-76 Greeks | 1e-10 relative | 5.0e-12 and 1.7e-12 (theta) |
| Implied volatility (Black-76) | 1e-10 relative | 1.4e-15 |
| Heston COS (20,011 options) | 1e-9 of the forward | 2.9e-15 of the forward |
| Diffusions (Black-Scholes, Hull-White, correlated log-normal) | 1e-11 absolute | 8.9e-16, 2.8e-17, 8.9e-16 |
| Path-dependent payoffs (European, Asian, barrier, lookback, basket, rainbow, autocallable, variance swap) | 1e-12 relative | 8.8e-16 to 2.2e-13 |
| Constant-mix bootstrap (100,003 paths) | 1e-12 relative | 0, except variance 7.2e-16 |
| Full revaluation, Philox and Sobol (65,536 scenarios) | 1e-10 of max abs P&L | within tolerance |

Windows (RTX 4070 Laptop) and Linux (RTX 3060 Ti) ran the same tests with the same outcome: 1556 passed, 10 skipped,
0 failed. The Philox random words are identical bit for bit between host and device (observed indirectly through the
bootstrap, whose results match to the bit).

## Hybridizer editions and terms

- The library itself is licensed under **MIT**.
- Hybridizer is a separate product. Its **community edition is free to use**. For **commercial use,
  contact the publisher**: <https://hybridizer.io/>.
- Limits of the community edition, as observed during verification:
  - **CUDA flavour only**: no AVX or OMP binaries, so the AVX/OMP tests stay skipped.
  - **No delegate support**: Longstaff-Schwartz passes host-created delegates to its kernels, so it **cannot run on the
    GPU yet** (its two GPU tests are skipped with that reason). The CPU paths are covered. A rewrite with generic
    functors is the proposed fix.

## Requirements

- An NVIDIA GPU and driver, and the CUDA toolkit (13.0 was used).
- .NET SDK 8.
- The Hybridizer tool: `dotnet tool install -g Hybridizer --version 7.6.3`.
- Windows: Visual Studio C++ tools (2022 was used; the targets locate them with `vswhere`/`vcvarsall`) and `CUDA_PATH`.
- Linux: Docker with the NVIDIA runtime (`--gpus all`); nothing is installed on the host.

The device code is JIT-compiled by nvrtc for **the GPU present at build time** (`--gpu-architecture=auto`). The
satellite is therefore not portable across GPU architectures: build it on the GPU model that will run it.

## Quickstart

### Windows

```sh
dotnet tool install -g Hybridizer --version 7.6.3
dotnet build InFine.QuantFinance.Cuda/InFine.QuantFinance.Cuda.csproj -c Release -p:HybridizeCuda=true
```

This hybridizes the ten kernel assemblies, compiles the generated sources with nvcc, and produces the satellite
(2.3 MB). Measured on Windows: about 58 s of generation and nvrtc JIT, then 17 s of nvcc. Without the property the
project only compiles, so the solution builds anywhere (CI included) without Hybridizer, CUDA or a GPU.

### Linux, with Docker

The environment is fixed in `build/hybridizer/linux/Dockerfile` (`nvidia/cuda:13.0.0-devel-ubuntu24.04`, .NET 8,
Hybridizer 7.6.3):

```sh
docker build -t qfl-cuda build/hybridizer/linux
docker run --rm --gpus all -v "$PWD":/work -w /work qfl-cuda bash -lc \
  "dotnet build InFine.QuantFinance.Cuda/InFine.QuantFinance.Cuda.csproj -c Release -p:HybridizeCuda=true \
   && dotnet build QuantFinance.sln -c Release && dotnet test QuantFinance.sln -c Release --no-build"
```

Generation took about 32 s on Linux.

### Where the satellite lands

`InFine.QuantFinance_CUDA.dll` is built in `InFine.QuantFinance.Cuda/bin/<Configuration>/net8.0/` and copied to
`artifacts/cuda/`. `Directory.Build.targets` then copies it next to the test and benchmark assemblies on their next
build.

### Run the GPU tests and benchmarks

```sh
dotnet test QuantFinance.sln -c Release

# GPU workloads of several seconds each (about 10 minutes)
dotnet run -c Release --project benchmarks/InFine.QuantFinance.Benchmarks -- --filter '*LongGpu*' --inProcess
# shorter GPU suites
dotnet run -c Release --project benchmarks/InFine.QuantFinance.Benchmarks -- --filter '*Gpu*' --inProcess
```

The GPU tests are `Cuda_*` in Coherence.Tests, `ClosedFormCudaTests`, `FullRevaluationCudaTests` (Philox and Sobol),
`BacktestCudaTests` and `TestMethod_MRG32K3A_VsCuda`. Without the satellite or a device they are skipped, as are the GPU
benchmarks. See [the benchmarks README](../benchmarks/InFine.QuantFinance.Benchmarks/README.md).

## Performance

BenchmarkDotNet 0.15.8, default job, `--inProcess`, .NET 8. GPU times are **end to end**: `HybRunner` copies the arrays to
the device and back on every call. CPU uses all cores. Both sides are checked against each other before measuring.

| Machine | CPU | GPU |
| --- | --- | --- |
| Windows | Ryzen 9 7845HX, 24 threads | RTX 4070 Laptop (sm_89, 36 SM, 8 GB) |
| Linux | Ryzen 7 5800X, 16 threads | RTX 3060 Ti (sm_86, 38 SM, 8 GB) |

Short workloads:

| Workload | Machine | CPU all cores | GPU end to end | GPU kernel only |
| --- | --- | ---: | ---: | ---: |
| Black-Scholes, 1M options | Windows | 5.9 ms | 23.9 ms | 12.5 ms |
| Black-Scholes, 1M options | Linux | 7.4 ms | 23.5 ms | 13.1 ms |
| VaR full revaluation, M = 100k, N = 100, F = 101 | Windows | 74.3 ms | 46.3 ms | 36.3 ms |
| VaR full revaluation, M = 100k | Linux | 111 ms | 68.4 ms | 57 ms |
| VaR full revaluation, M = 1M | Windows | 740 ms | 303 ms | 292 ms |
| VaR full revaluation, M = 1M | Linux | 1018 ms | 471 ms | 460 ms |
| Bootstrap, 100k paths x 2520 days, 10 assets | Windows | 604 ms | 275 ms | 269 ms |
| Bootstrap, 100k paths x 2520 days, 10 assets | Linux | 831 ms | 321 ms | n/a |

Long workloads (`LongGpuBenchmarks`, 1 warm-up and 3 measured iterations):

| Workload | Machine | CPU all cores | GPU end to end | GPU / CPU |
| --- | --- | ---: | ---: | ---: |
| VaR full revaluation, 20M scenarios x 100 options x 101 factors | Windows | 12.96 s | 5.73 s | x2.3 |
| VaR full revaluation, 20M scenarios | Linux | 19.75 s | 9.18 s | x2.2 |
| Bootstrap, 1M paths x 2520 days x 10 assets | Windows | 5.27 s | 2.62 s | x2.0 |
| Bootstrap, 1M paths x 2520 days x 10 assets | Linux | 7.55 s | 3.03 s | x2.5 |
| Heston COS, 2M options | Windows | 8.67 s | 3.88 s | x2.2 |
| Heston COS, 2M options | Linux | 14.88 s | 4.41 s | x3.4 |

Full-revaluation throughput at M = 1M: 3.3 M scenarios/s on the RTX 4070 Laptop and 2.1 M scenarios/s on the RTX 3060 Ti,
against 1.35 M scenarios/s (Ryzen 9, 24 threads) and 0.98 M scenarios/s (Ryzen 7, 16 threads) on the CPUs.

How to read these numbers honestly:

- The kernels are **double precision**, and a consumer GPU computes double at 1/64 of its single-precision rate. The gain
  on heavy workloads is **x2 to x3.4**, not an order of magnitude. A data-centre GPU (A100, H100) or a move to single
  precision would change the ratio; neither was measured.
- On small, transfer-bound workloads the GPU can lose: Black-Scholes on 1M options is faster on 24 CPU threads
  (5.9 ms against 23.9 ms), because about half of the GPU time goes to copying eight 8 MB arrays.
- The Linux Heston gain is larger mostly because that CPU has 8 cores against 12 on the Windows laptop.
- The RTX 3060 Ti is about 1.5 times slower than the RTX 4070 Laptop on the VaR workload.
- Cold first runs overstated the ratio (x4 to x5) because the first CPU pass pays JIT and clock ramp-up; the reliable
  figure is x2 to x2.4 on the Windows machine.
- Hybridizer 7.5.1 and 7.6.3 gave the same timings on Linux, within measurement noise.

## Known issues

- **Array fields in structs break CUDA generation on Linux** ([hybridizer-io/hybridizer-software-suite#338](https://github.com/hybridizer-io/hybridizer-software-suite/issues/338)).
  Hybridizer's `FieldTools.GetFields` uses `Marshal.OffsetOf`, which fails for array fields in structs on Linux (the
  default marshalling is SAFEARRAY, Windows only); the field list comes back empty and generation stops with
  `0X6099 : Cannot identify fields in type YieldCurve`. Not fixed in 7.6.3. Workaround here: the legacy
  `OptimizedFixedCashFlowsPricer`, which no test or benchmark runs on the device, is marked `[HybridizerIgnore]`. Any
  struct reachable from a kernel and holding an array would break Linux generation the same way.
- **Runtime races on host copies (`alignedstorage`)**, in `Hybridizer.Runtime.CUDAImports` 3.7.1:
  - lazy host allocation and refresh are not synchronised, and marshalling re-uploads a stale host buffer even when the
    device holds the current data. A GPU diffusion followed by a GPU payoff on the same paths silently used stale
    paths (526 wrong premiums out of 1009, no error);
  - when several threads do the first host read of a storage written by the device, the device-to-host copies fail
    with CUDA error 700, and the CUDA context becomes unusable.
  - Workaround: `CudaLauncher` refreshes stale host copies before and after each launch, on the launching thread, at the
    cost of one device-to-host copy per step. Application code that chains launches or reads results from several
    threads should do the same. The proper fix belongs in the runtime (a lock in `CheckRefreshHost`, and honouring the
    status when marshalling).
- **Hybridizer 7.6.3 is verified on Windows and Linux** (see the 7.6.3 sections of the two verification reports).
  `-p:HybridizerToolVersion=7.5.1` still selects the previous tool if needed.
- **Not portable satellite**: see Requirements (one architecture per build). Building for several architectures was not
  tested.
- **Not tested**: other GPUs than the two above, `Dual`/`Dual4` on the device (no entry point uses them yet), and
  `double.IsNegativeInfinity` in device code (mistranslated by the shipped builtins).
