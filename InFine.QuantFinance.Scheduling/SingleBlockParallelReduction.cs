using System;
using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Scheduling
{
    // Not generated for any flavor, for the same reason as ParallelReduction<T> (Action<int, int, T[]>); unused on device.
    [HybridizerIgnore]
    public class SingleBlockParallelReduction<T> : Reduction where T : struct
    {
        public delegate void MapFunction(alignedindex idx, ref T target);
        public delegate void InitFunction(ref T target);
        private Action<int, int, T[]> reduceFunc;
        private MapFunction mapFunc;
        private InitFunction initFunc;
        private T[] buffer;

        public SingleBlockParallelReduction(Action<int, int, T[]> reduceFunc, MapFunction mapFunc, InitFunction initFunc, T[] buffer)
        {
            this.reduceFunc = reduceFunc;
            this.mapFunc = mapFunc;
            this.initFunc = initFunc;
            this.buffer = buffer;
        }

        [Kernel, HybridizerIgnore("AVX,OMP,VSX")]
        public void Reduce(int from, int to)
        {
            var sharedAlloc = new SharedMemoryAllocator<T>();
            var regArr = sharedAlloc.allocate(blockDim.x);

            Tools.SyncThreads();
            T t = default(T);
            initFunc(ref t);
            for (alignedindex id = from + VectorUnit.ID; id < to; id += VectorUnit.Count)
            {
                mapFunc(id, ref t);
            }
            regArr[threadIdx.x] = t;

            Tools.SyncThreads();

            ParallelReduction<T>.ReduceBlock(regArr, reduceFunc);

            if (threadIdx.x == 0)
            {
                buffer[0] = regArr[0];
            }
            Tools.SyncThreads();
        }
    }
}
