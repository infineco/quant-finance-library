using System;
using System.Diagnostics;
using System.Threading.Tasks;
using InFine.QuantFinance.Models.Concepts;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Scheduling;
using InFine.QuantFinance.TestSupport;
using Xunit;

namespace InFine.QuantFinance.LongstaffSchwartz.Tests
{
    public class Test_LS_Paper
    { 
        //[Test] - fails for an unknown reason
        public unsafe void TestMethod_AmericanPut_LSPaper_Cuda()
        {
            int simcount = 8;

            double[,] spotValues = 
            { 
                { 1.00, 1.09, 1.08, 1.34 },
                { 1.00, 1.16, 1.26, 1.54 },
                { 1.00, 1.22, 1.07, 1.03 },
                { 1.00, 0.93, 0.97, 0.92 },
                { 1.00, 1.11, 1.56, 1.52 },
                { 1.00, 0.76, 0.77, 0.90 },
                { 1.00, 0.92, 0.84, 1.01 },
                { 1.00, 0.88, 1.22, 1.34 }
            };

            var spot = new ArrayDoubleSpot(simcount, 4);
            for (int i = 0; i < simcount; ++i)
                for (int j = 0; j < 4; ++j)
                    spot[i, j] = spotValues[i, j];

            int datesCount = 365;
            double[] datesSched = new double[datesCount];
            for (int k = 0; k < datesCount; ++k) { datesSched[k] = (double)k / 365.0; }

            PrecalculatedSchedule dailySchedule = new PrecalculatedSchedule(datesSched);

            ConstantRateDiscounting discounting = new ConstantRateDiscounting(0.06, dailySchedule);

            double[] dates = { 0.0, 1.0, 2.0, 3.0 };

            AmericanPut product = new AmericanPut();
            product.Strike = 1.10;
            product.Spot = spot;

            var regressor = new QuadraticUnivariateRegressorStruct();

            var ls = new LongstaffSchwartz<ArrayDoubleSpot, ConstantRateDiscounting, AmericanPut, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>();

            // inner data
            ls._dates = dates;
            ls._exercise = new alignedstorage_int(simcount);
            ls._premium = new alignedstorage_double(simcount);
            ls._simulationCount = simcount;
            ls._datesCount = dates.Length;
            
            // outer data
            ls.discounting = discounting;
            ls.observable = spot;
            ls.product = product;
            ls.regressor = regressor;
            ls.executor = new SingleBlockParallelExecutor();


            LSEntryPoint.Calculate_Paper(ls, ls.GetHelperParallelReduction());

            cuda.DeviceSetLimit(cudaLimit.cudaLimitStackSize, 4096);

            cudaStream_t stream;
            cuda.StreamCreate(out stream);
            var cu = HybRunner.Cuda(NativeEnvironment.CudaSatellitePath, stream, CudaMarshaler.Create(false, stream)).SetDistrib(1, 32);
            cu.SetShared(cu.BlockDimX * sizeof(QuadraticUnivariateRegressorStruct.RegressorData));
            var lsEntryPoint = new LSEntryPoint();
            var wrapped = cu.Wrap(lsEntryPoint);
            wrapped.Calculate_Paper(ls, ls.GetHelperParallelReduction());

            double premium = ls.res[0]/ls._simulationCount;
            Console.Out.WriteLine("Premium = {0}, took {1}", premium, cu.LastKernelDuration.Elapsed.TotalMilliseconds);

            Assert.Equal(0.1144, premium, 0.00005);
        }

