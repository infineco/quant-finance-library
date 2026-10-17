using System;
using System.Linq;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

/// <summary>
/// Curve identities. Flat forward: closed forms <c>P = exp(-r t)</c>, zero = forward = r, annual zero <c>e^r - 1</c>,
/// simple zero <c>(e^{rt} - 1) / t</c>. Interpolated curves: exact pillars, consistency of zero/forward/discount
/// definitions, instantaneous forward against central finite differences of <c>-ln P</c>, and the documented flat-forward
/// extrapolation at both ends. Tolerances are a few ulps for closed forms and 1e-8 for finite differences
/// (step 1e-6: truncation ~ 1e-12 f'', rounding ~ 1e-16 / 1e-6 = 1e-10).
/// </summary>
public class YieldCurveTests
{
    private static readonly DateOnly Reference = D.Of("2025-01-15");

    [Fact]
    public void FlatForwardClosedForms()
    {
        const double r = 0.0315;
        var curve = new FlatForward(Reference, r, DayCounter.Actual365Fixed);
        foreach (var t in new[] { 0.0, 0.01, 0.5, 1.0, 7.3, 30.0 })
        {
            Assert.Equal(Math.Exp(-r * t), curve.DiscountFactor(t), 15);
            Assert.Equal(r, curve.ZeroRate(t), 14);
            Assert.Equal(r, curve.InstantaneousForward(t));
            Assert.Equal(Math.Exp(r) - 1, curve.ZeroRate(t, Compounding.Annual), 13);
            if (t > 0) Assert.Equal((Math.Exp(r * t) - 1) / t, curve.ZeroRate(t, Compounding.Simple), 13);
        }
        Assert.Equal(r, curve.ZeroRate(0.0, Compounding.Simple));
        Assert.Equal(r, curve.ForwardRate(2.0, 5.0), 14);
        Assert.Equal(r, curve.ForwardRate(4.0, 4.0));
        Assert.Equal((Math.Exp(r * 0.25) - 1) / 0.25, curve.ForwardRate(1.0, 1.25, Compounding.Simple), 13);
        // Dates: 365 days later is exactly t = 1 under Act/365F.
        Assert.Equal(Math.Exp(-r), curve.DiscountFactor(Reference.AddDays(365)), 15);
        Assert.Equal(1.0, curve.DiscountFactor(Reference));
        Assert.Equal(r, curve.ZeroRate(Reference.AddDays(1000)), 14);
    }

    [Fact]
    public void FlatForwardFromAnAnnualRate()
    {
        var curve = new FlatForward(Reference, 0.03, DayCounter.Actual365Fixed, Compounding.Annual);
        Assert.Equal(Math.Log(1.03), curve.Rate, 15);
        Assert.Equal(Math.Pow(1.03, -2.5), curve.DiscountFactor(2.5), 15);
        Assert.Equal(0.03, curve.ZeroRate(4.0, Compounding.Annual), 14);
        Assert.Throws<ArgumentException>(() => new FlatForward(Reference, 0.03, DayCounter.Actual365Fixed, Compounding.Simple));
    }

