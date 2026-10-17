using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A discount curve seen from a reference date. Times are year fractions from <see cref="ReferenceDate"/> measured
    /// with <see cref="DayCounter"/>; that day counter only measures time on the curve and is unrelated to the accrual
    /// conventions of the instruments priced on it. Rates are continuously compounded unless a <see cref="Compounding"/>
    /// says otherwise. Implementations are immutable and thread-safe; times must be finite and non-negative.
    /// </summary>
    public interface IYieldCurve
    {
        /// <summary>The date at which discount factors equal one (time zero).</summary>
        DateOnly ReferenceDate { get; }

        /// <summary>The convention turning dates into curve times. It must not need a <see cref="DayCountContext"/>.</summary>
        IDayCounter DayCounter { get; }

        /// <summary>The curve time of <paramref name="date"/>: the year fraction from <see cref="ReferenceDate"/>.</summary>
        double TimeFromReference(DateOnly date);

        /// <summary>The discount factor from <paramref name="date"/> back to the reference date.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="date"/> is before the reference date.</exception>
        double DiscountFactor(DateOnly date);

        /// <summary>The discount factor <c>P(0, t)</c>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="t"/> is negative or not finite.</exception>
        double DiscountFactor(double t);

        /// <summary>
        /// The zero rate to <paramref name="t"/>, i.e. the rate whose discount factor over <c>t</c> is <c>P(0, t)</c>. At
        /// <c>t = 0</c> it is the limit, which is the instantaneous forward at zero converted with
        /// <see cref="CompoundingExtensions.FromInstantaneous"/>.
        /// </summary>
        double ZeroRate(double t, Compounding compounding = Compounding.Continuous);

        /// <summary>
        /// The forward rate from <paramref name="t1"/> to <paramref name="t2"/>, i.e. the rate whose growth over
        /// <c>t2 - t1</c> is <c>P(0, t1) / P(0, t2)</c>. When <c>t1 = t2</c> it is the instantaneous forward
        /// converted with <see cref="CompoundingExtensions.FromInstantaneous"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="t2"/> is before <paramref name="t1"/>.</exception>
        double ForwardRate(double t1, double t2, Compounding compounding = Compounding.Continuous);

        /// <summary>
        /// The instantaneous forward rate <c>f(t) = -d ln P(0, t) / dt</c>. Where the curve has a kink (a pillar of a
        /// piecewise curve) the right derivative is returned, except at the last pillar where the left one is.
        /// </summary>
        double InstantaneousForward(double t);
    }

    /// <summary>Date-based conveniences on <see cref="IYieldCurve"/>.</summary>
    public static class YieldCurveExtensions
    {
        /// <summary>The zero rate to <paramref name="date"/>.</summary>
        public static double ZeroRate(this IYieldCurve curve, DateOnly date, Compounding compounding = Compounding.Continuous)
        {
            ArgumentNullException.ThrowIfNull(curve);
            return curve.ZeroRate(curve.TimeFromReference(date), compounding);
        }

        /// <summary>
        /// The forward rate between two dates with its own accrual convention: the rate <c>r</c> such that one unit
        /// invested at <paramref name="start"/> grows to <c>P(0, start) / P(0, end)</c> at <paramref name="end"/>, the
        /// accrual period being <c>tau = dayCounter.YearFraction(start, end)</c>. With simple compounding (the
        /// money-market default) it is <c>(P(0, start) / P(0, end) - 1) / tau</c>.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="end"/> is not after <paramref name="start"/>, or the accrual period is not positive.</exception>
        public static double ForwardRate(this IYieldCurve curve, DateOnly start, DateOnly end, IDayCounter dayCounter,
            Compounding compounding = Compounding.Simple)
        {
            ArgumentNullException.ThrowIfNull(curve);
            ArgumentNullException.ThrowIfNull(dayCounter);
            if (end <= start)
                throw new ArgumentException($"The end date {end} must be after the start date {start}.", nameof(end));
            var tau = dayCounter.YearFraction(start, end);
            if (!(tau > 0.0))
                throw new ArgumentException($"The accrual period {start} to {end} has a non-positive year fraction under {dayCounter.Name}.", nameof(dayCounter));
            return compounding.RateFromGrowth(curve.DiscountFactor(start) / curve.DiscountFactor(end), tau);
        }
    }
}
