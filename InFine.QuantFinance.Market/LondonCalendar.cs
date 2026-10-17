using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// UK (England and Wales) bank-holiday calendar, used for London business days: New Year's Day, Good Friday, Easter
    /// Monday, Early May (first Monday of May), Spring (last Monday of May), Summer (last Monday of August), Christmas Day
    /// and Boxing Day.
    /// <para>
    /// Substitution: New Year's Day on a weekend moves to the following Monday; Christmas Day on a Saturday or Sunday
    /// moves to 27 December and Boxing Day on a Saturday or Sunday moves to 28 December.
    /// </para>
    /// <para>
    /// One-off changes included: Early May on 8 May in 1995 and 2020 (VE Day anniversary); the Golden Jubilee (2002),
    /// royal wedding (29 April 2011), Diamond Jubilee (2012), Platinum Jubilee (2022: Spring holiday moved to 2 June plus
    /// 3 June), State funeral of Queen Elizabeth II (19 September 2022), coronation of Charles III (8 May 2023) and the
    /// millennium (31 December 1999). This list of special days is NOT exhaustive for years before 1995; Scottish and
    /// Northern Irish holidays are not modelled.
    /// </para>
    /// </summary>
    public sealed class LondonCalendar : HolidayCalendar
    {
        /// <summary>The shared instance.</summary>
        public static readonly LondonCalendar Instance = new();

        private LondonCalendar() { }

        /// <inheritdoc />
        public override string Name => "London";

        /// <inheritdoc />
        protected override bool IsClosedWeekday(DateOnly date)
        {
            var (y, m, d) = (date.Year, date.Month, date.Day);
            switch (m)
            {
                case 1:
                    // 1 Jan, moved to the next Monday when it falls on a weekend.
                    return date == FirstWeekdayOnOrAfter(new DateOnly(y, 1, 1));
                case 3:
                case 4:
                {
                    var easter = DateRules.EasterSunday(y);
                    if (date == easter.AddDays(-2) || date == easter.AddDays(1)) return true;
                    return m == 4 && y == 2011 && d == 29;
                }
                case 5:
                    return IsMayHoliday(date);
                case 6:
                    // Golden Jubilee 2002, Diamond Jubilee 2012, Platinum Jubilee 2022.
                    return (y, d) is (2002, 3 or 4) or (2012, 4 or 5) or (2022, 2 or 3);
                case 8:
                    return date == DateRules.LastWeekday(y, 8, DayOfWeek.Monday);
                case 9:
                    return y == 2022 && d == 19;
                case 12:
                    return d switch
                    {
                        25 or 26 or 27 or 28 => IsChristmasHoliday(date),
                        31 => y == 1999,
                        _ => false,
                    };
                default:
                    return false;
            }
        }

        private static bool IsMayHoliday(DateOnly date)
        {
            var y = date.Year;
            // Early May bank holiday.
            var early = y is 1995 or 2020 ? new DateOnly(y, 5, 8) : DateRules.NthWeekday(y, 5, DayOfWeek.Monday, 1);
            if (date == early) return true;
            if (y == 2023 && date.Day == 8) return true; // Coronation of Charles III.
            // Spring bank holiday: last Monday of May, except the Jubilee years where it moves into June.
            return y is not (2002 or 2012 or 2022) && date == DateRules.LastWeekday(y, 5, DayOfWeek.Monday);
        }

        private static bool IsChristmasHoliday(DateOnly date)
        {
            var christmas = new DateOnly(date.Year, 12, 25);
            var boxing = new DateOnly(date.Year, 12, 26);
            var christmasObserved = christmas.IsWeekday() ? christmas : new DateOnly(date.Year, 12, 27);
            var boxingObserved = boxing.IsWeekday() ? boxing : new DateOnly(date.Year, 12, 28);
            return date == christmasObserved || date == boxingObserved;
        }

        private static DateOnly FirstWeekdayOnOrAfter(DateOnly date) =>
            date.DayOfWeek switch
            {
                DayOfWeek.Saturday => date.AddDays(2),
                DayOfWeek.Sunday => date.AddDays(1),
                _ => date,
            };
    }
}
