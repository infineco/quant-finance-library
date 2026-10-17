using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Models.Concepts
{
    [HybridTemplateConcept]
    public interface IRate
    {
        [Kernel] double this[int simId, int timeIndex] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; }
        [Kernel] double this[int simId, double dateInYears] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; }
        [Kernel] double this[alignedindex simId, int timeIndex] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; }
        [Kernel] double this[alignedindex simId, double dateInYears] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; }
    }

    public class ConstantRate : IRate 
    {
		public ConstantRate() { }
		public ConstantRate(double yearlyRate) { _yearlyRate = yearlyRate; }

        double _yearlyRate ;
        public double YearlyRate { get { return _yearlyRate; } set { _yearlyRate = value; } }

        [Kernel] public double this[int simId, int timeIndex] { get { return _yearlyRate; } }
        [Kernel] public double this[int simId, double dateInYears] { get { return _yearlyRate; } }
        [Kernel] public double this[alignedindex simId, int timeIndex] { get { return _yearlyRate; } }
        [Kernel] public double this[alignedindex simId, double dateInYears] { get { return _yearlyRate; } }
    }
}
