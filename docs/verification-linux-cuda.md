# CUDA verification on Linux

Complements [verification-windows-gpu.md](verification-windows-gpu.md): the Windows verification had wired up CUDA
generation, the GPU = CPU tests and the GPU benchmarks, but the Linux branch of `build/hybridizer/Hybridizer.targets`
had never run. Branch `verif/linux-cuda`, taken from `main` after PR #64 was merged.

## Environment

| Item | Version |
| --- | --- |
| Host | Ubuntu (kernel 7.0.0), x86_64, SSH access |
| CPU | AMD Ryzen 7 5800X, 8 cores / 16 threads |
| GPU | NVIDIA GeForce RTX 3060 Ti, sm_86, 38 SMs, 8 GB |
| NVIDIA driver | 595.84 |
| Container | `nvidia/cuda:13.0.0-devel-ubuntu24.04` through the `nvidia` Docker runtime (`--gpus all`) |
| CUDA toolkit | 13.0 (nvcc V13.0.48), g++ 13.3 |
| .NET | SDK 8.0.426, runtime 8.0.32 |
| Hybridizer | global tool `hybridizer` 7.5.1 then 7.6.3 (free edition, CUDA only); runtime `Hybridizer.Runtime.CUDAImports` 3.7.1 |

Nothing is installed on the host, which has no .NET SDK, no C++ compiler and no CUDA toolkit: everything runs in a
container. The environment is pinned in [`build/hybridizer/linux/Dockerfile`](../build/hybridizer/linux/Dockerfile), and
the full sequence was replayed in a fresh image built from that file, with the same result.

```sh
docker build -t qfl-cuda build/hybridizer/linux
docker run --rm --gpus all -v "$PWD":/work -w /work qfl-cuda bash -lc \
  "dotnet build InFine.QuantFinance.Cuda/InFine.QuantFinance.Cuda.csproj -c Release -p:HybridizeCuda=true \
   && dotnet build QuantFinance.sln -c Release && dotnet test QuantFinance.sln -c Release --no-build"
```

## Summary

| Item | Status |
| --- | --- |
| CUDA generation on Linux (opt-in step) | fixed (2 problems), verified: ELF satellite in 31 s |
| Full suite on GPU | verified: 1556 passed, 10 skipped, 0 failed (identical to Windows) |
| GPU = CPU equality, all families | verified (same tests and tolerances as on Windows) |
| Satellite loading by the tests | fixed (1 test) |
| GPU vs CPU benchmarks | measured |

Commits on the branch (Lore format):

1. `OptimizedFixedCashFlowsPricer` excluded from generation (array field in a struct, see below).
2. Satellite loaded by full path in all tests (`NativeEnvironment.CudaSatellitePath`).
3. `libdl.so` check in the prerequisites, Linux `Dockerfile` and this report.
4. Switch to the Hybridizer 7.6.3 tool, which loads `libdl.so.2`: link and `libdl.so` check removed (section "Hybridizer 7.6.3").

## Problems encountered

| Problem | Cause | Resolution |
| --- | --- | --- |
| `0X6099 : Cannot identify fields in type YieldCurve -- generic in struct?` | Hybridizer's `FieldTools.GetFields` places the fields of a struct with `Marshal.OffsetOf`. For an array field (`float[] Tenors`), the default marshalling inside a struct is SAFEARRAY, which exists only on Windows: `OffsetOf` throws on Linux, the field list comes back empty and generation stops | `OptimizedFixedCashFlowsPricer`, a legacy pricer that no test or benchmark launches on a device, marked `[HybridizerIgnore]`. Any struct reachable from a kernel and containing an array will break Linux generation in the same way |
| `DllNotFoundException: libdl.so` (generation, tool 7.5.1) | tool 7.5.1 embeds in `Hybridizer.Application.dll` an old copy of `LinuxKernelInteropTools` (CUDAImports runtime), called by the PTX JIT compiler, which P/Invokes the unversioned name `libdl.so`. Since glibc 2.34 (Ubuntu 22.04 and later), only `libdl.so.2` is shipped. The source runtime has used `libdl.so.2` since commit de6dd44 (2026-02-13); the published runtime package 3.7.1 and tool 7.6.3 do too | first a `libdl.so -> libdl.so.2` link and a prerequisites check; then a switch to tool 7.6.3, which makes both unnecessary (removed). `HybRunner` (runtime 3.7.1) was never affected |
| `Cannot load dll InFine.QuantFinance_CUDA.dll` (`TestMethod_MRG32K3A_VsCuda`) | the test passed the bare name to `HybRunner.Cuda`. On Windows, `LoadLibrary` searches the application folder; on Linux, `dlopen` of a name without `/` searches only the loader paths | `NativeEnvironment.CudaSatellitePath` (full path), used by this test and by the Longstaff-Schwartz tests, which had the same defect |

The satellite keeps the `.dll` extension: it is an x86-64 ELF shared object, loaded by full path, so the name does not
matter. As on Windows, the device code is compiled by nvrtc for the GPU present at build time
(`--gpu-architecture=auto`): a satellite built on the RTX 3060 Ti (sm_86) targets only that architecture.

## Tests

`dotnet test QuantFinance.sln -c Release` with the satellite:

| Passed | Skipped | Failed |
| ---: | ---: | ---: |
| 1556 | 10 | 0 |

