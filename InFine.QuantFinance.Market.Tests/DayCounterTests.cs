using System;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

/// <summary>
/// Expected values are stated with their derivation. Where a value is a long decimal it was obtained by an independent
/// per-day / formula script (Python, not sharing code with the library) and cross-checked by hand on the fraction shown.
/// </summary>
public class DayCounterTests
{
    private const double Tol = 1e-12;

    private static double Yf(IDayCounter counter, string start, string end) => counter.YearFraction(D.Of(start), D.Of(end));

    // --- Actual / fixed denominators -------------------------------------------------------------------------------

    [Fact]
    public void ActualFixedDenominators()
    {
        // 15 Jan -> 15 Jul 2025: 16 (rest of Jan) + 28 + 31 + 30 + 31 + 30 + 15 = 181 days.
        Assert.Equal(181, DayCounter.Actual360.DayCount(D.Of("2025-01-15"), D.Of("2025-07-15")));
        Assert.Equal(181 / 360.0, Yf(DayCounter.Actual360, "2025-01-15", "2025-07-15"), Tol);
        Assert.Equal(181 / 365.0, Yf(DayCounter.Actual365Fixed, "2025-01-15", "2025-07-15"), Tol);
        Assert.Equal(181 / 365.25, Yf(DayCounter.Actual36525, "2025-01-15", "2025-07-15"), Tol);
        // 365 Fixed ignores leap years: 1 Jan 2024 -> 1 Jan 2025 is 366 days.
        Assert.Equal(366 / 365.0, Yf(DayCounter.Actual365Fixed, "2024-01-01", "2025-01-01"), Tol);
        Assert.Equal(366 / 360.0, Yf(DayCounter.Actual360, "2024-01-01", "2025-01-01"), Tol);
    }

    [Fact]
    public void ZeroLengthAndReversedIntervals()
    {
        var counters = new[]
        {
            DayCounter.Actual360, DayCounter.Actual365Fixed, DayCounter.Actual36525, DayCounter.Thirty360BondBasis,
            DayCounter.Thirty360E, DayCounter.Thirty360EIsda, DayCounter.ActualActualIsda, DayCounter.ActualActualAfb,
            new Business252(TargetCalendar.Instance),
        };
        var a = D.Of("2023-01-31");
        var b = D.Of("2024-03-29");
        foreach (var counter in counters)
        {
            Assert.Equal(0.0, counter.YearFraction(a, a));
            Assert.Equal(0, counter.DayCount(a, a));
            Assert.Equal(-counter.YearFraction(a, b), counter.YearFraction(b, a), Tol);
            Assert.Equal(-counter.DayCount(a, b), counter.DayCount(b, a));
            Assert.True(counter.YearFraction(a, b) > 0.0, counter.Name);
        }
    }

    // --- Actual/Actual: ISDA memo 'EMU and market conventions: recent developments' example -------------------------
    // 1 Nov 2003 -> 1 May 2004. Total 182 days = 61 (Nov 1 -> Jan 1 2004) + 121 (Jan 1 -> May 1 2004, leap year).

    private const string MemoStart = "2003-11-01";
    private const string MemoEnd = "2004-05-01";

    [Fact]
    public void ActualActualIsdaMemoExample()
    {
        // 61/365 + 121/366 = 0.16712328767123 + 0.33060109289617 = 0.49772438056741
        // (also obtained by summing 1/365 or 1/366 per day over the 182 days with exact rational arithmetic).
        Assert.Equal(61 / 365.0 + 121 / 366.0, Yf(DayCounter.ActualActualIsda, MemoStart, MemoEnd), Tol);
        Assert.Equal(0.49772438056741, Yf(DayCounter.ActualActualIsda, MemoStart, MemoEnd), 1e-13);
    }

    [Fact]
    public void ActualActualAfbMemoExample()
    {
        // The period contains 29 Feb 2004, so 182 / 366 = 0.49726775956284.
        Assert.Equal(182 / 366.0, Yf(DayCounter.ActualActualAfb, MemoStart, MemoEnd), Tol);
        Assert.Equal(0.497267759563, Yf(DayCounter.ActualActualAfb, MemoStart, MemoEnd), 1e-12);
    }

