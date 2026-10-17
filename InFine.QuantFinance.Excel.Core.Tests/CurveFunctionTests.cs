using System;
using System.Linq;
using InFine.QuantFinance.Market;
using Xunit;

namespace InFine.QuantFinance.Excel.Core.Tests;

[Collection("QuantFunctions")]
public class CurveFunctionTests
{
    private readonly ObjectCache _cache = new();
    private readonly QuantFunctions _qf;

    private static readonly DateOnly Ref = new(2025, 1, 15);
    private static readonly double RefSerial = ExcelDates.ToSerial(Ref);

    private static readonly (string Type, string Tenor, double Quote)[] Quotes =
    [
        ("Deposit", "1W", 0.0290), ("Deposit", "1M", 0.0285), ("Deposit", "3M", 0.0275), ("Deposit", "6M", 0.0260),
        ("Swap", "1Y", 0.0245), ("Swap", "2Y", 0.0230), ("Swap", "3Y", 0.0228), ("Swap", "5Y", 0.0233),
        ("Swap", "7Y", 0.0240), ("Swap", "10Y", 0.0250), ("Swap", "15Y", 0.0258), ("Swap", "30Y", 0.0235),
    ];

    public CurveFunctionTests()
    {
        _qf = new QuantFunctions(_cache);
    }

    private static object[,] Types(params string[] values) => T.Column(values.Cast<object>().ToArray());

    private string Bootstrap(string name = "EUR", object? interpolation = null, object? calendar = null) =>
        T.Text(_qf.CurveBootstrap(name, RefSerial,
            T.Column(Quotes.Select(q => (object)q.Type).ToArray()),
            T.Column(Quotes.Select(q => (object)q.Tenor).ToArray()),
            T.Column(Quotes.Select(q => (object)q.Quote).ToArray()), interpolation, calendar));

    [Fact]
    public void FlatCurveDiscountsExponentially()
    {
        string handle = T.Text(_qf.CurveFlat("FLAT", RefSerial, 0.03, "ACT/365F"));
        Assert.Equal("QF.Curve:FLAT#1", handle);
        double oneYear = ExcelDates.ToSerial(Ref.AddDays(365));   // exactly 1.0 under Actual/365 Fixed
        Assert.Equal(Math.Exp(-0.03), T.Num(_qf.CurveDf(handle, oneYear)), 14);
        double tenYears = ExcelDates.ToSerial(Ref.AddDays(3650));
        Assert.Equal(Math.Exp(-0.3), T.Num(_qf.CurveDf(handle, tenYears)), 14);
        Assert.Equal(1.0, T.Num(_qf.CurveDf(handle, RefSerial)), 14);
        Assert.Equal(0.03, T.Num(_qf.CurveZero(handle, oneYear)), 14);
        // The same flat curve in annual compounding: exp(r) - 1.
        Assert.Equal(Math.Exp(0.03) - 1.0, T.Num(_qf.CurveZero(handle, oneYear, "Annual")), 14);
        Assert.Equal(Math.Exp(0.03) - 1.0, T.Num(_qf.CurveZero(handle, oneYear, "annual")), 14);
    }

    [Fact]
    public void FlatCurveInAnnualCompoundingDiscountsAsOnePlusRatePower()
    {
        string handle = T.Text(_qf.CurveFlat("ANN", RefSerial, 0.03, "ACT/365F", "Annual"));
        double twoYears = ExcelDates.ToSerial(Ref.AddDays(730));
        Assert.Equal(1.0 / (1.03 * 1.03), T.Num(_qf.CurveDf(handle, twoYears)), 14);
        Assert.Equal(0.03, T.Num(_qf.CurveZero(handle, twoYears, "Annual")), 14);
    }

    [Fact]
    public void FlatCurveHonoursTheDayCount()
    {
        string a360 = T.Text(_qf.CurveFlat("A", RefSerial, 0.04, "ACT/360"));
        double date = ExcelDates.ToSerial(Ref.AddDays(180));
        Assert.Equal(Math.Exp(-0.04 * 0.5), T.Num(_qf.CurveDf(a360, date)), 14);
    }