The 10 skipped tests are exactly those of the Windows verification: MKL absent (4), AVX/OMP flavors absent from the free
edition of Hybridizer (4), Longstaff-Schwartz on GPU blocked by delegates (2). All GPU tests run: the `Cuda_*` tests of
Coherence, `ClosedFormCudaTests`, `FullRevaluationCudaTests` (Philox and Sobol), `BacktestCudaTests`,
`TestMethod_MRG32K3A_VsCuda`. The workarounds for the runtime races (`CudaLauncher`, refreshing the host copies after
each launch) behave as on Windows.

## Performance

BenchmarkDotNet 0.15.8, default job, `--inProcess`, .NET 8.0.32 x64 RyuJIT (x86-64-v3), `*Gpu*` suites. GPU end to end
(host <-> device copies included); kernel-only time read from `HybRunner.LastKernelDuration`. CPU: all cores
(16 threads) unless noted otherwise. In parentheses, the Windows measurement (RTX 4070 Laptop, Ryzen 9 7845HX, 24 threads).

| Workload | CPU 1 thread | CPU 16 threads | GPU, end to end | GPU, kernel only |
| --- | ---: | ---: | ---: | ---: |
| Black-Scholes, 1M options | 62.6 ms (60.9) | 7.4 ms (5.9) | 23.5 ms (23.9) | 13.1 ms (12.5) |
| VaR full reval, M = 100k, N = 100, F = 101 | — | 111 ms (74.3) | 68.4 ms (46.3) | 57 ms (36.3) |
| VaR full reval, M = 1M | — | 1018 ms (740) | 471 ms (303) | 460 ms (292) |
| Constant-mix bootstrap, 100k paths × 2520 days | — | 831 ms (604) | 321 ms (275) | — |

Long workloads (`LongGpuBenchmarks`, 1 warm-up, 3 measurements):

| Workload | CPU 16 threads | GPU, end to end | GPU, kernel | GPU / CPU | Windows |
| --- | ---: | ---: | ---: | ---: | ---: |
| VaR full reval, 20M scenarios × 100 options × 101 factors | 19.75 s | 9.18 s | 9.15 s | ×2.2 | ×2.3 |
| Bootstrap, 1M paths × 2520 days × 10 assets | 7.55 s | 3.03 s | 3.02 s | ×2.5 | ×2.0 |
| Heston COS, 2M options | 14.88 s | 4.41 s | 4.38 s | ×3.4 | ×2.2 |

Reading:

- The GPU / CPU ratio stays between ×2 and ×3.4 on the heavy workloads, as on Windows: these kernels are in double
  precision, and a consumer RTX computes double at 1/64 of its single-precision throughput. Heston COS gains more here
  because the CPU (8 cores) is weaker than the 12-core Ryzen 9 of the Windows laptop.
- The RTX 3060 Ti (38 SMs, sm_86) is about 1.5 times slower than the RTX 4070 Laptop on VaR full reval; on
  Black-Scholes, which is transfer-bound, the two GPUs are level.
- In full-revaluation throughput: 0.98 M scenarios/s on the CPU (0.12 M/s per core, as on the other machines) and
  2.1 M scenarios/s on the GPU at M = 1M.

## Hybridizer 7.6.3

Tool 7.6.3 (the most recent on NuGet) imports `libdl.so.2`: the image was rebuilt from the `Dockerfile` **without** the
`libdl.so` link, then the whole chain was replayed.

| Item | 7.5.1 (with `libdl.so` link) | 7.6.3 (without link) |
| --- | --- | --- |
| Satellite generation | 31 s | 32 s |
| Tests | 1556 passed, 10 skipped, 0 failed | 1556 passed, 10 skipped, 0 failed (same skipped) |
| `[HybridizerIgnore]` on `OptimizedFixedCashFlowsPricer` | required | still required (#338 not fixed) |

Performance, GPU end to end (kernel only in parentheses):

| Workload | 7.5.1 | 7.6.3 |
| --- | ---: | ---: |
| Black-Scholes, 1M options | 23.5 ms (13.1) | 23.5 ms (13.2) |
| VaR full reval, M = 100k | 68.4 ms (57) | 68.5 ms (57.5) |
| VaR full reval, M = 1M | 471 ms (460) | 477 ms (463) |
| Bootstrap, 100k paths | 321 ms | 322 ms |
| VaR full reval, 20M scenarios | 9.18 s | 9.20 s |
| Bootstrap, 1M paths | 3.03 s | 3.04 s |
| Heston COS, 2M options | 4.41 s | 4.40 s |

No difference beyond measurement noise. The default version in `Hybridizer.targets` and in the `Dockerfile` therefore
moves to 7.6.3, and the `libdl.so` check and the link are removed. **Not re-verified with 7.6.3: Windows** (the
verification in `verification-windows-gpu.md` was done with 7.5.1); `-p:HybridizerToolVersion=7.5.1` makes it possible to go back.

## Remaining work

- **Hybridizer**: hybridizer-io/hybridizer-software-suite#338, `FieldTools.GetFields` should not depend on
  `Marshal.OffsetOf` for structs with array fields (layout computed by Hybridizer itself, the array being a pointer on
  the device side). The `[HybridizerIgnore]` on `OptimizedFixedCashFlowsPricer` could then go away.
- Rerun the Windows verification with tool 7.6.3.
- The open points of the Windows report still apply on Linux: Longstaff-Schwartz on GPU (delegates), races in the
  `alignedstorage` runtime, AVX/OMP flavors, MKL.
