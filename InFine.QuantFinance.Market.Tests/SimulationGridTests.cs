using System;
using System.Collections.Generic;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

/// <summary>
/// Date-to-time compilation of simulation grids. References: Act/365F day counts worked by hand (2025-01-15 to
/// 2025-04-15 is 90 days, to 2025-07-15 181 days, to 2026-01-15 365 days) and the refinement rule
/// <c>ceil(gap / h)</c> equal steps per gap. Event times must be the day counter's year fractions bit for bit.
/// </summary>
public class SimulationGridTests
{
    private static readonly DateOnly Valuation = D.Of("2025-01-15");

    private static SimulationGridBuilder Builder() => new(Valuation, DayCounter.Actual365Fixed);

    [Fact]
    public void MergesSortsAndIndexesEventDates()
    {
        var grid = Builder()
            .AddEventDates([D.Of("2026-01-15"), D.Of("2025-04-15"), D.Of("2025-07-15"), D.Of("2025-04-15"), Valuation])
            .Build();

        Assert.Equal(new[] { 0.0, 90 / 365.0, 181 / 365.0, 1.0 }, grid.Times);
        Assert.Equal(3, grid.StepCount);
        Assert.Equal(4, grid.PillarCount);
        Assert.Equal(new[] { Valuation, D.Of("2025-04-15"), D.Of("2025-07-15"), D.Of("2026-01-15") }, grid.EventDates);
        Assert.Equal(new DateOnly?[] { Valuation, D.Of("2025-04-15"), D.Of("2025-07-15"), D.Of("2026-01-15") }, grid.PillarDates);
        Assert.Equal(0, grid.PillarIndex(Valuation));
        Assert.Equal(1, grid.PillarIndex(D.Of("2025-04-15")));
        Assert.Equal(2, grid.PillarIndex(D.Of("2025-07-15")));
        Assert.Equal(3, grid.PillarIndex(D.Of("2026-01-15")));
        foreach (var date in grid.EventDates)
            Assert.Equal(DayCounter.Actual365Fixed.YearFraction(Valuation, date), grid.Times[grid.PillarIndex(date)]);
    }

    [Fact]
    public void RefinesEveryGapIntoEqualStepsNoLongerThanTheMaximum()
    {
        const double h = 1.0 / 12.0;
        var events = new[] { D.Of("2025-04-15"), D.Of("2025-07-15"), D.Of("2026-01-15"), D.Of("2027-03-01") };
        var grid = Builder().WithMaxTimeStep(h).AddEventDates(events).Build();

        var previous = 0.0;
        var previousIndex = 0;
        foreach (var date in events)
        {
            var index = grid.PillarIndex(date);
            var time = DayCounter.Actual365Fixed.YearFraction(Valuation, date);
            Assert.Equal(time, grid.Times[index]);
            var expectedSteps = (int)Math.Ceiling((time - previous) / h);
            Assert.Equal(expectedSteps, index - previousIndex);
            for (var k = previousIndex; k < index; k++)
            {
                var dt = grid.Times[k + 1] - grid.Times[k];
                Assert.True(dt <= h * (1 + 1e-12), $"step {k} is {dt}");
                Assert.Equal((time - previous) / expectedSteps, dt, 1e-14);
                if (k > previousIndex) Assert.Null(grid.PillarDates[k]);
            }
            previous = time;
            previousIndex = index;
        }
        Assert.Equal(previousIndex, grid.StepCount);
    }

    [Fact]
    public void AnExactMultipleOfTheMaximumStepIsNotSplitOnceMore()
    {
        // One Act/365F year is exactly 1.0; 1 / (1/12) rounds to 12.000000000000002, which must still give 12 steps.
        Assert.Equal(12, Builder().WithMaxTimeStep(1.0 / 12.0).AddEventDate(D.Of("2026-01-15")).Build().StepCount);
        Assert.Equal(4, Builder().WithMaxTimeStep(0.25).AddEventDate(D.Of("2026-01-15")).Build().StepCount);
        Assert.Equal(1, Builder().WithMaxTimeStep(5.0).AddEventDate(D.Of("2026-01-15")).Build().StepCount);
    }

    [Fact]
    public void DatesWithTheSameYearFractionShareAPillar()
    {
        // 30E/360 counts both the 30th and the 31st of January as 15 days after the 15th.
        var grid = new SimulationGridBuilder(Valuation, DayCounter.Thirty360E)
            .AddEventDates([D.Of("2025-01-31"), D.Of("2025-01-30"), D.Of("2025-02-15")])
            .Build();
        Assert.Equal(new[] { 0.0, 15 / 360.0, 30 / 360.0 }, grid.Times);
        Assert.Equal(1, grid.PillarIndex(D.Of("2025-01-30")));
        Assert.Equal(1, grid.PillarIndex(D.Of("2025-01-31")));
        Assert.Equal(D.Of("2025-01-30"), grid.PillarDates[1]);
        Assert.Equal(3, grid.EventDates.Count);
    }

    [Fact]
    public void TimesArrayIsACopy()
    {
        var grid = Builder().AddEventDate(D.Of("2026-01-15")).Build();
        var times = grid.ToTimesArray();
        times[1] = 42.0;
        Assert.Equal(1.0, grid.Times[1]);
    }

    [Fact]
    public void RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Builder().AddEventDate(D.Of("2025-01-14")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Builder().WithMaxTimeStep(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Builder().WithMaxTimeStep(double.NaN));
        Assert.Throws<InvalidOperationException>(() => Builder().Build());
        Assert.Throws<InvalidOperationException>(() => Builder().AddEventDate(Valuation).Build());
        var grid = Builder().AddEventDate(D.Of("2026-01-15")).Build();
        Assert.Throws<KeyNotFoundException>(() => grid.PillarIndex(D.Of("2025-06-01")));
        Assert.False(grid.TryGetPillarIndex(D.Of("2025-06-01"), out _));
    }
}
