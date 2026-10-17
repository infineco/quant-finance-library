using InFine.QuantFinance.Random.CURAND;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.Random.Tests
{
    public class UnitTest_MRG32K3A
    {
        [NativeFact(NativeRequirement.Mkl)]
        public void TestMethod_MRG32K3A_MKL_GET_NAN()
        {
            int count = 16;
            double[] unif_data = new double[count];
            double[] gauss_data = new double[count];
            InFine.QuantFinance.Random.MKL.MRG32K3A mkl = new MKL.MRG32K3A(new int[] {1403580, 810728, 1, 527612, 1, 1370589 });
            mkl.GenerateUniforms(UniformTransformEnum.STANDARD, 0.0, 1.0, count, unif_data);
            InFine.QuantFinance.Random.MKL.MRG32K3A mkl2 = new MKL.MRG32K3A(new int[] { 1403580, 810728, 1, 527612, 1, 1370589 });
            mkl2.GenerateGaussians(GaussianTransformEnum.BoxMuller2, 0.0, 1.0, count, gauss_data);
            return;
        }

        [NativeFact(NativeRequirement.Mkl)]
        public void TestMethod_MRG32K3A_MKL()
        {
            int count = 128;

            double[] mkl_data = new double[count];
            InFine.QuantFinance.Random.MKL.MRG32K3A mkl = new MKL.MRG32K3A(new int[] { 1403580, 810728, 1, 527612, 1, 1370589 });
            mkl.GenerateGaussians(GaussianTransformEnum.BoxMuller2, 0.0, 1.0, count, mkl_data); // NOT ACCURATE !!!
            // mkl.GenerateUniforms(UniformTransformEnum.STANDARD, 0.0, 1.0, count, mkl_data);

            double[] dotnet_data = new double[count];
            DotNet.MRG32K3A dotnet = new DotNet.MRG32K3A(new int[] { 1403580, 810728, 1, 527612, 1, 1370589 });
            dotnet.GenerateGaussians(GaussianTransformEnum.BoxMuller2, 0.0, 1.0, count, dotnet_data);
            // dotnet.GenerateUniforms(UniformTransformEnum.STANDARD, 0.0, 1.0, count, dotnet_data);

            double[] delta = new double[count];
            for (int k = 0; k < 128; ++k)
            {
                delta[k] = mkl_data[k] - dotnet_data[k];
            }
            return;
        }

        [HybridizerFact]
        public void TestMethod_MRG32K3A_Advance()
        {
            DotNet.MRG32K3A reference = new DotNet.MRG32K3A(new int[] { 1403580, 810728, 1, 527612, 1, 1370589 });
            DotNet.MRG32K3A dotnet = new DotNet.MRG32K3A(new int[] { 1403580, 810728, 1, 527612, 1, 1370589 });

            double[] reftarget = new double[10];
            reference.GenerateUniforms(UniformTransformEnum.STANDARD, -100, 100, 10, reftarget);
            reference.GenerateUniforms(UniformTransformEnum.STANDARD, -100, 100, 10, reftarget);

            double[] testtarget = new double[10];
            dotnet.skipahead(10);
            dotnet.GenerateUniforms(UniformTransformEnum.STANDARD, -100, 100, 10, testtarget);

            for(int i = 0; i<reftarget.Length; ++i)
                Assert.Equal(reftarget[i], testtarget[i]);
        }

        [GpuFact(NativeEnvironment.CudaSatelliteName)]
        public void TestMethod_MRG32K3A_VsCuda()
        {
            CurandSamples o = new CurandSamples();
            ulong skipSequence = 87239;
            ulong skipSubsequence = 2938;
            ulong skip = 9879;
            ulong seed = 1928379729UL;

            var wrapped = HybRunner.Cuda(o, NativeEnvironment.CudaSatellitePath);
            var count = 10;
            var cudaRes = new double[count];
            wrapped.GenerateUniform(seed, 0, 0, cudaRes, count, skipSequence, skipSubsequence, skip);

            DotNet.MRG32K3A dotnet = new DotNet.MRG32K3A(seed);
            var dotnetRes = new double[count];
            dotnet.skipahead_sequence(skipSequence);
            dotnet.skipahead_subsequence(skipSubsequence);
            dotnet.skipahead(skip);

            dotnet.GenerateUniforms(UniformTransformEnum.STANDARD, 0, 1, count, dotnetRes);
            
            for (int i = 0; i < cudaRes.Length; ++i)
                Assert.Equal(cudaRes[i], dotnetRes[i], 1e-6);
        }

        [NativeFact(NativeRequirement.None, "InFine.Quantfinance.PlainC.dll")]
        // Not working, static matrices are not properly initialized...
        public void TestMethod_MRG32K3A_VsOMP()
        {
            CurandSamples o = new CurandSamples();
            ulong skipSequence = 87239;
            ulong skipSubsequence = 2938;
            ulong skip = 9879;
            ulong seed = 1928379729UL;

            var wrapped = HybRunner.OMP(o, "InFine.Quantfinance.PlainC.dll");
            var count = 10;
            var cudaRes = new double[count];
            wrapped.GenerateUniform(seed, 0, 0, cudaRes, count, skipSequence, skipSubsequence, skip);

            DotNet.MRG32K3A dotnet = new DotNet.MRG32K3A(seed);
            var dotnetRes = new double[count];
            dotnet.skipahead_sequence(skipSequence);
            dotnet.skipahead_subsequence(skipSubsequence);
            dotnet.skipahead(skip);

            dotnet.GenerateUniforms(UniformTransformEnum.STANDARD, 0, 1, count, dotnetRes);

            for (int i = 0; i < cudaRes.Length; ++i)
                Assert.Equal(cudaRes[i], dotnetRes[i], 1e-6);
        }

        //[Test]
        // Not working, static matrices are not properly initialized...
        public void TestMethod_MRG32K3A_VsAVX()
        {
            CurandSamples o = new CurandSamples();
            ulong skipSequence = 87239;
            ulong skipSubsequence = 2938;
            ulong skip = 9879;
            ulong seed = 1928379729UL;

            var wrapped = HybRunner.AVX(o, "InFine.Quantfinance.Haswell.dll");
            var count = 10;
            var cudaRes = new double[count];
            wrapped.GenerateUniform(seed, 0, 0, cudaRes, count, skipSequence, skipSubsequence, skip);

            DotNet.MRG32K3A dotnet = new DotNet.MRG32K3A(seed);
            var dotnetRes = new double[count];
            dotnet.skipahead_sequence(skipSequence);
            dotnet.skipahead_subsequence(skipSubsequence);
            dotnet.skipahead(skip);

            dotnet.GenerateUniforms(UniformTransformEnum.STANDARD, 0, 1, count, dotnetRes);

            for (int i = 0; i < cudaRes.Length; ++i)
                Assert.Equal(cudaRes[i], dotnetRes[i], 1e-6);
        }

        static void Main(string[] args)
        {
            new UnitTest_MRG32K3A().TestMethod_MRG32K3A_VsCuda();
        }
    }
}
