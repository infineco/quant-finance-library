using Hybridizer.Runtime.CUDAImports;

namespace InFine.QuantFinance.Payoffs.Concepts
{
    /// <summary>
    /// Premium concept.
    /// 
    /// The premium is a placeholder for Monte Carlo simulation results.
    /// Premium is relative to a schedule.
    /// </summary>
    [HybridTemplateConcept]
    public interface IPremium
    {
        /// <summary>
        /// Gets or set premium value for a given time index.
        /// </summary>
        /// <param name="simId">Id of the simulation</param>
        /// <returns></returns>
        [Kernel] double this[int simId] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; set; }
        /// <summary>
        /// Gets or set premium value for a given time index.
        /// </summary>
        /// <param name="simId">Id of the simulation</param>
        /// <returns></returns>
        [Kernel] double this[alignedindex simId] { [ReturnTypeInference(Index = 1, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; set; }
    }

    public class ArrayPremium : IPremium
    {
        int _simCount;
        alignedstorage_double _backing;

        public ArrayPremium(int simCount)
        {
            _simCount = simCount;
            _backing = new alignedstorage_double(simCount, GetType().Name);
        }

        [Kernel]
        public double this[int simId]
        {
            get { return _backing[simId]; }
            set { _backing[simId] = value; }
        }

        [Kernel]
        public double this[alignedindex simId]
        {
            get { return _backing[simId]; }
            set { _backing[simId] = value; }
        }
    }
}
