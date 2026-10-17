using System;
using ClosedXML.Excel;

namespace InFine.QuantFinance.ExcelSamples;

/// <summary>03-Yield-Curve: euro-style deposits, FRA and swaps bootstrapped into a curve, read on a date grid, against a flat curve.</summary>
internal static class CurveWorkbook
{
    public static readonly (string Type, string Tenor, double Quote)[] Quotes =
    {
        ("DEPOSIT", "1M", 0.0288),
        ("DEPOSIT", "3M", 0.0274),
        ("DEPOSIT", "6M", 0.0258),
        ("FRA", "6x12", 0.0242),
        ("SWAP", "2Y", 0.0215),
        ("SWAP", "3Y", 0.0215),
        ("SWAP", "5Y", 0.0224),
        ("SWAP", "7Y", 0.0236),
        ("SWAP", "10Y", 0.0251),
        ("SWAP", "15Y", 0.0263),
        ("SWAP", "20Y", 0.0260),
        ("SWAP", "30Y", 0.0240),
    };

    public static readonly string[] GridTenors =
    {
        "1M", "2M", "3M", "6M", "9M", "1Y", "2Y", "3Y", "4Y", "5Y", "6Y", "7Y", "8Y", "9Y", "10Y", "12Y", "15Y", "20Y", "25Y", "30Y",
    };

    public static readonly DateTime ValuationDate = new(2025, 1, 15);
    public const int QuoteFirstRow = 5;
    public const int GridFirstRow = 16;

