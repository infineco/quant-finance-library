using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ClosedXML.Excel;

namespace InFine.QuantFinance.ExcelSamples;

/// <summary>Builds the five example workbooks of samples/excel.</summary>
public static class SampleWorkbooks
{
    public const string DocsUrl = "https://github.com/infineco/quant-finance-library/blob/main/docs/excel.md";

    public static readonly string[] FileNames =
    {
        "01-Pricing-and-Greeks.xlsx",
        "02-Implied-Volatility-Smile.xlsx",
        "03-Yield-Curve.xlsx",
        "04-Portfolio-Risk.xlsx",
        "05-Monte-Carlo-VaR.xlsx",
    };

    /// <summary>Writes every workbook into <paramref name="directory"/> and returns the paths.</summary>
    public static IReadOnlyList<string> WriteAll(string directory)
    {
        // The About texts interpolate numbers: build under the invariant culture so the committed workbooks read the
        // same (decimal points) whatever the culture of the machine that regenerates them.
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return WriteAllInvariant(directory);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static IReadOnlyList<string> WriteAllInvariant(string directory)
    {
        Directory.CreateDirectory(directory);
        var books = new[]
        {
            PricingWorkbook.Build(FileNames[0]),
            SmileWorkbook.Build(FileNames[1]),
            CurveWorkbook.Build(FileNames[2]),
            PortfolioRiskWorkbook.Build(FileNames[3]),
            MonteCarloWorkbook.Build(FileNames[4]),
        };
        var paths = new List<string>();
        foreach (var book in books)
        {
            string path = Path.Combine(directory, book.FileName);
            using var saved = new MemoryStream();
            book.Workbook.SaveAs(saved);
            File.WriteAllBytes(path, ChartInjector.Process(saved.ToArray(), book.Charts));
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// The "About" sheet shared by all workbooks: purpose, sheet guide, how to load the add-in, colour code, and live cells
    /// that show whether the add-in is loaded and its last error.
    /// </summary>
    internal static void About(SampleBook book, string title, string purpose, string[] sheetGuide, string[]? extraNotes = null)
    {
        var s = book.AddSheet("About");
        s.Widths((2, 118));
        var ws = s.Ws;
        int row = 2;

        void Line(string text, bool bold = false, bool header = false)
        {
            var cell = ws.Cell(row, 2);
            cell.Value = text;
            cell.Style.Alignment.WrapText = true;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            if (bold) cell.Style.Font.Bold = true;
            if (header)
            {
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = Sx.HeaderFill;
            }
            row++;
        }

        s.Title("B2", title);
        row = 3;
        Line(purpose);
        row++;
        Line("What is in this workbook", header: true);
        foreach (var g in sheetGuide) Line("   " + g);
        if (extraNotes != null)
        {
            row++;
            foreach (var n in extraNotes) Line(n);
        }
        row++;
        Line("Before you start", header: true);
        Line("   1. The numbers appear only when the InFine QuantFinance add-in (an Excel-DNA .xll, Windows, 64-bit Excel) is loaded. Without it, every QF.* cell shows #NAME?.");
        Line("   2. Build or download the packed add-in, unblock the file (right-click, Properties, Unblock), then File > Options > Add-ins > Manage Excel Add-ins > Go > Browse and pick the .xll.");
        Line("   3. Open this workbook. If cells still show #NAME? or #REF!, press Ctrl+Alt+F9 (full recalculation): objects such as curves and covariance matrices are kept in memory and are rebuilt by the cells that create them.");
        Line("   4. Full instructions, the function reference and the conventions are in docs/excel.md:");
        var link = ws.Cell(row, 2);
        link.Value = "   " + DocsUrl;
        link.SetHyperlink(new XLHyperlink(DocsUrl));
        row++;
        row++;
        Line("Colour code", header: true);
        var input = ws.Cell(row, 2);
        input.Value = "   Blue on pale yellow: an input, type over it.";
        s.StyleInput(input, null);
        row++;
        var calc = ws.Cell(row, 2);
        calc.Value = "   Black on white: a formula, do not overwrite it. Cells that start a QF.* block are Ctrl+Shift+Enter array formulas ({=...}).";
        row++;
        row++;
        Line("Diagnostics", header: true);
        var status = ws.Cell(row, 2);
        status.FormulaA1 = "\"   Add-in status: \"&IF(ISERROR(QF.VERSION()),\"NOT LOADED (QF.* cells show #NAME?)\",\"loaded, version \"&QF.VERSION())";
        status.Style.Font.Bold = true;
        row++;
        var last = ws.Cell(row, 2);
        last.FormulaA1 = "\"   Last error reported by the add-in (QF.LASTERROR): \"&IFERROR(QF.LASTERROR(),\"(add-in not loaded)\")";
        row++;
        Line("   Tip: a cell can only show #VALUE!, #NUM! or #REF!. When a QF.* cell fails, type =QF.LASTERROR() in a free cell to read the reason, with the function and the argument name (it is the last failure of any QF function).");
        row++;
        Line("All market data in these workbooks is synthetic or illustrative; it is not a recommendation and not a market quote.");
        ws.SheetView.ZoomScale = 100;
        ws.TabColor = XLColor.FromHtml("#7F7F7F");
    }
}
