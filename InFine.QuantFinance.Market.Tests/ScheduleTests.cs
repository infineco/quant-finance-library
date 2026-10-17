using System;
using System.Linq;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

/// <summary>
/// Expected schedules are written out from the generation rules (see the comment on each test); the adjusted dates
/// and accrual day counts of the 5Y swap were also reproduced with an independent Python implementation
/// (own TARGET rule, own month arithmetic).
/// </summary>
public class ScheduleTests
{
    private const double Tol = 1e-12;

    private static DateOnly[] Dates(params string[] iso) => iso.Select(D.Of).ToArray();

    private static ScheduleBuilder Unadjusted(string effective, string termination, Frequency frequency) =>
        new ScheduleBuilder(D.Of(effective), D.Of(termination))
            .WithFrequency(frequency)
            .WithConvention(BusinessDayConvention.Unadjusted);

    // --- 5Y semiannual swap on TARGET ------------------------------------------------------------------------------

    private static Schedule FiveYearSwap() =>
        new ScheduleBuilder(D.Of("2024-03-18"), D.Of("2029-03-18"))
            .WithFrequency(Frequency.Semiannual)
            .WithCalendar(TargetCalendar.Instance)
            .WithConvention(BusinessDayConvention.ModifiedFollowing)
            .Build();

    [Fact]
    public void FiveYearSemiannualSwapBackwardModifiedFollowing()
    {
        var schedule = FiveYearSwap();
        // Backward from 18 Mar 2029 in 6 months: every 18 Mar / 18 Sep, which lands exactly on the effective date: no stub.
        Assert.Equal(Dates("2024-03-18", "2024-09-18", "2025-03-18", "2025-09-18", "2026-03-18", "2026-09-18",
            "2027-03-18", "2027-09-18", "2028-03-18", "2028-09-18", "2029-03-18"), schedule.UnadjustedDates);
        // Weekdays: 18 Sep 2027 is a Saturday -> Mon 20 Sep; 18 Mar 2028 Saturday -> Mon 20 Mar; 18 Mar 2029 Sunday -> Mon 19 Mar
        // (the next business day is still in March). No other date is a weekend or a TARGET holiday.
        Assert.Equal(Dates("2024-03-18", "2024-09-18", "2025-03-18", "2025-09-18", "2026-03-18", "2026-09-18",
            "2027-03-18", "2027-09-20", "2028-03-20", "2028-09-18", "2029-03-19"), schedule.AdjustedDates);
        Assert.Equal(11, schedule.Count);
        Assert.Equal(10, schedule.PeriodCount);
        Assert.All(Enumerable.Range(0, 10), i => Assert.True(schedule.IsRegular(i)));
    }

    [Fact]
    public void FiveYearSwapAccrualFractions()
    {
        var periods = FiveYearSwap().GetAccrualPeriods(DayCounter.Actual360);
        // Actual days between adjusted dates: 184 181 184 181 184 181 186 182 182 182 (e.g. 18 Mar -> 18 Sep 2024 = 31+30+31+30+31+31 = 184, counting month by month from the 18th).
        var days = new[] { 184, 181, 184, 181, 184, 181, 186, 182, 182, 182 };
        Assert.Equal(10, periods.Count);
        for (var i = 0; i < 10; i++)
            Assert.Equal(days[i] / 360.0, periods[i].YearFraction, Tol);
        Assert.Equal(D.Of("2027-09-18"), periods[7].UnadjustedStart);
        Assert.Equal(D.Of("2028-03-18"), periods[7].UnadjustedEnd);
        Assert.Equal(D.Of("2027-09-20"), periods[7].Start);
        Assert.Equal(D.Of("2028-03-20"), periods[7].End);
        // Accruing on the unadjusted dates instead: 18 Mar 2027 -> 18 Sep 2027 is 184 days, not 186.
        var unadjusted = FiveYearSwap().GetAccrualPeriods(DayCounter.Actual360, useAdjustedDates: false);
        Assert.Equal(184 / 360.0, unadjusted[6].YearFraction, Tol);
        Assert.Equal(186 / 360.0, periods[6].YearFraction, Tol);
        // Adjacent periods share their boundary dates.
        for (var i = 1; i < periods.Count; i++)
            Assert.Equal(periods[i - 1].End, periods[i].Start);
    }

