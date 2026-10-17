using System;
using InFine.QuantFinance.Models.Concepts;
using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.LongstaffSchwartz
{
    public class AmericanPut : ICashFlowSource
    {
        [Kernel]

        public double Strike { get; set; }
        [Kernel]
        public ISpot Spot { get; set; }

        [Kernel]
        public double FlowIfExercise(alignedindex simId, int time)
        {
            return Math.Max(0.0, Strike - Spot[simId, time]);
        }
    }

    public class AmericanCall<SpotType> : ICashFlowSource where SpotType : ISpot
    {
        [Kernel]
        public double Strike { get; set; }

        [Kernel]
        public SpotType Spot { get; set; }

        [Kernel]
        public double FlowIfExercise(alignedindex simId, int time)
        {
            return Math.Max(0.0, Spot[simId, time] - Strike);
        }
    }
}
