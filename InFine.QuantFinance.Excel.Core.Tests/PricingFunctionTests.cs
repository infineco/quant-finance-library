using System;
using InFine.QuantFinance.ClosedForm;
using Xunit;

namespace InFine.QuantFinance.Excel.Core.Tests;

// ErrorLog is process-wide, so the classes asserting on it must not run in parallel.
[Collection("QuantFunctions")]
public class PricingFunctionTests
{
    private readonly QuantFunctions _qf = new(new ObjectCache());

    private const double S = 100.0, K = 105.0, Tau = 0.75, R = 0.03, Q = 0.01, Vol = 0.22;

    [Fact]
    public void BsPriceReproducesTheTextbookValues()
    {
        // S = K = 100, T = 1, r = 5%, q = 0, vol = 20%: call 10.4506, put 5.5735 (Hull, and the README example).
        Assert.Equal(10.4506, T.Num(_qf.BsPrice("C", 100.0, 100.0, 1.0, 0.05, 0.0, 0.2)), 4);
        Assert.Equal(5.5735, T.Num(_qf.BsPrice("P", 100.0, 100.0, 1.0, 0.05, 0.0, 0.2)), 4);
    }

    [Theory]
    [InlineData("C", OptionType.Call)]
    [InlineData("Put", OptionType.Put)]
    [InlineData("1", OptionType.Call)]
    [InlineData("-1", OptionType.Put)]
    public void BsPriceMatchesTheLibrary(string type, OptionType expected)
    {
        double direct = BlackScholes.Price(expected, S, K, Tau, R, Q, Vol);
        Assert.Equal(direct, T.Num(_qf.BsPrice(type, S, K, Tau, R, Q, Vol)));
    }

    [Fact]
    public void BsPriceSatisfiesPutCallParity()
    {
        double call = T.Num(_qf.BsPrice("C", S, K, Tau, R, Q, Vol));
        double put = T.Num(_qf.BsPrice("P", S, K, Tau, R, Q, Vol));
        double forwardValue = S * Math.Exp(-Q * Tau) - K * Math.Exp(-R * Tau);
        Assert.Equal(forwardValue, call - put, 12);
    }

    [Fact]
    public void BsPriceAcceptsStringsFromTextFormattedCells()
    {
        Assert.Equal(T.Num(_qf.BsPrice("C", S, K, Tau, R, Q, Vol)), T.Num(_qf.BsPrice("C", "100", "105", "0.75", "0.03", "0.01", "0.22")));
    }

    [Fact]
    public void BsGreeksAllIsAHorizontalRowMatchingTheLibrary()
    {
        var g = BlackScholes.Greeks(OptionType.Call, S, K, Tau, R, Q, Vol);
        var row = T.Array(_qf.BsGreeks("C", S, K, Tau, R, Q, Vol));
        Assert.Equal(1, row.GetLength(0));
        Assert.Equal(10, row.GetLength(1));
        double[] expected = { g.Price, g.Delta, g.Gamma, g.Vega, g.Theta, g.Rho, g.DividendRho, g.Vanna, g.Volga, g.DualDelta };
        for (int j = 0; j < 10; j++) Assert.Equal(expected[j], Assert.IsType<double>(row[0, j]));
        // An explicit "ALL" is the default.
        Assert.Equal(row[0, 3], T.Array(_qf.BsGreeks("C", S, K, Tau, R, Q, Vol, "all"))[0, 3]);
    }

    [Fact]
    public void BsGreeksHeaderOptionAddsALabelRow()
    {
        var table = T.Array(_qf.BsGreeks("P", S, K, Tau, R, Q, Vol, "HEADER"));
        Assert.Equal(2, table.GetLength(0));
        Assert.Equal(10, table.GetLength(1));
        Assert.Equal("Price", table[0, 0]);
        Assert.Equal("Delta", table[0, 1]);
        Assert.Equal("DualDelta", table[0, 9]);
        Assert.Equal(T.Num(_qf.BsPrice("P", S, K, Tau, R, Q, Vol)), Assert.IsType<double>(table[1, 0]));
    }

