# Verification on Windows x64, NVIDIA GPU and Excel

This report covers what could not be verified on the arm64 development Mac (no GPU, no Windows Excel) or on the ubuntu
CI: the Hybridizer-related tests on x64, CUDA generation, GPU = CPU equality, GPU performance and the Excel add-in.
Branch `verif/windows-gpu`, taken from `main` after PRs #62 and #63 were merged.

## Environment

| Item | Version |
| --- | --- |
| OS | Windows 11 Home 10.0.26300, x64 |
| CPU | AMD Ryzen 9 7845HX, 12 cores / 24 threads, 63 GB RAM (laptop, on mains power) |
| GPU | NVIDIA GeForce RTX 4070 Laptop GPU, sm_89, 36 SMs, 8 GB, WDDM mode |
| NVIDIA driver | 610.74 (CUDA UMD 13.3) |
| CUDA toolkit | 13.0 (nvcc V13.0.48), `CUDA_PATH` set |
| .NET | SDK 8.0.425 (installed during the verification), 9.0.318, 10.0.401; runtimes 8.0.31, 9.0.20, 10.0.12 |
| Hybridizer | global tool `hybridizer` 7.5.1, "Hybridizer Essentials", **free edition**: CUDA flavor only, JIT compilation by nvrtc; runtime `Hybridizer.Runtime.CUDAImports` 3.7.1 |
| nvcc host compiler | Visual Studio Community 2022 17.14 (C++ tools); VS 2026 18.10 also installed |
| Excel | Microsoft 365 (O365BusinessRetail), 16.0 build 20430, **64-bit**, French interface |
| Intel MKL | **absent** |

The .NET 8 SDK was missing at the start: the first pass was built with SDK 10 (bypassing `global.json`).
It was then installed (`winget install Microsoft.DotNet.SDK.8`, 8.0.425); build, CUDA satellite and tests were
redone with it, with the same result (1556 passed, 10 skipped, 0 failed).

Missing, not installed:

- **Intel MKL** (optional): required by 7 tests (`mkl_rt`). Suggested installation: Intel oneMKL, for example through
  `winget search oneMKL` then `winget install <id found>`, or the Intel oneAPI Base Toolkit installer.

## Summary

| Item | Status |
| --- | --- |
| x64 baseline: build and tests, `[HybridizerFact]` executed | verified |
| CUDA generation by Hybridizer (opt-in step) | fixed (9 tool or library problems worked around), verified |
| GPU = CPU equality, all requested families except Longstaff-Schwartz | verified |
| Longstaff-Schwartz on GPU | **failed**, blocked by the free edition of Hybridizer (delegates) |
| Hybridizer runtime races on the GPU side | two data losses found, worked around, documented |
| BenchmarkDotNet benchmarks, all suites | verified |
| GPU vs CPU | measured |
| Excel add-in in a real 64-bit Excel | verified (64/64), one documented behavior corrected |

Commits on the branch (one per problem, Lore format):

1. Reductions with `Action<int, int, T[]>` delegates excluded from generation (Hybridizer emits a class deriving from
   `::System::Array`).
2. Longstaff-Schwartz regressor without `fixed` buffers (Hybridizer 7.5 generates undeclared pinned variables).
3. `SimulatableScheduler.SimulateSingle`: 64-bit block bounds (257 paths out of 1,048,583 never simulated with 4096
   blocks).
4. Ticket #60: `EuropeanCallOption<HullWhiteDiscounting, …>` registered.
5. ClosedForm kernel classes made instantiable, so that `HybRunner.Wrap` reaches their entry points.
6. Opt-in Hybridizer build step (`InFine.QuantFinance.Cuda`, `build/hybridizer`).
7. GPU = CPU tests and wiring of `CudaBackend.Launcher`.
8. Refresh of host copies after each launch (runtime race, CUDA error 700).
9. Excel smoke test through COM and correction of `docs/excel.md`.
10. GPU full-revaluation test extended to Sobol (`uint[]`).
11. GPU vs CPU benchmarks, README and this report.

## 1. x64 baseline

`dotnet build QuantFinance.sln -c Release`: 0 errors. `dotnet test` on `main` before any change:

| Passed | Skipped | Failed |
| ---: | ---: | ---: |
| 1539 | 13 | 0 |

