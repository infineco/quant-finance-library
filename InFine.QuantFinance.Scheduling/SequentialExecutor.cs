using System;
using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Scheduling
{
    public class SequentialExecutor : IParallelExecutor
    {
        [Kernel]
        public void For(int from, int to, Action<alignedindex> functor)
        {
            for (alignedindex id = from + VectorUnit.ID; id < to; id += VectorUnit.Count)
            {
                functor(id);
            }
            VectorUnit.Barrier();
        }

        [Kernel]
        public double Reduce(alignedstorage_double array, int from, int to, Func<double, double, double> func, double neutral)
        {
            double res = neutral;
            
            if (Tools.EnforceSerial())
            {
                for (int k = from; k < to; ++k)
                {
                    res = func(res, array[k]);
                }
            }
            return res;
        }
    }
}