using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClosedXML.Excel;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.ExcelSamples;
using Xunit;

namespace InFine.QuantFinance.Excel.Core.Tests;

/// <summary>
/// The example workbooks of samples/excel: structural checks (every formula calls a function the add-in declares, with a valid number
/// of arguments, on cells that are filled in; charts point at filled cells), numerical sanity of their inputs through the Core
/// implementations of the same functions, and a check that the committed files are what the generator writes.
/// Nothing here needs Excel: the formulas of the workbooks are not evaluated by a spreadsheet engine but by the Core.
/// </summary>
[Collection("QuantFunctions")]
public class SampleWorkbookTests
{
    private static readonly Lazy<Dictionary<string, ShimFunction>> Registry = new(ShimRegistry.Load);

    private readonly QuantFunctions _qf = new(new ObjectCache());

    public static IEnumerable<object[]> Workbooks() => SampleFiles.All();

    // ---- the registry itself ----------------------------------------------------------------------------------------

    [Fact]
    public void ShimRegistry_lists_the_25_functions_with_their_arities()
    {
        var registry = Registry.Value;
        Assert.Equal(25, registry.Count);
        Assert.Equal((7, 7), (registry["QF.BS.PRICE"].MinArgs, registry["QF.BS.PRICE"].MaxArgs));
        Assert.Equal((7, 8), (registry["QF.BS.GREEKS"].MinArgs, registry["QF.BS.GREEKS"].MaxArgs));
        Assert.Equal((5, 7), (registry["QF.CURVE.BOOTSTRAP"].MinArgs, registry["QF.CURVE.BOOTSTRAP"].MaxArgs));
        Assert.Equal((2, 5), (registry["QF.DATE.ADVANCE"].MinArgs - 1, registry["QF.DATE.ADVANCE"].MaxArgs));
        Assert.Equal((1, 5), (registry["QF.COV"].MinArgs, registry["QF.COV"].MaxArgs));
        Assert.Equal((6, 10), (registry["QF.MCVAR"].MinArgs, registry["QF.MCVAR"].MaxArgs));
        Assert.Equal((0, 0), (registry["QF.VERSION"].MinArgs, registry["QF.VERSION"].MaxArgs));
    }

    [Fact]
    public void FormulaScanner_counts_arguments_of_nested_calls_and_omitted_arguments()
    {
        var calls = FormulaScanner.QfCalls("IF(QF.VAR(R,0.99,,10,{1,2;3,4})>0,\"QF.NOT.A.CALL(1)\",QF.VERSION())");
        Assert.Equal(new[] { "QF.VAR", "QF.VERSION" }, calls.Select(c => c.Name));
        Assert.Equal(5, calls[0].Arguments.Count);
        Assert.Equal("", calls[0].Arguments[2]);
        Assert.Empty(calls[1].Arguments);
    }

    // ---- structure ----------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Workbooks))]
    public void Every_QF_call_exists_in_the_shim_with_a_valid_number_of_arguments(string file)
    {
        using var workbook = SampleFiles.Open(file);
        var registry = Registry.Value;
        int calls = 0;
        foreach (var (cell, formula) in Formulas(workbook))
        {
            Assert.False(formula.StartsWith('='), $"{file} {Where(cell)}: the formula text must not start with '='");
            Assert.DoesNotContain("_xlfn", formula);
            foreach (var call in FormulaScanner.QfCalls(formula))
            {
                calls++;
                Assert.True(registry.TryGetValue(call.Name, out var function), $"{file} {Where(cell)}: {call.Name} is not a function of the add-in");
                Assert.True(call.Arguments.Count >= function!.MinArgs && call.Arguments.Count <= function.MaxArgs,
                    $"{file} {Where(cell)}: {call.Name} has {call.Arguments.Count} arguments, expected {function.MinArgs} to {function.MaxArgs}: {formula}");
                for (int i = 0; i < function.MinArgs; i++)
                    Assert.True(call.Arguments[i].Trim().Length > 0, $"{file} {Where(cell)}: required argument {i + 1} of {call.Name} is omitted");
            }
            // No QF.* name outside a call (a typo such as QF.BS.PRICE without parenthesis).
            Assert.Equal(FormulaScanner.QfCalls(formula).Count,
                Regex.Matches(FormulaScanner.BlankStrings(formula), @"(?<![A-Za-z0-9_.])QF\.").Count);
        }
        Assert.True(calls > 0, $"{file} calls no QF function");
    }

