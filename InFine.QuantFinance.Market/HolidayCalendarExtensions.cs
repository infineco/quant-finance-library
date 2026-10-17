using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// Business-day arithmetic available on every <see cref="IHolidayCalendar"/>.
    /// </summary>
    public static class HolidayCalendarExtensions
    {
        // A calendar closed for a whole year is certainly a bug (infinite loop guard).
        private const int MaxSearchDays = 366;

        /// <summary>
        /// Rolls <paramref name="date"/> onto a business day following <paramref name="convention"/>.
        /// A date that already is a business day is returned unchanged by every convention.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Unknown convention.</exception>
        /// <exception cref="InvalidOperationException">The calendar has no business day within a year of the date.</exception>
        public static DateOnly Adjust(this IHolidayCalendar calendar, DateOnly date, BusinessDayConvention convention)
        {
            ArgumentNullException.ThrowIfNull(calendar);
            switch (convention)
            {
                case BusinessDayConvention.Unadjusted:
                    return date;
                case BusinessDayConvention.Following:
                    return Roll(calendar, date, +1);
                case BusinessDayConvention.Preceding:
                    return Roll(calendar, date, -1);
                case BusinessDayConvention.ModifiedFollowing:
                {
                    var rolled = Roll(calendar, date, +1);
                    return rolled.Month == date.Month ? rolled : Roll(calendar, date, -1);
                }
                case BusinessDayConvention.ModifiedPreceding:
                {
                    var rolled = Roll(calendar, date, -1);
                    return rolled.Month == date.Month ? rolled : Roll(calendar, date, +1);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(convention), convention, "Unknown business-day convention.");
            }
        }

        /// <summary>
        /// Moves <paramref name="date"/> by <paramref name="businessDays"/> business days: each unit is a step to the next
        /// (or previous, if negative) business day, so the start date need not be a business day itself.
        /// Zero returns <paramref name="date"/> unchanged.
        /// </summary>
        public static DateOnly Advance(this IHolidayCalendar calendar, DateOnly date, int businessDays)
        {
            ArgumentNullException.ThrowIfNull(calendar);
            var step = Math.Sign(businessDays);
            for (var remaining = Math.Abs(businessDays); remaining > 0; remaining--)
                date = Roll(calendar, date.AddDays(step), step);
            return date;
        }

        /// <summary>
        /// Number of business days in the half-open interval [<paramref name="from"/>, <paramref name="to"/>):
        /// <paramref name="from"/> counts if it is a business day, <paramref name="to"/> does not.
        /// Negative when <paramref name="to"/> is before <paramref name="from"/>
        /// (the result is then the negation of the count in [<paramref name="to"/>, <paramref name="from"/>)).
        /// For a business day <c>d</c>, <c>BusinessDaysBetween(d, Advance(d, n)) == n</c> for any <c>n</c>.
        /// </summary>
        public static int BusinessDaysBetween(this IHolidayCalendar calendar, DateOnly from, DateOnly to)
        {
            ArgumentNullException.ThrowIfNull(calendar);
            if (to < from) return -calendar.BusinessDaysBetween(to, from);
            var count = 0;
            for (var d = from; d < to; d = d.AddDays(1))
                if (calendar.IsBusinessDay(d)) count++;
            return count;
        }

        private static DateOnly Roll(IHolidayCalendar calendar, DateOnly date, int direction)
        {
            for (var i = 0; i <= MaxSearchDays; i++, date = date.AddDays(direction))
                if (calendar.IsBusinessDay(date)) return date;
            throw new InvalidOperationException($"Calendar '{calendar}' has no business day within {MaxSearchDays} days.");
        }
    }
}
