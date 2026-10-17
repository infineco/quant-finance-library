using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// Rule-based date generators used by holiday calendars and schedule generation: nth weekday of a month,
    /// IMM dates and the Gregorian Easter Sunday.
    /// </summary>
    public static class DateRules
    {
        /// <summary>
        /// The <paramref name="n"/>-th (1 to 5) occurrence of <paramref name="dayOfWeek"/> in the given month.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="n"/> is not in 1..5, or the month has fewer than <paramref name="n"/> such weekdays.
        /// </exception>
        public static DateOnly NthWeekday(int year, int month, DayOfWeek dayOfWeek, int n)
        {
            if (n is < 1 or > 5)
                throw new ArgumentOutOfRangeException(nameof(n), n, "n must be between 1 and 5.");
            var first = new DateOnly(year, month, 1);
            var offset = ((int)dayOfWeek - (int)first.DayOfWeek + 7) % 7;
            var day = 1 + offset + 7 * (n - 1);
            if (day > DateTime.DaysInMonth(year, month))
                throw new ArgumentOutOfRangeException(nameof(n), n, $"The month has no occurrence number {n} of {dayOfWeek}.");
            return new DateOnly(year, month, day);
        }

        /// <summary>The last occurrence of <paramref name="dayOfWeek"/> in the given month.</summary>
        public static DateOnly LastWeekday(int year, int month, DayOfWeek dayOfWeek)
        {
            var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            var back = ((int)last.DayOfWeek - (int)dayOfWeek + 7) % 7;
            return last.AddDays(-back);
        }

        /// <summary>
        /// The IMM date of a given IMM month: the third Wednesday of March, June, September or December.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="month"/> is not 3, 6, 9 or 12.</exception>
        public static DateOnly ImmDate(int year, int month)
        {
            if (month % 3 != 0)
                throw new ArgumentOutOfRangeException(nameof(month), month, "IMM months are March, June, September and December.");
            return NthWeekday(year, month, DayOfWeek.Wednesday, 3);
        }

        /// <summary>True when the date is an IMM date (third Wednesday of March, June, September or December).</summary>
        public static bool IsImmDate(DateOnly date) =>
            date.Month % 3 == 0 && date == ImmDate(date.Year, date.Month);

        /// <summary>The first IMM date strictly after <paramref name="date"/>.</summary>
        public static DateOnly NextImmDate(DateOnly date)
        {
            var year = date.Year;
            for (var month = (date.Month + 2) / 3 * 3; ; month += 3)
            {
                if (month > 12) { month = 3; year++; }
                var candidate = ImmDate(year, month);
                if (candidate > date) return candidate;
            }
        }

        /// <summary>
        /// Easter Sunday of the Gregorian calendar for <paramref name="year"/>, by the anonymous Gregorian algorithm
        /// (Meeus/Jones/Butcher), valid for every year from 1583.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="year"/> is before 1583 or after 9999.</exception>
        public static DateOnly EasterSunday(int year)
        {
            if (year is < 1583 or > 9999)
                throw new ArgumentOutOfRangeException(nameof(year), year, "The Gregorian computus is defined from 1583 to 9999.");
            var a = year % 19;
            var b = year / 100;
            var c = year % 100;
            var d = b / 4;
            var e = b % 4;
            var f = (b + 8) / 25;
            var g = (b - f + 1) / 3;
            var h = (19 * a + b - d - g + 15) % 30;
            var i = c / 4;
            var k = c % 4;
            var l = (32 + 2 * e + 2 * i - h - k) % 7;
            var m = (a + 11 * h + 22 * l) / 451;
            var month = (h + l - 7 * m + 114) / 31;
            var day = (h + l - 7 * m + 114) % 31 + 1;
            return new DateOnly(year, month, day);
        }
    }
}