    [Theory]
    [MemberData(nameof(Workbooks))]
    public void Every_cell_a_formula_refers_to_is_filled_in(string file)
    {
        using var workbook = SampleFiles.Open(file);
        var arrays = ArrayRanges(workbook);
        var names = workbook.DefinedNames.Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int checkedCells = 0;
        foreach (var (cell, formula) in Formulas(workbook))
        {
            foreach (var range in References(workbook, cell.Worksheet, formula, names))
                foreach (var target in range.Cells())
                {
                    checkedCells++;
                    bool filled = !target.IsEmpty() || arrays.Any(a => Contains(a, target));
                    Assert.True(filled, $"{file} {Where(cell)} refers to the empty cell {Where(target)}: {formula}");
                }
        }
        Assert.True(checkedCells > 0);
    }

    [Theory]
    [MemberData(nameof(Workbooks))]
    public void Charts_refer_to_filled_cells(string file)
    {
        using var zip = ZipFile.OpenRead(SampleFiles.FullPath(file));
        using var workbook = SampleFiles.Open(file);
        var arrays = ArrayRanges(workbook);
        var charts = zip.Entries.Where(e => e.FullName.StartsWith("xl/charts/chart", StringComparison.Ordinal)).ToList();
        Assert.Equal(file.StartsWith("01", StringComparison.Ordinal) || file.StartsWith("02", StringComparison.Ordinal) || file.StartsWith("03", StringComparison.Ordinal),
            charts.Count > 0);
        foreach (var entry in charts)
        {
            var doc = XDocument.Load(entry.Open());
            XNamespace c = "http://schemas.openxmlformats.org/drawingml/2006/chart";
            var references = doc.Descendants(c + "f").Select(f => f.Value).ToList();
            Assert.NotEmpty(references);
            foreach (string reference in references)
            {
                var m = Regex.Match(reference, @"^'(?<sheet>[^']+)'!\$(?<a>[A-Z]+)\$(?<r1>\d+):\$(?<b>[A-Z]+)\$(?<r2>\d+)$");
                Assert.True(m.Success, $"{entry.FullName}: unexpected reference {reference}");
                var range = workbook.Worksheet(m.Groups["sheet"].Value).Range($"{m.Groups["a"].Value}{m.Groups["r1"].Value}:{m.Groups["b"].Value}{m.Groups["r2"].Value}");
                foreach (var cell in range.Cells())
                    Assert.True(!cell.IsEmpty() || arrays.Any(a => Contains(a, cell)),
                        $"{entry.FullName}: {reference} contains the empty cell {Where(cell)}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Workbooks))]
    public void The_package_has_no_schema_errors_in_the_parts_the_generator_adds(string file)
    {
        using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(SampleFiles.FullPath(file), false);
        var validator = new DocumentFormat.OpenXml.Validation.OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019);
        var errors = validator.Validate(doc)
            .Where(e => e.Part != null && (e.Part.Uri.ToString().Contains("/charts/") || e.Part.Uri.ToString().Contains("/drawings/")
                || e.Part.Uri.ToString().Contains("workbook.xml") || (e.Path?.XPath ?? "").Contains("drawing")))
            .Select(e => $"{e.Part!.Uri}: {e.Description} ({e.Path?.XPath})")
            .ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void The_committed_workbooks_are_what_the_generator_writes()
    {
        string temp = Path.Combine(Path.GetTempPath(), "qf-samples-" + Guid.NewGuid().ToString("N"));
        try
        {
            SampleWorkbooks.WriteAll(temp);
            foreach (string file in SampleFiles.Names)
            {
                var expected = Snapshot(Path.Combine(temp, file));
                var committed = Snapshot(SampleFiles.FullPath(file));
                Assert.True(expected.Count == committed.Count, $"{file}: {committed.Count} entries committed, the generator writes {expected.Count}. Run the generator (dotnet run --project tools/InFine.QuantFinance.ExcelSamples) and commit samples/excel.");
                for (int i = 0; i < expected.Count; i++)
                    Assert.True(SameEntry(expected[i], committed[i]),
                        $"{file}: the committed workbook differs from the generated one at '{Short(expected[i])}' (committed: '{Short(committed[i])}'). Regenerate samples/excel (dotnet run --project tools/InFine.QuantFinance.ExcelSamples).");
            }
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }

    // ---- 01 Pricing and Greeks ---------------------------------------------------------------------------------------

    [Fact]
    public void Pricing_inputs_reproduce_independent_BlackScholes_prices_and_greeks()
    {
        using var wb = SampleFiles.Open("01-Pricing-and-Greeks.xlsx");
        double s = Sheet.Number(wb, "Spot"), k = Sheet.Number(wb, "Strike"), t = Sheet.Number(wb, "Maturity");
        double r = Sheet.Number(wb, "Rate"), q = Sheet.Number(wb, "DivYield"), vol = Sheet.Number(wb, "Vol");
        string type = (string)Sheet.Scalar(wb, "OptType");
        var optionType = type == "C" ? OptionType.Call : OptionType.Put;

        // What the QF.BS.PRICE / QF.BS.GREEKS cells of the Pricing sheet return, from its own inputs.
        double call = T.Num(_qf.BsPrice("C", s, k, t, r, q, vol));
        double put = T.Num(_qf.BsPrice("P", s, k, t, r, q, vol));
        Assert.Equal(BlackScholes.Price(OptionType.Call, s, k, t, r, q, vol), call, 12);
        Assert.Equal(BlackScholes.Price(OptionType.Put, s, k, t, r, q, vol), put, 12);

        // Textbook closed form with the library's normal distribution, and put-call parity.
        double d1 = (Math.Log(s / k) + (r - q + 0.5 * vol * vol) * t) / (vol * Math.Sqrt(t)), d2 = d1 - vol * Math.Sqrt(t);
        double textbookCall = s * Math.Exp(-q * t) * NormalDistribution.Cdf(d1) - k * Math.Exp(-r * t) * NormalDistribution.Cdf(d2);
        Assert.Equal(textbookCall, call, 10);
        Assert.Equal(s * Math.Exp(-q * t) - k * Math.Exp(-r * t), call - put, 10);

        var greeks = T.Array(_qf.BsGreeks(type, s, k, t, r, q, vol, "HEADER"));
        Assert.Equal((2, 10), (greeks.GetLength(0), greeks.GetLength(1)));
        var expected = BlackScholes.Greeks(optionType, s, k, t, r, q, vol);
        Assert.Equal(expected.Delta, Assert.IsType<double>(greeks[1, 1]), 12);
        Assert.Equal(expected.Gamma, Assert.IsType<double>(greeks[1, 2]), 12);
        Assert.Equal(expected.Vega, Assert.IsType<double>(greeks[1, 3]), 12);

        // Black-76 on the forward equals Black-Scholes, and both implied vols give back the input volatility.
        double forward = s * Math.Exp((r - q) * t), df = Math.Exp(-r * t);
        double b76 = T.Num(_qf.Black76Price(type, forward, k, t, df, vol));
        double bs = T.Num(_qf.BsPrice(type, s, k, t, r, q, vol));
        Assert.Equal(bs, b76, 10);
        Assert.Equal(vol, T.Num(_qf.ImpliedVol(bs, type, s, k, t, r, q)), 8);
        Assert.Equal(vol, T.Num(_qf.ImpliedVolBlack76(b76, type, forward, k, t, df)), 8);

        // The ladder: every strike of the sheet prices without error, parity holds, calls decrease with the strike.
        var ladder = wb.Worksheet("Ladder");
        double previousCall = double.MaxValue;
        for (int row = 5; row <= 17; row++)
        {
            double moneyness = ladder.Cell(row, 2).GetDouble();
            double strike = Math.Round(s * moneyness, 2);
            double c = T.Num(_qf.BsPrice("C", s, strike, t, r, q, vol)), p = T.Num(_qf.BsPrice("P", s, strike, t, r, q, vol));
            Assert.Equal(s * Math.Exp(-q * t) - strike * Math.Exp(-r * t), c - p, 10);
            Assert.True(c < previousCall);
            previousCall = c;
            Assert.Equal(BlackScholes.Greeks(OptionType.Call, s, strike, t, r, q, vol).Delta, T.Num(_qf.BsGreeks("C", s, strike, t, r, q, vol, "Delta")), 12);
        }
    }

    // ---- 02 Smile -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Smile_prices_give_the_generating_vols_and_the_svi_fit_converges()
    {
        using var wb = SampleFiles.Open("02-Implied-Volatility-Smile.xlsx");
        double s = Sheet.Number(wb, "Spot"), r = Sheet.Number(wb, "Rate"), q = Sheet.Number(wb, "DivYield"), t = Sheet.Number(wb, "Maturity");
        double forward = s * Math.Exp((r - q) * t);
        var smile = wb.Worksheet("Smile");
        var strikes = new List<double>();
        var vols = new List<double>();
        for (int row = 13; row <= 25; row++)
        {
            double strike = smile.Cell(row, 2).GetDouble(), price = smile.Cell(row, 4).GetDouble();
            string type = strike < forward ? "P" : "C";
            double iv = T.Num(_qf.ImpliedVol(price, type, s, strike, t, r, q));
            // The price is rounded to 4 decimals: the vol is recovered to well under a tenth of a vol point except far out of the money.
            Assert.InRange(iv, SmileWorkbook.SmileVol(strike, row - 13) - 0.003, SmileWorkbook.SmileVol(strike, row - 13) + 0.003);
            strikes.Add(strike);
            vols.Add(iv);
        }
        Assert.True(vols[0] > vols[6] && vols[0] > vols[12], "the smile has a downside skew");

        string handle = T.Text(_qf.SviFit("SAMPLE02_SVI", t, forward, T.Column(strikes.Cast<object>().ToArray()), T.Column(vols.Cast<object>().ToArray())));
        double sumSquares = 0;
        for (int i = 0; i < strikes.Count; i++)
        {
            double fitted = T.Num(_qf.SurfaceVol(handle, t, strikes[i]));
            sumSquares += (fitted - vols[i]) * (fitted - vols[i]);
        }
        Assert.True(Math.Sqrt(sumSquares / strikes.Count) < 0.003, "the SVI fit follows the strip to within 0.3 vol point (RMSE)");
        // The dense grid of the sheet stays inside the fitted range.
        Assert.True(T.Num(_qf.SurfaceVol(handle, t, 70)) > 0 && T.Num(_qf.SurfaceVol(handle, t, 130)) > 0);
    }

    // ---- 03 Curve -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Curve_quotes_bootstrap_and_the_grid_dates_are_valid()
    {
        using var wb = SampleFiles.Open("03-Yield-Curve.xlsx");
        double valuation = Convert.ToDouble(Sheet.Scalar(wb, "ValDate"));
        string calendar = (string)Sheet.Scalar(wb, "Cal"), interpolation = (string)Sheet.Scalar(wb, "Interp");
        var handle = T.Text(_qf.CurveBootstrap("SAMPLE03_EUR", valuation, Sheet.Matrix(wb, "InstTypes"), Sheet.Matrix(wb, "InstTenors"),
            Sheet.Matrix(wb, "InstQuotes"), interpolation, calendar));
        var flat = T.Text(_qf.CurveFlat("SAMPLE03_FLAT", valuation, Sheet.Number(wb, "FlatRate"), (string)Sheet.Scalar(wb, "DayCount")));

        var curve = wb.Worksheet("Curve");
        double previousDf = 1.0, previousYears = 0.0;
        for (int row = 16; row <= 35; row++)
        {
            string tenor = curve.Cell(row, 2).GetString();
            double date = T.Num(_qf.DateAdvance(valuation, tenor, calendar));
            double years = T.Num(_qf.YearFrac(valuation, date, "ACT/365F"));
            double df = T.Num(_qf.CurveDf(handle, date));
            double zero = T.Num(_qf.CurveZero(handle, date));
            T.Num(_qf.CurveZero(handle, date, "Annual"));
            Assert.True(years > previousYears);
            Assert.True(df > 0 && df < previousDf, $"{tenor}: discount factors decrease");
            Assert.InRange(zero, 0.015, 0.035);
            Assert.Equal(-Math.Log(df) / years, zero, 10);
            Assert.Equal(Math.Exp(-Sheet.Number(wb, "FlatRate") * years), T.Num(_qf.CurveDf(flat, date)), 10);
            previousDf = df;
            previousYears = years;
        }
    }

    // ---- 04 Portfolio risk --------------------------------------------------------------------------------------------

    [Fact]
    public void Portfolio_inputs_give_var_es_and_decompositions_without_errors()
    {
        using var wb = SampleFiles.Open("04-Portfolio-Risk.xlsx");
        var returns = Sheet.Matrix(wb, "ReturnsData");
        var header = Sheet.Matrix(wb, "ReturnsHeader");
        var exposures = Sheet.Matrix(wb, "Exposures");
        double alpha = Sheet.Number(wb, "Alpha"), horizon = Sheet.Number(wb, "Horizon"), lambda = Sheet.Number(wb, "Lambda"), periods = Sheet.Number(wb, "PeriodsPerYear");
        Assert.Equal((250, 5), (returns.GetLength(0), returns.GetLength(1)));

        var var_ = new Dictionary<string, double>();
        foreach (string method in new[] { "HISTORICAL", "PARAMETRIC", "CORNISHFISHER" })
        {
            double v = T.Num(_qf.Var(returns, alpha, method, horizon, exposures));
            double e = T.Num(_qf.Es(returns, alpha, method, horizon, exposures));
            Assert.True(v > 0 && e >= v * 0.999, $"{method}: 0 < VaR <= ES");
            var_[method] = v;
        }
        // The three methods are of the same order of magnitude but not identical (fat tails).
        Assert.InRange(var_["HISTORICAL"] / var_["PARAMETRIC"], 0.5, 2.0);
        Assert.NotEqual(var_["HISTORICAL"], var_["PARAMETRIC"]);

        // QF.VAR on the P&L column (the sheet's MMULT of returns and exposures) equals QF.VAR with positions.
        var pnl = new object[250, 1];
        for (int i = 0; i < 250; i++)
        {
            double sum = 0;
            for (int j = 0; j < 5; j++) sum += Convert.ToDouble(returns[i, j]) * Convert.ToDouble(exposures[j, 0]);
            pnl[i, 0] = sum;
        }
        Assert.Equal(var_["HISTORICAL"], T.Num(_qf.Var(pnl, alpha, "HISTORICAL", horizon)), 6);

        foreach (string method in new[] { "PARAMETRIC", "HISTORICAL" })
        {
            var table = T.Array(_qf.VarDecomp(header, exposures, alpha, method, horizon, true));
            Assert.Equal((7, 7), (table.GetLength(0), table.GetLength(1)));
            double components = Enumerable.Range(1, 5).Sum(i => Assert.IsType<double>(table[i, 2]));
            Assert.Equal(Assert.IsType<double>(table[6, 2]), components, 4);
        }

        foreach (var (method, lam, annualisation) in new[] { ("SAMPLE", (object?)null, periods), ("EWMA", lambda, periods), ("LW_CC", null, periods) })
        {
            var cov = T.Array(_qf.Cov(returns, method, lam, annualisation));
            Assert.Equal((5, 5), (cov.GetLength(0), cov.GetLength(1)));
            for (int i = 0; i < 5; i++)
            {
                double volatility = Math.Sqrt(Assert.IsType<double>(cov[i, i]));
                Assert.InRange(volatility, 0.03, 0.40);
            }
        }
        // The header version of the same range names the assets.
        Assert.Equal("ESTOXX50", header[0, 0]);
    }

    // ---- 05 Monte Carlo VaR -------------------------------------------------------------------------------------------

    [Fact]
    public void MonteCarlo_inputs_run_deterministically_and_philox_agrees_with_sobol()
    {
        using var wb = SampleFiles.Open("05-Monte-Carlo-VaR.xlsx");
        var header = Sheet.Matrix(wb, "ReturnsHeader");
        double alpha = Sheet.Number(wb, "Alpha"), scenarios = Sheet.Number(wb, "Scenarios"), seed = Sheet.Number(wb, "Seed");
        double horizon = Sheet.Number(wb, "Horizon"), rate = Sheet.Number(wb, "Rate"), lambda = Sheet.Number(wb, "Lambda");
        string method = (string)Sheet.Scalar(wb, "CovMethod");
        var handle = T.Text(_qf.CovObj("SAMPLE05_COV", header, method, lambda, null, true));
        var spots = Sheet.Matrix(wb, "Spots");
        var vols = Sheet.Matrix(wb, "Vols");
        var positions = Sheet.Matrix(wb, "Positions");

        object[,] Run(string generator, double s) =>
            T.Array(_qf.McVar(handle, spots, vols, positions, alpha, scenarios, s, generator, rate, horizon));

        var philox = Run("PHILOX", seed);
        var again = Run("PHILOX", seed);
        var other = Run("PHILOX", seed + 1);
        var sobol = Run("SOBOL", seed);

        Assert.Equal((3, 3), (philox.GetLength(0), philox.GetLength(1)));
        Assert.Equal("VaR", philox[1, 0]);
        Assert.Equal("ES", philox[2, 0]);
        double varPhilox = Assert.IsType<double>(philox[1, 1]), esPhilox = Assert.IsType<double>(philox[2, 1]);
        Assert.True(varPhilox > 0 && esPhilox > varPhilox);
        Assert.Equal(varPhilox, Assert.IsType<double>(again[1, 1]));                                  // same seed, same digits
        Assert.NotEqual(varPhilox, Assert.IsType<double>(other[1, 1]));                               // another seed, another estimate
        Assert.IsType<double>(philox[1, 2]);                                                           // a standard error at this size
        double varSobol = Assert.IsType<double>(sobol[1, 1]);
        Assert.InRange(varSobol / varPhilox, 0.95, 1.05);
        Assert.InRange(Assert.IsType<double>(other[1, 1]) / varPhilox, 0.95, 1.05);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private static string Where(IXLCell cell) => $"{cell.Worksheet.Name}!{cell.Address.ToStringRelative()}";

    /// <summary>Cells that hold a formula, with its text (the first cell of an array formula included).</summary>
    private static IEnumerable<(IXLCell Cell, string Formula)> Formulas(XLWorkbook workbook) =>
        workbook.Worksheets.SelectMany(ws => ws.CellsUsed(XLCellsUsedOptions.All)).Where(c => c.HasFormula).Select(c => (c, c.FormulaA1));

    private static List<IXLRangeAddress> ArrayRanges(XLWorkbook workbook) =>
        workbook.Worksheets.SelectMany(ws => ws.CellsUsed(XLCellsUsedOptions.All)).Where(c => c.HasArrayFormula).Select(c => c.FormulaReference).ToList();

    private static bool Contains(IXLRangeAddress range, IXLCell cell) =>
        range.Worksheet == cell.Worksheet && range.Contains(cell.Address);

    private static readonly Regex ReferencePattern = new(
        @"(?<![A-Za-z0-9_.])(?:(?:'(?<q>[^']+)'|(?<s>[A-Za-z_][A-Za-z0-9_]*))!)?\$?(?<c1>[A-Z]{1,3})\$?(?<r1>\d+)(?::\$?(?<c2>[A-Z]{1,3})\$?(?<r2>\d+))?(?![A-Za-z0-9_(.])",
        RegexOptions.Compiled);

    /// <summary>The ranges a formula reads: A1 references (sheet-qualified or not) and defined names, outside string literals.</summary>
    private static IEnumerable<IXLRange> References(XLWorkbook workbook, IXLWorksheet current, string formula, HashSet<string> names)
    {
        string text = FormulaScanner.BlankStrings(formula);
        foreach (Match m in ReferencePattern.Matches(text))
        {
            string sheet = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["s"].Success ? m.Groups["s"].Value : current.Name;
            string to = m.Groups["c2"].Success ? $":{m.Groups["c2"].Value}{m.Groups["r2"].Value}" : "";
            yield return workbook.Worksheet(sheet).Range($"{m.Groups["c1"].Value}{m.Groups["r1"].Value}{to}");
        }
        foreach (Match m in Regex.Matches(text, @"(?<![A-Za-z0-9_.!'$])[A-Za-z_][A-Za-z0-9_.]*(?![A-Za-z0-9_.(!])"))
            if (names.Contains(m.Value))
                yield return workbook.DefinedName(m.Value)!.Ranges.Single();
    }

    /// <summary>One line per formula, value or defined name of the workbook, plus the chart parts: what "the same workbook" means.</summary>
    private static List<string> Snapshot(string path)
    {
        var lines = new List<string>();
        using (var wb = new XLWorkbook(path))
        {
            foreach (var name in wb.DefinedNames.OrderBy(n => n.Name, StringComparer.Ordinal))
                lines.Add($"name {name.Name} = {name.RefersTo}");
            foreach (var ws in wb.Worksheets)
            {
                lines.Add($"sheet {ws.Name}");
                foreach (var cell in ws.CellsUsed(XLCellsUsedOptions.All))
                {
                    string where = cell.Address.ToStringRelative();
                    if (cell.HasFormula) lines.Add($"{ws.Name}!{where} F {(cell.HasArrayFormula ? "{" + cell.FormulaReference.ToStringRelative() + "}" : "")}{cell.FormulaA1}");
                    else if (!cell.IsEmpty()) lines.Add($"{ws.Name}!{where} {(cell.Value.IsText ? "T" : "N")} {(cell.Value.IsText ? cell.Value.GetText() : Convert.ToString(Sheet.Value(cell), System.Globalization.CultureInfo.InvariantCulture))}");
                    lines.Add($"{ws.Name}!{where} S {cell.Style.NumberFormat.Format}|{cell.Style.Fill.BackgroundColor}");
                }
            }
        }
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/charts/", StringComparison.Ordinal)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var reader = new StreamReader(entry.Open());
            lines.Add($"{entry.FullName} {reader.ReadToEnd()}");
        }
        return lines;
    }

    private static string Short(string line) => line.Length <= 160 ? line : line[..160] + "...";

    /// <summary>Equal lines; numbers compare to 1e-4 so that a last-digit difference of a libm function on another platform is not a failure.</summary>
    private static bool SameEntry(string expected, string committed)
    {
        if (expected == committed) return true;
        var e = Regex.Match(expected, @"^(?<head>\S+ N )(?<v>\S+)$");
        var c = Regex.Match(committed, @"^(?<head>\S+ N )(?<v>\S+)$");
        return e.Success && c.Success && e.Groups["head"].Value == c.Groups["head"].Value
            && Math.Abs(double.Parse(e.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture)
                - double.Parse(c.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture)) < 1e-4;
    }
}
