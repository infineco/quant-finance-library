using System;
using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Scheduling
{
    public class SingleBlockParallelExecutor : IParallelExecutor
    {
        #region For
        
        [Kernel]
        public void For(int from, int to, Action<alignedindex> functor)
        {
            InnerFor(from, to, functor);
        }

        [Kernel]
        public void InnerFor(int from, int to, Action<alignedindex> functor)
        {
            for (alignedindex id = from + VectorUnit.ID; id < to; id += VectorUnit.Count)
            {
                functor(id);
            }
            VectorUnit.Barrier();
        }

        #endregion

        #region Reduce

        [Kernel]
        public void Reduce(int from, int to, Func<double, double, double> func, Func<alignedindex, double> mapFunc, double neutral, double[] buffer)
        {
            var sharedAlloc = new SharedMemoryAllocator<double>();
            var regArr = sharedAlloc.allocate(blockDim.x);

            Tools.SyncThreads();
            regArr[threadIdx.x] = neutral;
            for (alignedindex id = from + VectorUnit.ID; id < to; id += VectorUnit.Count)
            {
                regArr[threadIdx.x] = func(regArr[threadIdx.x], mapFunc(id));
            }

            Tools.SyncThreads();

            MultiBlockParallelExecutor.ReduceBlock(regArr);

            if (threadIdx.x != 0) return;

            buffer[blockIdx.x] = regArr[0];
        }

        // Scalar version
        public double Reduce(alignedstorage_double array, int @from, int to, Func<double, double, double> reduce, double neutral)
        {
            double accum = neutral;

            for (alignedindex id = from + VectorUnit.ID; id < to; id += VectorUnit.Count)
            {
                accum = reduce(accum, array[id]);
            }

            return accum;
        }



        #endregion
    }
}