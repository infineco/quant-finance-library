using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace InFine.QuantFinance.Excel.Core.Tests;

/// <summary>A worksheet function of the shim: name and number of arguments it accepts.</summary>
internal sealed record ShimFunction(string Name, int MinArgs, int MaxArgs);

/// <summary>
/// The registry of the add-in's worksheet functions, read from the source of the Excel-DNA shim (the
/// <c>[ExcelFunction(Name = "QF....")]</c> methods of QuantFinanceFunctions.cs and RiskFunctions.cs). The shim targets
/// net8.0-windows and references Excel-DNA, so a net8.0 test project cannot reference it (NU1201) and reflecting over its
/// assembly would need Windows targeting and the Excel-DNA runtime. Reading the source needs neither, and it is the
/// declaration that Excel itself sees: a parameter with a default value (<c>= null</c>) is optional.
/// </summary>
internal static class ShimRegistry
{
    private static readonly string[] SourceFiles = { "QuantFinanceFunctions.cs", "RiskFunctions.cs" };

    public static Dictionary<string, ShimFunction> Load()
    {
        string folder = Path.Combine(RepositoryRoot(), "InFine.QuantFinance.Excel");
        var result = new Dictionary<string, ShimFunction>(StringComparer.Ordinal);
        foreach (string file in SourceFiles)
        {
            string text = File.ReadAllText(Path.Combine(folder, file));
            int position = 0;
            while ((position = text.IndexOf("[ExcelFunction(", position, StringComparison.Ordinal)) >= 0)
            {
                var name = Regex.Match(text[position..], @"^\[ExcelFunction\(\s*Name\s*=\s*""([^""]+)""");
                if (!name.Success) throw new InvalidOperationException($"{file}: [ExcelFunction] without a Name near offset {position}");
                int method = text.IndexOf("public static object ", position, StringComparison.Ordinal);
                int open = text.IndexOf('(', method);
                int close = MatchingParenthesis(text, open);
                string parameters = text.Substring(open + 1, close - open - 1);
                var pieces = SplitTopLevel(parameters).Where(p => p.Trim().Length > 0).ToList();
                int optional = pieces.Count(p => Regex.IsMatch(p.Trim(), @"=\s*null$"));
                result[name.Groups[1].Value] = new ShimFunction(name.Groups[1].Value, pieces.Count - optional, pieces.Count);
                position = close;
            }
        }
        return result;
    }

    /// <summary>
    /// The repository root: found from the test binaries when they are built inside it, otherwise (an
    /// <c>--artifacts-path</c> elsewhere) from the location of this source file, which the compiler records.
    /// </summary>
    public static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFile) ?? "" })
        {
            var dir = start.Length == 0 ? null : new DirectoryInfo(start);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QuantFinance.sln"))) dir = dir.Parent;
            if (dir != null) return dir.FullName;
        }
        throw new InvalidOperationException("QuantFinance.sln not found above " + AppContext.BaseDirectory + " or " + sourceFile);
    }

    /// <summary>Index of the ')' matching the '(' at <paramref name="open"/>, skipping C# string literals.</summary>
    private static int MatchingParenthesis(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                i++;
                while (text[i] != '"') i += text[i] == '\\' ? 2 : 1;
            }
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        throw new InvalidOperationException("Unbalanced parentheses in the shim source");
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                i++;
                while (text[i] != '"') i += text[i] == '\\' ? 2 : 1;
            }
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }
}

/// <summary>One call of a QF.* function found in a formula: name and its arguments' text (an omitted argument is empty).</summary>
internal sealed record FormulaCall(string Name, IReadOnlyList<string> Arguments);

internal static class FormulaScanner
{
    private static readonly Regex CallStart = new(@"(?<![A-Za-z0-9_.])(QF\.[A-Za-z0-9.]*)\(", RegexOptions.Compiled);

    /// <summary>Every QF.* call of the formula, nested ones included. Text inside string literals is ignored.</summary>
    public static List<FormulaCall> QfCalls(string formula)
    {
        string text = BlankStrings(formula);
        var calls = new List<FormulaCall>();
        foreach (Match m in CallStart.Matches(text))
        {
            int open = m.Index + m.Length - 1;
            int depth = 0, start = open + 1;
            var args = new List<string>();
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (c is '(' or '{') depth++;
                else if (c is ')' or '}')
                {
                    if (--depth == 0)
                    {
                        args.Add(formula[start..i]);
                        break;
                    }
                }
                else if (c == ',' && depth == 1)
                {
                    args.Add(formula[start..i]);
                    start = i + 1;
                }
            }
            if (args.Count == 1 && args[0].Trim().Length == 0) args.Clear();
            calls.Add(new FormulaCall(m.Groups[1].Value, args));
        }
        return calls;
    }

    /// <summary>The formula with the content of its string literals replaced by spaces (same length).</summary>
    public static string BlankStrings(string formula)
    {
        var sb = new StringBuilder(formula.Length);
        bool inString = false;
        foreach (char c in formula)
        {
            if (c == '"') { inString = !inString; sb.Append(c); }
            else sb.Append(inString ? ' ' : c);
        }
        return sb.ToString();
    }
}

internal static class SampleFiles
{
    public static readonly string[] Names =
    {
        "01-Pricing-and-Greeks.xlsx",
        "02-Implied-Volatility-Smile.xlsx",
        "03-Yield-Curve.xlsx",
        "04-Portfolio-Risk.xlsx",
        "05-Monte-Carlo-VaR.xlsx",
    };

    public static string Directory => Path.Combine(ShimRegistry.RepositoryRoot(), "samples", "excel");

    public static string FullPath(string name) => Path.Combine(Directory, name);

    public static XLWorkbook Open(string name) => new(FullPath(name));

    public static IEnumerable<object[]> All() => Names.Select(n => new object[] { n });
}

/// <summary>Reads cells of a sample workbook the way Excel hands them to a worksheet function.</summary>
internal static class Sheet
{
    public static object? Value(IXLCell cell)
    {
        var v = cell.Value;
        if (v.IsBlank) return null;
        if (v.IsDateTime) return v.GetDateTime().ToOADate();
        if (v.IsNumber) return v.GetNumber();
        if (v.IsBoolean) return v.GetBoolean();
        if (v.IsText) return v.GetText();
        throw new InvalidOperationException($"{cell.Address}: unexpected value {v}");
    }

    public static IXLRange Named(XLWorkbook workbook, string name) =>
        workbook.DefinedName(name)!.Ranges.Single();

    /// <summary>A named single cell, as a scalar.</summary>
    public static object Scalar(XLWorkbook workbook, string name) =>
        Value(Named(workbook, name).FirstCell()) ?? throw new InvalidOperationException($"{name} is empty");

    public static double Number(XLWorkbook workbook, string name) => Convert.ToDouble(Scalar(workbook, name));

    /// <summary>A named range or an A1 address on a sheet, as a 2-D array of numbers and texts (null for empty cells).</summary>
    public static object[,] Matrix(IXLRange range)
    {
        int rows = range.RowCount(), columns = range.ColumnCount();
        var result = new object[rows, columns];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < columns; j++)
                result[i, j] = Value(range.Cell(i + 1, j + 1))!;
        return result;
    }

    public static object[,] Matrix(XLWorkbook workbook, string name) => Matrix(Named(workbook, name));

    public static double[] Column(object[,] matrix, int column) =>
        Enumerable.Range(0, matrix.GetLength(0)).Select(i => Convert.ToDouble(matrix[i, column])).ToArray();
}
