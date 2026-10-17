using System;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// The extra information some day-count conventions need besides the two dates:
    /// Actual/Actual ICMA needs the regular reference (coupon) period containing the accrual and its frequency;
    /// 30E/360 ISDA needs the termination date of the instrument for its February rule.
    /// Conventions ignore the members they do not use, so a fully populated context can be passed to any of them.
    /// </summary>
    /// <param name="RefPeriodStart">Start of the reference period (ICMA).</param>
    /// <param name="RefPeriodEnd">End of the reference period (ICMA).</param>
    /// <param name="Frequency">Number of reference periods per year (ICMA); not <see cref="Market.Frequency.Once"/>.</param>
    /// <param name="TerminationDate">Last date of the instrument (30E/360 ISDA).</param>
    public readonly record struct DayCountContext(
        DateOnly? RefPeriodStart = null,
        DateOnly? RefPeriodEnd = null,
        Frequency? Frequency = null,
        DateOnly? TerminationDate = null);
}
