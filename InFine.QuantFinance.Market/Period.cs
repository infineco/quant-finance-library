using System;
using System.Globalization;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A tenor such as "3M", "1Y", "2W" or "10D": a signed integer length and a <see cref="PeriodUnit"/>.
    /// Equality is structural, so "12M" and "1Y" are different periods even though they move a date identically.
    /// </summary>
    public readonly struct Period : IEquatable<Period>
    {
        /// <summary>Creates a period of <paramref name="length"/> units; negative lengths go back in time.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Unknown unit.</exception>
        public Period(int length, PeriodUnit unit)
        {
            if (!Enum.IsDefined(unit))
                throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown period unit.");
            Length = length;
            Unit = unit;
        }

        /// <summary>The signed number of units.</summary>
        public int Length { get; }

        /// <summary>The unit of <see cref="Length"/>.</summary>
        public PeriodUnit Unit { get; }

        /// <summary>
        /// Parses a tenor: an optionally signed integer followed by D, W, M or Y (case-insensitive), e.g.
        /// "10D", "2W", "3M", "-1Y". Surrounding white space is ignored.
        /// </summary>
        /// <exception cref="FormatException">The text is not a valid tenor.</exception>
        public static Period Parse(string text) =>
            TryParse(text, out var period) ? period : throw new FormatException($"'{text}' is not a valid period (expected e.g. 10D, 2W, 3M, 1Y).");

        /// <summary>Tries to parse a tenor, see <see cref="Parse"/>.</summary>
        public static bool TryParse(string? text, out Period period)
        {
            period = default;
            if (text is null) return false;
            var s = text.Trim();
            if (s.Length < 2) return false;
            PeriodUnit unit;
            switch (char.ToUpperInvariant(s[^1]))
            {
                case 'D': unit = PeriodUnit.Days; break;
                case 'W': unit = PeriodUnit.Weeks; break;
                case 'M': unit = PeriodUnit.Months; break;
                case 'Y': unit = PeriodUnit.Years; break;
                default: return false;
            }
            var number = s[..^1];
            if (!int.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var length))
                return false;
            period = new Period(length, unit);
            return true;
        }

        /// <summary>Number of months of a month- or year-based period; <c>null</c> for days and weeks.</summary>
        public int? TotalMonths => Unit switch
        {
            PeriodUnit.Months => Length,
            PeriodUnit.Years => 12 * Length,
            _ => null,
        };

        /// <summary>The period traversed in the opposite direction.</summary>
        public Period Negate() => new(-Length, Unit);

        /// <summary>This period repeated <paramref name="factor"/> times.</summary>
        public Period Multiply(int factor) => new(checked(Length * factor), Unit);

        /// <summary>
        /// The frequency equal to this period: a month or year period dividing a year (1Y, 6M, 4M, 3M, 2M, 1M).
        /// </summary>
        public bool TryGetFrequency(out Frequency frequency)
        {
            frequency = Frequency.Once;
            var months = TotalMonths;
            if (months is null or <= 0 || 12 % months.Value != 0) return false;
            var perYear = 12 / months.Value;
            if (!Enum.IsDefined((Frequency)perYear)) return false;
            frequency = (Frequency)perYear;
            return true;
        }

        /// <summary>
        /// Adds the period to <paramref name="date"/>. Days and weeks are calendar days (<paramref name="endOfMonth"/>
        /// is ignored). Months and years keep the day number, clamped to the target month length; when
        /// <paramref name="endOfMonth"/> is set and <paramref name="date"/> is a month end, the result is the month end
        /// of the target month (see <see cref="DateOnlyExtensions.AddMonths(DateOnly, int, bool)"/>).
        /// </summary>
        public DateOnly AddTo(DateOnly date, bool endOfMonth = false) => Unit switch
        {
            PeriodUnit.Days => date.AddDays(Length),
            PeriodUnit.Weeks => date.AddDays(checked(7 * Length)),
            PeriodUnit.Months => date.AddMonths(Length, endOfMonth),
            PeriodUnit.Years => date.AddMonths(checked(12 * Length), endOfMonth),
            _ => throw new InvalidOperationException("Default-initialised or invalid period unit."),
        };

        /// <summary>
        /// Adds the period then, when <paramref name="calendar"/> is not <c>null</c>, rolls the result onto a business day
        /// with <paramref name="convention"/>. A <c>null</c> calendar returns the unadjusted result.
        /// </summary>
        public DateOnly AddTo(DateOnly date, IHolidayCalendar? calendar, BusinessDayConvention convention, bool endOfMonth = false)
        {
            var result = AddTo(date, endOfMonth);
            return calendar is null ? result : calendar.Adjust(result, convention);
        }

        /// <summary>Formats as the tenor string accepted by <see cref="Parse"/>, e.g. "3M".</summary>
        public override string ToString() =>
            Length.ToString(CultureInfo.InvariantCulture) + Unit switch
            {
                PeriodUnit.Days => "D",
                PeriodUnit.Weeks => "W",
                PeriodUnit.Months => "M",
                _ => "Y",
            };

        /// <inheritdoc />
        public bool Equals(Period other) => Length == other.Length && Unit == other.Unit;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Period other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Length, Unit);

        /// <summary>Structural equality.</summary>
        public static bool operator ==(Period left, Period right) => left.Equals(right);

        /// <summary>Structural inequality.</summary>
        public static bool operator !=(Period left, Period right) => !left.Equals(right);
    }
}