    [Fact]
    public void FiveYearSwapIcmaOnAdjustedDatesIsExactlyHalfAYear()
    {
        // Each period is its own reference period (adjusted like the accrual dates): 1 / (2 x 1)... exactly 0.5.
        var periods = FiveYearSwap().GetAccrualPeriods(DayCounter.ActualActualIcma);
        Assert.All(periods, p => Assert.Equal(0.5, p.YearFraction, Tol));
        var unadjusted = FiveYearSwap().GetAccrualPeriods(DayCounter.ActualActualIcma, useAdjustedDates: false);
        Assert.All(unadjusted, p => Assert.Equal(0.5, p.YearFraction, Tol));
    }

    [Fact]
    public void HolidaysAndWeekendsOnAnnualDates()
    {
        // 1 May 2024 (Wed), 2025 (Thu) and 2026 (Fri) are TARGET holidays.
        // Following: 2024 -> Thu 2 May; 2025 -> Fri 2 May; 2026 -> Sat 2 May is a weekend, so Mon 4 May.
        var schedule = new ScheduleBuilder(D.Of("2024-05-01"), D.Of("2026-05-01"))
            .WithFrequency(Frequency.Annual)
            .WithCalendar(TargetCalendar.Instance)
            .WithConvention(BusinessDayConvention.Following)
            .Build();
        Assert.Equal(Dates("2024-05-01", "2025-05-01", "2026-05-01"), schedule.UnadjustedDates);
        Assert.Equal(Dates("2024-05-02", "2025-05-02", "2026-05-04"), schedule.AdjustedDates);

        // 25 Dec 2025 (Thu) and 25 Dec 2026 (Fri) are TARGET holidays, 26 Dec too, then weekends:
        // ModifiedFollowing -> Mon 29 Dec 2025 and Mon 28 Dec 2026 (both still in December).
        var christmas = new ScheduleBuilder(D.Of("2025-06-25"), D.Of("2027-06-25"))
            .WithFrequency(Frequency.Semiannual)
            .WithCalendar(TargetCalendar.Instance)
            .Build();
        Assert.Equal(Dates("2025-06-25", "2025-12-25", "2026-06-25", "2026-12-25", "2027-06-25"), christmas.UnadjustedDates);
        Assert.Equal(Dates("2025-06-25", "2025-12-29", "2026-06-25", "2026-12-28", "2027-06-25"), christmas.AdjustedDates);
    }

    [Fact]
    public void TerminationConventionCanDifferFromTheOthers()
    {
        // Same 5Y swap, termination left unadjusted: 18 Mar 2029 (Sunday) stays, the other weekend dates still roll.
        var schedule = new ScheduleBuilder(D.Of("2024-03-18"), D.Of("2029-03-18"))
            .WithFrequency(Frequency.Semiannual)
            .WithCalendar(TargetCalendar.Instance)
            .WithTerminationConvention(BusinessDayConvention.Unadjusted)
            .Build();
        Assert.Equal(D.Of("2029-03-18"), schedule.AdjustedDates[^1]);
        Assert.Equal(D.Of("2028-03-20"), schedule.AdjustedDates[^3]);
        Assert.Equal(BusinessDayConvention.Unadjusted, schedule.TerminationConvention);
        Assert.Equal(BusinessDayConvention.ModifiedFollowing, schedule.Convention);
    }

    // --- Stubs -----------------------------------------------------------------------------------------------------

    // Effective 1 Mar 2025, termination 15 Jan 2027, semiannual: not a whole number of tenors.
    private const string StubEffective = "2025-03-01";
    private const string StubTermination = "2027-01-15";

