using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

/// <summary>
/// Bootstrap of a realistic EUR-like single curve: deposits 1W-6M (Act/360) and swaps 1Y-30Y (annual 30/360 fixed vs
/// 6M Act/360 floating), TARGET, ModifiedFollowing, 2-day spot, curve time Act/365F. Quotes are a humped early-2025
/// shape (not market data).
/// <para>
/// Re-pricing tolerance 1e-10 on rates: Brent solves each pillar zero rate to 1e-14 and global iteration stops when no
/// zero rate moves by more than 1e-12 in a sweep; an implied rate has sensitivity O(1) (at most ~ t_k / annuity ~ 1.3
/// for a 30Y swap) to its own and neighbouring pillar zero rates, so the residual error is O(1e-12), two orders of
/// magnitude inside the tolerance.
/// </para>
/// </summary>
public class BootstrapTests
{
    private static readonly DateOnly Trade = D.Of("2025-01-15");

    private static readonly (string Tenor, double Rate)[] DepositQuotes =
        [("1W", 0.0290), ("1M", 0.0285), ("2M", 0.0280), ("3M", 0.0275), ("6M", 0.0260)];

    private static readonly (string Tenor, double Rate)[] SwapQuotes =
    [
        ("1Y", 0.0245), ("2Y", 0.0230), ("3Y", 0.0228), ("4Y", 0.0230), ("5Y", 0.0233), ("6Y", 0.0236), ("7Y", 0.0240),
        ("8Y", 0.0243), ("9Y", 0.0247), ("10Y", 0.0250), ("12Y", 0.0255), ("15Y", 0.0258), ("20Y", 0.0252),
        ("25Y", 0.0242), ("30Y", 0.0235),
    ];

    public static TheoryData<string> Interpolations => new() { "LinearZero", "LogLinearDiscount", "MonotoneCubicZero" };

    private static CurveInterpolation Interpolation(string name) => name switch
    {
        "LinearZero" => CurveInterpolation.LinearZero,
        "LogLinearDiscount" => CurveInterpolation.LogLinearDiscount,
        _ => CurveInterpolation.MonotoneCubicZero,
    };

    internal static List<(ICurveInstrument Instrument, double Quote)> EurQuotes() =>
        DepositQuotes.Select(q => ((ICurveInstrument)Deposit.FromTenor(Trade, Period.Parse(q.Tenor), TargetCalendar.Instance), q.Rate))
            .Concat(SwapQuotes.Select(q => ((ICurveInstrument)VanillaSwap.FromTenor(Trade, Period.Parse(q.Tenor), SwapConventions.EurSixMonth), q.Rate)))
            .ToList();

    internal static PiecewiseYieldCurveBuilder Builder(string interpolation, IEnumerable<(ICurveInstrument Instrument, double Quote)> quotes)
    {
        var builder = new PiecewiseYieldCurveBuilder(Trade, DayCounter.Actual365Fixed).WithInterpolation(Interpolation(interpolation));
        foreach (var (instrument, quote) in quotes) builder.Add(instrument, quote);
        return builder;
    }

    [Theory]
    [MemberData(nameof(Interpolations))]
    public void RepricesEveryEurInstrument(string interpolation)
    {
        var quotes = EurQuotes();
        var result = Builder(interpolation, quotes).Bootstrap();
        Assert.Equal(quotes.Count, result.Curve.PillarTimes.Count);
        foreach (var (instrument, quote) in quotes)
            Assert.True(Math.Abs(instrument.ImpliedRate(result.Curve) - quote) <= 1e-10,
                $"{interpolation}: {instrument.MaturityDate} implies {instrument.ImpliedRate(result.Curve)} for quote {quote}");
        Assert.True(result.MaxRepricingError <= 1e-10);
        // A local interpolation is exact after one sequential sweep; the cubic needs global iteration.
        if (Interpolation(interpolation).IsLocal)
            Assert.Equal(1, result.Passes);
        else
            Assert.InRange(result.Passes, 3, 100);
    }

    [Theory]
    [MemberData(nameof(Interpolations))]
    public void RecoversAFlatCurveFromItsOwnQuotes(string interpolation)
    {
        // Quotes implied by a 3% continuous flat curve must bootstrap back to it: every pillar zero rate is 3%, and since
        // all three interpolations reproduce a constant zero rate, the whole curve is flat (to the Brent tolerance).
        var flat = new FlatForward(Trade, 0.03, DayCounter.Actual365Fixed);
        var quotes = EurQuotes().Select(q => (q.Instrument, q.Instrument.ImpliedRate(flat))).ToList();
        var curve = Builder(interpolation, quotes).Build();
        foreach (var t in curve.PillarTimes)
            Assert.Equal(0.03, curve.ZeroRate(t), 12);
        foreach (var t in new[] { 0.001, 0.3, 1.7, 9.9, 29.0, 35.0 })
        {
            Assert.Equal(Math.Exp(-0.03 * t), curve.DiscountFactor(t), 12);
            Assert.Equal(0.03, curve.InstantaneousForward(t), 10);
        }
    }

