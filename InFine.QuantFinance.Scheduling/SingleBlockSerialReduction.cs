using System;
using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Scheduling
{
    public class SingleBlockSerialReduction<T> : Reduction where T : struct
    {
        public delegate void MapFunction(alignedindex idx, ref T target);
        public delegate void InitFunction(ref T target);
        private MapFunction mapFunc;
        private InitFunction initFunc;
        private T[] buffer;

        public SingleBlockSerialReduction(MapFunction mapFunc, InitFunction initFunc, T[] buffer)
        {
            this.mapFunc = mapFunc;
            this.initFunc = initFunc;
            this.buffer = buffer;
        }

        [Kernel]
        public void Reduce(int from, int to)
        {
            InternalReduce(from, to);
        }

        //[IntrinsicFunction("custom_avx_reduce", (int)HybridizerFlavor.AVX)]
        private void InternalReduce(int from, int to)
        {
            T t = default(T);
            initFunc(ref t);
            for (alignedindex id = from + VectorUnit.ID; id < to; id += VectorUnit.Count)
            {
                mapFunc(id, ref t);
            }
            buffer[0] = t;
            Tools.SyncThreads();
        }
    }
}
