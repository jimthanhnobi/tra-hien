using System.Globalization;
using System.Net;
using System.Text.Json;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Catalog;

namespace TeaPremium.Application.Catalog;

public sealed record ImportProductLine(int Row, IReadOnlyDictionary<string, string> Cells)
{
    public string Cell(string column) => Cells.GetValueOrDefault(column, "");
}

public sealed record ProductImportError(int Row, string Column, string Code, string Message);
public sealed record ProductImportRow(int Row, string? VariantName, long? PriceVnd, string? Sku,
    bool GenerateOnApply, Guid? VariantId, int? WeightG, JsonElement? PackageDimensions, IReadOnlyList<ProductValue> Attributes,
    IReadOnlyList<ProductImportError> Errors);
public sealed record ProductImportGroupResult(string Operation, string Status, string? Slug, Guid? ProductId,
    string Name, IReadOnlyList<string> Warnings, IReadOnlyList<ProductImportRow> Rows);
public sealed record ProductImportSummary(int Groups, int Rows, int Ready, int Committed, int Failed, int Unknown, int NotProcessed);
public sealed record ProductImportResult(ProductImportSummary Summary, IReadOnlyList<ProductImportGroupResult> Groups,
    IReadOnlyList<ProductImportRow> Rows);
public sealed record ImportType(Guid Id, string? Prefix);
public sealed record ImportMaterial(Guid Id, string? Code);
public sealed record ImportAttributeReference(TypedAttributeDefinition Definition, IReadOnlyDictionary<string, TypedAttributeOption> Options);

public interface IProductImportReference
{
    Task<ImportType?> TypeAsync(string code, CancellationToken ct);
    Task<ImportMaterial?> MaterialAsync(string slug, CancellationToken ct);
    Task<Guid?> CategoryAsync(string slug, bool write, CancellationToken ct);
    Task<Guid?> ArtisanAsync(string slug, bool write, CancellationToken ct);
    Task<IReadOnlyDictionary<string, ImportAttributeReference>> AttributesAsync(Guid type, CancellationToken ct);
    Task<string[]> ExistingSkusAsync(string[] skus, CancellationToken ct);
}

public sealed record PlanProductImportGroup(ImportProductLine[] Lines, ProductActor Actor, IReadOnlySet<string> ReservedSlugs,
    IReadOnlyList<ProductImportError> InitialErrors) : IQuery<ProductImportPlan>;
public sealed record ApplyProductImportGroup(PlanProductImportGroup Input, IReadOnlySet<string> ReservedSkus) : ICommand<ProductImportPlan>;

public sealed record ProductImportPlan(ProductData? Product, ProductImportGroupResult Result)
{
    public ProductImportGroupResult WithCommit(ProductData committed) => Result with
    {
        Status = "committed",
        ProductId = committed.Id,
        Slug = committed.Slug,
        Rows = Result.Rows.Select((row, index) => row with
        {
            VariantId = committed.Variants[index].Id,
            Sku = committed.Variants[index].Sku,
            GenerateOnApply = false
        }).ToArray()
    };
}

