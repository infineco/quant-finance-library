using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A forward rate agreement on the simple rate from <see cref="StartDate"/> to <see cref="EndDate"/>, accrued with
    /// <see cref="DayCounter"/>. In the single-curve framework its fair rate is the simple forward
    /// <c>(P(0, start) / P(0, end) - 1) / tau</c> (no convexity, FRA discounting at the fixing rate is immaterial for the fair rate).
    /// </summary>
    public sealed class ForwardRateAgreement : ICurveInstrument
    {
        /// <summary>An FRA between explicit (already adjusted) dates.</summary>
        /// <exception cref="ArgumentException"><paramref name="endDate"/> is not after <paramref name="startDate"/>.</exception>
        public ForwardRateAgreement(DateOnly startDate, DateOnly endDate, IDayCounter dayCounter)
        {
            ArgumentNullException.ThrowIfNull(dayCounter);
            if (endDate <= startDate)
                throw new ArgumentException($"The end date {endDate} must be after the start date {startDate}.", nameof(endDate));
            StartDate = startDate;
            EndDate = endDate;
            DayCounter = dayCounter;
            YearFraction = dayCounter.YearFraction(startDate, endDate);
        }

        /// <summary>
        /// A "start x (start + index)" FRA with market conventions, e.g. 3x9 is <paramref name="startTenor"/> 3M and
        /// <paramref name="indexTenor"/> 6M: start = spot + start tenor and end = start + index tenor, each rolled with
        /// <paramref name="convention"/>. The day counter defaults to Actual/360.
        /// </summary>
        public static ForwardRateAgreement FromTenors(DateOnly tradeDate, Period startTenor, Period indexTenor, IHolidayCalendar calendar,
            int spotLag = 2, BusinessDayConvention convention = BusinessDayConvention.ModifiedFollowing, IDayCounter? dayCounter = null,
            bool endOfMonth = false)
        {
            var spot = Deposit.SpotDate(tradeDate, calendar, spotLag);
            var start = startTenor.AddTo(spot, calendar, convention, endOfMonth);
            var end = indexTenor.AddTo(start, calendar, convention, endOfMonth);
            return new ForwardRateAgreement(start, end, dayCounter ?? Market.DayCounter.Actual360);
        }

        /// <summary>Start of the forward period.</summary>
        public DateOnly StartDate { get; }

        /// <summary>End of the forward period.</summary>
        public DateOnly EndDate { get; }

        /// <summary>The accrual convention of the rate.</summary>
        public IDayCounter DayCounter { get; }

        /// <summary>The accrual period <c>tau</c>.</summary>
        public double YearFraction { get; }

        /// <inheritdoc />
        public DateOnly MaturityDate => EndDate;

        /// <inheritdoc />
        public double ImpliedRate(IYieldCurve curve) => Deposit.SimpleForward(curve, StartDate, EndDate, YearFraction);
    }
}
