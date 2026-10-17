using InFine.QuantFinance.Arithmetic.AutoDiff;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Scheduling;
using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Payoffs.Equity
{
    /// <summary>
    /// Hybridizer-free numerics of the Asian options, shared by the kernel and the unit tests.
    /// </summary>
    public static class AsianMath
    {
        /// <summary>
        /// What is accumulated for one fixing: the spot (arithmetic average) or its logarithm (geometric average).
        /// </summary>
        [Kernel]
        public static double Term(bool geometric, double spot)
        {
            return geometric ? Math.Log(spot) : spot;
        }

        /// <summary>Average from the sum of <see cref="Term(bool, double)"/> over <paramref name="count"/> fixings.</summary>
        [Kernel]
        public static double Average(bool geometric, double sum, int count)
        {
            return geometric ? Math.Exp(sum / count) : sum / count;
        }

        /// <summary>
        /// Undiscounted payoff: max(sign (A - K), 0) for an average-price option, max(sign (S_T - A), 0) for an
        /// average-strike option (a call pays S_T - A, a put A - S_T; the strike is then ignored).
        /// </summary>
        [Kernel]
        public static double Payoff(int sign, bool averageStrike, double strike, double average, double spotAtMaturity)
        {
            double intrinsic = averageStrike ? sign * (spotAtMaturity - average) : sign * (average - strike);
            return intrinsic > 0.0 ? intrinsic : 0.0;
        }

        /// <summary>
        /// Undiscounted payoff of a spot path S_0..S_n sampled at the pillar indices <paramref name="fixings"/>
        /// (same arithmetic as the kernel); the spot at maturity is <paramref name="path"/>[<paramref name="maturity"/>].
        /// </summary>
        public static double PathPayoff(double[] path, int[] fixings, int maturity, int sign, bool geometric, bool averageStrike, double strike)
        {
            double sum = 0.0;
            for (int k = 0; k < fixings.Length; ++k)
                sum += Term(geometric, path[fixings[k]]);
            return Payoff(sign, averageStrike, strike, Average(geometric, sum, fixings.Length), path[maturity]);
        }

        /// <summary><see cref="Term(bool, double)"/> on a dual spot (pathwise Greeks).</summary>
        [Kernel]
        public static Dual4 Term(bool geometric, Dual4 spot)
        {
            return geometric ? Dual4.Log(spot) : spot;
        }

        /// <summary><see cref="Average(bool, double, int)"/> on a dual sum (pathwise Greeks).</summary>
        [Kernel]
        public static Dual4 Average(bool geometric, Dual4 sum, int count)
        {
            return geometric ? Dual4.Exp(sum / count) : sum / count;
        }

        /// <summary>
        /// <see cref="Payoff(int, bool, double, double, double)"/> on dual spots (pathwise Greeks). The kink follows the
        /// <see cref="Dual4.Max"/> convention: an intrinsic value of exactly 0 is differentiated as exercised.
        /// </summary>
        [Kernel]
        public static Dual4 Payoff(int sign, bool averageStrike, double strike, Dual4 average, Dual4 spotAtMaturity)
        {
            Dual4 intrinsic = averageStrike ? sign * (spotAtMaturity - average) : sign * (average - strike);
            return Dual4.Max(intrinsic, 0.0);
        }

        /// <summary>
        /// <see cref="PathPayoff(double[], int[], int, int, bool, bool, double)"/> on a dual path (pathwise Greeks), same
        /// arithmetic.
        /// </summary>
        public static Dual4 PathPayoff(Dual4[] path, int[] fixings, int maturity, int sign, bool geometric, bool averageStrike, double strike)
        {
            Dual4 sum = 0.0;
            for (int k = 0; k < fixings.Length; ++k)
                sum += Term(geometric, path[fixings[k]]);
            return Payoff(sign, averageStrike, strike, Average(geometric, sum, fixings.Length), path[maturity]);
        }
    }

    /// <summary>
    /// Asian option on an equity underlying: payoff on the arithmetic or geometric average of the spot observed at a set
    /// of fixing pillars, paid at the maturity pillar.
    ///
    /// Average-price: max(sign (A - K), 0). Average-strike: max(sign (S_T - A), 0). The average is
    /// $$ A = \frac{1}{n} \sum_{k} S(t_{f_k}) \quad \text{(arithmetic)}, \qquad A = \exp\left(\frac{1}{n} \sum_k \ln S(t_{f_k})\right) \quad \text{(geometric)} $$
    /// over the pillar indices f_0..f_(n-1) given at construction (any subset of the schedule, fixings may include
    /// pillar 0 or the maturity). The premium of each path is the discounted payoff; the option value is the mean of the
    /// premium. For the control-variate estimator run the same paths twice, once geometric and once arithmetic,
    /// and combine the two premium arrays with <see cref="AsianControlVariate"/>.
    /// </summary>
    /// <typeparam name="Discounting">Discounting curve to be applied</typeparam>
    /// <typeparam name="Spot">Spot curve of the underlying</typeparam>
    /// <typeparam name="Schedule">Time schedule of the pricing</typeparam>
    /// <typeparam name="Premium">Output premium</typeparam>
    [HybridRegisterTemplate(Specialize = typeof(AsianOption<ConstantRateDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>))]
    [HybridRegisterTemplate(Specialize = typeof(AsianOption<CurveDiscounting, LogBasedSpot<ArrayDoubleSpot>, PrecalculatedSchedule, ArrayPremium>))]
    public class AsianOption<Discounting, Spot, Schedule, Premium> : ISimulatable
        where Discounting : IDiscounting
        where Spot : ISpot
        where Schedule : ISchedule
        where Premium : IPremium
    {
        Spot _underlying;
        Discounting _discounting;
        Schedule _schedule;

        Premium _premium;

        int[] _fixings;
        int _fixingCount;
        int _maturity;
        double _strike;
        int _sign;
        bool _geometric;
        bool _averageStrike;

        /// <summary>
        /// Creates a new Asian option.
        /// </summary>
        /// <param name="underlying">Product underlying</param>
        /// <param name="discounting">Discounting curve</param>
        /// <param name="schedule">Pricing schedule</param>
        /// <param name="premium">Output premium</param>
        /// <param name="fixings">Pillar indices of the fixings (non-empty, within the schedule)</param>
        /// <param name="maturity">Payment pillar index, at least the last fixing</param>
        /// <param name="strike">Strike (ignored for an average-strike option)</param>
        /// <param name="sign">+1 for a call, -1 for a put</param>
        /// <param name="geometric">true for the geometric average, false for the arithmetic one</param>
        /// <param name="averageStrike">true for an average-strike option, false for an average-price option</param>
        public AsianOption(Spot underlying, Discounting discounting, Schedule schedule, Premium premium,
            int[] fixings, int maturity, double strike, int sign, bool geometric, bool averageStrike)
        {
            _underlying = underlying;
            _discounting = discounting;
            _schedule = schedule;
            _premium = premium;
            _fixings = fixings;
            _fixingCount = fixings.Length;
            _maturity = maturity;
            _strike = strike;
            _sign = sign;
            _geometric = geometric;
            _averageStrike = averageStrike;
        }

        /// <summary>
        /// Monte Carlo simulation of the product: _premium receives the discounted payoff for each simulation path.
        /// </summary>
        /// <param name="simFrom">lower inclusive bound of simulation</param>
        /// <param name="simTo">upper exclusive bound of simulation</param>
        [Kernel]
        public void Simulate(int simFrom, int simTo)
        {
            for (alignedindex simId = VectorUnit.ID + simFrom; simId < simTo; simId += VectorUnit.Count)
            {
                double sum = 0.0;
                for (int k = 0; k < _fixingCount; ++k)
                    sum += AsianMath.Term(_geometric, _underlying[simId, _fixings[k]]);
                double average = AsianMath.Average(_geometric, sum, _fixingCount);
                double payoff = AsianMath.Payoff(_sign, _averageStrike, _strike, average, _underlying[simId, _maturity]);
                _premium[simId] = payoff * _discounting[simId, _maturity];
            }
        }
    }
}
