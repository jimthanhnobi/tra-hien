using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace TeaPremium.Application.Import;

public enum ImportSheetMode
{
    SingleOnly,
    ExactName,
    SingleOrNamed
}

public sealed record ImportTableSchema(
    Func<string, bool> IsAllowedHeader,
    string[] RequiredColumns,
    int MaxRows,
    ImportSheetMode SheetMode,
    string? SheetName,
    string? RowLimitMessage = null);

public sealed record TabularImportLine(int Row, IReadOnlyDictionary<string, string> Cells)
{
    public string Cell(string column) => Cells.GetValueOrDefault(column, "");
}

public sealed class TabularImportFileException(int status, int row, string column, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public int Row { get; } = row;
    public string Column { get; } = column;
    public string Code { get; } = code;
}

/// <summary>
/// Pure-BCL CSV (strict UTF-8 with optional leading BOM) and XLSX (OOXML, macro/formula/zip-bomb hardened) table reader
/// shared across P1 product/opening-stock imports and P2-03 legacy customer imports.
/// </summary>
public static partial class TabularImportFile
{
    public const int MaxFile = 5 * 1024 * 1024;
    public const int MaxRequest = MaxFile + 64 * 1024;
    public const int MaxExpanded = 32 * 1024 * 1024;
    public const int MaxColumns = 100;
    private const int MaxZipEntries = 4096;
    private const int MaxXmlDepth = 48;
    private const string Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Package = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Content = "http://schemas.openxmlformats.org/package/2006/content-types";

