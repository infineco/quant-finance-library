using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.Numerics;
using System;

namespace InFine.QuantFinance.Models.Concepts
{
    /// <summary>
    /// Deterministic short-rate drift read from a compiled term structure, for a log-Euler equity diffusion on a schedule.
    /// <para>
    /// On step <c>k</c>, i.e. <c>[t_k, t_{k+1}]</c>, the rate is the continuously compounded forward over the step,
    /// <c>r_k = -ln(P(0, t_{k+1}) / P(0, t_k)) / (t_{k+1} - t_k)</c>, not the instantaneous forward at <c>t_k</c>. With it,
    /// <c>E[S_{k+1} | S_k] = S_k exp(r_k dt_k) = S_k P(0, t_k) / P(0, t_{k+1})</c>: the spot discounted with
    /// <see cref="CurveDiscounting"/> is exactly a martingale on the schedule, whatever the step size and the curve shape.
    /// Indexing by a date in years gives the instantaneous forward <c>f(t) = -d ln P(0, t) / dt</c> instead.
    /// </para>
    /// </summary>
    public class CurveRate : IRate
    {
        int _mode;
        int _count;
        double[] _times;
        double[] _values;
        double[] _slopes;
        double _lastForward;

        // precalculated, one per schedule step
        double[] _stepRates;

        /// <summary>Copies the curve data and precomputes the step forwards of <paramref name="schedule"/>.</summary>
        /// <param name="curve">The compiled curve, on the same clock as the schedule.</param>
        /// <param name="schedule">The schedule of the diffusion.</param>
        public CurveRate(CompiledDiscountCurve curve, ISchedule schedule)
        {
            if (curve == null) throw new ArgumentNullException(nameof(curve));
            if (schedule == null) throw new ArgumentNullException(nameof(schedule));
            _mode = curve.Mode;
            _count = curve.Count;
            _times = curve.Times.ToArray();
            _values = curve.Values.ToArray();
            _slopes = curve.Slopes.ToArray();
            _lastForward = curve.LastForward;
            _stepRates = curve.StepForwards(ScheduleTimes.Of(schedule));
        }

        [Kernel]
        private double Evaluate(double dateInYears)
        {
            return DiscountCurveKernels.InstantaneousForward(_mode, _times, _values, _slopes, _count, _lastForward, dateInYears);
        }

        /// <summary>The forward over the schedule step starting at pillar <paramref name="timeIndex"/>.</summary>
        [Kernel] public double this[int simId, int timeIndex] { get { return _stepRates[timeIndex]; } }

        /// <summary>The instantaneous forward at a date in years (non-negative).</summary>
        [Kernel] public double this[int simId, double dateInYears] { get { return Evaluate(dateInYears); } }

        /// <summary>The forward over the schedule step starting at pillar <paramref name="timeIndex"/>.</summary>
        [Kernel] public double this[alignedindex simId, int timeIndex] { get { return _stepRates[timeIndex]; } }

        /// <summary>The instantaneous forward at a date in years (non-negative).</summary>
        [Kernel] public double this[alignedindex simId, double dateInYears] { get { return Evaluate(dateInYears); } }
    }
}