    [Fact]
    public void BackwardShortFrontStub()
    {
        // Backward from 15 Jan 2027: 15 Jul 2026, 15 Jan 2026, 15 Jul 2025, then 15 Jan 2025 < effective: the front stub is
        // 1 Mar -> 15 Jul 2025, shorter than 6 months.
        var schedule = Unadjusted(StubEffective, StubTermination, Frequency.Semiannual).Build();
        Assert.Equal(Dates("2025-03-01", "2025-07-15", "2026-01-15", "2026-07-15", "2027-01-15"), schedule.UnadjustedDates);
        Assert.False(schedule.IsRegular(0));
        Assert.True(schedule.IsRegular(1));
        Assert.True(schedule.IsRegular(3));
    }

    [Fact]
    public void BackwardLongFrontStub()
    {
        // The date nearest the effective date (15 Jul 2025) is dropped: first period 1 Mar 2025 -> 15 Jan 2026 (320 days).
        var schedule = Unadjusted(StubEffective, StubTermination, Frequency.Semiannual).WithStub(StubPolicy.Long).Build();
        Assert.Equal(Dates("2025-03-01", "2026-01-15", "2026-07-15", "2027-01-15"), schedule.UnadjustedDates);
        Assert.False(schedule.IsRegular(0));
        Assert.Equal(320, schedule.UnadjustedDates[1].DayNumber - schedule.UnadjustedDates[0].DayNumber);
    }

    [Fact]
    public void ForwardShortBackStub()
    {
        // Forward from 1 Mar 2025: 1 Sep 2025, 1 Mar 2026, 1 Sep 2026, then 1 Mar 2027 > termination: back stub 1 Sep 2026 -> 15 Jan 2027.
        var schedule = Unadjusted(StubEffective, StubTermination, Frequency.Semiannual).WithRule(DateGenerationRule.Forward).Build();
        Assert.Equal(Dates("2025-03-01", "2025-09-01", "2026-03-01", "2026-09-01", "2027-01-15"), schedule.UnadjustedDates);
        Assert.True(schedule.IsRegular(0));
        Assert.False(schedule.IsRegular(3));
    }

    [Fact]
    public void ForwardLongBackStub()
    {
        // 1 Sep 2026 is dropped: last period 1 Mar 2026 -> 15 Jan 2027.
        var schedule = Unadjusted(StubEffective, StubTermination, Frequency.Semiannual)
            .WithRule(DateGenerationRule.Forward).WithStub(StubPolicy.Long).Build();
        Assert.Equal(Dates("2025-03-01", "2025-09-01", "2026-03-01", "2027-01-15"), schedule.UnadjustedDates);
        Assert.False(schedule.IsRegular(2));
    }

    [Theory]
    [InlineData(DateGenerationRule.Backward, StubPolicy.Short)]
    [InlineData(DateGenerationRule.Backward, StubPolicy.Long)]
    [InlineData(DateGenerationRule.Forward, StubPolicy.Short)]
    [InlineData(DateGenerationRule.Forward, StubPolicy.Long)]
    public void NoStubWhenTheSpanIsAWholeNumberOfTenors(DateGenerationRule rule, StubPolicy stub)
    {
        var schedule = Unadjusted("2025-01-15", "2027-01-15", Frequency.Semiannual).WithRule(rule).WithStub(stub).Build();
        Assert.Equal(Dates("2025-01-15", "2025-07-15", "2026-01-15", "2026-07-15", "2027-01-15"), schedule.UnadjustedDates);
        Assert.All(Enumerable.Range(0, 4), i => Assert.True(schedule.IsRegular(i)));
    }

    [Theory]
    [InlineData(StubPolicy.Short)]
    [InlineData(StubPolicy.Long)]
    public void TenorLongerThanTheSpanGivesASingleStubPeriod(StubPolicy stub)
    {
        var schedule = Unadjusted("2025-03-01", "2025-05-01", Frequency.Semiannual).WithStub(stub).Build();
        Assert.Equal(Dates("2025-03-01", "2025-05-01"), schedule.UnadjustedDates);
        Assert.False(schedule.IsRegular(0));
    }

