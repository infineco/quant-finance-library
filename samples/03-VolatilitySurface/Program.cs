using System;
using System.Collections.Generic;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Market;
using InFine.QuantFinance.Volatility;

// Sample 3: fit the SVI parametrisation to noisy synthetic smiles at four expiries, check each slice for butterfly
// arbitrage and each pair of expiries for calendar arbitrage, assemble a surface and read implied volatilities and a price
// from it. The "market" is generated from a known SVI per expiry plus +-0.15 volatility points of deterministic noise.

const double spot = 100.0, rate = 0.02, dividendYield = 0.01;
var valuation = new DateOnly(2026, 3, 16);
var discountCurve = new FlatForward(valuation, rate, DayCounter.Actual365Fixed);
var dividendCurve = new FlatForward(valuation, dividendYield, DayCounter.Actual365Fixed);
var forwards = new SpotForwardCurve(spot, discountCurve, dividendCurve);

double[] maturities = { 0.25, 0.5, 1.0, 2.0 };
var rng = new Random(12345);
var calibrations = new List<SviCalibrationResult>();

Console.WriteLine($"== SVI fit per expiry (noise +-0.15 vol points, 17 strikes from 67% to 149% of the forward) ==");
Console.WriteLine();
Console.WriteLine($"{"T",5}{"a",10}{"b",9}{"rho",9}{"m",9}{"sigma",9}{"rmse(w)",11}{"max|dvol|",11}{"iters",7}  butterfly");
foreach (double t in maturities)
{
    SviRawParameters truth = TrueSlice(t);
    double forward = forwards.Forward(t);
    var logMoneyness = new List<double>();
    var strikes = new List<double>();
    var vols = new List<double>();
    for (int i = -8; i <= 8; i++)
    {
        double k = 0.05 * i;
        double vol = Math.Sqrt(truth.TotalVariance(k) / t) + (rng.NextDouble() - 0.5) * 0.003;
        logMoneyness.Add(k);
        strikes.Add(forward * Math.Exp(k));
        vols.Add(vol);
    }

    var quotes = new SmileQuotes(t, forward, strikes, vols);
    SviCalibrationResult fit = SviCalibrator.Calibrate(quotes);
    calibrations.Add(fit);

    ButterflyCheckResult butterfly = ArbitrageChecks.CheckButterfly(fit.Parameters, ArbitrageChecks.Grid(-1.0, 1.0, 401));
    SviRawParameters p = fit.Parameters;
    Console.WriteLine($"{t,5:F2}{p.A,10:F5}{p.B,9:F4}{p.Rho,9:F4}{p.M,9:F4}{p.Sigma,9:F4}{fit.RootMeanSquareTotalVarianceError,11:0.00E+0}" +
                      $"{fit.MaxAbsoluteVolatilityError,11:0.00E+0}{fit.Iterations,7}  {(butterfly.IsArbitrageFree ? "free" : "VIOLATED")} (min g = {butterfly.MinimumDensity:F3})");
}

// A slice that is a valid SVI (wing slopes below 2) but not arbitrage-free: a very sharp kink with steep wings.
var bad = new SviRawParameters(a: -0.02, b: 0.9, rho: -0.5, m: 0.0, sigma: 0.03);
ButterflyCheckResult badCheck = ArbitrageChecks.CheckButterfly(bad, ArbitrageChecks.Grid(-1.0, 1.0, 401));
Console.WriteLine();
Console.WriteLine($"Counter-example {bad}: admissible wings = {bad.HasAdmissibleWings}, butterfly free = {badCheck.IsArbitrageFree}, " +
                  $"{badCheck.Violations.Count} violating grid points, min g = {badCheck.MinimumDensity:F3} at k = {badCheck.ArgMinimum:F2}");

SviVolatilitySurface surface = SviVolatilitySurface.FromCalibrations(forwards, calibrations);

Console.WriteLine();
Console.WriteLine("== Calendar arbitrage between consecutive expiries (total variance must not decrease) ==");
IReadOnlyList<CalendarCheckResult> calendar = surface.CheckCalendar(ArbitrageChecks.Grid(-1.0, 1.0, 201));
for (int i = 0; i < calendar.Count; i++)
    Console.WriteLine($"T {surface.Maturities[i]:F2} -> {surface.Maturities[i + 1]:F2}: {(calendar[i].IsArbitrageFree ? "free" : "VIOLATED")} " +
                      $"(min w_later - w_earlier = {calendar[i].MinimumDifference:F5})");

Console.WriteLine();
Console.WriteLine("== Implied volatility surface (columns: expiry; * = interpolated in total variance between fitted expiries) ==");
double[] queryMaturities = { 0.25, 0.375, 0.5, 0.75, 1.0, 1.5, 2.0 };
double[] moneyness = { 0.80, 0.90, 0.95, 1.00, 1.05, 1.10, 1.20 };
Console.WriteLine();
Console.Write($"{"K/F",6}");
foreach (double t in queryMaturities)
    Console.Write($"{t,8:F3}{(Array.IndexOf(maturities, t) < 0 ? "*" : " ")}");
Console.WriteLine();
foreach (double m in moneyness)
{
    Console.Write($"{m,6:F2}");
    foreach (double t in queryMaturities)
        Console.Write($"{surface.ImpliedVolatility(t, m * forwards.Forward(t)) * 100.0,8:F2}%");
    Console.WriteLine();
}

double expiry = 0.75, strike = 105.0;
double discount = discountCurve.DiscountFactor(expiry);
double callPrice = surface.Price(OptionType.Call, expiry, strike, discount);
double vol75 = surface.ImpliedVolatility(expiry, strike);
Console.WriteLine();
Console.WriteLine($"Call T={expiry:F2} K={strike:F0}: vol {vol75 * 100.0:F2}%, price {callPrice:F4} " +
                  $"(Black-76 on forward {forwards.Forward(expiry):F4}, discount factor {discount:F6})");

// The data-generating SVI of an expiry: total variance grows with T through a (level) and b (slope of the wings).
static SviRawParameters TrueSlice(double t) =>
    new(a: 0.012 * t, b: 0.09 * Math.Pow(t, 0.75), rho: -0.6, m: 0.02, sigma: 0.25);
