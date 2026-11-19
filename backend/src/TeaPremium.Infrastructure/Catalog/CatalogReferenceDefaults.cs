using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

// P1-03 / 03 §6. Owner approved stable UUID v7 seed identities in this session.
// IDs are an immutable manifest: never reorder/reassign offsets to a different reference.
public static class CatalogReferenceDefaults
{
    public static Guid Id(int ordinal) => Guid.Parse($"019ead30-0000-7000-8000-{ordinal:000000000000}");
    public static ProductType[] Types() => new[]
    {
        ("teapot", "AM", "Ấm"), ("cup", "CH", "Chén/Ly"), ("pitcher", "TO", "Tống"),
        ("tea_tray", "KH", "Khay trà"), ("tea_caddy", "HU", "Hũ trà"), ("vase", "BB", "Bình bông"),
        ("incense", "XT", "Xông trầm"), ("wine_set", "RU", "Bộ ly/bình rượu"),
        ("accessory", "PK", "Phụ kiện: ngắt nước, kẹp, lọc...")
    }.Select((x, i) => new ProductType { Id = Id(i + 1), Code = x.Item1, SkuPrefix = x.Item2, Name = x.Item3 }).ToArray();

    public static AttributeDefinition[] Attributes() =>
    [
        Attr(101, 1, "capacity_ml", "Dung tích", "number", "ml", "variant", axis: true, filter: true, required: true),
        Attr(102, 1, "filter_type", "Kiểu lọc", "option", filter: true),
        Attr(103, 1, "technique", "Kỹ thuật", "option", filter: true),
        Dims(104, 1), Attr(105, 1, "artisan_rank", "Cấp bậc nghệ nhân", "text"), Attr(106, 1, "certificate", "Chứng thư", "text"),
        Attr(107, 2, "capacity_ml", "Dung tích", "number", "ml", "variant", axis: true, filter: true),
        Attr(108, 2, "set_size", "Số lượng trong bộ", "number", "cái/bộ"), Dims(109, 2),
        Attr(110, 3, "capacity_ml", "Dung tích", "number", "ml"), Dims(111, 3),
        Attr(112, 4, "tray_style", "Kiểu khay", "option"), Dims(113, 4), Attr(114, 4, "wood_type", "Loại gỗ", "text"),
        Attr(115, 5, "capacity_g", "Sức chứa", "number", "g"), Dims(116, 5), Dims(117, 6),
        Attr(118, 7, "incense_style", "Kiểu xông trầm", "option"), Dims(119, 7),
        Attr(120, 8, "set_size", "Số lượng trong bộ", "number", "cái/bộ"),
        Attr(121, 8, "capacity_ml", "Dung tích", "number", "ml"), Dims(122, 8), Dims(123, 9)
    ];
    private static AttributeDefinition Dims(int id, int type) => Attr(id, type, "dimensions", "Kích thước", "dimensions", "cm");
    private static AttributeDefinition Attr(int id, int type, string code, string name, string dataType,
        string? unit = null, string scope = "product", bool axis = false, bool filter = false, bool required = false)
        => new()
        {
            Id = Id(id),
            ProductTypeId = Id(type),
            Code = code,
            Name = name,
            DataType = dataType,
            Unit = unit,
            Scope = scope,
            IsVariantAxis = axis,
            IsFilterable = filter,
            IsRequiredForPublish = required
        };

    public static AttributeOption[] Options() =>
    [
        Opt(201, 102, "luoi_loc", "Lưới lọc"), Opt(202, 102, "1_lo", "1 lỗ"), Opt(203, 102, "7_lo", "7 lỗ"),
        Opt(204, 102, "9_lo", "9 lỗ"), Opt(205, 102, "khac", "Khác"),
        Opt(206, 103, "thu_cong_toan_phan", "Thủ công toàn phần"), Opt(207, 103, "ban_thu_cong", "Bán thủ công"), Opt(208, 103, "khuon", "Khuôn"),
        Opt(209, 112, "khay_nuoc", "Khay nước"), Opt(210, 112, "khay_kho", "Khay khô"), Opt(211, 112, "khay_hut_nuoc", "Khay hút nước"),
        Opt(212, 118, "hop", "Hộp"), Opt(213, 118, "gom", "Gốm"), Opt(214, 118, "khac", "Khác")
    ];
    private static AttributeOption Opt(int id, int attr, string code, string label)
        => new() { Id = Id(id), AttributeId = Id(attr), Code = code, Label = label };

    public static Material[] Materials() => new[]
    {
        ("TN", "tu-ne", "tử nê", "clay"), ("LN", "luc-ne", "lục nê", "clay"), ("DN", "doan-ne", "đoạn nê", "clay"),
        ("CN", "chu-ne", "chu nê", "clay"), ("HN", "hong-ne", "hồng nê", "clay"), ("HE", "hac-ne", "hắc nê", "clay"),
        ("GA", "gang", "gang", "metal"), ("TT", "thuy-tinh", "thủy tinh", "glass"), ("GM", "gom", "gốm", "ceramic"),
        ("SU", "su", "sứ", "porcelain"), ("DO", "dong-hop-kim", "đồng hợp kim", "metal"),
        ("GO", "go", "gỗ", "wood"), ("DA", "da", "đá", "stone")
    }.Select((x, i) => new Material { Id = Id(i + 301), SkuCode = x.Item1, Slug = x.Item2, Name = x.Item3, Kind = x.Item4, Status = "draft" }).ToArray();
}