    [Fact]
    public void MonotoneCubicForwardCurveHasNoSpikes()
    {
        // Sanity of the forward curve shape: quotes lie in [2.28%, 2.90%]; daily instantaneous forwards to 32Y must stay
        // positive and within a plausible band, and move smoothly (C0 forward curve, so no day-to-day jump beyond the
        // drift of a smooth curve) except at the first pillar, where the flat front stub joins the cubic.
        var curve = Builder("MonotoneCubicZero", EurQuotes()).Build();
        var firstPillar = curve.PillarTimes[0];
        double min = double.MaxValue, max = double.MinValue, maxJump = 0;
        var previous = curve.InstantaneousForward(0);
        for (var day = 1; day <= 32 * 365; day++)
        {
            var t = day / 365.0;
            var f = curve.InstantaneousForward(t);
            min = Math.Min(min, f);
            max = Math.Max(max, f);
            if (t - 1 / 365.0 > firstPillar) maxJump = Math.Max(maxJump, Math.Abs(f - previous));
            previous = f;
        }
        Assert.True(min > 0.012, $"min forward {min}");
        Assert.True(max < 0.035, $"max forward {max}");
        Assert.True(maxJump < 2e-4, $"max daily forward move {maxJump}");
        // The piecewise-linear zero curve, by contrast, has forward jumps at the pillars.
        var linear = Builder("LinearZero", EurQuotes()).Build();
        var linearJump = linear.PillarTimes.Skip(1).SkipLast(1)
            .Max(t => Math.Abs(linear.InstantaneousForward(t + 1e-9) - linear.InstantaneousForward(t - 1e-9)));
        Assert.True(linearJump > maxJump);
    }

    [Fact]
    public void InterpolationsAgreeAtSwapPillars()
    {
        // Different interpolations change the curve between pillars, hence the pillar values the swaps imply, but only by
        // about a basis point on a smooth set of quotes (0.2bp observed).
        var curves = new[] { "LinearZero", "LogLinearDiscount", "MonotoneCubicZero" }.Select(i => Builder(i, EurQuotes()).Build()).ToArray();
        foreach (var t in curves[0].PillarTimes)
        {
            var zeros = curves.Select(c => c.ZeroRate(t)).ToArray();
            Assert.True(zeros.Max() - zeros.Min() < 1e-4, $"pillar {t}: {string.Join(", ", zeros)}");
        }
    }

    [Theory]
    [MemberData(nameof(Interpolations))]
    public void RepricesASetWithFras(string interpolation)
    {
        var calendar = TargetCalendar.Instance;
        var quotes = new List<(ICurveInstrument, double)>
        {
            (Deposit.FromTenor(Trade, Period.Parse("1M"), calendar), 0.0285),
            (Deposit.FromTenor(Trade, Period.Parse("3M"), calendar), 0.0275),
            (ForwardRateAgreement.FromTenors(Trade, Period.Parse("3M"), Period.Parse("3M"), calendar), 0.0262),
            (ForwardRateAgreement.FromTenors(Trade, Period.Parse("6M"), Period.Parse("3M"), calendar), 0.0248),
            (ForwardRateAgreement.FromTenors(Trade, Period.Parse("9M"), Period.Parse("3M"), calendar), 0.0236),
            (VanillaSwap.FromTenor(Trade, Period.Parse("2Y"), SwapConventions.EurSixMonth), 0.0230),
            (VanillaSwap.FromTenor(Trade, Period.Parse("5Y"), SwapConventions.EurSixMonth), 0.0233),
            (VanillaSwap.FromTenor(Trade, Period.Parse("10Y"), SwapConventions.EurSixMonth), 0.0250),
        };
        var curve = Builder(interpolation, quotes).Build();
        foreach (var (instrument, quote) in quotes)
            Assert.True(Math.Abs(instrument.ImpliedRate(curve) - quote) <= 1e-10);
    }

    [Fact]
    public void InputOrderDoesNotMatter()
    {
        var quotes = EurQuotes();
        var sorted = Builder("MonotoneCubicZero", quotes).Build();
        var shuffled = Builder("MonotoneCubicZero", quotes.AsEnumerable().Reverse()).Build();
        Assert.Equal(sorted.DiscountFactors, shuffled.DiscountFactors);
    }

    [Fact]
    public void RejectsInconsistentInputs()
    {
        var builder = new PiecewiseYieldCurveBuilder(Trade, DayCounter.Actual365Fixed);
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        var deposit = Deposit.FromTenor(Trade, Period.Parse("3M"), TargetCalendar.Instance);
        builder.Add(deposit, 0.0275).Add(new Deposit(D.Of("2025-02-17"), deposit.EndDate, DayCounter.Actual360), 0.028);
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        var stale = new PiecewiseYieldCurveBuilder(Trade, DayCounter.Actual365Fixed)
            .Add(new Deposit(D.Of("2025-01-10"), D.Of("2025-01-14"), DayCounter.Actual360), 0.03);
        Assert.Throws<InvalidOperationException>(() => stale.Build());
    }
}