    [Fact]
    public void ExplicitNextToLastDateFixesTheLastPeriod()
    {
        // Regular dates are generated backward from the next-to-last date 15 Jul 2026; the last period runs to 20 Dec 2026.
        var schedule = Unadjusted("2025-01-15", "2026-12-20", Frequency.Semiannual).WithNextToLastDate(D.Of("2026-07-15")).Build();
        Assert.Equal(Dates("2025-01-15", "2025-07-15", "2026-01-15", "2026-07-15", "2026-12-20"), schedule.UnadjustedDates);
        Assert.Equal(new[] { true, true, true, false }, Enumerable.Range(0, 4).Select(schedule.IsRegular).ToArray());
        // ICMA: the last period is 15 Jul -> 20 Dec 2026 = 158 days (15 Jul -> 15 Dec = 31+31+30+31+30 = 153, plus 5) of the
        // notional period 15 Jul 2026 -> 15 Jan 2027 (184 days): 158 / (2 x 184).
        var last = schedule.GetAccrualPeriods(DayCounter.ActualActualIcma)[3];
        Assert.Equal(158 / 368.0, last.YearFraction, Tol);
    }

    [Theory]
    [InlineData(DateGenerationRule.Forward)]
    [InlineData(DateGenerationRule.Backward)]
    public void ExplicitFirstDateFixesTheFirstPeriod(DateGenerationRule rule)
    {
        // First period 15 Jan -> 1 Apr 2025, then every 6 months from 1 Apr 2025.
        var schedule = Unadjusted("2025-01-15", "2026-04-01", Frequency.Semiannual).WithRule(rule).WithFirstDate(D.Of("2025-04-01")).Build();
        Assert.Equal(Dates("2025-01-15", "2025-04-01", "2025-10-01", "2026-04-01"), schedule.UnadjustedDates);
        Assert.Equal(new[] { false, true, true }, Enumerable.Range(0, 3).Select(schedule.IsRegular).ToArray());
        // ICMA front stub 15 Jan -> 1 Apr 2025 (76 days) against the notional period 1 Oct 2024 -> 1 Apr 2025 (182 days).
        Assert.Equal(76 / 364.0, schedule.GetAccrualPeriods(DayCounter.ActualActualIcma)[0].YearFraction, Tol);
    }

    [Fact]
    public void IcmaStubsFollowTheNotionalPeriodRules()
    {
        // Short front stub 1 Mar -> 15 Jul 2025: 136 days of the notional 15 Jan -> 15 Jul 2025 (181 days).
        var shortFront = Unadjusted(StubEffective, StubTermination, Frequency.Semiannual).Build().GetAccrualPeriods(DayCounter.ActualActualIcma);
        Assert.Equal(136 / 362.0, shortFront[0].YearFraction, Tol);
        Assert.All(shortFront.Skip(1), p => Assert.Equal(0.5, p.YearFraction, Tol));

        // Long front stub 1 Mar 2025 -> 15 Jan 2026: 136/362 in the previous notional period plus a whole regular one.
        var longFront = Unadjusted(StubEffective, StubTermination, Frequency.Semiannual).WithStub(StubPolicy.Long).Build()
            .GetAccrualPeriods(DayCounter.ActualActualIcma);
        Assert.Equal(136 / 362.0 + 0.5, longFront[0].YearFraction, Tol);

        // Short back stub 1 Sep 2026 -> 15 Jan 2027: 136 days of the notional 1 Sep 2026 -> 1 Mar 2027 (181 days).
        var shortBack = Unadjusted(StubEffective, StubTermination, Frequency.Semiannual).WithRule(DateGenerationRule.Forward).Build()
            .GetAccrualPeriods(DayCounter.ActualActualIcma);
        Assert.Equal(136 / 362.0, shortBack[3].YearFraction, Tol);

        // Long back stub 1 Mar 2026 -> 15 Jan 2027: 0.5 + 136/362.
        var longBack = Unadjusted(StubEffective, StubTermination, Frequency.Semiannual)
            .WithRule(DateGenerationRule.Forward).WithStub(StubPolicy.Long).Build().GetAccrualPeriods(DayCounter.ActualActualIcma);
        Assert.Equal(0.5 + 136 / 362.0, longBack[2].YearFraction, Tol);
    }