The `[HybridizerFact]` / `[HybridizerTheory]` tests, skipped on arm64, all run on x64. The 13 skipped tests were all
waiting for MKL or a Hybridizer-generated binary. At the end of the branch:

| Passed | Skipped | Failed |
| ---: | ---: | ---: |
| 1556 | 10 | 0 |

Remaining skipped tests, each with its reason:

| Test | Reason |
| --- | --- |
| `TestMethod_MRG32K3A_MKL`, `TestMethod_MRG32K3A_MKL_GET_NAN`, `AmericanCall_CSharp`, `AmericanCall_CUDA` | MKL absent |
| `AmericanCall_AVX`, `AmericanCall_OMP`, `TestMethod_AmericanPut_LSPaper_AVX`, `TestMethod_MRG32K3A_VsOMP` | AVX/OMP flavors absent from the free edition of Hybridizer (and MKL for the first two) |
| `Cuda_LongstaffSchwartz_PaperExample_GivesThePublishedPrice`, `Cuda_LongstaffSchwartz_AmericanPut_MatchesTheHostPrice` | delegates not supported (section 3) |

The GPU tests run in a few seconds, but most of that time goes to the CPU references and to module loading: each
kernel lasts only a few milliseconds. The GPU tests that run: `Cuda_*` of Coherence (9), `ClosedFormCudaTests` (4),
`FullRevaluationCudaTests` (Philox and Sobol), `BacktestCudaTests`, `TestMethod_MRG32K3A_VsCuda`. Three consecutive runs of
Coherence.Tests: stable.

## 2. CUDA generation

### Build step

```sh
dotnet build InFine.QuantFinance.Cuda/InFine.QuantFinance.Cuda.csproj -c Release -p:HybridizeCuda=true
```

hybridizes the ten kernel assemblies (Scheduling, Numerics, Arithmetic, ClosedForm, Random, Models, Payoffs, Risk,
Backtesting, LongstaffSchwartz), compiles the generated sources with nvcc and produces `InFine.QuantFinance_CUDA.dll`
(2.3 MB), copied to `artifacts/cuda/`. `Directory.Build.targets` drops it next to the test and benchmark assemblies.
Without the property, the project merely compiles: the ubuntu CI needs neither Hybridizer, nor CUDA, nor a GPU. Duration:
58 s of generation and nvrtc JIT, then 17 s of nvcc.

The chain follows `altimesh/hybridizer-basic-samples` (`src/Directory.Build.targets`: CUDA version detection through
`nvcc --version`, `vswhere` then `vcvarsall`, nvcc on `hybridizer.generated.cpp` and `hybridizer.wrappers.cu`). The device
code is a cubin compiled by nvrtc for **the GPU present at build time** (`--gpu-architecture=auto`), embedded in
`hybridizer.generated.cpp`. The satellite is therefore not portable from one GPU architecture to another.

### Problems encountered

| Problem | Cause | Resolution |
| --- | --- | --- |
| The `hybridizer` launcher hangs at 0% CPU, with no output | deadlock in the launcher that relays the worker's output when it is large | direct call to `dotnet Hybridizer.Application.dll` |
| `input assembly not found`, then a hang | the tool resolves dependencies only next to each input | the project copies all its dependencies (`CopyLocalLockFileAssemblies`) |
| `System.Text.Json 10.0.0.0` not found | runtime 3.7.1 depends on the System.Text.Json 10.0.1 package | same |
| `An item with the same key has already been added` | an assembly listed before one of its dependencies is loaded twice | topological order enforced (`HybridizedAssembly`) |
| `namespace "hybridizer" has no member "is_infinity"` | `double.IsInfinity` mapped to a function absent from the headers | `quantfinance.cuda.builtins`, passed **first** (the first registration wins) |
| `IsNegativeInfinity` mapped to `hybridizer::is_even_integer` | error in the shipped builtins | not used by the kernels; flagged in the builtins file |
| `cannot open source file "quantfinance.tools.h"` | `[IntrinsicInclude]` headers left in `research/` | `build/hybridizer/include`, with `__shfl_xor_sync` (maskless `__shfl_xor` no longer exists since CUDA 9 on sm_70+) |
| `name followed by "::" must be a class or namespace name : ::System::Array` | generic delegate with an array argument (`Action<int, int, T[]>`) | `ParallelReduction<T>` and `SingleBlockParallelReduction<T>`, unused on device, ignored |
| `identifier "a1" is undefined` | `fixed` statements translated into undeclared pinned variables | LS regressor in scalar fields, bit-identical prices |
| `cuLinkComplete (300) device kernel image is invalid` | `--relocatable-device-code=true` | option removed |
| A successful run displays the errors of the previous run | `hybridizer.stderr` is not truncated | working folder emptied before each generation |

