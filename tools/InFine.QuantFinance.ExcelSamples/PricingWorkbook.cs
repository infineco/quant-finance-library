using System;
using System.Collections.Generic;
using ClosedXML.Excel;

namespace InFine.QuantFinance.ExcelSamples;

/// <summary>01-Pricing-and-Greeks: Black-Scholes and Black-76 prices, all greeks, a strike ladder with two charts.</summary>
internal static class PricingWorkbook
{
    public const int LadderFirstRow = 5;
    public const int LadderLastRow = 17;

    public static SampleBook Build(string fileName)
    {
        var book = new SampleBook(fileName);
        SampleWorkbooks.About(book, "Pricing and Greeks",
            "European option pricing with Black-Scholes-Merton and Black-76, the ten greeks in one call, and a strike ladder. " +
            "Change the yellow inputs on the Pricing sheet and everything, including the ladder and its charts, follows.",
            new[]
            {
                "Pricing: inputs (spot, strike, maturity, rate, dividend yield, volatility, call or put), QF.BS.PRICE, put-call parity check, " +
                    "QF.BS.GREEKS with a header row, Black-76 on the forward with its implied volatility round trip.",
                "Ladder: prices, deltas, gamma and vega for 13 strikes from 70% to 130% of spot, with charts of price and delta against strike.",
            },
            new[]
            {
                "Functions used: QF.BS.PRICE, QF.BS.GREEKS, QF.BLACK76.PRICE, QF.IMPLIEDVOL, QF.IMPLIEDVOL.BLACK76.",
            });

        // ---------------------------------------------------------------- Pricing
        var p = book.AddSheet("Pricing");
        p.Widths((2, 40), (3, 14), (4, 14), (5, 12), (6, 12), (7, 12), (8, 12), (9, 12), (10, 12), (11, 12));
        p.Title("B1", "Black-Scholes and Black-76", "B2", "Yellow cells are inputs, white cells are formulas. Rates and volatilities are decimals, times are in years.");
        p.Header("B4", "Inputs", "Value");
        string[] labels = { "Spot S", "Strike K", "Maturity T (years)", "Risk-free rate r (continuous)", "Dividend yield q (continuous)", "Volatility", "Option type (C or P)" };
        for (int i = 0; i < labels.Length; i++) p.Text($"B{5 + i}", labels[i]);
        p.Input("C5", 100.0, "0.00", "Spot");
        p.Input("C6", 100.0, "0.00", "Strike");
        p.Input("C7", 1.0, "0.00", "Maturity");
        p.Input("C8", 0.03, "0.00%", "Rate");
        p.Input("C9", 0.01, "0.00%", "DivYield");
        p.Input("C10", 0.20, "0.00%", "Vol");
        p.Input("C11", "C", null, "OptType").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        p.List("C11", "C,P");
        p.Note("D8", "0.03 is 3%");
        p.Note("D10", "0.20 is 20%");

        const string args = "Spot,Strike,Maturity,Rate,DivYield,Vol";
        p.Header("B13", "Black-Scholes-Merton", "Value");
        p.Text("B14", "Price (selected type)");
        p.Calc("C14", $"QF.BS.PRICE(OptType,{args})", "0.0000", "BsPrice");
        p.Text("B15", "Call price");
        p.Calc("C15", $"QF.BS.PRICE(\"C\",{args})", "0.0000");
        p.Text("B16", "Put price");
        p.Calc("C16", $"QF.BS.PRICE(\"P\",{args})", "0.0000");
        p.Text("B17", "Call - Put");
        p.Calc("C17", "C15-C16", "0.0000");
        p.Text("B18", "S e^(-qT) - K e^(-rT)");
        p.Calc("C18", "Spot*EXP(-DivYield*Maturity)-Strike*EXP(-Rate*Maturity)", "0.0000");
        p.Text("B19", "Put-call parity difference");
        p.Calc("C19", "C17-C18", "0.0E+00");
        p.Text("B20", "Put-call parity check");
        p.Calc("C20", "IF(ABS(C19)<1E-9,\"OK\",\"CHECK\")").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        p.Text("B21", "Delta (selected type)");
        p.Calc("C21", $"QF.BS.GREEKS(OptType,{args},\"Delta\")", "0.0000");
        p.Text("B22", "Implied volatility of the price (round trip)");
        p.Calc("C22", "QF.IMPLIEDVOL(C14,OptType,Spot,Strike,Maturity,Rate,DivYield)", "0.00%");
        p.Note("D22", "returns the input volatility");

        p.Header("B24", "Black-76 (option on the forward)", "Value");
        p.Text("B25", "Forward F = S e^((r-q)T)");
        p.Calc("C25", "Spot*EXP((Rate-DivYield)*Maturity)", "0.0000", "Fwd");
        p.Text("B26", "Discount factor e^(-rT)");
        p.Calc("C26", "EXP(-Rate*Maturity)", "0.000000", "DiscFactor");
        p.Text("B27", "Black-76 price (selected type)");
        p.Calc("C27", "QF.BLACK76.PRICE(OptType,Fwd,Strike,Maturity,DiscFactor,Vol)", "0.0000");
        p.Text("B28", "Difference with Black-Scholes");
        p.Calc("C28", "C27-C14", "0.0E+00");
        p.Text("B29", "Black-76 implied volatility (round trip)");
        p.Calc("C29", "QF.IMPLIEDVOL.BLACK76(C27,OptType,Fwd,Strike,Maturity,DiscFactor)", "0.00%");

        p.Header("B31", "All greeks: QF.BS.GREEKS(..., \"HEADER\")");
        p.Header("C31", "", "", "", "", "", "", "", "", "");
        var block = p.Block("B32:K33", $"QF.BS.GREEKS(OptType,{args},\"HEADER\")", "0.0000");
        foreach (var cell in block.Row(1).Cells())
        {
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Fill.BackgroundColor = Sx.OutputFill;
            cell.Style.NumberFormat.Format = "General";
        }
        p.Note("B34", "Raw derivatives: vega is per unit of volatility and theta per year; divide by 100 for one vol point and by 365 for one day. Rho and DivRho are per unit of rate.");
        p.Note("B35", "QF.BS.GREEKS returns 2 rows x 10 columns with \"HEADER\" (1 x 10 with \"ALL\"); it is a Ctrl+Shift+Enter array formula over B32:K33.");

        // ---------------------------------------------------------------- Ladder
        var l = book.AddSheet("Ladder");
        l.Widths((2, 10), (3, 12), (4, 12), (5, 12), (6, 12), (7, 12), (8, 12), (9, 16), (10, 14));
        l.Title("B1", "Strike ladder", "B2", "Uses Spot, Maturity, Rate, DivYield and Vol from the Pricing sheet. Change the moneyness column to move the strikes.");
        l.Header("B4", "K / S", "Strike", "Call", "Put", "Call delta", "Put delta", "Gamma", "Vega (per vol point)", "Parity difference");
        for (int i = 0; i <= LadderLastRow - LadderFirstRow; i++)
        {
            int r = LadderFirstRow + i;
            const string common = "Spot,C{0},Maturity,Rate,DivYield,Vol";
            string c = string.Format(common, r);
            l.Input($"B{r}", Math.Round(0.70 + 0.05 * i, 2), "0%");
            l.Calc($"C{r}", $"ROUND(Spot*B{r},2)", "0.00");
            l.Calc($"D{r}", $"QF.BS.PRICE(\"C\",{c})", "0.0000");
            l.Calc($"E{r}", $"QF.BS.PRICE(\"P\",{c})", "0.0000");
            l.Calc($"F{r}", $"QF.BS.GREEKS(\"C\",{c},\"Delta\")", "0.0000");
            l.Calc($"G{r}", $"QF.BS.GREEKS(\"P\",{c},\"Delta\")", "0.0000");
            l.Calc($"H{r}", $"QF.BS.GREEKS(\"C\",{c},\"Gamma\")", "0.0000");
            l.Calc($"I{r}", $"QF.BS.GREEKS(\"C\",{c},\"Vega\")/100", "0.0000");
            l.Calc($"J{r}", $"D{r}-E{r}-(Spot*EXP(-DivYield*Maturity)-C{r}*EXP(-Rate*Maturity))", "0.0E+00");
        }
        l.Note($"B{LadderLastRow + 2}", "The last column is the put-call parity difference of each strike: zero up to rounding.");

        string x = l.Ref($"C{LadderFirstRow}:C{LadderLastRow}");
        string Y(string col) => l.Ref($"{col}{LadderFirstRow}:{col}{LadderLastRow}");
        book.Charts.Add(new ChartSpec("Ladder", "Option price against strike", "Strike", "Price", "0", "0.00", 11, 3, 19, 20, new[]
        {
            new ChartSeries("Call", x, Y("D"), "1F4E79", true, true),
            new ChartSeries("Put", x, Y("E"), "C55A11", true, true),
        }));
        book.Charts.Add(new ChartSpec("Ladder", "Delta against strike", "Strike", "Delta", "0", "0.00", 11, 21, 19, 38, new[]
        {
            new ChartSeries("Call delta", x, Y("F"), "1F4E79", true, true),
            new ChartSeries("Put delta", x, Y("G"), "C55A11", true, true),
        }));
        return book;
    }
}
