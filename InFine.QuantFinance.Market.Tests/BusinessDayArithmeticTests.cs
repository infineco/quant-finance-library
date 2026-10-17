using System;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

public class BusinessDayArithmeticTests
{
    [Theory]
    [InlineData("2025-11-26", 1, "2025-11-28")]   // Thanksgiving skipped
    [InlineData("2025-11-28", -1, "2025-11-26")]
    [InlineData("2025-11-26", 2, "2025-12-01")]
    [InlineData("2025-12-24", 1, "2025-12-26")]   // Christmas skipped
    [InlineData("2025-11-27", 1, "2025-11-28")]   // start on a holiday
    [InlineData("2025-11-29", 1, "2025-12-01")]   // start on a Saturday
    [InlineData("2025-11-29", -1, "2025-11-28")]
    [InlineData("2025-11-29", 0, "2025-11-29")]   // zero leaves the date as is
    [InlineData("2025-03-03", 10, "2025-03-17")]
    [InlineData("2025-03-17", -10, "2025-03-03")]
    public void AdvanceNyse(string from, int n, string expected) =>
        Assert.Equal(D.Of(expected), NyseCalendar.Instance.Advance(D.Of(from), n));

    [Theory]
    [InlineData("2025-04-17", 1, "2025-04-22")]  // Good Friday and Easter Monday skipped
    [InlineData("2025-04-22", -1, "2025-04-17")]
    [InlineData("2025-04-30", 1, "2025-05-02")]  // 1 May
    [InlineData("2025-12-24", 1, "2025-12-29")]  // 25 and 26 Dec, weekend
    public void AdvanceTarget(string from, int n, string expected) =>
        Assert.Equal(D.Of(expected), TargetCalendar.Instance.Advance(D.Of(from), n));

    [Fact]
    public void BusinessDaysBetweenIsHalfOpenAndAntisymmetric()
    {
        var target = TargetCalendar.Instance;
        // 14,15,16,17 April count; 18 (Good Friday), 19-20 (weekend) and 21 (Easter Monday) do not.
        Assert.Equal(4, target.BusinessDaysBetween(D.Of("2025-04-14"), D.Of("2025-04-21")));
        Assert.Equal(4, target.BusinessDaysBetween(D.Of("2025-04-14"), D.Of("2025-04-22"))); // end excluded
        Assert.Equal(5, target.BusinessDaysBetween(D.Of("2025-04-14"), D.Of("2025-04-23")));
        Assert.Equal(-4, target.BusinessDaysBetween(D.Of("2025-04-21"), D.Of("2025-04-14")));
        Assert.Equal(0, target.BusinessDaysBetween(D.Of("2025-04-14"), D.Of("2025-04-14")));
        Assert.Equal(1, target.BusinessDaysBetween(D.Of("2025-04-14"), D.Of("2025-04-15")));
        // Starting on a holiday: the start does not count.
        Assert.Equal(0, target.BusinessDaysBetween(D.Of("2025-04-18"), D.Of("2025-04-22")));
    }

    [Fact]
    public void BusinessDaysBetweenOverAYearMatchesCalendarCount()
    {
        Assert.Equal(261, WeekendsOnlyCalendar.Instance.BusinessDaysBetween(D.Of("2025-01-01"), D.Of("2026-01-01")));
        // 261 weekdays - 6 TARGET closings (Wed 1 Jan, Fri 18 Apr, Mon 21 Apr, Thu 1 May, Thu 25 Dec, Fri 26 Dec).
        Assert.Equal(255, TargetCalendar.Instance.BusinessDaysBetween(D.Of("2025-01-01"), D.Of("2026-01-01")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(40)]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(-33)]
    public void AdvanceAndBusinessDaysBetweenAreConsistentFromBusinessDays(int n)
    {
        var joint = new JointCalendar(TargetCalendar.Instance, NyseCalendar.Instance, LondonCalendar.Instance);
        foreach (var calendar in new IHolidayCalendar[] { WeekendsOnlyCalendar.Instance, NyseCalendar.Instance, LondonCalendar.Instance, joint })
            for (var d = new DateOnly(2025, 1, 1); d.Year == 2025; d = d.AddDays(1))
            {
                if (!calendar.IsBusinessDay(d)) continue;
                var moved = calendar.Advance(d, n);
                Assert.True(calendar.IsBusinessDay(moved));
                Assert.Equal(n, calendar.BusinessDaysBetween(d, moved));
                Assert.Equal(d, calendar.Advance(moved, -n)); // round trip from a business day
            }
    }

    [Fact]
    public void NullCalendarIsRejected()
    {
        IHolidayCalendar calendar = null!;
        Assert.Throws<ArgumentNullException>(() => calendar.Advance(D.Of("2025-01-01"), 1));
        Assert.Throws<ArgumentNullException>(() => calendar.Adjust(D.Of("2025-01-01"), BusinessDayConvention.Following));
        Assert.Throws<ArgumentNullException>(() => calendar.BusinessDaysBetween(D.Of("2025-01-01"), D.Of("2025-01-02")));
    }
}