### Known risk points

| Item | Status | Detail |
| --- | --- | --- |
| `out` parameters | verified | `BlackScholesKernels.Greeks` / `Black76Kernels.Greeks` (10 and 9 `out`) through `GreeksArray` on GPU |
| `ulong`, `uint[]` | verified | `ulong` Philox seed (full revaluation, bootstrap); `uint[]` Sobol directions and offsets (full revaluation with Sobol) |
| `Cplx` struct and operator overloads (Heston COS) | verified | `HestonKernels.PriceArray` within 2.9e-15 of the forward |
| Readonly structs `Dual` / `Dual4` | generated, not executed | the Arithmetic assembly hybridizes without error, but no entry point uses the duals yet |
| `Math.Sinh` / `Math.Pow` ("Let's Be Rational") | verified | `ImpliedVolatilityKernels.Black76Array` within 1.4e-15 of the CPU |
| Cross-assembly calls | verified | Risk → ClosedForm and Random, Payoffs → Models, Models → Numerics |
| 29-parameter `[EntryPoint]` `SimulateScenarios` | verified | Philox and Sobol |
| `int` indexing `i*M + s` of `instrumentPnl` | verified, no fix | the host refuses `M × N > Array.MaxLength` (< 2^31) before allocating |
| (found along the way) `N * blockIdx.x` in `int` in `SimulateSingle` | fixed | 64-bit bounds, test with 4096 blocks |

## 3. GPU = CPU equality

`CudaBackend.Launcher` is wired by a module initializer of `InFine.QuantFinance.Coherence.Tests`
(`CudaLauncher`): `HybRunner.Cuda(path).SetDistrib(32, 128).Wrap(new SimulatableScheduler()).SimulateSingle(...)`. The
library's simulatables run on the GPU; those of the test project (reference Philox Brownian fill, which allocates) stay
on the host. The expected binary is now called `InFine.QuantFinance_CUDA.dll` everywhere
(`NativeEnvironment.CudaSatelliteName`) instead of `InFine.Quantfinance.KEPLER.dll`. It is loaded by full path,
because `HybRunner` resolves a bare name relative to the current directory.