    public static TabularImportLine[] Read(Stream input, string filename, ImportTableSchema schema)
    {
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".csv" => Bind(Csv(input), schema),
                ".xlsx" => Bind(Excel(input, schema.SheetMode, schema.SheetName), schema),
                _ => throw Unsupported()
            };
        }
        catch (DecoderFallbackException) { throw Invalid(1, "file", "invalid_utf8", "CSV phải dùng mã hóa UTF-8."); }
        catch (Exception error) when (error is XmlException or InvalidDataException or OverflowException)
        { throw Invalid(1, "file", "malformed_file", "Không đọc được cấu trúc tệp."); }
    }

    private static TabularImportLine[] Bind(IEnumerable<(int Row, string[] Cells)> records, ImportTableSchema schema)
    {
        using var enumerator = records.GetEnumerator();
        if (!enumerator.MoveNext() || enumerator.Current.Row != 1)
            throw Invalid(1, "file", "missing_headers", "Cần tiêu đề ở dòng 1.");

        var headers = enumerator.Current.Cells.Select((x, i) => (Name: x.Trim(), Index: i + 1)).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            if (!schema.IsAllowedHeader(header.Name))
                throw Invalid(1, $"#{header.Index}", "invalid_header", "Tên cột không có trong mẫu.");
            if (!seen.Add(header.Name))
                throw Invalid(1, header.Name, "duplicate_header", "Cột xuất hiện nhiều lần.");
        }
        foreach (var required in schema.RequiredColumns)
            if (!seen.Contains(required)) throw Invalid(1, required, "missing_header", "Thiếu cột bắt buộc.");

        var output = new List<TabularImportLine>();
        var dataRows = 0;
        long textLength = 0;
        var rowLimitMessage = schema.RowLimitMessage ?? $"Tệp có hơn {schema.MaxRows} dòng dữ liệu.";
        while (enumerator.MoveNext())
        {
            var (row, cells) = enumerator.Current;
            textLength += cells.Sum(x => (long)x.Length);
            if (textLength > MaxExpanded) throw Limit(row, "file", "Dung lượng dữ liệu ô vượt giới hạn.");
            if (cells.Skip(headers.Length).Any(x => !string.IsNullOrWhiteSpace(x)))
                throw Invalid(row, "file", "extra_cells", "Có ô nằm ngoài các cột đã khai báo.");
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            if (++dataRows > schema.MaxRows) throw Limit(row, "file", rowLimitMessage);
            var values = headers.ToDictionary(x => x.Name, x => x.Index <= cells.Length ? cells[x.Index - 1].Trim() : "", StringComparer.Ordinal);
            output.Add(new(row, values));
        }
        if (output.Count == 0) throw Invalid(2, "file", "missing_rows", "Tệp cần ít nhất một dòng dữ liệu.");
        return output.ToArray();
    }

    private static IEnumerable<(int Row, string[] Cells)> Csv(Stream input)
    {
        // Decode only UTF-8. StreamReader BOM detection would also accept UTF-16 and UTF-32.
        // Never reject leading '+' in CSV values because E.164 phones start with +84.
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        if (reader.Peek() == '\uFEFF') reader.Read();
        var physicalRow = 1;
        var startRow = 1;
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        for (var next = reader.Read(); next >= 0; next = reader.Read())
        {
            var ch = (char)next;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else { quoted = false; closed = true; }
                }
                else
                {
                    field.Append(ch);
                    if (ch == '\n' || ch == '\r' && reader.Peek() != '\n') physicalRow++;
                }
                continue;
            }
            if (ch == ',' || ch is '\n' or '\r')
            {
                fields.Add(field.ToString()); field.Clear(); closed = false;
                if (fields.Count > MaxColumns || ch == ',' && fields.Count == MaxColumns)
                    throw Limit(startRow, "file", "Tệp có hơn 100 cột.");
                if (ch == ',') continue;
                if (ch == '\r' && reader.Peek() == '\n') reader.Read();
                yield return (startRow, fields.ToArray());
                fields.Clear();
                startRow = ++physicalRow;
                continue;
            }
            if (ch == '"' && field.Length == 0 && !closed) { quoted = true; continue; }
            if (closed || ch == '"') throw Invalid(startRow, $"#{fields.Count + 1}", "malformed_csv", "Dấu ngoặc kép CSV không hợp lệ.");
            field.Append(ch);
        }
        if (quoted) throw Invalid(startRow, $"#{fields.Count + 1}", "malformed_csv", "Ô CSV thiếu dấu đóng ngoặc kép.");
        if (closed || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return (startRow, fields.ToArray());
        }
    }

    private static IEnumerable<(int Row, string[] Cells)> Excel(Stream input, ImportSheetMode sheetMode, string? sheetName)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var parts = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long expanded = 0;
        if (archive.Entries.Count > MaxZipEntries) throw Limit(1, "file", "XLSX có quá nhiều thành phần.");
        foreach (var entry in archive.Entries)
        {
            if (!parts.TryAdd(entry.FullName, entry)) throw Invalid(1, "file", "duplicate_part", "XLSX có thành phần trùng.");
            if (entry.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)) throw Unsupported();
            if (entry.Length > MaxExpanded - expanded) throw Limit(1, "file", "XLSX giải nén vượt 32 MiB.");
            expanded += entry.Length;
        }

        XmlReader OpenPart(string path)
        {
            if (!parts.TryGetValue(path, out var entry)) throw Invalid(1, "file", "missing_part", "Thiếu thành phần bắt buộc của XLSX.");
            return XmlReader.Create(new XlsxPartStream(entry), new()
            {
                CloseInput = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxExpanded
            });
        }

        const string workbookType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
        var hasWorkbook = false;
        using (var content = OpenPart("[Content_Types].xml"))
        {
            foreach (var element in Elements(content))
            {
                if (element.Depth == 0 && !Is(element, "Types", Content)) throw Unsupported();
                var type = element.GetAttribute("ContentType");
                if (type is null) continue;
                if (type.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)) throw Unsupported();
                hasWorkbook |= type == workbookType;
            }
        }
        if (!hasWorkbook) throw Unsupported();

        var sheets = new List<(string? Name, string? RelId)>();
        using (var workbook = OpenPart("xl/workbook.xml"))
        {
            foreach (var element in Elements(workbook))
            {
                if (!Is(element, "sheet")) continue;
                sheets.Add((element.GetAttribute("name"), element.GetAttribute("id", Office)));
            }
        }

        string? id = ResolveSheetId(sheets, sheetMode, sheetName);
        if (string.IsNullOrEmpty(id)) throw MissingSheet(sheetMode, sheetName);

        string? worksheetPath = null;
        string? sharedPath = null;
        using (var relationships = OpenPart("xl/_rels/workbook.xml.rels"))
        {
            foreach (var element in Elements(relationships))
            {
                if (element.Depth != 1 || !Is(element, "Relationship", Package)) continue;
                var type = element.GetAttribute("Type");
                if (type == Office + "/worksheet" && element.GetAttribute("Id") == id)
                    worksheetPath = ReadTarget(element, worksheetPath);
                if (type == Office + "/sharedStrings")
                    sharedPath = ReadTarget(element, sharedPath);
            }
        }
        if (worksheetPath is null) throw Invalid(1, "file", "missing_part", "Thiếu thành phần bắt buộc của XLSX.");

        var shared = new List<string>();
        if (sharedPath is not null)
        {
            using var strings = OpenPart(sharedPath);
            foreach (var item in Elements(strings))
            {
                if (item.Depth == 0 && !Is(item, "sst"))
                    throw Invalid(1, "file", "malformed_file", "Bảng chuỗi dùng chung không hợp lệ.");
                if (item.Depth == 1 && Is(item, "si")) shared.Add(ReadRichText(item));
            }
        }

        using var worksheet = OpenPart(worksheetPath);
        var hasData = false;
        foreach (var element in Elements(worksheet))
        {
            if (element.Depth == 0 && !Is(element, "worksheet")) throw MalformedSheet(sheetMode, sheetName);
            if (element.Depth != 1 || !Is(element, "sheetData")) continue;
            if (hasData) throw MalformedSheet(sheetMode, sheetName);
            hasData = true;
            var lastRow = 0;
            foreach (var row in Elements(element, element.Depth))
            {
                if (row.Depth != 2 || !Is(row, "row")) continue;
                if (!int.TryParse(row.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var rowNumber) ||
                    rowNumber <= lastRow || rowNumber > 1_048_576)
                    throw Invalid(lastRow + 1, "file", "invalid_row", "Số hàng XLSX không hợp lệ.");
                lastRow = rowNumber;
                yield return (rowNumber, ReadRow(row, rowNumber, shared));
            }
        }
        if (!hasData) throw MalformedSheet(sheetMode, sheetName);
    }

    private static string? ResolveSheetId(List<(string? Name, string? RelId)> sheets, ImportSheetMode mode, string? sheetName)
    {
        if (sheets.Count == 0) return null;
        return mode switch
        {
            ImportSheetMode.SingleOnly => sheets.Count == 1 ? sheets[0].RelId : null,
            ImportSheetMode.ExactName => SingleNamed(sheets, sheetName),
            ImportSheetMode.SingleOrNamed => sheets.Count == 1 ? sheets[0].RelId : SingleNamed(sheets, sheetName),
            _ => null
        };
    }

    private static string? SingleNamed(List<(string? Name, string? RelId)> sheets, string? sheetName)
    {
        if (string.IsNullOrEmpty(sheetName)) return null;
        string? matched = null;
        var count = 0;
        foreach (var (name, relId) in sheets)
        {
            if (!string.Equals(name, sheetName, StringComparison.Ordinal)) continue;
            if (++count > 1) return null;
            matched = relId;
        }
        return matched;
    }

    private static TabularImportFileException MissingSheet(ImportSheetMode mode, string? sheetName) => Invalid(1, "file", "missing_sheet",
        mode switch
        {
            ImportSheetMode.SingleOnly => "Tệp phải có đúng một trang tính.",
            ImportSheetMode.ExactName => $"Cần một trang tính tên {sheetName}.",
            ImportSheetMode.SingleOrNamed => $"Tệp phải có một trang tính hoặc chứa duy nhất trang tính tên {sheetName}.",
            _ => "Không tìm thấy trang tính hợp lệ."
        });

    private static TabularImportFileException MalformedSheet(ImportSheetMode mode, string? sheetName) => Invalid(1, "file", "malformed_sheet",
        mode == ImportSheetMode.ExactName ? $"Trang {sheetName} thiếu bảng dữ liệu hợp lệ." : "Trang tính thiếu bảng dữ liệu hợp lệ.");

    private static string ReadTarget(XmlReader element, string? previous)
    {
        var target = element.GetAttribute("Target");
        if (previous is not null || element.GetAttribute("TargetMode") == "External" ||
            string.IsNullOrEmpty(target) || target.Split('/').Any(x => x is "." or "..") ||
            target.IndexOfAny(['\\', ':', '?', '#', '%']) >= 0)
            throw Invalid(1, "file", "invalid_part", "Tham chiếu thành phần XLSX không hợp lệ.");
        return target.StartsWith('/') ? target[1..] : "xl/" + target;
    }

    private static string[] ReadRow(XmlReader row, int rowNumber, List<string> shared)
    {
        var cells = new List<string>();
        var cellDepth = row.Depth + 1;
        foreach (var cell in Elements(row, row.Depth))
        {
            if (cell.Depth != cellDepth || !Is(cell, "c")) continue;
            var address = cell.GetAttribute("r") ?? "";
            var match = CellPattern().Match(address);
            if (!match.Success || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var cellRow) || cellRow != rowNumber)
                throw Invalid(rowNumber, "file", "invalid_cell", "Địa chỉ ô XLSX không hợp lệ.");
            var column = match.Groups[1].Value.Aggregate(0, (index, ch) => index * 26 + ch - 'A' + 1);
            if (column > MaxColumns) throw Limit(rowNumber, address, "Tệp có hơn 100 cột.");
            if (column <= cells.Count) throw Invalid(rowNumber, address, "invalid_cell_order", "Thứ tự ô XLSX không hợp lệ.");
            while (cells.Count < column - 1) cells.Add("");
            cells.Add(ReadCell(cell, rowNumber, address, shared));
        }
        return cells.ToArray();
    }

    private static string ReadCell(XmlReader cell, int row, string address, List<string> shared)
    {
        var type = cell.GetAttribute("t");
        var value = "";
        var inline = "";
        var valueDepth = cell.Depth + 1;
        foreach (var element in Elements(cell, cell.Depth))
        {
            if (element.Depth != valueDepth) continue;
            if (Is(element, "f")) throw Invalid(row, address, "formula_not_allowed", "Chuyển công thức thành giá trị trước khi nhập.");
            if (Is(element, "v")) value = ReadText(element);
            if (Is(element, "is")) inline = ReadRichText(element);
        }
        return type switch
        {
            "inlineStr" => inline,
            "s" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < shared.Count => shared[index],
            "b" when value is "0" or "1" => value == "1" ? "true" : "false",
            null or "n" or "str" => value,
            _ => throw Invalid(row, address, "unsupported_cell", "Kiểu ô XLSX không được hỗ trợ.")
        };
    }

    private static string ReadRichText(XmlReader container)
    {
        var childDepth = container.Depth + 1;
        var text = new StringBuilder();
        var run = false;
        foreach (var element in Elements(container, container.Depth))
        {
            if (element.Depth == childDepth) run = Is(element, "r");
            if ((element.Depth == childDepth || (element.Depth == childDepth + 1 && run)) && Is(element, "t")) text.Append(ReadText(element));
        }
        return text.ToString();
    }

    private static string ReadText(XmlReader element)
    {
        if (element.IsEmptyElement) return "";
        var parentDepth = element.Depth;
        var text = new StringBuilder();
        while (element.Read())
        {
            if (element.NodeType == XmlNodeType.EndElement && element.Depth == parentDepth) return text.ToString();
            if (element.NodeType == XmlNodeType.Element) throw new XmlException("Unexpected child in XLSX value.");
            if (element.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                text.Append(element.Value);
        }
        throw new XmlException("Unclosed XLSX value.");
    }

    private static IEnumerable<XmlReader> Elements(XmlReader reader, int? parentDepth = null)
    {
        if (parentDepth is not null && reader.IsEmptyElement) yield break;
        while (reader.Read())
        {
            if (reader.Depth > MaxXmlDepth) throw Invalid(1, "file", "invalid_xml_depth", "Thành phần XLSX có cấu trúc không hợp lệ.");
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == parentDepth) yield break;
            if (reader.NodeType == XmlNodeType.Element) yield return reader;
        }
    }

    private static bool Is(XmlReader reader, string name, string ns = Sheet) => reader.LocalName == name && reader.NamespaceURI == ns;

    // The ZIP directory supplies the aggregate budget. Also check actual decompression
    // against each advertised entry length, without buffering complete XML parts.
    private sealed class XlsxPartStream(ZipArchiveEntry entry) : Stream
    {
        private readonly Stream source = entry.Open();
        private long remaining = entry.Length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            // Read at most one byte beyond the declared size to detect a dishonest directory.
            var count = source.Read(buffer[..(int)Math.Min(buffer.Length, remaining + 1)]);
            remaining -= count;
            if (remaining < 0) throw Limit(1, "file", "XLSX giải nén vượt dung lượng đã khai báo.");
            if (count == 0 && remaining > 0)
                throw Invalid(1, "file", "invalid_part_length", "Kích thước thành phần XLSX không hợp lệ.");
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }

    public static TabularImportFileException Invalid(int row, string column, string code, string message) => new(400, row, column, code, message);
    public static TabularImportFileException Limit(int row, string column, string message) => new(413, row, column, "file_limit_exceeded", message);
    public static TabularImportFileException Unsupported() => new(415, 1, "file", "unsupported_format", "Chọn tệp CSV UTF-8 hoặc XLSX không có macro.");

    [GeneratedRegex("^([A-Z]{1,3})([1-9][0-9]{0,6})$", RegexOptions.CultureInvariant)]
    private static partial Regex CellPattern();
}
