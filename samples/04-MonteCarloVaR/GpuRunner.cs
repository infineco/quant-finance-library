using System;
using System.Diagnostics;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Risk;

namespace InFine.QuantFinance.Samples;

/// <summary>
/// Runs the full-revaluation scenarios on the GPU through HybRunner. This is the only type that touches Hybridizer types;
/// callers reach it through a non-inlined call inside a try block, so a host where the runtime does not load (for
/// example an arm64 process) fails here and not in the CPU code.
/// </summary>
internal static class GpuRunner
{
    // Grid-stride loop: 120 blocks of 128 threads, as in the library's GPU tests.
    private const int GridDimX = 120, BlockDimX = 128;

    public sealed record Result(double[] Pnl, double Seconds);

    /// <summary>One warm-up launch (CUDA context creation, module load) and one timed launch of the whole batch.</summary>
    public static Result Run(FullRevaluationEngine engine, MonteCarloVarOptions options, string satellitePath)
    {
        // Load by full path: on Linux dlopen does not search the application folder for a bare name.
        HybRunner runner = HybRunner.Cuda(satellitePath).SetDistrib(GridDimX, BlockDimX);
        dynamic kernels = runner.Wrap(new FullRevaluationKernels());

        Launch(kernels, engine.CreateBatch(options with { Scenarios = 1024 }));

        FullRevaluationBatch batch = engine.CreateBatch(options);
        var clock = Stopwatch.StartNew();
        Launch(kernels, batch);
        clock.Stop();
        return new Result(batch.Pnl, clock.Elapsed.TotalSeconds);
    }

    private static void Launch(dynamic kernels, FullRevaluationBatch b)
    {
        double[] scratch = b.CreateScratch(GridDimX * BlockDimX);
        kernels.SimulateScenarios(0, b.ScenarioCount, b.Generator, b.Seed, b.Stream, b.SobolDirections, b.SobolShifts,
            b.FactorCount, b.CholeskyLower, b.FactorKind, b.BaseLevel, b.HalfVariance, b.InstrumentCount, b.InstrumentType,
            b.OptionSign, b.SpotFactor, b.VolFactor, b.RateFactor, b.Strike, b.Maturity, b.DividendYield, b.Quantity,
            b.BaseValue, b.TimeDecay, scratch, b.Pnl, b.ScenarioCount, b.StoreInstrumentPnl, b.InstrumentPnl);
    }
}
