using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Scheduling;
using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Payoffs.Equity
{
    /// <summary>
    /// How the barrier is monitored between the simulated pillars (see <see cref="BarrierMath"/>).
    /// </summary>
    public static class BarrierMonitoring
    {
        /// <summary>Breach tested on the spot at the monitoring dates only.</summary>
        public const int Discrete = 0;

        /// <summary>
        /// Broadie-Glasserman-Kou continuity correction: discrete test against the barrier shifted <i>toward</i> the spot by
        /// exp(-+0.5826 sigma sqrt(dt)) (down for an up barrier, up for a down barrier), which approximates continuous
        /// monitoring: a discretely monitored barrier is crossed less often than a continuous one, so the test barrier is
        /// brought closer to compensate.
        /// </summary>
        public const int ContinuityCorrected = 1;

        /// <summary>
        /// Brownian-bridge crossing probability between consecutive monitoring dates, used as a survival weight
        /// (no random draw needed) or, in <see cref="BridgeSampledBarrierOption{Discounting,Spot,Schedule,Premium,Uniform}"/>,
        /// compared with a uniform draw.
        /// </summary>
        public const int BridgeProbability = 2;
    }

    /// <summary>
    /// Hybridizer-free numerics of the barrier options, shared by the kernels and the unit tests.
    /// </summary>
    public static class BarrierMath
    {
        /// <summary>Broadie, Glasserman and Kou (1997) constant: -zeta(1/2) / sqrt(2 pi) = 0.5826.</summary>
        public const double BroadieGlassermanKouBeta = 0.5826;

        /// <summary>True when the spot is on or beyond the barrier (above an up barrier, below a down barrier).</summary>
        [Kernel]
        public static bool IsBreached(bool isUp, double spot, double barrier)
        {
            return isUp ? spot >= barrier : spot <= barrier;
        }

        /// <summary>
        /// Broadie-Glasserman-Kou shifted barrier H exp(-+beta sigma sqrt(dt)), moved toward the spot (down for an up
        /// barrier, up for a down barrier); dt = 0 gives the barrier itself. A continuously monitored barrier at H is priced
        /// by discrete monitoring against this barrier, with an error O(dt) instead of O(sqrt(dt)).
        /// </summary>
        [Kernel]
        public static double ShiftedBarrier(bool isUp, double barrier, double sigma, double dt)
        {
            double shift = BroadieGlassermanKouBeta * sigma * Math.Sqrt(dt);
            return isUp ? barrier * Math.Exp(-shift) : barrier * Math.Exp(shift);
        }

        /// <summary>
        /// Probability that the path crossed the barrier during the monitoring interval [t_prev, t_next] of length
        /// <paramref name="dt"/>, given the spots at its ends, for the monitoring <paramref name="mode"/>
        /// (<see cref="BarrierMonitoring"/>).
        ///
        /// Discrete and continuity-corrected modes return 0 or 1 (end spot tested against the barrier or the shifted barrier).
        /// The bridge mode returns, for two spots on the safe side of the barrier, the exact crossing probability of the
        /// Brownian bridge of log-spot with volatility sigma:
        /// $$ p = \exp\left(-\frac{2 \ln(H/S_{prev}) \ln(H/S_{next})}{\sigma^2 \Delta t}\right) $$
        /// and 1 when either end is beyond the barrier (0 for a degenerate dt = 0 interval with both ends safe).
        /// </summary>
        [Kernel]
        public static double CrossingProbability(int mode, bool isUp, double barrier, double previous, double next, double sigma, double dt)
        {
            if (mode == BarrierMonitoring.Discrete)
                return IsBreached(isUp, next, barrier) ? 1.0 : 0.0;
            if (mode == BarrierMonitoring.ContinuityCorrected)
                return IsBreached(isUp, next, ShiftedBarrier(isUp, barrier, sigma, dt)) ? 1.0 : 0.0;
            if (IsBreached(isUp, previous, barrier) || IsBreached(isUp, next, barrier))
                return 1.0;
            if (!(dt > 0.0))
                return 0.0;
            return Math.Exp(-2.0 * Math.Log(barrier / previous) * Math.Log(barrier / next) / (sigma * sigma * dt));
        }

        /// <summary>
        /// Undiscounted-at-maturity assembly of the per-path premium from the survival weight and the discounted
        /// knock-out rebate accumulated along the path.
        ///
        /// Knock-out: survival * vanilla * DF(T) + rebatePv (rebate paid at the hit date). Knock-in:
        /// (1 - survival) * vanilla * DF(T) + survival * rebate * DF(T) (rebate paid at maturity when never hit).
        /// With a zero rebate, in + out = vanilla * DF(T) path by path.
        /// </summary>
        /// <param name="survival">Probability weight that the barrier was never crossed (0 or 1 when sampled).</param>
        /// <param name="knockOutRebatePv">Sum over monitoring intervals of survival_before * crossing * rebate * DF(date).</param>
        [Kernel]
        public static double Premium(int sign, bool isIn, double strike, double rebate, double survival, double knockOutRebatePv,
            double spotAtMaturity, double discountAtMaturity)
        {
            double intrinsic = sign * (spotAtMaturity - strike);
            double vanilla = (intrinsic > 0.0 ? intrinsic : 0.0) * discountAtMaturity;
            if (isIn)
                return (1.0 - survival) * vanilla + survival * rebate * discountAtMaturity;
            return survival * vanilla + knockOutRebatePv;
        }

        /// <summary>
        /// Premium of a spot path S_0..S_n (host version of the kernel loop). Monitoring covers [pillar 0, last monitored
        /// pillar]: the first interval is [0, monitor[0]]; include pillar 0 in <paramref name="monitor"/> so that S_0 is
        /// tested as an observation. <paramref name="dates"/> are the pillar times, <paramref name="discount"/> the discount
        /// factors at the pillars. <paramref name="uniforms"/> (one draw in (0, 1) per monitoring interval) makes the
        /// bridge mode sampled instead of probability-weighted; null otherwise.
        /// </summary>
        public static double PathPremium(double[] path, double[] dates, double[] discount, int[] monitor, int maturity,
            int sign, bool isUp, bool isIn, double strike, double barrier, double rebate, double sigma, int mode, double[] uniforms)
        {
            double survival = 1.0, rebatePv = 0.0;
            double previousSpot = path[0];
            int previousIndex = 0;
            for (int j = 0; j < monitor.Length; ++j)
            {
                int index = monitor[j];
                double next = path[index];
                double p = CrossingProbability(mode, isUp, barrier, previousSpot, next, sigma, dates[index] - dates[previousIndex]);
                if (uniforms != null)
                    p = uniforms[j] < p ? 1.0 : 0.0;
                rebatePv += survival * p * rebate * discount[index];
                survival *= 1.0 - p;
                previousSpot = next;
                previousIndex = index;
            }
            return Premium(sign, isIn, strike, rebate, survival, rebatePv, path[maturity], discount[maturity]);
        }
    }

    /// <summary>
    /// Single-barrier European option (up/down, in/out, call/put, with rebate) on an equity underlying, monitored at the
    /// given pillars.
    ///
    /// The vanilla payoff max(sign (S_T - K), 0) is paid at the maturity pillar if the option is active. Rebate (as in the
    /// closed form of <c>BarrierKernels</c>): a knock-out pays <c>rebate</c> at the (first) monitoring date where the
    /// barrier is found crossed, a knock-in pays <c>rebate</c> at maturity if the barrier was never crossed.
    ///
    /// Monitoring (<c>mode</c>, see <see cref="BarrierMonitoring"/>): <see cref="BarrierMonitoring.Discrete"/>;
    /// <see cref="BarrierMonitoring.ContinuityCorrected"/> (barrier shifted toward the spot, Broadie-Glasserman-Kou 1997, needs sigma and
    /// no random number); <see cref="BarrierMonitoring.BridgeProbability"/> (the premium of the path is weighted by the
    /// product of the per-interval Brownian-bridge survival probabilities, so it is still a deterministic function of the
    /// path, needs sigma and no extra random number, and is lower-variance than sampling the crossings; the knock-out
    /// rebate is then paid at the end of the interval in which the crossing happens, weighted by its probability).
    /// To sample the crossings instead, with an extra uniform draw per interval, use
    /// <see cref="BridgeSampledBarrierOption{Discounting,Spot,Schedule,Premium,Uniform}"/>.
    ///
    /// The first monitored interval starts at pillar 0 (see <see cref="BarrierMath.PathPremium"/>); the volatility
    /// <c>sigma</c> is the constant diffusion volatility of the paths.
    /// </summary>
    /// <typeparam name="Discounting">Discounting curve to be applied</typeparam>
    /// <typeparam name="Spot">Spot curve of the underlying</typeparam>
    /// <typeparam name="Schedule">Time schedule of the pricing</typeparam>
    /// <typeparam name="Premium">Output premium</typeparam>
    [HybridRegisterTemplate(Specialize=typeof(BarrierOption<ConstantRateDiscounting,LogBasedSpot<ArrayDoubleSpot>,PrecalculatedSchedule,ArrayPremium>))]
    [HybridRegisterTemplate(Specialize=typeof(BarrierOption<CurveDiscounting,LogBasedSpot<ArrayDoubleSpot>,PrecalculatedSchedule,ArrayPremium>))]
    public class BarrierOption<Discounting, Spot, Schedule, Premium> : ISimulatable
        where Discounting : IDiscounting
        where Spot : ISpot
        where Schedule : ISchedule
        where Premium : IPremium
    {
        Spot _underlying;
        Discounting _discounting;
        Schedule _schedule;

        Premium _premium;

        int[] _monitor;
        int _monitorCount;
        int _maturity;
        double _strike;
        int _sign;
        bool _isUp;
        bool _isIn;
        double _barrier;
        double _rebate;
        double _sigma;
        int _mode;

        /// <summary>
        /// Creates a new barrier option.
        /// </summary>
        /// <param name="underlying">Product underlying</param>
        /// <param name="discounting">Discounting curve</param>
        /// <param name="schedule">Pricing schedule</param>
        /// <param name="premium">Output premium</param>
        /// <param name="monitor">Ascending pillar indices of the monitoring dates (non-empty)</param>
        /// <param name="maturity">Payment pillar index, at least the last monitoring date</param>
        /// <param name="strike">Strike</param>
        /// <param name="sign">+1 for a call, -1 for a put</param>
        /// <param name="isUp">true for an up barrier, false for a down barrier</param>
        /// <param name="isIn">true for a knock-in, false for a knock-out</param>
        /// <param name="barrier">Barrier level H</param>
        /// <param name="rebate">Rebate</param>
        /// <param name="sigma">Constant volatility of the diffusion (used by the continuity-corrected and bridge modes)</param>
        /// <param name="mode">Monitoring mode, see <see cref="BarrierMonitoring"/></param>
        public BarrierOption(Spot underlying, Discounting discounting, Schedule schedule, Premium premium,
            int[] monitor, int maturity, double strike, int sign, bool isUp, bool isIn, double barrier, double rebate,
            double sigma, int mode)
        {
            _underlying = underlying;
            _discounting = discounting;
            _schedule = schedule;
            _premium = premium;
            _monitor = monitor;
            _monitorCount = monitor.Length;
            _maturity = maturity;
            _strike = strike;
            _sign = sign;
            _isUp = isUp;
            _isIn = isIn;
            _barrier = barrier;
            _rebate = rebate;
            _sigma = sigma;
            _mode = mode;
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
                double survival = 1.0;
                double rebatePv = 0.0;
                double previousSpot = _underlying[simId, 0];
                int previousIndex = 0;
                for (int j = 0; j < _monitorCount; ++j)
                {
                    int index = _monitor[j];
                    double next = _underlying[simId, index];
                    double dt = _schedule.getT(index) - _schedule.getT(previousIndex);
                    double p = BarrierMath.CrossingProbability(_mode, _isUp, _barrier, previousSpot, next, _sigma, dt);
                    rebatePv += survival * p * _rebate * _discounting[simId, index];
                    survival *= 1.0 - p;
                    previousSpot = next;
                    previousIndex = index;
                }
                _premium[simId] = BarrierMath.Premium(_sign, _isIn, _strike, _rebate, survival, rebatePv,
                    _underlying[simId, _maturity], _discounting[simId, _maturity]);
            }
        }
    }

    /// <summary>
    /// Barrier option whose continuous monitoring is approximated by sampling Brownian-bridge crossings: on each
    /// monitoring interval the path is declared crossed when a uniform draw is below the bridge crossing probability
    /// (<see cref="BarrierMath.CrossingProbability"/> in <see cref="BarrierMonitoring.BridgeProbability"/> mode).
    ///
    /// This needs one extra uniform draw in (0, 1) per monitoring interval, taken from <typeparamref name="Uniform"/>
    /// at <c>[simId, j]</c> for the interval ending at <c>monitor[j]</c>. Generate them from a Philox sub-stream
    /// <b>distinct from the one of the spot Brownian</b> (another stream index), with
    /// <c>Philox4x32x10.ToUniformOpen32</c>, so that draws and spot increments are independent. The weighted variant of
    /// <see cref="BarrierOption{Discounting,Spot,Schedule,Premium}"/> in bridge mode has the same expectation without
    /// the draws and a lower variance; the sampled one gives, with the same draws, a knock-in and a knock-out whose
    /// premia add up exactly to the vanilla premium path by path, and a crossing time known to the interval.
    /// </summary>
    /// <typeparam name="Discounting">Discounting curve to be applied</typeparam>
    /// <typeparam name="Spot">Spot curve of the underlying</typeparam>
    /// <typeparam name="Schedule">Time schedule of the pricing</typeparam>
    /// <typeparam name="Premium">Output premium</typeparam>
    /// <typeparam name="Uniform">Storage of the uniform draws, indexed [simId, monitoring interval]</typeparam>
    [HybridRegisterTemplate(Specialize=typeof(BridgeSampledBarrierOption<ConstantRateDiscounting,LogBasedSpot<ArrayDoubleSpot>,PrecalculatedSchedule,ArrayPremium,ArrayBrownian>))]
    public class BridgeSampledBarrierOption<Discounting, Spot, Schedule, Premium, Uniform> : ISimulatable
        where Discounting : IDiscounting
        where Spot : ISpot
        where Schedule : ISchedule
        where Premium : IPremium
        where Uniform : IBrownian
    {
        Spot _underlying;
        Discounting _discounting;
        Schedule _schedule;

        Premium _premium;
        Uniform _uniforms;

        int[] _monitor;
        int _monitorCount;
        int _maturity;
        double _strike;
        int _sign;
        bool _isUp;
        bool _isIn;
        double _barrier;
        double _rebate;
        double _sigma;

        /// <summary>
        /// Creates a new bridge-sampled barrier option. Parameters as
        /// <see cref="BarrierOption{Discounting,Spot,Schedule,Premium}"/> (the monitoring mode is always the bridge one)
        /// plus the storage of the uniform draws.
        /// </summary>
        public BridgeSampledBarrierOption(Spot underlying, Discounting discounting, Schedule schedule, Premium premium, Uniform uniforms,
            int[] monitor, int maturity, double strike, int sign, bool isUp, bool isIn, double barrier, double rebate, double sigma)
        {
            _underlying = underlying;
            _discounting = discounting;
            _schedule = schedule;
            _premium = premium;
            _uniforms = uniforms;
            _monitor = monitor;
            _monitorCount = monitor.Length;
            _maturity = maturity;
            _strike = strike;
            _sign = sign;
            _isUp = isUp;
            _isIn = isIn;
            _barrier = barrier;
            _rebate = rebate;
            _sigma = sigma;
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
                double survival = 1.0;
                double rebatePv = 0.0;
                double previousSpot = _underlying[simId, 0];
                int previousIndex = 0;
                for (int j = 0; j < _monitorCount; ++j)
                {
                    int index = _monitor[j];
                    double next = _underlying[simId, index];
                    double dt = _schedule.getT(index) - _schedule.getT(previousIndex);
                    double crossing = BarrierMath.CrossingProbability(BarrierMonitoring.BridgeProbability, _isUp, _barrier, previousSpot, next, _sigma, dt);
                    double p = _uniforms[simId, j] < crossing ? 1.0 : 0.0;
                    rebatePv += survival * p * _rebate * _discounting[simId, index];
                    survival *= 1.0 - p;
                    previousSpot = next;
                    previousIndex = index;
                }
                _premium[simId] = BarrierMath.Premium(_sign, _isIn, _strike, _rebate, survival, rebatePv,
                    _underlying[simId, _maturity], _discounting[simId, _maturity]);
            }
        }
    }
}
