using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Scheduling
{
    public interface IParallelExecutor
    {
        /// Should only be called by a single thread !
        [Kernel]
        void For (int from, int to, Action<alignedindex> functor);
        /// Should only be called by a single thread !
        [Kernel]
        double Reduce(alignedstorage_double array, int from, int to, Func<double, double, double> reduce, double neutral);
    }

    public interface Reduction
    {
        [Kernel]
        void Reduce(int from, int to);
    }
}
