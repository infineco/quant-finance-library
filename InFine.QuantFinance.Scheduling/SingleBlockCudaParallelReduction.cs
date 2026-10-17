using System;
using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Scheduling
{
    [HybridTemplateConcept]
    [HybridizerIgnore("OMP,AVX,VSX")]
    public interface Reductor<T> where T : struct
    {
        [Kernel]
        T ReduceWarp(ref T input);
    }

    [HybridizerIgnore("OMP,AVX,VSX")]
    public class DoubleReductor : Reductor<double>
    {
        [Kernel, HybridizerIgnore("OMP,AVX,VSX")]
        public double ReduceWarp(ref double input)
        {
            return Tools.reduce_warp_add_double(input);
        }
    }

    [HybridizerIgnore("OMP,AVX,VSX")]
    public class SingleBlockCudaParallelReduction<T, R> : Reduction
        where T : struct
        where R : Reductor<T>, new()
    {
        public delegate void MapFunction(alignedindex idx, ref T target);
        public delegate void InitFunction(ref T target);
        private R reductor;
        private MapFunction mapFunc;
        private InitFunction initFunc;
        private T[] buffer;

        public SingleBlockCudaParallelReduction(MapFunction mapFunc, InitFunction initFunc, T[] buffer)
        {
            this.reductor = new R();
            this.mapFunc = mapFunc;
            this.initFunc = initFunc;
            this.buffer = buffer;
        }

        [Kernel]
        [HybridizerIgnore("OMP,AVX,VSX")]
        public void Reduce(int from, int to)
        {
            Tools.SyncThreads();
            T t = default(T);
            initFunc(ref t);
            for (alignedindex id = from + VectorUnit.ID; id < to; id += VectorUnit.Count)
            {
                mapFunc(id, ref t);
            }

            Tools.SyncThreads();

            int lane = threadIdx.x % VectorUnit.WarpSize;
            int wid = threadIdx.x / VectorUnit.WarpSize;

            // Each warp performs partial reduction
            t = reductor.ReduceWarp(ref t);
            //Tools.SyncThreads();

            var sharedAlloc = new SharedMemoryAllocator<T>();
            var regArr = sharedAlloc.allocate(VectorUnit.WarpSize); // warp size
            if (lane == 0)
            {
                regArr[wid] = t;
            }
            Tools.SyncThreads();

            if (threadIdx.x < blockDim.x / VectorUnit.WarpSize)
            {
                t = regArr[lane];
            }
            else
            {
                initFunc(ref t);
            }
            t = reductor.ReduceWarp(ref t);
            if (threadIdx.x == 0)
            {
                buffer[0] = t;
            }
        }
    }
}