    [Fact]
    public void RecreatingAFlatCurveWithIdenticalInputsKeepsTheHandleAndChangingThemBumpsIt()
    {
        string h1 = T.Text(_qf.CurveFlat("C", RefSerial, 0.03, "ACT/365F"));
        string h2 = T.Text(_qf.CurveFlat("C", RefSerial, 0.03, "ACT/365F"));
        string h3 = T.Text(_qf.CurveFlat("C", RefSerial, 0.031, "ACT/365F"));
        Assert.Equal(h1, h2);
        Assert.Equal("QF.Curve:C#2", h3);
        // The old handle is now stale.
        Assert.Equal(XlError.Ref, _qf.CurveDf(h1, RefSerial + 100));
        Assert.Contains("stale", ErrorLog.Last);
        Assert.Equal(Math.Exp(-0.031 * 100 / 365.0), T.Num(_qf.CurveDf(h3, RefSerial + 100)), 14);
    }

    [Fact]
    public void BootstrapMatchesTheLibraryBuilderPillarForPillar()
    {
        string handle = Bootstrap();
        Assert.Equal("QF.Curve:EUR#1", handle);

        var builder = new PiecewiseYieldCurveBuilder(Ref, DayCounter.Actual365Fixed);
        var conventions = SwapConventions.EurSixMonth;
        foreach (var (type, tenor, quote) in Quotes)
            builder.Add(type == "Deposit"
                ? Deposit.FromTenor(Ref, Period.Parse(tenor), TargetCalendar.Instance)
                : VanillaSwap.FromTenor(Ref, Period.Parse(tenor), conventions), quote);
        var expected = builder.Build();

        foreach (int days in new[] { 7, 30, 91, 180, 365, 400, 900, 3650, 365 * 25, 365 * 40 })
        {
            var date = Ref.AddDays(days);
            Assert.Equal(expected.DiscountFactor(date), T.Num(_qf.CurveDf(handle, ExcelDates.ToSerial(date))), 14);
        }
        Assert.Equal(expected.ZeroRate(Ref.AddDays(2000), Compounding.Annual),
            T.Num(_qf.CurveZero(handle, ExcelDates.ToSerial(Ref.AddDays(2000)), "Annual")), 14);
    }

    [Fact]
    public void BootstrappedCurveRepricesItsInputInstruments()
    {
        // Independent of the builder's own bookkeeping: rebuild each instrument and price it off the cached curve.
        string handle = Bootstrap();
        var curve = _cache.Get<IYieldCurve>(handle);
        foreach (var (type, tenor, quote) in Quotes)
        {
            ICurveInstrument instrument = type == "Deposit"
                ? Deposit.FromTenor(Ref, Period.Parse(tenor), TargetCalendar.Instance)
                : VanillaSwap.FromTenor(Ref, Period.Parse(tenor), SwapConventions.EurSixMonth);
            Assert.True(Math.Abs(instrument.ImpliedRate(curve) - quote) < 1e-10, $"{type} {tenor}");
        }
    }

    [Theory]
    [InlineData("LinearZero")]
    [InlineData("LogLinearDiscount")]
    [InlineData("MonotoneCubicZero")]
    public void BootstrapInterpolationOptionSelectsTheLibraryInterpolation(string interpolation)
    {
        string handle = Bootstrap("I", interpolation);
        var curve = (InterpolatedDiscountCurve)_cache.Get<IYieldCurve>(handle);
        Assert.Contains(curve.Interpolation.Quantity.ToString(), Assert.IsType<string>(InfoValue(handle, "Interpolation")));
        Assert.Equal(Quotes.Length, curve.PillarTimes.Count);
        Assert.Equal(interpolation.EndsWith("Zero") ? InterpolatedQuantity.ZeroRate : InterpolatedQuantity.LogDiscountFactor,
            curve.Interpolation.Quantity);
    }

    private object InfoValue(string handle, string key)
    {
        var info = T.Array(_qf.ObjInfo(handle));
        for (int i = 0; i < info.GetLength(0); i++)
            if ((string)info[i, 0] == key) return info[i, 1];
        throw new Xunit.Sdk.XunitException("no such info key " + key);
    }