| Entry point / kernel | Test | Tolerance | Observed difference | Status |
| --- | --- | --- | --- | --- |
| `BlackScholesMertonPrimitive_CallPut_Array(Multi)` | `ClosedFormCudaTests` (100,003 options) | 1e-12 rel. | 2.5e-13 | verified |
| `BlackScholesKernels_Price_Array` / `Greeks_Array` | same | price 1e-12, Greeks 1e-10 rel. | price 2.7e-13, theta 5.0e-12 | verified |
| `Black76Kernels_Price_Array` / `Greeks_Array` | same | same | price 2.7e-13, theta 1.7e-12 | verified |
| `ImpliedVolatilityKernels_Black76_Array` | same (96,024 identifiable options) | 1e-10 rel. | 1.4e-15 | verified |
| `HestonKernels_Price_Array` | same (20,011 options) | 1e-9 × F | 2.9e-15 × F | verified |
| `SimulateSingle` + Black-Scholes-Merton | coherence | log-spot 1e-11 abs. | 8.9e-16 | verified |
| `SimulateSingle` + Heston (variance, spot) | coherence (existing test) | that of the test | within tolerance | verified |
| `SimulateSingle` + Hull-White (x, ln D) | `Cuda_CurveAndHullWhiteDiscounting` | 1e-11 abs. | 2.8e-17 | verified |
| `SimulateSingle` + correlated log-normal (3 assets) | `Cuda_MultiAsset…` | 1e-11 abs. | 8.9e-16 | verified |
| European, ConstantRate / Curve / HullWhite discounting | coherence | premiums 1e-12 rel. | 1.6e-13 / 2.5e-14 / 2.5e-14 | verified |
| Arithmetic / geometric Asian | coherence | 1e-12 rel. | 2.2e-13 / 1.2e-13 | verified |
| Discrete barrier / Brownian bridge | coherence | 1e-12 rel. | 4.3e-14 / 9.2e-14 | verified |
| Floating lookback | coherence | 1e-12 rel. | 1.2e-14 | verified |
| Arithmetic / geometric basket | coherence | 1e-12 rel. | 1.2e-13 / 4.4e-14 | verified |
| Rainbow worst-of put / best-of call | coherence | 1e-12 rel. | 6.9e-14 / 8.2e-14 | verified |
| WorstOfDownAndInPut | coherence | 1e-12 rel. | 3.0e-15 | verified |
| Autocallable | coherence | 1e-12 rel. | 8.8e-16 | verified |
| VarianceSwap | coherence | 1e-12 rel. | 4.8e-15 | verified |
| `SimulatableScheduler_SimulateGroup` (2 diffusions + 1 Asian) | `Cuda_SimulateGroup…` | same | 8.9e-16 / 2.2e-13 | verified |
| `FullRevaluationKernels_SimulateScenarios`, Philox and Sobol | `FullRevaluationCudaTests` (65,536 scenarios) | 1e-10 × max\|P&L\| | within tolerance | verified |
| `BacktestKernels_SimulateConstantMixPaths` | `BacktestCudaTests` (100,003 paths) | 1e-12 rel. | 0, except variance 7.2e-16 | verified |
| MRG32K3A (cuRAND) | `TestMethod_MRG32K3A_VsCuda` (existing test) | that of the test | within tolerance | verified |
| Longstaff-Schwartz (`LSEntryPoint`) | `Cuda_LongstaffSchwartz_*` (skipped with reason) | — | — | **failed** |

**Philox words, bit for bit**: verified indirectly but strictly. The bootstrap draws its row indices from the Philox
words; over 100,003 paths × 500 periods, final wealth, drawdown, turnover and costs are bit-identical, which requires
the same words on the device. No entry point exposes the raw words.

**Longstaff-Schwartz.** The pricer passes its kernels delegates created on the host: the `LSHelper` actions, and the
map / init functions of the reductions. For each of them, `HybRunner` looks for a device function pointer
(`Could not get function pointer for method Void applyRegressor(alignedindex)`). Hybridizer generates one only with
delegate support, and the free edition (7.5.1, and 7.6.3 as re-checked later) does not allow enabling it: `DelegateSupport="false"` in the generated
configuration, regenerated on every run, and no command-line option. The host paths remain covered by
`Test_LongstaffSchwartzCoherence`. Proposed fix: replace these delegates with generic functors registered through
`HybridRegisterTemplate`, the pattern of the rest of the library. That is a rework of LS outside the scope of this
verification.

### Known runtime race and transfers

The documented race (`DoubleResidentArray.CheckRefreshHost` / `AllocHost` unsynchronized, worked around by pre-touching
the storages) also shows up on the GPU side, in two more serious forms:

1. **Data loss on chained launches.** Marshalling an `alignedstorage_*` re-uploads the host buffer even when its status is
   `HostNeedsRefresh` (the device holds the up-to-date data). A GPU diffusion followed by a GPU payoff on the same paths
   replaces those paths with the stale host copy: 526 wrong European premiums out of 1009, with no error. A host read
   between the two launches is enough to avoid it.
2. **CUDA error 700 on concurrent read.** If several threads perform at the same time the first host read of a storage
   written by the device, the device → host copies fail with `cudaErrorIllegalAddress`. The error is persistent and
   makes the CUDA context unusable. Reproduced 20 times out of 20 (200,003 paths, 24 threads), with or without
   pre-touch; a first read on a single thread is always correct.

`CudaLauncher` refreshes the stale host copies before and after each launch, on the launching thread, which works
around both problems at the cost of one device → host copy per step. `Cuda_ResultsCanBeReadByManyThreadsAfterALaunch`
fails without this refresh and passes with it. The root-cause fix belongs to the runtime: a lock in `CheckRefreshHost`,
and respecting the status when marshalling.

## 4. Performance

### BenchmarkDotNet, existing suites

