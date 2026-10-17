using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// Base class for calendars defined by a rule giving the weekday closures. Subclasses only decide which
    /// weekdays are closed; the weekend handling is shared.
    /// </summary>
    public abstract class HolidayCalendar : IHolidayCalendar
    {
        /// <inheritdoc />
        public abstract string Name { get; }

        /// <inheritdoc />
        public bool IsHoliday(DateOnly date) => date.IsWeekday() && IsClosedWeekday(date);

        /// <inheritdoc />
        public bool IsBusinessDay(DateOnly date) => date.IsWeekday() && !IsClosedWeekday(date);

        /// <inheritdoc />
        public override string ToString() => Name;

        /// <summary>True when the market is closed on this date, which is guaranteed to be a Monday to Friday.</summary>
        protected abstract bool IsClosedWeekday(DateOnly date);
    }
}
