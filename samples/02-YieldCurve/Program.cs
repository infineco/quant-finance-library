using System;
using InFine.QuantFinance.Market;

// Sample 2: bootstrap a EUR-like single curve from deposits and swaps, print discount factors and rates, then price a
// fixed-rate bond and a vanilla swap on it. Dates are real: TARGET calendar, Actual/360 deposits, 30/360 fixed swap legs.
// The quotes are illustrative, not market data.

var trade = new DateOnly(2026, 3, 16);
IHolidayCalendar target = TargetCalendar.Instance;
SwapConventions eur = SwapConventions.EurSixMonth;

var builder = new PiecewiseYieldCurveBuilder(trade, DayCounter.Actual365Fixed)
    .WithInterpolation(CurveInterpolation.LogLinearDiscount);

(string Tenor, double Quote)[] deposits = { ("1M", 0.0198), ("3M", 0.0205), ("6M", 0.0212) };
(string Tenor, double Quote)[] swaps =
{
    ("1Y", 0.0221), ("2Y", 0.0238), ("3Y", 0.0251), ("5Y", 0.0269), ("7Y", 0.0281), ("10Y", 0.0294), ("15Y", 0.0305),
};

Console.WriteLine($"Trade date {trade:yyyy-MM-dd} ({trade.DayOfWeek}), spot {Spot(trade, target):yyyy-MM-dd}, calendar {target.Name}");
Console.WriteLine();
Console.WriteLine("== Instruments ==");
Console.WriteLine($"{"Instrument",-14}{"Start",12}{"Maturity",12}{"Quote",9}");
foreach (var (tenor, quote) in deposits)
{
    var deposit = Deposit.FromTenor(trade, Period.Parse(tenor), target);
    builder.Add(deposit, quote);
    Console.WriteLine($"{"Deposit " + tenor,-14}{deposit.StartDate,12:yyyy-MM-dd}{deposit.MaturityDate,12:yyyy-MM-dd}{Pct(quote, 3),9}");
}
foreach (var (tenor, quote) in swaps)
{
    var swap = VanillaSwap.FromTenor(trade, Period.Parse(tenor), eur);
    builder.Add(swap, quote);
    Console.WriteLine($"{"Swap " + tenor,-14}{swap.StartDate,12:yyyy-MM-dd}{swap.MaturityDate,12:yyyy-MM-dd}{Pct(quote, 3),9}");
}

BootstrapResult result = builder.Bootstrap();
InterpolatedDiscountCurve curve = result.Curve;
Console.WriteLine();
Console.WriteLine($"Bootstrap: {result.Passes} pass(es), max repricing error {result.MaxRepricingError:0.00E+0} (in rate)");

Console.WriteLine();
Console.WriteLine("== Curve (log-linear discount factors) ==");
Console.WriteLine($"{"Pillar",12}{"t (yrs)",9}{"DF",11}{"Zero cont.",12}{"Zero annual",13}{"Fwd 1y cont.",14}");
for (int i = 0; i < curve.PillarTimes.Count; i++)
{
    double t = curve.PillarTimes[i];
    DateOnly date = curve.PillarDates![i];
    Console.WriteLine($"{date,12:yyyy-MM-dd}{t,9:F3}{curve.DiscountFactor(t),11:F6}{Pct(curve.ZeroRate(t), 4),12}" +
                      $"{Pct(curve.ZeroRate(t, Compounding.Annual), 4),13}{Pct(curve.ForwardRate(t, t + 1.0), 4),14}");
}

// ---- Fixed-rate bond: 2.50% annual, Actual/Actual ICMA, issued 2023-05-15, maturing 2033-05-15, T+2 settlement. ----
var bond = new FixedRateBond(new DateOnly(2023, 5, 15), new DateOnly(2033, 5, 15), 0.025, Frequency.Annual,
    DayCounter.ActualActualIcma, target, BusinessDayConvention.Following, settlementDays: 2);
DateOnly settlement = bond.SettlementDate(trade);
Console.WriteLine();
Console.WriteLine($"== Bond: 2.50% annual {bond.DayCounter.Name}, maturity {bond.MaturityDate:yyyy-MM-dd}, settlement {settlement:yyyy-MM-dd} ==");
double accrued = bond.AccruedInterest(settlement);
double dirty = bond.DirtyPrice(curve);
double clean = bond.CleanPrice(curve);
double ytm = bond.YieldFromCleanPrice(clean, settlement);
Console.WriteLine($"Priced on the curve:  clean {clean:F4}  dirty {dirty:F4}  accrued {accrued:F4}");
Console.WriteLine($"Yield (annual, from clean price) {Pct(ytm, 4)}");
Console.WriteLine($"Macaulay duration {bond.MacaulayDuration(ytm, settlement):F4}  modified duration {bond.ModifiedDuration(ytm, settlement):F4}  " +
                  $"convexity {bond.Convexity(ytm, settlement):F3}  DV01 {bond.Dv01(ytm, settlement):F4} per 100");
Console.WriteLine($"Effective duration on the curve {bond.EffectiveDuration(curve):F4}  Z-spread at the curve price {Clean(bond.ZSpread(clean, curve) * 1e4):F3} bp");

const double marketClean = 97.50;
double marketYield = bond.YieldFromCleanPrice(marketClean, settlement);
Console.WriteLine($"At a market clean price of {marketClean:F2}: yield {Pct(marketYield, 4)}, Z-spread {bond.ZSpread(marketClean, curve) * 1e4:F1} bp over the curve");

// ---- Vanilla swap: 5Y payer, fixed 2.50% against 6M, notional 10 million. ----
var payer = VanillaSwap.FromTenor(trade, Period.Parse("5Y"), eur, fixedRate: 0.025, notional: 10_000_000.0);
Console.WriteLine();
Console.WriteLine($"== Swap: 5Y payer 2.50% on 10,000,000, {payer.StartDate:yyyy-MM-dd} to {payer.MaturityDate:yyyy-MM-dd} ==");
Console.WriteLine($"Par rate {Pct(payer.ParRate(curve), 4)}  NPV {payer.Npv(curve):N2}  PV01 (annuity x 1bp) {payer.Pv01(curve):N2}  DV01 {payer.Dv01(curve):N2}");
Console.WriteLine();
Console.WriteLine($"{"Fixed leg",-12}{"Accrual end",13}{"Year frac",11}{"Coupon",14}{"DF",11}");
foreach (var flow in payer.FixedLegCashFlows())
    Console.WriteLine($"{"",-12}{flow.AccrualEnd,13:yyyy-MM-dd}{flow.YearFraction,11:F6}{flow.Amount,14:N2}{curve.DiscountFactor(flow.PaymentDate),11:F6}");

static DateOnly Spot(DateOnly trade, IHolidayCalendar calendar) => calendar.Advance(trade, 2);

static string Pct(double x, int decimals) => (x * 100.0).ToString("F" + decimals) + "%";

// Avoids printing "-0.000" for a value that is zero up to the solver tolerance.
static double Clean(double x) => Math.Abs(x) < 5e-4 ? 0.0 : x;
