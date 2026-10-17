using System;
using System.Collections.Generic;
using System.Linq;
using InFine.QuantFinance.Numerics;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

/// <summary>
/// Curve compilation for the kernels. The independent reference is the host curve itself (tested against closed forms
/// and finite differences in <see cref="YieldCurveTests"/>): a node-for-node compiled curve must reproduce its discount
/// factors and instantaneous forwards bit for bit, at pillars, between them, before the first and beyond the last.
/// Sampled curves are checked against the flat-forward closed form <c>exp(-r t)</c> (a few ulps) and against the host at
/// the pillars (relative 1e-15: one exp/log round trip). Step forwards are checked through the identity
/// <c>P(0, t_k) exp(sum_{j &lt; k} r_j dt_j) = 1</c>, which is what makes the discounted spot a martingale on the grid
/// (1e-14: about 30 rounded additions of terms below 0.01).
/// </summary>
public class MarketCompilerTests
{
    private static readonly DateOnly Trade = D.Of("2025-01-15");

    public static TheoryData<string> Interpolations => new()
    {
        "LinearZero", "LogLinearDiscount", "MonotoneCubicZero", "MonotoneCubicLogDiscount",
    };

    private static InterpolatedDiscountCurve EurCurve(string interpolation)
    {
        if (interpolation != "MonotoneCubicLogDiscount")
            return BootstrapTests.Builder(interpolation, BootstrapTests.EurQuotes()).Build();
        // Any (quantity, interpolator) pair is a valid curve: reuse the bootstrapped pillars under the remaining pair.
        var source = BootstrapTests.Builder("MonotoneCubicZero", BootstrapTests.EurQuotes()).Build();
        return new InterpolatedDiscountCurve(Trade, DayCounter.Actual365Fixed, source.PillarTimes, source.DiscountFactors,
            new CurveInterpolation(InterpolatedQuantity.LogDiscountFactor, MonotoneCubicInterpolator.Instance));
    }

    private static SimulationGrid Grid(double maxStep = 1.0 / 12.0) =>
        new SimulationGridBuilder(Trade, DayCounter.Actual365Fixed).WithMaxTimeStep(maxStep)
            .AddEventDates([D.Of("2025-07-15"), D.Of("2026-01-15"), D.Of("2027-01-15")]).Build();

    private static IEnumerable<double> ProbeTimes(InterpolatedDiscountCurve curve) =>
        Enumerable.Range(0, 3601).Select(i => i / 100.0).Concat(curve.PillarTimes).Concat([1e-6, curve.PillarTimes[0] / 2]);

    [Theory]
    [MemberData(nameof(Interpolations))]
    public void InterpolatedCurveCompilesNodeForNodeAndIsReproducedBitForBit(string interpolation)
    {
        var curve = EurCurve(interpolation);
        var compiled = MarketCompiler.CompileDiscountCurve(curve, Grid());

        var expectedMode = interpolation switch
        {
            "LinearZero" => DiscountCurveKernels.LinearZeroRate,
            "LogLinearDiscount" => DiscountCurveKernels.LinearLogDiscount,
            "MonotoneCubicZero" => DiscountCurveKernels.HermiteZeroRate,
            _ => DiscountCurveKernels.HermiteLogDiscount,
        };
        Assert.Equal(expectedMode, compiled.Mode);
        Assert.Equal(curve.PillarTimes.Count + (expectedMode & 1), compiled.Count);
        foreach (var t in ProbeTimes(curve))
        {
            Assert.Equal(curve.DiscountFactor(t), compiled.DiscountFactor(t));
            Assert.Equal(curve.InstantaneousForward(t), compiled.InstantaneousForward(t));
        }
    }

    [Fact]
    public void FlatForwardIsSampledAndExactEverywhere()
    {
        const double r = 0.0315;
        var flat = new FlatForward(Trade, r, DayCounter.Actual365Fixed);
        var compiled = MarketCompiler.CompileDiscountCurve(flat, Grid());
        Assert.Equal(DiscountCurveKernels.LinearLogDiscount, compiled.Mode);
        foreach (var t in Enumerable.Range(0, 1001).Select(i => i / 25.0))
        {
            Assert.Equal(Math.Exp(-r * t), compiled.DiscountFactor(t), 1e-15);
            Assert.Equal(r, compiled.InstantaneousForward(t), 1e-13);
        }
    }

