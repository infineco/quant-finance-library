using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// One accrual period of a <see cref="Schedule"/>, with its year fraction under a given day-count convention.
    /// </summary>
    /// <param name="UnadjustedStart">Start before business-day adjustment.</param>
    /// <param name="UnadjustedEnd">End before business-day adjustment.</param>
    /// <param name="Start">Start after business-day adjustment; also the payment date of the previous period.</param>
    /// <param name="End">End after business-day adjustment; also this period's payment date.</param>
    /// <param name="YearFraction">The year fraction of the period (on adjusted or unadjusted dates, as asked of the schedule).</param>
    /// <param name="IsRegular">False for a stub period (shorter or longer than the tenor).</param>
    public readonly record struct AccrualPeriod(
        DateOnly UnadjustedStart,
        DateOnly UnadjustedEnd,
        DateOnly Start,
        DateOnly End,
        double YearFraction,
        bool IsRegular);
}