BenchmarkDotNet 0.15.8, default job, `--inProcess`, .NET 8.0.31 x64 RyuJIT (AVX-512). Total duration: 16 min 33 s,
21 benchmarks. Random, Diffusion and Longstaff-Schwartz, impossible on arm64, run here.

| Suite | Method | Size | Mean |
| --- | --- | ---: | ---: |
| ClosedForm | `BlackScholesCallPut_ScalarLoop` | 1,048,576 | 51.1 ms |
| ClosedForm | `NormalCdf` | 1,048,576 | 20.2 ms |
| Random | `Mrg32k3a_Uniform` | 1,048,576 | 11.0 ms |
| Random | `Mrg32k3a_Gaussian_BoxMuller2` | 1,048,576 | 19.8 ms |
| Diffusion | `Diffusion_Generics_CSharp` | 512 / 16,384 paths | 0.66 ms / 28.0 ms |
| Longstaff-Schwartz | `AmericanPut_CSharp_SerialReduction` | 4096 / 16,384 paths | 6.3 ms / 23.7 ms |

Adjoint Greeks (20-asset basket, 50 steps, 1000 paths), compared with the Mac measurement in `docs/architecture.md`:

| Method | Sensitivities | Windows x64 | Ratio | Mac M5 Pro | Mac ratio |
| --- | ---: | ---: | ---: | ---: | ---: |
| Valuation | 0 | 6.38 ms | 1.00 | 4.00 ms | 1.00 |
| Adjoint | 110 | 10.48 ms | 1.66 | 5.46 ms | 1.36 |
| Tangent, 1 `Dual4` pass | 4 | 9.65 ms | 1.53 | 6.17 ms | 1.54 |
| Tangent, 10 passes | 40 | 99.6 ms | 15.7 | 61.8 ms | 15.4 |
| Tangent, 28 passes | 110 | 282 ms | 44.6 | 173 ms | 43.2 |

The conclusion holds: the adjoint costs about 1.7 valuations for 110 sensitivities and beats the tangent from five
inputs onward. The adjoint ratio is slightly higher here than on arm64.

### GPU against CPU

`GpuVsCpuBenchmarks` and `FullRevaluationGpuBenchmarks` (new, skipped without a GPU or satellite). GPU measured end to
end: `HybRunner` copies the arrays to the device and back on every call. Kernel-only time comes from
`HybRunner.LastKernelDuration`, 1 ms resolution. CPU: all cores (`Parallel.For`), unless noted otherwise. Each
side is checked against the other before measuring.

| Workload | CPU 1 thread | CPU 24 threads | GPU, end to end | GPU, kernel only |
| --- | ---: | ---: | ---: | ---: |
| Black-Scholes, 1M options (`PriceArray`) | 60.9 ms | 5.9 ms | 23.9 ms | 12.5 ms |
| VaR full reval, M = 100k, N = 100, F = 101 | — | 74.3 ms | 46.3 ms | 36.3 ms |
| VaR full reval, M = 1M, N = 100, F = 101 | — | 740 ms | 303 ms | 292 ms |
| Constant-mix bootstrap, 100k paths × 2520 days, 10 assets | — | 604 ms | 275 ms | 269 ms |

In full-revaluation throughput:

| Machine | M | Scenarios/s | Revaluations/s |
| --- | ---: | ---: | ---: |
| Mac arm64, 18 cores (reference) | 100k | 2.1 M | 2.1e8 |
| Ryzen 9 7845HX, 24 threads | 100k | 1.35 M | 1.35e8 |
| Ryzen 9 7845HX, 24 threads | 1M | 1.35 M | 1.35e8 |
| RTX 4070 Laptop, end to end | 100k | 2.2 M | 2.2e8 |
| RTX 4070 Laptop, end to end | 1M | 3.3 M | 3.3e8 |

Reading:

- These kernels are in double precision, and a consumer RTX 4070 computes double at 1/64 of the single-precision
  throughput. The GPU therefore wins by a factor of 2.2 to 2.4 on the heavy workloads (VaR at 1M scenarios, bootstrap),
  not by an order of magnitude. A compute GPU (A100, H100) or a switch to single precision would change the ratio.
- On Black-Scholes, 1M options, the GPU loses to the 24 threads (23.9 ms against 5.9 ms). Half of the time goes to the
  transfers of eight 8 MB arrays, the other half to the kernel (exp, log, erfc in double).
