using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using InFine.QuantFinance.Models.Concepts;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Scheduling;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.LongstaffSchwartz.Tests
{
    public class Test_LS_AmericanCall
    {
        //int simCount = 1024 * 32 * 4; // AVX2: 311ms/option, CUDA: 597ms/option
        int simCount = 1024 * 16; // * 16;// * 32 * 4;       // AVX2:  31ms/option, CUDA: 40ms/option
        int datesCount = 365;
        bool computeCSharp = true;

        private int nbThreads = Environment.ProcessorCount;

        public int SimCount
        {
            get { return simCount; }
            set { simCount = value; }
        }

        public int NbThreads
        {
            get { return nbThreads; }
            set { nbThreads = value; }
        }

        private static unsafe LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor> InitLS(int datesCount, int simCount, double* copyRandoms = null)
        {
            double S0 = 1.0;
            var ls = new LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>();
            AmericanCall<LogBasedSpot<ArrayDoubleSpot>> product;
            ConstantRateDiscounting discounting;

            PrecalculatedSchedule dailySchedule;
            ArrayDoubleSpot udl;
            ConstantRate rate;

            double[] dates = new double[datesCount];
            for (int k = 0; k < datesCount; ++k)
            {
                dates[k] = (double)k / 365.0;
            }

            dailySchedule = new PrecalculatedSchedule(dates);
            ArrayBrownian brownian = new ArrayBrownian(simCount, datesCount);
            udl = new ArrayDoubleSpot(simCount, datesCount);

            // init values
            for (int k = 0; k < simCount; ++k)
            {
                udl[k, 0] = Math.Log(S0);
            }


            ConstantVolatility vol = new ConstantVolatility();
            rate = new ConstantRate();

            vol.Volatility = 0.45; // 45%
            rate.YearlyRate = 0.01; // 1% ACT

            // spot diffusion
            if (copyRandoms == null)
            {
                Random.MKL.MRG32K3A rng = new Random.MKL.MRG32K3A(20151114);
                rng.GenerateGaussians(Random.GaussianTransformEnum.BoxMuller2, 0.0, 1.0, simCount * datesCount, brownian.Backing);
            }
            else
            {
                var size = simCount * datesCount * sizeof(double);
                Buffer.MemoryCopy(copyRandoms, brownian.Backing, size, size);
            }
            //curand.switchToVersion(curand.VERSION.CUDA_80);
            //curand.curandGenerator_t gen;
            //curand.instance.curandCreateGenerator(out gen, curand.curandRngType_t.CURAND_RNG_PSEUDO_MRG32K3A);
            //curand.instance.curandGenerateNormalDouble(gen, (IntPtr) brownian.Backing, (size_t) (simCount * datesCount), 0.0, 1.0);

            Models.Diffusion.BlackScholesMerton<ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule> bed =
                new Models.Diffusion.BlackScholesMerton<ArrayDoubleSpot, ArrayBrownian, PrecalculatedSchedule>
                    (vol, rate, udl, brownian, dailySchedule);

            bed.Simulate(0, simCount);

            product = new AmericanCall<LogBasedSpot<ArrayDoubleSpot>>();
            product.Strike = 0.9;
            product.Spot = new LogBasedSpot<ArrayDoubleSpot>(udl);
            discounting = new ConstantRateDiscounting(rate.YearlyRate, dailySchedule);
            // inner data
            ls._dates = dates;
            ls._exercise = new alignedstorage_int(simCount);
            ls._premium = new alignedstorage_double(simCount, "premium");
            ls._simulationCount = simCount;
            ls._datesCount = dates.Length;

            // outer data
            ls.discounting = discounting;
            ls.observable = product.Spot;
            ls.product = product;
            ls.regressor = new QuadraticUnivariateRegressorStruct();
            ls.executor = new SingleBlockParallelExecutor();

            return ls;
        }

        private static unsafe LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpotZeroCopy>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpotZeroCopy>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor> InitLSZC(int datesCount, int simCount, double* copyRandoms = null)
        {
            double S0 = 1.0;
            var ls = new LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpotZeroCopy>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpotZeroCopy>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>();
            AmericanCall<LogBasedSpot<ArrayDoubleSpotZeroCopy>> product;
            ConstantRateDiscounting discounting;

            PrecalculatedSchedule dailySchedule;
            ArrayDoubleSpotZeroCopy udl;
            ConstantRate rate;

            double[] dates = new double[datesCount];
            for (int k = 0; k < datesCount; ++k)
            {
                dates[k] = (double)k / 365.0;
            }

            dailySchedule = new PrecalculatedSchedule(dates);
            ArrayBrownian brownian = new ArrayBrownian(simCount, datesCount);
            udl = new ArrayDoubleSpotZeroCopy(simCount, datesCount);

            // init values
            for (int k = 0; k < simCount; ++k)
            {
                udl[k, 0] = Math.Log(S0);
            }


            ConstantVolatility vol = new ConstantVolatility();
            rate = new ConstantRate();

            vol.Volatility = 0.45; // 45%
            rate.YearlyRate = 0.01; // 1% ACT

            // spot diffusion
            if (copyRandoms == null)
            {
                Random.MKL.MRG32K3A rng = new Random.MKL.MRG32K3A(20151114);
                rng.GenerateGaussians(Random.GaussianTransformEnum.BoxMuller2, 0.0, 1.0, simCount * datesCount, brownian.Backing);
            }
            else
            {
                var size = simCount * datesCount * sizeof(double);
                Buffer.MemoryCopy(copyRandoms, brownian.Backing, size, size);
            }
            //curand.switchToVersion(curand.VERSION.CUDA_80);
            //curand.curandGenerator_t gen;
            //curand.instance.curandCreateGenerator(out gen, curand.curandRngType_t.CURAND_RNG_PSEUDO_MRG32K3A);
            //curand.instance.curandGenerateNormalDouble(gen, (IntPtr) brownian.Backing, (size_t) (simCount * datesCount), 0.0, 1.0);

            Models.Diffusion.BlackScholesMerton<ArrayDoubleSpotZeroCopy, ArrayBrownian, PrecalculatedSchedule> bed =
                new Models.Diffusion.BlackScholesMerton<ArrayDoubleSpotZeroCopy, ArrayBrownian, PrecalculatedSchedule>
                    (vol, rate, udl, brownian, dailySchedule);

            bed.Simulate(0, simCount);

            product = new AmericanCall<LogBasedSpot<ArrayDoubleSpotZeroCopy>>();
            product.Strike = 0.9;
            product.Spot = new LogBasedSpot<ArrayDoubleSpotZeroCopy>(udl);
            discounting = new ConstantRateDiscounting(rate.YearlyRate, dailySchedule);
            // inner data
            ls._dates = dates;
            ls._exercise = new alignedstorage_int(simCount);
            ls._premium = new alignedstorage_double(simCount, "premium");
            ls._simulationCount = simCount;
            ls._datesCount = dates.Length;

            // outer data
            ls.discounting = discounting;
            ls.observable = product.Spot;
            ls.product = product;
            ls.regressor = new QuadraticUnivariateRegressorStruct();
            ls.executor = new SingleBlockParallelExecutor();

            return ls;
        }

        private static double runCuda(dynamic wrapped, LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor> ls, LSHelper helper, double cudaKernelDuration,
            HybRunner cu, Stopwatch swCuda)
        {
            wrapped.Calculate(ls, helper);
            if (cuda.GetLastError() != cudaError_t.cudaSuccess)
                Assert.Fail("Cuda error " + cuda.GetLastError());
            if (cuda.DeviceSynchronize() != cudaError_t.cudaSuccess)
                Assert.Fail("Cuda error " + cuda.GetLastError());
            cudaKernelDuration = cu.LastKernelDuration.Elapsed.TotalSeconds;
            cu.Marshaller.CleanUpManagedData(ls.res);
            swCuda.Stop();
            return cudaKernelDuration;
        }

        [NativeFact(NativeRequirement.CudaDevice | NativeRequirement.Mkl, NativeEnvironment.CudaSatelliteName)]
        public unsafe void AmericanCall_CUDA()
        {
            var ls = InitLS(datesCount, simCount);
            var helper = ls.GetHelperParallelReduction();

            double premiumRef = 0;
            double timeRef = 0;
            if (computeCSharp)
            {
                var swOld = Stopwatch.StartNew();
                ls.Calculate(helper);
                swOld.Stop();
                premiumRef = ls.res[0] / ls._simulationCount;
                timeRef = swOld.Elapsed.TotalSeconds;
            }

            cuda.DeviceSetLimit(cudaLimit.cudaLimitStackSize, 4096);
            cudaStream_t stream;
            cuda.StreamCreate(out stream);
            var cu = HybRunner.Cuda(NativeEnvironment.CudaSatellitePath, stream, CudaMarshaler.Create(true, stream)).SetDistrib(1, 128);
            cu.SetShared(cu.BlockDimX * sizeof(QuadraticUnivariateRegressorStruct.RegressorData));
            var lsEntryPoint = new LSEntryPoint();
            var wrapped = cu.Wrap(lsEntryPoint);
            cu.Marshaller.cleanUpNativeData = false;

            var swCuda = Stopwatch.StartNew();
            double cudaKernelDuration = 0;
            cudaKernelDuration = runCuda(wrapped, ls, helper, cudaKernelDuration, cu, swCuda);

            double premium = ls.res[0] / ls._simulationCount;

            Console.Out.WriteLine("C# = {0} ({2}) -- Cuda = {1} ({3} / {4}) ", premiumRef, premium, timeRef, swCuda.Elapsed.TotalSeconds, cudaKernelDuration);
            Assert.Equal(premiumRef, premium, 1E-4 * Math.Abs(premiumRef));
        }

        /// <summary>
        /// Compute many (one per block)
        /// </summary>
        //[Test]
        public unsafe double AmericanCall_CUDA_Many()
        {
            cuda.DeviceReset();
            int device;
            cuda.GetDevice(out device);
            cudaDeviceProp props;
            cuda.GetDeviceProperties(out props, device);
            long memPerBlock = datesCount * simCount * sizeof(double) + //Spots
                simCount * (sizeof(double) + sizeof(int)) // exercise and premium
                + datesCount * sizeof(double)  // dates
                + 2000; // Completely wrong estimation

            int nbProducts = (int)Math.Min(((long)props.totalGlobalMem - 1300 * 1024 * 1024) / memPerBlock, props.multiProcessorCount * 8);
            nbProducts = 88;
            Console.WriteLine("Computing {0} blocks ({1} MB)", nbProducts, (long)props.totalGlobalMem / (1 << 20));
            //var nbProducts = props.multiProcessorCount;



            var lss = new LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>[nbProducts];
            LSHelper[] helpers = new LSHelper[lss.Length];

            ArrayBrownian tmp = new ArrayBrownian(simCount, datesCount);
            Random.MKL.MRG32K3A rng = new Random.MKL.MRG32K3A(20151114);
            rng.GenerateGaussians(Random.GaussianTransformEnum.BoxMuller2, 0.0, 1.0, simCount * datesCount, tmp.Backing);

            Parallel.For(0, nbProducts, k =>
            {
                var ls = InitLS(datesCount, simCount, tmp.Backing);
                lss[k] = ls;
                helpers[k] = ls.GetHelperParallelReduction();
            });

            cuda.DeviceSetLimit(cudaLimit.cudaLimitStackSize, 4096);
            //cuda.DeviceSetCacheConfig(cudaFuncCache.cudaFuncCachePreferL1);
            cudaStream_t stream;
            cuda.StreamCreate(out stream);
            var cu = HybRunner.Cuda(NativeEnvironment.CudaSatellitePath, stream, CudaMarshaler.Create(true, stream)).SetDistrib(nbProducts, 128);
            cu.SetShared(Math.Max(32 * sizeof(QuadraticUnivariateRegressorStruct.RegressorData), cu.BlockDimX * sizeof(double)));
            var lsEntryPoint = new LSEntryPoint();
            var wrapped = cu.Wrap(lsEntryPoint);
            cu.Marshaller.cleanUpNativeData = false;

            var swCuda = Stopwatch.StartNew();
            var lssp = cu.Marshaller.MarshalManagedToNative(lss);
            var helpersp = cu.Marshaller.MarshalManagedToNative(helpers);
            var swCudaNoMarshalling = Stopwatch.StartNew();
            wrapped.CalculateMany(lss, helpers, nbProducts);
            if (cuda.DeviceSynchronize() != cudaError_t.cudaSuccess)
                Assert.Fail("Cuda error " + cuda.GetLastError());
            swCudaNoMarshalling.Stop();
            size_t free, total;
            cuda.MemGetInfo(out free, out total);
            for (int i = 0; i < nbProducts; ++i)
                cu.Marshaller.CleanUpManagedData(lss[i].res);
            swCuda.Stop();
            for (int i = 0; i < nbProducts; ++i)
            {
                lss[i]._exercise.destroy();
                lss[i]._premium.destroy();
                lss[i].observable.Free();
            }
            cu.Marshaller.Free();
            cuda.DeviceReset();

            double premium = lss[0].res[0] / lss[0]._simulationCount;

            var timePerOpt = swCuda.Elapsed.TotalSeconds / lss.Length;
            var simPerSec = simCount * nbProducts / swCudaNoMarshalling.Elapsed.TotalSeconds;

            Console.Out.WriteLine("Cuda Generated = {0} (totalTime(inc):{1} / totalTime(ex):{2}) : {3} sim/sec - {4} bytes used", premium, swCuda.Elapsed.TotalSeconds, swCudaNoMarshalling.Elapsed.TotalSeconds, simPerSec, ((long)total - (long)free) / nbProducts);
            for (int i = 1; i < nbProducts; ++i)
            {
                Assert.Equal(premium, lss[i].res[0] / lss[i]._simulationCount);
            }
            return simPerSec;
        }

        public unsafe double AmericanCall_CUDA_Many_ZC()
        {
            cuda.DeviceReset();
            int device;
            cuda.GetDevice(out device);
            cudaDeviceProp props;
            cuda.GetDeviceProperties(out props, device);
            long memPerBlock = datesCount * simCount * sizeof(double) + //Spots
                simCount * (sizeof(double) + sizeof(int)) // exercise and premium
                + datesCount * sizeof(double)  // dates
                + 2000; // Completely wrong estimation

            int nbProducts = props.multiProcessorCount * 8;
            Console.WriteLine("Computing {0} blocks ({1} MB)", nbProducts, (long)props.totalGlobalMem / (1 << 20));
            //var nbProducts = props.multiProcessorCount;



            var lss = new LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpotZeroCopy>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpotZeroCopy>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>[nbProducts];
            LSHelper[] helpers = new LSHelper[lss.Length];

            ArrayBrownian tmp = new ArrayBrownian(simCount, datesCount);
            Random.MKL.MRG32K3A rng = new Random.MKL.MRG32K3A(20151114);
            rng.GenerateGaussians(Random.GaussianTransformEnum.BoxMuller2, 0.0, 1.0, simCount * datesCount, tmp.Backing);

            Parallel.For(0, nbProducts, k =>
            {
                var ls = InitLSZC(datesCount, simCount, tmp.Backing);
                lss[k] = ls;
                helpers[k] = ls.GetHelperParallelReduction();
            });

            cuda.DeviceSetLimit(cudaLimit.cudaLimitStackSize, 4096);
            //cuda.DeviceSetCacheConfig(cudaFuncCache.cudaFuncCachePreferL1);
            cudaStream_t stream;
            cuda.StreamCreate(out stream);
            var cu = HybRunner.Cuda(NativeEnvironment.CudaSatellitePath, stream, CudaMarshaler.Create(true, stream)).SetDistrib(nbProducts, 128);
            cu.SetShared(32 * sizeof(QuadraticUnivariateRegressorStruct.RegressorData));
            var lsEntryPoint = new LSEntryPoint();
            var wrapped = cu.Wrap(lsEntryPoint);
            cu.Marshaller.cleanUpNativeData = false;

            var swCuda = Stopwatch.StartNew();
            var lssp = cu.Marshaller.MarshalManagedToNative(lss);
            var helpersp = cu.Marshaller.MarshalManagedToNative(helpers);
            var swCudaNoMarshalling = Stopwatch.StartNew();
            wrapped.CalculateManyZC(lss, helpers, nbProducts);
            if (cuda.DeviceSynchronize() != cudaError_t.cudaSuccess)
                Assert.Fail("Cuda error " + cuda.GetLastError());
            swCudaNoMarshalling.Stop();
            size_t free, total;
            cuda.MemGetInfo(out free, out total);
            for (int i = 0; i < nbProducts; ++i)
                cu.Marshaller.CleanUpManagedData(lss[i].res);
            swCuda.Stop();
            for (int i = 0; i < nbProducts; ++i)
            {
                lss[i]._exercise.destroy();
                lss[i]._premium.destroy();
                lss[i].observable.Free();
            }
            cu.Marshaller.Free();
            cuda.DeviceReset();

            double premium = lss[0].res[0] / lss[0]._simulationCount;

            var timePerOpt = swCuda.Elapsed.TotalSeconds / lss.Length;
            var simPerSec = simCount * nbProducts / swCudaNoMarshalling.Elapsed.TotalSeconds;

            Console.Out.WriteLine("Cuda Generated ZC = {0} (totalTime(inc):{1} / totalTime(ex):{2}) : {3} sim/sec - {4} bytes used", premium, swCuda.Elapsed.TotalSeconds, swCudaNoMarshalling.Elapsed.TotalSeconds, simPerSec, ((long)total - (long)free) / nbProducts);
            for (int i = 1; i < nbProducts; ++i)
            {
                Assert.Equal(premium, lss[i].res[0] / lss[i]._simulationCount);
            }
            return simPerSec;
        }

        public unsafe double AmericanCall_CUDA_Many_Manual()
        {
            cuda.DeviceReset();
            int device;
            cuda.GetDevice(out device);
            cudaDeviceProp props;
            cuda.GetDeviceProperties(out props, device);
            long memPerBlock = datesCount * simCount * sizeof(double) + //Spots
                simCount * (sizeof(double) + sizeof(int)) // exercise and premium
                + datesCount * sizeof(double)  // dates
                + 2000; // Completely wrong estimation

            int nbProducts = (int)Math.Min(((long)props.totalGlobalMem - 1300 * 1024 * 1024) / memPerBlock, props.multiProcessorCount * 8);
            //if (simCount == 16384)
            //    nbProducts = 100;
            //if (simCount == 32768)
            //    nbProducts = 46;
            //if (simCount == 32768 * 2)
            //    nbProducts = 25;
            //if (simCount == 32768 * 4)
            //    nbProducts = 12;
            //if (simCount == 32768 * 8)
            //    nbProducts = 6;
            //if (simCount == 32768 * 16)
            //    nbProducts = 3;
            Console.WriteLine("Computing {0} blocks ({1} MB)", nbProducts, (long)props.totalGlobalMem / (1 << 20));
            //var nbProducts = props.multiProcessorCount;

            //cuda.DeviceSetCacheConfig(cudaFuncCache.cudaFuncCachePreferL1);

            var lss = new LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>[nbProducts];
            LSHelper[] helpers = new LSHelper[lss.Length];

            ArrayBrownian tmp = new ArrayBrownian(simCount, datesCount);
            Random.MKL.MRG32K3A rng = new Random.MKL.MRG32K3A(20151114);
            rng.GenerateGaussians(Random.GaussianTransformEnum.BoxMuller2, 0.0, 1.0, simCount * datesCount, tmp.Backing);

            Parallel.For(0, nbProducts, k =>
            {
                var ls = InitLS(datesCount, simCount, tmp.Backing);
                lss[k] = ls;
                helpers[k] = ls.GetHelperParallelReduction();
            });

            //cuda.DeviceSetLimit(cudaLimit.cudaLimitStackSize, 4096);
            cudaStream_t stream;
            cuda.StreamCreate(out stream);
            var cu = HybRunner.Cuda(NativeEnvironment.CudaSatellitePath, stream, CudaMarshaler.Create(true, stream)).SetDistrib(nbProducts, 128);
            cu.SetShared(0); // 256 is a guesstimate...
            var lsEntryPoint = new LSEntryPoint();
            var wrapped = cu.Wrap(lsEntryPoint);
            cu.Marshaller.cleanUpNativeData = false;

            var swCuda = Stopwatch.StartNew();
            var lssp = cu.Marshaller.MarshalManagedToNative(lss);
            var helpersp = cu.Marshaller.MarshalManagedToNative(helpers);
            wrapped.Manual_LS(lssp, helpersp, nbProducts);
            if (cuda.DeviceSynchronize() != cudaError_t.cudaSuccess)
                Assert.Fail("Cuda error " + cuda.GetLastError());
            size_t free, total;
            cuda.MemGetInfo(out free, out total);
            for (int i = 0; i < nbProducts; ++i)
                cu.Marshaller.CleanUpManagedData(lss[i].res);
            swCuda.Stop();
            for (int i = 0; i < nbProducts; ++i)
            {
                lss[i]._exercise.destroy();
                lss[i]._premium.destroy();
                lss[i].observable.Free();
            }
            cu.Marshaller.Free();
            cuda.DeviceReset();

            double premium = lss[0].res[0] / lss[0]._simulationCount;

            var timePerOpt = swCuda.Elapsed.TotalSeconds / lss.Length;
            var simPerSec = simCount * nbProducts / cu.LastKernelDuration.Elapsed.TotalSeconds;

            Console.Out.WriteLine("Cuda Manual = {0} (totalTime(inc):{1} / totalTime(ex):{2}) : {3} sim/sec - {4} bytes used", premium, swCuda.Elapsed.TotalSeconds, cu.LastKernelDuration.Elapsed.TotalSeconds, simPerSec, ((long)total - (long)free) / nbProducts);
            for (int i = 1; i < nbProducts; ++i)
            {
                Assert.True(premium == lss[i].res[0] / lss[i]._simulationCount, "Error @ " + i);
            }
            return simPerSec;
        }

        public unsafe double AmericanCall_CUDA_Many_Manual_ZC()
        {
            cuda.DeviceReset();
            int device;
            cuda.GetDevice(out device);
            cudaDeviceProp props;
            cuda.GetDeviceProperties(out props, device);
            long memPerBlock = datesCount * simCount * sizeof(double) + //Spots
                simCount * (sizeof(double) + sizeof(int)) // exercise and premium
                + datesCount * sizeof(double)  // dates
                + 2000; // Completely wrong estimation

            int nbProducts = props.multiProcessorCount * 8;
            //if (simCount == 16384)
            //    nbProducts = 100;
            //if (simCount == 32768)
            //    nbProducts = 46;
            //if (simCount == 32768 * 2)
            //    nbProducts = 25;
            //if (simCount == 32768 * 4)
            //    nbProducts = 12;
            //if (simCount == 32768 * 8)
            //    nbProducts = 6;
            //if (simCount == 32768 * 16)
            //    nbProducts = 3;
            Console.WriteLine("Computing {0} blocks ({1} MB)", nbProducts, (long)props.totalGlobalMem / (1 << 20));
            //var nbProducts = props.multiProcessorCount;

            //cuda.DeviceSetCacheConfig(cudaFuncCache.cudaFuncCachePreferL1);

            var lss = new LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpotZeroCopy>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpotZeroCopy>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>[nbProducts];
            LSHelper[] helpers = new LSHelper[lss.Length];

            ArrayBrownian tmp = new ArrayBrownian(simCount, datesCount);
            Random.MKL.MRG32K3A rng = new Random.MKL.MRG32K3A(20151114);
            rng.GenerateGaussians(Random.GaussianTransformEnum.BoxMuller2, 0.0, 1.0, simCount * datesCount, tmp.Backing);

            Parallel.For(0, nbProducts, k =>
            {
                var ls = InitLSZC(datesCount, simCount, tmp.Backing);
                lss[k] = ls;
                helpers[k] = ls.GetHelperParallelReduction();
            });

            //cuda.DeviceSetLimit(cudaLimit.cudaLimitStackSize, 4096);
            cudaStream_t stream;
            cuda.StreamCreate(out stream);
            var cu = HybRunner.Cuda(NativeEnvironment.CudaSatellitePath, stream, CudaMarshaler.Create(true, stream)).SetDistrib(nbProducts, 128);
            cu.SetShared(0); // 256 is a guesstimate...
            var lsEntryPoint = new LSEntryPoint();
            var wrapped = cu.Wrap(lsEntryPoint);
            cu.Marshaller.cleanUpNativeData = false;

            var swCuda = Stopwatch.StartNew();
            var lssp = cu.Marshaller.MarshalManagedToNative(lss);
            var helpersp = cu.Marshaller.MarshalManagedToNative(helpers);
            wrapped.Manual_LS_ZC(lssp, helpersp, nbProducts);
            if (cuda.DeviceSynchronize() != cudaError_t.cudaSuccess)
                Assert.Fail("Cuda error " + cuda.GetLastError());
            size_t free, total;
            cuda.MemGetInfo(out free, out total);
            for (int i = 0; i < nbProducts; ++i)
                cu.Marshaller.CleanUpManagedData(lss[i].res);
            swCuda.Stop();
            for (int i = 0; i < nbProducts; ++i)
            {
                lss[i]._exercise.destroy();
                lss[i]._premium.destroy();
                lss[i].observable.Free();
            }
            cu.Marshaller.Free();
            cuda.DeviceReset();

            double premium = lss[0].res[0] / lss[0]._simulationCount;

            var timePerOpt = swCuda.Elapsed.TotalSeconds / lss.Length;
            var simPerSec = simCount * nbProducts / cu.LastKernelDuration.Elapsed.TotalSeconds;

            Console.Out.WriteLine("Cuda Manual ZC = {0} (totalTime(inc):{1} / totalTime(ex):{2}) : {3} sim/sec - {4} bytes used", premium, swCuda.Elapsed.TotalSeconds, cu.LastKernelDuration.Elapsed.TotalSeconds, simPerSec, ((long)total - (long)free) / nbProducts);
            for (int i = 1; i < nbProducts; ++i)
            {
                Assert.True(premium == lss[i].res[0] / lss[i]._simulationCount, "Error @ " + i);
            }
            return simPerSec;
        }

        /// <summary>
        /// Compute many (one per thread)
        /// </summary>
        //[Test]
        public unsafe double AmericanCall_AVX_Many()
        {
            int nbProducts = NbThreads;

            var lss = new LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>[nbProducts];
            LSHelper[] helpers = new LSHelper[lss.Length];

            ArrayBrownian tmp = new ArrayBrownian(simCount, datesCount);
            Random.MKL.MRG32K3A rng = new Random.MKL.MRG32K3A(20151114);
            rng.GenerateGaussians(Random.GaussianTransformEnum.BoxMuller2, 0.0, 1.0, simCount * datesCount, tmp.Backing);

            Parallel.For(0, nbProducts, k =>
            {
                var ls = InitLS(datesCount, simCount, tmp.Backing);
                lss[k] = ls;
                helpers[k] = ls.GetHelperSerialReduction();
            });


            var cu = HybRunner.AVX("InFine.QuantFinance.Haswell.dll");
            var lsEntryPoint = new LSEntryPoint();

            var wrapped = cu.Wrap(lsEntryPoint);
            cu.Marshaller.cleanUpNativeData = false;

            cu.Marshaller.MarshalManagedToNative(lss);
            cu.Marshaller.MarshalManagedToNative(helpers);
            var swCuda = Stopwatch.StartNew();
            Parallel.For(0, nbProducts, k =>
            {
                wrapped.Calculate(lss[k], helpers[k]);
                //wrapped.CalculateMany(lss, helpers, nbProducts);
            });
            swCuda.Stop();
            for (int i = 0; i < nbProducts; ++i)
                cu.Marshaller.CleanUpManagedData(lss[i].res);

            cu.Marshaller.Free();

            double premium = lss[0].res[0] / lss[0]._simulationCount;
            var timePerOpt = swCuda.Elapsed.TotalSeconds / lss.Length;
            var simPerSec = simCount * nbProducts / swCuda.Elapsed.TotalSeconds;

            Console.Out.WriteLine("AVX = {0} ({1} / {2}) : {3} sim/sec", premium, swCuda.Elapsed.TotalSeconds, timePerOpt, simPerSec);

            for (int i = 1; i < nbProducts; ++i)
            {
                Assert.Equal(premium, lss[i].res[0] / lss[i]._simulationCount);
            }
            return simPerSec;
        }

        /// <summary>
        /// Compute many (one per thread)
        /// </summary>
        //[Test]
        public unsafe double AmericanCall_Csharp_Many()
        {
            int nbProducts = nbThreads; ;

            var lss = new LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>[nbProducts];
            LSHelper[] helpers = new LSHelper[lss.Length];

            ArrayBrownian tmp = new ArrayBrownian(simCount, datesCount);
            Random.MKL.MRG32K3A rng = new Random.MKL.MRG32K3A(20151114);
            rng.GenerateGaussians(Random.GaussianTransformEnum.BoxMuller2, 0.0, 1.0, simCount * datesCount, tmp.Backing);

            Parallel.For(0, nbProducts, k =>
            {
                var ls = InitLS(datesCount, simCount, tmp.Backing);
                lss[k] = ls;
                helpers[k] = ls.GetHelperSerialReduction();
            });

            var swCuda = Stopwatch.StartNew();
            Parallel.For(0, nbProducts, k =>
            {
                LSEntryPoint.Calculate(lss[k], helpers[k]);
            });
            swCuda.Stop();

            double premium = lss[0].res[0] / lss[0]._simulationCount;
            var timePerOpt = swCuda.Elapsed.TotalSeconds / lss.Length;
            var simPerSec = simCount * nbProducts / swCuda.Elapsed.TotalSeconds;
            Console.Out.WriteLine("C# = {0} ({1} / {2}) : {3} sim/sec", premium, swCuda.Elapsed.TotalSeconds, timePerOpt, simPerSec);

            for (int i = 1; i < nbProducts; ++i)
            {
                Assert.Equal(premium, lss[i].res[0] / lss[i]._simulationCount);
            }
            return simPerSec;
        }

        //[Test]
        public unsafe void AmericanCall_CUDA_Many_Stream()
        {
            int device;
            cuda.GetDevice(out device);
            cudaDeviceProp props;
            cuda.GetDeviceProperties(out props, device);
            var nbProducts = 4; //props.multiProcessorCount * 4;

            var lss = new LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>[nbProducts];
            LSHelper[] helpers = new LSHelper[lss.Length];

            for (int k = 0; k < lss.Length; ++k)
            {
                var ls = InitLS(datesCount, simCount);
                lss[k] = ls;
                helpers[k] = ls.GetHelperParallelReduction();
            }

            cuda.DeviceSetLimit(cudaLimit.cudaLimitStackSize, 4096);


            HybRunner[] cu = new HybRunner[nbProducts];
            dynamic[] wrapped = new dynamic[nbProducts];
            for (int k = 0; k < lss.Length; ++k)
            {
                cudaStream_t stream;
                cuda.StreamCreate(out stream);
                cu[k] = HybRunner.Cuda(NativeEnvironment.CudaSatellitePath, stream, CudaMarshaler.Create(true, stream)).SetDistrib(1, 32);
                cu[k].SetShared(cu[k].BlockDimX * sizeof(QuadraticUnivariateRegressorStruct.RegressorData));
                var lsEntryPoint = new LSEntryPoint();
                wrapped[k] = cu[k].Wrap(lsEntryPoint);
                cu[k].Marshaller.cleanUpNativeData = false;
            }

            var swCuda = Stopwatch.StartNew();
            for (int k = 0; k < lss.Length; ++k)
            {
                wrapped[k].Calculate(lss[k], helpers[k]);
            }
            if (cuda.DeviceSynchronize() != cudaError_t.cudaSuccess)
                Assert.Fail("Cuda error " + cuda.GetLastError());
            cu[0].Marshaller.CleanUpManagedData(lss[0].res);
            swCuda.Stop();

            double premium = lss[0].res[0] / lss[0]._simulationCount;

            Console.Out.WriteLine("Cuda = {0} ({1} / {2}) ", premium, swCuda.Elapsed.TotalSeconds, swCuda.Elapsed.TotalSeconds / lss.Length);
        }

        [NativeFact(NativeRequirement.Mkl, "InFine.QuantFinance.Haswell.dll")]
        public unsafe void AmericanCall_AVX()
        {
            Stopwatch swRef = new Stopwatch();
            double premiumRef = 0;
            if (computeCSharp)
            {
                var ls = InitLS(datesCount, simCount);
                var helper = ls.GetHelperSerialReduction();

                swRef.Start();
                ls.Calculate(helper);
                premiumRef = ls.res[0] / ls._simulationCount;
                swRef.Stop();
            }
            {
                var ls = InitLS(datesCount, simCount);
                var helper = ls.GetHelperSerialReduction();

                var cu = HybRunner.AVX("InFine.QuantFinance.Haswell.dll");
                cu.Marshaller.cleanUpNativeData = false;
                var lsEntryPoint = new LSEntryPoint();
                var wrapped = cu.Wrap(lsEntryPoint);

                var swCuda = Stopwatch.StartNew();
                wrapped.Calculate(ls, helper);
                swCuda.Stop();
                //LSEntryPoint.Calculate(ls, helper);

                double premium = ls.res[0] / ls._simulationCount;

                Console.Out.WriteLine("C# = {0} ({2}) -- AVX = {1} ({3} / {4}) ", premiumRef, premium, swRef.Elapsed.TotalSeconds, swCuda.Elapsed.TotalSeconds, cu.LastKernelDuration.Elapsed.TotalSeconds);
                if (computeCSharp) Assert.Equal(premiumRef, premium, 1E-4 * Math.Abs(premiumRef));
            }
        }

        [NativeFact(NativeRequirement.Mkl | NativeRequirement.HybridizerRuntime)]
        public unsafe void AmericanCall_CSharp()
        {
            Stopwatch swRef = new Stopwatch();
            double premiumRef = 0;
            if (computeCSharp)
            {
                var ls = InitLS(datesCount, simCount);
                var helper = ls.GetHelperSerialReduction();

                swRef.Start();
                ls.Calculate(helper);
                premiumRef = ls.res[0] / ls._simulationCount;
                swRef.Stop();
            }
            Console.Out.WriteLine("C# = {0} ({1})", premiumRef, swRef.Elapsed.TotalSeconds);
        }

        [NativeFact(NativeRequirement.Mkl, "InFine.QuantFinance.PlainC.dll")]
        public unsafe void AmericanCall_OMP()
        {
            Stopwatch swRef = new Stopwatch();
            double premiumRef = 0;
            if (computeCSharp)
            {
                var ls = InitLS(datesCount, simCount);
                var helper = ls.GetHelperSerialReduction();

                swRef.Start();
                ls.Calculate(helper);
                premiumRef = ls.res[0] / ls._simulationCount;
                swRef.Stop();
            }
            {
                var ls = InitLS(datesCount, simCount);
                var helper = ls.GetHelperSerialReduction();

                var cu = HybRunner.OMP("InFine.QuantFinance.PlainC.dll");
                cu.Marshaller.cleanUpNativeData = false;
                var lsEntryPoint = new LSEntryPoint();
                var wrapped = cu.Wrap(lsEntryPoint);

                var swCuda = Stopwatch.StartNew();
                wrapped.Calculate(ls, helper);
                swCuda.Stop();
                //LSEntryPoint.Calculate(ls, helper);

                double premium = ls.res[0] / ls._simulationCount;

                Console.Out.WriteLine("C# = {0} ({2}) -- OMP = {1} ({3} / {4}) ", premiumRef, premium, swRef.Elapsed.TotalSeconds, swCuda.Elapsed.TotalSeconds, cu.LastKernelDuration.Elapsed.TotalSeconds);
                //Assert.Equal(premiumRef, premium, 1E-4 * Math.Abs(premiumRef));
            }
        }

        // Manual harness (dotnet run): no argument runs the C# and manual CUDA American call once;
        // "sweep [threads]" runs the simulation-size sweep comparing C#, AVX and both CUDA flavors.
        public static void Main(string[] args)
        {
            if (args.Length == 0 || args[0] != "sweep")
            {
                var tt = new Test_LS_AmericanCall();
                tt.AmericanCall_CSharp();
                tt.AmericanCall_CUDA_Many_Manual();
                return;
            }
            int nbThreads = Environment.ProcessorCount;
            if (args.Length > 1)
                nbThreads = int.Parse(args[1]);
            var sw = new StringWriter();

            Console.WriteLine("STARTING");
            sw.WriteLine("i;simcount;C#;AVX;CUDA Generated;CUDA Manual");
            for (int i = 5; i < 12; ++i)
            {
                var t = new Test_LS_AmericanCall();
                t.nbThreads = nbThreads;
                t.SimCount = 16 * (1 << i);
                Console.WriteLine("\n\n#### SIM SIZE: {0}", t.SimCount);
                double maxCSharp = 0;
                double maxAVX = 0;
                double maxCuda = 0;
                double maxCudaManual = 0;
                for (int k = 0; k < 2; ++k)
                {
                    var csharp = t.AmericanCall_Csharp_Many();
                    var avx = t.AmericanCall_AVX_Many();
                    var cuda = t.AmericanCall_CUDA_Many();
                    var cudaManual = t.AmericanCall_CUDA_Many_Manual();

                    maxCSharp = Math.Max(maxCSharp, csharp);
                    maxAVX = Math.Max(maxAVX, avx);
                    maxCuda = Math.Max(maxCuda, cuda);
                    maxCudaManual = Math.Max(maxCudaManual, cudaManual);
                }
                sw.WriteLine("{0};{1};{2};{3};{4};{5}", i, t.simCount, maxCSharp, maxAVX, maxCuda, maxCudaManual);
            }
            Console.WriteLine(sw.ToString());
        }
    }
}
