using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeaPremium.Application.Settings;

namespace TeaPremium.Application.AdministrativeData;

public sealed record ProvinceData(string Code, string Name, bool IsActive = true, int SortOrder = 0);
public sealed record WardData(string Code, string ProvinceCode, string Name, bool IsActive = true);
public sealed record AdministrativeCatalog(ProvinceData[] Provinces, WardData[] Wards)
{
    private static readonly JsonSerializerOptions DigestOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string Digest() => SettingsJson.Hash(JsonSerializer.Serialize(new object[]
    {
        "vn-admin-catalog/1",
        Provinces.OrderBy(x => x.Code, StringComparer.Ordinal).Select(x => new object[] { x.Code, x.Name, x.IsActive, x.SortOrder }).ToArray(),
        Wards.OrderBy(x => x.Code, StringComparer.Ordinal).Select(x => new object[] { x.Code, x.ProvinceCode, x.Name, x.IsActive }).ToArray()
    }, DigestOptions));
}
public sealed record AdministrativePayload(AdministrativeCatalog Catalog, int ProvinceOnlyRows, int DataRows);

public static partial class AdministrativeCsv
{
    public const int MaxBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static AdministrativePayload Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaxBytes) throw Invalid();
        var text = StrictUtf8.GetString(bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var rows = Rows(text);
        if (rows.Count < 2 || !rows[0].SequenceEqual(new[] { "province_code", "province_name", "ward_code", "ward_name" })) throw Invalid();
        var provinces = new Dictionary<string, ProvinceData>(StringComparer.Ordinal);
        var wards = new Dictionary<string, WardData>(StringComparer.Ordinal);
        var line = 1;
        foreach (var row in rows.Skip(1))
        {
            line++;
            if (row.Length != 4 || !ProvinceCode().IsMatch(row[0]) || !Name(row[1])) throw Invalid(line);
            if (row[2].Length == 0 && row[3].Length == 0)
            {
                if (!provinces.TryAdd(row[0], new(row[0], row[1].Normalize(NormalizationForm.FormC)))) throw Invalid(line);
                continue;
            }
            if (!WardCode().IsMatch(row[2]) || !Name(row[3]) || !provinces.TryGetValue(row[0], out var parent)
                || parent.Name != row[1].Normalize(NormalizationForm.FormC)
                || !wards.TryAdd(row[2], new(row[2], row[0], row[3].Normalize(NormalizationForm.FormC)))) throw Invalid(line);
        }
        return new(new(provinces.Values.ToArray(), wards.Values.ToArray()), provinces.Count, rows.Count - 1);
    }

    public static byte[] NormalizePinnedJson(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw Invalid();
        using var doc = JsonDocument.Parse(StrictUtf8.GetString(bytes));
        if (doc.RootElement.ValueKind != JsonValueKind.Array || SettingsJson.HasDuplicateProperties(doc.RootElement)) throw Invalid();
        var output = new StringBuilder("province_code,province_name,ward_code,ward_name\n");
        foreach (var p in doc.RootElement.EnumerateArray().OrderBy(p => p.GetProperty("Code").GetString(), StringComparer.Ordinal))
        {
            if (p.GetProperty("Type").GetString() != "province" || p.TryGetProperty("Districts", out _)) throw Invalid();
            var code = p.GetProperty("Code").GetString() ?? throw Invalid();
            var name = (p.GetProperty("FullName").GetString() ?? throw Invalid()).Normalize(NormalizationForm.FormC);
            WriteRow(output, [code, name, "", ""]);
            foreach (var w in p.GetProperty("Wards").EnumerateArray().OrderBy(w => w.GetProperty("Code").GetString(), StringComparer.Ordinal))
            {
                if (w.GetProperty("Type").GetString() != "ward" || w.GetProperty("ProvinceCode").GetString() != code) throw Invalid();
                WriteRow(output, [code, name, w.GetProperty("Code").GetString() ?? throw Invalid(),
                    (w.GetProperty("FullName").GetString() ?? throw Invalid()).Normalize(NormalizationForm.FormC)]);
            }
        }
        var csv = StrictUtf8.GetBytes(output.ToString());
        _ = Parse(csv); // Validate all codes, hierarchy and duplicates, not just the projected count.
        return csv;
    }

    private static void WriteRow(StringBuilder output, string[] row)
    {
        output.AppendJoin(',', row.Select(x => x.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + x.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : x));
        output.Append('\n');
    }

    private static List<string[]> Rows(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; closed = true; }
                }
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0 && !closed) { quoted = true; continue; }
            if (c == ',' || c is '\r' or '\n')
            {
                row.Add(field.ToString()); field.Clear(); closed = false;
                if (c == ',') continue;
                if (c == '\r') { if (i + 1 >= text.Length || text[++i] != '\n') throw Invalid(rows.Count + 1); }
                rows.Add(row.ToArray()); row.Clear();
                continue;
            }
            if (closed || c == '"') throw Invalid(rows.Count + 1);
            field.Append(c);
        }
        if (quoted) throw Invalid(rows.Count + 1);
        if (field.Length > 0 || row.Count > 0 || closed) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
    private static bool Name(string value) => !string.IsNullOrWhiteSpace(value) && !value.Any(c => char.IsControl(c) || c == '\uFEFF');
    private static AdministrativeCsvException Invalid(int line = 1) => new(line);
    [GeneratedRegex("^[0-9]{2}$", RegexOptions.CultureInvariant)] private static partial Regex ProvinceCode();
    [GeneratedRegex("^[0-9]{5}$", RegexOptions.CultureInvariant)] private static partial Regex WardCode();
}

public sealed class AdministrativeCsvException(int line) : FormatException("invalid_administrative_csv")
{
    public int Line { get; } = line;
}