- The x64 CPU revalues 1.35 M scenarios/s, against 2.1 M on the 18-core Mac: with 12 physical cores, that is about
  0.11 M/s per core here against 0.12 M/s on arm64.

Workloads sized to keep the GPU busy for several seconds: `LongGpuBenchmarks` (1 warm-up iteration, 3 measured,
7 min 23 s in total; at initialization, GPU and CPU are compared element by element). `nvidia-smi` sees the GPU above
90% for the whole duration of the kernels.

| Workload | CPU 24 threads | GPU, end to end | GPU, kernel | GPU / CPU |
| --- | ---: | ---: | ---: | ---: |
| VaR full reval, 20M scenarios × 100 options × 101 factors | 12.96 s | 5.73 s | 5.70 s | ×2.3 |
| Bootstrap, 1M paths × 2520 days × 10 assets | 5.27 s | 2.62 s | 2.61 s | ×2.0 |
| Heston COS, 2M options | 8.67 s | 3.88 s | 3.86 s | ×2.2 |

GPU-CPU differences: at most 9e-16 of the maximum P&L (VaR), 7e-16 relative (bootstrap), 2.4e-15 of the forward
(Heston). A first cold run, outside BenchmarkDotNet, gave 30 s, 12 s and 21 s of CPU, i.e. misleading ratios of ×4 to
×5: the first CPU pass pays for JIT compilation and the laptop's frequency ramp-up. The reliable ratio is ×2 to
×2.4, on these long workloads as on the shorter ones.

## 5. Excel add-in

`InFine.QuantFinance.Excel/smoke/Invoke-ExcelSmokeTest.ps1` loads the packed 64-bit `.xll` into a private, hidden
Excel instance (`RegisterXLL`), writes the formulas through `Range.Formula2` and reads `Value2`. Result: **64 checks out of 64**.

| Behavior | Status | Detail |
| --- | --- | --- |
| Loading of the packed `.xll` | verified | `RegisterXLL` true, .NET 8.0.31 hosted by Excel-DNA 1.9 |
| 25 `QF.*` functions | verified | `QF.BS.PRICE` 10.450583572185572 / 5.5735260222569671 (10.4506 / 5.5735); `QF.IMPLIEDVOL` and `QF.IMPLIEDVOL.BLACK76` round trips to 1e-16; `QF.CURVE.BOOTSTRAP` then `QF.CURVE.DF` (repricing error 2.1e-15); `QF.SVI.FIT` then `QF.SURFACE.VOL` which recovers the generating smile; covariance, parametric VaR and decomposition compared with independent PowerShell computations; `QF.MCVAR` within 2% of delta-normal |
| Omitted optional arguments | verified | convention, compounding, interpolation, calendar, method; omitted middle argument (`QF.COV.OBJ(…, 0.97, , TRUE)`) |
| Function wizard | verified once, by hand | category "InFine QuantFinance", signatures with argument names, descriptions ([screenshot](verification/excel-function-wizard.png)). Cannot be driven through COM; obtained with `PrintWindow` and posted keystrokes. Per-argument help was not verified |
| `QF.MCVAR`: `#N/A` then result | verified | |
| Cancellation on clearing or on argument change | verified | Excel's CPU goes from 22 cores to 0 in under a second; the uncancelled control stays at 22 cores |
| Recalculation during the computation | verified | it joins the computation in progress (result at 4.3 s instead of 6.9 s) |
| Recalculation after the result | **fixed (doc)** | Excel-DNA restarts the computation (`#N/A`, then the same figures); `docs/excel.md` promised re-reading of the finished result |
| Handle versioning, `#REF!` on stale or deleted handle | verified | `QF.Curve:V#1` → `#2`; identical recreation without a version change |
| Workbook reopening then Ctrl+Alt+F9 | verified | objects absent at opening (0 live objects), a re-entered dependency gives `#REF!`, `CalculateFull` rebuilds the objects then their users |
| Multithreaded recalculation | verified | 12,000 thread-safe cells, identical results on 1 and 24 threads (90 ms against 18 ms) |
| `QF.LASTERROR` | verified | `QF.BS.PRICE: S: 'abc' is not a number (#VALUE!)` |
| Errors (`#VALUE!`, `#NUM!`, `#REF!`, propagation) | verified | compared by `CVErr` code, Excel being localized (`#VALEUR!`, `#NOMBRE!`) |

