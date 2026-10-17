using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Scheduling
{
    [IntrinsicInclude("quantfinance.tools.h")]
    public static class Tools
    {
        [IntrinsicFunction("__syncthreads")]
        public static void SyncThreads()
        {
        }

        [IntrinsicFunction("__threadfence")]
        public static void ThreadFence()
        {
        }

        // TODO: implement other flavors
        [IntrinsicFunction(Flavor = (int)HybridizerFlavor.CUDA, Name="clock64")] 
        public static long Clock64()
        {
            return 0L ;
        }

        [IntrinsicFunction("atomicInc", Flavor = 1)]
        public static uint atomicInc(ref uint data, uint val)
        {
            return data = (data >= val ? 0 : data + 1);
        }

        [IntrinsicFunction("::hybridizer::atomicInc_int", Flavor = 1)]
        public static int atomicInc(ref int data, int val)
        {
            return data = (data >= val ? 0 : data + 1);
        }

        [IntrinsicFunction("::hybridizer::enforce_serial", Flavor = (int) HybridizerFlavor.CUDA)]
        public static bool EnforceSerial()
        {
            return true;
        }

        [IntrinsicFunction(Name = "hybridizer::reduce_add", Flavor = (int) HybridizerFlavor.AVX), ReturnTypeInference(VectorizerIntrinsicReturn.Unchanged)]
        public static double reduce_add(double v)
        {
            return v;
        }

        [IntrinsicFunction(Name = "hybridizer::reduce_constant", Flavor = (int)HybridizerFlavor.AVX), ReturnTypeInference(VectorizerIntrinsicReturn.VectorTransitive, 0)]
        public static double reduce_constant(double x, double value)
        {
            return value;
        }

        [IntrinsicFunction("::hybridizer::reduce_warp_add<double>")]
        public static double reduce_warp_add_double(double v) { return v; }

    }
}