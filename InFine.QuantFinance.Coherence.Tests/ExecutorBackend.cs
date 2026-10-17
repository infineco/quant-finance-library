using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using InFine.QuantFinance.Scheduling;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>
    /// One way of running a Monte Carlo workload, as seen by the coherence tests.
    /// </summary>
    /// <remarks>
    /// A backend does two things, which are the two ways the library splits work:
    /// <list type="bullet">
    /// <item><see cref="Simulate"/> runs an <see cref="ISimulatable"/> over the path range [simFrom, simTo),
    /// the way <c>SimulatableScheduler</c> does on a device: the range is cut in contiguous chunks and each chunk is
    /// handed to <c>ISimulatable.Simulate(from, to)</c>.</item>
    /// <item><see cref="CreateExecutor"/> gives the <see cref="IParallelExecutor"/> used by kernels that loop with
    /// <c>executor.For</c> (e.g. Longstaff-Schwartz).</item>
    /// </list>
    /// Every simulatable of the library writes only the cells of the paths it is given and every random draw is a pure
    /// function of (seed, path, stream, index), so the result of a workload must not depend on the backend. That is
    /// what the coherence tests check.
    /// <para>
    /// Backends are addressed by <see cref="Name"/> in xUnit theories (strings serialize, so each backend is its own
    /// test case): see <see cref="ExecutorBackends"/>.
    /// </para>
    /// </remarks>
    public abstract class ExecutorBackend
    {
        protected ExecutorBackend(string name)
        {
            Name = name;
        }

        /// <summary>Stable identifier, used as the xUnit theory argument.</summary>
        public string Name { get; }

        /// <summary>Null when the backend can run here, otherwise why it cannot (used to skip tests).</summary>
        public virtual string UnavailableReason => null;

        /// <summary>Runs <paramref name="simulatable"/> over the paths [simFrom, simTo).</summary>
        public abstract void Simulate(ISimulatable simulatable, int simFrom, int simTo);

        /// <summary>The <see cref="IParallelExecutor"/> this backend corresponds to.</summary>
        public abstract IParallelExecutor CreateExecutor();

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>Contiguous chunking of a simulation range, as <c>SimulatableScheduler.SimulateSingle</c> does it.</summary>
    public static class Chunking
    {
        /// <summary>
        /// Bounds of chunk <paramref name="chunk"/> out of <paramref name="chunkCount"/> for the range [simFrom, simTo):
        /// start = N * chunk / chunkCount, stop = N * (chunk + 1) / chunkCount (offset by simFrom), with N = simTo - simFrom.
        /// This is the exact formula of <c>SimulatableScheduler</c> with blockIdx.x = chunk and gridDim.x = chunkCount,
        /// evaluated in 64 bits so that it cannot overflow. The chunks tile the range with no gap and no overlap.
        /// </summary>
        public static void Bounds(int simFrom, int simTo, int chunkCount, int chunk, out int start, out int stop)
        {
            long n = simTo - simFrom;
            start = (int)((n * chunk) / chunkCount) + simFrom;
            stop = (int)((n * (chunk + 1)) / chunkCount) + simFrom;
        }
    }

    /// <summary>Managed, single thread: one call <c>Simulate(simFrom, simTo)</c>; <see cref="SequentialExecutor"/>.</summary>
    public sealed class SequentialBackend : ExecutorBackend
    {
        public SequentialBackend() : base("managed-sequential")
        {
        }

        public override void Simulate(ISimulatable simulatable, int simFrom, int simTo)
        {
            simulatable.Simulate(simFrom, simTo);
        }

        public override IParallelExecutor CreateExecutor()
        {
            return new SequentialExecutor();
        }
    }

    /// <summary>
    /// Managed run of the real <c>SimulatableScheduler.SimulateSingle</c> entry point. On the managed runtime
    /// blockIdx.x = 0 and gridDim.x = 1, so this is one chunk; it proves the entry point is a pass-through and
    /// pairs with <see cref="SingleBlockParallelExecutor"/>. Needs the Hybridizer runtime to load.
    /// </summary>
    public sealed class SchedulerSingleBlockBackend : ExecutorBackend
    {
        public SchedulerSingleBlockBackend() : base("managed-scheduler-single-block")
        {
        }

        public override void Simulate(ISimulatable simulatable, int simFrom, int simTo)
        {
            SimulatableScheduler.SimulateSingle(simFrom, simTo, simulatable);
        }

        public override IParallelExecutor CreateExecutor()
        {
            return new SingleBlockParallelExecutor();
        }
    }

    /// <summary>
    /// Managed, real concurrency: the range is cut in <c>chunkCount</c> contiguous chunks (see <see cref="Chunking"/>)
    /// that run concurrently on the thread pool, one thread per chunk, like the blocks of a grid. Pairs with
    /// <see cref="MultiBlockParallelExecutor"/> (<c>Parallel.For</c>).
    /// </summary>
    public sealed class ChunkedParallelBackend : ExecutorBackend
    {
        private readonly int _chunkCount;

        public ChunkedParallelBackend(int chunkCount) : base("managed-parallel-" + chunkCount)
        {
            if (chunkCount < 1) throw new ArgumentOutOfRangeException(nameof(chunkCount));
            _chunkCount = chunkCount;
        }

        public override void Simulate(ISimulatable simulatable, int simFrom, int simTo)
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = _chunkCount };
            Parallel.For(0, _chunkCount, options, chunk =>
            {
                Chunking.Bounds(simFrom, simTo, _chunkCount, chunk, out int start, out int stop);
                if (stop > start)
                    simulatable.Simulate(start, stop);
            });
        }

        public override IParallelExecutor CreateExecutor()
        {
            return new MultiBlockParallelExecutor();
        }
    }

    /// <summary>
    /// Managed, sequential, but the chunks run in decreasing order: catches any hidden dependency of a path on the
    /// paths simulated before it (shared scratch state, running accumulators...).
    /// </summary>
    public sealed class ChunkedReverseBackend : ExecutorBackend
    {
        private readonly int _chunkCount;

        public ChunkedReverseBackend(int chunkCount) : base("managed-reverse-chunks-" + chunkCount)
        {
            if (chunkCount < 1) throw new ArgumentOutOfRangeException(nameof(chunkCount));
            _chunkCount = chunkCount;
        }

        public override void Simulate(ISimulatable simulatable, int simFrom, int simTo)
        {
            for (int chunk = _chunkCount - 1; chunk >= 0; --chunk)
            {
                Chunking.Bounds(simFrom, simTo, _chunkCount, chunk, out int start, out int stop);
                if (stop > start)
                    simulatable.Simulate(start, stop);
            }
        }

        public override IParallelExecutor CreateExecutor()
        {
            return new SequentialExecutor();
        }
    }

    /// <summary>
    /// CUDA backend: the extension point for a HybRunner-based launch of the hybridized library.
    /// </summary>
    /// <remarks>
    /// Nothing here is executed on a host without an NVIDIA device and a hybridized binary: the backend reports
    /// itself unavailable and the CUDA tests are skipped. To plug a real launch in, set <see cref="Launcher"/> to
    /// a function that runs <c>simulatable.Simulate(simFrom, simTo)</c> on the device (typically a HybRunner wrapper
    /// of <c>SimulatableScheduler.SimulateSingle</c> over a hybridized assembly) and make the hybridized binary
    /// available next to the test assembly under <see cref="BinaryName"/>. <see cref="CudaLauncher"/> wires it when the
    /// satellite built by <c>InFine.QuantFinance.Cuda -p:HybridizeCuda=true</c> and a device are present.
    /// </remarks>
    public sealed class CudaBackend : ExecutorBackend
    {
        /// <summary>File name HybRunner is expected to find the hybridized CUDA binary under.</summary>
        public const string BinaryName = NativeEnvironment.CudaSatelliteName;

        public CudaBackend() : base("cuda")
        {
        }

        /// <summary>Device launch of a simulatable over [simFrom, simTo); null until wired by the first GPU run.</summary>
        public static Action<ISimulatable, int, int> Launcher { get; set; }

        public override string UnavailableReason
        {
            get
            {
                string reason = NativeEnvironment.MissingRequirements(NativeRequirement.CudaDevice, BinaryName);
                if (reason == null && Launcher == null)
                    reason = "the CUDA backend launcher is not wired (set CudaBackend.Launcher)";
                return reason;
            }
        }

        public override void Simulate(ISimulatable simulatable, int simFrom, int simTo)
        {
            if (Launcher == null)
                throw new NotSupportedException(UnavailableReason ?? "the CUDA backend launcher is not wired");
            Launcher(simulatable, simFrom, simTo);
        }

        public override IParallelExecutor CreateExecutor()
        {
            throw new NotSupportedException("The CUDA backend has no host-side IParallelExecutor: executors run inside kernels.");
        }
    }

    /// <summary>
    /// Registry of the backends, with xUnit <c>MemberData</c> sources.
    /// </summary>
    public static class ExecutorBackends
    {
        /// <summary>The reference every other backend is compared to.</summary>
        public static ExecutorBackend Reference => new SequentialBackend();

        /// <summary>All backends that run on the managed runtime, in a stable order, reference first.</summary>
        public static IReadOnlyList<ExecutorBackend> Managed
        {
            get
            {
                return new ExecutorBackend[]
                {
                    new SequentialBackend(),
                    new SchedulerSingleBlockBackend(),
                    new ChunkedParallelBackend(3),
                    new ChunkedParallelBackend(8),
                    new ChunkedReverseBackend(5),
                };
            }
        }

        /// <summary>The CUDA backend (unavailable unless <see cref="CudaBackend.Launcher"/> is wired on a GPU host).</summary>
        public static ExecutorBackend Cuda => new CudaBackend();

        /// <summary>Theory data: the name of each managed backend, one test case per backend.</summary>
        public static IEnumerable<object[]> ManagedNames()
        {
            foreach (ExecutorBackend backend in Managed)
                yield return new object[] { backend.Name };
        }

        /// <summary>Resolves a backend from its <see cref="ExecutorBackend.Name"/>.</summary>
        public static ExecutorBackend Get(string name)
        {
            foreach (ExecutorBackend backend in Managed)
            {
                if (backend.Name == name) return backend;
            }
            if (name == Cuda.Name) return Cuda;
            throw new ArgumentException("Unknown executor backend: " + name, nameof(name));
        }
    }

    /// <summary>
    /// A <see cref="FactAttribute"/> that runs only when the CUDA backend can actually run
    /// (device, hybridized binary and wired <see cref="CudaBackend.Launcher"/>).
    /// </summary>
    public sealed class CudaBackendFactAttribute : FactAttribute
    {
        public CudaBackendFactAttribute()
        {
            Skip = ExecutorBackends.Cuda.UnavailableReason;
        }
    }
}
