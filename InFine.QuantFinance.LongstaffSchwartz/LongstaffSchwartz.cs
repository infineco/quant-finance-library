using System;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Scheduling;
using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.LongstaffSchwartz
{
    /// <summary>
    /// The map and reduction actions of one Longstaff-Schwartz time step, kept together so that they can be handed to a device.
    /// </summary>
    public class LSHelper
    {
        public Action<alignedindex> initialMapAction;
        public Action<alignedindex> applyRegressorAction;
        public Reduction regressorReduction;
        public Reduction finalReduction;
    }

    [HybridRegisterTemplate(Specialize = typeof(SingleBlockCudaParallelReduction<QuadraticUnivariateRegressorStruct.RegressorData, QuadraticUnivariateRegressorStructDataReductor>))]
    [HybridRegisterTemplate(Specialize = typeof(SingleBlockCudaParallelReduction<double, DoubleReductor>))]
    [HybridRegisterTemplate(Specialize = typeof(LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>))]
    [HybridRegisterTemplate(Specialize = typeof(LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpotZeroCopy>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpotZeroCopy>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>))]
    [HybridRegisterTemplate(Specialize = typeof(SingleBlockSerialReduction<QuadraticUnivariateRegressorStruct.RegressorData>))]
    [HybridRegisterTemplate(Specialize = typeof(SingleBlockSerialReduction<double>))]
    [HybridRegisterTemplate(Specialize = typeof(LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanPut, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>))]
    [HybridRegisterTemplate(Specialize = typeof(LongstaffSchwartz<ArrayDoubleSpot, ConstantRateDiscounting, AmericanPut, QuadraticUnivariateRegressorStruct, SequentialExecutor>))]
    [HybridRegisterTemplate(Specialize = typeof(LongstaffSchwartz<ArrayDoubleSpot, ConstantRateDiscounting, AmericanPut, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>))]
    public class LongstaffSchwartz<Spot, Discounting, CashFlowSource, Regressor, ParallelExecutor>
        where Spot : ISpot
        where Discounting : IDiscounting
        where CashFlowSource : ICashFlowSource
        where Regressor : IRegressor<double, QuadraticUnivariateRegressorStruct.RegressorData>
        where ParallelExecutor : IParallelExecutor
    {

        [Kernel]
        public void initialMap(alignedindex simId)
        {
            _exercise[simId] = _datesCount - 1;
            _premium[simId] = product.FlowIfExercise(simId, _datesCount - 1);

            //Console.Out.WriteLine("## {0}:{1} @ {2}", (int)simId, _premium[simId], _exercise[simId]);
        }

        [Kernel]
        public void reduceRegressor(int i, int j, QuadraticUnivariateRegressorStruct.RegressorData[] t)
        {
            t[i].Add(ref t[j]);
        }

        [Kernel]
        public void mapRegressor([HybridVectorizer] alignedindex simId, ref QuadraticUnivariateRegressorStruct.RegressorData t)
        {
            double x;
            double y;
            double count;
            if (product.FlowIfExercise(simId, timeIndex) > 0.0)
            {
                x = observable[simId, timeIndex];
                y = _premium[simId] * discounting[simId, _dates[_exercise[simId]] - _dates[timeIndex]];
                count = Tools.reduce_constant(x, 1.0);
            }
            else
            {
                x = 0;
                y = 0;
                count = Tools.reduce_constant(x, 0.0);
            }
            //Console.WriteLine("{0}: {1}, {2}", (int) simId, x, y);
            t.Add(simId, x, y, count);
        }

        [Kernel]
        public void initRegressor(ref QuadraticUnivariateRegressorStruct.RegressorData t)
        {
            t.Init(_simulationCount);
        }

        [Kernel]
        public void solveRegressor(alignedindex simd)
        {
            if (simd == 0)
            {
                regressor.SetData(buffer[0]);
                regressor.Solve();
            }
        }

        [Kernel]
        public void applyRegressor([HybridVectorizer] alignedindex simId)
        {
            //Console.Out.WriteLine("Before {0}:{1} @ {2}", (int)simId, _premium[simId], _exercise[simId]);
            double exerciseValue = product.FlowIfExercise(simId, timeIndex);
            if (exerciseValue <= 0.0) return;
            double continuationValue = regressor.Evaluate(observable[simId, timeIndex]);
            if (!Double.IsNaN(continuationValue) && exerciseValue > continuationValue)
            {
                // exercise now
                _exercise[simId] = timeIndex;
                _premium[simId] = exerciseValue;
            }
            //Console.Out.WriteLine("After {0}:{1} @ {2} - continuation value {3}", (int)simId, _premium[simId], _exercise[simId], continuationValue);
        }

        [Kernel]
        public void finalReduce(int i, int j, double[] t)
        {
            t[i] += t[j];
        }

        [Kernel]
        public void finalMap(alignedindex simId, ref double res)
        {
            res += Tools.reduce_add(_premium[simId] * discounting[simId, _dates[_exercise[simId]]]);
        }

        [Kernel]
        public void finalInit(ref double res)
        {
            res = 0.0;
        }


        public int _simulationCount;
        public alignedstorage_double _premium;
        public alignedstorage_int _exercise;

        public int _datesCount;
        public double[] _dates;

        public Spot observable;
        public Discounting discounting;
        public CashFlowSource product;
        public Regressor regressor;
        public ParallelExecutor executor;
        public int timeIndex;
        public double[] res = new double[1];

        public QuadraticUnivariateRegressorStruct.RegressorData[] buffer;


        [HybridizerIgnore]
        public LSHelper GetHelperSerialReduction()
        {
            buffer = new QuadraticUnivariateRegressorStruct.RegressorData[1];
            var helper = new LSHelper();
            helper.initialMapAction = initialMap;
            helper.applyRegressorAction = applyRegressor;
            helper.regressorReduction = new SingleBlockSerialReduction<QuadraticUnivariateRegressorStruct.RegressorData>(mapRegressor, initRegressor, buffer);
            helper.finalReduction = new SingleBlockSerialReduction<double>(finalMap, finalInit, res);
            return helper;
        }

        [HybridizerIgnore]
        public LSHelper GetHelperParallelReduction()
        {
            buffer = new QuadraticUnivariateRegressorStruct.RegressorData[1];
            var helper = new LSHelper();
            helper.initialMapAction = initialMap;
            helper.applyRegressorAction = applyRegressor;
            //helper.regressorReduction = new SingleBlockParallelReduction<QuadraticUnivariateRegressorStruct.RegressorData>(reduceRegressor, mapRegressor, initRegressor, buffer);
            helper.regressorReduction = new SingleBlockCudaParallelReduction<QuadraticUnivariateRegressorStruct.RegressorData, QuadraticUnivariateRegressorStructDataReductor>(mapRegressor, initRegressor, buffer);
            // helper.finalReduction = new SingleBlockParallelReduction<double>(finalReduce, finalMap, finalInit, res);
            helper.finalReduction = new SingleBlockCudaParallelReduction<double, DoubleReductor>(finalMap, finalInit, res);
            return helper;
        }

        [Kernel]
        public void Calculate(LSHelper helper)
        {
#if METER
            long time, prev;
            prev = Tools.Clock64();
#endif

            // init with exercise at last date -- undiscounted !
            // for (int simId = 0; simId < simcount; ++simId)
            executor.For(0, _simulationCount, helper.initialMapAction);

#if METER
            time = Tools.Clock64();

            if (blockIdx.x == 0)
                if (Tools.EnforceSerial())
                    Console.Out.WriteLine("init took {0} cycles", (int)(time - prev));
#endif

            for (int timeIndex = _datesCount - 2; timeIndex >= 1; --timeIndex)
            {
#if METER
                prev = Tools.Clock64();
#endif

                if (Tools.EnforceSerial())
                    this.timeIndex = timeIndex;

                VectorUnit.Barrier();

                helper.regressorReduction.Reduce(0, _simulationCount);

#if METER
                time = Tools.Clock64();

                if (blockIdx.x == 0)
                    if (Tools.EnforceSerial())
                        Console.Out.WriteLine("helper.regressorReduction.Reduce took {0} cycles", (int)(time - prev));

                prev = Tools.Clock64();
#endif


                VectorUnit.Barrier();

                if (Tools.EnforceSerial())
                {
                    // solve regressor
                    regressor.SetData(buffer[0]);
                    regressor.Solve();
                    //Console.Write("{0}: {1}", timeIndex, regressor);
                }

#if METER
                time = Tools.Clock64();

                if (blockIdx.x == 0)
                    if (Tools.EnforceSerial())
                        Console.Out.WriteLine("solve.regressor took {0} cycles", (int)(time - prev));

                prev = Tools.Clock64();
#endif


                VectorUnit.Barrier();

                // then, check if relevant to exercise now
                // for (int simId = 0; simId < simcount; ++simId)
                executor.For(0, _simulationCount, helper.applyRegressorAction);

#if METER
                time = Tools.Clock64();

                if (blockIdx.x == 0)
                    if (Tools.EnforceSerial())
                        Console.Out.WriteLine("executor took {0} cycles", (int)(time - prev));
#endif
            }
            Tools.SyncThreads();

#if METER
            prev = Tools.Clock64();
#endif

            helper.finalReduction.Reduce(0, _simulationCount);

#if METER
            time = Tools.Clock64();

            if (blockIdx.x == 0)
                if (Tools.EnforceSerial())
                    Console.Out.WriteLine("finalReduction took {0} cycles", (int)(time - prev));
#endif
        }
    }

    public class LSEntryPoint
    {
        [EntryPoint]
        public static void Calculate(
            LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor> ls, LSHelper helper)
        {
            ls.Calculate(helper);
        }

        [EntryPoint]
        public static void CalculateMany(
            LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>[] lss, LSHelper[] helpers, int nb)
        {
            for (int i = blockIdx.x; i < nb; i += gridDim.x)
            {
                var ls = lss[i];
                LSHelper helper = helpers[i];
                ls.Calculate(helper);
            }
        }

        [EntryPoint]
        public static void CalculateManyZC(
            LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpotZeroCopy>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpotZeroCopy>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>[] lss, LSHelper[] helpers, int nb)
        {
            for (int i = blockIdx.x; i < nb; i += gridDim.x)
            {
                var ls = lss[i];
                LSHelper helper = helpers[i];
                ls.Calculate(helper);
            }
        }

        [EntryPoint]
        public static void Calculate_Paper(
            LongstaffSchwartz<ArrayDoubleSpot, ConstantRateDiscounting, AmericanPut, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor> ls, LSHelper helper)
        {
            ls.Calculate(helper);
        }

        [EntryPoint]
        public static void CalculateSequential(LongstaffSchwartz<ArrayDoubleSpot, ConstantRateDiscounting, AmericanPut, QuadraticUnivariateRegressorStruct, SequentialExecutor> ls, LSHelper helper)
        {
            ls.Calculate(helper);
        }

        [EntryPoint("Manual_LS"), HybridizerIgnore]
        public static void Manual_LS(
            LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>[] lss, LSHelper[] helpers, int nb)
        {
            for (int i = blockIdx.x; i < nb; i += gridDim.x)
            {
                var ls = lss[i];
                LSHelper helper = helpers[i];
                ls.Calculate(helper);
            }
        }

        [EntryPoint("Manual_LS_ZC"), HybridizerIgnore]
        public static void Manual_LS_ZC(
            LongstaffSchwartz<LogBasedSpot<ArrayDoubleSpot>, ConstantRateDiscounting, AmericanCall<LogBasedSpot<ArrayDoubleSpot>>, QuadraticUnivariateRegressorStruct, SingleBlockParallelExecutor>[] lss, LSHelper[] helpers, int nb)
        {
            for (int i = blockIdx.x; i < nb; i += gridDim.x)
            {
                var ls = lss[i];
                LSHelper helper = helpers[i];
                ls.Calculate(helper);
            }
        }
    }
}
