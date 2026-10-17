using System;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

public class DateTests
{
    [Theory]
    [InlineData("2025-06-14", true)]  // Saturday
    [InlineData("2025-06-15", true)]  // Sunday
    [InlineData("2025-06-16", false)] // Monday
    [InlineData("2025-06-20", false)] // Friday
    public void WeekendAndWeekday(string iso, bool weekend)
    {
        var date = D.Of(iso);
        Assert.Equal(weekend, date.IsWeekend());
        Assert.Equal(!weekend, date.IsWeekday());
    }

    [Theory]
    [InlineData("2024-02-28", false, "2024-02-29")]
    [InlineData("2024-02-29", true, "2024-02-29")]
    [InlineData("2025-02-28", true, "2025-02-28")]
    [InlineData("2025-04-30", true, "2025-04-30")]
    [InlineData("2025-12-31", true, "2025-12-31")]
    [InlineData("2025-12-15", false, "2025-12-31")]
    public void EndOfMonth(string iso, bool isEom, string eom)
    {
        var date = D.Of(iso);
        Assert.Equal(isEom, date.IsEndOfMonth());
        Assert.Equal(D.Of(eom), date.EndOfMonth());
    }

    [Theory]
    // Last day of a 30-day month + 2 months: EOM rule gives 31 Aug, plain AddMonths gives 30 Aug.
    [InlineData("2025-06-30", 2, true, "2025-08-31")]
    [InlineData("2025-06-30", 2, false, "2025-08-30")]
    // Non-EOM start is never pushed to month end.
    [InlineData("2025-06-29", 2, true, "2025-08-29")]
    // Clamping into a shorter month, with and without the rule.
    [InlineData("2025-01-31", 1, true, "2025-02-28")]
    [InlineData("2025-01-31", 1, false, "2025-02-28")]
    [InlineData("2024-01-31", 1, true, "2024-02-29")]
    [InlineData("2025-01-30", 1, true, "2025-02-28")]
    // February month end moving to a 31-day month only with the rule.
    [InlineData("2025-02-28", 3, true, "2025-05-31")]
    [InlineData("2025-02-28", 3, false, "2025-05-28")]
    // Negative shifts, year rollover.
    [InlineData("2025-03-31", -1, true, "2025-02-28")]
    [InlineData("2025-02-28", -2, true, "2024-12-31")]
    [InlineData("2025-02-28", -2, false, "2024-12-28")]
    [InlineData("2025-05-17", 0, true, "2025-05-17")]
    public void AddMonthsWithEndOfMonthRule(string start, int months, bool eom, string expected) =>
        Assert.Equal(D.Of(expected), D.Of(start).AddMonths(months, eom));

    [Theory]
    [InlineData(2025, 1, DayOfWeek.Monday, 3, "2025-01-20")]   // MLK day 2025
    [InlineData(2025, 11, DayOfWeek.Thursday, 4, "2025-11-27")] // Thanksgiving 2025
    [InlineData(2025, 9, DayOfWeek.Monday, 1, "2025-09-01")]   // first day of month is the weekday
    [InlineData(2025, 2, DayOfWeek.Friday, 4, "2025-02-28")]   // last possible, February
    [InlineData(2024, 2, DayOfWeek.Thursday, 5, "2024-02-29")] // fifth occurrence in a leap February
    public void NthWeekday(int year, int month, DayOfWeek dow, int n, string expected) =>
        Assert.Equal(D.Of(expected), DateRules.NthWeekday(year, month, dow, n));

    [Fact]
    public void NthWeekdayRejectsOccurrencesThatDoNotExist()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DateRules.NthWeekday(2025, 2, DayOfWeek.Monday, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => DateRules.NthWeekday(2025, 2, DayOfWeek.Monday, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DateRules.NthWeekday(2025, 2, DayOfWeek.Monday, 6));
    }

    [Theory]
    [InlineData(2025, 5, DayOfWeek.Monday, "2025-05-26")] // Memorial Day 2025
    [InlineData(2025, 8, DayOfWeek.Monday, "2025-08-25")]
    [InlineData(2025, 8, DayOfWeek.Sunday, "2025-08-31")] // last day of month is the weekday
    [InlineData(2024, 2, DayOfWeek.Thursday, "2024-02-29")]
    public void LastWeekday(int year, int month, DayOfWeek dow, string expected) =>
        Assert.Equal(D.Of(expected), DateRules.LastWeekday(year, month, dow));

    [Theory]
    // Published IMM dates: third Wednesday of Mar/Jun/Sep/Dec.
    [InlineData(2024, 3, "2024-03-20")]
    [InlineData(2024, 6, "2024-06-19")]
    [InlineData(2024, 9, "2024-09-18")]
    [InlineData(2024, 12, "2024-12-18")]
    [InlineData(2025, 3, "2025-03-19")]
    [InlineData(2025, 6, "2025-06-18")]
    [InlineData(2025, 9, "2025-09-17")]
    [InlineData(2025, 12, "2025-12-17")]
    [InlineData(2026, 3, "2026-03-18")]
    public void ImmDate(int year, int month, string expected)
    {
        var date = DateRules.ImmDate(year, month);
        Assert.Equal(D.Of(expected), date);
        Assert.Equal(DayOfWeek.Wednesday, date.DayOfWeek);
        Assert.True(DateRules.IsImmDate(date));
        Assert.False(DateRules.IsImmDate(date.AddDays(1)));
    }

    [Fact]
    public void ImmDateRejectsNonImmMonths() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DateRules.ImmDate(2025, 4));

    [Theory]
    [InlineData("2025-01-01", "2025-03-19")]
    [InlineData("2025-03-18", "2025-03-19")]
    [InlineData("2025-03-19", "2025-06-18")] // strictly after
    [InlineData("2025-03-20", "2025-06-18")]
    [InlineData("2025-11-05", "2025-12-17")]
    [InlineData("2025-12-17", "2026-03-18")]
    [InlineData("2025-12-31", "2026-03-18")]
    public void NextImmDate(string from, string expected) =>
        Assert.Equal(D.Of(expected), DateRules.NextImmDate(D.Of(from)));

    [Fact]
    public void EasterMatchesPublishedDates2000To2035()
    {
        Assert.Equal(36, ReferenceData.Easter2000To2035.Length);
        for (var i = 0; i < ReferenceData.Easter2000To2035.Length; i++)
            Assert.Equal(D.Of(ReferenceData.Easter2000To2035[i]), DateRules.EasterSunday(2000 + i));
    }

    [Theory]
    [InlineData(1818, "1818-03-22")] // earliest possible Easter
    [InlineData(1943, "1943-04-25")] // latest possible Easter
    [InlineData(1900, "1900-04-15")]
    [InlineData(2100, "2100-03-28")]
    public void EasterExtremes(int year, string expected)
    {
        var easter = DateRules.EasterSunday(year);
        Assert.Equal(D.Of(expected), easter);
        Assert.Equal(DayOfWeek.Sunday, easter.DayOfWeek);
    }

    [Fact]
    public void EasterIsAlwaysASundayInTheAllowedWindow()
    {
        for (var year = 1583; year <= 3000; year++)
        {
            var easter = DateRules.EasterSunday(year);
            Assert.Equal(DayOfWeek.Sunday, easter.DayOfWeek);
            Assert.InRange(easter, new DateOnly(year, 3, 22), new DateOnly(year, 4, 25));
        }
    }

    [Fact]
    public void EasterRejectsPreGregorianYears() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DateRules.EasterSunday(1582));
}
