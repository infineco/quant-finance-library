using System;
using ClosedXML.Excel;

namespace InFine.QuantFinance.ExcelSamples;

/// <summary>05-Monte-Carlo-VaR: full-revaluation Monte Carlo VaR / ES of a book of shares and options, PHILOX against SOBOL.</summary>
internal static class MonteCarloWorkbook
{
    public static readonly string[] Assets = { "ESTOXX50", "SPX", "GOLD" };
    public static readonly double[] Spots = { 5000.0, 6000.0, 2500.0 };
    public static readonly double[] Vols = { 0.19, 0.17, 0.15 };

    /// <summary>Asset, type, quantity, strike, maturity in years, dividend yield. Strike and maturity of a SPOT row are not read.</summary>
    public static readonly (string Asset, string Type, double Quantity, double? Strike, double? Maturity, double? Dividend)[] Book =
    {
        ("ESTOXX50", "SPOT", 40, null, null, null),
        ("SPX", "SPOT", 25, null, null, null),
        ("GOLD", "SPOT", 60, null, null, null),
        ("SPX", "PUT", 25, 5700.0, 0.25, 0.013),
        ("ESTOXX50", "CALL", -40, 5300.0, 0.5, 0.03),
        ("GOLD", "CALL", 60, 2650.0, 0.5, 0.0),
        ("ESTOXX50", "PUT", -20, 4600.0, 0.25, 0.03),
    };

    public const int Days = SyntheticMarket.Days;

