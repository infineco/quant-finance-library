using System;
using InFine.QuantFinance.Market;
using Xunit;

namespace InFine.QuantFinance.Excel.Core.Tests;

[Collection("QuantFunctions")]
public class DateFunctionTests
{
    private readonly QuantFunctions _qf = new(new ObjectCache());

    // 2025-12-25 is a Thursday and a TARGET holiday; 26 is a TARGET holiday too; 27-28 is a weekend.
    private static readonly double Christmas = T.Serial(2025, 12, 25);

    [Theory]
    [InlineData("Following", "2025-12-29")]
    [InlineData("MF", "2025-12-29")]
    [InlineData("Preceding", "2025-12-24")]
    [InlineData("Unadjusted", "2025-12-25")]
    public void AdjustRollsHolidaysOnTarget(string convention, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), T.Date(_qf.DateAdjust(Christmas, "TARGET", convention)));

    [Fact]
    public void AdjustDefaultsToFollowingAndKnowsTheOtherCalendars()
    {
        Assert.Equal(new DateOnly(2025, 12, 29), T.Date(_qf.DateAdjust(Christmas, "TARGET")));
        // The NYSE is closed on 2025-12-25 but open on the 26th; London is closed on both.
        Assert.Equal(new DateOnly(2025, 12, 26), T.Date(_qf.DateAdjust(Christmas, "NYSE")));
        Assert.Equal(new DateOnly(2025, 12, 29), T.Date(_qf.DateAdjust(Christmas, "LONDON")));
        Assert.Equal(Christmas, T.Num(_qf.DateAdjust(Christmas, "WEEKENDS")));
        // Saturday 2025-12-27, weekends only.
        Assert.Equal(new DateOnly(2025, 12, 29), T.Date(_qf.DateAdjust(T.Serial(2025, 12, 27), "WEEKENDS")));
    }

    [Fact]
    public void ModifiedFollowingStaysInTheMonth()
    {
        // 2025-05-31 is a Saturday: Following would leave May, Modified Following goes back to Friday the 30th.
        double saturday = T.Serial(2025, 5, 31);
        Assert.Equal(new DateOnly(2025, 6, 2), T.Date(_qf.DateAdjust(saturday, "WEEKENDS", "Following")));
        Assert.Equal(new DateOnly(2025, 5, 30), T.Date(_qf.DateAdjust(saturday, "WEEKENDS", "ModifiedFollowing")));
    }

    [Fact]
    public void AdjustMatchesTheLibraryOverAYear()
    {
        var start = new DateOnly(2025, 1, 1);
        for (int i = 0; i < 365; i++)
        {
            var d = start.AddDays(i);
            Assert.Equal(TargetCalendar.Instance.Adjust(d, BusinessDayConvention.ModifiedFollowing),
                T.Date(_qf.DateAdjust(ExcelDates.ToSerial(d), "TARGET", "MF")));
        }
    }

    [Fact]
    public void AdvanceByBusinessDays()
    {
        double eve = T.Serial(2025, 12, 24);
        Assert.Equal(new DateOnly(2025, 12, 29), T.Date(_qf.DateAdvance(eve, 1, "TARGET")));
        Assert.Equal(new DateOnly(2025, 12, 29), T.Date(_qf.DateAdvance(eve, "1B", "TARGET")));
        Assert.Equal(new DateOnly(2025, 12, 30), T.Date(_qf.DateAdvance(eve, "2", "TARGET")));
        Assert.Equal(new DateOnly(2025, 12, 23), T.Date(_qf.DateAdvance(eve, -1, "TARGET")));
        Assert.Equal(eve, T.Num(_qf.DateAdvance(eve, 0, "TARGET")));
        var expected = TargetCalendar.Instance.Advance(new DateOnly(2025, 12, 24), 10);
        Assert.Equal(expected, T.Date(_qf.DateAdvance(eve, 10, "TARGET")));
    }

    [Theory]
    [InlineData("2025-01-15", "3M", "2025-04-15")]
    [InlineData("2025-03-01", "10D", "2025-03-11")]
    [InlineData("2025-01-06", "2W", "2025-01-20")]
    [InlineData("2025-01-15", "1Y", "2026-01-15")]
    [InlineData("2025-01-31", "1M", "2025-02-28")]      // 28 February is a Friday
    [InlineData("2025-04-25", "2D", "2025-04-28")]      // Sunday 27th rolls to Monday (Modified Following)
    public void AdvanceByTenor(string start, string tenor, string expected)
    {
        double serial = ExcelDates.ToSerial(DateOnly.Parse(start));
        Assert.Equal(DateOnly.Parse(expected), T.Date(_qf.DateAdvance(serial, tenor, "TARGET")));
    }

    [Fact]
    public void AdvanceByTenorHonoursConventionAndEndOfMonth()
    {
        // 2025-02-28 + 1M = 2025-03-28 (Friday); with end-of-month it becomes 2025-03-31.
        double feb28 = T.Serial(2025, 2, 28);
        Assert.Equal(new DateOnly(2025, 3, 28), T.Date(_qf.DateAdvance(feb28, "1M", "TARGET")));
        Assert.Equal(new DateOnly(2025, 3, 31), T.Date(_qf.DateAdvance(feb28, "1M", "TARGET", "MF", true)));
        // 2025-08-30 + 1M... = 2025-09-30 (Tuesday); 2025-10-31 is a Friday; pick a Saturday end: 2025-05-31 - 1M.
        Assert.Equal(new DateOnly(2025, 4, 30), T.Date(_qf.DateAdvance(T.Serial(2025, 5, 31), "-1M", "TARGET")));
        var direct = Period.Parse("6M").AddTo(new DateOnly(2025, 8, 30), TargetCalendar.Instance, BusinessDayConvention.Preceding, false);
        Assert.Equal(direct, T.Date(_qf.DateAdvance(T.Serial(2025, 8, 30), "6M", "TARGET", "Preceding")));
    }

    [Fact]
    public void AdvanceRejectsMalformedPeriods()
    {
        Assert.Equal(XlError.Value, _qf.DateAdvance(Christmas, "three months", "TARGET"));
        Assert.Contains("period", ErrorLog.Last);
        Assert.Equal(XlError.Value, _qf.DateAdvance(Christmas, 1.5, "TARGET"));
        Assert.Equal(XlError.Value, _qf.DateAdvance(Christmas, "1M", "TARGET", "sideways"));
    }

    [Fact]
    public void DateFunctionsTakeSerialsIncludingTheLeapYearBugRegion()
    {
        // Serial 61 is 1900-03-01 (a Thursday); one weekday-calendar business day later is Friday 1900-03-02.
        Assert.Equal(new DateOnly(1900, 3, 2), T.Date(_qf.DateAdvance(61.0, 1, "WEEKENDS")));
        Assert.Equal(XlError.Value, _qf.DateAdjust(60.0, "WEEKENDS"));
        Assert.Contains("1900-02-29", ErrorLog.Last);
        Assert.Equal(XlError.Value, _qf.DateAdjust(0.0, "WEEKENDS"));
        Assert.Equal(XlError.Value, _qf.DateAdjust("not a date", "WEEKENDS"));
        Assert.Equal(XlError.Value, _qf.DateAdjust(Christmas, "MARS"));
    }

    [Fact]
    public void DatesBeforeTheExcelEpochCannotBeReturned()
    {
        // 1900-01-02 (serial 2) is a Tuesday: one business day back lands on the Monday 1900-01-01 (serial 1) ...
        Assert.Equal(1.0, T.Num(_qf.DateAdvance(2.0, -1, "WEEKENDS")));
        // ... and one more falls on Friday 1899-12-29, which Excel cannot represent.
        Assert.Equal(XlError.Value, _qf.DateAdvance(1.0, -1, "WEEKENDS"));
    }

    [Theory]
    [InlineData("ACT/360", 366.0 / 360.0)]
    [InlineData("ACT/365F", 366.0 / 365.0)]
    [InlineData("ACT/365.25", 366.0 / 365.25)]
    [InlineData("30/360", 1.0)]
    [InlineData("30E/360", 1.0)]
    [InlineData("ACT/ACT", 1.0)]
    public void YearFracOver2024(string dayCount, double expected)
    {
        Assert.Equal(expected, T.Num(_qf.YearFrac(T.Serial(2024, 1, 1), T.Serial(2025, 1, 1), dayCount)), 14);
    }

    [Fact]
    public void YearFracMatchesTheLibraryConventions()
    {
        // 30/360 bond basis: 31 Jan to 31 Mar is two months, 60 days.
        Assert.Equal(60.0 / 360.0, T.Num(_qf.YearFrac(T.Serial(2024, 1, 31), T.Serial(2024, 3, 31), "30/360")), 14);
        // 30E/360 ISDA and Actual/Actual ISDA across a leap year boundary (one day in 2023, 31+... in 2024).
        var start = new DateOnly(2023, 11, 15);
        var end = new DateOnly(2024, 4, 15);
        Assert.Equal(DayCounter.ActualActualIsda.YearFraction(start, end),
            T.Num(_qf.YearFrac(ExcelDates.ToSerial(start), ExcelDates.ToSerial(end), "Actual/Actual (ISDA)")));
        Assert.Equal(DayCounter.Thirty360EIsda.YearFraction(start, end),
            T.Num(_qf.YearFrac(ExcelDates.ToSerial(start), ExcelDates.ToSerial(end), "30E/360 ISDA")));
        // Reversed dates give a negative fraction, like the library.
        Assert.Equal(-DayCounter.Actual360.YearFraction(start, end),
            T.Num(_qf.YearFrac(ExcelDates.ToSerial(end), ExcelDates.ToSerial(start), "ACT/360")));
    }

    [Fact]
    public void YearFracRejectsUnknownAndUnsupportedDayCounts()
    {
        Assert.Equal(XlError.Value, _qf.YearFrac(T.Serial(2024, 1, 1), T.Serial(2025, 1, 1), "ACT/400"));
        Assert.Equal(XlError.Value, _qf.YearFrac(T.Serial(2024, 1, 1), T.Serial(2025, 1, 1), "ACT/ACT ICMA"));
        Assert.Contains("ICMA", ErrorLog.Last);
        Assert.Equal(XlError.Value, _qf.YearFrac(T.Serial(2024, 1, 1), 60.0, "ACT/360"));
    }
}
