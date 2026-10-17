using System;
using System.Linq;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

/// <summary>
/// Swap extensions: re-pricing of the bootstrap inputs, cash-flow tables, PV01 and DV01, par spread and forward-starting swaps.
/// </summary>
public class SwapTests
{
    private static readonly DateOnly Trade = D.Of("2025-01-15");
    private static readonly InterpolatedDiscountCurve Curve = BondTests.BuildEurCurve(Trade);
    private static readonly SwapConventions Eur = SwapConventions.EurSixMonth;

    private static void Near(double expected, double actual, double tolerance, string? what = null) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}expected {expected:R}, got {actual:R} (tolerance {tolerance})");

    [Fact]
    public void BootstrapSwapsRepriceToParAsPayerAndReceiver()
    {
        foreach (var (tenor, rate) in BondTests.SwapQuotes)
        {
            var payer = VanillaSwap.FromTenor(Trade, Period.Parse(tenor), Eur, rate);
            var receiver = VanillaSwap.FromTenor(Trade, Period.Parse(tenor), Eur, rate, direction: SwapDirection.Receiver);
            Near(0.0, payer.Npv(Curve), 1e-10, $"{tenor} payer: ");
            Near(0.0, receiver.Npv(Curve), 1e-10, $"{tenor} receiver: ");
            Near(rate, payer.ParRate(Curve), 1e-10, $"{tenor} par rate: ");
        }
    }

    [Fact]
    public void NpvIsLinearInTheFixedRateAndAntisymmetricInDirection()
    {
        var swap = VanillaSwap.FromTenor(Trade, Period.Parse("7Y"), Eur, 0.03, notional: 5e6);
        var receiver = VanillaSwap.FromTenor(Trade, Period.Parse("7Y"), Eur, 0.03, notional: 5e6, direction: SwapDirection.Receiver);
        Near(swap.Npv(Curve), -receiver.Npv(Curve), 1e-9);
        // Payer value N (par - K) A.
        Near(5e6 * (swap.ParRate(Curve) - 0.03) * swap.FixedLegAnnuity(Curve), swap.Npv(Curve), 1e-8);
    }

    [Fact]
    public void Pv01IsTheValueOfABasisPointOfFixedRate()
    {
        foreach (var direction in new[] { SwapDirection.Payer, SwapDirection.Receiver })
        {
            VanillaSwap Make(double k) => VanillaSwap.FromTenor(Trade, Period.Parse("10Y"), Eur, k, 0.0005, 2.5e7, direction);
            var swap = Make(0.025);
            var bumped = Make(0.025 + 1e-4);
            var change = bumped.Npv(Curve) - swap.Npv(Curve);
            // A payer loses Pv01 when the fixed rate it pays rises by 1bp; a receiver gains it. Exact: the NPV is linear in K.
            var expected = direction == SwapDirection.Payer ? -swap.Pv01(Curve) : swap.Pv01(Curve);
            Near(expected, change, 1e-8);
        }
        var s = VanillaSwap.FromTenor(Trade, Period.Parse("10Y"), Eur, 0.025, notional: 1e6);
        Near(1e6 * s.FixedLegAnnuity(Curve) * 1e-4, s.Pv01(Curve), 1e-9);
    }

    [Fact]
    public void Dv01MatchesTheAnalyticDerivativeOfTheTelescopedSwapValue()
    {
        // Single curve: floating leg = P(T0) - P(Tn) + spread A_float, so with a parallel shift s of the continuous zero
        // rates, dP(T)/ds = -T P(T) and  dNPV/ds = N [ -T0 P(T0) + Tn P(Tn) - spread sum tau T P + K sum tau T P ] (payer).
        const double k = 0.027, spread = 0.0008, notional = 1e7;
        var swap = VanillaSwap.FromTenor(Trade, Period.Parse("8Y"), Eur, k, spread, notional);
        double T(DateOnly d) => Curve.TimeFromReference(d);
        double P(DateOnly d) => Curve.DiscountFactor(d);
        var t0 = swap.FloatingPeriods[0].Start;
        var tn = swap.FloatingPeriods[^1].End;
        var floatingSensitivity = -T(t0) * P(t0) + T(tn) * P(tn);
        var spreadTerm = -spread * swap.FloatingPeriods.Sum(p => p.YearFraction * T(p.End) * P(p.End));
        var fixedTerm = k * swap.FixedPeriods.Sum(p => p.YearFraction * T(p.End) * P(p.End));
        var analytic = notional * (floatingSensitivity + spreadTerm + fixedTerm) * 1e-4;
        Assert.True(Math.Abs(swap.Dv01(Curve) / analytic - 1) < 1e-6, $"{swap.Dv01(Curve)} vs {analytic}");
        var receiver = VanillaSwap.FromTenor(Trade, Period.Parse("8Y"), Eur, k, spread, notional, SwapDirection.Receiver);
        Near(-swap.Dv01(Curve), receiver.Dv01(Curve), 1e-9);
        // Naive check of the bump itself: a payer's NPV rises when rates rise.
        Assert.True(swap.Dv01(Curve) > 0 || k < swap.ParRate(Curve));
    }

    [Fact]
    public void Dv01IsMultiCurveAwareAndConsistentAcrossEqualCurves()
    {
        var swap = VanillaSwap.FromTenor(Trade, Period.Parse("5Y"), Eur, 0.025, notional: 1e6);
        var copy = BondTests.BuildEurCurve(Trade);
        Near(swap.Dv01(Curve), swap.Dv01(Curve, copy), 1e-9);
        // Bumping only the forecast curve moves the floating leg alone: the two curve sensitivities sum to the total.
        var flat = new FlatForward(Trade, 0.021, DayCounter.Actual365Fixed);
        var discountOnly = Bump(swap, Curve, forecastBump: 0.0, discountBump: 1e-4);
        var forecastOnly = Bump(swap, Curve, forecastBump: 1e-4, discountBump: 0.0);
        Assert.True(forecastOnly > 0, "a payer gains when the forecast rates rise");
        Near(swap.Dv01(Curve), discountOnly + forecastOnly, 1e-4 * 1e-3 * Math.Abs(swap.Dv01(Curve)) + 1e-6);
        Near(0.0, swap.Dv01(flat, flat) - swap.Dv01(flat), 1e-12);
    }

    private static double Bump(VanillaSwap swap, IYieldCurve curve, double forecastBump, double discountBump)
    {
        double Npv(double d, double f) => swap.Npv(ZeroShiftedCurve.Parallel(curve, d), ZeroShiftedCurve.Parallel(curve, f));
        return (Npv(discountBump, forecastBump) - Npv(-discountBump, -forecastBump)) / 2.0;
    }

    [Fact]
    public void CashFlowTablesDiscountToTheLegValues()
    {
        var swap = VanillaSwap.FromTenor(Trade, Period.Parse("4Y"), Eur, 0.0275, 0.001, 2e6);
        var fixedFlows = swap.FixedLegCashFlows();
        var floatingFlows = swap.FloatingLegCashFlows(Curve);
        Assert.Equal(swap.FixedPeriods.Count, fixedFlows.Count);
        Assert.Equal(swap.FloatingPeriods.Count, floatingFlows.Count);
        Assert.Equal(swap.FixedPeriods.Select(p => p.End), fixedFlows.Select(f => f.PaymentDate));
        Near(2e6 * 0.0275 * swap.FixedLegAnnuity(Curve), fixedFlows.Sum(f => f.Amount * Curve.DiscountFactor(f.PaymentDate)), 1e-8);
        Near(2e6 * swap.FloatingLegPresentValue(Curve), floatingFlows.Sum(f => f.Amount * Curve.DiscountFactor(f.PaymentDate)), 1e-8);
        Assert.All(fixedFlows, f => Near(f.Rate * f.YearFraction * 2e6, f.Amount, 1e-9));
        // Each floating rate is forward + spread: first period [spot, spot + 6M] by hand from the curve.
        var first = floatingFlows[0];
        var forward = (Curve.DiscountFactor(first.AccrualStart) / Curve.DiscountFactor(first.AccrualEnd) - 1) / first.YearFraction;
        Near(forward + 0.001, first.Rate, 1e-14);
        // NPV of the payer = floating table PV - fixed table PV.
        var tablePv = floatingFlows.Sum(f => f.Amount * Curve.DiscountFactor(f.PaymentDate)) - fixedFlows.Sum(f => f.Amount * Curve.DiscountFactor(f.PaymentDate));
        Near(swap.Npv(Curve), tablePv, 1e-8);
    }

    [Fact]
    public void ParSpreadZeroesTheValue()
    {
        var swap = VanillaSwap.FromTenor(Trade, Period.Parse("6Y"), Eur, 0.0265, 0.002, 1e6);
        var spread = swap.ParSpread(Curve);
        var repriced = VanillaSwap.FromTenor(Trade, Period.Parse("6Y"), Eur, 0.0265, spread, 1e6);
        Near(0.0, repriced.Npv(Curve), 1e-9);
        // A swap already at its par rate (spread included) has the quoted spread as par spread; with no spread, zero.
        var atPar = VanillaSwap.FromTenor(Trade, Period.Parse("6Y"), Eur, swap.ParRate(Curve), 0.002, 1e6);
        Near(0.002, atPar.ParSpread(Curve), 1e-13);
        var noSpread = VanillaSwap.FromTenor(Trade, Period.Parse("6Y"), Eur, swap.ParRate(Curve));
        Near(0.0, VanillaSwap.FromTenor(Trade, Period.Parse("6Y"), Eur, noSpread.ParRate(Curve)).ParSpread(Curve), 1e-13);
        // Closed form for the telescoped single curve: s = (K A_fixed - (P(T0) - P(Tn))) / A_float.
        var t0 = swap.FloatingPeriods[0].Start;
        var tn = swap.FloatingPeriods[^1].End;
        var expected = (0.0265 * swap.FixedLegAnnuity(Curve) - (Curve.DiscountFactor(t0) - Curve.DiscountFactor(tn))) / swap.FloatingLegAnnuity(Curve);
        Near(expected, spread, 1e-12);
    }

    [Fact]
    public void ForwardStartingSwapDatesAndParRate()
    {
        // Trade Wed 15 Jan 2025, spot Fri 17 Jan; 1Y forward = Sat 17 Jan 2026, rolled (ModifiedFollowing) to Mon 19 Jan.
        var forward = VanillaSwap.FromForwardTenor(Trade, Period.Parse("1Y"), Period.Parse("5Y"), Eur, 0.025);
        Assert.Equal(D.Of("2026-01-19"), forward.StartDate);
        Assert.Equal(D.Of("2031-01-20"), forward.MaturityDate); // 19 Jan 2031 is a Sunday
        Assert.Equal(5, forward.FixedPeriods.Count);
        Assert.Equal(10, forward.FloatingPeriods.Count);
        // Single-curve par rate = (P(start) - P(end)) / A_fixed.
        var expected = (Curve.DiscountFactor(forward.StartDate) - Curve.DiscountFactor(forward.MaturityDate)) / forward.FixedLegAnnuity(Curve);
        Near(expected, forward.ParRate(Curve), 1e-13);
        Near(0.0, VanillaSwap.FromForwardTenor(Trade, Period.Parse("1Y"), Period.Parse("5Y"), Eur, expected).Npv(Curve), 1e-12);
        // Zero forward start is the spot-starting swap.
        var spot = VanillaSwap.FromTenor(Trade, Period.Parse("5Y"), Eur, 0.025);
        var zero = VanillaSwap.FromForwardTenor(Trade, Period.Parse("0D"), Period.Parse("5Y"), Eur, 0.025);
        Assert.Equal(spot.StartDate, zero.StartDate);
        Assert.Equal(spot.Npv(Curve), zero.Npv(Curve));
    }

    [Fact]
    public void ForwardSwapAnnuityAndParRateDecomposeTheLongerSwap()
    {
        // Same dates: spot 1Y swap + 1Y-into-5Y forward swap = spot 6Y swap, both legs. Hence A6 S6 = A1 S1 + Af Sf,
        // exactly in a single curve (the floating legs telescope, 30/360 is additive on the same adjusted dates).
        var spot = D.Of("2025-01-17");
        var oneYear = VanillaSwap.FromDates(spot, D.Of("2026-01-17"), Eur);
        var forward = VanillaSwap.FromDates(D.Of("2026-01-17"), D.Of("2031-01-17"), Eur);
        var sixYear = VanillaSwap.FromDates(spot, D.Of("2031-01-17"), Eur);
        var lhs = sixYear.ParRate(Curve) * sixYear.FixedLegAnnuity(Curve);
        var rhs = oneYear.ParRate(Curve) * oneYear.FixedLegAnnuity(Curve) + forward.ParRate(Curve) * forward.FixedLegAnnuity(Curve);
        Near(lhs, rhs, 1e-13);
        Near(sixYear.FixedLegAnnuity(Curve), oneYear.FixedLegAnnuity(Curve) + forward.FixedLegAnnuity(Curve), 1e-13);
        Assert.Equal(D.Of("2031-01-17"), sixYear.MaturityDate);
        Assert.Equal(D.Of("2026-01-19"), forward.StartDate);
    }
}
