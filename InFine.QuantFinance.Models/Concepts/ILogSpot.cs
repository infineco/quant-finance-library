using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Models.Concepts
{
    [HybridTemplateConcept]
    public interface ILogSpot
    {
        [Kernel] double this[int simId, int timeIndex] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; set; }
        [Kernel] double this[alignedindex simId, int timeIndex] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; set; }

        void Free();
    }

    /*
    public class ArrayDoubleSpot : ILogSpot
    {
        double[,] _backing;

        public ArrayDoubleSpot(int simCount, int datesCount)
        {
            _backing = new double[simCount, datesCount];
        }

        [Kernel]
        public double this[int simId, int timeIndex] { get { return _backing[simId, timeIndex]; } set { _backing[simId, timeIndex] = value; } }
        [Kernel]
        public double this[Scheduling.alignedindex simId, int timeIndex] { get { return _backing[simId.Inner, timeIndex]; } set { _backing[simId.Inner, timeIndex] = value; } }
    }
    */

    /* public class ArrayDoubleSpot : ILogSpot
    {
        int _simCount;
        double[] _backing;

        public ArrayDoubleSpot(int simCount, int datesCount)
        {
            _simCount = simCount;
            _backing = new double[simCount * datesCount];
        }

        [Kernel]
        public double this[int simId, int timeIndex] { get { return _backing[simId + _simCount * timeIndex]; } set { _backing[simId + _simCount * timeIndex] = value; } }
        [Kernel]
        public double this[Scheduling.alignedindex simId, int timeIndex] { get { return _backing[simId.Inner + _simCount * timeIndex]; } set { _backing[simId.Inner + _simCount * timeIndex] = value; } }
    } // */

    public unsafe class ArrayDoubleSpot : ILogSpot, ISpot
    {
        int _simCount;
        alignedstorage_double _backing;

        public ArrayDoubleSpot(int simCount, int datesCount)
        {
            _simCount = simCount;
            _backing = new alignedstorage_double(simCount * datesCount, GetType().Name);
        }

        [Kernel]
        public double this[int simId, int timeIndex]
        {
            get { return _backing[simId + _simCount * timeIndex]; }
            set { _backing[simId + _simCount * timeIndex] = value; }
        }

        [Kernel]
        public double this[alignedindex simId, int timeIndex]
        {
            get { return _backing[simId + (_simCount * timeIndex)]; }
            set { _backing[simId + (_simCount * timeIndex)] = value; }
        }

        public void Free()
        {
            _backing.destroy();
        }
    }

    public unsafe class ArrayDoubleSpotZeroCopy : ILogSpot, ISpot
    {
        int _simCount;
        alignedstorage_double_zerocopy _backing;

        public ArrayDoubleSpotZeroCopy(int simCount, int datesCount)
        {
            _simCount = simCount;
            _backing = new alignedstorage_double_zerocopy(simCount * datesCount, GetType().Name);
        }

        [Kernel]
        public double this[int simId, int timeIndex]
        {
            get { return _backing[simId + _simCount * timeIndex]; }
            set { _backing[simId + _simCount * timeIndex] = value; }
        }

        [Kernel]
        public double this[alignedindex simId, int timeIndex]
        {
            get { return _backing[simId + (_simCount * timeIndex)]; }
            set { _backing[simId + (_simCount * timeIndex)] = value; }
        }

        public void Free()
        {
            _backing.destroy();
        }
    }

    public unsafe class UnalignedArrayDoubleSpot : ILogSpot, ISpot
    {
        int _simCount;
        double[] _backing;

        public UnalignedArrayDoubleSpot(int simCount, int datesCount)
        {
            _simCount = simCount;
            _backing = new double[simCount * datesCount];
        }

        [Kernel]
        public double this[int simId, int timeIndex]
        {
            get { return _backing[simId + _simCount * timeIndex]; }
            set { _backing[simId + _simCount * timeIndex] = value; }
        }

        [Kernel]
        public double this[alignedindex simId, int timeIndex]
        {
            get { return _backing[simId + (_simCount * timeIndex)]; }
            set { _backing[simId + (_simCount * timeIndex)] = value; }
        }
        public void Free()
        {
        }
    }

}
