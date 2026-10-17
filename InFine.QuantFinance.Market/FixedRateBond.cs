using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A fixed-rate bullet bond. Coupons accrue on the <em>unadjusted</em> schedule dates (the bond market convention: the
    /// amount of a coupon does not depend on weekends or holidays) and are paid on the adjusted end of each period; the
    /// principal is repaid with the last coupon. Prices are quoted <em>per 100 of notional</em>; amounts (coupons, cash flows,
    /// <see cref="Npv"/>) are in currency units for <see cref="Notional"/>.
    /// <para>
    /// <b>Accrued interest</b> at a settlement date inside an accrual period is <c>100 c tau(start, settlement)</c>, with <c>tau</c>
    /// the day counter evaluated on the period's reference period (Actual/Actual ICMA: <c>days / (f x days in the period)</c>).
    /// With <see cref="ExCouponDays"/> &gt; 0, a settlement on or after the ex-coupon date (that many business days before the
    /// payment date) does not carry the coming coupon, and the accrued interest is negative: <c>-100 c tau(settlement, end)</c>.
    /// A coupon paid on the settlement date itself belongs to the seller.
    /// </para>
    /// <para>
    /// <b>Yield</b> follows the ICMA / street convention: a cash flow <c>e</c> coupon periods after settlement (the first one
    /// <c>f tau(settlement, next coupon)</c>, a fraction of a period, each following regular period adding one) is discounted by
    /// <c>(1 + y / f)^(-e)</c> (<see cref="YieldCompounding.Periodic"/>); the annual and continuous alternatives use
    /// <c>t = e / f</c> years. The yield is that of the <em>dirty</em> price: accrued interest is paid on top of the quoted clean
    /// price.
    /// </para>
    /// </summary>
    public sealed class FixedRateBond
    {
        private const double BasisPoint = 1e-4;

        private readonly AccrualPeriod[] _periods;
        private readonly DayCountContext[] _contexts;
        private readonly DateOnly[] _paymentDates;
        private readonly bool[] _regular;

        /// <summary>
        /// A bond from issue date to maturity with coupon dates generated backward from maturity (a short first coupon when
        /// the issue date is off the grid).
        /// </summary>
        /// <param name="issueDate">Start of the first accrual period.</param>
        /// <param name="maturityDate">Unadjusted maturity; the principal is paid on its adjustment.</param>
        /// <param name="couponRate">Annual coupon rate, e.g. 0.05.</param>
        /// <param name="frequency">Coupon frequency; must be a periodic one.</param>
        /// <param name="dayCounter">Accrual convention; Actual/Actual ICMA (government bonds) when <c>null</c>. 30/360 is the usual alternative.</param>
        /// <param name="calendar">Payment calendar; weekends only when <c>null</c>.</param>
        /// <param name="paymentConvention">How payment dates roll onto business days (Following by default).</param>
        /// <param name="settlementDays">Business days from trade (valuation) date to settlement.</param>
        /// <param name="redemption">Principal repaid, as a percentage of the notional.</param>
        /// <param name="notional">Face amount.</param>
        /// <param name="exCouponDays">Business days before a payment date at which the bond trades ex-coupon; 0 for none.</param>
        public FixedRateBond(DateOnly issueDate, DateOnly maturityDate, double couponRate, Frequency frequency,
            IDayCounter? dayCounter = null, IHolidayCalendar? calendar = null,
            BusinessDayConvention paymentConvention = BusinessDayConvention.Following, int settlementDays = 0,
            double redemption = 100.0, double notional = 100.0, int exCouponDays = 0)
            : this(BuildSchedule(issueDate, maturityDate, frequency, calendar, paymentConvention), couponRate, dayCounter,
                settlementDays, redemption, notional, exCouponDays)
        {
        }

        /// <summary>A bond on an explicit schedule (custom stubs, forward generation...); the schedule carries the payment calendar and convention.</summary>
        /// <exception cref="ArgumentException">The schedule has no standard coupon frequency.</exception>
        public FixedRateBond(Schedule schedule, double couponRate, IDayCounter? dayCounter = null, int settlementDays = 0,
            double redemption = 100.0, double notional = 100.0, int exCouponDays = 0)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            if (schedule.Tenor is not { } tenor || !tenor.TryGetFrequency(out var frequency))
                throw new ArgumentException("A fixed-rate bond needs a schedule with a standard coupon frequency (use ZeroCouponBond otherwise).", nameof(schedule));
            if (!double.IsFinite(couponRate))
                throw new ArgumentOutOfRangeException(nameof(couponRate), couponRate, "The coupon rate must be finite.");
            if (settlementDays < 0 || exCouponDays < 0)
                throw new ArgumentOutOfRangeException(settlementDays < 0 ? nameof(settlementDays) : nameof(exCouponDays), "Must not be negative.");
            if (!(notional > 0.0) || !double.IsFinite(redemption) || !double.IsFinite(notional))
                throw new ArgumentOutOfRangeException(nameof(notional), notional, "The notional must be finite and positive, the redemption finite.");

            Schedule = schedule;
            Frequency = frequency;
            CouponRate = couponRate;
            DayCounter = dayCounter ?? Market.DayCounter.ActualActualIcma;
            SettlementDays = settlementDays;
            Redemption = redemption;
            Notional = notional;
            ExCouponDays = exCouponDays;

            _periods = new AccrualPeriod[schedule.PeriodCount];
            _contexts = new DayCountContext[schedule.PeriodCount];
            _paymentDates = new DateOnly[schedule.PeriodCount];
            _regular = new bool[schedule.PeriodCount];
            var accruals = schedule.GetAccrualPeriods(DayCounter, useAdjustedDates: false);
            var coupons = new LegCashFlow[_periods.Length];
            var flows = new List<CashFlow>(_periods.Length + 1);
            for (var i = 0; i < _periods.Length; i++)
            {
                _periods[i] = accruals[i];
                _contexts[i] = schedule.DayCountContextOf(i, useAdjustedDates: false);
                _paymentDates[i] = accruals[i].End;
                _regular[i] = schedule.IsRegular(i);
                var amount = notional * couponRate * accruals[i].YearFraction;
                coupons[i] = new LegCashFlow(accruals[i].UnadjustedStart, accruals[i].UnadjustedEnd, accruals[i].End,
                    accruals[i].YearFraction, couponRate, amount);
                flows.Add(new CashFlow(accruals[i].End, amount));
            }
            flows.Add(new CashFlow(_paymentDates[^1], notional * redemption / 100.0));
            Coupons = coupons;
            CashFlows = flows;
        }

        private static Schedule BuildSchedule(DateOnly issueDate, DateOnly maturityDate, Frequency frequency,
            IHolidayCalendar? calendar, BusinessDayConvention convention)
        {
            if (frequency == Frequency.Once)
                throw new ArgumentException("A coupon bond needs a periodic frequency (use ZeroCouponBond for a single payment).", nameof(frequency));
            return new ScheduleBuilder(issueDate, maturityDate)
                .WithFrequency(frequency)
                .WithCalendar(calendar ?? WeekendsOnlyCalendar.Instance)
                .WithConvention(convention)
                .Build();
        }

        /// <summary>The coupon schedule (unadjusted accrual dates, adjusted payment dates).</summary>
        public Schedule Schedule { get; }

        /// <summary>Coupon frequency.</summary>
        public Frequency Frequency { get; }

        /// <summary>Annual coupon rate.</summary>
        public double CouponRate { get; }

        /// <summary>Accrual convention.</summary>
        public IDayCounter DayCounter { get; }

        /// <summary>Business days from valuation date to settlement.</summary>
        public int SettlementDays { get; }

        /// <summary>Principal repaid at maturity, in percent of the notional.</summary>
        public double Redemption { get; }

        /// <summary>Face amount.</summary>
        public double Notional { get; }

        /// <summary>Business days before a payment date at which the bond goes ex-coupon (0: never).</summary>
        public int ExCouponDays { get; }

        /// <summary>Issue date: the start of the first accrual period.</summary>
        public DateOnly IssueDate => Schedule.UnadjustedDates[0];

        /// <summary>The unadjusted maturity date.</summary>
        public DateOnly MaturityDate => Schedule.UnadjustedDates[^1];

        /// <summary>The date the principal is paid: the adjusted maturity.</summary>
        public DateOnly FinalPaymentDate => _paymentDates[^1];

        /// <summary>The coupons: accrual dates, payment date, year fraction, rate and amount.</summary>
        public IReadOnlyList<LegCashFlow> Coupons { get; }

        /// <summary>Every cash flow by payment date: the coupons, then the principal (on the same date as the last coupon).</summary>
        public IReadOnlyList<CashFlow> CashFlows { get; }

        /// <summary>The settlement date of a trade done on <paramref name="valuationDate"/>: <see cref="SettlementDays"/> business days later.</summary>
        public DateOnly SettlementDate(DateOnly valuationDate) => Schedule.Calendar.Advance(valuationDate, SettlementDays);

        // ---------------------------------------------------------------------------------------------------------------
        // Accrued interest
        // ---------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The accrued interest per 100 of notional at <paramref name="settlement"/>: zero before issue and from maturity on,
        /// negative between the ex-coupon date and the payment date of a coupon.
        /// </summary>
        public double AccruedInterest(DateOnly settlement)
        {
            var j = AccrualPeriodIndex(settlement);
            if (j < 0) return 0.0;
            var firstUnpaid = FirstUnpaidIndex(settlement);
            if (firstUnpaid == j && IsExCoupon(j, settlement))
                return -100.0 * CouponRate * DayCounter.YearFraction(settlement, _periods[j].UnadjustedEnd, _contexts[j]);
            return 100.0 * CouponRate * DayCounter.YearFraction(_periods[j].UnadjustedStart, settlement, _contexts[j]);
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Pricing from a curve
        // ---------------------------------------------------------------------------------------------------------------

        /// <summary>The dirty price per 100 of notional at the settlement date of the curve's reference date.</summary>
        public double DirtyPrice(IYieldCurve curve)
        {
            ArgumentNullException.ThrowIfNull(curve);
            return DirtyPrice(curve, SettlementDate(curve.ReferenceDate));
        }

        /// <summary>
        /// The dirty price per 100 of notional at <paramref name="settlement"/>: the cash flows after settlement discounted
        /// with <paramref name="curve"/> and expressed in value at the settlement date, <c>sum CF_k P(T_k) / P(settlement)</c>.
        /// </summary>
        public double DirtyPrice(IYieldCurve curve, DateOnly settlement)
        {
            ArgumentNullException.ThrowIfNull(curve);
            var sum = 0.0;
            foreach (var flow in RemainingFlows(settlement))
                sum += flow.AmountPer100 * curve.DiscountFactor(flow.Date);
            return sum / curve.DiscountFactor(settlement);
        }

        /// <summary>The clean price (dirty price less accrued interest) at the settlement date of the curve's reference date.</summary>
        public double CleanPrice(IYieldCurve curve)
        {
            ArgumentNullException.ThrowIfNull(curve);
            return CleanPrice(curve, SettlementDate(curve.ReferenceDate));
        }

        /// <summary>The clean price at <paramref name="settlement"/>.</summary>
        public double CleanPrice(IYieldCurve curve, DateOnly settlement) => DirtyPrice(curve, settlement) - AccruedInterest(settlement);

        /// <summary>
        /// The present value in currency, at the curve's reference date, of the cash flows the buyer receives from the
        /// settlement date of that reference date on (flows already paid or ex-coupon are excluded).
        /// </summary>
        public double Npv(IYieldCurve curve)
        {
            ArgumentNullException.ThrowIfNull(curve);
            var settlement = SettlementDate(curve.ReferenceDate);
            return DirtyPrice(curve, settlement) * curve.DiscountFactor(settlement) * Notional / 100.0;
        }

        /// <summary>
        /// The Z-spread of a clean price: the constant <c>s</c> added to the continuously compounded zero rates of
        /// <paramref name="curve"/> (<c>P(t) exp(-s t)</c>) for which the bond's clean price equals <paramref name="cleanPrice"/>.
        /// Zero when the curve reprices the bond. Settlement is that of the curve's reference date unless given.
        /// </summary>
        public double ZSpread(double cleanPrice, IYieldCurve curve, DateOnly? settlement = null)
        {
            ArgumentNullException.ThrowIfNull(curve);
            var settle = settlement ?? SettlementDate(curve.ReferenceDate);
            var target = cleanPrice + AccruedInterest(settle);
            return Solvers.BracketAndBrent(s => DirtyPrice(ZeroShiftedCurve.Parallel(curve, s), settle) - target, 0.0, 0.01, 1e-15);
        }

        /// <summary>
        /// The effective duration <c>-(1/P) dP/dr</c> of the dirty price for a parallel shift <c>r</c> of the continuously compounded
        /// zero curve, by central bump-and-reprice with shifts of <paramref name="bump"/> either side.
        /// </summary>
        public double EffectiveDuration(IYieldCurve curve, double bump = BasisPoint)
        {
            var settlement = SettlementDate(curve.ReferenceDate);
            return -ParallelDerivative(curve, settlement, bump) / DirtyPrice(curve, settlement);
        }

        /// <summary>
        /// The price change per 100 of notional for a one-basis-point rise of the zero curve (positive for a long position), by
        /// central bump-and-reprice of the dirty price.
        /// </summary>
        public double EffectiveDv01(IYieldCurve curve, double bump = BasisPoint) =>
            -ParallelDerivative(curve, SettlementDate(curve.ReferenceDate), bump) * BasisPoint;

        /// <summary>
        /// Key-rate DV01s: for each key time, the price change per 100 of notional (as <see cref="EffectiveDv01"/>) of a one-basis-point
        /// rise of the zero curve with a triangular weight peaking at that time (see <see cref="ZeroShiftedCurve.KeyRate"/>). They
        /// sum to the parallel DV01 up to the second-order cross terms of the central differences.
        /// </summary>
        public double[] KeyRateDv01s(IYieldCurve curve, IReadOnlyList<double> keyTimes, double bump = BasisPoint)
        {
            ArgumentNullException.ThrowIfNull(curve);
            ArgumentNullException.ThrowIfNull(keyTimes);
            var settlement = SettlementDate(curve.ReferenceDate);
            var result = new double[keyTimes.Count];
            for (var k = 0; k < result.Length; k++)
            {
                var up = DirtyPrice(ZeroShiftedCurve.KeyRate(curve, keyTimes, k, bump), settlement);
                var down = DirtyPrice(ZeroShiftedCurve.KeyRate(curve, keyTimes, k, -bump), settlement);
                result[k] = -(up - down) / (2.0 * bump) * BasisPoint;
            }
            return result;
        }

        // dP/dr of the dirty price for a parallel shift r.
        private double ParallelDerivative(IYieldCurve curve, DateOnly settlement, double bump)
        {
            ArgumentNullException.ThrowIfNull(curve);
            if (!(bump > 0.0)) throw new ArgumentOutOfRangeException(nameof(bump), bump, "The bump must be positive.");
            var up = DirtyPrice(ZeroShiftedCurve.Parallel(curve, bump), settlement);
            var down = DirtyPrice(ZeroShiftedCurve.Parallel(curve, -bump), settlement);
            return (up - down) / (2.0 * bump);
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Price <-> yield
        // ---------------------------------------------------------------------------------------------------------------

        /// <summary>The dirty price per 100 of notional for a yield.</summary>
        /// <exception cref="ArgumentException">The settlement date is before issue or the bond has matured.</exception>
        public double DirtyPriceFromYield(double yield, DateOnly settlement, YieldCompounding compounding = YieldCompounding.Periodic)
        {
            var flows = RemainingFlows(settlement);
            var f = (double)Frequency.PeriodsPerYear();
            var sum = 0.0;
            foreach (var flow in flows)
                sum += flow.AmountPer100 * Discount(yield, flow.Periods, f, compounding);
            return sum;
        }

        /// <summary>The clean price for a yield.</summary>
        public double CleanPriceFromYield(double yield, DateOnly settlement, YieldCompounding compounding = YieldCompounding.Periodic) =>
            DirtyPriceFromYield(yield, settlement, compounding) - AccruedInterest(settlement);

        /// <summary>The yield for a clean price, by Brent's method on the dirty price (accrued interest added).</summary>
        /// <exception cref="InvalidOperationException">No yield between -50% and a very large one reproduces the price.</exception>
        public double YieldFromCleanPrice(double cleanPrice, DateOnly settlement, YieldCompounding compounding = YieldCompounding.Periodic) =>
            YieldFromDirtyPrice(cleanPrice + AccruedInterest(settlement), settlement, compounding);

        /// <summary>The yield for a dirty price.</summary>
        /// <exception cref="InvalidOperationException">No yield between -50% and a very large one reproduces the price.</exception>
        public double YieldFromDirtyPrice(double dirtyPrice, DateOnly settlement, YieldCompounding compounding = YieldCompounding.Periodic)
        {
            if (!(dirtyPrice > 0.0) || !double.IsFinite(dirtyPrice))
                throw new ArgumentOutOfRangeException(nameof(dirtyPrice), dirtyPrice, "The price must be finite and positive.");
            var flows = RemainingFlows(settlement);
            var f = (double)Frequency.PeriodsPerYear();
            double Price(double y)
            {
                var sum = 0.0;
                foreach (var flow in flows) sum += flow.AmountPer100 * Discount(y, flow.Periods, f, compounding);
                return sum;
            }
            // The price falls with the yield, so [-50%, y_hi] brackets the root once Price(y_hi) is below the target.
            double lower = -0.5, upper = 0.5;
            if (!(Price(lower) > dirtyPrice))
                throw new InvalidOperationException($"The price {dirtyPrice} is above the price at a yield of {lower}.");
            while (Price(upper) > dirtyPrice)
            {
                upper *= 2.0;
                if (upper > 1e6) throw new InvalidOperationException($"No yield reproduces the price {dirtyPrice}.");
            }
            return Solvers.Brent(y => Price(y) - dirtyPrice, lower, upper, 1e-15);
        }

        /// <summary>
        /// The Macaulay duration in years: the present-value-weighted average time to the cash flows, <c>sum t_k PV_k / sum PV_k</c>,
        /// with <c>t = e / f</c>.
        /// </summary>
        public double MacaulayDuration(double yield, DateOnly settlement, YieldCompounding compounding = YieldCompounding.Periodic)
        {
            var (price, _, _, weighted) = Sensitivities(yield, settlement, compounding);
            return weighted / price;
        }

        /// <summary>
        /// The modified duration <c>-(1/P) dP/dy</c> of the dirty price: the Macaulay duration divided by <c>1 + y/f</c> (periodic),
        /// <c>1 + y</c> (annual), or equal to it (continuous).
        /// </summary>
        public double ModifiedDuration(double yield, DateOnly settlement, YieldCompounding compounding = YieldCompounding.Periodic)
        {
            var (price, derivative, _, _) = Sensitivities(yield, settlement, compounding);
            return -derivative / price;
        }

        /// <summary>The convexity <c>(1/P) d2P/dy2</c> of the dirty price.</summary>
        public double Convexity(double yield, DateOnly settlement, YieldCompounding compounding = YieldCompounding.Periodic)
        {
            var (price, _, second, _) = Sensitivities(yield, settlement, compounding);
            return second / price;
        }

        /// <summary>
        /// The DV01 (a.k.a. PV01) per 100 of notional: the price decrease for a one-basis-point rise of the yield, i.e.
        /// <c>modified duration x dirty price x 1e-4</c> (clean and dirty price move together: the accrued interest does not depend on the yield).
        /// </summary>
        public double Dv01(double yield, DateOnly settlement, YieldCompounding compounding = YieldCompounding.Periodic) =>
            -Sensitivities(yield, settlement, compounding).First * BasisPoint;

        // Dirty price P, dP/dy, d2P/dy2 and the time-weighted sum of present values (numerator of the Macaulay duration).
        private (double Price, double First, double Second, double TimeWeighted) Sensitivities(double y, DateOnly settlement, YieldCompounding compounding)
        {
            var f = (double)Frequency.PeriodsPerYear();
            double p = 0.0, d1 = 0.0, d2 = 0.0, tw = 0.0;
            foreach (var flow in RemainingFlows(settlement))
            {
                var e = flow.Periods;
                var t = e / f;
                double df, first, second;
                switch (compounding)
                {
                    case YieldCompounding.Periodic:
                        df = Math.Pow(1.0 + y / f, -e);
                        first = -t / (1.0 + y / f) * df;
                        second = t * (e + 1.0) / f / ((1.0 + y / f) * (1.0 + y / f)) * df;
                        break;
                    case YieldCompounding.Annual:
                        df = Math.Pow(1.0 + y, -t);
                        first = -t / (1.0 + y) * df;
                        second = t * (t + 1.0) / ((1.0 + y) * (1.0 + y)) * df;
                        break;
                    case YieldCompounding.Continuous:
                        df = Math.Exp(-y * t);
                        first = -t * df;
                        second = t * t * df;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(compounding), compounding, "Unknown compounding.");
                }
                p += flow.AmountPer100 * df;
                d1 += flow.AmountPer100 * first;
                d2 += flow.AmountPer100 * second;
                tw += flow.AmountPer100 * t * df;
            }
            return (p, d1, d2, tw);
        }

        private static double Discount(double y, double e, double f, YieldCompounding compounding) => compounding switch
        {
            YieldCompounding.Periodic => Math.Pow(1.0 + y / f, -e),
            YieldCompounding.Annual => Math.Pow(1.0 + y, -e / f),
            YieldCompounding.Continuous => Math.Exp(-y * e / f),
            _ => throw new ArgumentOutOfRangeException(nameof(compounding), compounding, "Unknown compounding."),
        };

        // ---------------------------------------------------------------------------------------------------------------
        // Cash flows seen from a settlement date
        // ---------------------------------------------------------------------------------------------------------------

        private readonly record struct RemainingFlow(DateOnly Date, double AmountPer100, double Periods);

        // Index of the first period whose payment date is after the settlement date (-1 when the bond has matured).
        private int FirstUnpaidIndex(DateOnly settlement)
        {
            for (var i = 0; i < _paymentDates.Length; i++)
                if (_paymentDates[i] > settlement) return i;
            return -1;
        }

        // Index of the accrual period (unadjusted dates) containing the settlement date, or -1 outside [issue, maturity).
        private int AccrualPeriodIndex(DateOnly settlement)
        {
            for (var i = 0; i < _periods.Length; i++)
                if (_periods[i].UnadjustedStart <= settlement && settlement < _periods[i].UnadjustedEnd) return i;
            return -1;
        }

        private bool IsExCoupon(int period, DateOnly settlement) =>
            ExCouponDays > 0 && settlement < _paymentDates[period]
            && settlement >= Schedule.Calendar.Advance(_paymentDates[period], -ExCouponDays);

        // The cash flows the buyer receives, with their time in coupon periods from settlement.
        private List<RemainingFlow> RemainingFlows(DateOnly settlement)
        {
            if (settlement < IssueDate)
                throw new ArgumentException($"The settlement date {settlement} is before the issue date {IssueDate}.", nameof(settlement));
            var first = FirstUnpaidIndex(settlement);
            if (first < 0)
                throw new ArgumentException($"The bond has matured: the settlement date {settlement} is not before the final payment {FinalPaymentDate}.", nameof(settlement));
            var f = (double)Frequency.PeriodsPerYear();
            // Fraction of the current period left, in periods; clamped for a settlement between unadjusted end and payment date.
            var end = _periods[first].UnadjustedEnd;
            var from = settlement < end ? settlement : end;
            var e = f * DayCounter.YearFraction(from, end, _contexts[first]);
            var flows = new List<RemainingFlow>(_periods.Length - first + 1);
            for (var i = first; i < _periods.Length; i++)
            {
                if (i > first) e += _regular[i] ? 1.0 : f * _periods[i].YearFraction;
                if (!(i == first && IsExCoupon(i, settlement)))
                    flows.Add(new RemainingFlow(_paymentDates[i], 100.0 * CouponRate * _periods[i].YearFraction, e));
            }
            flows.Add(new RemainingFlow(_paymentDates[^1], Redemption, e));
            return flows;
        }
    }
}
