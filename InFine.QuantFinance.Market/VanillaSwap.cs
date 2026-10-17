using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Market
{
    /// <summary>Which fixed leg a swap holder pays.</summary>
    public enum SwapDirection
    {
        /// <summary>Pays fixed, receives floating.</summary>
        Payer = 0,

        /// <summary>Receives fixed, pays floating.</summary>
        Receiver,
    }

    /// <summary>
    /// A vanilla fixed-versus-floating interest-rate swap. Coupons accrue on the adjusted schedule dates and are paid at
    /// the adjusted end of each accrual period (no payment lag); each floating coupon fixes on the index rate of its own
    /// accrual period (index period = accrual period, no fixing lag) plus a constant spread. All present values are
    /// at the reference date of the discount curve, and every coupon must pay on or after it (no past fixings).
    /// <para>
    /// Pricing takes a discount curve and a forecast curve: the floating rate of period <c>[s, e]</c> is the simple
    /// forward <c>F = (P_f(s) / P_f(e) - 1) / tau</c> on the forecast curve. In the single-curve framework used by
    /// <see cref="PiecewiseYieldCurveBuilder"/> both are the same curve and the floating leg telescopes:
    /// <c>sum tau F P(e) = sum (P(s) - P(e)) = P(start) - P(end)</c>. Multi-curve (e.g. OIS discounting with a separate
    /// Euribor forecast curve) only needs two different curves here.
    /// </para>
    /// </summary>
    public sealed class VanillaSwap : ICurveInstrument
    {
        private readonly AccrualPeriod[] _fixed;
        private readonly AccrualPeriod[] _floating;

        /// <summary>A swap from explicit leg schedules.</summary>
        /// <param name="fixedSchedule">Fixed-leg accrual schedule.</param>
        /// <param name="fixedDayCounter">Fixed-leg accrual convention.</param>
        /// <param name="floatingSchedule">Floating-leg accrual schedule.</param>
        /// <param name="floatingDayCounter">Floating-leg accrual convention (also that of its index).</param>
        /// <param name="fixedRate">The fixed rate (only <see cref="Npv(IYieldCurve)"/> uses it).</param>
        /// <param name="floatingSpread">Spread added to the floating index rate.</param>
        /// <param name="notional">Notional of both legs.</param>
        /// <param name="direction">Payer or receiver of the fixed leg.</param>
        public VanillaSwap(Schedule fixedSchedule, IDayCounter fixedDayCounter, Schedule floatingSchedule, IDayCounter floatingDayCounter,
            double fixedRate = 0.0, double floatingSpread = 0.0, double notional = 1.0, SwapDirection direction = SwapDirection.Payer)
        {
            ArgumentNullException.ThrowIfNull(fixedSchedule);
            ArgumentNullException.ThrowIfNull(fixedDayCounter);
            ArgumentNullException.ThrowIfNull(floatingSchedule);
            ArgumentNullException.ThrowIfNull(floatingDayCounter);
            if (!Enum.IsDefined(direction))
                throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown swap direction.");
            _fixed = ToArray(fixedSchedule.GetAccrualPeriods(fixedDayCounter));
            _floating = ToArray(floatingSchedule.GetAccrualPeriods(floatingDayCounter));
            FixedDayCounter = fixedDayCounter;
            FloatingDayCounter = floatingDayCounter;
            FixedRate = fixedRate;
            FloatingSpread = floatingSpread;
            Notional = notional;
            Direction = direction;
        }

        /// <summary>
        /// A spot-starting swap of the given tenor with market conventions: start = spot date, end = start + tenor
        /// (unadjusted, adjusted by the schedules), both legs generated backward.
        /// </summary>
        public static VanillaSwap FromTenor(DateOnly tradeDate, Period tenor, SwapConventions conventions, double fixedRate = 0.0,
            double floatingSpread = 0.0, double notional = 1.0, SwapDirection direction = SwapDirection.Payer)
        {
            ArgumentNullException.ThrowIfNull(conventions);
            var start = Deposit.SpotDate(tradeDate, conventions.Calendar, conventions.SpotLag);
            return FromDates(start, tenor.AddTo(start, conventions.EndOfMonth), conventions, fixedRate, floatingSpread, notional, direction);
        }

        /// <summary>
        /// A forward-starting swap: it starts <paramref name="forwardStart"/> after the spot date (the result rolled onto a business
        /// day with the conventions' business-day convention) and ends one <paramref name="tenor"/> later, e.g. "1Y into 5Y".
        /// </summary>
        public static VanillaSwap FromForwardTenor(DateOnly tradeDate, Period forwardStart, Period tenor, SwapConventions conventions,
            double fixedRate = 0.0, double floatingSpread = 0.0, double notional = 1.0, SwapDirection direction = SwapDirection.Payer)
        {
            ArgumentNullException.ThrowIfNull(conventions);
            var spot = Deposit.SpotDate(tradeDate, conventions.Calendar, conventions.SpotLag);
            var start = forwardStart.AddTo(spot, conventions.Calendar, conventions.Convention, conventions.EndOfMonth);
            return FromDates(start, tenor.AddTo(start, conventions.EndOfMonth), conventions, fixedRate, floatingSpread, notional, direction);
        }

        /// <summary>
        /// A swap between explicit unadjusted start and end dates (either may be in the future), both legs generated backward
        /// with the market conventions.
        /// </summary>
        public static VanillaSwap FromDates(DateOnly start, DateOnly end, SwapConventions conventions, double fixedRate = 0.0,
            double floatingSpread = 0.0, double notional = 1.0, SwapDirection direction = SwapDirection.Payer)
        {
            ArgumentNullException.ThrowIfNull(conventions);
            Schedule Leg(Frequency frequency) => new ScheduleBuilder(start, end)
                .WithFrequency(frequency)
                .WithCalendar(conventions.Calendar)
                .WithConvention(conventions.Convention)
                .WithEndOfMonth(conventions.EndOfMonth)
                .Build();
            return new VanillaSwap(Leg(conventions.FixedFrequency), conventions.FixedDayCounter,
                Leg(conventions.FloatingFrequency), conventions.FloatingDayCounter, fixedRate, floatingSpread, notional, direction);
        }

        /// <summary>The fixed-leg accrual periods.</summary>
        public IReadOnlyList<AccrualPeriod> FixedPeriods => _fixed;

        /// <summary>The floating-leg accrual periods.</summary>
        public IReadOnlyList<AccrualPeriod> FloatingPeriods => _floating;

        /// <summary>Fixed-leg accrual convention.</summary>
        public IDayCounter FixedDayCounter { get; }

        /// <summary>Floating-leg accrual convention.</summary>
        public IDayCounter FloatingDayCounter { get; }

        /// <summary>The fixed rate.</summary>
        public double FixedRate { get; }

        /// <summary>The spread over the floating index.</summary>
        public double FloatingSpread { get; }

        /// <summary>The notional of both legs.</summary>
        public double Notional { get; }

        /// <summary>Payer or receiver of the fixed leg.</summary>
        public SwapDirection Direction { get; }

        /// <summary>The earliest accrual start of the two legs.</summary>
        public DateOnly StartDate => _fixed[0].Start < _floating[0].Start ? _fixed[0].Start : _floating[0].Start;

        /// <inheritdoc />
        public DateOnly MaturityDate => _fixed[^1].End > _floating[^1].End ? _fixed[^1].End : _floating[^1].End;

        /// <summary>The fixed-leg annuity per unit notional: <c>A = sum tau_i P(T_i)</c> over the fixed coupons.</summary>
        public double FixedLegAnnuity(IYieldCurve discountCurve) => Annuity(_fixed, discountCurve);

        /// <summary>The floating-leg annuity per unit notional: <c>sum tau_j P(T_j)</c>, the value of one unit of spread.</summary>
        public double FloatingLegAnnuity(IYieldCurve discountCurve) => Annuity(_floating, discountCurve);

        /// <summary>
        /// The floating-leg present value per unit notional, <c>sum tau_j (F_j + spread) P_d(T_j)</c>, with <c>F_j</c> the
        /// simple forward of the accrual period on <paramref name="forecastCurve"/>.
        /// </summary>
        public double FloatingLegPresentValue(IYieldCurve discountCurve, IYieldCurve forecastCurve)
        {
            ArgumentNullException.ThrowIfNull(discountCurve);
            ArgumentNullException.ThrowIfNull(forecastCurve);
            var pv = 0.0;
            foreach (var period in _floating)
            {
                var forward = Deposit.SimpleForward(forecastCurve, period.Start, period.End, period.YearFraction);
                pv += period.YearFraction * (forward + FloatingSpread) * discountCurve.DiscountFactor(period.End);
            }
            return pv;
        }

        /// <summary>Single-curve floating-leg present value per unit notional.</summary>
        public double FloatingLegPresentValue(IYieldCurve curve) => FloatingLegPresentValue(curve, curve);

        /// <summary>The fixed rate that sets the value to zero: floating-leg value over fixed-leg annuity.</summary>
        public double ParRate(IYieldCurve discountCurve, IYieldCurve forecastCurve) =>
            FloatingLegPresentValue(discountCurve, forecastCurve) / FixedLegAnnuity(discountCurve);

        /// <summary>Single-curve par rate.</summary>
        public double ParRate(IYieldCurve curve) => ParRate(curve, curve);

        /// <summary>
        /// The value at the discount curve's reference date: <c>N (floating value - K A)</c> for a payer,
        /// the opposite for a receiver.
        /// </summary>
        public double Npv(IYieldCurve discountCurve, IYieldCurve forecastCurve)
        {
            var payer = FloatingLegPresentValue(discountCurve, forecastCurve) - FixedRate * FixedLegAnnuity(discountCurve);
            return Notional * (Direction == SwapDirection.Payer ? payer : -payer);
        }

        /// <summary>Single-curve value.</summary>
        public double Npv(IYieldCurve curve) => Npv(curve, curve);

        /// <summary>
        /// The floating spread that sets the value to zero at the fixed rate of the swap, replacing <see cref="FloatingSpread"/>:
        /// <c>(K A_fixed - (floating value - spread A_float)) / A_float</c>.
        /// </summary>
        public double ParSpread(IYieldCurve discountCurve, IYieldCurve forecastCurve)
        {
            var floatingAnnuity = FloatingLegAnnuity(discountCurve);
            var indexLeg = FloatingLegPresentValue(discountCurve, forecastCurve) - FloatingSpread * floatingAnnuity;
            return (FixedRate * FixedLegAnnuity(discountCurve) - indexLeg) / floatingAnnuity;
        }

        /// <summary>Single-curve par spread.</summary>
        public double ParSpread(IYieldCurve curve) => ParSpread(curve, curve);

        /// <summary>
        /// The PV01: the value of one basis point of fixed rate, <c>N A_fixed x 1e-4</c> (positive). A payer's value falls
        /// by this much, a receiver's rises, when the fixed rate rises by one basis point.
        /// </summary>
        public double Pv01(IYieldCurve discountCurve) => Notional * FixedLegAnnuity(discountCurve) * 1e-4;

        /// <summary>
        /// The DV01: the change of <see cref="Npv(IYieldCurve, IYieldCurve)"/> for a one-basis-point rise of the continuously
        /// compounded zero rates of both curves (parallel shift), by central bump-and-reprice with shifts of <paramref name="bump"/>
        /// either side. Positive for a payer when rates rise.
        /// </summary>
        public double Dv01(IYieldCurve discountCurve, IYieldCurve forecastCurve, double bump = 1e-4)
        {
            ArgumentNullException.ThrowIfNull(discountCurve);
            ArgumentNullException.ThrowIfNull(forecastCurve);
            if (!(bump > 0.0)) throw new ArgumentOutOfRangeException(nameof(bump), bump, "The bump must be positive.");
            double Shifted(double shift)
            {
                var discount = ZeroShiftedCurve.Parallel(discountCurve, shift);
                return Npv(discount, ReferenceEquals(discountCurve, forecastCurve) ? discount : ZeroShiftedCurve.Parallel(forecastCurve, shift));
            }
            return (Shifted(bump) - Shifted(-bump)) / (2.0 * bump) * 1e-4;
        }

        /// <summary>Single-curve DV01.</summary>
        public double Dv01(IYieldCurve curve, double bump = 1e-4) => Dv01(curve, curve, bump);

        /// <summary>The fixed-leg coupons (rate <see cref="FixedRate"/>, amounts for <see cref="Notional"/>, unsigned).</summary>
        public IReadOnlyList<LegCashFlow> FixedLegCashFlows()
        {
            var flows = new LegCashFlow[_fixed.Length];
            for (var i = 0; i < flows.Length; i++)
            {
                var p = _fixed[i];
                flows[i] = new LegCashFlow(p.Start, p.End, p.End, p.YearFraction, FixedRate, Notional * FixedRate * p.YearFraction);
            }
            return flows;
        }

        /// <summary>
        /// The floating-leg coupons (rate = simple forward on <paramref name="forecastCurve"/> plus <see cref="FloatingSpread"/>,
        /// amounts for <see cref="Notional"/>, unsigned).
        /// </summary>
        public IReadOnlyList<LegCashFlow> FloatingLegCashFlows(IYieldCurve forecastCurve)
        {
            ArgumentNullException.ThrowIfNull(forecastCurve);
            var flows = new LegCashFlow[_floating.Length];
            for (var i = 0; i < flows.Length; i++)
            {
                var p = _floating[i];
                var rate = Deposit.SimpleForward(forecastCurve, p.Start, p.End, p.YearFraction) + FloatingSpread;
                flows[i] = new LegCashFlow(p.Start, p.End, p.End, p.YearFraction, rate, Notional * rate * p.YearFraction);
            }
            return flows;
        }

        /// <summary>The single-curve par rate, the quote of a swap bootstrap input.</summary>
        double ICurveInstrument.ImpliedRate(IYieldCurve curve) => ParRate(curve);

        private static double Annuity(AccrualPeriod[] periods, IYieldCurve curve)
        {
            ArgumentNullException.ThrowIfNull(curve);
            var annuity = 0.0;
            foreach (var period in periods)
                annuity += period.YearFraction * curve.DiscountFactor(period.End);
            return annuity;
        }

        private static AccrualPeriod[] ToArray(IReadOnlyList<AccrualPeriod> periods)
        {
            var result = new AccrualPeriod[periods.Count];
            for (var i = 0; i < result.Length; i++) result[i] = periods[i];
            return result;
        }
    }
}
