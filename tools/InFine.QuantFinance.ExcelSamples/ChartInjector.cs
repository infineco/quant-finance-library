using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace InFine.QuantFinance.ExcelSamples;

/// <summary>One series of a scatter chart; <c>XRef</c> and <c>YRef</c> are worksheet references like 'Ladder'!$C$5:$C$17.</summary>
public sealed record ChartSeries(string Name, string XRef, string YRef, string Color, bool Line = true, bool Markers = false);

/// <summary>A scatter chart (straight lines and/or markers) anchored on a worksheet.</summary>
public sealed record ChartSpec(string Sheet, string Title, string XTitle, string YTitle, string XFormat, string YFormat,
    int FromColumn, int FromRow, int ToColumn, int ToRow, IReadOnlyList<ChartSeries> Series,
    double? XMin = null, double? XMax = null);

/// <summary>
/// ClosedXML cannot write charts, so they are added to the saved package: one drawing part per sheet that has charts,
/// one chart part per chart. The charts carry only the references of their data (no cached values): Excel reads the cells
/// when it opens the workbook, so they fill in once the add-in has calculated the formulas.
/// The same pass sets <c>fullCalcOnLoad</c> (the formulas have no cached value) and rewrites the package with fixed
/// entry timestamps, so that the output is byte-identical from run to run.
/// </summary>
internal static class ChartInjector
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";

    private const string RelDrawing = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing";
    private const string RelChart = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart";
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Process(byte[] saved, IReadOnlyList<ChartSpec> charts)
    {
        var entries = new Dictionary<string, byte[]>();
        var order = new List<string>();
        using (var zip = new ZipArchive(new MemoryStream(saved), ZipArchiveMode.Read))
            foreach (var e in zip.Entries)
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                entries[e.FullName] = ms.ToArray();
                order.Add(e.FullName);
            }

        void Put(string name, XDocument doc)
        {
            if (!entries.ContainsKey(name)) order.Add(name);
            entries[name] = Serialize(doc);
        }

        XDocument Get(string name) => XDocument.Load(new MemoryStream(entries[name]));

        // ClosedXML names the core-properties part and the package relationships with fresh GUIDs on every save.
        NormalizePackageRelationships(entries, order);

        // workbook.xml: full recalculation on load.
        var workbook = Get("xl/workbook.xml");
        var calc = workbook.Root!.Element(Main + "calcPr");
        if (calc == null)
        {
            calc = new XElement(Main + "calcPr");
            // After definedNames, before the elements that follow it in the schema (all optional and rare).
            var anchor = workbook.Root.Elements().LastOrDefault(e => e.Name.LocalName is "sheets" or "functionGroups" or "externalReferences" or "definedNames");
            if (anchor != null) anchor.AddAfterSelf(calc); else workbook.Root.Add(calc);
        }
        calc.SetAttributeValue("fullCalcOnLoad", "1");
        Put("xl/workbook.xml", workbook);

        if (charts.Count > 0)
        {
            var sheetFiles = SheetParts(Get("xl/workbook.xml"), Get("xl/_rels/workbook.xml.rels"));
            var contentTypes = Get("[Content_Types].xml");
            int drawingCount = 0, chartCount = 0;

            foreach (var group in charts.GroupBy(c => c.Sheet))
            {
                string sheetPath = sheetFiles[group.Key];
                string sheetFile = sheetPath[(sheetPath.LastIndexOf('/') + 1)..];
                string relsPath = $"xl/worksheets/_rels/{sheetFile}.rels";
                var rels = entries.ContainsKey(relsPath) ? Get(relsPath) : new XDocument(new XElement(PkgRel + "Relationships"));
                int drawingNumber = ++drawingCount;
                string drawingName = $"drawing{drawingNumber}.xml";
                string drawingRelId = NextRelId(rels);
                rels.Root!.Add(new XElement(PkgRel + "Relationship", new XAttribute("Id", drawingRelId),
                    new XAttribute("Type", RelDrawing), new XAttribute("Target", "../drawings/" + drawingName)));
                Put(relsPath, rels);

                var drawing = new XElement(Xdr + "wsDr", new XAttribute(XNamespace.Xmlns + "xdr", Xdr.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName));
                var drawingRels = new XElement(PkgRel + "Relationships");
                int anchorId = 1;
                foreach (var spec in group)
                {
                    int chartNumber = ++chartCount;
                    string chartName = $"chart{chartNumber}.xml";
                    Put("xl/charts/" + chartName, BuildChart(spec));
                    contentTypes.Root!.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/xl/charts/" + chartName),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.drawingml.chart+xml")));
                    string chartRelId = "rId" + anchorId;
                    drawingRels.Add(new XElement(PkgRel + "Relationship", new XAttribute("Id", chartRelId),
                        new XAttribute("Type", RelChart), new XAttribute("Target", "../charts/" + chartName)));
                    drawing.Add(Anchor(spec, anchorId, chartRelId));
                    anchorId++;
                }
                Put("xl/drawings/" + drawingName, new XDocument(drawing));
                Put($"xl/drawings/_rels/{drawingName}.rels", new XDocument(drawingRels));
                contentTypes.Root!.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/xl/drawings/" + drawingName),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.drawing+xml")));

                // <drawing r:id> goes before legacyDrawing / tableParts / extLst, after everything else.
                var sheet = Get(sheetPath);
                var element = new XElement(Main + "drawing", new XAttribute(Rel + "id", drawingRelId));
                var following = sheet.Root!.Elements().FirstOrDefault(e => e.Name.LocalName is "legacyDrawing" or "legacyDrawingHF"
                    or "drawingHF" or "picture" or "oleObjects" or "controls" or "webPublishItems" or "tableParts" or "extLst");
                if (following != null) following.AddBeforeSelf(element); else sheet.Root.Add(element);
                if (sheet.Root.Attribute(XNamespace.Xmlns + "r") == null)
                    sheet.Root.Add(new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName));
                Put(sheetPath, sheet);
            }
            Put("[Content_Types].xml", contentTypes);
        }

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            // [Content_Types].xml first, as Office writes it.
            foreach (var name in order.OrderBy(n => n == "[Content_Types].xml" ? 0 : 1))
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = FixedTime;
                using var s = entry.Open();
                s.Write(entries[name]);
            }
        }
        return output.ToArray();
    }

    private static void NormalizePackageRelationships(Dictionary<string, byte[]> entries, List<string> order)
    {
        const string fixedCoreProperties = "package/services/metadata/core-properties/core.psmdcp";
        var rels = XDocument.Load(new MemoryStream(entries["_rels/.rels"]));
        int next = 1;
        foreach (var r in rels.Root!.Elements())
        {
            r.SetAttributeValue("Id", "rId" + next++);
            string target = (string)r.Attribute("Target")!;
            if (target.EndsWith(".psmdcp", StringComparison.Ordinal))
            {
                string old = target.TrimStart('/');
                entries[fixedCoreProperties] = entries[old];
                entries.Remove(old);
                order[order.IndexOf(old)] = fixedCoreProperties;
                r.SetAttributeValue("Target", "/" + fixedCoreProperties);
            }
        }
        entries["_rels/.rels"] = Serialize(rels);
    }

    private static string NextRelId(XDocument rels)
    {
        int max = 0;
        foreach (var r in rels.Root!.Elements())
        {
            string id = (string?)r.Attribute("Id") ?? "";
            if (id.StartsWith("rId", StringComparison.Ordinal) && int.TryParse(id.AsSpan(3), out int n)) max = Math.Max(max, n);
        }
        return "rId" + (max + 1);
    }

    /// <summary>Sheet name to part path (xl/worksheets/sheetN.xml).</summary>
    private static Dictionary<string, string> SheetParts(XDocument workbook, XDocument rels)
    {
        var targets = rels.Root!.Elements().ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
        var result = new Dictionary<string, string>();
        foreach (var sheet in workbook.Root!.Element(Main + "sheets")!.Elements())
        {
            string target = targets[(string)sheet.Attribute(Rel + "id")!];
            result[(string)sheet.Attribute("name")!] = target.StartsWith('/') ? target[1..] : "xl/" + target;
        }
        return result;
    }

    private static XElement Anchor(ChartSpec spec, int id, string relId)
    {
        XElement Pos(string name, int col, int row) => new(Xdr + name,
            new XElement(Xdr + "col", col), new XElement(Xdr + "colOff", 0), new XElement(Xdr + "row", row), new XElement(Xdr + "rowOff", 0));
        return new XElement(Xdr + "twoCellAnchor",
            Pos("from", spec.FromColumn, spec.FromRow), Pos("to", spec.ToColumn, spec.ToRow),
            new XElement(Xdr + "graphicFrame", new XAttribute("macro", ""),
                new XElement(Xdr + "nvGraphicFramePr",
                    new XElement(Xdr + "cNvPr", new XAttribute("id", id + 1), new XAttribute("name", "Chart " + id)),
                    new XElement(Xdr + "cNvGraphicFramePr")),
                new XElement(Xdr + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                    new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0))),
                new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", C.NamespaceName),
                    new XElement(C + "chart", new XAttribute(XNamespace.Xmlns + "c", C.NamespaceName),
                        new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName), new XAttribute(Rel + "id", relId))))),
            new XElement(Xdr + "clientData"));
    }

    private static XElement RichText(string text, int size, bool bold) =>
        new(C + "tx", new XElement(C + "rich", new XElement(A + "bodyPr"), new XElement(A + "lstStyle"),
            new XElement(A + "p",
                new XElement(A + "pPr", new XElement(A + "defRPr", new XAttribute("sz", size), new XAttribute("b", bold ? 1 : 0))),
                new XElement(A + "r", new XElement(A + "rPr", new XAttribute("lang", "en-US"), new XAttribute("sz", size), new XAttribute("b", bold ? 1 : 0)),
                    new XElement(A + "t", text)))));

    private static XElement Fill(string color) => new(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", color)));

    private static XElement AxisElement(int id, int crossId, string position, string title, string format, bool gridlines,
        double? min = null, double? max = null)
    {
        // CT_Scaling order: orientation, max, min.
        var scaling = new XElement(C + "scaling", new XElement(C + "orientation", new XAttribute("val", "minMax")));
        if (max.HasValue) scaling.Add(new XElement(C + "max", new XAttribute("val", max.Value.ToString(CultureInfo.InvariantCulture))));
        if (min.HasValue) scaling.Add(new XElement(C + "min", new XAttribute("val", min.Value.ToString(CultureInfo.InvariantCulture))));
        var axis = new XElement(C + "valAx",
            new XElement(C + "axId", new XAttribute("val", id)),
            scaling,
            new XElement(C + "delete", new XAttribute("val", 0)),
            new XElement(C + "axPos", new XAttribute("val", position)));
        if (gridlines)
            axis.Add(new XElement(C + "majorGridlines", new XElement(C + "spPr",
                new XElement(A + "ln", new XAttribute("w", 6350), Fill("D9D9D9")))));
        axis.Add(new XElement(C + "title", RichText(title, 1000, false), new XElement(C + "overlay", new XAttribute("val", 0))),
            new XElement(C + "numFmt", new XAttribute("formatCode", format), new XAttribute("sourceLinked", 0)),
            new XElement(C + "majorTickMark", new XAttribute("val", "out")),
            new XElement(C + "minorTickMark", new XAttribute("val", "none")),
            new XElement(C + "tickLblPos", new XAttribute("val", "low")),
            new XElement(C + "crossAx", new XAttribute("val", crossId)),
            new XElement(C + "crosses", new XAttribute("val", "autoZero")),
            new XElement(C + "crossBetween", new XAttribute("val", "midCat")));
        return axis;
    }

    private static XDocument BuildChart(ChartSpec spec)
    {
        var scatter = new XElement(C + "scatterChart",
            new XElement(C + "scatterStyle", new XAttribute("val", "lineMarker")),
            new XElement(C + "varyColors", new XAttribute("val", 0)));
        int index = 0;
        foreach (var s in spec.Series)
        {
            var ser = new XElement(C + "ser",
                new XElement(C + "idx", new XAttribute("val", index)),
                new XElement(C + "order", new XAttribute("val", index)),
                new XElement(C + "tx", new XElement(C + "v", s.Name)));
            ser.Add(new XElement(C + "spPr", s.Line
                ? new XElement(A + "ln", new XAttribute("w", 22225), new XAttribute("cap", "rnd"), Fill(s.Color), new XElement(A + "round"))
                : new XElement(A + "ln", new XAttribute("w", 19050), new XElement(A + "noFill"))));
            ser.Add(s.Markers
                ? new XElement(C + "marker", new XElement(C + "symbol", new XAttribute("val", "circle")), new XElement(C + "size", new XAttribute("val", 6)),
                    new XElement(C + "spPr", Fill(s.Color), new XElement(A + "ln", new XAttribute("w", 9525), Fill(s.Color))))
                : new XElement(C + "marker", new XElement(C + "symbol", new XAttribute("val", "none"))));
            ser.Add(new XElement(C + "xVal", new XElement(C + "numRef", new XElement(C + "f", s.XRef))),
                new XElement(C + "yVal", new XElement(C + "numRef", new XElement(C + "f", s.YRef))),
                new XElement(C + "smooth", new XAttribute("val", 0)));
            scatter.Add(ser);
            index++;
        }
        scatter.Add(new XElement(C + "axId", new XAttribute("val", 50010)), new XElement(C + "axId", new XAttribute("val", 50020)));

        var chart = new XElement(C + "chartSpace",
            new XAttribute(XNamespace.Xmlns + "c", C.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName),
            new XElement(C + "roundedCorners", new XAttribute("val", 0)),
            new XElement(C + "chart",
                new XElement(C + "title", RichText(spec.Title, 1200, true), new XElement(C + "overlay", new XAttribute("val", 0))),
                new XElement(C + "autoTitleDeleted", new XAttribute("val", 0)),
                new XElement(C + "plotArea", new XElement(C + "layout"), scatter,
                    AxisElement(50010, 50020, "b", spec.XTitle, spec.XFormat, false, spec.XMin, spec.XMax),
                    AxisElement(50020, 50010, "l", spec.YTitle, spec.YFormat, true)),
                new XElement(C + "legend", new XElement(C + "legendPos", new XAttribute("val", "b")), new XElement(C + "overlay", new XAttribute("val", 0))),
                new XElement(C + "plotVisOnly", new XAttribute("val", 1)),
                new XElement(C + "dispBlanksAs", new XAttribute("val", "gap"))));
        return new XDocument(chart);
    }

    private static byte[] Serialize(XDocument doc)
    {
        using var ms = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(ms, new System.Xml.XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            doc.Save(writer);
        return ms.ToArray();
    }
}
