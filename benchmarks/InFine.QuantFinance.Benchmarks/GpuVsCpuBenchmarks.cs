using System;
using BenchmarkDotNet.Attributes;

namespace InFine.QuantFinance.Benchmarks
{
    /// <summary>
    /// GPU against CPU on three workloads (see <see cref="GpuScenarios"/>): the GPU side is timed end to end (transfers
    /// included, as a caller of HybRunner sees it); the CPU side uses every core. The kernel-only time of the GPU is
    /// printed after each GPU benchmark. Needs a CUDA device and the satellite; Program leaves the class out otherwise.
    /// </summary>
    public class GpuVsCpuBenchmarks
    {
        private Action _bsOne, _bsParallel, _bsGpu, _bootCpu, _bootGpu;

        [GlobalSetup]
        public void Setup()
        {
            Console.WriteLine("GPU: " + GpuScenarios.DeviceName());
            var bs = GpuScenarios.BuildBlackScholes(1_000_000);
            _bsOne = bs.CpuOneThread;
            _bsParallel = bs.CpuParallel;
            _bsGpu = bs.Gpu;
            var boot = GpuScenarios.BuildBootstrap(100_000);
            _bootCpu = boot.CpuParallel;
            _bootGpu = boot.Gpu;
        }

        internal static void Gpu(Action run, string what)
        {
            run();
            Console.WriteLine($"// {what}: kernel {GpuScenarios.LastKernelMilliseconds:F3} ms");
        }

        [Benchmark(Description = "BS 1M options, CPU 1 thread")]
        public void BlackScholesCpuOneThread() => _bsOne();

        [Benchmark(Description = "BS 1M options, CPU all cores")]
        public void BlackScholesCpuParallel() => _bsParallel();

        [Benchmark(Description = "BS 1M options, GPU")]
        public void BlackScholesGpu() => Gpu(_bsGpu, "BS 1M");

        [Benchmark(Description = "Bootstrap 100k paths x 2520 days, CPU all cores")]
        public void BootstrapCpu() => _bootCpu();

        [Benchmark(Description = "Bootstrap 100k paths x 2520 days, GPU")]
        public void BootstrapGpu() => Gpu(_bootGpu, "bootstrap");
    }

    /// <summary>
    /// Monte Carlo VaR by full revaluation, N = 100 options, F = 101 factors (the Mac reference: M = 100k, 2.1M scenarios/s
    /// on 18 arm64 cores), GPU end to end against all CPU cores.
    /// </summary>
    public class FullRevaluationGpuBenchmarks
    {
        private Action _cpu, _gpu;

        [Params(100_000, 1_000_000)]
        public int Scenarios { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            var full = GpuScenarios.BuildFullRevaluation(Scenarios);
            Console.WriteLine($"GPU: {GpuScenarios.DeviceName()}; full revaluation M = {Scenarios}, N = {full.Instruments}, F = {full.Factors}");
            _cpu = full.CpuParallel;
            _gpu = full.Gpu;
        }

        [Benchmark(Baseline = true, Description = "Full reval VaR, CPU all cores")]
        public void Cpu() => _cpu();

        [Benchmark(Description = "Full reval VaR, GPU")]
        public void Gpu() => GpuVsCpuBenchmarks.Gpu(_gpu, "full revaluation M=" + Scenarios);
    }
}
