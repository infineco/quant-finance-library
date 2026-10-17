using InFine.QuantFinance.Models.Concepts;
using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Scheduling;

namespace InFine.QuantFinance.Models.Diffusion
{
    /// <summary>
    /// BlackScholesMerton is constant rate and constant volatility diffusion model.
    /// This class is simply a holder for partially specialized EquityDiffusion.
    /// </summary>
    /// <typeparam name="LogSpot"></typeparam>
    /// <typeparam name="Brownian"></typeparam>
    /// <typeparam name="Schedule"></typeparam>
    [HybridRegisterTemplate(Specialize=typeof(BlackScholesMerton<Models.Concepts.ArrayDoubleSpot,Models.Concepts.ArrayBrownian,Models.Concepts.PrecalculatedSchedule>))]
    [HybridRegisterTemplate(Specialize = typeof(BlackScholesMerton<Models.Concepts.UnalignedArrayDoubleSpot, Models.Concepts.UnalignedArrayBrownian, Models.Concepts.PrecalculatedSchedule>))]
    public class BlackScholesMerton<LogSpot,Brownian,Schedule> : ISimulatable
            where LogSpot : ILogSpot
            where Brownian : IBrownian
            where Schedule : ISchedule
    {
        EquityDiffusion<ConstantVolatility, ConstantRate, LogSpot, Brownian, Schedule> _inner = new EquityDiffusion<ConstantVolatility, ConstantRate, LogSpot, Brownian, Schedule>();

        /// <summary>
        /// Diffusion kernel. Wrapper to generic equity diffusion kernel, where parameters may be overridden.
        /// </summary>
        /// <param name="simFrom"></param>
        /// <param name="simTo"></param>
        /// <param name="timeFrom"></param>
        /// <param name="timeTo"></param>
        /// <param name="volatility"></param>
        /// <param name="rate"></param>
        /// <param name="logSpot"></param>
        /// <param name="brownian"></param>
        /// <param name="schedule"></param>
        [Kernel]
        public void Diffusion(
            int simFrom, int simTo,
            int timeFrom, int timeTo, 
            ConstantVolatility volatility,
            ConstantRate rate,
            LogSpot logSpot,
            Brownian brownian,
            Schedule schedule)
        {
            _inner.Diffusion(simFrom, simTo, timeFrom, timeTo, volatility, rate, logSpot, brownian, schedule);
        }

        LogSpot _logspot;
        Brownian _brownian;
        Schedule _schedule;

        ConstantVolatility _volatility;
        ConstantRate _rate;

        /// <summary>
        /// Parameterless constructor. May be used with the Diffusion kernel
        /// </summary>
        public BlackScholesMerton()
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="volatility"></param>
        /// <param name="rate"></param>
        /// <param name="logspot"></param>
        /// <param name="brownian"></param>
        /// <param name="schedule"></param>
        public BlackScholesMerton(ConstantVolatility volatility, ConstantRate rate, 
                LogSpot logspot, Brownian brownian, Schedule schedule)
        {
            _volatility = volatility; _rate = rate; _logspot = logspot; _brownian = brownian; _schedule = schedule;
        }

        /// <summary>
        /// Diffusion of a range of simulations
        /// </summary>
        /// <param name="simFrom">Start simulation (inclusive)</param>
        /// <param name="simTo">End simulation (exclusive)</param>
        [Kernel]
        public void Simulate(int simFrom, int simTo)
        {
            Diffusion(simFrom, simTo, 0, _schedule.PilarCount, _volatility, _rate, _logspot, _brownian, _schedule);
        }
    }
}
