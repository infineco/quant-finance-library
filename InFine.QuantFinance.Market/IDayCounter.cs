using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A day-count convention: converts a pair of dates into a number of days and into a year fraction.
    /// Implementations are immutable and thread-safe. When <c>end</c> is before <c>start</c> the results are the
    /// negation of those for the swapped dates.
    /// </summary>
    public interface IDayCounter
    {
        /// <summary>The conventional name, e.g. "Actual/360".</summary>
        string Name { get; }

        /// <summary>The number of days between <paramref name="start"/> and <paramref name="end"/> under the convention.</summary>
        int DayCount(DateOnly start, DateOnly end);

        /// <summary>
        /// The year fraction between <paramref name="start"/> and <paramref name="end"/>, for conventions that need no
        /// extra information.
        /// </summary>
        /// <exception cref="ArgumentException">The convention needs a reference period (Actual/Actual ICMA); use the overload with a <see cref="DayCountContext"/>.</exception>
        double YearFraction(DateOnly start, DateOnly end);

        /// <summary>
        /// The year fraction with the reference period, frequency and termination date the convention may need.
        /// Without a termination date in <paramref name="context"/>, 30E/360 ISDA treats <paramref name="end"/> as not being the termination date.
        /// </summary>
        /// <exception cref="ArgumentException">The convention needs context members that <paramref name="context"/> lacks or that are inconsistent.</exception>
        double YearFraction(DateOnly start, DateOnly end, DayCountContext context);
    }

    /// <summary>Convenience overloads of <see cref="IDayCounter"/>.</summary>
    public static class DayCounterExtensions
    {
        /// <summary>
        /// Year fraction with an explicit reference period (the form needed by Actual/Actual ICMA):
        /// <paramref name="refPeriodStart"/> to <paramref name="refPeriodEnd"/> is the regular period containing the accrual,
        /// <paramref name="frequency"/> the number of such periods per year.
        /// </summary>
        public static double YearFraction(this IDayCounter dayCounter, DateOnly start, DateOnly end,
            DateOnly refPeriodStart, DateOnly refPeriodEnd, Frequency frequency)
        {
            ArgumentNullException.ThrowIfNull(dayCounter);
            return dayCounter.YearFraction(start, end, new DayCountContext(refPeriodStart, refPeriodEnd, frequency));
        }
    }
}
