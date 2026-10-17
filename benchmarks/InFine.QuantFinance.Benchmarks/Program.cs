using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace InFine.QuantFinance.Benchmarks
{
    public static class Program
    {
        // Benchmarks whose scenarios need Hybridizer.Runtime.CUDAImports (alignedstorage, curand device state...).
        private static readonly Type[] HybridizerDependent =
        {
            typeof(RandomBenchmarks),
            typeof(DiffusionBenchmarks),
            typeof(LongstaffSchwartzBenchmarks),
        };

        public static int Main(string[] args)
        {
            var types = typeof(Program).Assembly.GetTypes()
                .Where(t => t.IsClass && t.IsPublic && t.Name.EndsWith("Benchmarks", StringComparison.Ordinal))
                .ToList();

            string hybridizerError = HybridizerScenarios.Probe();
            if (hybridizerError != null)
            {
                types = types.Except(HybridizerDependent).ToList();
                Console.Error.WriteLine(
                    "NOTE: Hybridizer.Runtime.CUDAImports cannot be loaded in this " +
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture +
                    " process (" + hybridizerError + ").");
                Console.Error.WriteLine(
                    "      RandomBenchmarks, DiffusionBenchmarks and LongstaffSchwartzBenchmarks are unavailable here; run on an x64 host.");
            }

            // GPU benchmarks need a CUDA device and the satellite built by InFine.QuantFinance.Cuda -p:HybridizeCuda=true.
            string gpuError = hybridizerError ?? GpuScenarios.Probe();
            if (gpuError != null)
            {
                types = types.Except(new[] { typeof(GpuVsCpuBenchmarks), typeof(FullRevaluationGpuBenchmarks), typeof(LongGpuBenchmarks) }).ToList();
                Console.Error.WriteLine("NOTE: GPU benchmarks unavailable: " + gpuError + ".");
            }

            // BenchmarkDotNet's optimizations validator loads every assembly this one references, which
            // throws when Hybridizer.Runtime.CUDAImports cannot load (arm64); only disable it in that case.
            IConfig config = DefaultConfig.Instance;
            if (hybridizerError != null)
                config = config.WithOptions(ConfigOptions.DisableOptimizationsValidator);

            BenchmarkSwitcher.FromTypes(types.ToArray()).Run(args, config);
            return 0;
        }
    }
}