    public static SampleBook Build(string fileName)
    {
        var book = new SampleBook(fileName);
        SampleWorkbooks.About(book, "Yield curve bootstrap",
            "A discount curve bootstrapped from euro-style deposit, FRA and swap quotes (QF.CURVE.BOOTSTRAP), read as discount factors " +
            "and zero rates on a grid of dates built with QF.DATE.ADVANCE and QF.YEARFRAC, and compared with a flat curve (QF.CURVE.FLAT).",
            new[]
            {
                "Quotes: the market instruments (type, tenor, quoted rate) the curve is built from.",
                "Curve: the valuation date, calendar and interpolation inputs, the two curve handles, and the date grid with dates, year fractions, " +
                    "discount factors, zero rates (continuous and annual), forward rates and the spread to the flat curve; charts of the zero/forward curves and of the discount factors.",
            },
            new[]
            {
                "The quotes are illustrative euro-market levels around mid-January 2025, not live data. Conventions are the euro ones: deposits and FRAs Actual/360, " +
                    "swaps annual 30/360 against 6M Actual/360 floating, spot two TARGET business days after the valuation date.",
                "Functions used: QF.CURVE.BOOTSTRAP, QF.CURVE.FLAT, QF.CURVE.DF, QF.CURVE.ZERO, QF.DATE.ADVANCE, QF.YEARFRAC. " +
                    "The curves are named SAMPLE03_EUR and SAMPLE03_FLAT: object names are global to the Excel session, so do not open two copies of this workbook at once.",
            });

        var q = book.AddSheet("Quotes");
        q.Widths((2, 14), (3, 10), (4, 12));
        q.Title("B1", "Market quotes", "B2", "Type is DEPOSIT, FRA or SWAP; a FRA tenor reads 6x12 (starts in 6 months, ends in 12); quotes are decimals (0.0215 is 2.15%).");
        q.Header("B4", "Type", "Tenor", "Quote");
        for (int i = 0; i < Quotes.Length; i++)
        {
            int r = QuoteFirstRow + i;
            q.Input($"B{r}", Quotes[i].Type);
            q.Input($"C{r}", Quotes[i].Tenor);
            q.Input($"D{r}", Quotes[i].Quote, "0.00%");
        }
        int last = QuoteFirstRow + Quotes.Length - 1;
        q.Name("InstTypes", $"B{QuoteFirstRow}:B{last}");
        q.Name("InstTenors", $"C{QuoteFirstRow}:C{last}");
        q.Name("InstQuotes", $"D{QuoteFirstRow}:D{last}");
        q.Note($"B{last + 2}", "Keep one instrument per maturity: the bootstrap needs strictly increasing pillars.");

        var c = book.AddSheet("Curve");
        c.Widths((2, 30), (3, 14), (4, 10), (5, 10), (6, 12), (7, 12), (8, 12), (9, 10), (10, 10), (11, 12));
        c.Title("B1", "Bootstrapped curve and flat comparison", "B2", "Inputs are yellow. The grid dates are valuation date + tenor on the calendar (modified following).");
        c.Header("B4", "Inputs", "Value");
        c.Text("B5", "Valuation date");
        c.Input("C5", ValuationDate, "dd-mmm-yyyy", "ValDate");
        c.Text("B6", "Calendar");
        c.Input("C6", "TARGET", null, "Cal");
        c.List("C6", "TARGET,NYSE,LONDON,WEEKENDS,TARGET+NYSE");
        c.Text("B7", "Interpolation");
        c.Input("C7", "LogLinearDiscount", null, "Interp");
        c.List("C7", "LogLinearDiscount,LinearZero,MonotoneCubicZero");
        c.Text("B8", "Flat curve rate (continuous)");
        c.Input("C8", 0.025, "0.00%", "FlatRate");
        c.Text("B9", "Day count of the grid");
        c.Input("C9", "ACT/365F", null, "DayCount");
        c.Widen("C5", 18);

        c.Header("B11", "Curve objects", "Handle");
        c.Text("B12", "Bootstrapped curve");
        c.Calc("C12", "QF.CURVE.BOOTSTRAP(\"SAMPLE03_EUR\",ValDate,InstTypes,InstTenors,InstQuotes,Interp,Cal)", null, "CurveHandle");
        c.Text("B13", "Flat curve");
        c.Calc("C13", "QF.CURVE.FLAT(\"SAMPLE03_FLAT\",ValDate,FlatRate,DayCount)", null, "FlatHandle");
        c.Note("D12", "kind:name#version");

        c.Header("B15", "Tenor", "Date", "Years", "Discount factor", "Zero (cont.)", "Zero (annual)", "Forward (cont.)", "Flat DF", "Flat zero", "Curve - flat (bp)");
        int n = GridTenors.Length;
        int lastRow = GridFirstRow + n - 1;
        for (int i = 0; i < n; i++)
        {
            int r = GridFirstRow + i;
            c.Input($"B{r}", GridTenors[i]);
            c.Calc($"C{r}", $"QF.DATE.ADVANCE(ValDate,B{r},Cal)", "dd-mmm-yyyy");
            c.Calc($"D{r}", $"QF.YEARFRAC(ValDate,C{r},DayCount)", "0.000");
            c.Calc($"E{r}", $"QF.CURVE.DF(CurveHandle,C{r})", "0.000000");
            c.Calc($"F{r}", $"QF.CURVE.ZERO(CurveHandle,C{r})", "0.000%");
            c.Calc($"G{r}", $"QF.CURVE.ZERO(CurveHandle,C{r},\"Annual\")", "0.000%");
            // Forward rate over the period ending on this row; the first one starts at the valuation date.
            c.Calc($"H{r}", i == 0 ? $"-LN(E{r})/D{r}" : $"LN(E{r - 1}/E{r})/(D{r}-D{r - 1})", "0.000%");
            c.Calc($"I{r}", $"QF.CURVE.DF(FlatHandle,C{r})", "0.000000");
            c.Calc($"J{r}", $"QF.CURVE.ZERO(FlatHandle,C{r})", "0.000%");
            c.Calc($"K{r}", $"(F{r}-J{r})*10000", "0.0");
        }
        c.Note($"B{lastRow + 2}", "The forward rate is the continuously compounded rate between the previous grid date and this one (the first row runs from the valuation date).");
        c.Note($"B{lastRow + 3}", "The curve extrapolates with a flat forward beyond its last pillar; the swap pillars stop at 30Y.");

        string Rng(string col) => c.Ref($"{col}{GridFirstRow}:{col}{lastRow}");
        book.Charts.Add(new ChartSpec("Curve", "Zero and forward curves", "Years", "Rate", "0", "0.0%", 12, 3, 20, 21, new[]
        {
            new ChartSeries("Zero (continuous)", Rng("D"), Rng("F"), "1F4E79", true, true),
            new ChartSeries("Forward (continuous)", Rng("D"), Rng("H"), "C55A11", true, false),
            new ChartSeries("Flat curve", Rng("D"), Rng("J"), "7F7F7F", true, false),
        }));
        book.Charts.Add(new ChartSpec("Curve", "Discount factors", "Years", "Discount factor", "0", "0.00", 12, 22, 20, 39, new[]
        {
            new ChartSeries("Bootstrapped curve", Rng("D"), Rng("E"), "1F4E79", true, true),
            new ChartSeries("Flat curve", Rng("D"), Rng("I"), "7F7F7F", true, false),
        }));
        return book;
    }
}
