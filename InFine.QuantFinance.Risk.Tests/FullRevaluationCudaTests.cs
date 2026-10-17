using System;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Risk.Tests
{
    /// <summary>
    /// CUDA launch of <see cref="FullRevaluationKernels.SimulateScenarios"/> through HybRunner, compared with the host run.
    /// Skipped unless a CUDA device, the Hybridizer runtime and the hybridized binary are present (never on the
    /// development Macs); kept in its own class so that no other test JIT-compiles a method touching Hybridizer types.
    /// </summary>
    public class FullRevaluationCudaTests
    {
        /// <summary>
        /// Hybridized CUDA binary of the library, expected next to the test assembly (the name the coherence tests'
        /// CudaBackend uses). The kernel class must be part of the hybridized assemblies.
        /// </summary>
        public const string BinaryName = NativeEnvironment.CudaSatelliteName;

        [GpuTheory(BinaryName)]
        [InlineData(ScenarioGenerator.Philox)]
        [InlineData(ScenarioGenerator.ScrambledSobol)]
        public void DeviceRunMatchesTheHostRun(ScenarioGenerator generator)
        {
            FullRevaluationEngine engine = FullRevaluationTests.MixedBook();
            // Sobol passes its direction numbers and scrambling shifts to the device as uint[] (Philox: a ulong seed).
            var options = new MonteCarloVarOptions { Scenarios = 1 << 16, Horizon = 10, TimeDecay = 10.0 / 252, KeepInstrumentPnl = true, Generator = generator };

            FullRevaluationBatch host = engine.CreateBatch(options);
            host.RunOnHost();

            const int gridDimX = 120, blockDimX = 128;
            FullRevaluationBatch device = engine.CreateBatch(options);
            double[] scratch = device.CreateScratch(gridDimX * blockDimX);
            // HybRunner resolves a bare file name against the current directory, not the test assembly's folder.
            HybRunner runner = HybRunner.Cuda(System.IO.Path.Combine(AppContext.BaseDirectory, BinaryName)).SetDistrib(gridDimX, blockDimX);
            dynamic kernels = runner.Wrap(new FullRevaluationKernels());
            kernels.SimulateScenarios(0, device.ScenarioCount, device.Generator, device.Seed, device.Stream,
                device.SobolDirections, device.SobolShifts, device.FactorCount, device.CholeskyLower, device.FactorKind,
                device.BaseLevel, device.HalfVariance, device.InstrumentCount, device.InstrumentType, device.OptionSign,
                device.SpotFactor, device.VolFactor, device.RateFactor, device.Strike, device.Maturity, device.DividendYield,
                device.Quantity, device.BaseValue, device.TimeDecay, scratch, device.Pnl, device.ScenarioCount,
                device.StoreInstrumentPnl, device.InstrumentPnl);

            // Same counters, same sums; only the device's exp / log / erfc may differ in the last bits.
            double scale = 0.0;
            foreach (double p in host.Pnl) scale = Math.Max(scale, Math.Abs(p));
            for (int s = 0; s < host.ScenarioCount; s++)
                Assert.True(Math.Abs(device.Pnl[s] - host.Pnl[s]) <= 1e-10 * scale, $"scenario {s}: device {device.Pnl[s]:R}, host {host.Pnl[s]:R}");
        }
    }
}
