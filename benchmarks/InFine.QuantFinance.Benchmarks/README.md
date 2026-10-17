# InFine.QuantFinance.Benchmarks

BenchmarkDotNet suite replacing `research/legacy-benchmarks`. Always run in Release.

| Class | What it measures | Needs Hybridizer runtime |
| --- | --- | --- |
| `ClosedFormBenchmarks` | `BlackScholesMertonPrimitive.CallPut` scalar loop over N options, `NormalDistribution.Cdf` throughput | no |
| `RandomBenchmarks` | MRG32K3A (managed) uniform and Gaussian (Box-Muller) generation | yes (curand state type) |
| `DiffusionBenchmarks` | BSM path diffusion, managed generic backend | yes |
| `LongstaffSchwartzBenchmarks` | American put, managed backend, serial reduction | yes |
| `AdjointGreeksBenchmarks` | Basket call on 20 correlated assets, 50 steps: valuation vs adjoint (all 110 Greeks) vs tangent `Dual4` passes (4, 40, 110 inputs); results in `docs/architecture.md` | no |
| `GpuVsCpuBenchmarks` | Black-Scholes on 1M options (CPU 1 thread, CPU all cores, GPU) and a stationary bootstrap of 100k paths x 2520 days (CPU, GPU) | yes, plus a CUDA device and the satellite |
| `FullRevaluationGpuBenchmarks` | Monte Carlo VaR by full revaluation, N = 100 options, F = 101 factors, M = 100k and 1M (CPU all cores, GPU) | yes, plus a CUDA device and the satellite |
| `LongGpuBenchmarks` | Workloads that keep the GPU busy for several seconds: VaR on 20M scenarios, bootstrap of 1M paths x 2520 days, 2M Heston COS prices (CPU all cores, GPU); 1 warm-up and 3 iterations, about 10 minutes in all | yes, plus a CUDA device and the satellite |

The GPU benchmarks need the CUDA satellite: build it first with
`dotnet build InFine.QuantFinance.Cuda/InFine.QuantFinance.Cuda.csproj -c Release -p:HybridizeCuda=true`; without it (or
without a device) `Program` leaves them out with a note. GPU times are end to end (HybRunner copies the arrays to the
device and back on every call); the kernel-only time is printed after each GPU call. The AVX backends of the legacy
benchmarks are not ported (the Hybridizer free edition has no AVX flavour).

## Commands

```sh
# everything available on this host
dotnet run -c Release --project benchmarks/InFine.QuantFinance.Benchmarks -- --filter '*' --inProcess

# GPU workloads of several seconds each (about 10 minutes)
dotnet run -c Release --project benchmarks/InFine.QuantFinance.Benchmarks -- --filter '*LongGpu*' --inProcess

# quick closed-form run
dotnet run -c Release --project benchmarks/InFine.QuantFinance.Benchmarks -- --filter '*ClosedForm*' --job short --inProcess

# smoke test (one iteration)
dotnet run -c Release --project benchmarks/InFine.QuantFinance.Benchmarks -- --filter '*' --job dry --inProcess

# list benchmarks
dotnet run -c Release --project benchmarks/InFine.QuantFinance.Benchmarks -- --list flat
```

`--inProcess` is required while `research/legacy-benchmarks/InFine.QuantFinance.Benchmarks` exists: BenchmarkDotNet's
default toolchain generates a child project by name and refuses two projects called `InFine.QuantFinance.Benchmarks`.
Drop the flag once the legacy folder is removed.

## Hosts where Hybridizer does not load

`Hybridizer.Runtime.CUDAImports` 3.7.1 is stamped AMD64 and does not load in an arm64 process (Apple Silicon).
There `Program` prints a note, leaves out the Random, Diffusion and Longstaff-Schwartz benchmarks, and disables
BenchmarkDotNet's optimizations validator (it would otherwise crash loading every referenced assembly). Run those on an x64 host.

## Adding a benchmark

Keep Hybridizer-typed code in `HybridizerScenarios` and hold only delegates in the benchmark class, so the class itself loads everywhere.
TODO: add Philox next to MRG32K3A in `RandomBenchmarks`.
