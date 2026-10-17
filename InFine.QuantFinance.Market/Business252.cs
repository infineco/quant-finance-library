using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// Business/252 (Brazilian market): the number of business days of a calendar in [start, end) over 252.
    /// </summary>
    public sealed class Business252 : DayCounter
    {
        private readonly IHolidayCalendar _calendar;

        /// <summary>Creates the convention counting business days of <paramref name="calendar"/>.</summary>
        public Business252(IHolidayCalendar calendar)
        {
            ArgumentNullException.ThrowIfNull(calendar);
            _calendar = calendar;
        }

        /// <inheritdoc />
        public override string Name => $"Business/252 ({_calendar.Name})";

        /// <inheritdoc />
        protected override int DayCountCore(DateOnly start, DateOnly end, in DayCountContext context) =>
            _calendar.BusinessDaysBetween(start, end);

        /// <inheritdoc />
        protected override double YearFractionCore(DateOnly start, DateOnly end, in DayCountContext context) =>
            DayCountCore(start, end, context) / 252.0;
    }
}
