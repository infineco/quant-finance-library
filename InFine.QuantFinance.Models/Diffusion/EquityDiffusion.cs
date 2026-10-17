using InFine.QuantFinance.Models.Concepts;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Scheduling;

namespace InFine.QuantFinance.Models.Diffusion
{
    /// <summary>
    /// General equity diffusion. Volatility and Rate may also be stochastic, though no coupling is allowed (neither volatility nor rate may depend on the spot).
    /// </summary>
    /// <typeparam name="Volatility"></typeparam>
    /// <typeparam name="Rate"></typeparam>
    /// <typeparam name="LogSpot"></typeparam>
    /// <typeparam name="Brownian"></typeparam>
    /// <typeparam name="Schedule"></typeparam>
    [HybridRegisterTemplate(Specialize = typeof(EquityDiffusion<ConstantVolatility, ConstantRate, Models.Concepts.ArrayDoubleSpot, Models.Concepts.ArrayBrownian, Models.Concepts.PrecalculatedSchedule>))]
    [HybridRegisterTemplate(Specialize = typeof(EquityDiffusion<ConstantVolatility, ConstantRate, Models.Concepts.UnalignedArrayDoubleSpot, Models.Concepts.UnalignedArrayBrownian, Models.Concepts.PrecalculatedSchedule>))]
    [HybridRegisterTemplate(Specialize = typeof(EquityDiffusion<ConstantVolatility, CurveRate, Models.Concepts.ArrayDoubleSpot, Models.Concepts.ArrayBrownian, Models.Concepts.PrecalculatedSchedule>))]
    public class EquityDiffusion<Volatility, Rate, LogSpot, Brownian, Schedule>
        where Volatility : IVolatility
        where Rate : IRate
        where LogSpot : ILogSpot
        where Brownian : IBrownian
        where Schedule : ISchedule
    {
        /// <summary>
        /// Diffusion of a spot 
        /// </summary>
        /// <param name="simFrom">start simulation index (inclusive) for this call</param>
        /// <param name="simTo">end simulation index (exclusive)</param>
        /// <param name="timeFrom">start date for diffusion - spot is known at that time</param>
        /// <param name="timeTo">end date of diffusion (inclusive)</param>
        /// <param name="volatility"></param>
        /// <param name="rate"></param>
        /// <param name="logSpot"></param>
        /// <param name="brownian"></param>
        /// <param name="schedule"></param>
        [Kernel]
        public void Diffusion(
            int simFrom, int simTo,
            int timeFrom, int timeTo,
            Volatility volatility,
            Rate rate,
            LogSpot logSpot,
            Brownian brownian,
            Schedule schedule)
        {
            /* time outer loop
            for (int t = timeFrom; t < timeTo; ++t)
            {
                for (alignedindex simId = VectorUnit.ID + simFrom;
                    simId < simTo; simId += VectorUnit.Count)
                {
                    double lnSk = logSpot[simId, t];
                    double sigma = volatility[lnSk, simId, t];
                    double sqrtdt = schedule.getSqrtDT(t);
                    double dt = schedule.getDT(t);
                    logSpot[simId, t + 1] = lnSk + (sigma * brownian[simId, t] * sqrtdt) +
                        (rate[simId, t] - 0.5 * sigma * sigma) * dt;
                }
            } // */
            //* sim outer loop
            for (alignedindex simId = simFrom + VectorUnit.ID; simId < simTo; simId += VectorUnit.Count)
            {
                double lnSk = logSpot[simId, timeFrom];
                for (int t = timeFrom; t < timeTo; ++t)
                {
                    double sigma = volatility[lnSk, simId, t];
                    double sqrtdt = schedule.getSqrtDT(t);
                    double dt = schedule.getDT(t);
                    lnSk += (sigma * brownian[simId, t] * sqrtdt) +
                        (rate[simId, t] - 0.5 * sigma * sigma) * dt;
                    logSpot[simId, t + 1] = lnSk;
                }
            } // */
        }
    }

    /// <summary>
    /// Base equity diffusion. This holder type exposes an equity diffusion using interfaces and virtual functions, hence not benefitting from any optimization 
    /// of template transformation of generic types. Though, it should also work for types for which template parameters have not been registered.
    /// </summary>
    public class BaseEquityDiffusion : ISimulatable
    {
        EquityDiffusion<IVolatility, IRate, ILogSpot, IBrownian, ISchedule> _inner = new EquityDiffusion<IVolatility, IRate, ILogSpot, IBrownian, ISchedule>();

        IVolatility _volatility;
        IRate _rate;
        ILogSpot _logSpot;
        IBrownian _brownian;
        ISchedule _schedule;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="volatility"></param>
        /// <param name="rate"></param>
        /// <param name="logSpot"></param>
        /// <param name="brownian"></param>
        /// <param name="schedule"></param>
        public BaseEquityDiffusion(IVolatility volatility, IRate rate, ILogSpot logSpot, IBrownian brownian, ISchedule schedule)
        {
            _volatility = volatility;
            _rate = rate;
            _logSpot = logSpot;
            _brownian = brownian;
            _schedule = schedule;
        }

        /// <summary>
        /// Performs diffusion of the equity diffusion model for a range of simulations
        /// </summary>
        /// <param name="simFrom">Start simulation index (inclusive)</param>
        /// <param name="simTo">End simulation index (exclusive)</param>
        [Kernel]
        public void Simulate(int simFrom, int simTo)
        {
            _inner.Diffusion(simFrom, simTo, 0, _schedule.PilarCount, _volatility, _rate, _logSpot, _brownian, _schedule);
        }
    }
}
