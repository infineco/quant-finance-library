using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Risk;
using InFine.QuantFinance.Samples;

// Sample 4: Monte Carlo VaR by full revaluation of an equity-option book.
//   1. synthetic daily risk-factor moves  ->  Ledoit-Wolf covariance
//   2. every scenario draws correlated factor moves and reprices every option (Black-Scholes) in the shocked market
//   3. VaR / ES, then the Euler decomposition by position, with timings (CPU)
//   4. if the CUDA satellite is present: the same scenarios on the GPU, with time and max |GPU - CPU|
// Usage: dotnet run -c Release --project samples/04-MonteCarloVaR [-- <scenarios>]

int scenarios = args.Length > 0 ? int.Parse(args[0]) : 1_000_000;
const double confidence = 0.99;
const int horizonDays = 10;

// ---- 1. Risk factors and covariance --------------------------------------------------------------------------------
string[] names = { "SX5E", "SPX", "NKY", "vol SX5E", "vol SPX", "vol NKY", "EUR 5y swap" };
double[] levels = { 100.0, 100.0, 100.0, 0.22, 0.20, 0.25, 0.03 };
double[,] moves = SyntheticMoves(days: 750, seed: 2024);
ShrinkageEstimate estimate = LedoitWolf.ConstantCorrelation(moves);
Console.WriteLine($"== Covariance: {moves.GetLength(0)} days x {moves.GetLength(1)} factors, Ledoit-Wolf shrinkage intensity {estimate.Intensity:F3} ==");
Console.WriteLine("daily vol of each factor move: " + string.Join(", ", names.Select((name, j) => $"{name} {Math.Sqrt(estimate.Covariance[j, j]):G3}")));

var factors = new RiskFactor[names.Length];
for (int j = 0; j < names.Length; j++)
    factors[j] = new RiskFactor(names[j], levels[j], j < 3 ? RiskFactorKind.LogNormal : RiskFactorKind.Additive);

// ---- 2. The book: per index a stock, a long 1y call, a short 6m put, a short 6m call and a 3m protective put. -------
var positions = new System.Collections.Generic.List<PortfolioPosition>();
var labels = new System.Collections.Generic.List<string>();
for (int i = 0; i < 3; i++)
{
    int spot = i, vol = 3 + i, rate = 6;
    string index = names[i];
    Add($"{index} stock", new SpotPosition(spot, 1_000));
    Add($"{index} long 1y 100C", new EuropeanOptionPosition(OptionType.Call, spot, vol, rate, 100.0, 1.0, 2_000, DividendYield: 0.02));
    Add($"{index} short 6m 90P", new EuropeanOptionPosition(OptionType.Put, spot, vol, rate, 90.0, 0.5, -3_000, DividendYield: 0.02));
    Add($"{index} short 6m 110C", new EuropeanOptionPosition(OptionType.Call, spot, vol, rate, 110.0, 0.5, -1_500, DividendYield: 0.02));
    Add($"{index} long 3m 95P", new EuropeanOptionPosition(OptionType.Put, spot, vol, rate, 95.0, 0.25, 2_000, DividendYield: 0.02));
}
void Add(string label, PortfolioPosition position) { labels.Add(label); positions.Add(position); }

var engine = new FullRevaluationEngine(factors, estimate.Covariance, positions);
Console.WriteLine();
Console.WriteLine($"== Book: {engine.PositionCount} positions on {engine.FactorCount} factors, value {engine.PortfolioValue:N0} ==");

// ---- 3. CPU run ----------------------------------------------------------------------------------------------------
var options = new MonteCarloVarOptions
{
    Scenarios = scenarios,
    Seed = 2024,
    Horizon = horizonDays,                      // the covariance is daily: moves are scaled by sqrt(10)
    TimeDecay = horizonDays / 252.0,            // options age by 10 trading days, so the P&L includes theta
};
engine.Run(options with { Scenarios = 2_000 }); // warm-up: JIT and thread pool, outside the timing
var clock = Stopwatch.StartNew();
MonteCarloVarResult cpu = engine.Run(options);
clock.Stop();
double cpuSeconds = clock.Elapsed.TotalSeconds;
VarEs measures = cpu.Measures(confidence);
Console.WriteLine();
Console.WriteLine($"== CPU: {scenarios:N0} scenarios, {horizonDays}-day horizon, {Environment.ProcessorCount} cores ==");
Console.WriteLine($"VaR {confidence * 100.0:F0}%: {measures.Var,12:N0}   ES {confidence * 100.0:F0}%: {measures.ExpectedShortfall,12:N0}   (loss-positive)");
Console.WriteLine($"Time {cpuSeconds * 1000.0:F0} ms  ({scenarios * (double)engine.PositionCount / cpuSeconds / 1e6:F1} M revaluations/s)");

