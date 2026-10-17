using System;
using InFine.QuantFinance.ClosedForm;

// Sample 1: price a European option, read its Greeks, recover the volatility from the price, price it again under
// Heston, and print a strike ladder. Everything is closed form or semi-analytic; no random numbers are involved.

const double spot = 100.0, strike = 100.0, maturity = 1.0, rate = 0.025, dividendYield = 0.01, sigma = 0.20;

Console.WriteLine("== Black-Scholes-Merton: S=100 K=100 T=1y r=2.5% q=1% sigma=20% ==");
Console.WriteLine();
Console.WriteLine($"{"",-12}{"Call",12}{"Put",12}");
OptionGreeks call = BlackScholes.Greeks(OptionType.Call, spot, strike, maturity, rate, dividendYield, sigma);
OptionGreeks put = BlackScholes.Greeks(OptionType.Put, spot, strike, maturity, rate, dividendYield, sigma);
Row("Price", call.Price, put.Price);
Row("Delta", call.Delta, put.Delta);
Row("Gamma", call.Gamma, put.Gamma);
Row("Vega", call.Vega, put.Vega);
Row("Theta/yr", call.Theta, put.Theta);
Row("Rho", call.Rho, put.Rho);
Row("Div. rho", call.DividendRho, put.DividendRho);
Row("Vanna", call.Vanna, put.Vanna);
Row("Volga", call.Volga, put.Volga);

// Put-call parity: C - P = S exp(-qT) - K exp(-rT).
double parity = call.Price - put.Price - (spot * Math.Exp(-dividendYield * maturity) - strike * Math.Exp(-rate * maturity));
Console.WriteLine();
Console.WriteLine($"Put-call parity residual: {parity:0.00E+0}");

Console.WriteLine();
Console.WriteLine("== Implied volatility round trip ==");
double impliedCall = ImpliedVolatility.BlackScholes(spot, strike, maturity, rate, dividendYield, call.Price, OptionType.Call);
double impliedPut = ImpliedVolatility.BlackScholes(spot, strike, maturity, rate, dividendYield, put.Price, OptionType.Put);
Console.WriteLine($"input sigma {sigma:F10}; implied from the call {impliedCall:F10}; from the put {impliedPut:F10}");
Console.WriteLine($"max |error| = {Math.Max(Math.Abs(impliedCall - sigma), Math.Abs(impliedPut - sigma)):0.00E+0}");

Console.WriteLine();
Console.WriteLine("== Heston (COS method) on the same option ==");
// Variance starts at sigma^2 and mean-reverts to it; negative spot/variance correlation gives a skew.
var heston = new HestonParameters(V0: sigma * sigma, Kappa: 2.0, Theta: sigma * sigma, Xi: 0.4, Rho: -0.7);
double forward = spot * Math.Exp((rate - dividendYield) * maturity);
double discount = Math.Exp(-rate * maturity);
Console.WriteLine($"Feller ratio {heston.FellerRatio:F2} (variance stays positive: {heston.SatisfiesFeller})");
double hestonCall = Heston.Price(OptionType.Call, forward, strike, discount, maturity, heston);
double hestonPut = Heston.Price(OptionType.Put, forward, strike, discount, maturity, heston);
double hestonVol = ImpliedVolatility.Black76(forward, strike, maturity, discount, hestonCall, OptionType.Call);
Console.WriteLine($"Heston call {hestonCall:F4} (BSM {call.Price:F4}); put {hestonPut:F4} (BSM {put.Price:F4}); implied vol {Pct(hestonVol)}");

Console.WriteLine();
Console.WriteLine("== Strike ladder, 1y calls: Black-Scholes at 20% vs Heston, with Heston implied vol ==");
Console.WriteLine();
Console.WriteLine($"{"Strike",8}{"BSM price",12}{"Heston",12}{"Heston IV",12}{"BSM delta",12}{"BSM vega",12}");
for (double k = 80.0; k <= 120.0; k += 5.0)
{
    OptionGreeks bsm = BlackScholes.Greeks(OptionType.Call, spot, k, maturity, rate, dividendYield, sigma);
    double h = Heston.Price(OptionType.Call, forward, k, discount, maturity, heston);
    double iv = ImpliedVolatility.Black76(forward, k, maturity, discount, h, OptionType.Call);
    Console.WriteLine($"{k,8:F1}{bsm.Price,12:F4}{h,12:F4}{Pct(iv),12}{bsm.Delta,12:F4}{bsm.Vega,12:F4}");
}

static void Row(string name, double call, double put) => Console.WriteLine($"{name,-12}{call,12:F6}{put,12:F6}");

static string Pct(double x) => (x * 100.0).ToString("F2") + "%";
