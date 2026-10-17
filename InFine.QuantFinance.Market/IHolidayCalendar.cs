using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A set of business days. Every calendar of the library treats Saturday and Sunday as non-business days;
    /// what distinguishes calendars is the set of additional weekday closures (holidays).
    /// Implementations must be immutable and thread-safe, and must not perform I/O.
    /// </summary>
    public interface IHolidayCalendar
    {
        /// <summary>A short human-readable name, for diagnostics.</summary>
        string Name { get; }

        /// <summary>
        /// True when the date is a weekday on which the market is closed. Always false on a Saturday or a Sunday:
        /// a holiday that falls on a weekend is not reported (use <see cref="IsBusinessDay"/> for "is it open").
        /// </summary>
        bool IsHoliday(DateOnly date);

        /// <summary>True when the date is neither a weekend day nor a holiday.</summary>
        bool IsBusinessDay(DateOnly date);
    }
}