    public static SampleBook Build(string fileName)
    {
        var book = new SampleBook(fileName);
        SampleWorkbooks.About(book, "Monte Carlo VaR",
            "Value-at-Risk and Expected Shortfall of a book of shares and European options by Monte Carlo full revaluation (QF.MCVAR): " +
            "correlated scenarios from a covariance kept by the add-in (QF.COV.OBJ), every position repriced in every scenario, so the option curvature is captured.",
            new[]
            {
                "Returns: 250 days of synthetic daily log returns of three assets (the covariance of the log returns is what QF.MCVAR needs).",
                "MonteCarlo: the settings, the covariance handle, spots and volatilities, the positions table, and three Monte Carlo runs side by side " +
                    "(PHILOX with the seed, PHILOX with seed + 1, SOBOL with the seed), then a comparison table.",
            },
            new[]
            {
                "QF.MCVAR is asynchronous: the cell shows #N/A while the scenarios are computed in the background and Excel stays responsive; the result then replaces it, " +
                    "and so do the cells that depend on it. Do not wrap it in IFERROR or IFNA. Ctrl+Alt+F9 recomputes all of them.",
                "QF.MCVAR is deterministic: same inputs and same seed give the same digits, whatever the number of cores. Change the seed for an independent estimate; " +
                    "keep it to compare two books. The standard error shrinks as one over the square root of the number of scenarios.",
                "PHILOX is pseudo-random; SOBOL is scrambled quasi-random, usually closer to the true value for the same number of scenarios " +
                    "(use a power of two; 131072 = 2^17). Its standard error is only indicative.",
                "The covariance object is named SAMPLE05_COV: object names are global to the Excel session, so do not open two copies of this workbook at once.",
                "Functions used: QF.COV.OBJ, QF.MCVAR.",
            });

        // ---------------------------------------------------------------- Returns
        var r = book.AddSheet("Returns");
        r.Widths((2, 14), (3, 11), (4, 11), (5, 11));
        r.Header("B1", "Date", Assets[0], Assets[1], Assets[2]);
        var returns = SyntheticMarket.DailyLogReturns(Days);
        var dates = SyntheticMarket.Dates(Days);
        for (int i = 0; i < Days; i++)
        {
            int row = i + 2;
            r.Ws.Cell(row, 2).Value = dates[i];
            r.Ws.Cell(row, 2).Style.NumberFormat.Format = "dd-mmm-yyyy";
            for (int j = 0; j < Assets.Length; j++)
            {
                var cell = r.Ws.Cell(row, 3 + j);
                r.StyleInput(cell, "0.00%");
                cell.Value = returns[i, j];
            }
        }
        r.Ws.SheetView.FreezeRows(1);
        r.Name("ReturnsHeader", $"C1:E{Days + 1}");
        r.Note("G2", "Daily log returns (synthetic, the first three assets of the portfolio-risk sample). The header row gives the asset names used by the positions table.");

        // ---------------------------------------------------------------- MonteCarlo
        var m = book.AddSheet("MonteCarlo");
        m.Widths((2, 30), (3, 14), (4, 14), (5, 12), (6, 14), (7, 14), (8, 14), (9, 12), (10, 14), (11, 14), (12, 14));
        m.Title("B1", "Monte Carlo VaR and ES", "B2", "Yellow cells are inputs. The three result blocks are asynchronous: #N/A while computing, then the numbers.");
        m.Header("B3", "Settings", "Value");
        m.Text("B4", "Confidence level");
        m.Input("C4", 0.99, "0.0%", "Alpha");
        m.Text("B5", "Scenarios");
        m.Input("C5", 131072, "#,##0", "Scenarios");
        m.Text("B6", "Seed");
        m.Input("C6", 42, "0", "Seed");
        m.Text("B7", "Horizon (days)");
        m.Input("C7", 10, "0", "Horizon");
        m.Text("B8", "Risk-free rate (continuous)");
        m.Input("C8", 0.02, "0.00%", "Rate");
        m.Text("B9", "Covariance method");
        m.Input("C9", "EWMA", null, "CovMethod");
        m.List("C9", "SAMPLE,EWMA,LW_CC,LW_ID");
        m.Text("B10", "EWMA decay lambda");
        m.Input("C10", 0.97, "0.00", "Lambda");
        m.Text("B11", "Covariance handle");
        m.Calc("C11", "QF.COV.OBJ(\"SAMPLE05_COV\",ReturnsHeader,CovMethod,Lambda,,TRUE)", null, "CovHandle");
        m.Note("D11", "per-period (daily) covariance of the log returns; the header row names the assets");

        m.Header("B13", "Asset", "Spot", "Volatility");
        for (int i = 0; i < Assets.Length; i++)
        {
            int row = 14 + i;
            m.Calc($"B{row}", $"Returns!{(char)('C' + i)}1");
            m.Input($"C{row}", Spots[i], "#,##0.00");
            m.Input($"D{row}", Vols[i], "0.0%");
        }
        m.Name("Spots", "C14:C16");
        m.Name("Vols", "D14:D16");
        m.Note("E14", "implied volatilities are held fixed in the scenarios; only the spots move");

        m.Header("B18", "Asset", "Type", "Quantity", "Strike", "Maturity (years)", "Dividend yield");
        for (int i = 0; i < Book.Length; i++)
        {
            int row = 19 + i;
            var p = Book[i];
            m.Input($"B{row}", p.Asset);
            m.Input($"C{row}", p.Type);
            m.List($"C{row}", "SPOT,CALL,PUT");
            m.Input($"D{row}", p.Quantity, "#,##0");
            if (p.Strike is double k) m.Input($"E{row}", k, "#,##0.00"); else m.Input($"E{row}", "-");
            if (p.Maturity is double t) m.Input($"F{row}", t, "0.00"); else m.Input($"F{row}", "-");
            if (p.Dividend is double q) m.Input($"G{row}", q, "0.0%"); else m.Input($"G{row}", "-");
            foreach (var col in new[] { "E", "F", "G" }) m.Ws.Cell($"{col}{row}").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        int lastPosition = 18 + Book.Length;
        m.Name("Positions", $"B18:G{lastPosition}");
        m.Note($"B{lastPosition + 1}", "Quantity is in units (negative is short). Strike, maturity and dividend yield are not read for SPOT rows (a dash is shown); the first row of titles is skipped.");

        // Three runs, side by side.
        int top = lastPosition + 3;
        string common = "CovHandle,Spots,Vols,Positions,Alpha,Scenarios";
        string[] titles = { "PHILOX, seed", "PHILOX, seed + 1", "SOBOL, seed" };
        string[] formulas =
        {
            $"QF.MCVAR({common},Seed,\"PHILOX\",Rate,Horizon)",
            $"QF.MCVAR({common},Seed+1,\"PHILOX\",Rate,Horizon)",
            $"QF.MCVAR({common},Seed,\"SOBOL\",Rate,Horizon)",
        };
        string[] leftColumns = { "B", "F", "J" };
        string[] midColumns = { "C", "G", "K" };
        string[] rightColumns = { "D", "H", "L" };
        for (int i = 0; i < 3; i++)
        {
            m.Header($"{leftColumns[i]}{top}", titles[i], "", "");
            var block = m.Block($"{leftColumns[i]}{top + 1}:{rightColumns[i]}{top + 3}", formulas[i], "#,##0.00");
            foreach (var cell in block.Row(1).Cells())
            {
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = Sx.OutputFill;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }
            block.Column(1).Style.Font.Bold = true;
            block.Column(3).Style.NumberFormat.Format = "#,##0.000";
            block.Column(3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        int v = top + 2, e = top + 3;     // VaR and ES rows of each block

        int cmp = top + 5;
        m.Header($"B{cmp}", "Comparison", "VaR", "ES", "VaR std error");
        string[] rowTitles = { "PHILOX, seed", "PHILOX, seed + 1", "SOBOL, seed" };
        for (int i = 0; i < 3; i++)
        {
            int row = cmp + 1 + i;
            m.Text($"B{row}", rowTitles[i]);
            m.Calc($"C{row}", $"{midColumns[i]}{v}", "#,##0.00");
            m.Calc($"D{row}", $"{midColumns[i]}{e}", "#,##0.00");
            m.Calc($"E{row}", $"{rightColumns[i]}{v}", "#,##0.000");
            m.Ws.Cell($"E{row}").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        m.Text($"B{cmp + 4}", "VaR: seed vs seed + 1");
        m.Calc($"C{cmp + 4}", $"C{cmp + 2}-C{cmp + 1}", "#,##0.00");
        m.Note($"D{cmp + 4}", "two independent PHILOX estimates differ by about their standard error");
        m.Text($"B{cmp + 5}", "VaR: SOBOL vs PHILOX");
        m.Calc($"C{cmp + 5}", $"C{cmp + 3}-C{cmp + 1}", "#,##0.00");
        m.Text($"B{cmp + 7}", "Status", bold: true);
        m.Calc($"C{cmp + 7}", $"IF(OR(ISNA(C{v}),ISNA(G{v}),ISNA(K{v})),\"computing in the background...\",\"done\")");
        m.Note($"B{cmp + 9}", "Std error: scenarios are split in 20 blocks; it reads n/a below 2000 scenarios and describes PHILOX sampling error.");
        return book;
    }
}
