using BenchmarkDotNet.Attributes;
using System;

namespace InFine.QuantFinance.Benchmarks
{
    /// <summary>
    /// Managed MRG32K3A generation. The generator is built through HybridizerScenarios because MRG32K3A's state
    /// type (curandStateMRG32k3a_t) lives in Hybridizer.Runtime.CUDAImports: not loadable on arm64.
    /// TODO: add the Philox generator here once it is merged (same two benchmarks, same sizes) so the generators can be compared side by side.
    /// </summary>
    [MemoryDiagnoser]
    public class RandomBenchmarks
    {
        [Params(1024, 65536, 1048576)]
        public int N;

        private double[] _target;
        private Action<int, double[]> _uniform, _gaussian;

        [GlobalSetup]
        public void Setup()
        {
            _target = new double[N];
            (_uniform, _gaussian) = HybridizerScenarios.CreateMrg32k3a(20151114UL);
        }

        [Benchmark]
        public double Mrg32k3a_Uniform()
        {
            _uniform(N, _target);
            return _target[N - 1];
        }

        [Benchmark]
        public double Mrg32k3a_Gaussian_BoxMuller2()
        {
            _gaussian(N, _target);
            return _target[N - 1];
        }
    }
}
