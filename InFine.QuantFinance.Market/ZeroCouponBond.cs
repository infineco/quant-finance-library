using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A zero-coupon bond paying <see cref="Redemption"/> percent of the notional at the adjusted maturity. It accrues no
    /// interest, so its clean and dirty prices coincide. Prices are per 100 of notional.
    /// A yield is measured over <c>t = dayCounter(settlement, payment date)</c> years:
    /// <c>P = R (1 + y/f)^(-f t)</c> (periodic at frequency <c>f</c>), <c>R (1 + y)^(-t)</c> or <c>R exp(-y t)</c>.
    /// </summary>
    public sealed class ZeroCouponBond
    {
        /// <summary>A zero-coupon bond.</summary>
        /// <param name="maturityDate">Unadjusted maturity.</param>
        /// <param name="calendar">Payment calendar; weekends only when <c>null</c>.</param>
        /// <param name="paymentConvention">How the maturity rolls onto a business day.</param>
        /// <param name="settlementDays">Business days from valuation date to settlement.</param>
        /// <param name="redemption">Payment as a percentage of the notional.</param>
        /// <param name="notional">Face amount.</param>
        /// <param name="dayCounter">Convention measuring the time to payment in a yield; Actual/Actual ISDA when <c>null</c>.</param>
        public ZeroCouponBond(DateOnly maturityDate, IHolidayCalendar? calendar = null,
            BusinessDayConvention paymentConvention = BusinessDayConvention.Following, int settlementDays = 0,
            double redemption = 100.0, double notional = 100.0, IDayCounter? dayCounter = null)
        {
            if (settlementDays < 0)
                throw new ArgumentOutOfRangeException(nameof(settlementDays), settlementDays, "Must not be negative.");
            if (!(notional > 0.0) || !double.IsFinite(notional) || !(redemption > 0.0) || !double.IsFinite(redemption))
                throw new ArgumentOutOfRangeException(nameof(notional), "Notional and redemption must be finite and positive.");
            Calendar = calendar ?? WeekendsOnlyCalendar.Instance;
            MaturityDate = maturityDate;
            PaymentDate = Calendar.Adjust(maturityDate, paymentConvention);
            SettlementDays = settlementDays;
            Redemption = redemption;
            Notional = notional;
            DayCounter = dayCounter ?? Market.DayCounter.ActualActualIsda;
        }

        /// <summary>The payment calendar.</summary>
        public IHolidayCalendar Calendar { get; }

        /// <summary>The unadjusted maturity.</summary>
        public DateOnly MaturityDate { get; }

        /// <summary>The adjusted payment date.</summary>
        public DateOnly PaymentDate { get; }

        /// <summary>Business days from valuation date to settlement.</summary>
        public int SettlementDays { get; }

        /// <summary>The payment in percent of the notional.</summary>
        public double Redemption { get; }

        /// <summary>Face amount.</summary>
        public double Notional { get; }

        /// <summary>The convention measuring time in a yield.</summary>
        public IDayCounter DayCounter { get; }

        /// <summary>The settlement date of a trade done on <paramref name="valuationDate"/>.</summary>
        public DateOnly SettlementDate(DateOnly valuationDate) => Calendar.Advance(valuationDate, SettlementDays);

        /// <summary>The price per 100 of notional at the settlement date of the curve's reference date: <c>R P(T) / P(settlement)</c>.</summary>
        public double Price(IYieldCurve curve)
        {
            ArgumentNullException.ThrowIfNull(curve);
            var settlement = SettlementDate(curve.ReferenceDate);
            CheckOutstanding(settlement);
            return Redemption * curve.DiscountFactor(PaymentDate) / curve.DiscountFactor(settlement);
        }

        /// <summary>The price per 100 of notional for a yield.</summary>
        /// <param name="yield">The yield.</param>
        /// <param name="settlement">The settlement date.</param>
        /// <param name="compounding">The compounding of the yield.</param>
        /// <param name="frequency">Compounding frequency of <see cref="YieldCompounding.Periodic"/>; ignored otherwise.</param>
        public double PriceFromYield(double yield, DateOnly settlement, YieldCompounding compounding = YieldCompounding.Annual,
            Frequency frequency = Frequency.Annual)
        {
            CheckOutstanding(settlement);
            var t = DayCounter.YearFraction(settlement, PaymentDate);
            return Redemption * compounding switch
            {
                YieldCompounding.Periodic => Math.Pow(1.0 + yield / PeriodsOf(frequency), -PeriodsOf(frequency) * t),
                YieldCompounding.Annual => Math.Pow(1.0 + yield, -t),
                YieldCompounding.Continuous => Math.Exp(-yield * t),
                _ => throw new ArgumentOutOfRangeException(nameof(compounding), compounding, "Unknown compounding."),
            };
        }

        /// <summary>The yield for a price per 100 of notional (closed form).</summary>
        public double YieldFromPrice(double price, DateOnly settlement, YieldCompounding compounding = YieldCompounding.Annual,
            Frequency frequency = Frequency.Annual)
        {
            CheckOutstanding(settlement);
            if (!(price > 0.0) || !double.IsFinite(price))
                throw new ArgumentOutOfRangeException(nameof(price), price, "The price must be finite and positive.");
            var t = DayCounter.YearFraction(settlement, PaymentDate);
            var growth = Redemption / price;
            return compounding switch
            {
                YieldCompounding.Periodic => PeriodsOf(frequency) * (Math.Pow(growth, 1.0 / (PeriodsOf(frequency) * t)) - 1.0),
                YieldCompounding.Annual => Math.Pow(growth, 1.0 / t) - 1.0,
                YieldCompounding.Continuous => Math.Log(growth) / t,
                _ => throw new ArgumentOutOfRangeException(nameof(compounding), compounding, "Unknown compounding."),
            };
        }

        private static double PeriodsOf(Frequency frequency)
        {
            var f = frequency.PeriodsPerYear();
            if (f == 0) throw new ArgumentException("Periodic compounding needs a periodic frequency, not Once.", nameof(frequency));
            return f;
        }

        private void CheckOutstanding(DateOnly settlement)
        {
            if (settlement >= PaymentDate)
                throw new ArgumentException($"The bond has matured: settlement {settlement} is not before the payment date {PaymentDate}.", nameof(settlement));
        }
    }
}
