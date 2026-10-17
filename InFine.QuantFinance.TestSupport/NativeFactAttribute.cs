using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hybridizer.Runtime.CUDAImports;
using Xunit;

namespace InFine.QuantFinance.TestSupport
{
    /// <summary>
    /// Native resources a test may need beyond the managed runtime.
    /// </summary>
    [Flags]
    public enum NativeRequirement
    {
        None = 0,

        /// <summary>A CUDA driver/runtime with at least one visible device.</summary>
        CudaDevice = 1,

        /// <summary>Intel MKL runtime (mkl_rt) loadable by name.</summary>
        Mkl = 2,

        /// <summary>
        /// The managed Hybridizer.Runtime.CUDAImports assembly loads in this process.
        /// Implied by <see cref="CudaDevice"/> and by any Hybridizer-generated binary.
        /// </summary>
        HybridizerRuntime = 4,
    }

    /// <summary>
    /// Probes the host once for the native resources tests depend on.
    /// </summary>
    public static class NativeEnvironment
    {
        /// <summary>
        /// File name of the CUDA satellite of the library, produced by
        /// <c>dotnet build InFine.QuantFinance.Cuda -p:HybridizeCuda=true</c> and copied next to the test assemblies.
        /// </summary>
        public const string CudaSatelliteName = "InFine.QuantFinance_CUDA.dll";

        /// <summary>
        /// Full path of the CUDA satellite next to the test assemblies. Pass this, not the bare name, to
        /// <c>HybRunner.Cuda</c>: on Linux dlopen does not search the current or application directory for a bare name.
        /// </summary>
        public static string CudaSatellitePath => Path.Combine(AppContext.BaseDirectory, CudaSatelliteName);

        private static readonly Lazy<string> s_hybridizerRuntimeError = new Lazy<string>(ProbeHybridizerRuntime);
        private static readonly Lazy<bool> s_cudaDevice = new Lazy<bool>(ProbeCudaDevice);
        private static readonly Lazy<bool> s_mkl = new Lazy<bool>(ProbeMkl);

        public static bool HasHybridizerRuntime => s_hybridizerRuntimeError.Value == null;

        public static bool HasCudaDevice => s_cudaDevice.Value;

        public static bool HasMkl => s_mkl.Value;

        /// <summary>
        /// True when a Hybridizer-generated native binary (CUDA, AVX or OMP flavor) is present
        /// where <see cref="HybRunner"/> will look for it.
        /// </summary>
        public static bool HasHybridizedBinary(string fileName)
        {
            return File.Exists(fileName) || File.Exists(Path.Combine(AppContext.BaseDirectory, fileName));
        }

        /// <summary>
        /// Returns a skip reason naming every missing requirement, or null when all are met.
        /// </summary>
        public static string MissingRequirements(NativeRequirement requirements, params string[] hybridizedBinaries)
        {
            hybridizedBinaries = hybridizedBinaries ?? Array.Empty<string>();
            if ((requirements & NativeRequirement.CudaDevice) != 0 || hybridizedBinaries.Length > 0)
                requirements |= NativeRequirement.HybridizerRuntime;

            string reason = null;
            if ((requirements & NativeRequirement.HybridizerRuntime) != 0 && !HasHybridizerRuntime)
                reason = Append(reason, s_hybridizerRuntimeError.Value);
            if ((requirements & NativeRequirement.CudaDevice) != 0 && !HasCudaDevice)
                reason = Append(reason, "requires a CUDA device");
            if ((requirements & NativeRequirement.Mkl) != 0 && !HasMkl)
                reason = Append(reason, "requires Intel MKL (mkl_rt)");
            foreach (string binary in hybridizedBinaries)
            {
                if (!HasHybridizedBinary(binary))
                    reason = Append(reason, $"requires the Hybridizer-generated native binary {binary}");
            }
            return reason;
        }

