using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// The TARGET (T2) settlement calendar of the euro, as published by the ECB: besides weekends, T2 is closed on
    /// 1 January, Good Friday, Easter Monday, 1 May, 25 December and 26 December. The last four rules apply from 2000;
    /// 31 December was also closed in 1998, 1999 and 2001 (one-off decisions). Years before 1998 are not modelled
    /// (TARGET did not exist) and are treated as weekends-only apart from 1 January and 25 December.
    /// </summary>
    public sealed class TargetCalendar : HolidayCalendar
    {
        /// <summary>The shared instance.</summary>
        public static readonly TargetCalendar Instance = new();

        private TargetCalendar() { }

        /// <inheritdoc />
        public override string Name => "TARGET";

        /// <inheritdoc />
        protected override bool IsClosedWeekday(DateOnly date)
        {
            var (y, m, d) = (date.Year, date.Month, date.Day);
            if ((m == 1 && d == 1) || (m == 12 && d == 25)) return true;
            if (m == 12 && d == 31 && y is 1998 or 1999 or 2001) return true;
            if (y < 2000) return false;
            if ((m == 5 && d == 1) || (m == 12 && d == 26)) return true;
            var easter = DateRules.EasterSunday(y);
            return date == easter.AddDays(-2) || date == easter.AddDays(1);
        }
    }
}
