using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Backtesting;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Risk;

namespace InFine.QuantFinance.Benchmarks
{
    /// <summary>
    /// GPU (HybRunner over the CUDA satellite) and CPU versions of three workloads, as delegates: the benchmark classes
    /// hold only the delegates, so loading them never needs the Hybridizer assembly (see <see cref="HybridizerScenarios"/>).
    /// </summary>
    /// <remarks>
    /// The GPU side is timed end to end, as a caller sees it: HybRunner marshals the input arrays to the device, runs
    /// the kernel and copies the outputs back on every call. <see cref="LastKernelMilliseconds"/> gives the kernel alone.
    /// The satellite comes from <c>dotnet build InFine.QuantFinance.Cuda -c Release -p:HybridizeCuda=true</c>.
    /// Every builder checks once that the GPU and CPU results agree before handing out the delegates.
    /// </remarks>
    internal static class GpuScenarios
    {
        internal const string Satellite = "InFine.QuantFinance_CUDA.dll";

        /// <summary>Kernel duration of the last GPU call, from HybRunner (transfers excluded).</summary>
        internal static double LastKernelMilliseconds { get; private set; }

        /// <summary>Null when a CUDA device and the satellite are available, otherwise the reason.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string Probe()
        {
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, Satellite)))
                return "the CUDA satellite " + Satellite + " is not next to the benchmarks (build InFine.QuantFinance.Cuda with -p:HybridizeCuda=true)";
            try
            {
                return cuda.GetDeviceCount(out int count) == cudaError_t.cudaSuccess && count > 0 ? null : "no CUDA device";
            }
            catch (Exception e)
            {
                return "CUDA runtime unavailable (" + e.GetType().Name + ")";
            }
        }

        /// <summary>Grid of 16 blocks per multiprocessor, 128 threads per block.</summary>
        private static HybRunner Runner(out int threads)
        {
            cuda.GetDeviceProperties(out cudaDeviceProp prop, 0);
            int grid = 16 * prop.multiProcessorCount;
            threads = grid * 128;
            return HybRunner.Cuda(Path.Combine(AppContext.BaseDirectory, Satellite)).SetDistrib(grid, 128);
        }

        internal static string DeviceName()
        {
            cuda.GetDeviceProperties(out cudaDeviceProp prop, 0);
            return new string(prop.name).TrimEnd('\0') + ", " + prop.multiProcessorCount + " SMs";
        }

        private static void Check(double[] cpu, double[] gpu, double relative, string what)
        {
            for (int k = 0; k < cpu.Length; ++k)
            {
                if (Math.Abs(gpu[k] - cpu[k]) > relative * Math.Abs(cpu[k]) + 1e-12)
                    throw new InvalidOperationException($"{what}: GPU and CPU differ at {k}: {gpu[k]:R} vs {cpu[k]:R}");
            }
        }

        // ---------------------------------------------------------------- Black-Scholes closed form

        internal sealed class BlackScholesBatch
        {
            public Action CpuOneThread, CpuParallel, Gpu;
        }

        internal static BlackScholesBatch BuildBlackScholes(int count)
        {
            var random = new System.Random(11);
            var sign = new int[count];
            double[] spot = new double[count], strike = new double[count], maturity = new double[count], rate = new double[count],
                dividend = new double[count], sigma = new double[count], cpu = new double[count], gpu = new double[count];
            for (int k = 0; k < count; ++k)
            {
                sign[k] = random.Next(2) == 0 ? 1 : -1;
                spot[k] = 50 + 100 * random.NextDouble();
                strike[k] = 50 + 100 * random.NextDouble();
                maturity[k] = 0.05 + 2 * random.NextDouble();
                rate[k] = 0.04 * random.NextDouble();
                dividend[k] = 0.02 * random.NextDouble();
                sigma[k] = 0.1 + 0.5 * random.NextDouble();
            }
            HybRunner runner = Runner(out _);
            dynamic kernels = runner.Wrap(new BlackScholesKernels());
            var batch = new BlackScholesBatch
            {
                CpuOneThread = () => BlackScholesKernels.PriceArray(count, cpu, sign, spot, strike, maturity, rate, dividend, sigma),
                CpuParallel = () => Parallel.For(0, (count + 4095) / 4096, chunk =>
                {
                    int end = Math.Min(count, (chunk + 1) * 4096);
                    for (int k = chunk * 4096; k < end; ++k)
                        BlackScholesKernels.PriceAt(k, cpu, sign, spot, strike, maturity, rate, dividend, sigma);
                }),
                Gpu = () =>
                {
                    kernels.PriceArray(count, gpu, sign, spot, strike, maturity, rate, dividend, sigma);
                    LastKernelMilliseconds = runner.LastKernelDuration.Elapsed.TotalMilliseconds;
                },
            };
            batch.CpuParallel();
            batch.Gpu();
            Check(cpu, gpu, 1e-12, "Black-Scholes prices");
            return batch;
        }

        // ---------------------------------------------------------------- Monte Carlo VaR by full revaluation

        internal sealed class FullRevaluationRun
        {
            public Action CpuParallel, Gpu;
            public int Factors, Instruments;
        }

        /// <summary>
        /// 50 equities: a log-normal spot and an additive implied volatility each, plus one rate (F = 101 factors); a call and
        /// a put on each (N = 100 options), 10-day horizon with time decay: the configuration measured on the Mac (M = 100k).
        /// </summary>
        internal static FullRevaluationEngine BuildBook()
        {
            const int assets = 50;
            var factors = new List<RiskFactor>();
            for (int a = 0; a < assets; ++a) factors.Add(new RiskFactor("S" + a, 50.0 + 2.0 * a, RiskFactorKind.LogNormal));
            for (int a = 0; a < assets; ++a) factors.Add(new RiskFactor("vol" + a, 0.15 + 0.005 * a, RiskFactorKind.Additive));
            factors.Add(new RiskFactor("rate", 0.03, RiskFactorKind.Additive));
            int f = factors.Count;
            var sd = new double[f];
            for (int i = 0; i < f; ++i) sd[i] = i < assets ? 0.015 : i < 2 * assets ? 0.008 : 0.0005;
            var covariance = new double[f, f];
            for (int i = 0; i < f; ++i)
                for (int j = 0; j < f; ++j)
                {
                    bool iSpot = i < assets, jSpot = j < assets, iVol = !iSpot && i < 2 * assets, jVol = !jSpot && j < 2 * assets;
                    double rho = i == j ? 1.0
                        : iSpot && jSpot ? 0.5
                        : (iSpot && j == i + assets) || (jSpot && i == j + assets) ? -0.6
                        : iVol && jVol ? 0.4
                        : 0.0;
                    covariance[i, j] = rho * sd[i] * sd[j];
                }
            var positions = new List<PortfolioPosition>();
            for (int a = 0; a < assets; ++a)
            {
                double s = factors[a].Level;
                positions.Add(new EuropeanOptionPosition(OptionType.Call, a, assets + a, 2 * assets, s * 1.05, 0.5 + 0.02 * a, 10.0));
                positions.Add(new EuropeanOptionPosition(OptionType.Put, a, assets + a, 2 * assets, s * 0.95, 0.25 + 0.03 * a, -5.0, 0.01));
            }
            return new FullRevaluationEngine(factors, covariance, positions);
        }

        internal static FullRevaluationRun BuildFullRevaluation(int scenarios)
        {
            FullRevaluationEngine engine = BuildBook();
            var options = new MonteCarloVarOptions { Scenarios = scenarios, Horizon = 10, TimeDecay = 10.0 / 252, Seed = 42 };
            FullRevaluationBatch host = engine.CreateBatch(options);
            FullRevaluationBatch device = engine.CreateBatch(options);
            HybRunner runner = Runner(out int threads);
            double[] scratch = device.CreateScratch(threads);
            dynamic kernels = runner.Wrap(new FullRevaluationKernels());
            var run = new FullRevaluationRun
            {
                Factors = device.FactorCount,
                Instruments = device.InstrumentCount,
                CpuParallel = () => host.RunOnHost(true, 1024),
                Gpu = () =>
                {
                    kernels.SimulateScenarios(0, device.ScenarioCount, device.Generator, device.Seed, device.Stream,
                        device.SobolDirections, device.SobolShifts, device.FactorCount, device.CholeskyLower, device.FactorKind,
                        device.BaseLevel, device.HalfVariance, device.InstrumentCount, device.InstrumentType, device.OptionSign,
                        device.SpotFactor, device.VolFactor, device.RateFactor, device.Strike, device.Maturity, device.DividendYield,
                        device.Quantity, device.BaseValue, device.TimeDecay, scratch, device.Pnl, device.ScenarioCount,
                        device.StoreInstrumentPnl, device.InstrumentPnl);
                    LastKernelMilliseconds = runner.LastKernelDuration.Elapsed.TotalMilliseconds;
                },
            };
            run.CpuParallel();
            run.Gpu();
            double scale = 0.0;
            foreach (double p in host.Pnl) scale = Math.Max(scale, Math.Abs(p));
            for (int s = 0; s < host.ScenarioCount; ++s)
            {
                if (Math.Abs(device.Pnl[s] - host.Pnl[s]) > 1e-10 * scale)
                    throw new InvalidOperationException($"full revaluation: scenario {s}: {device.Pnl[s]:R} vs {host.Pnl[s]:R}");
            }
            return run;
        }

        // ---------------------------------------------------------------- stationary bootstrap of a constant-mix backtest

        internal sealed class BootstrapRun
        {
            public Action CpuParallel, Gpu;
        }

        /// <summary>10 assets, 10 years of daily returns (2520 periods), mean block 20 days, monthly rebalancing with costs.</summary>
        internal static BootstrapRun BuildBootstrap(int paths)
        {
            const int periods = 2520, assets = 10;
            const ulong seed = 2025;
            var random = new System.Random(5);
            var returns = new double[periods * assets];
            var cash = new double[periods];
            for (int t = 0; t < periods; ++t)
            {
                cash[t] = 0.0001;
                for (int i = 0; i < assets; ++i) returns[t * assets + i] = 0.0003 + 0.02 * (random.NextDouble() - 0.5);
            }
            var weights = new double[assets];
            for (int i = 0; i < assets; ++i) weights[i] = 1.0 / assets;
            double[][] cpu = Outputs(paths), gpu = Outputs(paths);
            HybRunner runner = Runner(out int threads);
            var scratch = new double[threads * assets];
            dynamic kernels = runner.Wrap(new BacktestKernels());
            var run = new BootstrapRun
            {
                CpuParallel = () => Parallel.For(0, (paths + 255) / 256, () => new double[assets], (chunk, _, rowScratch) =>
                {
                    int end = Math.Min(paths, (chunk + 1) * 256);
                    for (int p = chunk * 256; p < end; ++p)
                        BacktestKernels.EvaluateConstantMixPath(p, seed, 0, 0.05, periods, assets, returns, cash, weights, 21, 0.0005, 0.0, 0.001,
                            rowScratch, 0, cpu[0], cpu[1], cpu[2], cpu[3], cpu[4], cpu[5]);
                    return rowScratch;
                }, _ => { }),
                Gpu = () =>
                {
                    kernels.SimulateConstantMixPaths(0, paths, seed, 0u, 0.05, periods, assets, returns, cash, weights, 21, 0.0005, 0.0, 0.001,
                        scratch, gpu[0], gpu[1], gpu[2], gpu[3], gpu[4], gpu[5]);
                    LastKernelMilliseconds = runner.LastKernelDuration.Elapsed.TotalMilliseconds;
                },
            };
            run.CpuParallel();
            run.Gpu();
            for (int o = 0; o < 6; ++o) Check(cpu[o], gpu[o], 1e-12, "bootstrap output " + o);
            return run;
        }

        // ---------------------------------------------------------------- Heston COS prices

        internal sealed class HestonRun
        {
            public Action CpuParallel, Gpu;
        }

        /// <summary>
        /// A grid of Heston prices under one parameter set (Fang and Oosterlee 2008, section 5.3), default COS settings: forward
        /// 100, strikes 60 to 140, maturities 0.1 to 3.1 years, calls and puts.
        /// </summary>
        internal static HestonRun BuildHeston(int count)
        {
            const double v0 = 0.0175, kappa = 1.5768, theta = 0.0398, xi = 0.5751, rho = -0.5711;
            var random = new System.Random(4);
            var sign = new int[count];
            double[] forward = new double[count], strike = new double[count], discount = new double[count], maturity = new double[count],
                cpu = new double[count], gpu = new double[count];
            for (int k = 0; k < count; ++k)
            {
                sign[k] = random.Next(2) == 0 ? 1 : -1;
                forward[k] = 100.0;
                strike[k] = 60.0 + 80.0 * random.NextDouble();
                maturity[k] = 0.1 + 3.0 * random.NextDouble();
                discount[k] = Math.Exp(-0.03 * maturity[k]);
            }
            HybRunner runner = Runner(out _);
            dynamic kernels = runner.Wrap(new HestonKernels());
            var run = new HestonRun
            {
                CpuParallel = () => Parallel.For(0, (count + 1023) / 1024, chunk =>
                {
                    int end = Math.Min(count, (chunk + 1) * 1024);
                    for (int k = chunk * 1024; k < end; ++k)
                        HestonKernels.PriceAt(k, cpu, sign, forward, strike, discount, maturity, v0, kappa, theta, xi, rho,
                            HestonKernels.DefaultTerms, HestonKernels.DefaultTruncationWidth);
                }),
                Gpu = () =>
                {
                    kernels.PriceArray(count, gpu, sign, forward, strike, discount, maturity, v0, kappa, theta, xi, rho,
                        HestonKernels.DefaultTerms, HestonKernels.DefaultTruncationWidth);
                    LastKernelMilliseconds = runner.LastKernelDuration.Elapsed.TotalMilliseconds;
                },
            };
            run.CpuParallel();
            run.Gpu();
            for (int k = 0; k < count; ++k)
            {
                if (Math.Abs(gpu[k] - cpu[k]) > 1e-9 * forward[k])
                    throw new InvalidOperationException($"Heston: GPU and CPU differ at {k}: {gpu[k]:R} vs {cpu[k]:R}");
            }
            return run;
        }

        private static double[][] Outputs(int paths)
        {
            var outputs = new double[6][];
            for (int o = 0; o < 6; ++o) outputs[o] = new double[paths];
            return outputs;
        }
    }
}
