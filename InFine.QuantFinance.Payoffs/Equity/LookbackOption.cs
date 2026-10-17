using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Payoffs.Concepts;
using InFine.QuantFinance.Scheduling;
using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Payoffs.Equity
{
    /// <summary>
    /// Hybridizer-free numerics of the lookback options, shared by the kernel and the unit tests.
    /// </summary>
    public static class LookbackMath
    {
        /// <summary>Broadie, Glasserman and Kou (1997) constant: -zeta(1/2) / sqrt(2 pi) = 0.5826.</summary>
        public const double BroadieGlassermanKouBeta = 0.5826;

        /// <summary>
        /// Multiplicative continuity correction exp(0.5826 sigma sqrt(dt)) for the extrema of a path monitored every
        /// <paramref name="dt"/> years: the continuous maximum is about shift times the discrete one, the continuous
        /// minimum about the discrete one divided by shift. Reduces the discretisation bias from O(sqrt(dt)) to O(dt).
        /// </summary>
        [Kernel]
        public static double ContinuityShift(double sigma, double dt)
        {
            return Math.Exp(BroadieGlassermanKouBeta * sigma * Math.Sqrt(dt));
        }

        /// <summary>
        /// Undiscounted payoff. Floating strike: a call pays S_T - minimum, a put maximum - S_T. Fixed strike: a call pays
        /// max(maximum - K, 0), a put max(K - minimum, 0).
        /// </summary>
        [Kernel]
        public static double Payoff(bool floating, int sign, double strike, double spotAtMaturity, double minimum, double maximum)
        {
            if (floating)
                return sign > 0 ? spotAtMaturity - minimum : maximum - spotAtMaturity;
            double intrinsic = sign > 0 ? maximum - strike : strike - minimum;
            return intrinsic > 0.0 ? intrinsic : 0.0;
        }

        /// <summary>
        /// Extrema seen by the option: the simulated extrema over the monitored pillars, moved outward by
        /// <paramref name="shift"/> (continuity correction, 1 for none), together with the spot at inception and an
        /// already observed extremum (<paramref name="observed"/> &gt; 0; it is a minimum for a floating call or a fixed
        /// put, a maximum otherwise, and is ignored for the other side).
        /// </summary>
        [Kernel]
        public static void Extrema(double initialSpot, double pathMinimum, double pathMaximum, double shift,
            bool observedIsMinimum, double observed, out double minimum, out double maximum)
        {
            minimum = Math.Min(initialSpot, pathMinimum / shift);
            maximum = Math.Max(initialSpot, pathMaximum * shift);
            if (observed > 0.0)
            {
                if (observedIsMinimum)
                    minimum = Math.Min(minimum, observed);
                else
                    maximum = Math.Max(maximum, observed);
            }
        }

        /// <summary>
        /// Undiscounted payoff of a spot path S_0..S_n monitored at <paramref name="monitor"/> (host version of the kernel loop).
        /// </summary>
        public static double PathPayoff(double[] path, int[] monitor, int maturity, bool floating, int sign, double strike,
            double observed, double shift)
        {
            double lo = double.MaxValue, hi = 0.0;
            for (int j = 0; j < monitor.Length; ++j)
            {
                double s = path[monitor[j]];
                lo = Math.Min(lo, s);
                hi = Math.Max(hi, s);
            }
            double minimum, maximum;
            Extrema(path[0], lo, hi, shift, floating ? sign > 0 : sign < 0, observed, out minimum, out maximum);
            return Payoff(floating, sign, strike, path[maturity], minimum, maximum);
        }
    }

    /// <summary>
    /// Lookback option on an equity underlying with discrete monitoring: the payoff depends on the running minimum or
    /// maximum of the spot over the monitored pillars (and the spot at inception).
    ///
    /// Floating strike: a call pays S_T - min, a put pays max - S_T. Fixed strike: a call pays max(max - K, 0), a put
    /// pays max(K - min, 0). Paid at the maturity pillar.
    ///
    /// Discretisation bias: the extrema of a path observed at n dates are inside the continuous extrema, by about
    /// 0.5826 sigma sqrt(dt) in relative terms (Broadie, Glasserman, Kou 1997), so every payoff is underestimated compared to
    /// a continuously monitored lookback (<c>LookbackKernels</c> closed form) by O(sqrt(dt)) - for example around 5% of the
    /// price at 50 dates, sigma = 25%, T = 1. The <c>continuityShift</c> argument (<see cref="LookbackMath.ContinuityShift"/>)
    /// moves the simulated extrema outward and brings the bias to O(dt); 1 leaves the discrete payoff untouched.
    /// An <c>observedExtremum</c> &gt; 0 is the extremum already recorded before inception (seasoned option): the minimum
    /// for a floating call or a fixed put, the maximum for a floating put or a fixed call.
    /// </summary>
    /// <typeparam name="Discounting">Discounting curve to be applied</typeparam>
    /// <typeparam name="Spot">Spot curve of the underlying</typeparam>
    /// <typeparam name="Schedule">Time schedule of the pricing</typeparam>
    /// <typeparam name="Premium">Output premium</typeparam>
    [HybridRegisterTemplate(Specialize=typeof(LookbackOption<ConstantRateDiscounting,LogBasedSpot<ArrayDoubleSpot>,PrecalculatedSchedule,ArrayPremium>))]
    [HybridRegisterTemplate(Specialize=typeof(LookbackOption<CurveDiscounting,LogBasedSpot<ArrayDoubleSpot>,PrecalculatedSchedule,ArrayPremium>))]
    public class LookbackOption<Discounting, Spot, Schedule, Premium> : ISimulatable
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
        bool _floating;
        double _observed;
        double _shift;

        /// <summary>
        /// Creates a new lookback option.
        /// </summary>
        /// <param name="underlying">Product underlying</param>
        /// <param name="discounting">Discounting curve</param>
        /// <param name="schedule">Pricing schedule</param>
        /// <param name="premium">Output premium</param>
        /// <param name="monitor">Pillar indices of the monitoring dates (non-empty; include the maturity pillar)</param>
        /// <param name="maturity">Payment pillar index</param>
        /// <param name="strike">Fixed strike (ignored for a floating-strike option)</param>
        /// <param name="sign">+1 for a call, -1 for a put</param>
        /// <param name="floating">true for a floating-strike option, false for a fixed-strike one</param>
        /// <param name="observedExtremum">Extremum observed before inception, or 0 for a new option</param>
        /// <param name="continuityShift">Extrema continuity correction, 1 for none (see <see cref="LookbackMath.ContinuityShift"/>)</param>
        public LookbackOption(Spot underlying, Discounting discounting, Schedule schedule, Premium premium,
            int[] monitor, int maturity, double strike, int sign, bool floating, double observedExtremum, double continuityShift)
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
            _floating = floating;
            _observed = observedExtremum;
            _shift = continuityShift;
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
                double lo = _underlying[simId, _monitor[0]];
                double hi = lo;
                for (int j = 1; j < _monitorCount; ++j)
                {
                    double s = _underlying[simId, _monitor[j]];
                    lo = Math.Min(lo, s);
                    hi = Math.Max(hi, s);
                }
                double minimum, maximum;
                LookbackMath.Extrema(_underlying[simId, 0], lo, hi, _shift, _floating ? _sign > 0 : _sign < 0, _observed, out minimum, out maximum);
                double payoff = LookbackMath.Payoff(_floating, _sign, _strike, _underlying[simId, _maturity], minimum, maximum);
                _premium[simId] = payoff * _discounting[simId, _maturity];
            }
        }
    }
}
