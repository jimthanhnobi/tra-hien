using System.Text.RegularExpressions;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Import;

namespace TeaPremium.Api.Catalog;

internal sealed class ProductImportFileException(int status, int row, string column, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public ProductImportError Error { get; } = new(row, column, code, message);
}

internal static partial class ProductImportFile
{
    public const int MaxFile = TabularImportFile.MaxFile;
    public const int MaxRequest = TabularImportFile.MaxRequest;
    private const int MaxRows = 500;

    private static readonly ImportTableSchema ProductSchema = new(
        header => HeaderPattern().IsMatch(header) && ProductImportPlanner.RejectColumn(header) is null,
        ["name", "product_type_code"],
        MaxRows,
        ImportSheetMode.ExactName,
        "SanPham",
        "Tệp có hơn 500 dòng dữ liệu.");

    public static ImportProductLine[] Read(Stream input, string filename) => Read(input, filename, null);

    public static ImportProductLine[] Read(Stream input, string filename, string[]? openingColumns)
    {
        var schema = openingColumns is null
            ? ProductSchema
            : new ImportTableSchema(
                header => openingColumns.Contains(header, StringComparer.Ordinal),
                openingColumns,
                MaxRows,
                ImportSheetMode.SingleOnly,
                null,
                "Tệp có hơn 500 dòng dữ liệu.");

        try
        {
            var rows = TabularImportFile.Read(input, filename, schema);
            return rows.Select(x => new ImportProductLine(x.Row, x.Cells)).ToArray();
        }
        catch (TabularImportFileException error)
        {
            throw new ProductImportFileException(error.Status, error.Row, error.Column, error.Code, error.Message);
        }
    }

    public static ProductImportFileException Invalid(int row, string column, string code, string message) => new(400, row, column, code, message);
    public static ProductImportFileException Limit(int row, string column, string message) => new(413, row, column, "file_limit_exceeded", message);

    [GeneratedRegex("^(?:[pv]\\.)?[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();
}
