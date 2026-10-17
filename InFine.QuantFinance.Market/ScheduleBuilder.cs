using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// Fluent builder of <see cref="Schedule"/>. Defaults: Backward generation, Short stub, WeekendsOnly calendar,
    /// ModifiedFollowing for every date (the termination date uses the same convention unless
    /// <see cref="WithTerminationConvention"/> says otherwise), no end-of-month rule, and a single period
    /// (<see cref="Frequency.Once"/>) until a tenor or frequency is given.
    /// </summary>
    public sealed class ScheduleBuilder
    {
        private readonly DateOnly _effective;
        private readonly DateOnly _termination;
        private Period? _tenor;
        private DateGenerationRule _rule = DateGenerationRule.Backward;
        private StubPolicy _stub = StubPolicy.Short;
        private IHolidayCalendar _calendar = WeekendsOnlyCalendar.Instance;
        private BusinessDayConvention _convention = BusinessDayConvention.ModifiedFollowing;
        private BusinessDayConvention? _terminationConvention;
        private bool _endOfMonth;
        private DateOnly? _firstDate;
        private DateOnly? _nextToLastDate;

        /// <summary>Starts a schedule running from <paramref name="effectiveDate"/> to <paramref name="terminationDate"/> (unadjusted).</summary>
        public ScheduleBuilder(DateOnly effectiveDate, DateOnly terminationDate)
        {
            _effective = effectiveDate;
            _termination = terminationDate;
        }

        /// <summary>Sets the regular step between dates, e.g. 6 months. Must be positive.</summary>
        public ScheduleBuilder WithTenor(Period tenor)
        {
            _tenor = tenor;
            return this;
        }

        /// <summary>Sets the regular step from a frequency; <see cref="Frequency.Once"/> gives a single period.</summary>
        public ScheduleBuilder WithFrequency(Frequency frequency)
        {
            _tenor = frequency == Frequency.Once ? null : frequency.ToPeriod();
            return this;
        }

        /// <summary>Sets the date generation rule.</summary>
        public ScheduleBuilder WithRule(DateGenerationRule rule)
        {
            _rule = rule;
            return this;
        }

        /// <summary>Sets whether a leftover period is a short or a long stub.</summary>
        public ScheduleBuilder WithStub(StubPolicy stub)
        {
            _stub = stub;
            return this;
        }

        /// <summary>Sets the calendar used to adjust the dates.</summary>
        public ScheduleBuilder WithCalendar(IHolidayCalendar calendar)
        {
            ArgumentNullException.ThrowIfNull(calendar);
            _calendar = calendar;
            return this;
        }

        /// <summary>Sets the business-day convention of every date (and of the termination date unless overridden).</summary>
        public ScheduleBuilder WithConvention(BusinessDayConvention convention)
        {
            _convention = convention;
            return this;
        }

        /// <summary>Sets a separate business-day convention for the termination date (e.g. Unadjusted).</summary>
        public ScheduleBuilder WithTerminationConvention(BusinessDayConvention convention)
        {
            _terminationConvention = convention;
            return this;
        }

        /// <summary>
        /// Sets the end-of-month rule: when the date the grid is anchored on is a month end, every generated date is
        /// the month end of its month. Applies to month- and year-based tenors only.
        /// </summary>
        public ScheduleBuilder WithEndOfMonth(bool endOfMonth = true)
        {
            _endOfMonth = endOfMonth;
            return this;
        }

        /// <summary>
        /// Fixes the end of the first period (the first date after the effective date), making the first period an explicit stub;
        /// regular dates are then generated from it. Must lie strictly between effective and termination dates.
        /// </summary>
        public ScheduleBuilder WithFirstDate(DateOnly? firstDate)
        {
            _firstDate = firstDate;
            return this;
        }

        /// <summary>
        /// Fixes the start of the last period (the last date before the termination date), making the last period an explicit stub;
        /// regular dates are then generated back from it. Must lie strictly between effective and termination dates.
        /// </summary>
        public ScheduleBuilder WithNextToLastDate(DateOnly? nextToLastDate)
        {
            _nextToLastDate = nextToLastDate;
            return this;
        }

        /// <summary>Generates the schedule.</summary>
        /// <exception cref="InvalidOperationException">The settings are inconsistent (see the message).</exception>
        public Schedule Build()
        {
            Validate();
            var unadjusted = GenerateUnadjusted();
            var terminationConvention = _terminationConvention ?? _convention;
            var adjusted = new DateOnly[unadjusted.Length];
            for (var i = 0; i < adjusted.Length; i++)
                adjusted[i] = _calendar.Adjust(unadjusted[i], i == adjusted.Length - 1 ? terminationConvention : _convention);

            var regular = new bool[unadjusted.Length - 1];
            for (var i = 0; i < regular.Length; i++)
                regular[i] = _tenor is not { } tenor || _rule == DateGenerationRule.Zero
                    || (i > 0 && i < regular.Length - 1)
                    || Shift(tenor, _rule, _endOfMonth, unadjusted[i], +1) == unadjusted[i + 1]
                    || Shift(tenor, _rule, _endOfMonth, unadjusted[i + 1], -1) == unadjusted[i];

            var effectiveTenor = _rule == DateGenerationRule.Zero ? null : _tenor;
            return new Schedule(unadjusted, adjusted, regular, effectiveTenor, _rule, _endOfMonth, _calendar, _convention, terminationConvention);
        }

        // The date k steps of the tenor from the anchor. Computed from the anchor rather than by iterating, so that month-end
        // clamping (30 Jun + 1M = 30 Jul, not the 31 Jul a clamped 30 Apr + 3M would give) cannot accumulate.
        internal static DateOnly Shift(Period tenor, DateGenerationRule rule, bool endOfMonth, DateOnly anchor, int steps)
        {
            var shifted = tenor.Multiply(steps).AddTo(anchor, endOfMonth);
            return rule == DateGenerationRule.ThirdWednesday
                ? DateRules.NthWeekday(shifted.Year, shifted.Month, DayOfWeek.Wednesday, 3)
                : shifted;
        }

        private void Validate()
        {
            if (_termination <= _effective)
                throw new InvalidOperationException($"The termination date {_termination} must be after the effective date {_effective}.");
            if (_firstDate is { } first && (first <= _effective || first >= _termination))
                throw new InvalidOperationException($"The first date {first} must be strictly between the effective and termination dates.");
            if (_nextToLastDate is { } last && (last <= _effective || last >= _termination))
                throw new InvalidOperationException($"The next-to-last date {last} must be strictly between the effective and termination dates.");
            if (_firstDate is { } f && _nextToLastDate is { } l && f >= l)
                throw new InvalidOperationException($"The first date {f} must be before the next-to-last date {l}.");
            if (_rule == DateGenerationRule.Zero || _tenor is null)
            {
                if (_firstDate is not null || _nextToLastDate is not null)
                    throw new InvalidOperationException("First and next-to-last dates need a periodic schedule.");
                return;
            }
            if (_tenor.Value.Length <= 0)
                throw new InvalidOperationException($"The tenor {_tenor.Value} must be positive.");
            if (_rule == DateGenerationRule.ThirdWednesday)
            {
                if (_tenor.Value.TotalMonths is null)
                    throw new InvalidOperationException("The ThirdWednesday rule needs a month- or year-based tenor.");
                if (_endOfMonth)
                    throw new InvalidOperationException("The end-of-month rule cannot be combined with the ThirdWednesday rule.");
            }
        }

        private DateOnly[] GenerateUnadjusted()
        {
            var dates = new List<DateOnly> { _effective };
            if (_rule != DateGenerationRule.Zero && _tenor is { } tenor)
            {
                if (_firstDate is { } first) dates.Add(first);
                var interior = _rule == DateGenerationRule.Backward ? Backward(tenor) : Forward(tenor);
                dates.AddRange(interior);
                if (_nextToLastDate is { } last) dates.Add(last);
            }
            dates.Add(_termination);
            return dates.ToArray();
        }

        // Regular dates strictly between the lower bound (first date or effective date) and the seed (next-to-last or
        // termination date), generated backward from the seed. Returned in increasing order.
        private List<DateOnly> Backward(Period tenor)
        {
            var seed = _nextToLastDate ?? _termination;
            var lower = _firstDate ?? _effective;
            var found = new List<DateOnly>();
            DateOnly candidate;
            for (var k = 1; (candidate = Shift(tenor, _rule, _endOfMonth, seed, -k)) > lower; k++)
                found.Add(candidate);
            // candidate <= lower is the first grid date outside; strictly before the effective date means a front stub.
            var hasStub = _firstDate is null && candidate < _effective;
            if (hasStub && _stub == StubPolicy.Long && found.Count > 0)
                found.RemoveAt(found.Count - 1);
            found.Reverse();
            return found;
        }

        // Regular dates strictly between the seed (first date or effective date) and the upper bound (next-to-last or
        // termination date), generated forward from the seed.
        private List<DateOnly> Forward(Period tenor)
        {
            var seed = _firstDate ?? _effective;
            var upper = _nextToLastDate ?? _termination;
            var found = new List<DateOnly>();
            DateOnly candidate;
            for (var k = 1; (candidate = Shift(tenor, _rule, _endOfMonth, seed, k)) < upper; k++)
                found.Add(candidate);
            var hasStub = _nextToLastDate is null && candidate > _termination;
            if (hasStub && _stub == StubPolicy.Long && found.Count > 0)
                found.RemoveAt(found.Count - 1);
            return found;
        }
    }
}
