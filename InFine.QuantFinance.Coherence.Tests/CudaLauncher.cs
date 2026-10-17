using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Scheduling;
using InFine.QuantFinance.TestSupport;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>
    /// Wires <see cref="CudaBackend.Launcher"/> to a HybRunner launch of <see cref="SimulatableScheduler.SimulateSingle"/>
    /// in the hybridized satellite, when a CUDA device and the satellite are present.
    /// </summary>
    /// <remarks>
    /// The module initializer runs before xUnit evaluates the skip reasons of <see cref="CudaBackendFactAttribute"/>, so
    /// the CUDA tests run as soon as <c>dotnet build InFine.QuantFinance.Cuda -p:HybridizeCuda=true</c> has produced the
    /// satellite. It touches Hybridizer types only after <see cref="NativeEnvironment"/> has confirmed that the runtime
    /// loads (it does not on arm64 hosts), and <see cref="Launch"/> is only JIT-compiled when first called.
    /// <para>
    /// Only the library's simulatables exist on the device: a type of this test assembly (the Philox Brownian fills,
    /// which allocate and are the host reference generator anyway) has no device implementation and would hit a "pure
    /// virtual call" in the satellite. Such simulatables run on the host; every other stage goes to the GPU, and
    /// <see cref="DeviceLaunches"/> lets a test check that it did.
    /// </para>
    /// <para>
    /// Before each launch the host copy of every resident storage reachable from the simulatable is brought up to date
    /// (<see cref="RefreshStaleHostCopies"/>). Hybridizer.Runtime.CUDAImports 3.7.1 marshals an <c>alignedstorage_*</c> by
    /// uploading its host buffer, even when its status says the device holds the newer data (HostNeedsRefresh, after a
    /// kernel wrote it): chaining two launches on the same storage - a diffusion, then a payoff reading its paths -
    /// silently replaces the paths with the stale host copy (observed: 526 of 1009 European premiums wrong). A host read
    /// between the launches, which is what the refresh does, avoids it at the cost of one device-to-host copy.
    /// </para>
    /// <para>
    /// The same refresh runs after each launch. The lazy host synchronization of <c>DoubleResidentArray</c>
    /// (<c>CheckRefreshHost</c>, unsynchronized, the race <see cref="Allocation"/> works around for host allocation) also
    /// applies to the device-to-host copy: reading a storage the device just wrote from several threads at once fails
    /// with CUDA error 700 on every try (20 of 20, 200003 paths, 24 threads), whether the storage was primed or not.
    /// </para>
    /// <para>
    /// Launches are serialized: xUnit runs test classes in parallel and nothing documents HybRunner as thread-safe.
    /// </para>
    /// </remarks>
    internal static class CudaLauncher
    {
        /// <summary>Blocks of the launch: SimulateSingle gives each block a contiguous chunk of paths.</summary>
        public const int GridDimX = 32;

        /// <summary>Threads per block: the simulatables stride over their chunk with VectorUnit.Count = blockDim.x.</summary>
        public const int BlockDimX = 128;

        /// <summary>Assemblies compiled into the satellite (InFine.QuantFinance.Cuda's HybridizedAssembly items).</summary>
        private static readonly HashSet<string> s_hybridized = new HashSet<string>(StringComparer.Ordinal)
        {
            "InFine.QuantFinance.Scheduling", "InFine.QuantFinance.Numerics", "InFine.QuantFinance.Arithmetic",
            "InFine.QuantFinance.ClosedForm", "InFine.QuantFinance.Random", "InFine.QuantFinance.Models",
            "InFine.QuantFinance.Payoffs", "InFine.QuantFinance.Risk", "InFine.QuantFinance.Backtesting",
            "InFine.QuantFinance.LongstaffSchwartz",
        };

        private static readonly object s_gate = new object();
        private static HybRunner s_runner;
        // Per thread: a launch runs on the thread of the test that asked for it, and xUnit runs test classes in parallel.
        [ThreadStatic]
        private static int s_deviceLaunches;

        /// <summary>Full path of the satellite: HybRunner resolves a bare file name against the current directory.</summary>
        public static string SatellitePath => Path.Combine(AppContext.BaseDirectory, CudaBackend.BinaryName);

        /// <summary>Serializes the tests that drive HybRunner themselves with the launches of this class.</summary>
        public static object Gate => s_gate;

        /// <summary>Number of SimulateSingle launches the calling thread has made on the device.</summary>
        public static int DeviceLaunches => s_deviceLaunches;

        [ModuleInitializer]
        internal static void Wire()
        {
            if (NativeEnvironment.MissingRequirements(NativeRequirement.CudaDevice, CudaBackend.BinaryName) == null)
                CudaBackend.Launcher = Launch;
        }

        /// <summary>True when the type and all its generic arguments come from hybridized assemblies.</summary>
        public static bool IsHybridized(Type type)
        {
            if (!s_hybridized.Contains(type.Assembly.GetName().Name))
                return false;
            foreach (Type argument in type.GetGenericArguments())
            {
                if (!IsHybridized(argument))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Calls <see cref="IResidentArray.RefreshHost"/> on every resident array reachable from <paramref name="root"/>
        /// through fields of hybridized types whose host copy is stale.
        /// </summary>
        public static int RefreshStaleHostCopies(object root)
        {
            int refreshed = 0;
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<object>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                object current = pending.Pop();
                if (current == null || (!current.GetType().IsValueType && !visited.Add(current)))
                    continue;
                if (current is IResidentArray resident)
                {
                    if (resident.Status == ResidentArrayStatus.HostNeedsRefresh)
                    {
                        resident.RefreshHost();
                        refreshed++;
                    }
                    continue;
                }
                if (current is Array array)
                {
                    if (!array.GetType().GetElementType().IsPrimitive)
                    {
                        foreach (object element in array)
                            pending.Push(element);
                    }
                    continue;
                }
                Type type = current.GetType();
                bool storage = type.Namespace == typeof(alignedstorage_double).Namespace && type.Name.StartsWith("alignedstorage", StringComparison.Ordinal);
                if (!storage && !IsHybridized(type))
                    continue;
                for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
                {
                    foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!field.FieldType.IsPrimitive && !field.FieldType.IsPointer)
                            pending.Push(field.GetValue(current));
                    }
                }
            }
            return refreshed;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Launch(ISimulatable simulatable, int simFrom, int simTo)
        {
            if (!IsHybridized(simulatable.GetType()))
            {
                simulatable.Simulate(simFrom, simTo);
                return;
            }

            lock (s_gate)
            {
                RefreshStaleHostCopies(simulatable);
                s_runner ??= HybRunner.Cuda(SatellitePath).SetDistrib(GridDimX, BlockDimX);
                dynamic scheduler = s_runner.Wrap(new SimulatableScheduler());
                object status = scheduler.SimulateSingle(simFrom, simTo, simulatable);
                cudaError_t sync = cuda.DeviceSynchronize();
                if ((status is int code && code != 0) || sync != cudaError_t.cudaSuccess)
                    throw new InvalidOperationException($"CUDA launch of SimulateSingle failed: wrapper status {status}, synchronize {sync}");
                s_deviceLaunches++;
                // Bring the results to the host now, on this thread: the first host access to a storage the device
                // wrote copies it back without synchronization, and when several threads make it at once (a managed
                // parallel stage after a device stage) the copies fail with cudaErrorIllegalAddress (700), which leaves
                // the CUDA context unusable.
                RefreshStaleHostCopies(simulatable);
            }
        }
    }
}