    [Fact]
    public void BootstrapAcceptsRowRangesAndFraAndAnotherCalendar()
    {
        var types = T.Row("Deposit", "FRA", "Swap", "Swap");
        var tenors = T.Row("3M", "3x6", "2Y", "5Y");
        var quotes = T.Row(0.027, 0.0265, 0.0235, 0.0240);
        string handle = T.Text(_qf.CurveBootstrap("MIX", RefSerial, types, tenors, quotes, "LinearZero", "TARGET+LONDON"));
        var curve = _cache.Get<IYieldCurve>(handle);
        var cal = new JointCalendar(TargetCalendar.Instance, LondonCalendar.Instance);
        var fra = ForwardRateAgreement.FromTenors(Ref, new Period(3, PeriodUnit.Months), new Period(3, PeriodUnit.Months), cal);
        Assert.True(Math.Abs(fra.ImpliedRate(curve) - 0.0265) < 1e-10);
        Assert.Equal(cal.Name, InfoValue(handle, "Calendar"));
    }

    [Fact]
    public void BootstrapReportsInconsistentInputs()
    {
        var types = T.Column("Deposit", "Swap");
        var tenors = T.Column("3M", "5Y");
        Assert.Equal(XlError.Value, _qf.CurveBootstrap("X", RefSerial, types, tenors, T.Column(0.03), null, null));
        Assert.Contains("same length", ErrorLog.Last);
        Assert.Equal(XlError.Value, _qf.CurveBootstrap("X", RefSerial, T.Column("Bond"), T.Column("3M"), T.Column(0.03), null, null));
        Assert.Contains("DEPOSIT", ErrorLog.Last);
        Assert.Equal(XlError.Value, _qf.CurveBootstrap("X", RefSerial, T.Column("Deposit"), T.Column("soon"), T.Column(0.03), null, null));
        Assert.Equal(XlError.Value, _qf.CurveBootstrap("X", RefSerial, T.Column("Deposit"), T.Column("3M"), T.Column(new object?[] { null }), null, null));
        Assert.Equal(XlError.Value, _qf.CurveBootstrap("X", RefSerial, T.Column("FRA"), T.Column("6x3"), T.Column(0.03), null, null));
        // Two instruments maturing the same day cannot form a curve: a library failure, reported as #NUM!.
        Assert.Equal(XlError.Num, _qf.CurveBootstrap("X", RefSerial, T.Column("Deposit", "Deposit"), T.Column("3M", "3M"), T.Column(0.03, 0.031), null, null));
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public void CurveFunctionsRejectBadHandlesAndDates()
    {
        string curve = T.Text(_qf.CurveFlat("C", RefSerial, 0.03, "ACT/365F"));
        Assert.Equal(XlError.Value, _qf.CurveDf("not a handle", RefSerial));
        Assert.Equal(XlError.Ref, _qf.CurveDf("QF.Curve:GHOST#1", RefSerial));
        Assert.Equal(XlError.Num, _qf.CurveDf(curve, RefSerial - 10));        // before the reference date
        Assert.Equal(XlError.Value, _qf.CurveDf(curve, 60.0));                // the fake leap day
        Assert.Equal(XlError.Value, _qf.CurveDf(curve, null));
        Assert.Equal(XlError.Value, _qf.CurveZero(curve, RefSerial + 10, "weekly"));
        Assert.Equal(XlError.NA, _qf.CurveDf(XlError.NA, RefSerial));
    }

    [Fact]
    public void CurveFunctionRejectsAHandleOfAnotherKind()
    {
        string surface = T.Text(_qf.SviFit("S", 1.0, 100.0, T.Column(80.0, 90.0, 100.0, 110.0, 120.0), T.Column(0.28, 0.24, 0.21, 0.22, 0.25)));
        Assert.Equal(XlError.Value, _qf.CurveDf(surface, RefSerial + 10));
        Assert.Contains("SviSmile", ErrorLog.Last);
        Assert.Contains("IYieldCurve", ErrorLog.Last);
    }
}
