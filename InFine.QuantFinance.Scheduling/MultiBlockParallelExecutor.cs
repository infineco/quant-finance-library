using System;
using System.Threading.Tasks;
using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Scheduling
{
    public class MultiBlockParallelExecutor : IParallelExecutor
    {
        private int retirementCount;
        #region For

        //[Kernel]
        public void For(int from, int to, Action<alignedindex> functor)
        {
            Parallel.For(from, to, x => functor((alignedindex)x));
        }

        public double Reduce(alignedstorage_double array, int @from, int to, Func<double, double, double> reduce, double neutral)
        {
            throw new NotImplementedException();
        }

        [EntryPoint]
        public static void InnerFor(int from, int to, Action<alignedindex> functor)
        {
            for (alignedindex id = from + VectorUnit.ID + VectorUnit.Count * blockIdx.x; id < to; id += VectorUnit.Count * gridDim.x)
            {
                functor(id);
            }
        }

        #endregion

        #region Reduce


        internal static void ReduceBlock(double[] regArr)
        {
            for (int offset = blockDim.x / 2;
                offset > 0;
                offset >>= 1)
            {
                if (threadIdx.x < offset)
                {
                    regArr[threadIdx.x] = regArr[threadIdx.x] + regArr[threadIdx.x + offset];
                }

                Tools.SyncThreads();
            }
        }

        [EntryPoint]
        public void Reduce(int from, int to, Func<double, double, double> func, Func<alignedindex, double> mapFunc, double neutral, double[] buffer)
        {
            var sharedAlloc = new SharedMemoryAllocator<double>();
            var regArr = sharedAlloc.allocate(blockDim.x);

            Tools.SyncThreads();
            regArr[threadIdx.x] = neutral;
            for (alignedindex id = from + VectorUnit.ID + VectorUnit.Count * blockIdx.x; id < to; id += VectorUnit.Count * gridDim.x)
            {
                regArr[threadIdx.x] = func(regArr[threadIdx.x], mapFunc(id));
            }

            Tools.SyncThreads();

            ReduceBlock(regArr);

            if (threadIdx.x != 0) return;

            buffer[blockIdx.x] = regArr[0];

            if (blockDim.x == 1) return;

            {
                bool amLast = false;

                Tools.ThreadFence();

                if (threadIdx.x == 0)
                {
                    int ticket = Tools.atomicInc(ref retirementCount, gridDim.x);
                    amLast = (ticket == (gridDim.x - 1));
                }

                if (amLast) // only occurs on one block one thread
                {
                    for (int i = 1; i < gridDim.x; ++i)
                        buffer[0] += buffer[i];
                    retirementCount = 0;
                }
            }
        }

        #endregion
    }

    public class MultiBlockParallelExecutorT
    {
        [EntryPoint]
        public static void Reduce(int from, int to, Reduction reduction)
        {
            reduction.Reduce(from, to);
        }
    }
}
