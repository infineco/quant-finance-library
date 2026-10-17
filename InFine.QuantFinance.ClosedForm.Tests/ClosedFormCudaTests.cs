using System;
using System.IO;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    /// <summary>
    /// GPU = CPU for the closed-form [EntryPoint] array kernels: the same batch is evaluated by the static method on the host
    /// (where threadIdx = 0, blockDim = gridDim = 1, so the grid-stride loop covers the batch) and through HybRunner on the
    /// device, then compared element by element.
    /// </summary>
    /// <remarks>
    /// Both sides run the library's own normal CDF (Cody erfc in C#); only exp, log and sqrt come from the device math
    /// library, so prices agree to about 1e-14 relative. The tolerance is 1e-12 relative plus an absolute floor of 1e-12 of
    /// the underlying for prices that are numerically zero (deep out of the money). Kept in its own class so that no other
    /// test JIT-compiles a method touching Hybridizer types (they do not load on arm64).
    /// </remarks>
    public class ClosedFormCudaTests
    {
        private const string Binary = NativeEnvironment.CudaSatelliteName;
        private const int Count = 100_003;   // not a multiple of the launch, so the grid-stride tail is exercised
        private const int GridDim = 256, BlockDim = 128;

        private static readonly object s_gate = new object();
        private readonly ITestOutputHelper _output;

        public ClosedFormCudaTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static dynamic Wrap(object kernels)
        {
            // HybRunner resolves a bare file name against the current directory, not the test assembly's folder.
            HybRunner runner = HybRunner.Cuda(Path.Combine(AppContext.BaseDirectory, Binary)).SetDistrib(GridDim, BlockDim);
            return runner.Wrap(kernels);
        }

        private sealed class Batch
        {
            public int[] Sign = new int[Count];
            public double[] Spot = new double[Count], Strike = new double[Count], Maturity = new double[Count], Rate = new double[Count],
                Dividend = new double[Count], Sigma = new double[Count], Forward = new double[Count], Discount = new double[Count];

            public Batch(int seed)
            {
                var random = new System.Random(seed);
                for (int k = 0; k < Count; ++k)
                {
                    Sign[k] = random.Next(2) == 0 ? 1 : -1;
                    Spot[k] = 50.0 + 100.0 * random.NextDouble();
                    Strike[k] = 50.0 + 100.0 * random.NextDouble();
                    Maturity[k] = 0.02 + 3.0 * random.NextDouble();
                    Rate[k] = -0.01 + 0.07 * random.NextDouble();
                    Dividend[k] = 0.04 * random.NextDouble();
                    Sigma[k] = 0.05 + 0.75 * random.NextDouble();
                    Forward[k] = Spot[k] * Math.Exp((Rate[k] - Dividend[k]) * Maturity[k]);
                    Discount[k] = Math.Exp(-Rate[k] * Maturity[k]);
                }
            }
        }

        private void AssertClose(double[] host, double[] device, double relative, double[] scale, double floor, string what)
        {
            double worst = 0.0;
            for (int k = 0; k < host.Length; ++k)
            {
                double error = Math.Abs(device[k] - host[k]);
                Assert.True(error <= relative * Math.Abs(host[k]) + floor * scale[k],
                    $"{what}: element {k}: device {device[k]:R}, host {host[k]:R}");
                if (Math.Abs(host[k]) > floor * scale[k]) worst = Math.Max(worst, error / Math.Abs(host[k]));
            }
            _output.WriteLine($"{what}: max relative error {worst:E2} over {host.Length} options");
        }

        [GpuFact(Binary)]
        public void BlackScholesMertonPrimitive_CallPutArrays_MatchTheHost()
        {
            var b = new Batch(1);
            double[] hostCall = new double[Count], hostPut = new double[Count], call = new double[Count], put = new double[Count];
            BlackScholesMertonPrimitive.CallPutArrayMulti(Count, hostCall, hostPut, b.Spot, b.Strike, b.Rate, b.Sigma, b.Maturity);
            lock (s_gate) Wrap(new BlackScholesMertonPrimitive()).CallPutArrayMulti(Count, call, put, b.Spot, b.Strike, b.Rate, b.Sigma, b.Maturity);
            AssertClose(hostCall, call, 1e-12, b.Spot, 1e-12, "CallPut_ArrayMulti call");
            AssertClose(hostPut, put, 1e-12, b.Spot, 1e-12, "CallPut_ArrayMulti put");

            BlackScholesMertonPrimitive.CallPutArray(Count, hostCall, hostPut, b.Spot, b.Strike, 0.03, 0.25, b.Maturity);
            lock (s_gate) Wrap(new BlackScholesMertonPrimitive()).CallPutArray(Count, call, put, b.Spot, b.Strike, 0.03, 0.25, b.Maturity);
            AssertClose(hostCall, call, 1e-12, b.Spot, 1e-12, "CallPut_Array call");
            AssertClose(hostPut, put, 1e-12, b.Spot, 1e-12, "CallPut_Array put");
        }

        [GpuFact(Binary)]
        public void BlackScholesAndBlack76_PriceAndGreeksArrays_MatchTheHost()
        {
            var b = new Batch(2);
            double[] host = new double[Count], device = new double[Count];
            BlackScholesKernels.PriceArray(Count, host, b.Sign, b.Spot, b.Strike, b.Maturity, b.Rate, b.Dividend, b.Sigma);
            lock (s_gate) Wrap(new BlackScholesKernels()).PriceArray(Count, device, b.Sign, b.Spot, b.Strike, b.Maturity, b.Rate, b.Dividend, b.Sigma);
            AssertClose(host, device, 1e-12, b.Spot, 1e-12, "BlackScholesKernels.PriceArray");

            var hostGreeks = new double[10][];
            var deviceGreeks = new double[10][];
            for (int g = 0; g < 10; ++g) { hostGreeks[g] = new double[Count]; deviceGreeks[g] = new double[Count]; }
            BlackScholesKernels.GreeksArray(Count, hostGreeks[0], hostGreeks[1], hostGreeks[2], hostGreeks[3], hostGreeks[4], hostGreeks[5], hostGreeks[6],
                hostGreeks[7], hostGreeks[8], hostGreeks[9], b.Sign, b.Spot, b.Strike, b.Maturity, b.Rate, b.Dividend, b.Sigma);
            lock (s_gate)
                Wrap(new BlackScholesKernels()).GreeksArray(Count, deviceGreeks[0], deviceGreeks[1], deviceGreeks[2], deviceGreeks[3], deviceGreeks[4], deviceGreeks[5],
                    deviceGreeks[6], deviceGreeks[7], deviceGreeks[8], deviceGreeks[9], b.Sign, b.Spot, b.Strike, b.Maturity, b.Rate, b.Dividend, b.Sigma);
            string[] names = { "price", "delta", "gamma", "vega", "theta", "rho", "dividend rho", "vanna", "volga", "dual delta" };
            // Greeks are differences of terms of the price's size: 1e-10 relative, floor 1e-10 of the spot.
            for (int g = 0; g < 10; ++g)
                AssertClose(hostGreeks[g], deviceGreeks[g], 1e-10, b.Spot, 1e-10, "BlackScholesKernels.GreeksArray " + names[g]);

            Black76Kernels.PriceArray(Count, host, b.Sign, b.Forward, b.Strike, b.Discount, b.Maturity, b.Sigma);
            lock (s_gate) Wrap(new Black76Kernels()).PriceArray(Count, device, b.Sign, b.Forward, b.Strike, b.Discount, b.Maturity, b.Sigma);
            AssertClose(host, device, 1e-12, b.Spot, 1e-12, "Black76Kernels.PriceArray");

            for (int g = 0; g < 9; ++g) { Array.Clear(hostGreeks[g]); Array.Clear(deviceGreeks[g]); }
            Black76Kernels.GreeksArray(Count, hostGreeks[0], hostGreeks[1], hostGreeks[2], hostGreeks[3], hostGreeks[4], hostGreeks[5], hostGreeks[6],
                hostGreeks[7], hostGreeks[8], b.Sign, b.Forward, b.Strike, b.Rate, b.Maturity, b.Sigma);
            lock (s_gate)
                Wrap(new Black76Kernels()).GreeksArray(Count, deviceGreeks[0], deviceGreeks[1], deviceGreeks[2], deviceGreeks[3], deviceGreeks[4], deviceGreeks[5],
                    deviceGreeks[6], deviceGreeks[7], deviceGreeks[8], b.Sign, b.Forward, b.Strike, b.Rate, b.Maturity, b.Sigma);
            string[] names76 = { "price", "delta", "gamma", "vega", "theta", "rho", "vanna", "volga", "dual delta" };
            for (int g = 0; g < 9; ++g)
                AssertClose(hostGreeks[g], deviceGreeks[g], 1e-10, b.Spot, 1e-10, "Black76Kernels.GreeksArray " + names76[g]);

        }

        [GpuFact(Binary)]
        public void ImpliedVolatilityBlack76Array_MatchesTheHostAndRecoversTheVolatility()
        {
            var b = new Batch(3);
            var prices = new double[Count];
            Black76Kernels.PriceArray(Count, prices, b.Sign, b.Forward, b.Strike, b.Discount, b.Maturity, b.Sigma);
            double[] host = new double[Count], device = new double[Count];
            ImpliedVolatilityKernels.Black76Array(Count, host, b.Sign, prices, b.Forward, b.Strike, b.Discount, b.Maturity);
            lock (s_gate) Wrap(new ImpliedVolatilityKernels()).Black76Array(Count, device, b.Sign, prices, b.Forward, b.Strike, b.Discount, b.Maturity);

            // Let's Be Rational is accurate to a few ulps where the price carries information about the volatility; where
            // the time value is below 1e-10 of the forward it is not identifiable and both sides may legitimately differ.
            int compared = 0;
            double worst = 0.0, worstRecovery = 0.0;
            for (int k = 0; k < Count; ++k)
            {
                double intrinsic = b.Discount[k] * Math.Max(b.Sign[k] * (b.Forward[k] - b.Strike[k]), 0.0);
                if (prices[k] - intrinsic < 1e-10 * b.Forward[k]) continue;
                compared++;
                double error = Math.Abs(device[k] - host[k]);
                Assert.True(error <= 1e-10 * host[k], $"implied vol {k}: device {device[k]:R}, host {host[k]:R}");
                worst = Math.Max(worst, error / host[k]);
                worstRecovery = Math.Max(worstRecovery, Math.Abs(device[k] - b.Sigma[k]) / b.Sigma[k]);
            }
            _output.WriteLine($"Black76Array: {compared} identifiable options, max relative device-host {worst:E2}, max relative error to the true vol {worstRecovery:E2}");
            Assert.True(compared > Count / 2);
            Assert.True(worstRecovery < 1e-6, $"recovery {worstRecovery}");
        }

        [GpuFact(Binary)]
        public void HestonPriceArray_MatchesTheHost()
        {
            // Fang and Oosterlee (2008) section 5.3 parameters (Feller condition violated), default COS settings.
            const double v0 = 0.0175, kappa = 1.5768, theta = 0.0398, xi = 0.5751, rho = -0.5711;
            const int count = 20_011;
            var b = new Batch(4);
            var maturity = new double[count];
            var discount = new double[count];
            for (int k = 0; k < count; ++k) { maturity[k] = 0.1 + b.Maturity[k]; discount[k] = Math.Exp(-b.Rate[k] * maturity[k]); }
            double[] host = new double[count], device = new double[count];
            HestonKernels.PriceArray(count, host, b.Sign, b.Forward, b.Strike, discount, maturity, v0, kappa, theta, xi, rho,
                HestonKernels.DefaultTerms, HestonKernels.DefaultTruncationWidth);
            lock (s_gate)
                Wrap(new HestonKernels()).PriceArray(count, device, b.Sign, b.Forward, b.Strike, discount, maturity, v0, kappa, theta, xi, rho,
                    HestonKernels.DefaultTerms, HestonKernels.DefaultTruncationWidth);
            // A COS price is a sum of N terms of complex exponentials (struct Cplx): 1e-9 relative to the forward.
            double worst = 0.0;
            for (int k = 0; k < count; ++k)
            {
                double error = Math.Abs(device[k] - host[k]);
                Assert.True(error <= 1e-9 * b.Forward[k], $"Heston {k}: device {device[k]:R}, host {host[k]:R}");
                worst = Math.Max(worst, error / b.Forward[k]);
            }
            _output.WriteLine($"HestonKernels.PriceArray: max |device - host| / F = {worst:E2} over {count} options");
        }
    }
}