    [Fact]
    public void ActualActualIcmaMemoExample()
    {
        // The accrual is the whole semiannual reference period: 182 / (2 x 182) = 0.5, whatever the leap year.
        var yf = DayCounter.ActualActualIcma.YearFraction(D.Of(MemoStart), D.Of(MemoEnd), D.Of(MemoStart), D.Of(MemoEnd), Frequency.Semiannual);
        Assert.Equal(0.5, yf, Tol);
    }

    [Fact]
    public void ActualActualIsdaSplitsYearsAtJanuaryFirst()
    {
        // Same year: 1 Mar -> 1 Sep 2004 = 31+30+31+30+31+31 = 184 days, over 366.
        Assert.Equal(184 / 366.0, Yf(DayCounter.ActualActualIsda, "2004-03-01", "2004-09-01"), Tol);
        Assert.Equal(184 / 365.0, Yf(DayCounter.ActualActualIsda, "2003-03-01", "2003-09-01"), Tol);
        // Whole calendar years are exactly 1 each, leap or not.
        Assert.Equal(2.0, Yf(DayCounter.ActualActualIsda, "2003-01-01", "2005-01-01"), Tol);
        // 1 Jul 2003 -> 1 Mar 2005: 184/365 (Jul 1 -> Jan 1) + 1 (2004) + 59/365 (Jan 1 -> Mar 1 2005) = 1 + 243/365.
        Assert.Equal(1 + 243 / 365.0, Yf(DayCounter.ActualActualIsda, "2003-07-01", "2005-03-01"), Tol);
        Assert.Equal(1.6657534246575343, Yf(DayCounter.ActualActualIsda, "2003-07-01", "2005-03-01"), 1e-13);
    }

    [Fact]
    public void ActualActualAfbCountsWholeYearsBackFromTheEndDate()
    {
        Assert.Equal(3.0, Yf(DayCounter.ActualActualAfb, "2002-01-15", "2005-01-15"), Tol);
        // 31 Dec 2003 -> 1 Mar 2005: one whole year back to 1 Mar 2004, remainder 31 Dec 2003 -> 1 Mar 2004 = 1 + 31 + 29 = 61 days
        // containing 29 Feb 2004, so 1 + 61/366 = 1 + 1/6.
        Assert.Equal(1 + 61 / 366.0, Yf(DayCounter.ActualActualAfb, "2003-12-31", "2005-03-01"), Tol);
        // No leap day in [2005-03-01, 2005-09-01): 184 / 365.
        Assert.Equal(184 / 365.0, Yf(DayCounter.ActualActualAfb, "2005-03-01", "2005-09-01"), Tol);
    }

    // --- Actual/Actual ICMA ----------------------------------------------------------------------------------------

    [Fact]
    public void IcmaRegularPeriodsAreExactFractionsOfAYear()
    {
        Assert.Equal(0.25, DayCounter.ActualActualIcma.YearFraction(D.Of("2025-01-15"), D.Of("2025-04-15"), D.Of("2025-01-15"), D.Of("2025-04-15"), Frequency.Quarterly), Tol);
        Assert.Equal(1.0, DayCounter.ActualActualIcma.YearFraction(D.Of("2024-01-15"), D.Of("2025-01-15"), D.Of("2024-01-15"), D.Of("2025-01-15"), Frequency.Annual), Tol);
        // Half of a semiannual reference period 15 Jan -> 15 Jul 2025 (181 days): 15 Jan -> 15 Apr is 90 days, 90 / (2 x 181).
        Assert.Equal(90 / 362.0, DayCounter.ActualActualIcma.YearFraction(D.Of("2025-01-15"), D.Of("2025-04-15"), D.Of("2025-01-15"), D.Of("2025-07-15"), Frequency.Semiannual), Tol);
    }

