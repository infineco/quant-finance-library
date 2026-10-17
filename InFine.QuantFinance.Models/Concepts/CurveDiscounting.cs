using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Numerics;
using System;

namespace InFine.QuantFinance.Models.Concepts
{
    /// <summary>
    /// Deterministic discounting read from a term structure compiled by the host (see <see cref="CompiledDiscountCurve"/>,
    /// produced from a market curve by <c>InFine.QuantFinance.Market.MarketCompiler</c>).
    /// <para>
    /// Discount factors on the schedule pillars are precomputed; any other date in years is evaluated on the device by
    /// <see cref="DiscountCurveKernels.LogDiscountFactor"/> on the flat node arrays, i.e. with the same interpolation and
    /// extrapolation as the host curve.
    /// </para>
    /// </summary>
    public class CurveDiscounting : IDiscounting
    {
        int _mode;
        int _count;
        double[] _times;
        double[] _values;
        double[] _slopes;
        double _lastLogDiscount;
        double _lastForward;

        // precalculated, one per schedule pillar
        double[] _discounting;

        /// <summary>
        /// Copies the curve data and precomputes the discount factors at the pillars <c>0 .. PilarCount</c> of
        /// <paramref name="schedule"/>.
        /// </summary>
        /// <param name="curve">The compiled curve, on the same clock as the schedule.</param>
        /// <param name="schedule">The schedule on which this discounting is applied.</param>
        public CurveDiscounting(CompiledDiscountCurve curve, ISchedule schedule)
        {
            if (curve == null) throw new ArgumentNullException(nameof(curve));
            if (schedule == null) throw new ArgumentNullException(nameof(schedule));
            _mode = curve.Mode;
            _count = curve.Count;
            _times = curve.Times.ToArray();
            _values = curve.Values.ToArray();
            _slopes = curve.Slopes.ToArray();
            _lastLogDiscount = curve.LastLogDiscount;
            _lastForward = curve.LastForward;
            _discounting = curve.DiscountFactors(ScheduleTimes.Of(schedule));
        }

        [Kernel]
        private double Evaluate(double dateInYears)
        {
            return Math.Exp(DiscountCurveKernels.LogDiscountFactor(_mode, _times, _values, _slopes, _count, _lastLogDiscount, _lastForward, dateInYears));
        }

        /// <summary>Discount factor at a pillar of the schedule.</summary>
        [Kernel] public double this[int simId, int timeIndex] { get { return _discounting[timeIndex]; } }

        /// <summary>Discount factor at a date in years (non-negative), interpolated on the compiled curve.</summary>
        [Kernel] public double this[int simId, double dateInYears] { get { return Evaluate(dateInYears); } }

        /// <summary>Discount factor at a pillar of the schedule.</summary>
        [Kernel] public double this[alignedindex simId, int timeIndex] { get { return _discounting[timeIndex]; } }

        /// <summary>Discount factor at a date in years (non-negative), interpolated on the compiled curve.</summary>
        [Kernel] public double this[alignedindex simId, double dateInYears] { get { return Evaluate(dateInYears); } }
    }

    /// <summary>Host-side helper: the pillar times <c>0 .. PilarCount</c> of a schedule.</summary>
    internal static class ScheduleTimes
    {
        public static double[] Of(ISchedule schedule)
        {
            var times = new double[schedule.PilarCount + 1];
            for (int k = 0; k < times.Length; ++k)
                times[k] = schedule.getT(k);
            return times;
        }
    }
}
