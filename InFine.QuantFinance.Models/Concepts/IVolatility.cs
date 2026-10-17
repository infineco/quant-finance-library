using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Models.Concepts
{
    [HybridTemplateConcept]
    public interface IVolatility
    {
        [Kernel]
        double this[double lnS, int simId, int timeIndex] { [ReturnTypeInference(Index = 2, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; }
        [Kernel]
        double this[double lnS, alignedindex simId, int timeIndex] { [ReturnTypeInference(Index = 2, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; }
    }

    public class ConstantVolatility : IVolatility
    {
        double _vol;
        public ConstantVolatility() {}
        public ConstantVolatility(double vol) { _vol = vol; }
        [Kernel]
        public double Volatility { get { return _vol; } set { _vol = value; } }
        [Kernel]
        public double this[double lnS, int simId, int timeIndex] { get { return Volatility; } }
        [Kernel]
        public double this[double lnS, alignedindex simId, int timeIndex] { get { return Volatility; } }
    }

    [HybridTemplateConcept]
    public interface IStochasticVolatility
    {
        [Kernel]
        double this[int simId, int timeIndex] { [ReturnTypeInference(Index = 2, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; set; }
        [Kernel]
        double this[alignedindex simId, int timeIndex] { [ReturnTypeInference(Index = 2, Return = VectorizerIntrinsicReturn.VectorTransitive)] get; set; }
    }

    /// <summary>
    /// Per-simulation, per-date storage of a stochastic <b>variance</b> $v_t$ (e.g. diffused by
    /// <see cref="Diffusion.HestonVolatilityModel{Volatility, Rate, Brownian, Schedule}"/>).
    ///
    /// The two views read the same storage differently:
    /// <list type="bullet">
    /// <item><see cref="IStochasticVolatility"/> indexers read and write the raw variance, which a full-truncation
    /// scheme may leave negative.</item>
    /// <item><see cref="IVolatility"/> indexers return the instantaneous volatility $\sigma = \sqrt{\max(v, 0)}$,
    /// which is what <see cref="Diffusion.EquityDiffusion{Volatility, Rate, LogSpot, Brownian, Schedule}"/> expects.</item>
    /// </list>
    /// </summary>
    public class ArrayStochasticVolatility : IVolatility, IStochasticVolatility
    {
        int _simCount;
        alignedstorage_double _backing;

        public ArrayStochasticVolatility(int simCount, int datesCount)
        {
            _simCount = simCount;
            _backing = new alignedstorage_double(simCount * datesCount);
        }

        // volatility view: sigma = sqrt(v+), consistent with the full-truncation variance scheme
        [Kernel] public double this[double lnS, int simId, int timeIndex] { get { return Math.Sqrt(Math.Max(_backing[simId + timeIndex * _simCount], 0.0)) ; } }
        [Kernel] public double this[double lnS, alignedindex simId, int timeIndex] { get { return Math.Sqrt(Math.Max(_backing[simId + timeIndex * _simCount], 0.0)) ; } }

        // variance view: raw variance
        [Kernel] public double this[int simId, int timeIndex] { get { return _backing[simId + timeIndex * _simCount] ; } set { _backing[simId + timeIndex * _simCount] = value ; } }
        [Kernel] public double this[alignedindex simId, int timeIndex] { get { return _backing[simId + timeIndex * _simCount] ; } set { _backing[simId + timeIndex * _simCount] = value ; } }
    }
}