    [Fact]
    public void OtherInterpolatorsAreSampledExactlyAtThePillars()
    {
        var source = EurCurve("MonotoneCubicZero");
        var curve = new InterpolatedDiscountCurve(Trade, DayCounter.Actual365Fixed, source.PillarTimes, source.DiscountFactors,
            new CurveInterpolation(InterpolatedQuantity.ZeroRate, new CustomLinear()));
        var grid = Grid();
        var compiled = MarketCompiler.CompileDiscountCurve(curve, grid);
        Assert.Equal(DiscountCurveKernels.LinearLogDiscount, compiled.Mode);
        Assert.Equal(grid.PillarCount, compiled.Count);
        foreach (var t in grid.Times)
            Assert.Equal(curve.DiscountFactor(t), compiled.DiscountFactor(t), 1e-15 * curve.DiscountFactor(t));
        var last = grid.Times[^1];
        Assert.Equal(curve.InstantaneousForward(last), compiled.InstantaneousForward(last + 3.0));
    }

    [Theory]
    [MemberData(nameof(Interpolations))]
    public void StepForwardsMakeTheDiscountedSpotAMartingaleOnTheGrid(string interpolation)
    {
        var curve = EurCurve(interpolation);
        var grid = Grid(1.0 / 52.0);
        var compiled = MarketCompiler.CompileDiscountCurve(curve, grid);
        var times = grid.ToTimesArray();
        var rates = compiled.StepForwards(times);
        var discounts = compiled.DiscountFactors(times);

        Assert.Equal(grid.StepCount, rates.Length);
        Assert.Equal(1.0, discounts[0]);
        var integral = 0.0;
        for (var k = 0; k < rates.Length; k++)
        {
            Assert.Equal(curve.ForwardRate(times[k], times[k + 1]), rates[k], 1e-12);
            integral += rates[k] * (times[k + 1] - times[k]);
            Assert.Equal(1.0, discounts[k + 1] * Math.Exp(integral), 1e-14);
        }
    }

    [Fact]
    public void RejectsACurveOnAnotherClock()
    {
        var grid = Grid();
        Assert.Throws<ArgumentException>(() =>
            MarketCompiler.CompileDiscountCurve(new FlatForward(Trade.AddDays(1), 0.03, DayCounter.Actual365Fixed), grid));
        Assert.Throws<ArgumentException>(() =>
            MarketCompiler.CompileDiscountCurve(new FlatForward(Trade, 0.03, DayCounter.Actual360), grid));
    }

    [Fact]
    public void CompiledCurveValidatesItsData()
    {
        double[] t = [0.5, 1.0];
        double[] z = [0.02, 0.03];
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompiledDiscountCurve(4, t, z, default, 0, 0));
        Assert.Throws<ArgumentException>(() => new CompiledDiscountCurve(DiscountCurveKernels.LinearZeroRate, [], [], default, 0, 0));
        Assert.Throws<ArgumentException>(() => new CompiledDiscountCurve(DiscountCurveKernels.LinearZeroRate, [0.0, 1.0], z, default, 0, 0));
        Assert.Throws<ArgumentException>(() => new CompiledDiscountCurve(DiscountCurveKernels.LinearZeroRate, [1.0, 1.0], z, default, 0, 0));
        Assert.Throws<ArgumentException>(() => new CompiledDiscountCurve(DiscountCurveKernels.LinearZeroRate, t, [0.02], default, 0, 0));
        Assert.Throws<ArgumentException>(() => new CompiledDiscountCurve(DiscountCurveKernels.HermiteZeroRate, t, z, [0.0], 0, 0));
        Assert.Throws<ArgumentException>(() => new CompiledDiscountCurve(DiscountCurveKernels.LinearLogDiscount, t, z, default, 0, 0));
        Assert.Throws<ArgumentException>(() => new CompiledDiscountCurve(DiscountCurveKernels.LinearZeroRate, t, [0.02, double.NaN], default, 0, 0));
        Assert.Throws<ArgumentException>(() => new CompiledDiscountCurve(DiscountCurveKernels.LinearZeroRate, t, z, default, 0, double.PositiveInfinity));

        var curve = new CompiledDiscountCurve(DiscountCurveKernels.LinearZeroRate, t, z, default, -0.03, 0.04);
        Assert.Throws<ArgumentOutOfRangeException>(() => curve.DiscountFactor(-1e-9));
        Assert.Throws<ArgumentException>(() => curve.StepForwards([0.0]));
        Assert.Throws<ArgumentException>(() => curve.StepForwards([0.0, 0.5, 0.5]));
    }

    /// <summary>A linear interpolator the compiler does not know, so it must fall back to sampling.</summary>
    private sealed class CustomLinear : IInterpolator
    {
        public string Name => "Custom linear";

        public bool IsLocal => true;

        public IInterpolation Interpolate(ReadOnlySpan<double> x, ReadOnlySpan<double> y) => LinearInterpolator.Instance.Interpolate(x, y);
    }
}