    // --- End of month ----------------------------------------------------------------------------------------------

    [Fact]
    public void EndOfMonthRuleBackwardFromAThirtyDayMonth()
    {
        // Backward quarterly from 30 Jun 2025, a month end. With the rule every date is a month end: 31 Mar, 31 Dec, 30 Sep.
        var withRule = Unadjusted("2024-06-30", "2025-06-30", Frequency.Quarterly).WithEndOfMonth().Build();
        Assert.Equal(Dates("2024-06-30", "2024-09-30", "2024-12-31", "2025-03-31", "2025-06-30"), withRule.UnadjustedDates);
        // Without it the day number 30 is kept (30 Mar, 30 Dec).
        var without = Unadjusted("2024-06-30", "2025-06-30", Frequency.Quarterly).Build();
        Assert.Equal(Dates("2024-06-30", "2024-09-30", "2024-12-30", "2025-03-30", "2025-06-30"), without.UnadjustedDates);
    }

    [Fact]
    public void EndOfMonthRuleForwardFromFebruaryMonthEnd()
    {
        // Monthly forward from 29 Feb 2024 (leap-year month end), with the rule: 31 Mar, 30 Apr, 31 May, 30 Jun.
        var withRule = Unadjusted("2024-02-29", "2024-06-30", Frequency.Monthly).WithRule(DateGenerationRule.Forward).WithEndOfMonth().Build();
        Assert.Equal(Dates("2024-02-29", "2024-03-31", "2024-04-30", "2024-05-31", "2024-06-30"), withRule.UnadjustedDates);
        Assert.All(Enumerable.Range(0, 4), i => Assert.True(withRule.IsRegular(i)));
        // Without it, day 29 is kept (29 Mar, 29 Apr, 29 May, 29 Jun) and 29 Jun -> 30 Jun is a one-day back stub.
        var without = Unadjusted("2024-02-29", "2024-06-30", Frequency.Monthly).WithRule(DateGenerationRule.Forward).Build();
        Assert.Equal(Dates("2024-02-29", "2024-03-29", "2024-04-29", "2024-05-29", "2024-06-29", "2024-06-30"), without.UnadjustedDates);
        Assert.False(without.IsRegular(4));
    }

    [Fact]
    public void EndOfMonthDatesAreThenAdjustedForBusinessDays()
    {
        // Same as the quarterly case but ModifiedFollowing on weekends: 30 Jun 2024 is a Sunday; Monday 1 Jul is in the
        // next month, so it rolls back to Friday 28 Jun. 30 Sep 2024 Mon, 31 Dec 2024 Tue, 31 Mar 2025 Mon, 30 Jun 2025 Mon.
        var schedule = new ScheduleBuilder(D.Of("2024-06-30"), D.Of("2025-06-30")).WithFrequency(Frequency.Quarterly).WithEndOfMonth().Build();
        Assert.Equal(Dates("2024-06-28", "2024-09-30", "2024-12-31", "2025-03-31", "2025-06-30"), schedule.AdjustedDates);
    }

    // --- Other rules -----------------------------------------------------------------------------------------------