    [Fact]
    public void CurvesRejectNegativeTimesAndBackwardForwards()
    {
        var curve = new FlatForward(Reference, 0.02, DayCounter.Actual365Fixed);
        Assert.Throws<ArgumentOutOfRangeException>(() => curve.DiscountFactor(-0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => curve.DiscountFactor(Reference.AddDays(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => curve.DiscountFactor(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => curve.ForwardRate(2.0, 1.0));
    }

    [Fact]
    public void CompoundingConversionsRoundTrip()
    {
        foreach (var c in new[] { Compounding.Continuous, Compounding.Annual, Compounding.Simple })
        foreach (var t in new[] { 0.1, 1.0, 12.5 })
            Assert.Equal(0.0275, c.ImpliedRate(c.DiscountFactor(0.0275, t), t), 14);
        Assert.Equal(1 / 1.0275, Compounding.Simple.DiscountFactor(0.0275, 1.0), 15);
        Assert.Equal(Math.Pow(1.0275, -3), Compounding.Annual.DiscountFactor(0.0275, 3.0), 15);
    }

    // --- Interpolated curves ------------------------------------------------------------------------------------------

    private static readonly double[] Times = [0.25, 0.5, 1, 2, 5, 10, 30];
    private static readonly double[] Zeros = [0.029, 0.028, 0.026, 0.023, 0.024, 0.026, 0.024];

    public static TheoryData<string> Interpolations => new() { "LinearZero", "LogLinearDiscount", "MonotoneCubicZero" };

    private static CurveInterpolation Interpolation(string name) => name switch
    {
        "LinearZero" => CurveInterpolation.LinearZero,
        "LogLinearDiscount" => CurveInterpolation.LogLinearDiscount,
        _ => CurveInterpolation.MonotoneCubicZero,
    };

    private static InterpolatedDiscountCurve Curve(string name) =>
        new(Reference, DayCounter.Actual365Fixed, Times, Times.Select((t, i) => Math.Exp(-Zeros[i] * t)).ToArray(), Interpolation(name));

    [Theory]
    [MemberData(nameof(Interpolations))]
    public void ExactAtPillars(string name)
    {
        var curve = Curve(name);
        for (var i = 0; i < Times.Length; i++)
        {
            Assert.Equal(Math.Exp(-Zeros[i] * Times[i]), curve.DiscountFactor(Times[i]), 15);
            Assert.Equal(Zeros[i], curve.ZeroRate(Times[i]), 14);
        }
        Assert.Equal(1.0, curve.DiscountFactor(0.0));
    }

    [Theory]
    [MemberData(nameof(Interpolations))]
    public void RateDefinitionsAreConsistent(string name)
    {
        var curve = Curve(name);
        foreach (var (t1, t2) in new[] { (0.1, 0.3), (0.7, 3.2), (4.0, 12.0), (25.0, 40.0) })
        {
            var p1 = curve.DiscountFactor(t1);
            var p2 = curve.DiscountFactor(t2);
            Assert.Equal(Math.Log(p1 / p2) / (t2 - t1), curve.ForwardRate(t1, t2), 14);
            Assert.Equal((p1 / p2 - 1) / (t2 - t1), curve.ForwardRate(t1, t2, Compounding.Simple), 14);
            // Zero rates and forwards compose: z2 t2 = z1 t1 + F (t2 - t1).
            Assert.Equal(curve.ZeroRate(t2) * t2, curve.ZeroRate(t1) * t1 + curve.ForwardRate(t1, t2) * (t2 - t1), 14);
        }
    }

    [Theory]
    [MemberData(nameof(Interpolations))]
    public void InstantaneousForwardIsMinusTheLogDerivative(string name)
    {
        var curve = Curve(name);
        const double h = 1e-6;
        foreach (var t in new[] { 0.1, 0.4, 0.8, 1.5, 3.3, 7.0, 20.0, 35.0 })
        {
            var fd = -(Math.Log(curve.DiscountFactor(t + h)) - Math.Log(curve.DiscountFactor(t - h))) / (2 * h);
            Assert.Equal(fd, curve.InstantaneousForward(t), 8);
        }
    }

    [Theory]
    [MemberData(nameof(Interpolations))]
    public void ForwardIsFlatOutsideThePillars(string name)
    {
        var curve = Curve(name);
        // Before the first pillar: the first zero rate.
        Assert.Equal(Zeros[0], curve.InstantaneousForward(0.0), 14);
        Assert.Equal(Zeros[0], curve.InstantaneousForward(0.2), 14);
        Assert.Equal(Zeros[0], curve.ZeroRate(0.1), 14);
        // Beyond the last pillar: the forward at the last pillar.
        var fLast = curve.InstantaneousForward(30.0);
        Assert.Equal(fLast, curve.InstantaneousForward(31.0));
        Assert.Equal(fLast, curve.InstantaneousForward(100.0));
        Assert.Equal(curve.DiscountFactor(30.0) * Math.Exp(-fLast * 20.0), curve.DiscountFactor(50.0), 15);
        Assert.Equal(fLast, curve.ForwardRate(40.0, 60.0), 13);
    }

    [Fact]
    public void LogLinearDiscountGivesPiecewiseConstantForwards()
    {
        var curve = Curve("LogLinearDiscount");
        double[] nodes = [0.0, .. Times];
        double[] logP = [0.0, .. Times.Select((t, i) => -Zeros[i] * t)];
        for (var i = 0; i + 1 < nodes.Length; i++)
        {
            var expected = -(logP[i + 1] - logP[i]) / (nodes[i + 1] - nodes[i]);
            for (var j = 0; j < 10; j++)
            {
                var t = nodes[i] + (nodes[i + 1] - nodes[i]) * j / 10.0;
                Assert.Equal(expected, curve.InstantaneousForward(t), 13);
            }
            // Any sub-period forward inside the segment is the same constant.
            var a = nodes[i] + 0.2 * (nodes[i + 1] - nodes[i]);
            var b = nodes[i] + 0.7 * (nodes[i + 1] - nodes[i]);
            Assert.Equal(expected, curve.ForwardRate(a, b), 12);
        }
    }

    [Fact]
    public void MonotoneCubicZeroHasContinuousForwards()
    {
        // Zero rates are C1, so f = z + t z' is continuous at the pillars (linear zero rates would jump there).
        var cubic = Curve("MonotoneCubicZero");
        var linear = Curve("LinearZero");
        const double eps = 1e-9;
        var maxLinearJump = 0.0;
        foreach (var t in Times.Skip(1).Take(Times.Length - 2))
        {
            Assert.Equal(cubic.InstantaneousForward(t - eps), cubic.InstantaneousForward(t + eps), 7);
            maxLinearJump = Math.Max(maxLinearJump, Math.Abs(linear.InstantaneousForward(t + eps) - linear.InstantaneousForward(t - eps)));
        }
        Assert.True(maxLinearJump > 1e-3);
    }

    [Fact]
    public void BuildsFromDatesAndValidates()
    {
        var dates = new[] { D.Of("2025-04-15"), D.Of("2026-01-15") };
        var curve = new InterpolatedDiscountCurve(Reference, DayCounter.Actual365Fixed, dates, [0.99, 0.97], CurveInterpolation.LinearZero);
        Assert.Equal(90 / 365.0, curve.PillarTimes[0], 15);
        Assert.Equal(0.97, curve.DiscountFactor(D.Of("2026-01-15")), 15);
        Assert.Equal(dates, curve.PillarDates);
        Assert.Throws<ArgumentException>(() => new InterpolatedDiscountCurve(Reference, DayCounter.Actual365Fixed, [1.0, 1.0], [0.99, 0.98], CurveInterpolation.LinearZero));
        Assert.Throws<ArgumentException>(() => new InterpolatedDiscountCurve(Reference, DayCounter.Actual365Fixed, [0.0], [1.0], CurveInterpolation.LinearZero));
        Assert.Throws<ArgumentException>(() => new InterpolatedDiscountCurve(Reference, DayCounter.Actual365Fixed, [1.0], [-0.5], CurveInterpolation.LinearZero));
        Assert.Throws<ArgumentException>(() => new InterpolatedDiscountCurve(Reference, DayCounter.Actual365Fixed, [1.0, 2.0], [0.9], CurveInterpolation.LinearZero));
    }
}
