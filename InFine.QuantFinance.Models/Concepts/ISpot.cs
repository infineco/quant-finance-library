using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Models.Concepts
{
    [HybridTemplateConcept]
    public interface ISpot
    {
        [Kernel] double this[int simId, int timeIndex] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; set; }
        [Kernel] double this[alignedindex simId, int timeIndex] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; set; }

        void Free();
    }

    [HybridRegisterTemplate(Specialize=typeof(LogBasedSpot<ArrayDoubleSpot>))]
    public class LogBasedSpot<LogSpot> : ISpot
            where LogSpot : ILogSpot
    {
        LogSpot _logSpot;

        public LogBasedSpot(LogSpot logSpot)
        {
            _logSpot = logSpot;
        }

        [Kernel]
        public double this[int simId, int timeIndex]
        {
            get { return Math.Exp(_logSpot[simId, timeIndex]); }
            set { _logSpot[simId, timeIndex] = Math.Log(value); }
        }

        [Kernel]
        public double this[alignedindex simId, int timeIndex]
        {
            get { return Math.Exp(_logSpot[simId, timeIndex]); }
            set { _logSpot[simId, timeIndex] = Math.Log(value); }
        }

        public void Free()
        {
            _logSpot.Free();
        }
    }

    public class Spot : ISpot
    {
        public double[,] values;
        // public double this[int simId, int time] { get { return values[simId, time]; } }

        public double this[int simId, int timeIndex]
        {
            get
            {
                return values[simId, timeIndex];
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public double this[alignedindex simId, int timeIndex]
        {
            get
            {
                return values[simId, timeIndex];
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public void Free()
        {
        }
    }
}