    [Fact]
    public void ZeroRuleAndOnceFrequencyGiveASinglePeriod()
    {
        foreach (var schedule in new[]
        {
            Unadjusted("2025-01-15", "2030-01-15", Frequency.Quarterly).WithRule(DateGenerationRule.Zero).Build(),
            Unadjusted("2025-01-15", "2030-01-15", Frequency.Once).Build(),
            new ScheduleBuilder(D.Of("2025-01-15"), D.Of("2030-01-15")).WithConvention(BusinessDayConvention.Unadjusted).Build(),
        })
        {
            Assert.Equal(Dates("2025-01-15", "2030-01-15"), schedule.UnadjustedDates);
            Assert.Equal(1, schedule.PeriodCount);
            Assert.True(schedule.IsRegular(0));
            Assert.Null(schedule.Tenor);
            // 15 Jan 2025 -> 15 Jan 2030 is 5 years = 1826 days (one leap day, 29 Feb 2028).
            Assert.Equal(1826 / 360.0, schedule.GetAccrualPeriods(DayCounter.Actual360)[0].YearFraction, Tol);
            Assert.Equal(5.0, schedule.GetAccrualPeriods(DayCounter.Thirty360BondBasis)[0].YearFraction, Tol);
            Assert.Throws<ArgumentException>(() => schedule.GetAccrualPeriods(DayCounter.ActualActualIcma));
        }
    }

    [Fact]
    public void ThirdWednesdayRuleSnapsIntermediateDatesToImmDates()
    {
        // 19 Mar 2025 and 18 Mar 2026 are third Wednesdays. Forward quarterly: 19 Jun -> 18 Jun, 19 Sep -> 17 Sep, 19 Dec -> 17 Dec;
        // 19 Mar 2026 -> 18 Mar 2026 equals the termination date, so it is the end.
        var schedule = new ScheduleBuilder(D.Of("2025-03-19"), D.Of("2026-03-18"))
            .WithFrequency(Frequency.Quarterly)
            .WithRule(DateGenerationRule.ThirdWednesday)
            .WithConvention(BusinessDayConvention.Unadjusted)
            .Build();
        Assert.Equal(Dates("2025-03-19", "2025-06-18", "2025-09-17", "2025-12-17", "2026-03-18"), schedule.UnadjustedDates);
        Assert.All(schedule.UnadjustedDates, d => Assert.True(DateRules.IsImmDate(d)));
        Assert.All(Enumerable.Range(0, 4), i => Assert.True(schedule.IsRegular(i)));
        Assert.All(schedule.GetAccrualPeriods(DayCounter.ActualActualIcma), p => Assert.Equal(0.25, p.YearFraction, Tol));
    }

    [Fact]
    public void ThirdWednesdayRuleKeepsNonImmEndpoints()
    {
        // Effective 10 Mar 2025 (not IMM): forward quarterly gives 18 Jun, 17 Sep; the last one 17 Dec 2025 is before the 20 Dec end.
        var schedule = new ScheduleBuilder(D.Of("2025-03-10"), D.Of("2025-12-20"))
            .WithFrequency(Frequency.Quarterly)
            .WithRule(DateGenerationRule.ThirdWednesday)
            .WithConvention(BusinessDayConvention.Unadjusted)
            .Build();
        Assert.Equal(Dates("2025-03-10", "2025-06-18", "2025-09-17", "2025-12-17", "2025-12-20"), schedule.UnadjustedDates);
        Assert.True(schedule.IsRegular(0));   // the grid is generated forward from the effective date: 10 Mar + 3M snapped = 18 Jun
        Assert.True(schedule.IsRegular(1));
        Assert.False(schedule.IsRegular(3));  // 17 Dec -> 20 Dec stub
    }

    [Fact]
    public void WeeklyAndDailyTenorsStepInCalendarDays()
    {
        // Backward from 31 Mar 2025 in 2 weeks: 17 Mar, then 3 Mar = effective.
        var schedule = new ScheduleBuilder(D.Of("2025-03-03"), D.Of("2025-03-31"))
            .WithTenor(Period.Parse("2W")).WithConvention(BusinessDayConvention.Unadjusted).Build();
        Assert.Equal(Dates("2025-03-03", "2025-03-17", "2025-03-31"), schedule.UnadjustedDates);
        Assert.All(Enumerable.Range(0, 2), i => Assert.True(schedule.IsRegular(i)));
        // ICMA has no meaning for a 2-week step.
        Assert.Throws<ArgumentException>(() => schedule.GetAccrualPeriods(DayCounter.ActualActualIcma));
    }

