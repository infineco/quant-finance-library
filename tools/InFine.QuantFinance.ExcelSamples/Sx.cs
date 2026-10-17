using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace InFine.QuantFinance.ExcelSamples;

/// <summary>A workbook under construction: the ClosedXML workbook plus the charts to add once it is saved.</summary>
public sealed class SampleBook
{
    public SampleBook(string fileName)
    {
        FileName = fileName;
        Workbook = new XLWorkbook();
        Workbook.Properties.Author = "InFine QuantFinance";
        Workbook.Properties.Title = fileName;
        Workbook.Properties.Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Workbook.Properties.Modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    public string FileName { get; }
    public XLWorkbook Workbook { get; }
    public List<ChartSpec> Charts { get; } = new();

    public Sx AddSheet(string name) => new(this, Workbook.AddWorksheet(name));
}

/// <summary>Writing helpers shared by the sample workbooks: input / formula / header styles, named ranges, notes.</summary>
public sealed class Sx
{
    // Financial-model colour code: blue on pale yellow is something to type over, black on white is a formula.
    public static readonly XLColor InputFill = XLColor.FromHtml("#FFF2CC");
    public static readonly XLColor InputFont = XLColor.FromHtml("#0000FF");
    public static readonly XLColor HeaderFill = XLColor.FromHtml("#1F3864");
    public static readonly XLColor Border = XLColor.FromHtml("#BFBFBF");
    public static readonly XLColor NoteFont = XLColor.FromHtml("#595959");
    public static readonly XLColor OutputFill = XLColor.FromHtml("#E2EFDA");

    private readonly SampleBook _book;

    internal Sx(SampleBook book, IXLWorksheet ws)
    {
        _book = book;
        Ws = ws;
        ws.ShowGridLines = false;
        ws.Style.Font.FontName = "Calibri";
        ws.Style.Font.FontSize = 11;
        ws.Column(1).Width = 2;
    }

    public IXLWorksheet Ws { get; }
    public SampleBook Book => _book;

    /// <summary>Absolute, sheet-qualified reference of a cell or range address: 'Sheet'!$C$5:$C$9.</summary>
    public string Ref(string address) => $"'{Ws.Name}'!{Absolute(address)}";

    public static string Absolute(string address) =>
        Regex.Replace(address, @"([A-Z]+)(\d+)", "$$$1$$$2");

    public Sx Name(string name, string address)
    {
        _book.Workbook.DefinedNames.Add(name, Ref(address));
        return this;
    }

    public Sx Widths(params (int column, double width)[] widths)
    {
        foreach (var (column, width) in widths) Ws.Column(column).Width = width;
        return this;
    }

    public void Title(string address, string text, string? subtitleAddress = null, string? subtitle = null)
    {
        var cell = Ws.Cell(address);
        cell.Value = text;
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontSize = 16;
        cell.Style.Font.FontColor = HeaderFill;
        if (subtitleAddress != null) Note(subtitleAddress, subtitle ?? "");
    }

    public void Text(string address, string text, bool bold = false)
    {
        var cell = Ws.Cell(address);
        cell.Value = text;
        cell.Style.Font.Bold = bold;
    }

    public void Note(string address, string text)
    {
        var cell = Ws.Cell(address);
        cell.Value = text;
        cell.Style.Font.Italic = true;
        cell.Style.Font.FontColor = NoteFont;
    }

    public void Header(string address, params string[] titles)
    {
        var first = Ws.Cell(address);
        for (int i = 0; i < titles.Length; i++)
        {
            var cell = first.CellRight(i);
            cell.Value = titles[i];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = HeaderFill;
            cell.Style.Alignment.Horizontal = i == 0 ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.WrapText = true;
        }
    }

    /// <summary>An input cell (blue on pale yellow). <paramref name="value"/> is a number, text or DateTime.</summary>
    public IXLCell Input(string address, object value, string? format = null, string? name = null)
    {
        var cell = Ws.Cell(address);
        switch (value)
        {
            case double d: cell.Value = d; break;
            case int i: cell.Value = i; break;
            case string s: cell.Value = s; break;
            case DateTime t: cell.Value = t; break;
            default: throw new ArgumentException($"Unsupported input value {value.GetType()}");
        }
        StyleInput(cell, format);
        if (name != null) Name(name, address);
        return cell;
    }

    public void StyleInput(IXLCell cell, string? format)
    {
        cell.Style.Fill.BackgroundColor = InputFill;
        cell.Style.Font.FontColor = InputFont;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = Border;
        if (format != null) cell.Style.NumberFormat.Format = format;
    }

    /// <summary>A formula cell (no leading '='). Plain QF.* names: the add-in's functions are XLL functions, not _xlfn ones.</summary>
    public IXLCell Calc(string address, string formula, string? format = null, string? name = null)
    {
        var cell = Ws.Cell(address);
        cell.FormulaA1 = formula;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = Border;
        if (format != null) cell.Style.NumberFormat.Format = format;
        if (name != null) Name(name, address);
        return cell;
    }

    /// <summary>
    /// A function returning a block (QF.BS.GREEKS, QF.COV, QF.VAR.DECOMP, QF.MCVAR...), written as a legacy (Ctrl+Shift+Enter)
    /// array formula over exactly the block the function returns. ClosedXML cannot write the cell metadata that makes a
    /// formula a dynamic-array (spilling) one, and a plain formula would be opened by Excel 365 as implicit-intersection
    /// (=@QF.BS.GREEKS(...)), which keeps only the first cell. A legacy array formula works in every Excel version.
    /// </summary>
    public IXLRange Block(string rangeAddress, string formula, string? format = null)
    {
        var range = Ws.Range(rangeAddress);
        range.FormulaArrayA1 = formula;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = Border;
        if (format != null) range.Style.NumberFormat.Format = format;
        return range;
    }

    public void List(string address, string items)
    {
        var validation = Ws.Cell(address).CreateDataValidation();
        validation.List("\"" + items + "\"", true);
    }

    public void Widen(string address, double width) => Ws.Column(Ws.Cell(address).Address.ColumnNumber).Width = width;
}
