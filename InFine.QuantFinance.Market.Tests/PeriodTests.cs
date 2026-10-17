using System;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

public class PeriodTests
{
    [Theory]
    [InlineData("3M", 3, PeriodUnit.Months)]
    [InlineData("1Y", 1, PeriodUnit.Years)]
    [InlineData("2W", 2, PeriodUnit.Weeks)]
    [InlineData("10D", 10, PeriodUnit.Days)]
    [InlineData("3m", 3, PeriodUnit.Months)]
    [InlineData("  6M ", 6, PeriodUnit.Months)]
    [InlineData("-1Y", -1, PeriodUnit.Years)]
    [InlineData("+2W", 2, PeriodUnit.Weeks)]
    [InlineData("0D", 0, PeriodUnit.Days)]
    [InlineData("12M", 12, PeriodUnit.Months)]
    public void ParsesTenors(string text, int length, PeriodUnit unit)
    {
        var period = Period.Parse(text);
        Assert.Equal(length, period.Length);
        Assert.Equal(unit, period.Unit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("M")]
    [InlineData("3")]
    [InlineData("3X")]
    [InlineData("1.5Y")]
    [InlineData("abc")]
    [InlineData("3 M")]
    [InlineData("M3")]
    public void RejectsMalformedTenors(string text)
    {
        Assert.False(Period.TryParse(text, out _));
        Assert.Throws<FormatException>(() => Period.Parse(text));
    }

    [Fact]
    public void TryParseRejectsNull() => Assert.False(Period.TryParse(null, out _));

    [Theory]
    [InlineData("3M")]
    [InlineData("1Y")]
    [InlineData("2W")]
    [InlineData("10D")]
    [InlineData("-18M")]
    public void ToStringRoundTrips(string text) => Assert.Equal(text, Period.Parse(text).ToString());

    [Fact]
    public void EqualityIsStructural()
    {
        Assert.Equal(Period.Parse("3M"), new Period(3, PeriodUnit.Months));
        Assert.True(Period.Parse("3M") == Period.Parse("3m"));
        Assert.True(Period.Parse("12M") != Period.Parse("1Y")); // same effect on a date, different periods
        Assert.NotEqual(Period.Parse("1W"), Period.Parse("7D"));
    }

    [Theory]
    [InlineData("2025-01-31", "1M", false, "2025-02-28")]  // day clamped to the length of February
    [InlineData("2024-01-31", "1M", false, "2024-02-29")]  // leap year
    [InlineData("2025-02-28", "1M", false, "2025-03-28")]  // no EOM rule: day number kept
    [InlineData("2025-02-28", "1M", true, "2025-03-31")]   // EOM rule: month end to month end
    [InlineData("2025-06-30", "2M", true, "2025-08-31")]
    [InlineData("2025-06-30", "2M", false, "2025-08-30")]
    [InlineData("2025-06-15", "2M", true, "2025-08-15")]   // not a month end: EOM rule inert
    [InlineData("2024-02-29", "1Y", false, "2025-02-28")]
    [InlineData("2025-06-30", "1Y", true, "2026-06-30")]
    [InlineData("2025-03-15", "18M", false, "2026-09-15")]
    [InlineData("2025-05-31", "-3M", false, "2025-02-28")]
    [InlineData("2025-03-03", "2W", false, "2025-03-17")]
    [InlineData("2025-02-25", "10D", false, "2025-03-07")]
    [InlineData("2025-02-28", "1D", true, "2025-03-01")]    // EOM flag ignored for days
    [InlineData("2025-03-17", "-2W", false, "2025-03-03")]
    [InlineData("2025-03-17", "0D", false, "2025-03-17")]
    public void AddsCalendarDaysAndMonths(string from, string tenor, bool eom, string expected) =>
        Assert.Equal(D.Of(expected), Period.Parse(tenor).AddTo(D.Of(from), eom));

    [Fact]
    public void AddToWithCalendarAdjustsTheResult()
    {
        var threeMonths = Period.Parse("3M");
        var from = D.Of("2025-05-31");   // + 3M = Sunday 31 August 2025
        var target = TargetCalendar.Instance;
        Assert.Equal(D.Of("2025-08-31"), threeMonths.AddTo(from, null, BusinessDayConvention.Following));   // no calendar: raw result
        Assert.Equal(D.Of("2025-09-01"), threeMonths.AddTo(from, target, BusinessDayConvention.Following));
        Assert.Equal(D.Of("2025-08-29"), threeMonths.AddTo(from, target, BusinessDayConvention.ModifiedFollowing)); // 1 Sep is next month
        Assert.Equal(D.Of("2025-08-29"), threeMonths.AddTo(from, target, BusinessDayConvention.Preceding));
        // 1 May 2025 is a TARGET holiday: 30 Apr + 1D = Thursday 1 May -> Friday 2 May.
        Assert.Equal(D.Of("2025-05-02"), Period.Parse("1D").AddTo(D.Of("2025-04-30"), target, BusinessDayConvention.Following));
    }

    [Theory]
    [InlineData("1Y", true, Frequency.Annual)]
    [InlineData("12M", true, Frequency.Annual)]
    [InlineData("6M", true, Frequency.Semiannual)]
    [InlineData("4M", true, Frequency.EveryFourthMonth)]
    [InlineData("3M", true, Frequency.Quarterly)]
    [InlineData("2M", true, Frequency.Bimonthly)]
    [InlineData("1M", true, Frequency.Monthly)]
    [InlineData("5M", false, Frequency.Once)]
    [InlineData("24M", false, Frequency.Once)]
    [InlineData("0M", false, Frequency.Once)]
    [InlineData("2W", false, Frequency.Once)]
    [InlineData("-3M", false, Frequency.Once)]
    public void ConvertsToFrequency(string tenor, bool ok, Frequency expected)
    {
        Assert.Equal(ok, Period.Parse(tenor).TryGetFrequency(out var frequency));
        Assert.Equal(expected, frequency);
    }

    [Fact]
    public void FrequencyToPeriod()
    {
        Assert.Equal(Period.Parse("12M"), Frequency.Annual.ToPeriod());
        Assert.Equal(Period.Parse("6M"), Frequency.Semiannual.ToPeriod());
        Assert.Equal(Period.Parse("3M"), Frequency.Quarterly.ToPeriod());
        Assert.Equal(Period.Parse("1M"), Frequency.Monthly.ToPeriod());
        Assert.Equal(4, Frequency.Quarterly.PeriodsPerYear());
        Assert.Equal(0, Frequency.Once.PeriodsPerYear());
        Assert.Throws<ArgumentException>(() => Frequency.Once.ToPeriod());
    }

    [Fact]
    public void NegateAndMultiply()
    {
        Assert.Equal(Period.Parse("-3M"), Period.Parse("3M").Negate());
        Assert.Equal(Period.Parse("18M"), Period.Parse("3M").Multiply(6));
        Assert.Equal(12, Period.Parse("1Y").TotalMonths);
        Assert.Null(Period.Parse("2W").TotalMonths);
    }
}