// Decomposition needs the per-position P&L of every scenario (scenarios x positions doubles): use a smaller run.
MonteCarloVarResult detailed = engine.Run(options with { Scenarios = Math.Min(scenarios, 200_000), KeepInstrumentPnl = true });
RiskDecomposition decomposition = detailed.Decompose(confidence);
Console.WriteLine();
Console.WriteLine($"== Decomposition by position ({detailed.Pnl.Length:N0} scenarios, so its total differs a little from the run above; components add up to it) ==");
Console.WriteLine($"{"Position",-22}{"Quantity",10}{"Component VaR",16}{"% VaR",8}{"Component ES",16}{"% ES",8}");
double[] quantities = detailed.Quantities, pctVar = decomposition.PercentVar, pctEs = decomposition.PercentEs;
for (int i = 0; i < labels.Count; i++)
    Console.WriteLine($"{labels[i],-22}{quantities[i],10:N0}{decomposition.ComponentVar[i],16:N0}{pctVar[i] * 100.0,7:F1}%{decomposition.ComponentEs[i],16:N0}{pctEs[i] * 100.0,7:F1}%");
Console.WriteLine($"{"Total",-22}{"",10}{decomposition.Total.Var,16:N0}{"",8}{decomposition.Total.ExpectedShortfall,16:N0}");

// ---- 4. GPU run, only when the satellite is there ------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("== GPU ==");
string satellite = Environment.GetEnvironmentVariable("QFL_CUDA_SATELLITE")
    ?? Path.Combine(AppContext.BaseDirectory, "InFine.QuantFinance_CUDA.dll");
if (!File.Exists(satellite))
{
    Console.WriteLine($"GPU run skipped: the CUDA satellite was not found at {satellite}.");
    Console.WriteLine("Build it on a machine with an NVIDIA GPU, CUDA and Hybridizer, then run this sample again:");
    Console.WriteLine("  dotnet build InFine.QuantFinance.Cuda/InFine.QuantFinance.Cuda.csproj -c Release -p:HybridizeCuda=true");
    Console.WriteLine("See docs/gpu.md (Requirements and Quickstart). The satellite is picked up from artifacts/cuda or QFL_CUDA_SATELLITE.");
}
else if (!TryLoadHybridizerRuntime(out string? reason))
{
    Console.WriteLine($"GPU run skipped: the satellite is present but the Hybridizer runtime cannot be used here ({reason}).");
    Console.WriteLine("The runtime assembly is x64 only; run on an x64 host with an NVIDIA GPU (docs/gpu.md).");
}
else
{
    try
    {
        RunGpu(engine, options, satellite, cpu, cpuSeconds);
    }
    catch (Exception e)
    {
        // No device, a driver or architecture mismatch, a missing CUDA library: the CPU results above stand.
        Console.WriteLine($"GPU run failed and was skipped: {e.GetType().Name}: {e.Message}");
        Console.WriteLine("The satellite is compiled for the GPU present at build time (docs/gpu.md); rebuild it on this machine.");
    }
}
return;

// A separate non-inlined method: the JIT must not need Hybridizer types to compile the code above.
[MethodImpl(MethodImplOptions.NoInlining)]
static void RunGpu(FullRevaluationEngine engine, MonteCarloVarOptions options, string satellite, MonteCarloVarResult cpu, double cpuSeconds)
{
    GpuRunner.Result gpu = GpuRunner.Run(engine, options, satellite);
    double maxDiff = 0.0, scale = 0.0;
    for (int s = 0; s < gpu.Pnl.Length; s++)
    {
        maxDiff = Math.Max(maxDiff, Math.Abs(gpu.Pnl[s] - cpu.Pnl[s]));
        scale = Math.Max(scale, Math.Abs(cpu.Pnl[s]));
    }
    VarEs onGpu = HistoricalRisk.Measures(gpu.Pnl, 0.99);
    Console.WriteLine($"VaR 99%: {onGpu.Var,12:N0}   ES 99%: {onGpu.ExpectedShortfall,12:N0}");
    Console.WriteLine($"Time {gpu.Seconds * 1000.0:F0} ms end to end (host-device copies included), {cpuSeconds / gpu.Seconds:F1}x the CPU");
    Console.WriteLine($"max |GPU - CPU| P&L = {maxDiff:0.00E+0} ({maxDiff / scale:0.00E+0} of the largest |P&L|)");
}

static bool TryLoadHybridizerRuntime(out string? reason)
{
    try
    {
        Assembly.Load("Hybridizer.Runtime.CUDAImports");
        reason = null;
        return true;
    }
    catch (Exception e)
    {
        reason = $"{e.GetType().Name} when loading it in an {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} process";
        return false;
    }
}

// Daily moves of 7 factors driven by one market factor and idiosyncratic noise: spots as log returns, vols and the rate as
// absolute changes. Index vols fall when the index rises, as in real markets. Deterministic for a given seed.
static double[,] SyntheticMoves(int days, int seed)
{
    var rng = new Random(seed);
    double Gauss()
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
    double[] beta = { 1.0, 0.9, 0.6 }, idio = { 0.004, 0.0035, 0.007 }, volBeta = { -0.25, -0.2, -0.3 };
    var moves = new double[days, 7];
    for (int t = 0; t < days; t++)
    {
        double market = 0.009 * Gauss();
        double rate = 0.0004 * (0.3 * Gauss() + 0.5 * market / 0.009);
        for (int i = 0; i < 3; i++)
        {
            double r = beta[i] * market + idio[i] * Gauss();
            moves[t, i] = r;
            moves[t, 3 + i] = volBeta[i] * r + 0.003 * Gauss();
        }
        moves[t, 6] = rate;
    }
    return moves;
}
