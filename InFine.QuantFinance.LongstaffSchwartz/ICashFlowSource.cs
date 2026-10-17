using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.LongstaffSchwartz
{
    public interface ICashFlowSource
    {
        double FlowIfExercise(alignedindex simId, int timeIndex);
    }
}
