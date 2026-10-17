using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A money-market deposit: one unit lent from <see cref="StartDate"/> to <see cref="EndDate"/> at a simple rate
    /// accrued with <see cref="DayCounter"/> (Actual/360 for EUR). Its curve-implied rate is the simple forward rate
    /// <c>(P(0, start) / P(0, end) - 1) / tau</c>.
    /// </summary>
    public sealed class Deposit : ICurveInstrument
    {
        /// <summary>A deposit between explicit (already adjusted) dates.</summary>
        /// <exception cref="ArgumentException"><paramref name="endDate"/> is not after <paramref name="startDate"/>.</exception>
        public Deposit(DateOnly startDate, DateOnly endDate, IDayCounter dayCounter)
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
        /// A spot-starting deposit with market conventions: start = <paramref name="spotLag"/> business days after
        /// <paramref name="tradeDate"/> on <paramref name="calendar"/>; end = start + <paramref name="tenor"/>, rolled with
        /// <paramref name="convention"/>. The day counter defaults to Actual/360.
        /// </summary>
        public static Deposit FromTenor(DateOnly tradeDate, Period tenor, IHolidayCalendar calendar, int spotLag = 2,
            BusinessDayConvention convention = BusinessDayConvention.ModifiedFollowing, IDayCounter? dayCounter = null, bool endOfMonth = false)
        {
            var start = SpotDate(tradeDate, calendar, spotLag);
            return new Deposit(start, tenor.AddTo(start, calendar, convention, endOfMonth), dayCounter ?? Market.DayCounter.Actual360);
        }

        /// <summary>The value date.</summary>
        public DateOnly StartDate { get; }

        /// <summary>The repayment date.</summary>
        public DateOnly EndDate { get; }

        /// <summary>The accrual convention.</summary>
        public IDayCounter DayCounter { get; }

        /// <summary>The accrual period <c>tau</c>.</summary>
        public double YearFraction { get; }

        /// <inheritdoc />
        public DateOnly MaturityDate => EndDate;

        /// <inheritdoc />
        public double ImpliedRate(IYieldCurve curve) => SimpleForward(curve, StartDate, EndDate, YearFraction);

        internal static DateOnly SpotDate(DateOnly tradeDate, IHolidayCalendar calendar, int spotLag)
        {
            ArgumentNullException.ThrowIfNull(calendar);
            if (spotLag < 0)
                throw new ArgumentOutOfRangeException(nameof(spotLag), spotLag, "The spot lag must not be negative.");
            return calendar.Advance(tradeDate, spotLag);
        }

        internal static double SimpleForward(IYieldCurve curve, DateOnly start, DateOnly end, double tau)
        {
            ArgumentNullException.ThrowIfNull(curve);
            return (curve.DiscountFactor(start) / curve.DiscountFactor(end) - 1.0) / tau;
        }
    }
}
