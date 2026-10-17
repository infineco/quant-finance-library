using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

public class HolidayCalendarTests
{
    private static IEnumerable<DateOnly> AllDays(int year)
    {
        for (var d = new DateOnly(year, 1, 1); d.Year == year; d = d.AddDays(1)) yield return d;
    }

    private static string[] WeekdayHolidays(IHolidayCalendar calendar, int year) =>
        AllDays(year).Where(calendar.IsHoliday).Select(d => d.ToString("yyyy-MM-dd")).ToArray();

    private static string[] Expected(string[] reference, int year) =>
        reference.Select(D.Of).Where(d => d.Year == year && d.IsWeekday()).Select(d => d.ToString("yyyy-MM-dd")).ToArray();

    // Full-year comparison: any extra or missing closing day shows up in the diff.
    [Theory]
    [InlineData(2024)] [InlineData(2025)] [InlineData(2026)] [InlineData(2027)]
    [InlineData(2028)] [InlineData(2029)] [InlineData(2030)]
    public void TargetHolidaysMatchEcbRules(int year)
    {
        Assert.Equal(Expected(ReferenceData.Target, year), WeekdayHolidays(TargetCalendar.Instance, year));
        // 1 Jan, Good Friday, Easter Monday, 1 May, 25 Dec, 26 Dec: at most six, fewer when some fall on a weekend.
        Assert.InRange(WeekdayHolidays(TargetCalendar.Instance, year).Length, 2, 6);
    }

    [Theory]
    [InlineData(2024)] [InlineData(2025)] [InlineData(2026)] [InlineData(2027)]
    [InlineData(2028)] [InlineData(2029)] [InlineData(2030)]
    public void NyseHolidaysMatchPublishedCalendar(int year) =>
        Assert.Equal(Expected(ReferenceData.Nyse, year), WeekdayHolidays(NyseCalendar.Instance, year));

    [Theory]
    [InlineData(2024)] [InlineData(2025)] [InlineData(2026)] [InlineData(2027)]
    [InlineData(2028)] [InlineData(2029)] [InlineData(2030)]
    public void LondonHolidaysMatchGovUk(int year) =>
        Assert.Equal(Expected(ReferenceData.London, year), WeekdayHolidays(LondonCalendar.Instance, year));

    [Theory]
    [InlineData("2025-12-25", false)]
    [InlineData("2025-12-26", false)]
    [InlineData("2025-12-24", true)]
    [InlineData("2025-04-18", false)] // Good Friday
    [InlineData("2025-04-21", false)] // Easter Monday
    [InlineData("2025-05-01", false)]
    [InlineData("2025-05-02", true)]
    [InlineData("2025-05-03", false)] // Saturday
    [InlineData("2025-05-05", true)]  // early May bank holiday is UK-only
    [InlineData("2025-07-04", true)]  // US-only
    public void TargetBusinessDays(string iso, bool open) =>
        Assert.Equal(open, TargetCalendar.Instance.IsBusinessDay(D.Of(iso)));

    [Theory]
    [InlineData("1999-12-31", true)]
    [InlineData("2001-12-31", true)]
    [InlineData("2002-12-31", false)]
    [InlineData("1999-04-02", false)]  // Good Friday 1999: TARGET did not close yet
    [InlineData("2000-04-21", true)]   // Good Friday 2000: first year of the closing
    [InlineData("2000-05-01", true)]   // Monday
    [InlineData("1999-12-27", false)]  // Boxing day closing starts in 2000
    public void TargetOneOffAndStartYear(string iso, bool holiday) =>
        Assert.Equal(holiday, TargetCalendar.Instance.IsHoliday(D.Of(iso)));

    [Theory]
    // New Year's Day: Sunday -> Monday; Saturday -> no observance, Friday 31 Dec open.
    [InlineData("2023-01-02", true)]
    [InlineData("2022-01-03", false)] // 1 Jan 2022 Saturday: Monday is not a holiday
    [InlineData("2021-12-31", false)]
    [InlineData("2022-12-30", false)]
    [InlineData("2028-01-03", false)]
    [InlineData("2027-12-31", false)]
    // Independence Day: Saturday -> Friday, Sunday -> Monday.
    [InlineData("2020-07-03", true)]
    [InlineData("2020-07-04", false)] // Saturday itself
    [InlineData("2021-07-05", true)]
    [InlineData("2024-07-04", true)]
    // Christmas: Saturday -> Friday 24, Sunday -> Monday 26.
    [InlineData("2021-12-24", true)]
    [InlineData("2022-12-26", true)]
    [InlineData("2022-12-23", false)]
    // Juneteenth only from 2022, with observance.
    [InlineData("2021-06-18", false)]
    [InlineData("2022-06-20", true)]
    [InlineData("2023-06-19", true)]
    [InlineData("2027-06-18", true)]
    // Martin Luther King Jr. / Presidents Day / Memorial / Labor / Thanksgiving.
    [InlineData("2025-01-20", true)]
    [InlineData("2025-01-27", false)]
    [InlineData("2025-05-26", true)]
    [InlineData("2025-11-28", false)] // day after Thanksgiving: open (early close is out of scope)
    public void NyseObservanceEdgeCases(string iso, bool holiday) =>
        Assert.Equal(holiday, NyseCalendar.Instance.IsHoliday(D.Of(iso)));

