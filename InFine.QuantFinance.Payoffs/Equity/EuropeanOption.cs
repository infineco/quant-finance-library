using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Scheduling;
using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Payoffs.Equity
{
    /// <summary>
    /// European call option. The contract gives the right to buy an underlying, defined here by Spot, at a given price, defined by strike, at a 
    /// predefined future date, the maturity. 
    /// </summary>
    /// <typeparam name="Discounting">The discounting curve.</typeparam>
    /// <typeparam name="Spot">The simulated spot of the underlying</typeparam>
    /// <typeparam name="Schedule">The schedule for this pricing</typeparam>
    /// <typeparam name="Premium"></typeparam>
    [HybridRegisterTemplate(Specialize=typeof(EuropeanCallOption<ConstantRateDiscounting,LogBasedSpot<ArrayDoubleSpot>,PrecalculatedSchedule,ArrayPremium>))]
    [HybridRegisterTemplate(Specialize=typeof(EuropeanCallOption<CurveDiscounting,LogBasedSpot<ArrayDoubleSpot>,PrecalculatedSchedule,ArrayPremium>))]
    [HybridRegisterTemplate(Specialize=typeof(EuropeanCallOption<HullWhiteDiscounting,LogBasedSpot<ArrayDoubleSpot>,PrecalculatedSchedule,ArrayPremium>))]
    public class EuropeanCallOption<Discounting, Spot, Schedule, Premium> : ISimulatable
            where Discounting : IDiscounting
            where Spot : ISpot
            where Schedule : ISchedule
            where Premium : IPremium
    {
        Spot _underlying;
        Discounting _discounting;
        Schedule _schedule;

        Premium _premium;

        int _maturity;
        double _strike;

        /// <summary>
        /// Creates a new instance of a European call option.
        /// </summary>
        /// <param name="underlying">The underlying product</param>
        /// <param name="discounting">The discounting curve</param>
        /// <param name="schedule">Pricing schedule</param>
        /// <param name="premium">Output premium</param>
        /// <param name="maturity">Maturity of the option</param>
        /// <param name="strike">Strike of the option</param>
        public EuropeanCallOption(Spot underlying, Discounting discounting, Schedule schedule, Premium premium, int maturity, double strike)
        {
            _underlying = underlying;
            _discounting = discounting;
            _schedule = schedule;
            _premium = premium;
            _maturity = maturity;
            _strike = strike;
        }

        /// <summary>
        /// Monte Carlo simulation of the product: _premium recieves the premium for each simulation path.
        /// 
        /// The premium of the option is the expected value of _premium.
        /// </summary>
        /// <param name="simFrom">lower inclusive bound of simulation</param>
        /// <param name="simTo">upper exclusive bound of simulation</param>
        [Kernel]
        public void Simulate(int simFrom, int simTo)
        {
            for (alignedindex simId = VectorUnit.ID + simFrom; simId < simTo; simId += VectorUnit.Count)
            {
                _premium[simId] = Math.Max(0.0, _underlying[simId, _maturity] - _strike) * _discounting[simId, _maturity]; 
            }
        }
    }
}
