using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A curve whose continuously compounded zero rates are those of a base curve plus a time-dependent shift
    /// <c>s(t)</c>: <c>P(0, t) = P_base(0, t) exp(-s(t) t)</c>. A constant shift is a parallel bump (or a Z-spread); a hat-shaped
    /// shift is a key-rate bump. Same reference date and time convention as the base curve.
    /// </summary>
    public sealed class ZeroShiftedCurve : YieldCurve
    {
        private readonly IYieldCurve _base;
        private readonly Func<double, double> _shift;
        private readonly Func<double, double> _shiftDerivative;

        /// <summary>A curve shifted by <paramref name="shift"/>(t), whose derivative is <paramref name="shiftDerivative"/>(t).</summary>
        public ZeroShiftedCurve(IYieldCurve baseCurve, Func<double, double> shift, Func<double, double> shiftDerivative)
            : base((baseCurve ?? throw new ArgumentNullException(nameof(baseCurve))).ReferenceDate, baseCurve.DayCounter)
        {
            ArgumentNullException.ThrowIfNull(shift);
            ArgumentNullException.ThrowIfNull(shiftDerivative);
            _base = baseCurve;
            _shift = shift;
            _shiftDerivative = shiftDerivative;
        }

        /// <summary>The curve with every continuously compounded zero rate raised by <paramref name="shift"/>.</summary>
        public static ZeroShiftedCurve Parallel(IYieldCurve baseCurve, double shift) =>
            new(baseCurve, _ => shift, _ => 0.0);

        /// <summary>
        /// The curve with the zero rates raised by <paramref name="shift"/> times the triangular weight of key time
        /// <paramref name="index"/> among <paramref name="keyTimes"/> (strictly increasing). The weights of all key times sum
        /// to one everywhere (the first is flat to the left of its time, the last flat to the right of its), so shifting every key
        /// time by the same amount is the parallel shift.
        /// </summary>
        public static ZeroShiftedCurve KeyRate(IYieldCurve baseCurve, IReadOnlyList<double> keyTimes, int index, double shift)
        {
            ArgumentNullException.ThrowIfNull(keyTimes);
            if ((uint)index >= (uint)keyTimes.Count) throw new ArgumentOutOfRangeException(nameof(index));
            for (var i = 1; i < keyTimes.Count; i++)
                if (!(keyTimes[i] > keyTimes[i - 1]))
                    throw new ArgumentException("Key times must be strictly increasing.", nameof(keyTimes));
            var times = new double[keyTimes.Count];
            for (var i = 0; i < times.Length; i++) times[i] = keyTimes[i];
            return new ZeroShiftedCurve(baseCurve, t => shift * Weight(times, index, t), t => shift * WeightDerivative(times, index, t));
        }

        /// <inheritdoc />
        protected override double DiscountFactorCore(double t) => _base.DiscountFactor(t) * Math.Exp(-_shift(t) * t);

        /// <inheritdoc />
        protected override double InstantaneousForwardCore(double t) =>
            _base.InstantaneousForward(t) + _shift(t) + t * _shiftDerivative(t);

        private static double Weight(double[] times, int k, double t)
        {
            if (k > 0 && t < times[k - 1]) return 0.0;
            if (k > 0 && t <= times[k]) return (t - times[k - 1]) / (times[k] - times[k - 1]);
            if (k == 0 && t <= times[0]) return 1.0;
            if (k == times.Length - 1) return 1.0;
            if (t >= times[k + 1]) return 0.0;
            return (times[k + 1] - t) / (times[k + 1] - times[k]);
        }

        // Right derivative (the weights are piecewise linear).
        private static double WeightDerivative(double[] times, int k, double t)
        {
            if (k > 0 && t >= times[k - 1] && t < times[k]) return 1.0 / (times[k] - times[k - 1]);
            if (k < times.Length - 1 && t >= times[k] && t < times[k + 1]) return -1.0 / (times[k + 1] - times[k]);
            return 0.0;
        }
    }
}
