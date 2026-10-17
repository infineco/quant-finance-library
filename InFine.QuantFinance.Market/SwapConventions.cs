using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// Market conventions of a vanilla fixed-versus-floating interest-rate swap, used by
    /// <see cref="VanillaSwap.FromTenor"/>: both legs start <see cref="SpotLag"/> business days after the trade date and
    /// end one tenor later, with schedules generated backward and rolled with <see cref="Convention"/> on <see cref="Calendar"/>.
    /// </summary>
    /// <param name="Calendar">The business-day calendar of both legs and of the spot lag.</param>
    /// <param name="SpotLag">Business days from trade date to start date.</param>
    /// <param name="Convention">The business-day convention of every schedule date.</param>
    /// <param name="FixedFrequency">Coupon frequency of the fixed leg.</param>
    /// <param name="FixedDayCounter">Accrual convention of the fixed leg.</param>
    /// <param name="FloatingFrequency">Coupon frequency of the floating leg (also the tenor of its index).</param>
    /// <param name="FloatingDayCounter">Accrual convention of the floating leg and its index.</param>
    /// <param name="EndOfMonth">End-of-month rule for the maturity and the schedules.</param>
    public sealed record SwapConventions(
        IHolidayCalendar Calendar,
        int SpotLag,
        BusinessDayConvention Convention,
        Frequency FixedFrequency,
        IDayCounter FixedDayCounter,
        Frequency FloatingFrequency,
        IDayCounter FloatingDayCounter,
        bool EndOfMonth = false)
    {
        /// <summary>
        /// The EUR vanilla swap against 6M Euribor: TARGET, 2-day spot, ModifiedFollowing, fixed leg annual 30/360
        /// (Bond Basis), floating leg semiannual Actual/360.
        /// </summary>
        public static SwapConventions EurSixMonth { get; } = new(
            TargetCalendar.Instance, 2, BusinessDayConvention.ModifiedFollowing,
            Frequency.Annual, DayCounter.Thirty360BondBasis,
            Frequency.Semiannual, DayCounter.Actual360);
    }
}
