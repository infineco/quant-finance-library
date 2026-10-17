using InFine.QuantFinance.Scheduling;
using Hybridizer.Runtime.CUDAImports;
using System;
using System.Runtime.InteropServices;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Optimized version of the fixed cash flows pricer.
    /// 
    /// In this version, the interpolation of the yield curve, which is a linear interpolation in rate,
    /// is precalculated for performance improvement. As a result, overall simulation is split in two 
    /// parts:
    /// \begin{itemize}
    /// \item Precalculation of indices and interpolation weights for discounting
    /// \item Actual evaluation of payoff, which results in the exponential of a weighted average of 
    /// two values, with precalculated coefficients.
    /// \end{itemize}
    /// 
    /// This pricer is a benchmark, at it turns to be compute bound, memory bound or memory latency bound
    /// depending on the platform. 
    /// 
    /// The exact number of FLOPs depends on the implementation of exp, the bandwidth depends on the 
    /// order of operations, which relies on the indexing choice for the two loops, and the amount of 
    /// cache dedicated to each compute unit.
    /// </summary>
    // Not generated for any flavor: YieldCurve holds a float[] field, and Hybridizer 7.5 lays out struct fields with
    // Marshal.OffsetOf, which throws on Linux for array fields (SAFEARRAY marshalling exists on Windows only), so
    // generation fails with 0x6099 there. No test or benchmark launches this legacy pricer on a device.
    [HybridizerIgnore]
    public class OptimizedFixedCashFlowsPricer
    {
        /// <summary>
        /// The yield curbe
        /// </summary>
        public struct YieldCurve
        {
            /// <summary>
            /// Pillars of the yield curve, expressed in years
            /// </summary>
            public float[] Tenors;
            /// <summary>
            /// Actual value of the YC, one per tenor per simulation per curve pillar
            /// </summary>
            public alignedstorage_float Values;
            /// <summary>
            /// number of pillars in the Tenors curve
            /// </summary>
            public int TenorsCount;
         
            /// <summary>
            /// Number of simulations
            /// </summary>
            public int SimulationCount;

            /// <summary>
            /// \textbf{TODO} - for testing purposes
            /// </summary>
            /// <param name="rnd"></param>
            /// <param name="timePointCount"></param>
            /// <param name="simCount"></param>
            /// <param name="tenors"></param>
            /// <returns></returns>
            public static YieldCurve GenerateRand(JavaRandom rnd, int timePointCount, int simCount, float[] tenors)
            {
                var res = new YieldCurve();
                res.SimulationCount = simCount;
                res.Tenors = tenors;
                res.TenorsCount = tenors.Length;
                res.Values = new alignedstorage_float(timePointCount * simCount * tenors.Length);

                for (int k = 0; k < timePointCount * simCount * tenors.Length; ++k)
                {
                    res.Values[k] = -(float)(rnd.nextFloat() * 0.03 + 0.01);
                }

                return res;
            }

            /// <summary>
            /// \textbf{TODO} - for testing purposes
            /// </summary>
            /// <param name="rnd"></param>
            /// <param name="timePointCount"></param>
            /// <param name="simCount"></param>
            /// <param name="tenors"></param>
            /// <returns></returns>
            public unsafe static YieldCurve GenerateRand_CUDA(Random rnd, int timePointCount, int simCount, float[] tenors)
            {
                var res = new YieldCurve();
                res.SimulationCount = simCount;
                res.Tenors = tenors;
                res.TenorsCount = tenors.Length;
                // res.Values = new alignedstorage_float(timePointCount * simCount * tenors.Length);
                res.Values = new alignedstorage_float(timePointCount * simCount * tenors.Length);

                for (int k = 0; k < simCount * timePointCount; ++k)
                {
                    res.Values[k] = -(float)(rnd.NextDouble() * 0.03 + 0.01);
                }
                return res;
            }

            /// <summary>
            /// Returns the discount factor for given tenor, timePoint and simulation id.
            /// </summary>
            /// <param name="simId">The simulation id, between zero inclusive, and simulationCount exclusive</param>
            /// <param name="timePoint">The time index</param>
            /// <param name="tenor">The tenor, in years.</param>
            /// <returns></returns>
            public float GetDiscountFactor(int simId, int timePoint, float tenor)
            {
                int index = FindIndex(tenor);
                if (index == (TenorsCount - 1)) // beyond last point
                    return Expf(Values[simId + SimulationCount * (index + TenorsCount * timePoint)] * tenor);
                float t1 = Tenors[index];
                float t2 = Tenors[index + 1];
                // interpolate rate
                return Expf((
                    (Values[simId + SimulationCount * (index + TenorsCount * timePoint)] * (tenor - t1) / (t2 - t1)) +
                    (Values[simId + SimulationCount * (index + 1 + TenorsCount * timePoint)] * (t2 - tenor) / (t2 - t1))) * tenor);
            }

            /// <summary>
            /// System.Math does not expose the exp function for floats. This intrinsic method is transformed in an 
            /// optimized version for single precision exp function.
            /// </summary>
            /// <param name="x">Value of which exp is to be obtained.</param>
            /// <returns>$$ res = e^x $$</returns>
            [IntrinsicFunction("hybridizer::exp", IsNaked = true, Flavor = 3)]
            private static float Expf(float x)
            {
                return (float)Math.Exp((double)x);
            }

            /// <summary>
            /// Precalculates the discount factor index and weights for fast evaluation.
            /// The indexing and weight does not depend on the Monte Carlo simulation index, hence these 
            /// interpolation parameters may be evaluated only once for all simulations. 
            /// </summary>
            /// <param name="timePoint"></param>
            /// <param name="tenor"></param>
            /// <param name="idx"></param>
            /// <param name="indexLeft"></param>
            /// <param name="indexRight"></param>
            /// <param name="weightLeft"></param>
            /// <param name="weightRight"></param>
            public void GetDiscountFactorIndexAndWeights(int timePoint, float tenor, int idx,
                int[] indexLeft, int[] indexRight, float[] weightLeft, float[] weightRight)
            {
                int index = FindIndex(tenor);
                if (index == (TenorsCount - 1)) // beyond last point
                {
                    indexLeft[idx] = SimulationCount * (index + TenorsCount * timePoint);
                    weightLeft[idx] = 1.0f;
                    indexRight[idx] = SimulationCount * (index + TenorsCount * timePoint);
                    weightRight[idx] = 0.0f;
                }
                else
                {
                    float t1 = Tenors[index];
                    float t2 = Tenors[index + 1];
                    indexLeft[idx] = SimulationCount * (index + TenorsCount * timePoint);
                    weightLeft[idx] = (tenor - t1) / (t2 - t1);
                    indexRight[idx] = SimulationCount * (1 + index + TenorsCount * timePoint);
                    weightRight[idx] = (t2 - tenor) / (t2 - t1);
                }
            }

            private int FindIndex(float tenor)
            {
                int left = 0;
                int right = TenorsCount - 1;
                if (tenor <= Tenors[left]) return left;
                if (tenor >= Tenors[right]) return right;
                while ((right - left) > 1)
                {
                    int mid = (left + right) >> 1;
                    if (Tenors[mid] > tenor) right = mid;
                    else left = mid;
                }
                return left;
            }

        }

        int _cashFlowsCount;// Numbers of cash flows
        float[] _paymentDate; // Cash flow dates
        float[] _values; // Values of cash flow (1 per date)
        YieldCurve _yc;

        int[] indexLeft; // Size timepointCount * cashFlowCount
        int[] indexRight;
        float[] weightLeft;
        float[] weightRight;

        /// <summary>
        /// Storage for result values.
        /// </summary>
        public alignedstorage_float result;

        int timePointCount;
        int simulationCount;
        float[] timePoints;

        /// <summary>
        /// Precalculate the interpolation coefficients of the yield curve.
        /// </summary>
        [EntryPoint("FixedCashFlowsPricerOptimized_Precompute")]
        public void Precompute()
        {
            for (int timePoint = blockIdx.x; timePoint < timePointCount; timePoint += gridDim.x)
            {
                for (int cf = threadIdx.x; cf < _cashFlowsCount; cf += blockDim.x)
                {
                    int index = cf + _cashFlowsCount * timePoint;
                    _yc.GetDiscountFactorIndexAndWeights(timePoint, _paymentDate[cf], index, indexLeft, indexRight,
                        weightLeft, weightRight);
                }
            }
        }

        /// <summary>
        /// Actual simulation.
        /// </summary>
        /// <param name="simFrom"></param>
        /// <param name="simTo"></param>
        [EntryPoint("FixedCashFlowsPricerOptimized_Calculate")]
        public void Calculate(int simFrom, int simTo)
        {
            float price;
            float vl, vr;
            float wl;
            float wr;
            int il;
            int ir;
            float dt;
            float v;

            alignedstorage_float res = result;

            alignedstorage_float ycvalues = _yc.Values;
            for (int timePoint = blockIdx.x; timePoint < timePointCount; timePoint += gridDim.x)
            {
                float simulDate = timePoints[timePoint];
                for (alignedindex simId = VectorUnit.ID + simFrom ; simId < simTo; simId += VectorUnit.Count)
                {
                    price = 0.0f;
                    alignedindex resIdx = simId + simulationCount * timePoint;
                    for (int k = 0; k < _cashFlowsCount; ++k)
                    {
                        if (simulDate >= _paymentDate[k]) continue;
                        dt = _paymentDate[k] - simulDate;
                        v = _values[k];

                        int index = k + _cashFlowsCount * timePoint;
                        il = indexLeft[index];
                        ir = indexRight[index];
                        wl = weightLeft[index];
                        wr = weightRight[index];

                        vl = ycvalues[simId + il];
                        vr = ycvalues[simId + ir];
                        float cfNPV = v * Expf((vl * wl + vr * wr) * dt);
                        price += cfNPV;
                    }

                    res[resIdx] = price;
                }
            }
        }

        [IntrinsicFunction("hybridizer::exp", IsNaked = true, Flavor = 3)]
        private static float Expf(float p)
        {
            return (float)Math.Exp((double)p);
        }

        /// <summary>
        /// \textbf{TODO} - for testing purposes only
        /// </summary>
        /// <param name="timePointCount"></param>
        /// <param name="simCount"></param>
        /// <param name="timePoints"></param>
        /// <param name="tenors"></param>
        /// <param name="cashFlows"></param>
        /// <returns></returns>
        public static OptimizedFixedCashFlowsPricer Build(int timePointCount, int simCount, 
            float[] timePoints,
            float[] tenors,
            float[] cashFlows)
        {
            var rand = new JavaRandom(42);
            var res = new OptimizedFixedCashFlowsPricer();
            res._yc = YieldCurve.GenerateRand(rand, timePointCount, simCount, tenors);
            res._cashFlowsCount = cashFlows.Length;
            res._paymentDate = cashFlows;
            res._values = new float[cashFlows.Length];
            for (int k = 0; k < cashFlows.Length; ++k)
            {
                res._values[k] = rand.nextFloat() * 1000.0f;
            }

            Console.WriteLine("Last cash flow: {0}@{1}", res._values[cashFlows.Length - 1], cashFlows.Length - 1);

            res.indexLeft = new int[timePointCount * cashFlows.Length];
            res.indexRight = new int[timePointCount * cashFlows.Length];
            res.weightLeft = new float[timePointCount * cashFlows.Length];
            res.weightRight = new float[timePointCount * cashFlows.Length];

            res.result = new alignedstorage_float(timePointCount * simCount);
            res.simulationCount = simCount;
            res.timePointCount = timePointCount;
            res.timePoints = timePoints;
            return res;
        }

        /// <summary>
        /// \textbf{TODO} - for testing purposes only
        /// </summary>
        public static unsafe OptimizedFixedCashFlowsPricer Build_CUDA(int timePointCount, int simCount,
            float[] timePoints,
            float[] tenors,
            float[] cashFlows)
        {
            var rand = new Random(1978);
            var res = new OptimizedFixedCashFlowsPricer();
            res._yc = YieldCurve.GenerateRand_CUDA(rand, timePointCount, simCount, tenors);
            res._cashFlowsCount = cashFlows.Length;
            res._paymentDate = cashFlows;
            res._values = new float[cashFlows.Length];
            for (int k = 0; k < cashFlows.Length; ++k)
            {
                res._values[k] = (float)rand.NextDouble() * 1000.0f;
            }

            res.indexLeft = new int[timePointCount * cashFlows.Length];
            res.indexRight = new int[timePointCount * cashFlows.Length];
            res.weightLeft = new float[timePointCount * cashFlows.Length];
            res.weightRight = new float[timePointCount * cashFlows.Length];

            res.result = new alignedstorage_float(timePointCount * simCount);
            res.simulationCount = simCount;
            res.timePointCount = timePointCount;
            res.timePoints = timePoints;
            return res;
        }

    }
}
