using System;
using System.Collections.Generic;
using InFine.QuantFinance.Random;
using InFine.QuantFinance.Random.DotNet;
using InFine.QuantFinance.Scheduling;
using Xunit;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>
    /// The harness itself, and the Philox draws it relies on. Nothing here touches a Hybridizer type, so these
    /// tests run on every host (arm64 included).
    /// </summary>
    public class Test_ExecutorBackends
    {
        private sealed class RangeRecorder : ISimulatable
        {
            private readonly object _lock = new object();
            public readonly List<(int From, int To)> Ranges = new List<(int, int)>();

            public void Simulate(int simFrom, int simTo)
            {
                lock (_lock)
                    Ranges.Add((simFrom, simTo));
            }
        }

        private static IEnumerable<ExecutorBackend> HybridizerFreeBackends()
        {
            yield return new SequentialBackend();
            yield return new ChunkedParallelBackend(1);
            yield return new ChunkedParallelBackend(3);
            yield return new ChunkedParallelBackend(8);
            yield return new ChunkedParallelBackend(64);
            yield return new ChunkedReverseBackend(5);
        }

        [Fact]
        public void Backends_TileTheRangeWithNoGapAndNoOverlap()
        {
            var ranges = new (int From, int To)[] { (0, 1009), (0, 1), (0, 0), (7, 8), (10, 13), (500, 1500), (0, 5) };
            foreach (ExecutorBackend backend in HybridizerFreeBackends())
            {
                foreach (var (from, to) in ranges)
                {
                    var recorder = new RangeRecorder();
                    backend.Simulate(recorder, from, to);

                    var covered = new int[Math.Max(to - from, 0)];
                    foreach (var (start, stop) in recorder.Ranges)
                    {
                        Assert.True(start < stop || (from == to && start == stop), $"{backend.Name} [{from},{to}): empty chunk handed to Simulate");
                        Assert.True(start >= from && stop <= to, $"{backend.Name} [{from},{to}): chunk [{start},{stop}) leaves the range");
                        for (int p = start; p < stop; ++p)
                            covered[p - from]++;
                    }
                    Assert.All(covered, count => Assert.Equal(1, count));
                }
            }
        }

        [Fact]
        public void Chunking_MatchesTheSimulatableSchedulerFormula()
        {
            // SimulatableScheduler.SimulateSingle: start = (N * blockIdx) / gridDim, stop = (N * (blockIdx + 1)) / gridDim.
            const int simFrom = 3, simTo = 1012, gridDim = 7;
            int n = simTo - simFrom;
            for (int blockIdx = 0; blockIdx < gridDim; ++blockIdx)
            {
                Chunking.Bounds(simFrom, simTo, gridDim, blockIdx, out int start, out int stop);
                Assert.Equal((n * blockIdx) / gridDim + simFrom, start);
                Assert.Equal((n * (blockIdx + 1)) / gridDim + simFrom, stop);
            }
        }

        [Fact]
        public void PhiloxFill_IsBitIdenticalWhateverTheChunking()
        {
            const int paths = Market.Paths, steps = Market.BlackScholesSteps;
            var reference = new double[paths * steps];
            new SequentialBackend().Simulate(new PhiloxPlainFill(Market.Seed, Market.SpotStream, steps, reference), 0, paths);

            foreach (ExecutorBackend backend in HybridizerFreeBackends())
            {
                var filled = new double[paths * steps];
                backend.Simulate(new PhiloxPlainFill(Market.Seed, Market.SpotStream, steps, filled), 0, paths);
                Matrices.AssertBitIdentical(reference, filled, backend.Name);
            }
        }

        [Fact]
        public void PhiloxFill_PathDrawsAreThePathSubStreamAlone()
        {
            const int paths = Market.Paths, steps = Market.BlackScholesSteps;
            var filled = new double[paths * steps];
            new ChunkedParallelBackend(8).Simulate(new PhiloxPlainFill(Market.Seed, Market.SpotStream, steps, filled), 0, paths);

            var expected = new double[steps];
            foreach (int path in new[] { 0, 1, 2, 500, paths - 1 })
            {
                // the independent, public way: a generator on sub-stream (seed, path, stream), nothing else
                new PhiloxGenerator(Market.Seed, (uint)path, Market.SpotStream)
                    .GenerateGaussians(GaussianTransformEnum.BoxMuller, 0.0, 1.0, steps, expected);
                var actual = new double[steps];
                Array.Copy(filled, path * steps, actual, 0, steps);
                Matrices.AssertBitIdentical(expected, actual, "path " + path);
            }
        }

        [Fact]
        public void PhiloxFill_StreamsAndPathsAreDistinct()
        {
            const int paths = 16, steps = 12;
            var stream0 = new double[paths * steps];
            var stream1 = new double[paths * steps];
            new SequentialBackend().Simulate(new PhiloxPlainFill(Market.Seed, 0, steps, stream0), 0, paths);
            new SequentialBackend().Simulate(new PhiloxPlainFill(Market.Seed, 1, steps, stream1), 0, paths);
            for (int p = 0; p < paths; ++p)
            {
                for (int q = p + 1; q < paths; ++q)
                    Assert.NotEqual(stream0[p * steps], stream0[q * steps]);
                Assert.NotEqual(stream0[p * steps], stream1[p * steps]);
            }
        }

        [Fact]
        public void PhiloxFill_DrawsAreStandardNormal()
        {
            // Sanity that the harness feeds real N(0,1) draws, not zeros: 12108 draws, seeded, so deterministic.
            // The sample mean has standard error 0.009 and the sample variance 0.013: 5 sigma tolerances.
            const int paths = Market.Paths, steps = Market.BlackScholesSteps;
            var filled = new double[paths * steps];
            new SequentialBackend().Simulate(new PhiloxPlainFill(Market.Seed, Market.SpotStream, steps, filled), 0, paths);
            double sum = 0.0, sumSq = 0.0;
            foreach (double z in filled)
            {
                sum += z;
                sumSq += z * z;
            }
            double mean = sum / filled.Length;
            double variance = sumSq / filled.Length - mean * mean;
            Assert.InRange(mean, -0.045, 0.045);
            Assert.InRange(variance, 0.93, 1.07);
        }

        [Fact]
        public void CudaBackend_IsUnavailableUntilWired()
        {
            ExecutorBackend cuda = ExecutorBackends.Cuda;
            if (CudaBackend.Launcher == null)
                Assert.NotNull(cuda.UnavailableReason);
            Assert.Equal("cuda", cuda.Name);
            Assert.Same(typeof(CudaBackend), ExecutorBackends.Get("cuda").GetType());
        }
    }
}
