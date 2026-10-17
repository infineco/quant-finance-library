using System;
using ClosedXML.Excel;
using InFine.QuantFinance.ClosedForm;

namespace InFine.QuantFinance.ExcelSamples;

/// <summary>02-Implied-Volatility-Smile: option prices to implied vols, raw-SVI fit, smile chart and residuals.</summary>
public static class SmileWorkbook
{
    // The "market" is a raw-SVI smile in total variance, w(k) = a + b (rho (k - m) + sqrt((k - m)^2 + sigma^2)), k = ln(K / F),
    // plus a deterministic 0.1 vol-point wiggle so that the fit has residuals to show.
    public const double Spot = 100.0, Rate = 0.03, DivYield = 0.01, Maturity = 0.5;
    public const double A = 0.008, B = 0.06, Rho = -0.5, M = 0.0, Sigma = 0.2;
    public const int FirstRow = 13, LastRow = 25;

    public static double Forward => Spot * Math.Exp((Rate - DivYield) * Maturity);

    public static double SmileVol(double strike, int index)
    {
        double k = Math.Log(strike / Forward);
        double w = A + B * (Rho * (k - M) + Math.Sqrt((k - M) * (k - M) + Sigma * Sigma));
        double wiggle = 0.001 * Math.Sin(2.7 * index + 0.5);
        return Math.Sqrt(w / Maturity) + wiggle;
    }

    /// <summary>The static market: strike, option type (out of the money) and price rounded to 4 decimals.</summary>
    public static (double Strike, string Type, double Price)[] Market()
    {
        var result = new (double, string, double)[LastRow - FirstRow + 1];
        for (int i = 0; i < result.Length; i++)
        {
            double strike = 70.0 + 5.0 * i;
            string type = strike < Forward ? "P" : "C";
            double vol = SmileVol(strike, i);
            double price = BlackScholes.Price(type == "C" ? OptionType.Call : OptionType.Put, Spot, strike, Maturity, Rate, DivYield, vol);
            result[i] = (strike, type, Math.Round(price, 4));
        }
        return result;
    }

