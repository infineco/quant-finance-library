using System;
using BenchmarkDotNet.Attributes;

namespace InFine.QuantFinance.Benchmarks
{
    /// <summary>
    /// Workloads sized to keep the GPU busy for several seconds, against all CPU cores: a Monte Carlo VaR by full
    /// revaluation on 20M scenarios (N = 100 options, F = 101 factors), a stationary bootstrap of 1M paths x 2520 days x
    /// 10 assets, and 2M Heston COS prices. Measured on an RTX 4070 Laptop against a Ryzen 9 7845HX (24 threads): 5.7 s,
    /// 2.6 s and 3.9 s on the GPU (transfers included), 13.0 s, 5.3 s and 8.7 s on the CPU.
    /// </summary>
    /// <remarks>
    /// One invocation per iteration, one warm-up and three measured iterations: BenchmarkDotNet's default job would run
    /// these for tens of minutes. The attributes change the job in place (a <c>[SimpleJob]</c> would add a second job,
    /// which cannot run with <c>--inProcess</c>). Setup runs each workload once on both sides and checks that they agree,
    /// which itself takes about a minute and a half. Needs a CUDA device and the satellite; Program leaves the class out
    /// otherwise. Run it alone:
    /// <c>dotnet run -c Release --project benchmarks/InFine.QuantFinance.Benchmarks -- --filter '*LongGpu*' --inProcess</c>.
    /// </remarks>
    [WarmupCount(1)]
    [IterationCount(3)]
    [InvocationCount(1, 1)]   // one invocation per iteration, unroll factor 1
    public class LongGpuBenchmarks
    {
        private Action _varCpu, _varGpu, _bootCpu, _bootGpu, _hestonCpu, _hestonGpu;

        [GlobalSetup]
        public void Setup()
        {
            Console.WriteLine("GPU: " + GpuScenarios.DeviceName());
            var var = GpuScenarios.BuildFullRevaluation(20_000_000);
            _varCpu = var.CpuParallel;
            _varGpu = var.Gpu;
            var boot = GpuScenarios.BuildBootstrap(1_000_000);
            _bootCpu = boot.CpuParallel;
            _bootGpu = boot.Gpu;
            var heston = GpuScenarios.BuildHeston(2_000_000);
            _hestonCpu = heston.CpuParallel;
            _hestonGpu = heston.Gpu;
        }

        [Benchmark(Description = "VaR full reval, 20M scenarios, CPU all cores")]
        public void FullRevaluationCpu() => _varCpu();

        [Benchmark(Description = "VaR full reval, 20M scenarios, GPU")]
        public void FullRevaluationGpu() => GpuVsCpuBenchmarks.Gpu(_varGpu, "full revaluation M=20M");

        [Benchmark(Description = "Bootstrap 1M paths x 2520 days, CPU all cores")]
        public void BootstrapCpu() => _bootCpu();

        [Benchmark(Description = "Bootstrap 1M paths x 2520 days, GPU")]
        public void BootstrapGpu() => GpuVsCpuBenchmarks.Gpu(_bootGpu, "bootstrap 1M");

        [Benchmark(Description = "Heston COS, 2M options, CPU all cores")]
        public void HestonCpu() => _hestonCpu();

        [Benchmark(Description = "Heston COS, 2M options, GPU")]
        public void HestonGpu() => GpuVsCpuBenchmarks.Gpu(_hestonGpu, "Heston 2M");
    }
}