    [Fact]
    public void AnnualTenorGivenAsYearsMatchesTwelveMonths()
    {
        var years = Unadjusted("2025-02-28", "2030-02-28", Frequency.Annual).Build();
        var months = new ScheduleBuilder(D.Of("2025-02-28"), D.Of("2030-02-28"))
            .WithTenor(Period.Parse("1Y")).WithConvention(BusinessDayConvention.Unadjusted).Build();
        Assert.Equal(years.UnadjustedDates, months.UnadjustedDates);
        Assert.Equal(6, years.Count);
    }

    // --- 30E/360 ISDA needs the termination date -------------------------------------------------------------------

    [Fact]
    public void Isda30E360UsesTheScheduleTerminationDate()
    {
        // Single period 31 Aug 2022 -> 28 Feb 2023 (backward: 28 Aug 2022 is before the effective date, so it is a stub).
        // 28 Feb 2023 is the termination date, so D2 stays 28: 360 - 180 - 2 = 178 days. Called without context it would be 180.
        var schedule = Unadjusted("2022-08-31", "2023-02-28", Frequency.Semiannual).Build();
        Assert.Equal(Dates("2022-08-31", "2023-02-28"), schedule.UnadjustedDates);
        Assert.Equal(178 / 360.0, schedule.GetAccrualPeriods(DayCounter.Thirty360EIsda)[0].YearFraction, Tol);
        Assert.Equal(180 / 360.0, DayCounter.Thirty360EIsda.YearFraction(D.Of("2022-08-31"), D.Of("2023-02-28")), Tol);
    }

    // --- Validation ------------------------------------------------------------------------------------------------

    [Fact]
    public void RejectsInconsistentSettings()
    {
        var e = D.Of("2025-01-15");
        var t = D.Of("2027-01-15");
        Assert.Throws<InvalidOperationException>(() => new ScheduleBuilder(t, e).Build());
        Assert.Throws<InvalidOperationException>(() => new ScheduleBuilder(e, e).Build());
        Assert.Throws<InvalidOperationException>(() => new ScheduleBuilder(e, t).WithTenor(Period.Parse("0M")).Build());
        Assert.Throws<InvalidOperationException>(() => new ScheduleBuilder(e, t).WithTenor(Period.Parse("-6M")).Build());
        Assert.Throws<InvalidOperationException>(() => new ScheduleBuilder(e, t).WithFrequency(Frequency.Annual).WithFirstDate(e).Build());
        Assert.Throws<InvalidOperationException>(() => new ScheduleBuilder(e, t).WithFrequency(Frequency.Annual).WithFirstDate(t).Build());
        Assert.Throws<InvalidOperationException>(() => new ScheduleBuilder(e, t).WithFrequency(Frequency.Annual).WithNextToLastDate(D.Of("2024-01-01")).Build());
        Assert.Throws<InvalidOperationException>(() => new ScheduleBuilder(e, t).WithFrequency(Frequency.Annual)
            .WithFirstDate(D.Of("2026-06-01")).WithNextToLastDate(D.Of("2026-01-01")).Build());
        Assert.Throws<InvalidOperationException>(() => new ScheduleBuilder(e, t).WithFirstDate(D.Of("2026-01-01")).Build()); // no tenor
        Assert.Throws<InvalidOperationException>(() => new ScheduleBuilder(e, t).WithTenor(Period.Parse("2W"))
            .WithRule(DateGenerationRule.ThirdWednesday).Build());
        Assert.Throws<InvalidOperationException>(() => new ScheduleBuilder(e, t).WithFrequency(Frequency.Quarterly)
            .WithRule(DateGenerationRule.ThirdWednesday).WithEndOfMonth().Build());
        Assert.Throws<ArgumentNullException>(() => new ScheduleBuilder(e, t).WithCalendar(null!));
        var schedule = new ScheduleBuilder(e, t).WithFrequency(Frequency.Annual).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.IsRegular(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.IsRegular(-1));
    }
}
