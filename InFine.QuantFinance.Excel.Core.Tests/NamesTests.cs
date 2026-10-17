using System;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Market;
using Xunit;

namespace InFine.QuantFinance.Excel.Core.Tests;

public class NamesTests
{
    [Theory]
    [InlineData("C", OptionType.Call)]
    [InlineData("call", OptionType.Call)]
    [InlineData(" Call ", OptionType.Call)]
    [InlineData("P", OptionType.Put)]
    [InlineData("PUT", OptionType.Put)]
    [InlineData("put", OptionType.Put)]
    public void OptionTypesFromText(string text, OptionType expected) =>
        Assert.Equal(expected, Names.ToOptionType(text, "type"));

    [Fact]
    public void OptionTypesFromNumbers()
    {
        Assert.Equal(OptionType.Call, Names.ToOptionType(1.0, "type"));
        Assert.Equal(OptionType.Put, Names.ToOptionType(-1.0, "type"));
        Assert.Equal(OptionType.Put, Names.ToOptionType("-1", "type"));
        Assert.Equal(OptionType.Call, Names.ToOptionType("1", "type"));
        Assert.Equal(XlError.Value, Assert.Throws<XlException>(() => Names.ToOptionType(0.0, "type")).Error);
        Assert.Equal(XlError.Value, Assert.Throws<XlException>(() => Names.ToOptionType("straddle", "type")).Error);
        Assert.Equal(XlError.Value, Assert.Throws<XlException>(() => Names.ToOptionType(null, "type")).Error);
    }

    [Theory]
    [InlineData("ACT/360", "Actual/360")]
    [InlineData("act/365f", "Actual/365 (Fixed)")]
    [InlineData("Act/365 (Fixed)", "Actual/365 (Fixed)")]
    [InlineData("ACT/365", "Actual/365 (Fixed)")]
    [InlineData("ACT/365.25", "Actual/365.25")]
    [InlineData("30/360", "30/360 Bond Basis")]
    [InlineData("30E/360", "30E/360")]
    [InlineData("30E/360 ISDA", "30E/360 ISDA")]
    [InlineData("ACT/ACT", "Actual/Actual (ISDA)")]
    [InlineData("Actual/Actual (ISDA)", "Actual/Actual (ISDA)")]
    [InlineData("ACT/ACT AFB", "Actual/Actual (AFB)")]
    public void DayCountAliases(string text, string libraryName) =>
        Assert.Equal(libraryName, Names.ToDayCounter(text, "dc").Name);

    [Fact]
    public void UnknownNamesListWhatIsAccepted()
    {
        var e = Assert.Throws<XlException>(() => Names.ToDayCounter("ACT/999", "daycount"));
        Assert.Equal(XlError.Value, e.Error);
        Assert.Contains("daycount", e.Message);
        Assert.Contains("ACT/360", e.Message);
        var icma = Assert.Throws<XlException>(() => Names.ToDayCounter("ACT/ACT ICMA", "daycount"));
        Assert.Contains("ICMA", icma.Message);
    }

    [Fact]
    public void Calendars()
    {
        Assert.Same(TargetCalendar.Instance, Names.ToCalendar("TARGET", "c"));
        Assert.Same(NyseCalendar.Instance, Names.ToCalendar("nyse", "c"));
        Assert.Same(LondonCalendar.Instance, Names.ToCalendar("London", "c"));
        Assert.Same(WeekendsOnlyCalendar.Instance, Names.ToCalendar("WEEKENDS", "c"));
        var joint = Names.ToCalendar("TARGET + NYSE", "c");
        Assert.IsType<JointCalendar>(joint);
        // 2025-07-04 is an NYSE holiday only, 2025-12-26 a TARGET holiday only: both close a joint calendar.
        Assert.False(joint.IsBusinessDay(new DateOnly(2025, 7, 4)));
        Assert.False(joint.IsBusinessDay(new DateOnly(2025, 12, 26)));
        Assert.True(joint.IsBusinessDay(new DateOnly(2025, 7, 7)));
        Assert.Equal(XlError.Value, Assert.Throws<XlException>(() => Names.ToCalendar("MARS", "c")).Error);
        Assert.Same(TargetCalendar.Instance, Names.ToCalendar(null, "c", TargetCalendar.Instance));
    }

    [Fact]
    public void ConventionsCompoundingsAndInterpolations()
    {
        Assert.Equal(BusinessDayConvention.ModifiedFollowing, Names.ToConvention("MF", "c", BusinessDayConvention.Following));
        Assert.Equal(BusinessDayConvention.ModifiedFollowing, Names.ToConvention("Modified Following", "c", BusinessDayConvention.Following));
        Assert.Equal(BusinessDayConvention.Preceding, Names.ToConvention("preceding", "c", BusinessDayConvention.Following));
        Assert.Equal(BusinessDayConvention.Following, Names.ToConvention(null, "c", BusinessDayConvention.Following));
        Assert.Equal(Compounding.Annual, Names.ToCompounding("annual", "c", Compounding.Continuous));
        Assert.Equal(Compounding.Continuous, Names.ToCompounding(null, "c", Compounding.Continuous));
        Assert.Same(CurveInterpolation.MonotoneCubicZero, Names.ToInterpolation("MonotoneCubicZero", "i", CurveInterpolation.LinearZero));
        Assert.Same(CurveInterpolation.LinearZero, Names.ToInterpolation("linear zero", "i", CurveInterpolation.LogLinearDiscount));
        Assert.Same(CurveInterpolation.LogLinearDiscount, Names.ToInterpolation("", "i", CurveInterpolation.LogLinearDiscount));
        Assert.Equal(XlError.Value, Assert.Throws<XlException>(() => Names.ToConvention("sideways", "c", default)).Error);
    }
}
