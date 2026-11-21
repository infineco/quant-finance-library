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
        DateOnly? TerminationDate = null)
    {
        /// <summary>
        /// True when the coupon schedule follows the end-of-month rule (ICMA). The notional periods before and after the
        /// reference period are then month ends too: six months before 28 February 2025 is 31 August 2024, not the 28th.
        /// It matters only for an accrual that leaves the reference period (a long stub) on a month-end schedule.
        /// </summary>
        // An init-only property, not a positional parameter: the constructor and Deconstruct keep their signatures.
        public bool EndOfMonth { get; init; }
    }
}
