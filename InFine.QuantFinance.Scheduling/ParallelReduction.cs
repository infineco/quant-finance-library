using System;
using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Scheduling
{
    // Not generated for any flavor: Action<int, int, T[]> makes Hybridizer 7.5 emit a class deriving from
    // ::System::Array, which nvrtc rejects, and nothing reaches this class from device code (LongstaffSchwartz uses
    // SingleBlockCudaParallelReduction and SingleBlockSerialReduction).
    [HybridizerIgnore]
    public class ParallelReduction<T> : Reduction where T : struct
    {
        /// <summary>
        /// Reduction step taking both operands by reference (see
        /// http://stackoverflow.com/questions/1707310/is-it-possible-to-use-ref-types-in-c-sharp-built-in-action-delegate).
        /// </summary>
        /// <param name="accum">running value, updated in place.</param>
        /// <param name="adder">value folded into <paramref name="accum"/>.</param>
        public delegate void ReduceRefFuncType(ref T accum, ref T adder);

        public delegate void MapFunction(alignedindex idx, ref T target);
        public delegate void InitFunction(ref T target);
        private Action<int, int, T[]> reduceFunc;
        private MapFunction mapFunc;
        private InitFunction initFunc;
        private T[] buffer;

        public ParallelReduction(Action<int, int, T[]> reduceFunc, MapFunction mapFunc, InitFunction initFunc, T[] buffer)
        {
            this.reduceFunc = reduceFunc;
            this.mapFunc = mapFunc;
            this.initFunc = initFunc;
            this.buffer = buffer;
        }

        [Kernel]
        internal static void ReduceBlock(T[] regArr, Action<int, int, T[]> reduceFunc)
        {
            for (int offset = blockDim.x / 2;
                offset > 0;
                offset >>= 1)
            {
                if (threadIdx.x < offset)
                {
                    reduceFunc(threadIdx.x, threadIdx.x + offset, regArr);
                }

                Tools.SyncThreads();
            }
        }

        [IntrinsicFunction("hybridizer::shuffledown<>")]
        private static T ShuffleDown(T t, int shift)
        {
            return t;
        }

        [Kernel]
        internal static T ReduceBlockShuffle(T t, ReduceRefFuncType reduceFunc)
        {
            var sharedAlloc = new SharedMemoryAllocator<T>();
            var regArr = sharedAlloc.allocate(blockDim.x / 32);

            T acc = t;
            // shuffle reduction
            for (int offset = VectorUnit.WarpSize / 2; offset > 0; offset >>= 1)
            {
                T tmp = ShuffleDown(acc, offset);
                reduceFunc(ref acc, ref tmp);
            }

            // shared reduction
            if ((threadIdx.x & 31) == 0)
            {
                regArr[threadIdx.x >> 5] = t;
            }
            Tools.SyncThreads();

            for (int offset = blockDim.x / (2 * VectorUnit.WarpSize);
                offset > 0;
                offset >>= 1)
            {
                Tools.SyncThreads();
                if (threadIdx.x < offset)
                {
                    reduceFunc(ref regArr[threadIdx.x], ref regArr[threadIdx.x + offset]);
                }
            }
            Tools.SyncThreads();
            return regArr[0];
        }

        [Kernel, HybridizerIgnore]
        public void Reduce(int from, int to)
        {
            // Intentionally empty: the multi-block reduction was never completed (HYB-527, vectorization of the accumulator).
            // Kept only to satisfy Reduction; use SingleBlockCudaParallelReduction.
        }
    }
}