using System;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

public class BusinessDayConventionTests
{
    private static readonly IHolidayCalendar Weekends = WeekendsOnlyCalendar.Instance;

    private static DateOnly Adjust(IHolidayCalendar calendar, string iso, BusinessDayConvention convention) =>
        calendar.Adjust(D.Of(iso), convention);

    [Theory]
    [InlineData(BusinessDayConvention.Unadjusted, "2025-08-30")]
    [InlineData(BusinessDayConvention.Following, "2025-09-01")]
    [InlineData(BusinessDayConvention.ModifiedFollowing, "2025-08-29")] // would cross into September
    [InlineData(BusinessDayConvention.Preceding, "2025-08-29")]
    [InlineData(BusinessDayConvention.ModifiedPreceding, "2025-08-29")]
    public void SaturdayEndOfAugust(BusinessDayConvention convention, string expected) =>
        Assert.Equal(D.Of(expected), Adjust(Weekends, "2025-08-30", convention));

    [Theory]
    [InlineData(BusinessDayConvention.Unadjusted, "2025-06-01")]
    [InlineData(BusinessDayConvention.Following, "2025-06-02")]
    [InlineData(BusinessDayConvention.ModifiedFollowing, "2025-06-02")]
    [InlineData(BusinessDayConvention.Preceding, "2025-05-30")]
    [InlineData(BusinessDayConvention.ModifiedPreceding, "2025-06-02")] // would cross into May
    public void SundayFirstOfJune(BusinessDayConvention convention, string expected) =>
        Assert.Equal(D.Of(expected), Adjust(Weekends, "2025-06-01", convention));

    [Theory]
    [InlineData(BusinessDayConvention.Following, "2025-05-02")]
    [InlineData(BusinessDayConvention.ModifiedFollowing, "2025-05-02")]
    [InlineData(BusinessDayConvention.Preceding, "2025-04-30")]
    [InlineData(BusinessDayConvention.ModifiedPreceding, "2025-05-02")] // 1 May TARGET holiday, previous day is April
    public void TargetLabourDay(BusinessDayConvention convention, string expected) =>
        Assert.Equal(D.Of(expected), Adjust(TargetCalendar.Instance, "2025-05-01", convention));

    [Theory]
    // Thursday 30 and Friday 31 October closed, then the weekend: Following lands on Monday 3 November (next month).
    [InlineData("2025-10-30", BusinessDayConvention.Following, "2025-11-03")]
    [InlineData("2025-10-30", BusinessDayConvention.ModifiedFollowing, "2025-10-29")]
    [InlineData("2025-10-31", BusinessDayConvention.Following, "2025-11-03")]
    [InlineData("2025-10-31", BusinessDayConvention.ModifiedFollowing, "2025-10-29")]
    [InlineData("2025-10-31", BusinessDayConvention.Preceding, "2025-10-29")]
    [InlineData("2025-10-31", BusinessDayConvention.ModifiedPreceding, "2025-10-29")]
    public void ClosedMonthEnd(string iso, BusinessDayConvention convention, string expected)
    {
        var calendar = new FixedClosuresCalendar(D.Of("2025-10-30"), D.Of("2025-10-31"));
        Assert.Equal(D.Of(expected), calendar.Adjust(D.Of(iso), convention));
    }

    [Theory]
    // Monday 3 and Tuesday 4 November closed: Preceding goes back over the weekend to Friday 31 October.
    [InlineData("2025-11-03", BusinessDayConvention.Preceding, "2025-10-31")]
    [InlineData("2025-11-03", BusinessDayConvention.ModifiedPreceding, "2025-11-05")] // previous business day is in October
    [InlineData("2025-11-04", BusinessDayConvention.ModifiedPreceding, "2025-11-05")]
    [InlineData("2025-11-03", BusinessDayConvention.Following, "2025-11-05")]
    [InlineData("2025-11-03", BusinessDayConvention.ModifiedFollowing, "2025-11-05")]
    public void ClosedMonthStart(string iso, BusinessDayConvention convention, string expected)
    {
        var calendar = new FixedClosuresCalendar(D.Of("2025-11-03"), D.Of("2025-11-04"));
        Assert.Equal(D.Of(expected), calendar.Adjust(D.Of(iso), convention));
    }

    [Theory]
    [InlineData(BusinessDayConvention.Unadjusted)]
    [InlineData(BusinessDayConvention.Following)]
    [InlineData(BusinessDayConvention.ModifiedFollowing)]
    [InlineData(BusinessDayConvention.Preceding)]
    [InlineData(BusinessDayConvention.ModifiedPreceding)]
    public void BusinessDaysAreFixedPoints(BusinessDayConvention convention) =>
        Assert.Equal(D.Of("2025-08-29"), Adjust(Weekends, "2025-08-29", convention));

    [Fact]
    public void ModifiedConventionsNeverLeaveTheMonthOverAYear()
    {
        foreach (var calendar in new IHolidayCalendar[] { TargetCalendar.Instance, NyseCalendar.Instance, LondonCalendar.Instance })
            for (var d = new DateOnly(2025, 1, 1); d.Year == 2025; d = d.AddDays(1))
            {
                var mf = calendar.Adjust(d, BusinessDayConvention.ModifiedFollowing);
                var mp = calendar.Adjust(d, BusinessDayConvention.ModifiedPreceding);
                Assert.True(calendar.IsBusinessDay(mf));
                Assert.True(calendar.IsBusinessDay(mp));
                Assert.Equal(d.Month, mf.Month);
                Assert.Equal(d.Month, mp.Month);
                Assert.True(calendar.Adjust(d, BusinessDayConvention.Following) >= d);
                Assert.True(calendar.Adjust(d, BusinessDayConvention.Preceding) <= d);
            }
    }

    [Fact]
    public void UnknownConventionIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Weekends.Adjust(D.Of("2025-01-01"), (BusinessDayConvention)99));

    [Fact]
    public void CalendarWithNoBusinessDayFailsInsteadOfLooping()
    {
        var closed = new AlwaysClosed();
        Assert.Throws<InvalidOperationException>(() => closed.Adjust(D.Of("2025-01-01"), BusinessDayConvention.Following));
    }

    private sealed class AlwaysClosed : IHolidayCalendar
    {
        public string Name => "Always closed";
        public bool IsHoliday(DateOnly date) => date.IsWeekday();
        public bool IsBusinessDay(DateOnly date) => false;
    }
}