    [Fact]
    public void IcmaShortStubUsesTheNotionalRegularPeriod()
    {
        // Stub 1 Mar -> 15 Jul 2025 = 31 (Mar) + 30 (Apr) + 31 (May) + 30 (Jun) + 14 = 136 days
        // in the notional period 15 Jan -> 15 Jul 2025 (181 days): 136 / (2 x 181) = 0.375690607735.
        var yf = DayCounter.ActualActualIcma.YearFraction(D.Of("2025-03-01"), D.Of("2025-07-15"), D.Of("2025-01-15"), D.Of("2025-07-15"), Frequency.Semiannual);
        Assert.Equal(136 / 362.0, yf, Tol);
        Assert.Equal(0.3756906077348066, yf, 1e-13);
    }

    [Fact]
    public void IcmaLongStubIsSplitOverNotionalPeriods()
    {
        // Long first stub 1 Mar 2025 -> 15 Jan 2026, reference period 15 Jul 2025 -> 15 Jan 2026 (184 days).
        // Part in the previous notional period 15 Jan -> 15 Jul 2025 (181 days): 136 / 362; part in the reference period: 0.5.
        var first = DayCounter.ActualActualIcma.YearFraction(D.Of("2025-03-01"), D.Of("2026-01-15"), D.Of("2025-07-15"), D.Of("2026-01-15"), Frequency.Semiannual);
        Assert.Equal(136 / 362.0 + 0.5, first, Tol);
        Assert.Equal(0.8756906077348066, first, 1e-13);
        // Long last stub 1 Mar 2026 -> 15 Jan 2027, reference period 1 Mar -> 1 Sep 2026: 0.5 plus 1 Sep 2026 -> 15 Jan 2027
        // (136 days) in the next notional period 1 Sep 2026 -> 1 Mar 2027 (30+31+30+31+31+28 = 181 days).
        var last = DayCounter.ActualActualIcma.YearFraction(D.Of("2026-03-01"), D.Of("2027-01-15"), D.Of("2026-03-01"), D.Of("2026-09-01"), Frequency.Semiannual);
        Assert.Equal(0.5 + 136 / 362.0, last, Tol);
    }

    [Fact]
    public void IcmaRequiresAValidContext()
    {
        var a = D.Of("2025-01-15");
        var b = D.Of("2025-07-15");
        Assert.Throws<ArgumentException>(() => DayCounter.ActualActualIcma.YearFraction(a, b));
        Assert.Throws<ArgumentException>(() => DayCounter.ActualActualIcma.YearFraction(a, b, new DayCountContext(Frequency: Frequency.Semiannual)));
        Assert.Throws<ArgumentException>(() => DayCounter.ActualActualIcma.YearFraction(a, b, a, b, Frequency.Once));
        Assert.Throws<ArgumentException>(() => DayCounter.ActualActualIcma.YearFraction(a, b, b, a, Frequency.Semiannual));
    }

    [Fact]
    public void OtherCountersIgnoreTheContext()
    {
        var a = D.Of("2025-01-15");
        var b = D.Of("2025-07-15");
        Assert.Equal(DayCounter.Actual360.YearFraction(a, b), DayCounter.Actual360.YearFraction(a, b, a, b, Frequency.Semiannual));
    }

    // --- 30/360 variants: hand-computed with 360 x dY + 30 x dM + (D2 - D1) ------------------------------------------

