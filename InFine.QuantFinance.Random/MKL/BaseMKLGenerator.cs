using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace InFine.QuantFinance.Random.MKL
{
    // from : mkl_vsl_defines
    public enum mkl_vsl_defines
    {
        VSL_RNG_METHOD_UNIFORMBITS_STD = 0,
        VSL_RNG_METHOD_UNIFORMBITS32_STD = 0,
        VSL_RNG_METHOD_UNIFORMBITS64_STD = 0,

        VSL_RNG_METHOD_UNIFORM_STD = 0,

        VSL_BRNG_MRG32K3A = 3 << 20,
        VSL_BRNG_MT19937 = 8 << 20,
        VSL_BRNG_SFMT19937 = 13 << 20,

        /// <summary>
        /// BOXMULLER  generates normally distributed random number x thru the pair of
        ///        uniformly distributed numbers u1 and u2 according to the formula:
        ///
        ///           x=sqrt(-ln(u1))*sin(2*Pi*u2)
        ///
        /// </summary>
        VSL_RNG_METHOD_GAUSSIAN_BOXMULLER = 0,

        /// <summary>
        /// BOXMULLER2 generates pair of normally distributed random numbers x1 and x2
        ///            thru the pair of uniformly dustributed numbers u1 and u2
        ///            according to the formula
        ///
        ///               x1=sqrt(-ln(u1))*sin(2*Pi*u2)
        ///               x2=sqrt(-ln(u1))*cos(2*Pi*u2)
        ///
        ///            NOTE: implementation correctly works with odd vector lengths
        ///
        /// </summary>
        VSL_RNG_METHOD_GAUSSIAN_BOXMULLER2 = 1,

        /// <summary>
        ///  ICDF       inverse cumulative distribution function method
        /// </summary>
        VSL_RNG_METHOD_GAUSSIAN_ICDF = 2,
    }

    /// <summary>
    /// C:\Program Files (x86)\Intel\Composer XE 2015\mkl\include
    /// mkl_vsl_defines
    /// mkl_vsl_functions
    /// mkl_vsl_types
    /// </summary>
    public abstract class BaseMKLGenerator : IRandomNumberGenerator
    {
        static Dictionary<GaussianTransformEnum, int> S_GAUSSIAN_TRANSFORMS ;
        static Dictionary<UniformTransformEnum, int> S_UNIFORM_TRANSFORMS;
        public static bool IsRunningOnMono()
        {
            return Type.GetType("Mono.Runtime") != null || Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX;
        }


        // .NET probes the platform-specific name (mkl_rt.dll, libmkl_rt.so, libmkl_rt.dylib).
        const string MKL_DLL = "mkl_rt";

        static BaseMKLGenerator()
        {
            S_GAUSSIAN_TRANSFORMS = new Dictionary<GaussianTransformEnum, int>();
            S_GAUSSIAN_TRANSFORMS.Add(GaussianTransformEnum.BoxMuller, (int)mkl_vsl_defines.VSL_RNG_METHOD_GAUSSIAN_BOXMULLER);
            S_GAUSSIAN_TRANSFORMS.Add(GaussianTransformEnum.BoxMuller2, (int)mkl_vsl_defines.VSL_RNG_METHOD_GAUSSIAN_BOXMULLER2);
            S_GAUSSIAN_TRANSFORMS.Add(GaussianTransformEnum.InverseCumulativeDistributionFunction, (int)mkl_vsl_defines.VSL_RNG_METHOD_GAUSSIAN_ICDF);

            S_UNIFORM_TRANSFORMS = new Dictionary<UniformTransformEnum, int>();
            S_UNIFORM_TRANSFORMS.Add(UniformTransformEnum.STANDARD, (int)mkl_vsl_defines.VSL_RNG_METHOD_UNIFORM_STD);
        }

        protected abstract IntPtr RNGStream { get; }

        // C:\Program Files (x86)\Intel\Composer XE 2015\redist\intel64\mkl
        [DllImport(MKL_DLL, CallingConvention=CallingConvention.Cdecl)]
        private static extern int vdRngUniform(int transformType, IntPtr stream, int count, double[] output, double a, double b) ;
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vdRngCauchy(int transformType, IntPtr stream, int count, double[] output, double a, double b);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vdRngGaussian(int transformType, IntPtr stream, int count, double[] output, double a, double b);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private unsafe static extern int vdRngGaussian(int transformType, IntPtr stream, int count, double* output, double a, double b);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vdRngGaussian(int transformType, IntPtr stream, int count, double[,] output, double a, double b);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vdRngExponential(int transformType, IntPtr stream, int count, double[] output, double a, double b);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vdRngLaplace(int transformType, IntPtr stream, int count, double[] output, double a, double b);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vdRngWeibull(int transformType, IntPtr stream, int count, double[] output, double a, double b, double c);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vdRngRayleigh(int transformType, IntPtr stream, int count, double[] output, double a, double b);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vdRngLognormal(int transformType, IntPtr stream, int count, double[] output, double a, double b, double c, double d);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vdRngGumbel(int transformType, IntPtr stream, int count, double[] output, double a, double b);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vdRngGamma(int transformType, IntPtr stream, int count, double[] output, double a, double b, double c);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vdRngBeta(int transformType, IntPtr stream, int count, double[] output, double a, double b, double c, double d);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int viRngUniformBits32(int transformType, IntPtr stream, int count, uint[] output);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int viRngUniformBits64(int transformType, IntPtr stream, int count, ulong[] output);


        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        protected static extern int vslNewStream(out IntPtr result, int streamType, int seed);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        protected static extern int vslNewStreamEx(out IntPtr result, int streamType, int count, int[] par);
        [DllImport(MKL_DLL, CallingConvention = CallingConvention.Cdecl)]
        protected static extern int vslDeleteStream(ref IntPtr result);
        
        public virtual void GenerateUniforms(UniformTransformEnum transform, double leftbound, double rightbound, int count, double[] target)
        {
            vdRngUniform(S_UNIFORM_TRANSFORMS[transform], RNGStream, count, target, leftbound, rightbound);
        }

        /// <summary>
        /// https://software.intel.com/en-us/articles/new-fast-basic-random-number-generator-sfmt19937-in-intel-mkl
        /// </summary>
        /// <param name="transform"></param>
        /// <param name="mean"></param>
        /// <param name="stddev"></param>
        /// <param name="count"></param>
        /// <param name="target"></param>
        public virtual void GenerateGaussians(GaussianTransformEnum transform, double mean, double stddev, int count, double[] target)
        {
            vdRngGaussian(S_GAUSSIAN_TRANSFORMS[transform], RNGStream, count, target, mean, stddev);
        }
        public unsafe virtual void GenerateGaussians(GaussianTransformEnum transform, double mean, double stddev, int count, double* target)
        {
            vdRngGaussian(S_GAUSSIAN_TRANSFORMS[transform], RNGStream, count, target, mean, stddev);
        }
        public virtual void GenerateGaussians(GaussianTransformEnum transform, double mean, double stddev, int count, double[,] target)
        {
            vdRngGaussian(S_GAUSSIAN_TRANSFORMS[transform], RNGStream, count, target, mean, stddev);
        }
    }

    public class MRG32K3A : BaseMKLGenerator, IDisposable
    {
        IntPtr _stream ;

        protected override IntPtr RNGStream
        {
            get { return _stream; }
        }

        public MRG32K3A(int seed)
        {
            vslNewStream(out _stream, (int)(mkl_vsl_defines.VSL_BRNG_MRG32K3A), seed);
        }

        public MRG32K3A(int[] seed)
        {
            vslNewStreamEx(out _stream, (int)(mkl_vsl_defines.VSL_BRNG_MRG32K3A), seed.Length > 6 ? 6 : seed.Length, seed);
        }

        #region IDisposable
        
        ~MRG32K3A()
        {
            Dispose(false);
        }

        protected void Dispose(bool disposing)
        { 
            if (disposing)
                GC.SuppressFinalize(this);
            vslDeleteStream(ref _stream);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion
    }

    public class MT19937 : BaseMKLGenerator, IDisposable
    {
        IntPtr _stream ;

        protected override IntPtr RNGStream
        {
            get { return _stream; }
        }

        public MT19937(int seed)
        {
            vslNewStream(out _stream, (int)(mkl_vsl_defines.VSL_BRNG_MT19937), seed);
        }

        #region IDisposable

        ~MT19937()
        {
            Dispose(false);
        }

        protected void Dispose(bool disposing)
        { 
            if (disposing)
                GC.SuppressFinalize(this);
            vslDeleteStream(ref _stream);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion
    }
}
