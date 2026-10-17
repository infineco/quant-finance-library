using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// An immutable sequence of schedule dates, unadjusted and adjusted for business days, built by
    /// <see cref="ScheduleBuilder"/>. N dates delimit N-1 accrual periods.
    /// </summary>
    public sealed class Schedule
    {
        private readonly DateOnly[] _unadjusted;
        private readonly DateOnly[] _adjusted;
        private readonly bool[] _regular;
        private readonly bool _endOfMonth;

        internal Schedule(DateOnly[] unadjusted, DateOnly[] adjusted, bool[] regular, Period? tenor, DateGenerationRule rule,
            bool endOfMonth, IHolidayCalendar calendar, BusinessDayConvention convention, BusinessDayConvention terminationConvention)
        {
            _unadjusted = unadjusted;
            _adjusted = adjusted;
            _regular = regular;
            Tenor = tenor;
            Rule = rule;
            _endOfMonth = endOfMonth;
            Calendar = calendar;
            Convention = convention;
            TerminationConvention = terminationConvention;
        }

        /// <summary>The generated dates before business-day adjustment, strictly increasing.</summary>
        public IReadOnlyList<DateOnly> UnadjustedDates => _unadjusted;

        /// <summary>The dates rolled onto business days (the last one with <see cref="TerminationConvention"/>).</summary>
        public IReadOnlyList<DateOnly> AdjustedDates => _adjusted;

        /// <summary>Number of dates, i.e. <see cref="PeriodCount"/> + 1.</summary>
        public int Count => _unadjusted.Length;

        /// <summary>Number of accrual periods.</summary>
        public int PeriodCount => _unadjusted.Length - 1;

        /// <summary>The regular step between dates; <c>null</c> for a single-period (zero-coupon) schedule.</summary>
        public Period? Tenor { get; }

        /// <summary>The rule the dates were generated with.</summary>
        public DateGenerationRule Rule { get; }

        /// <summary>The calendar used for adjustment.</summary>
        public IHolidayCalendar Calendar { get; }

        /// <summary>The convention applied to every date but the last.</summary>
        public BusinessDayConvention Convention { get; }

        /// <summary>The convention applied to the last date.</summary>
        public BusinessDayConvention TerminationConvention { get; }

        /// <summary>
        /// True when period <paramref name="periodIndex"/> (0-based) is a regular one, one tenor long on the generation grid.
        /// False for a stub. A single-period schedule without tenor counts as regular.
        /// </summary>
        public bool IsRegular(int periodIndex)
        {
            if ((uint)periodIndex >= (uint)PeriodCount) throw new ArgumentOutOfRangeException(nameof(periodIndex));
            return _regular[periodIndex];
        }

        /// <summary>
        /// The accrual periods with their year fractions under <paramref name="dayCounter"/>.
        /// </summary>
        /// <param name="dayCounter">The convention.</param>
        /// <param name="useAdjustedDates">
        /// True (default) to accrue between adjusted dates, false between unadjusted ones. The termination date handed to the
        /// day counter and the Actual/Actual ICMA reference periods are on the same basis: for a stub the reference period is the
        /// notional regular period next to it (adjusted too when <paramref name="useAdjustedDates"/>), and a long stub is split
        /// over two notional periods. ICMA needs a month-based tenor dividing a year, otherwise the day counter throws.
        /// </param>
        public IReadOnlyList<AccrualPeriod> GetAccrualPeriods(IDayCounter dayCounter, bool useAdjustedDates = true)
        {
            ArgumentNullException.ThrowIfNull(dayCounter);
            var dates = useAdjustedDates ? _adjusted : _unadjusted;
            var result = new AccrualPeriod[PeriodCount];
            for (var i = 0; i < result.Length; i++)
                result[i] = new AccrualPeriod(_unadjusted[i], _unadjusted[i + 1], _adjusted[i], _adjusted[i + 1],
                    dayCounter.YearFraction(dates[i], dates[i + 1], DayCountContextOf(i, useAdjustedDates)), _regular[i]);
            return result;
        }

        /// <summary>
        /// The day-count context (termination date, and ICMA reference period and frequency when the schedule has a standard
        /// tenor) of period <paramref name="index"/>, on adjusted or unadjusted dates; what <see cref="GetAccrualPeriods"/> passes
        /// to the day counter, exposed so that partial-period accruals (bond accrued interest) use the same reference period.
        /// </summary>
        internal DayCountContext DayCountContextOf(int index, bool useAdjustedDates)
        {
            var context = new DayCountContext(TerminationDate: useAdjustedDates ? _adjusted[^1] : _unadjusted[^1]);
            if (Tenor is not { } tenor || !tenor.TryGetFrequency(out var frequency)) return context;
            var (refStart, refEnd) = ReferencePeriod(index);
            if (useAdjustedDates)
            {
                refStart = Calendar.Adjust(refStart, Convention);
                refEnd = Calendar.Adjust(refEnd, refEnd == _unadjusted[^1] ? TerminationConvention : Convention);
            }
            return context with { RefPeriodStart = refStart, RefPeriodEnd = refEnd, Frequency = frequency };
        }

        // Unadjusted ICMA reference period of a period: itself when regular, else the notional regular period adjacent to
        // the stub, on the side where the grid continues (a front stub is anchored on its end, any other stub on its start).
        private (DateOnly Start, DateOnly End) ReferencePeriod(int index)
        {
            var start = _unadjusted[index];
            var end = _unadjusted[index + 1];
            if (_regular[index]) return (start, end);
            var anchorAtEnd = index == 0 && (PeriodCount > 1 || Rule == DateGenerationRule.Backward);
            return anchorAtEnd
                ? (ScheduleBuilder.Shift(Tenor!.Value, Rule, _endOfMonth, end, -1), end)
                : (start, ScheduleBuilder.Shift(Tenor!.Value, Rule, _endOfMonth, start, +1));
        }
    }
}
