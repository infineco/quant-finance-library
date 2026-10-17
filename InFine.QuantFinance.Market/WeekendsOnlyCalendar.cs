using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>A calendar with no holidays: every Monday to Friday is a business day.</summary>
    public sealed class WeekendsOnlyCalendar : HolidayCalendar
    {
        /// <summary>The shared instance.</summary>
        public static readonly WeekendsOnlyCalendar Instance = new();

        private WeekendsOnlyCalendar() { }

        /// <inheritdoc />
        public override string Name => "Weekends only";

        /// <inheritdoc />
        protected override bool IsClosedWeekday(DateOnly date) => false;
    }
}
