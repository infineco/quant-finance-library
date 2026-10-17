using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// Calendar-arithmetic helpers on <see cref="DateOnly"/>. All methods are pure and thread-safe.
    /// </summary>
    public static class DateOnlyExtensions
    {
        /// <summary>True when the date is a Saturday or a Sunday.</summary>
        public static bool IsWeekend(this DateOnly date) =>
            date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        /// <summary>True when the date is a Monday to Friday (the complement of <see cref="IsWeekend"/>).</summary>
        public static bool IsWeekday(this DateOnly date) => !date.IsWeekend();

        /// <summary>True when the date is the last calendar day of its month.</summary>
        public static bool IsEndOfMonth(this DateOnly date) =>
            date.Day == DateTime.DaysInMonth(date.Year, date.Month);

        /// <summary>The last calendar day of the month containing <paramref name="date"/>.</summary>
        public static DateOnly EndOfMonth(this DateOnly date) =>
            new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));

        /// <summary>
        /// Adds <paramref name="months"/> calendar months. When <paramref name="endOfMonthRule"/> is set and
        /// <paramref name="date"/> is the last day of its month, the result is the last day of the target month
        /// (30 Jun + 2 months = 31 Aug, not 30 Aug). Otherwise the day number is kept, clamped to the length of the
        /// target month (31 Jan + 1 month = 28/29 Feb), exactly like <see cref="DateOnly.AddMonths(int)"/>.
        /// </summary>
        public static DateOnly AddMonths(this DateOnly date, int months, bool endOfMonthRule)
        {
            var result = date.AddMonths(months);
            return endOfMonthRule && date.IsEndOfMonth() ? result.EndOfMonth() : result;
        }
    }
}
