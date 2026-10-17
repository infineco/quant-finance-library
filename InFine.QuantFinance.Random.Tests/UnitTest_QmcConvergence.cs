using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Random.DotNet;
using InFine.QuantFinance.Random.QuasiRandom;
using Xunit;
using Xunit.Abstractions;

namespace InFine.QuantFinance.Random.Tests
{
    public class UnitTest_QmcConvergence
    {
        private readonly ITestOutputHelper _output;

        public UnitTest_QmcConvergence(ITestOutputHelper output)
        {
            _output = output;
        }

        const double Spot = 100.0, Strike = 105.0, Rate = 0.03, Sigma = 0.25;
        const int Replications = 16;
        static readonly int[] LogSizes = { 12, 14, 16 };
        // 16 non-uniform monitoring dates; the payoff only uses the terminal value, but the path is built
        // step by step from the bridge increments as a path-dependent pricer would.
        static readonly double[] Times =
        {
            0.02, 0.05, 0.1, 0.15, 0.25, 0.3, 0.4, 0.5, 0.55, 0.6, 0.7, 0.75, 0.8, 0.9, 0.95, 1.2,
        };

        // Discounted call payoff of the path whose standard per-step normals are in g (overwritten).
        private static double DiscountedPayoff(double[] g)
        {
            double lnS = Math.Log(Spot), previousT = 0.0;
            for (int i = 0; i < Times.Length; ++i)
            {
                double dt = Times[i] - previousT;
                lnS += (Rate - 0.5 * Sigma * Sigma) * dt + Sigma * Math.Sqrt(dt) * g[i];
                previousT = Times[i];
            }
            double maturity = Times[^1];
            return Math.Exp(-Rate * maturity) * Math.Max(Math.Exp(lnS) - Strike, 0.0);
        }

        // RMSE against the closed form across replications, at each size in LogSizes (nested prefixes).
        private static double[] Rmse(Func<int, Action<double[]>> replicationSampler, double exact)
        {
            int maxN = 1 << LogSizes[^1];
            var squaredErrors = new double[LogSizes.Length];
            var g = new double[Times.Length];
            for (int r = 0; r < Replications; ++r)
            {
                Action<double[]> nextPathNormals = replicationSampler(r);
                double sum = 0.0;
                int next = 0;
                for (int n = 1; n <= maxN; ++n)
                {
                    nextPathNormals(g);
                    sum += DiscountedPayoff(g);
                    if (n == 1 << LogSizes[next])
                    {
                        double error = sum / n - exact;
                        squaredErrors[next] += error * error;
                        ++next;
                    }
                }
            }
            var rmse = new double[LogSizes.Length];
            for (int k = 0; k < rmse.Length; ++k) rmse[k] = Math.Sqrt(squaredErrors[k] / Replications);
            return rmse;
        }

        [Fact]
        public void EuropeanCall_SobolBridgeConvergesFasterThanPhilox()
        {
            BlackScholesMertonPrimitive.CallPut(out double exact, out _, Spot, Strike, Rate, Sigma, Times[^1]);
            int n = Times.Length;
            var bridge = new BrownianBridge(Times);

            // Plain Monte Carlo: Philox, one seed per replication, inverse-CDF normals used as per-step increments.
            double[] mc = Rmse(r =>
            {
                var gen = new PhiloxGenerator(0x5EED0000UL + (ulong)r);
                return g => gen.GenerateGaussians(GaussianTransformEnum.InverseCumulativeDistributionFunction, 0.0, 1.0, n, g);
            }, exact);

            // Randomised QMC: Sobol with an independent random digital shift per replication, origin kept (the
            // shifted origin is an ordinary point and 2^m-point blocks stay nets), Brownian bridge.
            double[] qmc = Rmse(r =>
            {
                var gen = new SobolGenerator(n, SobolScramblingKind.DigitalShift, seed: 0x50B01UL, replication: (uint)r, skipZeroPoint: false);
                var z = new double[n];
                return g =>
                {
                    gen.NextGaussians(z);
                    bridge.BuildPath(z, g);
                    BrownianBridge.PathToStandardIncrements(n, bridge.InverseSqrtDt, g, 0, 1);
                };
            }, exact);

            for (int k = 0; k < LogSizes.Length; ++k)
                _output.WriteLine($"N = 2^{LogSizes[k]}: MC RMSE = {mc[k]:E3}, QMC RMSE = {qmc[k]:E3}, ratio = {mc[k] / qmc[k]:F1}");

            // Thresholds (seeds are fixed, but the bounds are chosen with margin for other seeds): with 16 replications each RMSE is
            // known to about +-20%. Over five different seed sets the observed RMSE ratio MC / QMC at 2^16 was 49..73
            // and the QMC decay from 2^12 to 2^16 was 8.9..12.5, against sqrt(16) = 4 for MC (the bridge puts the
            // terminal value, the only thing the payoff depends on, on Sobol dimension 1, so the error decays
            // close to 1/N). Asserting a ratio above 10 and a decay above 6 leaves a wide margin on both.
            Assert.True(qmc[2] < mc[2] / 10.0, $"QMC RMSE {qmc[2]} not below MC RMSE / 10 = {mc[2] / 10.0} at 2^16");
            Assert.True(qmc[0] / qmc[2] > 6.0, $"QMC RMSE decay {qmc[0] / qmc[2]} from 2^12 to 2^16 not clearly above the MC rate (4)");
        }
    }
}