        [NativeFact(NativeRequirement.None, "InFine.Quantfinance.Haswell.dll")]
        public void TestMethod_AmericanPut_LSPaper_AVX()
        {
            int simcount = 8;

            double[,] spotValues = 
            { 
                { 1.00, 1.09, 1.08, 1.34 },
                { 1.00, 1.16, 1.26, 1.54 },
                { 1.00, 1.22, 1.07, 1.03 },
                { 1.00, 0.93, 0.97, 0.92 },
                { 1.00, 1.11, 1.56, 1.52 },
                { 1.00, 0.76, 0.77, 0.90 },
                { 1.00, 0.92, 0.84, 1.01 },
                { 1.00, 0.88, 1.22, 1.34 }
            };

            double[] dates = { 0.0, 1.0, 2.0, 3.0 };

            var spot = new ArrayDoubleSpot(simcount, dates.Length);

            for (int i = 0; i < simcount; ++i)
                for (int j = 0; j < dates.Length; ++j)
                    spot[i, j] = spotValues[i, j];

            int datesCount = 365;
            double[] datesSched = new double[datesCount];
            for (int k = 0; k < datesCount; ++k) { datesSched[k] = (double)k / 365.0; }

            PrecalculatedSchedule dailySchedule = new PrecalculatedSchedule(datesSched);

            ConstantRateDiscounting discounting = new ConstantRateDiscounting(0.06, dailySchedule);

            AmericanPut product = new AmericanPut();
            product.Strike = 1.10;
            product.Spot = spot;

            var regressor = new QuadraticUnivariateRegressorStruct();

            var ls = new LongstaffSchwartz<ArrayDoubleSpot, ConstantRateDiscounting, AmericanPut, QuadraticUnivariateRegressorStruct, SequentialExecutor>();

            // inner data
            ls._dates = dates;
            ls._exercise = new alignedstorage_int(simcount);
            ls._premium = new alignedstorage_double(simcount);
            ls._simulationCount = simcount;
            ls._datesCount = dates.Length;

            // outer data
            ls.discounting = discounting;
            ls.observable = spot;
            ls.product = product;
            ls.regressor = regressor;
            ls.executor = new SequentialExecutor();

            //ls.Calculate();

            var cu = HybRunner.AVX("InFine.Quantfinance.Haswell.dll");
            cu.Marshaller.cleanUpNativeData = false;
            var lsEntryPoint = new LSEntryPoint();
            var wrapped = cu.Wrap(lsEntryPoint);
            Console.Out.WriteLine("Starting call");
            wrapped.CalculateSequential(ls, ls.GetHelperSerialReduction());
            Console.Out.WriteLine("Finished call");
            cu.Marshaller.CleanUpManagedData(ls.res);
            Console.Out.WriteLine("Cleanup call");
            double premium = ls.res[0] / ls._simulationCount;
            Console.Out.WriteLine("Premium = {0}, took {1}", premium, cu.LastKernelDuration.Elapsed.TotalMilliseconds);

            Assert.Equal(0.1144, premium, 0.00005);
        }

        [HybridizerFact]
        public void TestMethod_AmericanPut_LSPaper_CSharp()
        {
            int simcount = 8;

            double[,] spotValues = 
            { 
                { 1.00, 1.09, 1.08, 1.34 },
                { 1.00, 1.16, 1.26, 1.54 },
                { 1.00, 1.22, 1.07, 1.03 },
                { 1.00, 0.93, 0.97, 0.92 },
                { 1.00, 1.11, 1.56, 1.52 },
                { 1.00, 0.76, 0.77, 0.90 },
                { 1.00, 0.92, 0.84, 1.01 },
                { 1.00, 0.88, 1.22, 1.34 }
            };

            double[] dates = { 0.0, 1.0, 2.0, 3.0 };

            var spot = new ArrayDoubleSpot(simcount, dates.Length);

            for (int i = 0; i < simcount; ++i)
                for (int j = 0; j < dates.Length; ++j)
                    spot[i, j] = spotValues[i, j];

            int datesCount = 365;
            double[] datesSched = new double[datesCount];
            for (int k = 0; k < datesCount; ++k) { datesSched[k] = (double)k / 365.0; }

            PrecalculatedSchedule dailySchedule = new PrecalculatedSchedule(datesSched);

            ConstantRateDiscounting discounting = new ConstantRateDiscounting(0.06, dailySchedule);



            AmericanPut product = new AmericanPut();
            product.Strike = 1.10;
            product.Spot = spot;

            var regressor = new QuadraticUnivariateRegressorStruct();

            var ls = new LongstaffSchwartz<ArrayDoubleSpot, ConstantRateDiscounting, AmericanPut, QuadraticUnivariateRegressorStruct, SequentialExecutor>();

            // inner data
            ls._dates = dates;
            ls._exercise = new alignedstorage_int(simcount);
            ls._premium = new alignedstorage_double(simcount);
            ls._simulationCount = simcount;
            ls._datesCount = dates.Length;

            // outer data
            ls.discounting = discounting;
            ls.observable = spot;
            ls.product = product;
            ls.regressor = regressor;
            ls.executor = new SequentialExecutor();

            var sw = Stopwatch.StartNew();
            LSEntryPoint.CalculateSequential(ls, ls.GetHelperSerialReduction());
            sw.Stop();

            double premium = ls.res[0] / ls._simulationCount;
            Console.Out.WriteLine("Premium = {0}, took {1}", premium, sw.Elapsed.TotalMilliseconds);

            Assert.True(Math.Abs(premium - 0.1144) < 0.00005);
        }
    }
}
