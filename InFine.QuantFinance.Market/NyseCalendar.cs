using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// US equity-market calendar (NYSE full-day closings): New Year's Day, Martin Luther King Jr. Day (third Monday of
    /// January), Washington's Birthday (third Monday of February), Good Friday, Memorial Day (last Monday of May),
    /// Juneteenth (from 2022), Independence Day, Labor Day (first Monday of September), Thanksgiving (fourth Thursday of
    /// November) and Christmas Day.
    /// <para>
    /// Observance: a fixed-date holiday on a Saturday is observed the preceding Friday and on a Sunday the following
    /// Monday, with the NYSE exception that when 1 January is a Saturday the market is open on the preceding Friday
    /// 31 December (no observance).
    /// </para>
    /// <para>
    /// Rules are those in force since 1998 (the first Martin Luther King Jr. Day closing); earlier years are not
    /// modelled. One-off special closings (national days of mourning, 11 September 2001, Hurricane Sandy, ...) and
    /// early closes are out of scope.
    /// </para>
    /// </summary>
    public sealed class NyseCalendar : HolidayCalendar
    {
        /// <summary>The shared instance.</summary>
        public static readonly NyseCalendar Instance = new();

        private NyseCalendar() { }

        /// <inheritdoc />
        public override string Name => "NYSE";

        /// <inheritdoc />
        protected override bool IsClosedWeekday(DateOnly date)
        {
            var (y, m, d) = (date.Year, date.Month, date.Day);
            switch (m)
            {
                case 1:
                    // 1 Jan: Sunday -> Monday 2 Jan; Saturday -> no observance.
                    return IsObserved(date, 1, 1, observeOnFriday: false) || date == DateRules.NthWeekday(y, 1, DayOfWeek.Monday, 3);
                case 2:
                    return date == DateRules.NthWeekday(y, 2, DayOfWeek.Monday, 3);
                case 3:
                case 4:
                    return date == DateRules.EasterSunday(y).AddDays(-2);
                case 5:
                    return date == DateRules.LastWeekday(y, 5, DayOfWeek.Monday);
                case 6:
                    return y >= 2022 && IsObserved(date, 6, 19);
                case 7:
                    return IsObserved(date, 7, 4);
                case 9:
                    return date == DateRules.NthWeekday(y, 9, DayOfWeek.Monday, 1);
                case 11:
                    return date == DateRules.NthWeekday(y, 11, DayOfWeek.Thursday, 4);
                case 12:
                    return IsObserved(date, 12, 25);
                default:
                    return false;
            }
        }

        // True when 'date' is the observed day of the holiday on (date.Year, month, day).
        private static bool IsObserved(DateOnly date, int month, int day, bool observeOnFriday = true)
        {
            var holiday = new DateOnly(date.Year, month, day);
            return holiday.DayOfWeek switch
            {
                DayOfWeek.Saturday => observeOnFriday && date == holiday.AddDays(-1),
                DayOfWeek.Sunday => date == holiday.AddDays(1),
                _ => date == holiday,
            };
        }
    }
}
