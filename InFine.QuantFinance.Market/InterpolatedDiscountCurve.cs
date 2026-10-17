using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A discount curve defined by discount factors at pillar times <c>0 &lt; t_1 &lt; ... &lt; t_n</c> and a
    /// <see cref="CurveInterpolation"/> in between.
    /// <list type="bullet">
    /// <item>Before the first pillar the forward is flat: <c>P(0, t) = exp(-z_1 t)</c> with <c>z_1</c> the first zero rate
    /// (for <see cref="InterpolatedQuantity.ZeroRate"/> the zero rate is held constant; for
    /// <see cref="InterpolatedQuantity.LogDiscountFactor"/> the node <c>(0, 0)</c> gives the same result with any interpolator
    /// that is linear on two nodes). In a bootstrapped curve this is what fills the gap between the reference date and the
    /// spot date of spot-starting instruments.</item>
    /// <item>Beyond the last pillar the forward is flat at the instantaneous forward of the last pillar (left derivative):
    /// <c>P(0, t) = P(0, t_n) exp(-f_n (t - t_n))</c>. This keeps the instantaneous forward continuous at <c>t_n</c>
    /// whenever it is continuous on the left of it.</item>
    /// </list>
    /// </summary>
    public sealed class InterpolatedDiscountCurve : YieldCurve
    {
        private readonly double[] _times;
        private readonly double[] _discountFactors;
        private readonly DateOnly[]? _dates;
        private readonly IInterpolation _interpolation;
        private readonly double _firstZero;
        private readonly double _lastTime;
        private readonly double _lastLogDiscount;
        private readonly double _lastForward;

        /// <summary>Creates the curve from pillar dates (converted to times with <paramref name="dayCounter"/>).</summary>
        /// <exception cref="ArgumentException">See <see cref="InterpolatedDiscountCurve(DateOnly, IDayCounter, IReadOnlyList{double}, IReadOnlyList{double}, CurveInterpolation)"/>.</exception>
        public InterpolatedDiscountCurve(DateOnly referenceDate, IDayCounter dayCounter, IReadOnlyList<DateOnly> pillarDates,
            IReadOnlyList<double> discountFactors, CurveInterpolation interpolation)
            : this(referenceDate, dayCounter, TimesOf(referenceDate, dayCounter, pillarDates), ToArray(pillarDates),
                ToArray(discountFactors), interpolation)
        {
        }

        /// <summary>Creates the curve from pillar times.</summary>
        /// <exception cref="ArgumentException">
        /// No pillar, lengths differ, times not finite, positive and strictly increasing, or discount factors not finite and positive.
        /// </exception>
        public InterpolatedDiscountCurve(DateOnly referenceDate, IDayCounter dayCounter, IReadOnlyList<double> pillarTimes,
            IReadOnlyList<double> discountFactors, CurveInterpolation interpolation)
            : this(referenceDate, dayCounter, ToArray(pillarTimes), null, ToArray(discountFactors), interpolation)
        {
        }

        // Takes ownership of the arrays.
        internal InterpolatedDiscountCurve(DateOnly referenceDate, IDayCounter dayCounter, double[] times, DateOnly[]? dates,
            double[] discountFactors, CurveInterpolation interpolation)
            : base(referenceDate, dayCounter)
        {
            ArgumentNullException.ThrowIfNull(interpolation);
            Validate(times, discountFactors);
            _times = times;
            _dates = dates;
            _discountFactors = discountFactors;
            Interpolation = interpolation;
            var n = times.Length;

            if (interpolation.Quantity == InterpolatedQuantity.ZeroRate)
            {
                var zeros = new double[n];
                for (var i = 0; i < n; i++)
                    zeros[i] = -Math.Log(discountFactors[i]) / times[i];
                _interpolation = interpolation.Interpolator.Interpolate(times, zeros);
                _firstZero = zeros[0];
                _lastForward = zeros[n - 1] + times[n - 1] * _interpolation.Derivative(times[n - 1]);
            }
            else
            {
                var x = new double[n + 1];
                var y = new double[n + 1];
                for (var i = 0; i < n; i++)
                {
                    x[i + 1] = times[i];
                    y[i + 1] = Math.Log(discountFactors[i]);
                }
                _interpolation = interpolation.Interpolator.Interpolate(x, y);
                _firstZero = -y[1] / x[1];
                _lastForward = -_interpolation.Derivative(times[n - 1]);
            }
            _lastTime = times[n - 1];
            _lastLogDiscount = Math.Log(discountFactors[n - 1]);
        }

        /// <summary>The pillar times, strictly increasing and positive.</summary>
        public IReadOnlyList<double> PillarTimes => _times;

        /// <summary>The pillar dates, or <c>null</c> when the curve was built from times.</summary>
        public IReadOnlyList<DateOnly>? PillarDates => _dates;

        /// <summary>The discount factors at the pillars.</summary>
        public IReadOnlyList<double> DiscountFactors => _discountFactors;

        /// <summary>The interpolation between pillars.</summary>
        public CurveInterpolation Interpolation { get; }

        /// <summary>The last pillar time, beyond which the curve extrapolates with a flat forward.</summary>
        public double MaxPillarTime => _lastTime;

        /// <inheritdoc />
        protected override double DiscountFactorCore(double t)
        {
            if (t > _lastTime)
                return Math.Exp(_lastLogDiscount - _lastForward * (t - _lastTime));
            if (Interpolation.Quantity == InterpolatedQuantity.LogDiscountFactor)
                return Math.Exp(_interpolation.Value(t));
            var z = t <= _times[0] ? _firstZero : _interpolation.Value(t);
            return Math.Exp(-z * t);
        }

        /// <inheritdoc />
        protected override double InstantaneousForwardCore(double t)
        {
            if (t > _lastTime) return _lastForward;
            if (Interpolation.Quantity == InterpolatedQuantity.LogDiscountFactor)
                return -_interpolation.Derivative(t);
            // -ln P = z(t) t, so f = z + t z'.
            if (t < _times[0]) return _firstZero;
            return _interpolation.Value(t) + t * _interpolation.Derivative(t);
        }

        private static void Validate(double[] times, double[] discountFactors)
        {
            if (times.Length == 0)
                throw new ArgumentException("A curve needs at least one pillar.", nameof(times));
            if (times.Length != discountFactors.Length)
                throw new ArgumentException($"{times.Length} pillars but {discountFactors.Length} discount factors.", nameof(discountFactors));
            for (var i = 0; i < times.Length; i++)
            {
                if (!double.IsFinite(times[i]) || !(times[i] > (i == 0 ? 0.0 : times[i - 1])))
                    throw new ArgumentException($"Pillar times must be finite, positive and strictly increasing (pillar {i}: {times[i]}).", nameof(times));
                if (!double.IsFinite(discountFactors[i]) || !(discountFactors[i] > 0.0))
                    throw new ArgumentException($"Discount factors must be finite and positive (pillar {i}: {discountFactors[i]}).", nameof(discountFactors));
            }
        }

        private static double[] TimesOf(DateOnly referenceDate, IDayCounter dayCounter, IReadOnlyList<DateOnly> dates)
        {
            ArgumentNullException.ThrowIfNull(dayCounter);
            ArgumentNullException.ThrowIfNull(dates);
            var times = new double[dates.Count];
            for (var i = 0; i < times.Length; i++)
                times[i] = dayCounter.YearFraction(referenceDate, dates[i]);
            return times;
        }

        private static T[] ToArray<T>(IReadOnlyList<T> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var result = new T[values.Count];
            for (var i = 0; i < result.Length; i++) result[i] = values[i];
            return result;
        }
    }
}
