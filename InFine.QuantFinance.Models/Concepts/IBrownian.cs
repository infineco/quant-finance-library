using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Models.Concepts
{
    [HybridTemplateConcept]
    public interface IBrownian
    {
        [Kernel]
        double this[int simId, int timeIndex] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; set; }
        [Kernel]
        double this[alignedindex simId, int timeIndex] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; set; }
    }

    public unsafe class ArrayBrownian : IBrownian
    {
        int _simCount;
        alignedstorage_double _backing;

        public double* Backing { get { return (double*)_backing.HostPointer.ToPointer(); } }

        public ArrayBrownian(int simCount, int datesCount)
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
    }

    public unsafe class UnalignedArrayBrownian : IBrownian
    {
        int _simCount;
        double[] _backing;

        [HybridizerIgnore]
        System.Runtime.InteropServices.GCHandle _gch = new System.Runtime.InteropServices.GCHandle();

        [HybridizerIgnore]
        public double* Backing 
        { 
            get 
            { 
                if (!(_gch.IsAllocated))
                    _gch = System.Runtime.InteropServices.GCHandle.Alloc(_backing, System.Runtime.InteropServices.GCHandleType.Pinned);
                return (double*)(System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(_backing, 0).ToPointer());
            } 
        }

        public UnalignedArrayBrownian(int simCount, int datesCount)
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
    }
}
