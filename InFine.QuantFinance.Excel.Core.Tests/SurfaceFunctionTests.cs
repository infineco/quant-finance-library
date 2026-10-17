using System;
using System.Linq;
using InFine.QuantFinance.Volatility;
using Xunit;

namespace InFine.QuantFinance.Excel.Core.Tests;

[Collection("QuantFunctions")]
public class SurfaceFunctionTests
{
    private readonly ObjectCache _cache = new();
    private readonly QuantFunctions _qf;

    private const double Maturity = 0.75, Forward = 100.0;
    private static readonly SviRawParameters Truth = new(a: 0.015, b: 0.09, rho: -0.45, m: 0.04, sigma: 0.18);
    private static readonly double[] Strikes = { 70, 80, 90, 95, 100, 105, 110, 120, 135 };

    public SurfaceFunctionTests()
    {
        _qf = new QuantFunctions(_cache);
    }

    private static double TruthVol(double strike) =>
        Math.Sqrt(Truth.TotalVariance(Math.Log(strike / Forward)) / Maturity);

    private string Fit(string name = "SPX") =>
        T.Text(_qf.SviFit(name, Maturity, Forward,
            T.Column(Strikes.Cast<object>().ToArray()),
            T.Column(Strikes.Select(k => (object)TruthVol(k)).ToArray())));

    [Fact]
    public void FitRecoversASyntheticSmileAndInterpolatesBetweenQuotes()
    {
        string handle = Fit();
        Assert.Equal("QF.Surface:SPX#1", handle);
        foreach (double k in Strikes.Concat(new[] { 72.5, 97.0, 102.5, 127.0 }))
            Assert.Equal(TruthVol(k), T.Num(_qf.SurfaceVol(handle, Maturity, k)), 6);
    }

    [Fact]
    public void FitMatchesTheLibraryCalibrator()
    {
        string handle = Fit();
        var quotes = new SmileQuotes(Maturity, Forward, Strikes, Strikes.Select(TruthVol).ToArray());
        var direct = new SviVolatilitySurface(new ConstantForwardCurve(Forward), new[] { Maturity },
            new[] { SviCalibrator.Calibrate(quotes).Parameters });
        foreach (double k in new[] { 75.0, 100.0, 125.0 })
            Assert.Equal(direct.ImpliedVolatility(Maturity, k), T.Num(_qf.SurfaceVol(handle, Maturity, k)), 12);
        // A different expiry scales total variance like the library surface does.
        Assert.Equal(direct.ImpliedVolatility(0.5, 95.0), T.Num(_qf.SurfaceVol(handle, 0.5, 95.0)), 12);
    }

    [Fact]
    public void FitAcceptsRowRangesAndDescribesTheFitInInfo()
    {
        string handle = T.Text(_qf.SviFit("ROW", Maturity, Forward,
            T.Row(Strikes.Cast<object>().ToArray()), T.Row(Strikes.Select(k => (object)TruthVol(k)).ToArray())));
        var info = T.Array(_qf.ObjInfo(handle));
        var map = Enumerable.Range(0, info.GetLength(0)).ToDictionary(i => (string)info[i, 0], i => info[i, 1]);
        Assert.Equal(Truth.Rho, (double)map["rho"], 4);
        Assert.Equal(Truth.B, (double)map["b"], 4);
        Assert.Equal(9.0, (double)map["Quotes"]);
        Assert.True((double)map["RMSE total variance"] < 1e-8);
    }

    [Fact]
    public void RefittingTheSameQuotesKeepsTheHandleNewQuotesBumpIt()
    {
        string h1 = Fit();
        string h2 = Fit();
        Assert.Equal(h1, h2);
        string h3 = T.Text(_qf.SviFit("SPX", Maturity, Forward + 1.0,
            T.Column(Strikes.Cast<object>().ToArray()), T.Column(Strikes.Select(k => (object)TruthVol(k)).ToArray())));
        Assert.Equal("QF.Surface:SPX#2", h3);
        Assert.Equal(XlError.Ref, _qf.SurfaceVol(h1, Maturity, 100.0));
    }

    [Fact]
    public void FitRejectsBadInputs()
    {
        var k = T.Column(Strikes.Cast<object>().ToArray());
        var v = T.Column(Strikes.Select(x => (object)TruthVol(x)).ToArray());
        Assert.Equal(XlError.Value, _qf.SviFit("X", Maturity, Forward, k, T.Column(0.2, 0.2)));          // length mismatch
        Assert.Contains("same length", ErrorLog.Last);
        Assert.Equal(XlError.Num, _qf.SviFit("X", Maturity, Forward, T.Column(90.0, 100.0, 110.0), T.Column(0.2, 0.2, 0.2)));  // too few
        Assert.Equal(XlError.Num, _qf.SviFit("X", -1.0, Forward, k, v));
        Assert.Equal(XlError.Num, _qf.SviFit("X", Maturity, 0.0, k, v));
        Assert.Equal(XlError.Value, _qf.SviFit("X", Maturity, Forward, k, T.Column(Strikes.Select(x => (object)"vol").ToArray())));
        Assert.Equal(XlError.Value, _qf.SviFit("", Maturity, Forward, k, v));
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public void SurfaceVolRejectsBadArguments()
    {
        string handle = Fit();
        Assert.Equal(XlError.Num, _qf.SurfaceVol(handle, 0.0, 100.0));
        Assert.Equal(XlError.Num, _qf.SurfaceVol(handle, Maturity, -5.0));
        Assert.Equal(XlError.Ref, _qf.SurfaceVol("QF.Surface:NOPE#1", Maturity, 100.0));
        string curve = T.Text(_qf.CurveFlat("C", 45000.0, 0.02, "ACT/365F"));
        Assert.Equal(XlError.Value, _qf.SurfaceVol(curve, Maturity, 100.0));
        Assert.Contains("SviSmile", ErrorLog.Last);
    }
}
