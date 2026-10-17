using System;
using BenchmarkDotNet.Attributes;

namespace InFine.QuantFinance.Benchmarks
{
    /// <summary>
    /// Longstaff-Schwartz American put on the managed (C#) backend with serial reductions.
    /// Needs Hybridizer.Runtime.CUDAImports to load; Program skips this class otherwise. Not runnable on arm64.
    /// </summary>
    [MemoryDiagnoser]
    public class LongstaffSchwartzBenchmarks
    {
        [Params(4096, 16384)]
        public int SimulationCount;

        private const int DatesCount = 100;
        private Func<double> _price;

        [GlobalSetup]
        public void Setup() => _price = HybridizerScenarios.BuildLongstaffSchwartz(SimulationCount, DatesCount);

        [Benchmark]
        public double AmericanPut_CSharp_SerialReduction() => _price();
    }
}
