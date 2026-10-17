using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A floating-rate note, single-curve: each coupon pays the simple forward rate of its accrual period on the pricing curve
    /// plus <see cref="Spread"/>, accrued with <see cref="DayCounter"/> on the adjusted schedule dates and paid at the adjusted
    /// end of the period; the principal is repaid with the last coupon. The coupon whose accrual period has already started at
    /// the curve's reference date is fixed: it pays the known <see cref="CurrentFixing"/> plus the spread.
    /// Prices are per 100 of notional; amounts are in currency units for <see cref="Notional"/>.
    /// With a zero spread and a fixing equal to the curve's forward, the note trades at par on a coupon date:
    /// the floating coupons telescope to <c>P(start) - P(end)</c> and add up with the principal to the notional.
    /// </summary>
    public sealed class FloatingRateNote
    {
        private readonly AccrualPeriod[] _periods;

        /// <summary>A floating-rate note with coupon dates generated backward from maturity.</summary>
        /// <param name="issueDate">Start of the first accrual period.</param>
        /// <param name="maturityDate">Unadjusted maturity.</param>
        /// <param name="frequency">Coupon frequency (the tenor of the index); periodic.</param>
        /// <param name="spread">Spread over the index rate.</param>
        /// <param name="currentFixing">The index rate already fixed for the period in progress at the pricing date.</param>
        /// <param name="dayCounter">Accrual convention; Actual/360 when <c>null</c>.</param>
        /// <param name="calendar">Calendar for the schedule and payments; weekends only when <c>null</c>.</param>
        /// <param name="convention">Business-day convention of the schedule dates (ModifiedFollowing by default).</param>
        /// <param name="settlementDays">Business days from valuation date to settlement.</param>
        /// <param name="redemption">Principal in percent of the notional.</param>
        /// <param name="notional">Face amount.</param>
        public FloatingRateNote(DateOnly issueDate, DateOnly maturityDate, Frequency frequency, double spread, double currentFixing,
            IDayCounter? dayCounter = null, IHolidayCalendar? calendar = null,
            BusinessDayConvention convention = BusinessDayConvention.ModifiedFollowing, int settlementDays = 0,
            double redemption = 100.0, double notional = 100.0)
        {
            if (frequency == Frequency.Once)
                throw new ArgumentException("A floating-rate note needs a periodic frequency.", nameof(frequency));
            if (settlementDays < 0)
                throw new ArgumentOutOfRangeException(nameof(settlementDays), settlementDays, "Must not be negative.");
            if (!(notional > 0.0) || !double.IsFinite(notional) || !double.IsFinite(redemption))
                throw new ArgumentOutOfRangeException(nameof(notional), "The notional must be finite and positive, the redemption finite.");
            Schedule = new ScheduleBuilder(issueDate, maturityDate)
                .WithFrequency(frequency)
                .WithCalendar(calendar ?? WeekendsOnlyCalendar.Instance)
                .WithConvention(convention)
                .Build();
            Spread = spread;
            CurrentFixing = currentFixing;
            DayCounter = dayCounter ?? Market.DayCounter.Actual360;
            SettlementDays = settlementDays;
            Redemption = redemption;
            Notional = notional;
            _periods = new AccrualPeriod[Schedule.PeriodCount];
            var accruals = Schedule.GetAccrualPeriods(DayCounter);
            for (var i = 0; i < _periods.Length; i++) _periods[i] = accruals[i];
        }

        /// <summary>The accrual schedule.</summary>
        public Schedule Schedule { get; }

        /// <summary>Spread over the index rate.</summary>
        public double Spread { get; }

        /// <summary>The known index fixing of the period in progress.</summary>
        public double CurrentFixing { get; }

        /// <summary>Accrual convention.</summary>
        public IDayCounter DayCounter { get; }

        /// <summary>Business days from valuation date to settlement.</summary>
        public int SettlementDays { get; }

        /// <summary>Principal in percent of the notional.</summary>
        public double Redemption { get; }

        /// <summary>Face amount.</summary>
        public double Notional { get; }

        /// <summary>The adjusted maturity: the date the principal is paid.</summary>
        public DateOnly FinalPaymentDate => _periods[^1].End;

        /// <summary>The settlement date of a trade done on <paramref name="valuationDate"/>.</summary>
        public DateOnly SettlementDate(DateOnly valuationDate) => Schedule.Calendar.Advance(valuationDate, SettlementDays);

        /// <summary>
        /// The coupons as seen from <paramref name="curve"/>: the rate of each is the known fixing for a period started on or
        /// before the reference date, the curve's simple forward otherwise, plus the spread.
        /// </summary>
        public IReadOnlyList<LegCashFlow> Coupons(IYieldCurve curve)
        {
            ArgumentNullException.ThrowIfNull(curve);
            var coupons = new LegCashFlow[_periods.Length];
            for (var i = 0; i < coupons.Length; i++)
            {
                var p = _periods[i];
                var rate = (p.Start <= curve.ReferenceDate ? CurrentFixing : Deposit.SimpleForward(curve, p.Start, p.End, p.YearFraction)) + Spread;
                coupons[i] = new LegCashFlow(p.Start, p.End, p.End, p.YearFraction, rate, Notional * rate * p.YearFraction);
            }
            return coupons;
        }

        /// <summary>The accrued interest per 100 of notional at <paramref name="settlement"/>, with the coupon rate of the period containing it.</summary>
        public double AccruedInterest(IYieldCurve curve, DateOnly settlement)
        {
            var coupons = Coupons(curve);
            for (var i = 0; i < _periods.Length; i++)
            {
                if (!(_periods[i].Start <= settlement && settlement < _periods[i].End)) continue;
                var context = Schedule.DayCountContextOf(i, useAdjustedDates: true);
                return 100.0 * coupons[i].Rate * DayCounter.YearFraction(_periods[i].Start, settlement, context);
            }
            return 0.0;
        }

        /// <summary>The dirty price per 100 of notional at the settlement date of the curve's reference date.</summary>
        public double DirtyPrice(IYieldCurve curve)
        {
            ArgumentNullException.ThrowIfNull(curve);
            return DirtyPrice(curve, SettlementDate(curve.ReferenceDate));
        }

        /// <summary>The dirty price at <paramref name="settlement"/>: the coupons paid after it and the principal, discounted to settlement.</summary>
        /// <exception cref="ArgumentException">The note has matured.</exception>
        public double DirtyPrice(IYieldCurve curve, DateOnly settlement)
        {
            if (settlement >= FinalPaymentDate)
                throw new ArgumentException($"The note has matured: settlement {settlement} is not before {FinalPaymentDate}.", nameof(settlement));
            var coupons = Coupons(curve);
            var sum = 0.0;
            foreach (var coupon in coupons)
                if (coupon.PaymentDate > settlement)
                    sum += 100.0 * coupon.Rate * coupon.YearFraction * curve.DiscountFactor(coupon.PaymentDate);
            sum += Redemption * curve.DiscountFactor(FinalPaymentDate);
            return sum / curve.DiscountFactor(settlement);
        }

        /// <summary>The clean price at the settlement date of the curve's reference date.</summary>
        public double CleanPrice(IYieldCurve curve)
        {
            ArgumentNullException.ThrowIfNull(curve);
            var settlement = SettlementDate(curve.ReferenceDate);
            return DirtyPrice(curve, settlement) - AccruedInterest(curve, settlement);
        }
    }
}