    public static SampleBook Build(string fileName)
    {
        var book = new SampleBook(fileName);
        SampleWorkbooks.About(book, "Implied volatility smile and SVI fit",
            "From a strip of option prices to implied volatilities (QF.IMPLIEDVOL), then a raw-SVI smile fitted to them (QF.SVI.FIT) " +
            "and read back on the same strikes (QF.SURFACE.VOL), with the fit residuals.",
            new[]
            {
                "Smile: the market data (spot, rates, expiry and 13 option prices), the implied vol of each price, the SVI handle, the fitted vols, " +
                    "the residuals in vol points, and a dense strike grid for the fitted curve.",
                "Charts: market implied vols against the SVI curve, and the residuals.",
            },
            new[]
            {
                "The prices are static numbers (rounded to four decimals), computed from a known raw-SVI smile with a 0.1 vol-point wiggle, " +
                    $"so the implied vols and the fit are meaningful: a={A}, b={B}, rho={Rho}, m={M}, sigma={Sigma} on total variance at T={Maturity}.",
                "Out-of-the-money options are used (puts below the forward, calls above), as a desk would quote them.",
                "Functions used: QF.IMPLIEDVOL, QF.SVI.FIT, QF.SURFACE.VOL. The SVI object is named SAMPLE02_SVI: object names are global to the Excel session, so do not open two copies of this workbook at once.",
            });

        var s = book.AddSheet("Smile");
        s.Widths((2, 34), (3, 12), (4, 14), (5, 14), (6, 14), (7, 16), (8, 4), (9, 12), (10, 14));
        s.Title("B1", "Implied volatility smile and SVI fit", "B2", "Yellow cells are inputs (the market); everything else is computed by the add-in.");
        s.Header("B4", "Market", "Value");
        s.Text("B5", "Spot S");
        s.Input("C5", Spot, "0.00", "Spot");
        s.Text("B6", "Risk-free rate r (continuous)");
        s.Input("C6", Rate, "0.00%", "Rate");
        s.Text("B7", "Dividend yield q (continuous)");
        s.Input("C7", DivYield, "0.00%", "DivYield");
        s.Text("B8", "Maturity T (years)");
        s.Input("C8", Maturity, "0.00", "Maturity");
        s.Text("B9", "Forward F = S e^((r-q)T)");
        s.Calc("C9", "Spot*EXP((Rate-DivYield)*Maturity)", "0.0000", "Fwd");
        s.Text("B10", "SVI surface handle");
        s.Calc("C10", "QF.SVI.FIT(\"SAMPLE02_SVI\",Maturity,Fwd,Strikes,MarketVols)", null, "SviHandle");
        s.Note("D10", "kind:name#version; changes whenever the fit inputs change");

        s.Header("B12", "Strike", "Type", "Market price", "Implied vol", "SVI vol", "Residual (vol points)");
        var market = Market();
        for (int i = 0; i < market.Length; i++)
        {
            int r = FirstRow + i;
            s.Input($"B{r}", market[i].Strike, "0.00");
            s.Calc($"C{r}", $"IF(B{r}<Fwd,\"P\",\"C\")").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            s.Input($"D{r}", market[i].Price, "0.0000");
            s.Calc($"E{r}", $"QF.IMPLIEDVOL(D{r},C{r},Spot,B{r},Maturity,Rate,DivYield)", "0.00%");
            s.Calc($"F{r}", $"QF.SURFACE.VOL(SviHandle,Maturity,B{r})", "0.00%");
            s.Calc($"G{r}", $"(F{r}-E{r})*100", "0.000");
        }
        s.Name("Strikes", $"B{FirstRow}:B{LastRow}");
        s.Name("MarketVols", $"E{FirstRow}:E{LastRow}");

        s.Header("B27", "Fit quality", "Value");
        s.Text("B28", "RMSE of the residuals (vol points)");
        s.Calc("C28", $"SQRT(SUMSQ(G{FirstRow}:G{LastRow})/COUNT(G{FirstRow}:G{LastRow}))", "0.000");
        s.Text("B29", "Largest residual (vol points)");
        s.Calc("C29", $"MAX(MAX(G{FirstRow}:G{LastRow}),-MIN(G{FirstRow}:G{LastRow}))", "0.000");
        s.Text("B30", "Fitted at-the-money-forward vol");
        s.Calc("C30", "QF.SURFACE.VOL(SviHandle,Maturity,Fwd)", "0.00%");
        s.Note("B32", "QF.OBJ.INFO(handle) (Excel 365: type it in a free cell) lists the fitted SVI parameters and the fit quality.");
        s.Note("B33", "The residual is fitted minus market. The wiggle added to the market vols is the only thing the smooth SVI curve cannot follow.");

        s.Header("I12", "Strike grid", "SVI vol");
        s.Calc("I13", $"ROUND(MIN(Strikes),0)", "0");
        for (int r = 14; r <= 73; r++) s.Calc($"I{r}", $"I{r - 1}+1", "0");
        for (int r = 13; r <= 73; r++) s.Calc($"J{r}", $"QF.SURFACE.VOL(SviHandle,Maturity,I{r})", "0.00%");

        string MarketRef(string col) => s.Ref($"{col}{FirstRow}:{col}{LastRow}");
        book.Charts.Add(new ChartSpec("Smile", "Implied volatility smile", "Strike", "Implied volatility", "0", "0%", 11, 3, 19, 21, new[]
        {
            new ChartSeries("Market implied vol", MarketRef("B"), MarketRef("E"), "C55A11", false, true),
            new ChartSeries("SVI fit", s.Ref("I13:I73"), s.Ref("J13:J73"), "1F4E79", true, false),
        }, XMin: market[0].Strike, XMax: market[^1].Strike));
        book.Charts.Add(new ChartSpec("Smile", "SVI residuals (fitted - market)", "Strike", "Vol points", "0", "0.0", 11, 22, 19, 38, new[]
        {
            new ChartSeries("Residual", MarketRef("B"), MarketRef("G"), "1F4E79", true, true),
        }, XMin: market[0].Strike, XMax: market[^1].Strike));
        return book;
    }
}