    [Theory]
    [InlineData("price")]
    [InlineData("delta")]
    [InlineData("gamma")]
    [InlineData("vega")]
    [InlineData("theta")]
    [InlineData("rho")]
    [InlineData("divrho")]
    [InlineData("vanna")]
    [InlineData("volga")]
    [InlineData("dualdelta")]
    public void BsGreeksSingleGreekIsTheMatchingColumn(string greek)
    {
        var row = T.Array(_qf.BsGreeks("C", S, K, Tau, R, Q, Vol));
        var headers = T.Array(_qf.BsGreeks("C", S, K, Tau, R, Q, Vol, "HEADER"));
        int column = Array.FindIndex(HeaderRow(headers), h => string.Equals((string)h, greek, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(row[0, column], T.Num(_qf.BsGreeks("C", S, K, Tau, R, Q, Vol, greek)));
    }

    private static object[] HeaderRow(object[,] table)
    {
        var headers = new object[table.GetLength(1)];
        for (int j = 0; j < headers.Length; j++) headers[j] = table[0, j];
        return headers;
    }

    [Fact]
    public void BsGreeksAgreeWithFiniteDifferencesOfQfBsPrice()
    {
        // Independent of the analytic formulas: bump the inputs of the Excel pricing function itself.
        double Price(double s = S, double t = Tau, double r = R, double v = Vol) => T.Num(_qf.BsPrice("C", s, K, t, r, Q, v));
        double h = 1e-4 * S;
        Assert.Equal((Price(s: S + h) - Price(s: S - h)) / (2 * h), T.Num(_qf.BsGreeks("C", S, K, Tau, R, Q, Vol, "delta")), 7);
        Assert.Equal((Price(s: S + h) - 2 * Price() + Price(s: S - h)) / (h * h), T.Num(_qf.BsGreeks("C", S, K, Tau, R, Q, Vol, "gamma")), 6);
        Assert.Equal((Price(v: Vol + 1e-5) - Price(v: Vol - 1e-5)) / 2e-5, T.Num(_qf.BsGreeks("C", S, K, Tau, R, Q, Vol, "vega")), 6);
        Assert.Equal((Price(r: R + 1e-6) - Price(r: R - 1e-6)) / 2e-6, T.Num(_qf.BsGreeks("C", S, K, Tau, R, Q, Vol, "rho")), 5);
        // Theta is dV/dt = -dV/dT per year.
        Assert.Equal(-(Price(t: Tau + 1e-6) - Price(t: Tau - 1e-6)) / 2e-6, T.Num(_qf.BsGreeks("C", S, K, Tau, R, Q, Vol, "theta")), 5);
    }

    [Fact]
    public void BsGreeksRejectsAnUnknownGreekListingTheValidOnes()
    {
        Assert.Equal(XlError.Value, _qf.BsGreeks("C", S, K, Tau, R, Q, Vol, "charm"));
        Assert.Contains("Delta", ErrorLog.Last);
    }

    [Fact]
    public void Black76PriceMatchesTheLibraryAndTheBsPriceOnTheForward()
    {
        double df = Math.Exp(-R * Tau), forward = S * Math.Exp((R - Q) * Tau);
        double direct = Black76.Price(OptionType.Put, forward, K, df, Tau, Vol);
        double viaExcel = T.Num(_qf.Black76Price("P", forward, K, Tau, df, Vol));
        Assert.Equal(direct, viaExcel);
        Assert.Equal(T.Num(_qf.BsPrice("P", S, K, Tau, R, Q, Vol)), viaExcel, 12);
    }

    [Fact]
    public void ImpliedVolInvertsBsPrice()
    {
        foreach (var type in new[] { "C", "P" })
            foreach (double vol in new[] { 0.05, 0.22, 0.8 })
            {
                double price = T.Num(_qf.BsPrice(type, S, K, Tau, R, Q, vol));
                Assert.Equal(vol, T.Num(_qf.ImpliedVol(price, type, S, K, Tau, R, Q)), 9);
            }
    }

    [Fact]
    public void ImpliedVolMatchesTheLibrary()
    {
        double price = BlackScholes.Price(OptionType.Call, S, K, Tau, R, Q, Vol);
        Assert.Equal(ImpliedVolatility.BlackScholes(S, K, Tau, R, Q, price, OptionType.Call), T.Num(_qf.ImpliedVol(price, "C", S, K, Tau, R, Q)));
    }

    [Fact]
    public void ImpliedVolBlack76InvertsBlack76Price()
    {
        double df = 0.97, forward = 98.0;
        double price = T.Num(_qf.Black76Price("C", forward, K, Tau, df, Vol));
        Assert.Equal(Vol, T.Num(_qf.ImpliedVolBlack76(price, "C", forward, K, Tau, df)), 9);
        Assert.Equal(ImpliedVolatility.Black76(forward, K, Tau, df, price, OptionType.Call),
            T.Num(_qf.ImpliedVolBlack76(price, "C", forward, K, Tau, df)));
    }

    [Fact]
    public void ImpliedVolOfAPriceBelowIntrinsicValueIsANumError()
    {
        double intrinsic = S * Math.Exp(-Q * Tau) - 80.0 * Math.Exp(-R * Tau);
        Assert.Equal(XlError.Num, _qf.ImpliedVol(intrinsic - 1.0, "C", S, 80.0, Tau, R, Q));
        Assert.False(string.IsNullOrEmpty(ErrorLog.Last));
    }

    [Fact]
    public void DomainErrorsBecomeNumAndTypeErrorsBecomeValue()
    {
        Assert.Equal(XlError.Num, _qf.BsPrice("C", -1.0, K, Tau, R, Q, Vol));      // negative spot
        Assert.Contains("QF.BS.PRICE", ErrorLog.Last);
        Assert.Equal(XlError.Num, _qf.BsPrice("C", S, K, -1.0, R, Q, Vol));        // negative maturity
        Assert.Equal(XlError.Num, _qf.BsPrice("C", S, K, Tau, R, Q, -0.2));        // negative vol
        Assert.Equal(XlError.Value, _qf.BsPrice("X", S, K, Tau, R, Q, Vol));       // unknown option type
        Assert.Equal(XlError.Value, _qf.BsPrice("C", "abc", K, Tau, R, Q, Vol));   // text where a number is needed
        Assert.Contains("S:", ErrorLog.Last);
        Assert.Equal(XlError.Value, _qf.BsPrice("C", null, K, Tau, R, Q, Vol));    // empty cell
        Assert.Equal(XlError.Value, _qf.BsPrice("C", T.Column(1.0, 2.0), K, Tau, R, Q, Vol));   // a range for a scalar
    }

    [Fact]
    public void ErrorValuesInArgumentsPropagateUnchanged()
    {
        Assert.Equal(XlError.NA, _qf.BsPrice("C", XlError.NA, K, Tau, R, Q, Vol));
        Assert.Equal(XlError.DivZero, _qf.BsGreeks("C", S, K, Tau, R, Q, XlError.DivZero));
        Assert.Equal(XlError.Ref, _qf.ImpliedVol(XlError.Ref, "C", S, K, Tau, R, Q));
    }

    [Fact]
    public void NonFiniteInputsAreNumErrors()
    {
        Assert.Equal(XlError.Num, _qf.BsPrice("C", double.NaN, K, Tau, R, Q, Vol));
        Assert.Equal(XlError.Num, _qf.BsPrice("C", double.PositiveInfinity, K, Tau, R, Q, Vol));
    }

    [Fact]
    public void VersionAndLastErrorAreStrings()
    {
        Assert.StartsWith("InFine.QuantFinance 0.", T.Text(_qf.Version()));
        _qf.BsPrice("X", S, K, Tau, R, Q, Vol);
        Assert.Contains("option type", T.Text(_qf.LastError()));
    }
}
