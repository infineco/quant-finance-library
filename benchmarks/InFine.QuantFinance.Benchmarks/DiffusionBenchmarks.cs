using System;
using BenchmarkDotNet.Attributes;

namespace InFine.QuantFinance.Benchmarks
{
    /// <summary>
    /// Black-Scholes-Merton path diffusion on the managed (C#) backend. Needs Hybridizer.Runtime.CUDAImports
    /// to load (alignedstorage); Program skips this class otherwise. Not runnable on arm64.
    /// </summary>
    [MemoryDiagnoser]
    public class DiffusionBenchmarks
    {
        [Params(512, 16384)]
        public int SimulationCount;

        private const int DatesCount = 256;
        private Action _simulate;

        [GlobalSetup]
        public void Setup() => _simulate = HybridizerScenarios.BuildDiffusion(SimulationCount, DatesCount);

        [Benchmark]
        public void Diffusion_Generics_CSharp() => _simulate();
    }
}
