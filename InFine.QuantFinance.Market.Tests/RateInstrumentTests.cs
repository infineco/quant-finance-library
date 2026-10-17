using System;
using System.Linq;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

/// <summary>
/// Deposit, FRA and swap pricing on a flat curve, against values recomputed by hand from the dates (written out in the
/// comments) and the closed form <c>P(t) = exp(-r t)</c>, t = actual days / 365.
/// </summary>
public class RateInstrumentTests
{
    private const double R = 0.03;
    private static readonly DateOnly Trade = D.Of("2025-01-15"); // Wednesday; TARGET spot = Friday 17 Jan 2025
    private static readonly FlatForward Flat = new(Trade, R, DayCounter.Actual365Fixed);

    private static double P(string iso) => Math.Exp(-R * (D.Of(iso).DayNumber - Trade.DayNumber) / 365.0);

    [Fact]
    public void DepositDatesAndImpliedRate()
    {
        var deposit = Deposit.FromTenor(Trade, Period.Parse("3M"), TargetCalendar.Instance);
        Assert.Equal(D.Of("2025-01-17"), deposit.StartDate);
        Assert.Equal(D.Of("2025-04-17"), deposit.EndDate); // Thursday before Good Friday 2025
        Assert.Equal(90 / 360.0, deposit.YearFraction, 15);
        Assert.Equal((P("2025-01-17") / P("2025-04-17") - 1) / (90 / 360.0), deposit.ImpliedRate(Flat), 14);
        // 1M lands on Mon 17 Feb; 1W on Fri 24 Jan.
        Assert.Equal(D.Of("2025-02-17"), Deposit.FromTenor(Trade, Period.Parse("1M"), TargetCalendar.Instance).EndDate);
        Assert.Equal(D.Of("2025-01-24"), Deposit.FromTenor(Trade, Period.Parse("1W"), TargetCalendar.Instance).EndDate);
        Assert.Throws<ArgumentException>(() => new Deposit(D.Of("2025-02-01"), D.Of("2025-02-01"), DayCounter.Actual360));
    }

    [Fact]
    public void FraDatesAndImpliedRate()
    {
        var fra = ForwardRateAgreement.FromTenors(Trade, Period.Parse("3M"), Period.Parse("6M"), TargetCalendar.Instance);
        Assert.Equal(D.Of("2025-04-17"), fra.StartDate);
        Assert.Equal(D.Of("2025-10-17"), fra.EndDate); // Friday
        Assert.Equal(183 / 360.0, fra.YearFraction, 15);
        Assert.Equal((P("2025-04-17") / P("2025-10-17") - 1) / (183 / 360.0), fra.ImpliedRate(Flat), 14);
        Assert.Equal(fra.ImpliedRate(Flat), Flat.ForwardRate(fra.StartDate, fra.EndDate, DayCounter.Actual360), 15);
    }

    private static VanillaSwap FiveYear(double fixedRate = 0.0, double spread = 0.0, double notional = 1.0,
        SwapDirection direction = SwapDirection.Payer) =>
        VanillaSwap.FromTenor(Trade, Period.Parse("5Y"), SwapConventions.EurSixMonth, fixedRate, spread, notional, direction);

    [Fact]
    public void FiveYearSwapFixedLegByHand()
    {
        var swap = FiveYear();
        // 17 Jan 2026 is a Saturday -> Mon 19; 17 Jan 2027 a Sunday -> Mon 18; 2028-2030 are weekdays.
        var ends = new[] { "2026-01-19", "2027-01-18", "2028-01-17", "2029-01-17", "2030-01-17" };
        Assert.Equal(ends.Select(D.Of), swap.FixedPeriods.Select(p => p.End));
        // 30/360: 17 Jan 25 -> 19 Jan 26 = 362; -> 18 Jan 27 = 359; -> 17 Jan 28 = 359; then 360, 360.
        var tau = new[] { 362, 359, 359, 360, 360 }.Select(d => d / 360.0).ToArray();
        Assert.Equal(tau, swap.FixedPeriods.Select(p => p.YearFraction).ToArray(), new ToleranceComparer(1e-15));
        var annuity = tau.Select((t, i) => t * P(ends[i])).Sum();
        Assert.Equal(annuity, swap.FixedLegAnnuity(Flat), 14);
        Assert.Equal(10, swap.FloatingPeriods.Count);
        Assert.Equal(D.Of("2025-01-17"), swap.StartDate);
        Assert.Equal(D.Of("2030-01-17"), swap.MaturityDate);
        // Single curve: the floating leg telescopes to P(start) - P(end).
        Assert.Equal(P("2025-01-17") - P("2030-01-17"), swap.FloatingLegPresentValue(Flat), 14);
        Assert.Equal((P("2025-01-17") - P("2030-01-17")) / annuity, swap.ParRate(Flat), 14);
    }

    [Fact]
    public void SwapValueAroundPar()
    {
        var par = FiveYear().ParRate(Flat);
        var annuity = FiveYear().FixedLegAnnuity(Flat);
        Assert.Equal(0.0, FiveYear(par).Npv(Flat), 15);
        // A payer of par + 1bp on 10m loses 1bp x annuity x notional; the receiver gains it.
        Assert.Equal(-1e-4 * annuity * 1e7, FiveYear(par + 1e-4, notional: 1e7).Npv(Flat), 7);
        Assert.Equal(1e-4 * annuity * 1e7, FiveYear(par + 1e-4, notional: 1e7, direction: SwapDirection.Receiver).Npv(Flat), 7);
        // A floating spread s moves the par rate by s x floating annuity / fixed annuity.
        var withSpread = FiveYear(spread: 0.001);
        Assert.Equal(par + 0.001 * withSpread.FloatingLegAnnuity(Flat) / annuity, withSpread.ParRate(Flat), 15);
    }

    [Fact]
    public void TwoCurvePricingSeparatesDiscountingFromForecasting()
    {
        // Forecasting on a 50bp higher flat curve raises each forward by about 50bp, while discounting is unchanged.
        var forecast = new FlatForward(Trade, R + 0.005, DayCounter.Actual365Fixed);
        var swap = FiveYear();
        var expected = swap.FloatingPeriods.Sum(p =>
            (Math.Exp(0.035 * (p.End.DayNumber - p.Start.DayNumber) / 365.0) - 1) * Flat.DiscountFactor(p.End));
        Assert.Equal(expected, swap.FloatingLegPresentValue(Flat, forecast), 14);
        Assert.True(swap.ParRate(Flat, forecast) > swap.ParRate(Flat) + 0.004);
    }

    private sealed class ToleranceComparer(double tolerance) : System.Collections.Generic.IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tolerance;
        public int GetHashCode(double obj) => 0;
    }
}
