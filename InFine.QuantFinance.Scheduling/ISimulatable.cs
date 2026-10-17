using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Scheduling
{
    public interface ISimulatable
    {
        [Kernel]
        void Simulate(int simFrom, int simTo);
    }

    public class SimulatableScheduler
    {
        [EntryPoint("SimulatableScheduler_SimulateSingle")]
        public static void SimulateSingle(int simFrom, int simTo, ISimulatable simulatable)
		{	
			// 64-bit products: N * blockIdx.x overflows an int as soon as paths x blocks exceeds 2^31
			// (1M paths on 2148 blocks), which gave negative chunk bounds and skipped paths.
			long N = simTo - simFrom ;
			int start = (int)((N * blockIdx.x) / gridDim.x) ;
			int stop = (int)((N * (blockIdx.x + 1)) / gridDim.x) ;
			simulatable.Simulate(start + simFrom, stop + simFrom);
			
			//#if false
			//// should be better -- experience showed it's not. 
			//int Nch = ((simTo - simFrom) + (blockDim.x - 1)) / blockDim.x ; // nb chunks of blockDim.x to cover sim span
			//int start = (Nch * blockIdx.x) / gridDim.x ;
			//int stop = (Nch * (blockIdx.x + 1)) / gridDim.x ;
			//
			//start *= blockDim.x ;
			//stop *= blockDim.x ;
			//
			//if (stop + simFrom > simTo) stop = simTo;
			//simulatable.Simulate(start + simFrom, stop + simFrom);
			//#endif
        }

		[EntryPoint("SimulatableScheduler_SimulateGroup")]
        public static void SimulateGroup(int simFrom, int simTo, int count, ISimulatable[] simulatable)
        {
            for (int k = blockIdx.x ; k < count ; k += gridDim.x)
                simulatable[k].Simulate(simFrom, simTo);
        }
    }
}
