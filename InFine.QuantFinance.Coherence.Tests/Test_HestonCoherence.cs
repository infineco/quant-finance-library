using System;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>
    /// Heston: Philox draws for the spot and for the independent driver, full-truncation Euler variance, then the
    /// log-Euler spot diffusion. Variance and spot paths are bit-identical on every backend.
    /// </summary>
    public class Test_HestonCoherence
    {
        private const int Paths = Market.Paths;
        private const int Steps = Market.HestonSteps;

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void VarianceAndSpotPaths_AreBitIdenticalAcrossBackends(string backendName)
        {
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            HestonPaths reference = Workloads.Heston(ExecutorBackends.Reference, Paths, Steps);
            HestonPaths actual = Workloads.Heston(backend, Paths, Steps);

            Matrices.AssertBitIdentical(reference.SpotBrownian, actual.SpotBrownian, backend.Name + " spot Brownian");
            Matrices.AssertBitIdentical(reference.IndependentBrownian, actual.IndependentBrownian, backend.Name + " independent Brownian");
            Matrices.AssertBitIdentical(reference.Variance, actual.Variance, backend.Name + " variance");
            Matrices.AssertBitIdentical(reference.LogSpot, actual.LogSpot, backend.Name + " log-spot");
        }

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void Paths_AreCorrelatedAndMeanRevert(string backendName)
        {
            // Guards the equality test against trivial arrays. Sample mean of the terminal variance against
            // E[v_T] = theta + (v0 - theta) exp(-kappa T) = 0.0398 - 0.0223 e^-1.5768 = 0.0398 - 0.00459 = 0.03521:
            // the std of v_T is about 0.065, so the sample mean has standard error 0.002 (tolerance 0.01 = 5 sigma;
            // the Euler scheme's bias at 50 steps is far below it). The two Brownian arrays must be independent:
            // their sample correlation has standard error 1/sqrt(N * Steps) = 0.0013 (tolerance 0.02).
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            HestonPaths run = Workloads.Heston(backend, Paths, Steps);

            double expectedVariance = Market.HestonTheta + (Market.HestonV0 - Market.HestonTheta) * Math.Exp(-Market.HestonKappa * Market.Maturity);
            double sum = 0.0;
            for (int p = 0; p < Paths; ++p)
                sum += run.Variance[Steps * Paths + p];
            Assert.InRange(sum / Paths, expectedVariance - 0.01, expectedVariance + 0.01);

            double sxy = 0.0, sxx = 0.0, syy = 0.0;
            for (int k = 0; k < run.SpotBrownian.Length; ++k)
            {
                sxy += run.SpotBrownian[k] * run.IndependentBrownian[k];
                sxx += run.SpotBrownian[k] * run.SpotBrownian[k];
                syy += run.IndependentBrownian[k] * run.IndependentBrownian[k];
            }
            Assert.InRange(sxy / Math.Sqrt(sxx * syy), -0.02, 0.02);
        }

        [CudaBackendFact]
        public void Cuda_VarianceAndSpot_MatchTheManagedReference()
        {
            // Same math-library caveat as the Black-Scholes test; the variance is order 0.04, the log-spot order 5,
            // 50 steps accumulate rounding differences: absolute tolerance 1e-9 on both.
            HestonPaths reference = Workloads.Heston(ExecutorBackends.Reference, Paths, Steps);
            HestonPaths cuda = Workloads.Heston(ExecutorBackends.Cuda, Paths, Steps);
            for (int k = 0; k < reference.Variance.Length; ++k)
            {
                Assert.InRange(cuda.Variance[k], reference.Variance[k] - 1e-9, reference.Variance[k] + 1e-9);
                Assert.InRange(cuda.LogSpot[k], reference.LogSpot[k] - 1e-9, reference.LogSpot[k] + 1e-9);
            }
        }
    }
}
