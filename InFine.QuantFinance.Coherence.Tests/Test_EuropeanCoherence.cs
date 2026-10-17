using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Scheduling;
using InFine.QuantFinance.TestSupport;
using Hybridizer.Runtime.CUDAImports;
using Xunit;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>
    /// European call by Monte Carlo: the premium of every path is bit-identical on every backend; their mean is
    /// identical up to the order of summation.
    /// </summary>
    public class Test_EuropeanCoherence
    {
        private const int Paths = Market.Paths;
        private const int Steps = Market.BlackScholesSteps;
        private const double Strike = 100.0;

        /// <summary>
        /// Relative tolerance on a mean of N non-negative terms summed in a different order.
        /// A sum of N positive doubles carries a relative error of at most (N - 1) * 2^-53 (about 1.1e-16 per addition,
        /// N = 1009 gives 1.1e-13); two summation orders therefore differ by at most twice that. 1e-12 covers it with
        /// a factor 4 of headroom.
        /// </summary>
        private const double SummationOrderRelativeTolerance = 1e-12;

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void PremiumPerPath_IsBitIdenticalAcrossBackends(string backendName)
        {
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            double[] reference = Premiums(ExecutorBackends.Reference);
            double[] actual = Premiums(backend);
            Matrices.AssertBitIdentical(reference, actual, backend.Name);
        }

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void ExecutorFor_ComputesTheSamePremiums(string backendName)
        {
            // The same payoff through IParallelExecutor.For (Sequential, SingleBlock or MultiBlock = Parallel.For)
            // instead of the ISimulatable kernel: bit-identical.
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            ArrayDoubleSpot logSpot = Workloads.DiffuseBlackScholes(backend, Paths, Steps);
            double[] bySimulate = Workloads.EuropeanCallPremiums(backend, logSpot, Paths, Steps, Strike);
            double[] byFor = Workloads.EuropeanCallPremiumsByExecutorFor(backend.CreateExecutor(), logSpot, Paths, Steps, Strike);
            Matrices.AssertBitIdentical(bySimulate, byFor, backend.Name);
        }

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void MonteCarloMean_IsIdenticalUpToSummationOrder(string backendName)
        {
            // The premiums are bit-identical, so the mean can only differ through the order of the additions. Backends
            // sum like their device counterparts: SequentialExecutor.Reduce and SingleBlockParallelExecutor.Reduce add in
            // index order (bit-identical to the host sum); MultiBlockParallelExecutor.Reduce is not implemented (it
            // throws), so that backend sums per chunk and adds the chunk sums, as the multi-block CUDA reduction does.
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            double[] premiums = Premiums(backend);
            double reference = Workloads.SequentialSum(premiums) / Paths;
            double mean = Mean(backend, premiums);
            Assert.InRange(Math.Abs(mean - reference) / reference, 0.0, SummationOrderRelativeTolerance);

            if (!(backend.CreateExecutor() is MultiBlockParallelExecutor))
                Assert.Equal(BitConverter.DoubleToInt64Bits(reference), BitConverter.DoubleToInt64Bits(mean));
        }

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void MonteCarloMean_MatchesBlackScholes(string backendName)
        {
            // Independent reference: closed-form Black-Scholes call. The estimate is within 4.5 standard errors
            // (error of the mean = sample standard deviation / sqrt(N)); the draws are seeded, so this is
            // deterministic, not a flaky statistical test.
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            double[] premiums = Premiums(backend);
            double mean = Workloads.SequentialSum(premiums) / Paths;
            double sumSq = 0.0;
            foreach (double v in premiums) sumSq += (v - mean) * (v - mean);
            double stdErr = Math.Sqrt(sumSq / (Paths - 1.0) / Paths);

            BlackScholesMertonPrimitive.CallPut(out double call, out double put, Market.Spot, Strike, Market.Rate, Market.Volatility, Market.Maturity);
            Assert.InRange(mean, call - 4.5 * stdErr, call + 4.5 * stdErr);
        }

        private static double[] Premiums(ExecutorBackend backend)
        {
            ArrayDoubleSpot logSpot = Workloads.DiffuseBlackScholes(backend, Paths, Steps);
            return Workloads.EuropeanCallPremiums(backend, logSpot, Paths, Steps, Strike);
        }

        private static double Mean(ExecutorBackend backend, double[] premiums)
        {
            IParallelExecutor executor = backend.CreateExecutor();
            double sum;
            if (executor is MultiBlockParallelExecutor)
            {
                // chunk sums, added in chunk order (8 chunks, the contiguous split of the backends)
                const int chunks = 8;
                sum = 0.0;
                for (int c = 0; c < chunks; ++c)
                {
                    Chunking.Bounds(0, premiums.Length, chunks, c, out int start, out int stop);
                    double partial = 0.0;
                    for (int k = start; k < stop; ++k) partial += premiums[k];
                    sum += partial;
                }
            }
            else
            {
                var storage = new alignedstorage_double(premiums.Length);
                for (int k = 0; k < premiums.Length; ++k) storage[k] = premiums[k];
                sum = executor.Reduce(storage, 0, premiums.Length, (a, b) => a + b, 0.0);
                storage.destroy();
            }
            return sum / premiums.Length;
        }

        [CudaBackendFact]
        public void Cuda_PremiumsAndMean_MatchTheManagedReference()
        {
            // Premiums: exp comes from the device math library; relative tolerance 1e-12 per path. The mean is a
            // device reduction in another order: same relative tolerance as the managed summation-order test.
            double[] reference = Premiums(ExecutorBackends.Reference);
            double[] cuda = Premiums(ExecutorBackends.Cuda);
            for (int k = 0; k < reference.Length; ++k)
                Assert.InRange(cuda[k], reference[k] * (1 - 1e-12) - 1e-12, reference[k] * (1 + 1e-12) + 1e-12);
            double referenceMean = Workloads.SequentialSum(reference) / Paths;
            double cudaMean = Workloads.SequentialSum(cuda) / Paths;
            Assert.InRange(Math.Abs(cudaMean - referenceMean) / referenceMean, 0.0, SummationOrderRelativeTolerance);
        }
    }
}