    [Theory]
    // Christmas / Boxing day substitution.
    [InlineData("2021-12-27", true)]  // 25 Dec Saturday -> Mon 27
    [InlineData("2021-12-28", true)]  // 26 Dec Sunday -> Tue 28
    [InlineData("2022-12-26", true)]  // Boxing Day Monday
    [InlineData("2022-12-27", true)]  // 25 Dec Sunday -> Tue 27
    [InlineData("2020-12-28", true)]  // 26 Dec Saturday -> Mon 28
    [InlineData("2020-12-25", true)]
    [InlineData("2023-12-26", true)]
    [InlineData("2023-12-27", false)]
    // New Year's Day substitution.
    [InlineData("2022-01-03", true)]
    [InlineData("2023-01-02", true)]
    [InlineData("2022-01-04", false)]
    // May bank holidays, including the one-off moves.
    [InlineData("2025-05-05", true)]
    [InlineData("2025-05-26", true)]
    [InlineData("2020-05-04", false)] // normal Early May moved to VE Day anniversary
    [InlineData("2020-05-08", true)]
    [InlineData("2022-05-02", true)]
    [InlineData("2022-05-30", false)] // Spring bank holiday moved
    [InlineData("2022-06-02", true)]
    [InlineData("2022-06-03", true)]
    [InlineData("2022-09-19", true)]  // State funeral
    [InlineData("2023-05-01", true)]
    [InlineData("2023-05-08", true)]  // Coronation
    [InlineData("2023-05-29", true)]
    [InlineData("2024-08-26", true)]  // Summer bank holiday
    [InlineData("2024-08-05", false)] // Scotland only
    [InlineData("2024-07-04", false)]
    public void LondonSubstitutionsAndOneOffs(string iso, bool holiday) =>
        Assert.Equal(holiday, LondonCalendar.Instance.IsHoliday(D.Of(iso)));

    [Fact]
    public void WeekendsAreNeverHolidaysButNeverBusinessDays()
    {
        var calendars = new IHolidayCalendar[]
        {
            WeekendsOnlyCalendar.Instance, TargetCalendar.Instance, NyseCalendar.Instance, LondonCalendar.Instance,
            new JointCalendar(TargetCalendar.Instance, NyseCalendar.Instance),
        };
        foreach (var calendar in calendars)
            foreach (var day in AllDays(2027).Where(d => d.IsWeekend()))
            {
                Assert.False(calendar.IsHoliday(day), $"{calendar} {day}");
                Assert.False(calendar.IsBusinessDay(day), $"{calendar} {day}");
            }
    }

    [Fact]
    public void BusinessDayIsExactlyWeekdayNotHoliday()
    {
        foreach (var calendar in new IHolidayCalendar[] { TargetCalendar.Instance, NyseCalendar.Instance, LondonCalendar.Instance })
            foreach (var day in AllDays(2026))
                Assert.Equal(day.IsWeekday() && !calendar.IsHoliday(day), calendar.IsBusinessDay(day));
    }

    [Fact]
    public void WeekendsOnlyHasNoHolidays()
    {
        Assert.DoesNotContain(AllDays(2025), WeekendsOnlyCalendar.Instance.IsHoliday);
        Assert.Equal(261, AllDays(2025).Count(WeekendsOnlyCalendar.Instance.IsBusinessDay));
    }

    [Fact]
    public void NumberOfNyseTradingDaysIn2025IsKnown()
    {
        // 261 weekdays - 10 regular closings (the national day of mourning of 9 Jan 2025 is a special closure, out of scope).
        Assert.Equal(251, AllDays(2025).Count(NyseCalendar.Instance.IsBusinessDay));
    }

    [Fact]
    public void CalendarsHaveNames()
    {
        Assert.Equal("TARGET", TargetCalendar.Instance.Name);
        Assert.Equal("NYSE", NyseCalendar.Instance.ToString());
        Assert.False(string.IsNullOrWhiteSpace(LondonCalendar.Instance.Name));
        Assert.False(string.IsNullOrWhiteSpace(WeekendsOnlyCalendar.Instance.Name));
    }

    [Fact]
    public void JointCalendarIsUnionOfHolidays()
    {
        var joint = new JointCalendar(TargetCalendar.Instance, NyseCalendar.Instance, LondonCalendar.Instance);
        foreach (var day in AllDays(2025).Concat(AllDays(2026)))
        {
            var anyHoliday = TargetCalendar.Instance.IsHoliday(day) || NyseCalendar.Instance.IsHoliday(day) || LondonCalendar.Instance.IsHoliday(day);
            Assert.Equal(anyHoliday, joint.IsHoliday(day));
            Assert.Equal(day.IsWeekday() && !anyHoliday, joint.IsBusinessDay(day));
        }

        Assert.False(joint.IsBusinessDay(D.Of("2025-07-04")));  // NYSE only
        Assert.False(joint.IsBusinessDay(D.Of("2025-05-05")));  // London only
        Assert.False(joint.IsBusinessDay(D.Of("2025-05-01")));  // TARGET only
        Assert.True(joint.IsBusinessDay(D.Of("2025-07-07")));
        Assert.Equal("TARGET + NYSE + London", joint.Name);
        Assert.Equal(3, joint.Calendars.Count);
    }

    [Fact]
    public void JointCalendarArgumentValidation()
    {
        Assert.Throws<ArgumentException>(() => new JointCalendar());
        Assert.Throws<ArgumentException>(() => new JointCalendar(TargetCalendar.Instance, null!));
        Assert.Throws<ArgumentNullException>(() => new JointCalendar((IEnumerable<IHolidayCalendar>)null!));
    }

    [Fact]
    public void JointOfOneCalendarBehavesLikeThatCalendar()
    {
        var joint = new JointCalendar(NyseCalendar.Instance);
        foreach (var day in AllDays(2026))
        {
            Assert.Equal(NyseCalendar.Instance.IsHoliday(day), joint.IsHoliday(day));
            Assert.Equal(NyseCalendar.Instance.IsBusinessDay(day), joint.IsBusinessDay(day));
        }
    }
}
