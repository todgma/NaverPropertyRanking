using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace NaverPropertyRanking.Services;

public sealed record ExcelExportColumn(string Name, string Header);

public sealed record ExcelExportRow(
    IReadOnlyDictionary<string, string> Values,
    bool HighlightMine = false,
    bool HighlightGroupHeader = false,
    bool IsSeparator = false,
    int OutlineLevel = 0,
    IReadOnlySet<string>? HighlightedColumns = null);

public static class ExcelExportService
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypeNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";

    public static void Export(
        string filePath,
        IReadOnlyList<ExcelExportColumn> listColumns,
        IReadOnlyList<ExcelExportRow> listRows,
        IReadOnlyList<ExcelExportColumn> detailColumns,
        IReadOnlyList<ExcelExportRow> detailRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(listColumns);
        ArgumentNullException.ThrowIfNull(listRows);
        ArgumentNullException.ThrowIfNull(detailColumns);
        ArgumentNullException.ThrowIfNull(detailRows);
        if (listColumns.Count == 0) throw new ArgumentException("Excel 목록 출력 컬럼이 없습니다.", nameof(listColumns));
        if (detailColumns.Count == 0) throw new ArgumentException("Excel 상세 출력 컬럼이 없습니다.", nameof(detailColumns));

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException("Excel 저장 경로를 확인할 수 없습니다.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                WriteContentTypes(archive);
                WriteRootRelationships(archive);
                WriteWorkbook(archive);
                WriteWorkbookRelationships(archive);
                WriteStyles(archive);
                WriteWorksheet(archive, "xl/worksheets/sheet1.xml", listColumns, listRows, includeOutlines: false);
                WriteWorksheet(archive, "xl/worksheets/sheet2.xml", detailColumns, detailRows, includeOutlines: false);
            }

            File.Move(temporaryPath, fullPath, true);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private static void WriteContentTypes(ZipArchive archive) =>
        WriteXml(archive, "[Content_Types].xml", writer =>
        {
            writer.WriteStartElement("Types", ContentTypeNamespace);
            WriteElement(writer, "Default", ContentTypeNamespace, ("Extension", "rels"), ("ContentType", "application/vnd.openxmlformats-package.relationships+xml"));
            WriteElement(writer, "Default", ContentTypeNamespace, ("Extension", "xml"), ("ContentType", "application/xml"));
            WriteElement(writer, "Override", ContentTypeNamespace, ("PartName", "/xl/workbook.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
            WriteElement(writer, "Override", ContentTypeNamespace, ("PartName", "/xl/styles.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"));
            WriteElement(writer, "Override", ContentTypeNamespace, ("PartName", "/xl/worksheets/sheet1.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
            WriteElement(writer, "Override", ContentTypeNamespace, ("PartName", "/xl/worksheets/sheet2.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
            writer.WriteEndElement();
        });

    private static void WriteRootRelationships(ZipArchive archive) =>
        WriteXml(archive, "_rels/.rels", writer =>
        {
            writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
            WriteElement(
                writer,
                "Relationship",
                PackageRelationshipNamespace,
                ("Id", "rId1"),
                ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                ("Target", "xl/workbook.xml"));
            writer.WriteEndElement();
        });

    private static void WriteWorkbook(ZipArchive archive) =>
        WriteXml(archive, "xl/workbook.xml", writer =>
        {
            writer.WriteStartElement("workbook", SpreadsheetNamespace);
            writer.WriteAttributeString("xmlns", "r", null, RelationshipNamespace);
            writer.WriteStartElement("sheets", SpreadsheetNamespace);
            WriteSheet(writer, "목록", 1, "rId1");
            WriteSheet(writer, "상세", 2, "rId2");
            writer.WriteEndElement();
            writer.WriteEndElement();
        });

    private static void WriteSheet(XmlWriter writer, string name, int sheetId, string relationshipId)
    {
        writer.WriteStartElement("sheet", SpreadsheetNamespace);
        writer.WriteAttributeString("name", name);
        writer.WriteAttributeString("sheetId", sheetId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("r", "id", RelationshipNamespace, relationshipId);
        writer.WriteEndElement();
    }

    private static void WriteWorkbookRelationships(ZipArchive archive) =>
        WriteXml(archive, "xl/_rels/workbook.xml.rels", writer =>
        {
            writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
            WriteElement(writer, "Relationship", PackageRelationshipNamespace, ("Id", "rId1"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), ("Target", "worksheets/sheet1.xml"));
            WriteElement(writer, "Relationship", PackageRelationshipNamespace, ("Id", "rId2"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), ("Target", "worksheets/sheet2.xml"));
            WriteElement(writer, "Relationship", PackageRelationshipNamespace, ("Id", "rId3"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"), ("Target", "styles.xml"));
            writer.WriteEndElement();
        });

    private static void WriteStyles(ZipArchive archive) =>
        WriteXml(archive, "xl/styles.xml", writer =>
        {
            writer.WriteStartElement("styleSheet", SpreadsheetNamespace);

            writer.WriteStartElement("fonts", SpreadsheetNamespace);
            writer.WriteAttributeString("count", "3");
            WriteFont(writer, bold: false, color: null);
            WriteFont(writer, bold: true, color: "FFFFFFFF");
            WriteFont(writer, bold: true, color: "FF00693E");
            writer.WriteEndElement();

            writer.WriteStartElement("fills", SpreadsheetNamespace);
            writer.WriteAttributeString("count", "6");
            WriteFill(writer, "none", null);
            WriteFill(writer, "gray125", null);
            WriteFill(writer, "solid", "FF212E2A");
            WriteFill(writer, "solid", "FFE8F7EF");
            WriteFill(writer, "solid", "FFFFF6C2");
            WriteFill(writer, "solid", "FFE7E6E6");
            writer.WriteEndElement();

            writer.WriteStartElement("borders", SpreadsheetNamespace);
            writer.WriteAttributeString("count", "2");
            WriteBorder(writer, null);
            WriteBorder(writer, "FFDCE5E1");
            writer.WriteEndElement();

            writer.WriteStartElement("cellStyleXfs", SpreadsheetNamespace);
            writer.WriteAttributeString("count", "1");
            WriteXf(writer, 0, 0, 0, applyAlignment: false, centered: false);
            writer.WriteEndElement();

            writer.WriteStartElement("cellXfs", SpreadsheetNamespace);
            writer.WriteAttributeString("count", "7");
            WriteXf(writer, 0, 0, 1, applyAlignment: true, centered: false);
            WriteXf(writer, 1, 2, 1, applyAlignment: true, centered: true);
            WriteXf(writer, 2, 3, 1, applyAlignment: true, centered: false);
            WriteXf(writer, 0, 0, 1, applyAlignment: true, centered: false);
            WriteXf(writer, 0, 4, 1, applyAlignment: true, centered: false);
            WriteXf(writer, 0, 5, 1, applyAlignment: true, centered: false);
            WriteXf(writer, 0, 0, 0, applyAlignment: false, centered: false);
            writer.WriteEndElement();

            writer.WriteStartElement("cellStyles", SpreadsheetNamespace);
            writer.WriteAttributeString("count", "1");
            WriteElement(writer, "cellStyle", SpreadsheetNamespace, ("name", "Normal"), ("xfId", "0"), ("builtinId", "0"));
            writer.WriteEndElement();

            writer.WriteEndElement();
        });

    private static void WriteFont(XmlWriter writer, bool bold, string? color)
    {
        writer.WriteStartElement("font", SpreadsheetNamespace);
        if (bold) writer.WriteElementString("b", SpreadsheetNamespace, string.Empty);
        writer.WriteStartElement("sz", SpreadsheetNamespace);
        writer.WriteAttributeString("val", "10");
        writer.WriteEndElement();
        writer.WriteStartElement("name", SpreadsheetNamespace);
        writer.WriteAttributeString("val", "맑은 고딕");
        writer.WriteEndElement();
        if (color is not null)
        {
            writer.WriteStartElement("color", SpreadsheetNamespace);
            writer.WriteAttributeString("rgb", color);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void WriteFill(XmlWriter writer, string patternType, string? color)
    {
        writer.WriteStartElement("fill", SpreadsheetNamespace);
        writer.WriteStartElement("patternFill", SpreadsheetNamespace);
        writer.WriteAttributeString("patternType", patternType);
        if (color is not null)
        {
            writer.WriteStartElement("fgColor", SpreadsheetNamespace);
            writer.WriteAttributeString("rgb", color);
            writer.WriteEndElement();
            writer.WriteStartElement("bgColor", SpreadsheetNamespace);
            writer.WriteAttributeString("indexed", "64");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteBorder(XmlWriter writer, string? color)
    {
        writer.WriteStartElement("border", SpreadsheetNamespace);
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            writer.WriteStartElement(side, SpreadsheetNamespace);
            if (color is not null)
            {
                writer.WriteAttributeString("style", "thin");
                writer.WriteStartElement("color", SpreadsheetNamespace);
                writer.WriteAttributeString("rgb", color);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteElementString("diagonal", SpreadsheetNamespace, string.Empty);
        writer.WriteEndElement();
    }

    private static void WriteXf(
        XmlWriter writer,
        int fontId,
        int fillId,
        int borderId,
        bool applyAlignment,
        bool centered)
    {
        writer.WriteStartElement("xf", SpreadsheetNamespace);
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", fontId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fillId", fillId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("borderId", borderId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("xfId", "0");
        if (fontId != 0) writer.WriteAttributeString("applyFont", "1");
        if (fillId != 0) writer.WriteAttributeString("applyFill", "1");
        if (borderId != 0) writer.WriteAttributeString("applyBorder", "1");
        if (applyAlignment)
        {
            writer.WriteAttributeString("applyAlignment", "1");
            writer.WriteStartElement("alignment", SpreadsheetNamespace);
            writer.WriteAttributeString("vertical", "center");
            writer.WriteAttributeString("horizontal", centered ? "center" : "left");
            writer.WriteAttributeString("wrapText", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void WriteWorksheet(
        ZipArchive archive,
        string entryName,
        IReadOnlyList<ExcelExportColumn> columns,
        IReadOnlyList<ExcelExportRow> rows,
        bool includeOutlines)
    {
        WriteXml(archive, entryName, writer =>
        {
            var finalRow = Math.Max(1, rows.Count + 1);
            var finalColumn = ColumnName(columns.Count);
            writer.WriteStartElement("worksheet", SpreadsheetNamespace);
            if (includeOutlines)
            {
                writer.WriteStartElement("sheetPr", SpreadsheetNamespace);
                writer.WriteStartElement("outlinePr", SpreadsheetNamespace);
                writer.WriteAttributeString("summaryBelow", "0");
                writer.WriteAttributeString("showOutlineSymbols", "1");
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteStartElement("dimension", SpreadsheetNamespace);
            writer.WriteAttributeString("ref", $"A1:{finalColumn}{finalRow}");
            writer.WriteEndElement();

            writer.WriteStartElement("sheetViews", SpreadsheetNamespace);
            writer.WriteStartElement("sheetView", SpreadsheetNamespace);
            writer.WriteAttributeString("showGridLines", "0");
            writer.WriteAttributeString("workbookViewId", "0");
            writer.WriteStartElement("pane", SpreadsheetNamespace);
            writer.WriteAttributeString("ySplit", "1");
            writer.WriteAttributeString("topLeftCell", "A2");
            writer.WriteAttributeString("activePane", "bottomLeft");
            writer.WriteAttributeString("state", "frozen");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("sheetFormatPr", SpreadsheetNamespace);
            writer.WriteAttributeString("defaultRowHeight", "21");
            if (includeOutlines) writer.WriteAttributeString("outlineLevelRow", "1");
            writer.WriteEndElement();

            WriteColumns(writer, columns, rows);

            writer.WriteStartElement("sheetData", SpreadsheetNamespace);
            WriteHeaderRow(writer, 1, columns.Select(column => column.Header), styleId: 1, height: 26);
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                WriteRow(
                    writer,
                    index + 2,
                    columns,
                    row,
                    styleId: row.IsSeparator ? 6 : row.HighlightGroupHeader ? 5 : row.HighlightMine ? 2 : row.OutlineLevel > 0 ? 3 : 0,
                    outlineLevel: includeOutlines ? row.OutlineLevel : 0,
                    height: 22);
            }
            writer.WriteEndElement();

            writer.WriteStartElement("autoFilter", SpreadsheetNamespace);
            writer.WriteAttributeString("ref", $"A1:{finalColumn}{finalRow}");
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
    }

    private static void WriteColumns(
        XmlWriter writer,
        IReadOnlyList<ExcelExportColumn> columns,
        IReadOnlyList<ExcelExportRow> rows)
    {
        writer.WriteStartElement("cols", SpreadsheetNamespace);
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var maxWidth = Math.Max(DisplayWidth(column.Header), rows
                .Select(row => row.Values.TryGetValue(column.Name, out var value) ? DisplayWidth(value) : 0)
                .DefaultIfEmpty(0)
                .Max());
            var width = Math.Clamp(maxWidth + 2, 9, column.Name == "Description" ? 48 : 34);
            writer.WriteStartElement("col", SpreadsheetNamespace);
            writer.WriteAttributeString("min", (index + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("max", (index + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("width", width.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static int DisplayWidth(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        return value.Sum(character => character > 0x7f ? 2 : 1);
    }

    private static void WriteRow(
        XmlWriter writer,
        int rowNumber,
        IReadOnlyList<ExcelExportColumn> columns,
        ExcelExportRow row,
        int styleId,
        int outlineLevel,
        int height)
    {
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("ht", height.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("customHeight", "1");
        if (outlineLevel > 0)
        {
            writer.WriteAttributeString("outlineLevel", outlineLevel.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("hidden", "0");
        }

        var columnIndex = 1;
        foreach (var column in columns)
        {
            var value = row.Values.TryGetValue(column.Name, out var cellValue) ? cellValue : string.Empty;
            var cellStyleId = row.HighlightedColumns?.Contains(column.Name) == true ? 4 : styleId;
            writer.WriteStartElement("c", SpreadsheetNamespace);
            writer.WriteAttributeString("r", $"{ColumnName(columnIndex++)}{rowNumber}");
            writer.WriteAttributeString("s", cellStyleId.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is", SpreadsheetNamespace);
            writer.WriteStartElement("t", SpreadsheetNamespace);
            if (!string.IsNullOrEmpty(value) && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
                writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
            writer.WriteString(value ?? string.Empty);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void WriteHeaderRow(
        XmlWriter writer,
        int rowNumber,
        IEnumerable<string> values,
        int styleId,
        int height)
    {
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("ht", height.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("customHeight", "1");

        var columnIndex = 1;
        foreach (var value in values)
        {
            WriteCell(writer, $"{ColumnName(columnIndex++)}{rowNumber}", value, styleId);
        }
        writer.WriteEndElement();
    }

    private static void WriteCell(XmlWriter writer, string reference, string? value, int styleId)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", reference);
        writer.WriteAttributeString("s", styleId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("t", "inlineStr");
        writer.WriteStartElement("is", SpreadsheetNamespace);
        writer.WriteStartElement("t", SpreadsheetNamespace);
        if (!string.IsNullOrEmpty(value) && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
            writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
        writer.WriteString(value ?? string.Empty);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string ColumnName(int oneBasedIndex)
    {
        var value = oneBasedIndex;
        var result = string.Empty;
        while (value > 0)
        {
            value--;
            result = (char)('A' + value % 26) + result;
            value /= 26;
        }
        return result;
    }

    private static void WriteXml(ZipArchive archive, string entryName, Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            CloseOutput = false
        });
        writer.WriteStartDocument();
        write(writer);
        writer.WriteEndDocument();
    }

    private static void WriteElement(
        XmlWriter writer,
        string name,
        string xmlNamespace,
        params (string Name, string Value)[] attributes)
    {
        writer.WriteStartElement(name, xmlNamespace);
        foreach (var attribute in attributes) writer.WriteAttributeString(attribute.Name, attribute.Value);
        writer.WriteEndElement();
    }
}