public sealed class ProductImportPlanner(IProductStore store, IProductImportReference refs) :
    IQueryHandler<PlanProductImportGroup, ProductImportPlan>
{
    private const NumberStyles DecimalStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
    public static bool ProductColumn(string name) => name is "name" or "product_type_code" or "category_slug"
        or "material_slug" or "artisan_slug" or "description" || name.StartsWith("p.", StringComparison.Ordinal);

    public static string? RejectColumn(string name) =>
        ProductColumn(name) || name is "product_slug" or "variant_name" or "price_vnd" or "sku" or "weight_g"
            or "package_l_cm" or "package_w_cm" or "package_h_cm" || name.StartsWith("v.", StringComparison.Ordinal) ? null : name;

    public async Task<Result<ProductImportPlan>> Handle(PlanProductImportGroup query, CancellationToken cancellationToken) =>
        Result.Success(await PlanAsync(query, false, cancellationToken));

    public async Task<ProductImportPlan> PlanAsync(PlanProductImportGroup input, bool write, CancellationToken ct)
    {
        var lines = input.Lines;
        var errors = input.InitialErrors.ToList();
        var fields = lines.SelectMany(x => x.Cells.Where(cell => cell.Value.Length > 0 && ProductColumn(cell.Key))
            .Select(cell => (x.Row, Column: cell.Key, cell.Value))).ToLookup(x => x.Column, StringComparer.Ordinal);

        void Add(int row, string column, string code, string message) => errors.Add(new(row, column, code, message));
        string Value(string column) => fields[column].FirstOrDefault().Value ?? "";
        void ProductError(string column, string code, string message)
        {
            foreach (var row in fields[column].Select(x => x.Row).DefaultIfEmpty(lines[0].Row)) Add(row, column, code, message);
        }

        foreach (var field in fields)
        {
            if (field.Select(x => x.Value).Distinct(StringComparer.Ordinal).Skip(1).Any())
                ProductError(field.Key, "conflicting_product_field", "Giá trị phải giống nhau trong cùng sản phẩm.");
        }

        // Check every row before sending any user-supplied reference to PostgreSQL.
        var invalidCells = new HashSet<(int Row, string Column)>();
        foreach (var line in lines)
        {
            if (line.Cell("product_type_code").Length == 0)
                Add(line.Row, "product_type_code", "required", "Cần nhập mã loại sản phẩm.");
            foreach (var (column, value) in line.Cells.Where(cell => cell.Value.Length > 0))
            {
                var error = !CatalogRules.IsPostgresText(value)
                    ? ("invalid_text", "Ô chứa ký tự không hỗ trợ.")
                    : column switch
                    {
                        "product_type_code" when !CatalogRules.ReferenceCode().IsMatch(value) =>
                            ("invalid_product_type_code", "Mã loại sản phẩm không đúng định dạng."),
                        "product_slug" or "material_slug" or "category_slug" or "artisan_slug" when !TaxonomyRules.ValidSlug(value) =>
                            ("invalid_slug", "Đường dẫn không đúng định dạng."),
                        "sku" when !ProductRules.ValidSku(value) =>
                            ("invalid_sku", "SKU phải viết hoa, từ 2 đến 40 ký tự hợp lệ."),
                        _ => default
                    };
                if (error.Item1 is not { } code) continue;
                invalidCells.Add((line.Row, column));
                Add(line.Row, column, code, error.Item2!);
            }
        }

        async Task<T> Resolve<T>(string column, Func<string, Task<T>> lookup, string unknownCode, string unknownMessage,
            Func<T, bool>? skuCodeMissing = null, string? missingCode = null, string? missingMessage = null)
        {
            T first = default!;
            var values = fields[column].Where(x => !invalidCells.Contains((x.Row, column))).GroupBy(x => x.Value, StringComparer.Ordinal);
            foreach (var group in values)
            {
                var reference = await lookup(group.Key);
                if (group.Key == Value(column)) first = reference;
                if (reference is null)
                    foreach (var cell in group) Add(cell.Row, column, unknownCode, unknownMessage);
                else if (skuCodeMissing?.Invoke(reference) == true)
                    foreach (var cell in group) Add(cell.Row, column, missingCode!, missingMessage!);
            }
            return first;
        }

        var type = await Resolve("product_type_code", value => refs.TypeAsync(value, ct),
            "unknown_product_type", "Mã loại sản phẩm không tồn tại hoặc đã ngừng dùng.",
            reference => reference is { Prefix: null }, "sku_prefix_missing", "Loại sản phẩm chưa có tiền tố SKU.");
        var material = await Resolve("material_slug", value => refs.MaterialAsync(value, ct),
            "unknown_material", "Không tìm thấy chất liệu.",
            reference => reference is { Code: null }, "material_sku_code_missing", "Chất liệu chưa có mã SKU.");
        var category = await Resolve("category_slug", value => refs.CategoryAsync(value, write, ct), "unknown_category", "Không tìm thấy danh mục.");
        var artisan = await Resolve("artisan_slug", value => refs.ArtisanAsync(value, write, ct), "unknown_artisan", "Không tìm thấy nghệ nhân.");

        var name = Value("name");
        var slug = lines[0].Cell("product_slug");
        var explicitSlug = slug.Length > 0;
        if (!explicitSlug && name.Length >= 2)
        {
            var basis = TaxonomyRules.Slug(name);
            if (TaxonomyRules.ValidSlug(basis))
                for (var attempt = 1; attempt <= 1000; attempt++)
                {
                    var candidate = TaxonomyRules.Candidate(basis, attempt);
                    if (input.ReservedSlugs.Contains(candidate) || await store.SlugExistsAsync(candidate, Guid.Empty, ct)) continue;
                    slug = candidate;
                    break;
                }
        }

        if (explicitSlug && invalidCells.Contains((lines[0].Row, "product_slug"))) slug = "";
        else if (!TaxonomyRules.ValidSlug(slug))
            foreach (var line in lines) Add(line.Row, "product_slug", "invalid_slug", "Điền đường dẫn hợp lệ hoặc tên có thể sinh đường dẫn.");
        else if (explicitSlug && await store.SlugExistsAsync(slug, Guid.Empty, ct))
            foreach (var line in lines) Add(line.Row, "product_slug", "slug_exists", "Đường dẫn sản phẩm đã tồn tại.");

        var attributes = type is null ? new Dictionary<string, ImportAttributeReference>() : await refs.AttributesAsync(type.Id, ct);
        var product = new ProductData
        {
            ProductTypeId = type?.Id ?? Guid.Empty,
            Slug = slug,
            Name = name,
            MaterialId = material?.Id,
            PrimaryCategoryId = category,
            CategoryIds = category is { } id ? [id] : [],
            ArtisanId = artisan,
            DescriptionHtml = Value("description") is { Length: > 0 } description
                ? "<p>" + WebUtility.HtmlEncode(description).Replace("\n", "<br>", StringComparison.Ordinal) + "</p>" : null
        };
        foreach (var field in fields.Where(x => x.Key.StartsWith("p.", StringComparison.Ordinal)))
        {
            foreach (var cell in field)
            {
                var value = ReadAttribute(attributes, field.Key, cell.Value, type?.Id, "product");
                if (value is null) Add(cell.Row, cell.Column, "invalid_attribute", "Thuộc tính hoặc giá trị không hợp lệ cho loại sản phẩm.");
                else if (product.Attributes.All(x => x.AttributeId != value.AttributeId)) product.Attributes.Add(value);
            }
        }

        var skus = input.Actor.IsOwner ? lines.Select(x => x.Cell("sku")).Where(ProductRules.ValidSku).Distinct(StringComparer.Ordinal).ToArray() : [];
        var existingSkus = (await refs.ExistingSkusAsync(skus, ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!input.Actor.Has("catalog.write")) Add(line.Row, "file", "forbidden", "Không có quyền nhập sản phẩm.");
            if (line.Cell("name") is not { Length: >= 2 and <= 200 } rowName || rowName.Any(char.IsControl))
                Add(line.Row, "name", "invalid_name", "Tên sản phẩm cần từ 2 đến 200 ký tự.");
            var variant = new VariantData
            {
                Position = product.Variants.Count,
                Name = line.Cell("variant_name") is { Length: > 0 } label && !invalidCells.Contains((line.Row, "variant_name")) ? label : null
            };
            if (line.Cell("sku") is { Length: > 0 } sku)
            {
                if (!input.Actor.IsOwner) Add(line.Row, "sku", "forbidden", "Chỉ chủ cửa hàng được nhập SKU thủ công.");
                else if (!invalidCells.Contains((line.Row, "sku")))
                {
                    variant.Sku = sku;
                    if (existingSkus.Contains(sku)) Add(line.Row, "sku", "sku_exists", "SKU đã tồn tại.");
                }
            }
            if (line.Cell("price_vnd") is { Length: > 0 } price)
            {
                if (!input.Actor.Has("pricing.write")) Add(line.Row, "price_vnd", "forbidden", "Không có quyền nhập giá bán.");
                else if (long.TryParse(price, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) && ProductRules.ValidPrice(amount))
                    variant.PriceVnd = amount;
                else Add(line.Row, "price_vnd", "invalid_price", "Giá phải là số nguyên VND từ 1 đến 10 tỷ.");
            }
            if (line.Cell("weight_g") is { Length: > 0 } weight)
            {
                if (int.TryParse(weight, NumberStyles.None, CultureInfo.InvariantCulture, out var grams) && grams > 0) variant.WeightG = grams;
                else Add(line.Row, "weight_g", "invalid_weight", "Khối lượng phải là số nguyên dương theo gram.");
            }

            var dims = new Dictionary<string, decimal>();
            foreach (var axis in new[] { "l", "w", "h" })
            {
                var column = $"package_{axis}_cm";
                if (line.Cell(column) is not { Length: > 0 } dimension) continue;
                if (TryReadExactDecimal(dimension, 29, 28, out var cm)) dims[axis] = cm;
                else Add(line.Row, column, "invalid_dimension", "Kích thước phải là số theo cm.");
            }
            if (dims.Count > 0) variant.PackageDimsCm = JsonSerializer.Serialize(dims);
            foreach (var cell in line.Cells.Where(x => x.Key.StartsWith("v.", StringComparison.Ordinal) && x.Value.Length > 0))
            {
                var value = ReadAttribute(attributes, cell.Key, cell.Value, type?.Id, "variant");
                if (value is null) Add(line.Row, cell.Key, "invalid_attribute", "Thuộc tính hoặc giá trị không hợp lệ cho biến thể.");
                else variant.Attributes.Add(value);
            }
            product.Variants.Add(variant);
        }

        var valid = errors.Count == 0;
        var rows = product.Variants.Select((variant, index) =>
        {
            var line = lines[index];
            var rowErrors = errors.Where(e => e.Row == line.Row).ToArray();
            if (!valid && rowErrors.Length == 0) rowErrors = [new(line.Row, "product_slug", "group_rejected", "Một dòng khác của sản phẩm có lỗi.")];
            return new ProductImportRow(line.Row, variant.Name, variant.PriceVnd, variant.Sku.Length == 0 ? null : variant.Sku,
                line.Cell("sku").Length == 0, null, variant.WeightG, variant.PackageDimsCm is null ? null : JsonSerializer.Deserialize<JsonElement>(variant.PackageDimsCm),
                variant.Attributes, rowErrors);
        }).ToArray();
        string[] warnings = !explicitSlug && lines.All(line => line.Cell("sku").Length == 0) ? ["reimport_can_create_duplicate"] : [];
        return new(valid ? product : null, new(valid ? "createDraft" : "skipGroup", valid ? "ready" : "failed",
            slug.Length > 0 ? slug : null, null, CatalogRules.IsPostgresText(name) ? name : "", warnings, rows));
    }

    private static ProductValue? ReadAttribute(IReadOnlyDictionary<string, ImportAttributeReference> attributes, string column,
        string value, Guid? type, string scope)
    {
        if (type is not { } id || !attributes.TryGetValue(column[2..], out var attribute)) return null;
        var definition = attribute.Definition;
        var option = attribute.Options.GetValueOrDefault(value);
        ProductValue? parsed = definition.DataType switch
        {
            "text" => new(definition.Id, ValueText: value),
            "number" when TryReadExactDecimal(value, 10, 2, out var number) => new(definition.Id, ValueNumber: number),
            "boolean" when bool.TryParse(value, out var flag) => new(definition.Id, ValueBool: flag),
            "option" when option is not null => new(definition.Id, OptionId: option.Id),
            "dimensions" when ReadJson(value) is { } json => new(definition.Id, ValueJson: json),
            _ => null
        };
        return parsed is not null && TypedAttributeValidator.Validate(definition, id, scope, parsed.Typed(), option) is null ? parsed : null;
    }

    private static bool TryReadExactDecimal(string value, int maxWholeDigits, int maxScale, out decimal number)
    {
        number = 0;
        if (value.Length == 0) return false;
        var unsigned = value.AsSpan();
        var negative = unsigned[0] == '-';
        if (unsigned[0] is '+' or '-') unsigned = unsigned[1..];
        var point = unsigned.IndexOf('.');
        var whole = point < 0 ? unsigned : unsigned[..point];
        var fraction = point < 0 ? ReadOnlySpan<char>.Empty : unsigned[(point + 1)..];
        if (whole.Length + fraction.Length == 0 ||
            whole.ContainsAnyExceptInRange('0', '9') || fraction.ContainsAnyExceptInRange('0', '9')) return false;

        // Normalize equivalent spellings before parsing. The import's scalar columns use
        // plain decimal syntax, so trimming zeros never needs to expand any exponent.
        whole = whole.TrimStart('0');
        fraction = fraction.TrimEnd('0');
        if (whole.Length > maxWholeDigits || fraction.Length > maxScale) return false;

        // Decimal stores an unsigned 96-bit coefficient and a scale of at most 28.
        // Check the integer coefficient before parsing, which could otherwise reduce
        // the scale and round even when each part fits its individual digit limit.
        const string maxCoefficient = "79228162514264337593543950335";
        var digits = whole.Length + fraction.Length;
        if (digits > maxCoefficient.Length || (digits == maxCoefficient.Length &&
            string.Concat(whole, fraction).AsSpan().SequenceCompareTo(maxCoefficient) > 0)) return false;

        var sign = negative && digits > 0 ? "-" : "";
        var normalized = string.Concat(sign, whole.IsEmpty ? "0" : whole, fraction.IsEmpty ? "" : ".", fraction);
        return decimal.TryParse(normalized, DecimalStyle, CultureInfo.InvariantCulture, out number);
    }

    private static JsonElement? ReadJson(string value)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(value); }
        catch (JsonException) { return null; }
    }
}

public sealed class ApplyProductImportHandler(ProductImportPlanner planner, ProductWorkflow workflow, IProductStore store)
    : ICommandHandler<ApplyProductImportGroup, ProductImportPlan>
{
    public async Task<Result<ProductImportPlan>> Handle(ApplyProductImportGroup command, CancellationToken cancellationToken)
    {
        await store.LockAsync(true, cancellationToken);
        var plan = await planner.PlanAsync(command.Input, true, cancellationToken);
        if (plan.Product is not { } product) return Result.Success(plan);
        try
        {
            var committed = await workflow.CreateImportedAsync(product, command.Input.Actor, command.ReservedSkus, cancellationToken);
            return Result.Success(plan with { Result = plan.WithCommit(committed) });
        }
        catch (CatalogPersistenceException failure) { return Result.Failure<ProductImportPlan>(failure.Error); }
    }
}