    [Theory]
    // start, end, termination (or null), Bond Basis days, 30E/360 days, 30E/360 ISDA days
    // 31 Jan -> 28 Feb 2023: BB D1=30 so D2 stays 28: 30 + (28-30) = 28. 30E: same, 28. ISDA: 28 Feb is the last day of
    //   February and not the termination date -> D2 = 30: 30 + 0 = 30.
    [InlineData("2023-01-31", "2023-02-28", null, 28, 28, 30)]
    // ...but if 28 Feb 2023 is the termination date it is left at 28: 28.
    [InlineData("2023-01-31", "2023-02-28", "2023-02-28", 28, 28, 28)]
    // 28 Feb 2023 -> 28 Feb 2024: BB and 30E: 360. ISDA: D1 = 30 (last day of Feb 2023), D2 = 28 (Feb 2024 ends on the 29th): 360 - 2 = 358.
    [InlineData("2023-02-28", "2024-02-28", null, 360, 360, 358)]
    // 29 Feb 2024 -> 29 Mar 2024: BB/30E: 30 + 0 = 30. ISDA: D1 = 30 (last day of Feb): 30 + (29-30) = 29.
    [InlineData("2024-02-29", "2024-03-29", null, 30, 30, 29)]
    // 30 Jan -> 31 Mar 2023: D1 = 30 so BB sets D2 = 30: 60. All variants 60.
    [InlineData("2023-01-30", "2023-03-31", null, 60, 60, 60)]
    // 15 Jan -> 31 Mar 2023: BB keeps D2 = 31 (D1 < 30): 60 + 16 = 76. 30E and ISDA cap D2 at 30: 75.
    [InlineData("2023-01-15", "2023-03-31", null, 76, 75, 75)]
    // 31 Aug 2022 -> 28 Feb 2023: D1 = 30. BB/30E: 360 - 180 + (28-30) = 178. ISDA: 180 unless 28 Feb 2023 is the termination (then 178).
    [InlineData("2022-08-31", "2023-02-28", null, 178, 178, 180)]
    [InlineData("2022-08-31", "2023-02-28", "2023-02-28", 178, 178, 178)]
    // 31 Jan -> 29 Feb 2024 (leap year, last day of February): BB/30E: 30 + (29-30) = 29; ISDA with termination: 29; without: 30.
    [InlineData("2024-01-31", "2024-02-29", "2024-02-29", 29, 29, 29)]
    [InlineData("2024-01-31", "2024-02-29", null, 29, 29, 30)]
    // 31 Dec 2024 -> 31 Dec 2025: a full year under all variants.
    [InlineData("2024-12-31", "2025-12-31", null, 360, 360, 360)]
    // 1 Jan 2025 -> 31 Dec 2025 : BB: D1=1, D2 stays 31: 330 + 30 = 360; 30E: 359; ISDA: 359.
    [InlineData("2025-01-01", "2025-12-31", null, 360, 359, 359)]
    public void ThirtyOver360Variants(string start, string end, string? termination, int bondBasis, int eurobond, int eurobondIsda)
    {
        var s = D.Of(start);
        var e = D.Of(end);
        var context = new DayCountContext(TerminationDate: termination is null ? null : D.Of(termination));
        Assert.Equal(bondBasis, DayCounter.Thirty360BondBasis.DayCount(s, e));
        Assert.Equal(bondBasis / 360.0, DayCounter.Thirty360BondBasis.YearFraction(s, e, context), Tol);
        Assert.Equal(eurobond, DayCounter.Thirty360E.DayCount(s, e));
        Assert.Equal(eurobond / 360.0, DayCounter.Thirty360E.YearFraction(s, e, context), Tol);
        Assert.Equal(eurobondIsda / 360.0, DayCounter.Thirty360EIsda.YearFraction(s, e, context), Tol);
        if (termination is null)
            Assert.Equal(eurobondIsda, DayCounter.Thirty360EIsda.DayCount(s, e));
    }

    // --- Business/252 ----------------------------------------------------------------------------------------------

    [Fact]
    public void Business252CountsBusinessDaysOfTheCalendar()
    {
        var weekends = new Business252(WeekendsOnlyCalendar.Instance);
        // Mon 6 Jan -> Mon 13 Jan 2025: Mon..Fri count, the week-end and the end date do not: 5.
        Assert.Equal(5, weekends.DayCount(D.Of("2025-01-06"), D.Of("2025-01-13")));
        Assert.Equal(5 / 252.0, Yf(weekends, "2025-01-06", "2025-01-13"), Tol);
        // A full year of weekdays: 261 (see BusinessDayArithmeticTests).
        Assert.Equal(261 / 252.0, Yf(weekends, "2025-01-01", "2026-01-01"), Tol);

        var target = new Business252(TargetCalendar.Instance);
        // 14 Apr -> 22 Apr 2025: Good Friday 18th and Easter Monday 21st are closed: 14,15,16,17 = 4.
        Assert.Equal(4, target.DayCount(D.Of("2025-04-14"), D.Of("2025-04-22")));
        Assert.Equal(255 / 252.0, Yf(target, "2025-01-01", "2026-01-01"), Tol);
        Assert.Contains("TARGET", target.Name);
        Assert.Throws<ArgumentNullException>(() => new Business252(null!));
    }
}
