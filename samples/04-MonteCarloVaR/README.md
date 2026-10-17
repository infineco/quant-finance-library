# 04 - Monte Carlo VaR, CPU and GPU

Full-revaluation Monte Carlo VaR of a small equity-option book: three indices, each with a stock position, a long 1-year
call, a short 6-month put, a short 6-month call and a long 3-month put (15 positions), driven by 7 risk factors (3 spots,
3 implied volatilities, one swap rate).

1. **Covariance.** 750 days of synthetic factor moves go through `LedoitWolf.ConstantCorrelation`, which shrinks the sample
   covariance toward a constant-correlation target.
2. **Scenarios.** `FullRevaluationEngine` draws correlated factor moves over a 10-day horizon (Philox random numbers,
   Cholesky factor of the covariance) and reprices every option with Black-Scholes in each shocked market, including the
   loss of time value. One scenario is one unit of work, so the scenarios run in parallel on all cores.
3. **Measures.** `MonteCarloVarResult.Measures` gives the 99% VaR and expected shortfall (loss-positive);
   `Decompose` gives the Euler decomposition by position (a smaller run keeps the per-position P&L of every scenario).
   Negative components reduce the risk of the book: here the long puts and the short calls against the stocks.
4. **GPU, when available.** The same scenarios run through the Hybridizer-generated CUDA kernel
   (`HybRunner.Cuda(path).SetDistrib(...).Wrap(new FullRevaluationKernels())`, see `GpuRunner.cs`) and the program prints the
   time and the largest |GPU - CPU| difference of the scenario P&L. The kernel is the very C# code that runs on the CPU.

## Run

```sh
dotnet run -c Release --project samples/04-MonteCarloVaR
dotnet run -c Release --project samples/04-MonteCarloVaR -- 4000000    # 4 million scenarios
```

The CPU part needs nothing else. With the default 1,000,000 scenarios the CPU run takes a fraction of a second on a modern
multi-core machine: a 15-position book is small. The GPU pays off on larger books and scenario counts; the measurements of
[docs/gpu.md](../../docs/gpu.md) (100,000 to 1,000,000 scenarios on a book of 100 positions and 101 factors) are the reference.

### Enable the GPU part

The GPU part needs an x64 host with an NVIDIA GPU and the CUDA satellite `InFine.QuantFinance_CUDA.dll`, which is built on
demand (Hybridizer 7.6.3, CUDA toolkit; on Linux the Docker image of `build/hybridizer/linux`):

```sh
dotnet build InFine.QuantFinance.Cuda/InFine.QuantFinance.Cuda.csproj -c Release -p:HybridizeCuda=true
dotnet run -c Release --project samples/04-MonteCarloVaR
```

The build copies the satellite to `artifacts/cuda/`; this sample's project copies it from there next to the executable. You
can also point the environment variable `QFL_CUDA_SATELLITE` at the file. The satellite is compiled for the GPU present at
build time, so build it on the machine that runs it. The full procedure, requirements and limits are in
[docs/gpu.md](../../docs/gpu.md).

When the satellite is missing, or the Hybridizer runtime does not load (it is x64 only, so it does not load in an arm64
process such as Apple Silicon), or no device is usable, the program says so in the "GPU" section and exits normally after the
CPU part.

## Expected output

CPU part and GPU section on a machine without the satellite (the timings depend on the machine):

```text
== Covariance: 750 days x 7 factors, Ledoit-Wolf shrinkage intensity 0.004 ==
daily vol of each factor move: SX5E 0.01, SPX 0.00931, NKY 0.0091, vol SX5E 0.00379, vol SPX 0.00347, vol NKY 0.00399, EUR 5y swap 0.000245

== Book: 15 positions on 7 factors, value 336,106 ==

== CPU: 1,000,000 scenarios, 10-day horizon, 18 cores ==
VaR 99%:       30,454   ES 99%:       34,502   (loss-positive)
Time 152 ms  (98.8 M revaluations/s)

== Decomposition by position (200,000 scenarios, so its total differs a little from the run above; components add up to it) ==
Position                Quantity   Component VaR   % VaR    Component ES    % ES
SX5E stock                 1,000           6,571   21.7%           7,420   21.6%
SX5E long 1y 100C          2,000           5,720   18.9%           6,267   18.2%
SX5E short 6m 90P         -3,000           6,235   20.6%           7,424   21.6%
SX5E short 6m 110C        -1,500          -2,164   -7.1%          -2,315   -6.7%
SX5E long 3m 95P           2,000          -5,318  -17.6%          -6,376  -18.6%
SPX stock                  1,000           6,051   20.0%           6,880   20.0%
SPX long 1y 100C           2,000           5,504   18.2%           6,087   17.7%
SPX short 6m 90P          -3,000           5,122   16.9%           6,163   17.9%
SPX short 6m 110C         -1,500          -1,902   -6.3%          -2,046   -6.0%
SPX long 3m 95P            2,000          -4,602  -15.2%          -5,586  -16.3%
NKY stock                  1,000           5,315   17.6%           6,144   17.9%
NKY long 1y 100C           2,000           4,694   15.5%           5,271   15.3%
NKY short 6m 90P          -3,000           5,196   17.2%           6,305   18.4%
NKY short 6m 110C         -1,500          -2,063   -6.8%          -2,263   -6.6%
NKY long 3m 95P            2,000          -4,094  -13.5%          -5,032  -14.7%
Total                                     30,266                  34,344

== GPU ==
GPU run skipped: the CUDA satellite was not found at /private/tmp/claude-501/qfl-artifacts/t81/bin/04-MonteCarloVaR/release/InFine.QuantFinance_CUDA.dll.
Build it on a machine with an NVIDIA GPU, CUDA and Hybridizer, then run this sample again:
  dotnet build InFine.QuantFinance.Cuda/InFine.QuantFinance.Cuda.csproj -c Release -p:HybridizeCuda=true
See docs/gpu.md (Requirements and Quickstart). The satellite is picked up from artifacts/cuda or QFL_CUDA_SATELLITE.
```

On a machine with the satellite, the GPU section instead has this shape (the numbers are the machine's):

```text
== GPU ==
VaR 99%:       <VaR>   ES 99%:       <ES>
Time <ms> ms end to end (host-device copies included), <n>x the CPU
max |GPU - CPU| P&L = <abs> (<rel> of the largest |P&L|)
```

The GPU VaR and ES should match the CPU ones closely: the library's GPU test requires the scenario P&L to agree within
1e-10 of the largest |P&L|, and only the device's exp / log / erfc may differ in the last bits.
