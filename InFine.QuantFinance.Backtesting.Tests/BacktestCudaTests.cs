using System;
using System.IO;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace InFine.QuantFinance.Backtesting.Tests
{
    /// <summary>
    /// GPU = CPU for the stationary-bootstrap constant-mix kernel: <see cref="BacktestKernels.SimulateConstantMixPaths"/>
    /// launched through HybRunner against <see cref="BacktestKernels.EvaluateConstantMixPath"/> run path by path on the
    /// host. Kept in its own class so that no other test JIT-compiles a method touching Hybridizer types.
    /// </summary>
    public class BacktestCudaTests
    {
        private readonly ITestOutputHelper _output;

        public BacktestCudaTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [GpuFact(NativeEnvironment.CudaSatelliteName)]
        public void DeviceBootstrapMatchesTheHostPathByPath()
        {
            const int periods = 500, assets = 4, paths = 100_003, grid = 64, block = 128;
            const ulong seed = 0xB007_5742_2025UL;
            const uint stream = 3;
            const double restart = 1.0 / 20.0;
            var random = new System.Random(7);
            var returns = new double[periods * assets];
            var cash = new double[periods];
            for (int t = 0; t < periods; ++t)
            {
                cash[t] = 0.0001;
                for (int i = 0; i < assets; ++i)
                    returns[t * assets + i] = 0.0004 + 0.01 * (i + 1) * (random.NextDouble() - 0.5);
            }
            double[] weights = { 0.4, 0.3, 0.2, 0.1 };

            double[][] host = NewOutputs(paths), device = NewOutputs(paths);
            var hostScratch = new double[assets];
            for (int p = 0; p < paths; ++p)
                BacktestKernels.EvaluateConstantMixPath(p, seed, stream, restart, periods, assets, returns, cash, weights, 21, 0.0005, 0.0, 0.001,
                    hostScratch, 0, host[0], host[1], host[2], host[3], host[4], host[5]);

            var scratch = new double[grid * block * assets];
            HybRunner runner = HybRunner.Cuda(Path.Combine(AppContext.BaseDirectory, NativeEnvironment.CudaSatelliteName)).SetDistrib(grid, block);
            dynamic kernels = runner.Wrap(new BacktestKernels());
            kernels.SimulateConstantMixPaths(0, paths, seed, stream, restart, periods, assets, returns, cash, weights, 21, 0.0005, 0.0, 0.001,
                scratch, device[0], device[1], device[2], device[3], device[4], device[5]);
            Assert.Equal(cudaError_t.cudaSuccess, cuda.DeviceSynchronize());

            string[] names = { "terminal wealth", "mean return", "return variance", "max drawdown", "turnover", "total cost" };
            for (int o = 0; o < names.Length; ++o)
            {
                double worst = 0.0;
                for (int p = 0; p < paths; ++p)
                {
                    double error = Math.Abs(device[o][p] - host[o][p]);
                    Assert.True(error <= 1e-12 * Math.Abs(host[o][p]) + 1e-15, $"{names[o]}, path {p}: device {device[o][p]:R}, host {host[o][p]:R}");
                    if (host[o][p] != 0.0) worst = Math.Max(worst, error / Math.Abs(host[o][p]));
                }
                _output.WriteLine($"{names[o]}: max relative error {worst:E2} over {paths} paths");
            }
        }

        private static double[][] NewOutputs(int paths)
        {
            var outputs = new double[6][];
            for (int o = 0; o < outputs.Length; ++o) outputs[o] = new double[paths];
            return outputs;
        }
    }
}
