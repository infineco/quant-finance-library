using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// Base class of the day-count conventions, and home of the shared instances of the ones that need no
    /// configuration (<see cref="Business252"/> needs a calendar and is built explicitly).
    /// The definitions follow the ISDA 2006 Definitions section 4.16 and the ICMA rule book (rule 251).
    /// </summary>
    public abstract class DayCounter : IDayCounter
    {
        /// <summary>Actual/360 (money market, ISDA 4.16(e)): actual days over 360.</summary>
        public static readonly IDayCounter Actual360 = new Actual360Counter();

        /// <summary>Actual/365 Fixed (ISDA 4.16(d)): actual days over 365, whatever the year.</summary>
        public static readonly IDayCounter Actual365Fixed = new Actual365FixedCounter();

        /// <summary>Actual/365.25: actual days over 365.25.</summary>
        public static readonly IDayCounter Actual36525 = new Actual36525Counter();

        /// <summary>
        /// 30/360 Bond Basis (ISDA 30/360, ISDA 4.16(f)): D1 = min(D1, 30); D2 = min(D2, 30) only if D1 is 30 or 31.
        /// Month-end February dates get no special treatment.
        /// </summary>
        public static readonly IDayCounter Thirty360BondBasis = new Thirty360BondBasisCounter();

        /// <summary>30E/360 (Eurobond basis, ICMA rule 251.1(ii), ISDA 4.16(g) without the February rule): D1 and D2 are capped at 30.</summary>
        public static readonly IDayCounter Thirty360E = new Thirty360ECounter();

        /// <summary>
        /// 30E/360 ISDA (ISDA 4.16(h)): like 30E/360 but a last day of February counts as 30 as well, except
        /// for the end date when it is the termination date (<see cref="DayCountContext.TerminationDate"/>).
        /// </summary>
        public static readonly IDayCounter Thirty360EIsda = new Thirty360EIsdaCounter();

        /// <summary>Actual/Actual ISDA (ISDA 4.16(b)): days in leap years over 366 plus days in other years over 365.</summary>
        public static readonly IDayCounter ActualActualIsda = new ActualActualIsdaCounter();

        /// <summary>
        /// Actual/Actual ICMA (ISMA-99, ICMA rule 251.1(i)): actual days over (frequency x days in the reference period).
        /// Needs a <see cref="DayCountContext"/> with the reference period and frequency; accruals longer than the reference
        /// period or starting before it (long stubs) are split over consecutive notional periods of the same length in months.
        /// </summary>
        public static readonly IDayCounter ActualActualIcma = new ActualActualIcmaCounter();

        /// <summary>
        /// Actual/Actual AFB (Euro, FBF): the whole years counted back from the end date, plus the remaining days over
        /// 366 if 29 February lies in the remaining period [start, end) and over 365 otherwise.
        /// </summary>
        public static readonly IDayCounter ActualActualAfb = new ActualActualAfbCounter();

        /// <inheritdoc />
        public abstract string Name { get; }

        /// <inheritdoc />
        public int DayCount(DateOnly start, DateOnly end) =>
            end < start ? -DayCountCore(end, start, default) : DayCountCore(start, end, default);

        /// <inheritdoc />
        public double YearFraction(DateOnly start, DateOnly end) => YearFraction(start, end, default);

        /// <inheritdoc />
        public double YearFraction(DateOnly start, DateOnly end, DayCountContext context) =>
            end < start ? -YearFractionCore(end, start, context) : YearFractionCore(start, end, context);

        /// <inheritdoc />
        public override string ToString() => Name;

        /// <summary>The day count for <c>start &lt;= end</c>.</summary>
        protected abstract int DayCountCore(DateOnly start, DateOnly end, in DayCountContext context);

        /// <summary>The year fraction for <c>start &lt;= end</c>.</summary>
        protected abstract double YearFractionCore(DateOnly start, DateOnly end, in DayCountContext context);

        /// <summary>Actual number of days, end exclusive.</summary>
        protected static int ActualDays(DateOnly start, DateOnly end) => end.DayNumber - start.DayNumber;

        private static int Days360(DateOnly start, int d1, DateOnly end, int d2) =>
            360 * (end.Year - start.Year) + 30 * (end.Month - start.Month) + (d2 - d1);

        private static bool IsLastDayOfFebruary(DateOnly date) => date.Month == 2 && date.IsEndOfMonth();

        private sealed class Actual360Counter : DayCounter
        {
            public override string Name => "Actual/360";
            protected override int DayCountCore(DateOnly start, DateOnly end, in DayCountContext context) => ActualDays(start, end);
            protected override double YearFractionCore(DateOnly start, DateOnly end, in DayCountContext context) => ActualDays(start, end) / 360.0;
        }

        private sealed class Actual365FixedCounter : DayCounter
        {
            public override string Name => "Actual/365 (Fixed)";
            protected override int DayCountCore(DateOnly start, DateOnly end, in DayCountContext context) => ActualDays(start, end);
            protected override double YearFractionCore(DateOnly start, DateOnly end, in DayCountContext context) => ActualDays(start, end) / 365.0;
        }

        private sealed class Actual36525Counter : DayCounter
        {
            public override string Name => "Actual/365.25";
            protected override int DayCountCore(DateOnly start, DateOnly end, in DayCountContext context) => ActualDays(start, end);
            protected override double YearFractionCore(DateOnly start, DateOnly end, in DayCountContext context) => ActualDays(start, end) / 365.25;
        }

        private abstract class Thirty360Base : DayCounter
        {
            protected override double YearFractionCore(DateOnly start, DateOnly end, in DayCountContext context) =>
                DayCountCore(start, end, context) / 360.0;
        }

        private sealed class Thirty360BondBasisCounter : Thirty360Base
        {
            public override string Name => "30/360 Bond Basis";

            protected override int DayCountCore(DateOnly start, DateOnly end, in DayCountContext context)
            {
                var d1 = Math.Min(start.Day, 30);
                var d2 = d1 == 30 ? Math.Min(end.Day, 30) : end.Day;
                return Days360(start, d1, end, d2);
            }
        }

        private sealed class Thirty360ECounter : Thirty360Base
        {
            public override string Name => "30E/360";

            protected override int DayCountCore(DateOnly start, DateOnly end, in DayCountContext context) =>
                Days360(start, Math.Min(start.Day, 30), end, Math.Min(end.Day, 30));
        }

        private sealed class Thirty360EIsdaCounter : Thirty360Base
        {
            public override string Name => "30E/360 ISDA";

            protected override int DayCountCore(DateOnly start, DateOnly end, in DayCountContext context)
            {
                var d1 = start.Day == 31 || IsLastDayOfFebruary(start) ? 30 : start.Day;
                var isTermination = context.TerminationDate is { } termination && end == termination;
                var d2 = end.Day == 31 || (IsLastDayOfFebruary(end) && !isTermination) ? 30 : end.Day;
                return Days360(start, d1, end, d2);
            }
        }

        private sealed class ActualActualIsdaCounter : DayCounter
        {
            public override string Name => "Actual/Actual (ISDA)";

            protected override int DayCountCore(DateOnly start, DateOnly end, in DayCountContext context) => ActualDays(start, end);

            protected override double YearFractionCore(DateOnly start, DateOnly end, in DayCountContext context)
            {
                if (start.Year == end.Year)
                    return ActualDays(start, end) / (double)DaysInYear(start.Year);
                var firstYearDays = ActualDays(start, new DateOnly(start.Year + 1, 1, 1));
                var lastYearDays = ActualDays(new DateOnly(end.Year, 1, 1), end);
                return firstYearDays / (double)DaysInYear(start.Year)
                    + (end.Year - start.Year - 1)
                    + lastYearDays / (double)DaysInYear(end.Year);
            }

            private static int DaysInYear(int year) => DateTime.IsLeapYear(year) ? 366 : 365;
        }

        private sealed class ActualActualAfbCounter : DayCounter
        {
            public override string Name => "Actual/Actual (AFB)";

            protected override int DayCountCore(DateOnly start, DateOnly end, in DayCountContext context) => ActualDays(start, end);

            protected override double YearFractionCore(DateOnly start, DateOnly end, in DayCountContext context)
            {
                var wholeYears = 0;
                while (end.AddYears(-(wholeYears + 1)) >= start) wholeYears++;
                var remainderEnd = end.AddYears(-wholeYears);
                var containsLeapDay = false;
                for (var year = start.Year; year <= remainderEnd.Year; year++)
                {
                    if (!DateTime.IsLeapYear(year)) continue;
                    var leapDay = new DateOnly(year, 2, 29);
                    if (start <= leapDay && leapDay < remainderEnd) containsLeapDay = true;
                }
                return wholeYears + ActualDays(start, remainderEnd) / (containsLeapDay ? 366.0 : 365.0);
            }
        }

        private sealed class ActualActualIcmaCounter : DayCounter
        {
            public override string Name => "Actual/Actual (ICMA)";

            protected override int DayCountCore(DateOnly start, DateOnly end, in DayCountContext context) => ActualDays(start, end);

            protected override double YearFractionCore(DateOnly start, DateOnly end, in DayCountContext context)
            {
                if (context.RefPeriodStart is not { } refStart || context.RefPeriodEnd is not { } refEnd || context.Frequency is not { } frequency)
                    throw new ArgumentException("Actual/Actual ICMA needs a reference period and a frequency.", nameof(context));
                if (refEnd <= refStart)
                    throw new ArgumentException("The reference period must have a positive length.", nameof(context));
                if (frequency == Frequency.Once)
                    throw new ArgumentException("Actual/Actual ICMA needs a periodic frequency, not Once.", nameof(context));
                if (start == end) return 0.0;

                var months = frequency.Months();
                var perYear = (double)frequency.PeriodsPerYear();

                // Notional periods j = ..., -1, 0, 1, ...: period 0 is the reference period; those before/after it
                // are the same number of months long, anchored on its ends so month-end clamping does not drift.
                DateOnly Boundary(int j) =>
                    j == 0 ? refStart :
                    j == 1 ? refEnd :
                    j > 1 ? refEnd.AddMonths((j - 1) * months) :
                    refStart.AddMonths(j * months);

                var period = 0;
                while (start < Boundary(period)) period--;
                while (start >= Boundary(period + 1)) period++;

                var total = 0.0;
                var cursor = start;
                while (cursor < end)
                {
                    var periodStart = Boundary(period);
                    var periodEnd = Boundary(period + 1);
                    var segmentEnd = end < periodEnd ? end : periodEnd;
                    total += ActualDays(cursor, segmentEnd) / (perYear * ActualDays(periodStart, periodEnd));
                    cursor = segmentEnd;
                    period++;
                }
                return total;
            }
        }
    }
}
