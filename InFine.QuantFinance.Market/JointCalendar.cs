using System;
using System.Collections.Generic;
using System.Linq;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// Combination of several calendars for instruments settling in more than one centre: a date is a holiday when it is a
    /// holiday in any of the calendars, hence a business day only when it is a business day in all of them.
    /// </summary>
    public sealed class JointCalendar : IHolidayCalendar
    {
        private readonly IHolidayCalendar[] _calendars;

        /// <summary>Creates the union of the holidays of <paramref name="calendars"/> (at least one).</summary>
        /// <exception cref="ArgumentException">No calendar, or a null calendar.</exception>
        public JointCalendar(params IHolidayCalendar[] calendars) : this((IEnumerable<IHolidayCalendar>)calendars)
        {
        }

        /// <summary>Creates the union of the holidays of <paramref name="calendars"/> (at least one).</summary>
        /// <exception cref="ArgumentException">No calendar, or a null calendar.</exception>
        public JointCalendar(IEnumerable<IHolidayCalendar> calendars)
        {
            ArgumentNullException.ThrowIfNull(calendars);
            _calendars = calendars.ToArray();
            if (_calendars.Length == 0)
                throw new ArgumentException("A joint calendar needs at least one calendar.", nameof(calendars));
            if (_calendars.Any(c => c is null))
                throw new ArgumentException("Calendars must not be null.", nameof(calendars));
        }

        /// <summary>The component calendars, in construction order.</summary>
        public IReadOnlyList<IHolidayCalendar> Calendars => _calendars;

        /// <inheritdoc />
        public string Name => string.Join(" + ", _calendars.Select(c => c.Name));

        /// <inheritdoc />
        public bool IsHoliday(DateOnly date) => date.IsWeekday() && _calendars.Any(c => c.IsHoliday(date));

        /// <inheritdoc />
        public bool IsBusinessDay(DateOnly date) => _calendars.All(c => c.IsBusinessDay(date));

        /// <inheritdoc />
        public override string ToString() => Name;
    }
}