## Re-verification with Hybridizer 7.6.3 and the open-source branch

Run on 2026-10-17 on the same machine (RTX 4070 Laptop, driver 610.74, CUDA 13.0; Windows 11 build 26300; .NET SDK
8.0.425; Excel 16.0.20430.20146, 64-bit), on branch `epic/11-open-source` (505d702), Hybridizer tool updated from 7.5.1
to **7.6.3**.

| Point | Result |
| --- | --- |
| `dotnet build QuantFinance.sln -c Release` | 0 warnings, 0 errors (warnings are errors) |
| CUDA generation, `-p:HybridizeCuda=true`, tool 7.6.3 | succeeded; 3 nvcc warnings `#20199-D unrecognized #pragma` from the CUDA 13.0 `curand_poisson.h` header, not errors |
| `dotnet test` with the satellite | 0 failed across 15 assemblies, about 1584 passed, 10 skipped (same 10 as before: MKL, AVX/OMP flavours, Longstaff-Schwartz on the device) |
| `Cuda_*` coherence tests | 10 passed, 2 skipped (Longstaff-Schwartz) |
| Longstaff-Schwartz on the device, skip removed | still fails with `Could not get function pointer for method Void applyRegressor(alignedindex)`; `DelegateSupport="false"` in all 11 generated configurations: the free-edition limitation is unchanged in 7.6.3 |
| `samples/04-MonteCarloVaR`, 1M scenarios | CPU (24 threads) 234 ms; GPU 46 ms end to end, ×5.1; same VaR 99% (30,454) and ES 99% (34,502); max \|GPU − CPU\| P&L 2.29e-10 (3.3e-15 relative) |
| Excel smoke test, functions and the five `samples/excel` workbooks (`-SampleWorkbooks`) | **70 checks, 0 failed** after one fix to the script (below) |

Excel details: every workbook recalculates with no error cell, so the `QF.*` names stored without the `_xll.` prefix
resolve correctly; the legacy array formulas fill; `QF.MCVAR` shows `#N/A` then its value (within 2% of delta-normal);
each About sheet reports the add-in loaded; the charts of workbooks 01–03 render (exported to PNG and inspected).

Fixed during this run: the smoke script failed one check with `RPC_E_CALL_REJECTED (0x80010001)` while polling the
asynchronous `QF.MCVAR`, because Excel rejects COM calls while it is busy. `Invoke-ComRetry` now retries those calls for
up to 60 s. Cosmetic: the smile charts of workbook 02 now start the strike axis at the lowest quoted strike instead of 0.

## Remaining problems

1. **Longstaff-Schwartz on GPU**: blocked by the free edition of Hybridizer (no delegate support). Rework into functors
   to be decided.
2. **`Hybridizer.Runtime.CUDAImports` 3.7.1 runtime**: data loss on chained launches and error 700 on concurrent read.
   Worked around in the tests; any application code that chains launches or reads results in parallel must first refresh
   the host on a single thread. To be reported to the vendor.
3. **AVX and OMP flavors** unavailable in the free edition: `AmericanCall_AVX`, `AmericanCall_OMP`,
   `TestMethod_AmericanPut_LSPaper_AVX` and `TestMethod_MRG32K3A_VsOMP` remain skipped.
4. **MKL absent**: `TestMethod_MRG32K3A_MKL`, `TestMethod_MRG32K3A_MKL_GET_NAN`, `AmericanCall_CSharp` and `AmericanCall_CUDA`
   remain skipped (the last one would be blocked by point 1 anyway).
5. **Satellite tied to the build GPU's architecture** (nvrtc `auto` cubin): a multi-architecture satellite
   would require setting `--gpu-architecture` per architecture, not tested.
6. **Not tested**: the Linux branch of the MSBuild targets; other GPUs; Excel 2016/2019; `Dual`/`Dual4` executed on
   device (no entry point); `FixedCashFlowsPricerOptimized_*` (out of scope).
7. `double.IsNegativeInfinity` is still mistranslated by the shipped builtins: to be fixed in `quantfinance.cuda.builtins`
   before any use in device code.