        private static string Append(string reason, string requirement)
        {
            return reason == null ? requirement : reason + "; " + requirement;
        }

        private static string ProbeHybridizerRuntime()
        {
            try
            {
                Assembly.Load("Hybridizer.Runtime.CUDAImports");
                return null;
            }
            catch (Exception e)
            {
                // Hybridizer.Runtime.CUDAImports 3.7.1 is stamped AMD64: CoreCLR refuses it in an
                // arm64 process (e.g. Apple Silicon) even though it is IL-only.
                return $"requires Hybridizer.Runtime.CUDAImports, which does not load in this "
                    + $"{RuntimeInformation.ProcessArchitecture} process ({e.GetType().Name})";
            }
        }

        private static bool ProbeCudaDevice()
        {
            if (!HasHybridizerRuntime)
                return false;
            try
            {
                return CudaDeviceCount() > 0;
            }
            catch (Exception)
            {
                // No driver/runtime library (DllNotFoundException), unsupported version
                // (TypeInitializationException), ...: no usable device either way.
                return false;
            }
        }

        // Kept out of line so a load failure of the Hybridizer assembly surfaces inside the
        // caller's try block rather than when the caller is JIT-compiled.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int CudaDeviceCount()
        {
            return cuda.GetDeviceCount(out int count) == cudaError_t.cudaSuccess ? count : 0;
        }

        private static bool ProbeMkl()
        {
            if (!NativeLibrary.TryLoad("mkl_rt", typeof(NativeEnvironment).Assembly, null, out IntPtr handle))
                return false;
            NativeLibrary.Free(handle);
            return true;
        }
    }

    /// <summary>
    /// A <see cref="FactAttribute"/> that skips itself when the host lacks the native resources
    /// the test needs (GPU, MKL, Hybridizer-generated binaries) instead of failing.
    /// </summary>
    public class NativeFactAttribute : FactAttribute
    {
        public NativeFactAttribute(NativeRequirement requirements, params string[] hybridizedBinaries)
        {
            Skip = NativeEnvironment.MissingRequirements(requirements, hybridizedBinaries);
        }
    }

    /// <summary>
    /// A CPU fact whose code under test needs the managed Hybridizer runtime to load.
    /// </summary>
    public sealed class HybridizerFactAttribute : NativeFactAttribute
    {
        public HybridizerFactAttribute()
            : base(NativeRequirement.HybridizerRuntime)
        {
        }
    }

    /// <summary>
    /// A fact that runs only when a CUDA device is available.
    /// </summary>
    public sealed class GpuFactAttribute : NativeFactAttribute
    {
        public GpuFactAttribute(params string[] hybridizedBinaries)
            : base(NativeRequirement.CudaDevice, hybridizedBinaries)
        {
        }
    }

    /// <summary>
    /// A <see cref="TheoryAttribute"/> that skips itself when the host lacks the native resources
    /// the test needs. The whole theory is skipped, not individual data rows.
    /// </summary>
    public class NativeTheoryAttribute : TheoryAttribute
    {
        public NativeTheoryAttribute(NativeRequirement requirements, params string[] hybridizedBinaries)
        {
            Skip = NativeEnvironment.MissingRequirements(requirements, hybridizedBinaries);
        }
    }

    /// <summary>
    /// A CPU theory whose code under test needs the managed Hybridizer runtime to load.
    /// </summary>
    public sealed class HybridizerTheoryAttribute : NativeTheoryAttribute
    {
        public HybridizerTheoryAttribute()
            : base(NativeRequirement.HybridizerRuntime)
        {
        }
    }

    /// <summary>
    /// A theory that runs only when a CUDA device is available.
    /// </summary>
    public sealed class GpuTheoryAttribute : NativeTheoryAttribute
    {
        public GpuTheoryAttribute(params string[] hybridizedBinaries)
            : base(NativeRequirement.CudaDevice, hybridizedBinaries)
        {
        }
    }
}
