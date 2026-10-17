using System;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>
    /// Black-Scholes-Merton diffusion: same seed, same log-spot matrix, whatever the backend.
    /// Everything compared here is bit-identical (no tolerance): the only operations are IEEE additions,
    /// multiplications and square roots in the same order, on draws that are a pure function of
    /// (seed, path, stream, index).
    /// </summary>
    public class Test_BlackScholesCoherence
    {
        private const int Paths = Market.Paths;
        private const int Steps = Market.BlackScholesSteps;

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void BrownianFill_IsBitIdenticalAcrossBackends(string backendName)
        {
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            double[] reference = Workloads.SpotBrownian(ExecutorBackends.Reference, (p, t) => new ArrayBrownian(p, t), Paths, Steps);
            double[] actual = Workloads.SpotBrownian(backend, (p, t) => new ArrayBrownian(p, t), Paths, Steps);
            Matrices.AssertBitIdentical(reference, actual, backend.Name);
        }

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void LogSpot_IsBitIdenticalAcrossBackends(string backendName)
        {
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            double[] reference = AlignedLogSpot(ExecutorBackends.Reference);
            double[] actual = AlignedLogSpot(backend);
            Matrices.AssertBitIdentical(reference, actual, backend.Name);
        }

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void LogSpot_DoesNotDependOnTheStorage(string backendName)
        {
            // Aligned (native) and unaligned (managed array) storage hold the same numbers.
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            double[] aligned = AlignedLogSpot(ExecutorBackends.Reference);
            double[] unaligned = Workloads.BlackScholesLogSpot(
                backend, (p, t) => new UnalignedArrayDoubleSpot(p, t), (p, t) => new UnalignedArrayBrownian(p, t), Paths, Steps);
            Matrices.AssertBitIdentical(aligned, unaligned, backend.Name);
        }

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void EquityDiffusionOverInterfaces_MatchesBlackScholesMerton(string backendName)
        {
            // BlackScholesMerton<...> and BaseEquityDiffusion run the same EquityDiffusion kernel, one specialized
            // over concrete types, the other over interfaces: they must agree bit for bit.
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            double[] blackScholes = AlignedLogSpot(ExecutorBackends.Reference);
            double[] equity = Workloads.EquityDiffusionLogSpot(backend, Paths, Steps);
            Matrices.AssertBitIdentical(blackScholes, equity, backend.Name);
        }

        [HybridizerTheory]
        [MemberData(nameof(ExecutorBackends.ManagedNames), MemberType = typeof(ExecutorBackends))]
        public void LogSpot_HasTheBlackScholesMoments(string backendName)
        {
            // Guards the equality tests against comparing two arrays of zeros: terminal log-spot is
            // N(ln S0 + (r - sigma^2/2) T, sigma^2 T). With 1009 paths the sample mean has standard error
            // sigma sqrt(T) / sqrt(N) = 0.0079 and the sample standard deviation about 0.0056: 5 sigma tolerances.
            ExecutorBackend backend = ExecutorBackends.Get(backendName);
            double[] logSpot = AlignedLogSpot(backend);
            double sum = 0.0, sumSq = 0.0;
            for (int p = 0; p < Paths; ++p)
            {
                double z = logSpot[Steps * Paths + p];
                sum += z;
                sumSq += z * z;
            }
            double mean = sum / Paths;
            double stdev = Math.Sqrt((sumSq / Paths - mean * mean) * Paths / (Paths - 1.0));
            double expectedMean = Math.Log(Market.Spot) + (Market.Rate - 0.5 * Market.Volatility * Market.Volatility) * Market.Maturity;
            double expectedStdev = Market.Volatility * Math.Sqrt(Market.Maturity);
            Assert.InRange(mean, expectedMean - 0.04, expectedMean + 0.04);
            Assert.InRange(stdev, expectedStdev - 0.03, expectedStdev + 0.03);
        }

        private static double[] AlignedLogSpot(ExecutorBackend backend)
        {
            return Workloads.BlackScholesLogSpot(
                backend, (p, t) => new ArrayDoubleSpot(p, t), (p, t) => new ArrayBrownian(p, t), Paths, Steps);
        }

        [CudaBackendFact]
        public void Cuda_LogSpot_MatchesTheManagedReference()
        {
            // The Philox words are bit-identical on CUDA, but Box-Muller's log/cos/sin and the final exp come from
            // the device math library, which may differ from the host's by an ulp or two: the tolerance is
            // absolute 1e-11 on a log-spot of order 5 (about 10^4 ulp of headroom for 12 accumulated steps).
            double[] reference = AlignedLogSpot(ExecutorBackends.Reference);
            double[] cuda = AlignedLogSpot(ExecutorBackends.Cuda);
            Assert.Equal(reference.Length, cuda.Length);
            for (int k = 0; k < reference.Length; ++k)
                Assert.InRange(cuda[k], reference[k] - 1e-11, reference[k] + 1e-11);
        }
    }
}
