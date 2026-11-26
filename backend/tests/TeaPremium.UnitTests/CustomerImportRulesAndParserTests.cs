using System.Globalization;
using System.IO.Compression;
using System.Text;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Import;
using TeaPremium.Domain.Crm;

namespace TeaPremium.UnitTests;

public sealed class CustomerImportRulesAndParserTests
{
    [Theory]
    [InlineData(null, true, true)]
    [InlineData("", true, true)]
    [InlineData("   ", true, true)]
    [InlineData("true", true, true)]
    [InlineData("TRUE", true, true)]
    [InlineData(" 1 ", true, true)]
    [InlineData("false", true, false)]
    [InlineData("FALSE", true, false)]
    [InlineData("0", true, false)]
    [InlineData("yes", false, false)]
    [InlineData("2", false, false)]
    [InlineData("abc", false, false)]
    [InlineData("true\0", false, false)]
    public void BrCrm03HadPurchaseParserDefaultsToTrueWhenBlankAndParsesBooleanTokensCaseInsensitively(
        string? raw,
        bool expectedValid,
        bool expectedValue)
    {
        var ok = CustomerRules.TryParseImportHadPurchase(raw, out var value);
        Assert.Equal(expectedValid, ok);
        if (expectedValid)
        {
            Assert.Equal(expectedValue, value);
        }
    }

    [Fact]
    public void BrCrm03TagsParserSplitsCommaAndSemicolonTrimsDeduplicatesCaseInsensitivelyAndEnforcesBounds()
    {
        Assert.True(CustomerRules.TryParseImportTags("  VIP , Ấm Tử Sa ; vip ; ; QUÀ TẶNG  ", out var tags));
        Assert.Equal(["VIP", "Ấm Tử Sa", "QUÀ TẶNG"], tags);

        Assert.True(CustomerRules.TryParseImportTags("   ", out var empty));
        Assert.Empty(empty);

        var maxLenTag = new string('A', CustomerRules.MaxTagNameLength);
        Assert.True(CustomerRules.TryParseImportTags(maxLenTag, out var atBound));
        Assert.Equal([maxLenTag], atBound);

        var tooLongTag = new string('A', CustomerRules.MaxTagNameLength + 1);
        Assert.False(CustomerRules.TryParseImportTags(tooLongTag, out _));
        Assert.False(CustomerRules.TryParseImportTags("VIP\0Bad", out _));

        var fiftyTags = string.Join(",", Enumerable.Range(1, 50).Select(i => $"Tag{i}"));
        Assert.True(CustomerRules.TryParseImportTags(fiftyTags, out var fiftyParsed));
        Assert.Equal(50, fiftyParsed.Length);

        var fiftyOneTags = string.Join(",", Enumerable.Range(1, 51).Select(i => $"Tag{i}"));
        Assert.False(CustomerRules.TryParseImportTags(fiftyOneTags, out _));
    }

    [Fact]
    public void BrCrm03PlannerNormalizesPhonesAppliesDefaultsAndFlagsZaloOnlyWarning()
    {
        var lines = new CustomerImportLine[]
        {
            new(2, "Nguyễn Văn A", "0901.234-567", "", "", "VIP; Ấm Tử Sa", "Khách cũ", ""),
            new(3, "Trần Thị B", "", "Zalo B", "wholesale", "", "", "false")
        };

        var validated = CustomerImportPlanner.ValidateLines(lines);
        var planned = CustomerImportPlanner.Plan(validated, new Dictionary<string, Guid>(), dryRun: true);

        Assert.Equal("would_import", planned[0].Status);
        Assert.Equal("would_import", planned[0].ReasonCode);
        Assert.Equal("+84901234567", planned[0].Validated!.NormalizedPhone);
        Assert.Equal("retail", planned[0].Validated!.CustomerGroup);
        Assert.True(planned[0].Validated!.HadPurchase);
        Assert.Empty(planned[0].Warnings);

        Assert.Equal("would_import", planned[1].Status);
        Assert.Null(planned[1].Validated!.NormalizedPhone);
        Assert.Equal("Zalo B", planned[1].Validated!.ZaloName);
        Assert.Equal("wholesale", planned[1].Validated!.CustomerGroup);
        Assert.False(planned[1].Validated!.HadPurchase);
        Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk], planned[1].Warnings);
    }

    [Fact]
    public void O4PlannerAddsPossibleDuplicateZaloCustomerWarningForInFileAndDbMatchesWithoutAutoSkippingOrShadowingInvalidRows()
    {
        var existingZaloKeys = new HashSet<ZaloIdentityKey>
        {
            ZaloIdentityKey.Create("Nguyễn Văn C", "Zalo C")
        };

        var lines = new CustomerImportLine[]
        {
            // Invalid row (bad phone) must NOT pollute seenZaloKeysInFile for row 3
            new(2, "Trần Thị B", "02438223344", "Zalo B", "", "", "", ""),
            // First valid Zalo-only occurrence in file (not in DB) -> only zalo_only_reimport_risk
            new(3, "Trần Thị B", "", "Zalo B", "", "", "", ""),
            // Case-insensitive + whitespace duplicate of row 3 in file -> both warnings, STILL ready (never skipped)
            new(4, "  TRẦN THỊ B ", "", " zalo b ", "", "", "", ""),
            // Matches existing DB Zalo-only customer case-insensitively -> both warnings, STILL ready
            new(5, "nguyễn văn c", "", "ZALO C", "", "", "", ""),
            // Different full_name with same zalo_name -> NOT a duplicate pair
            new(6, "Phạm Thị D", "", "Zalo B", "", "", "", ""),
            // Phone + Zalo row -> NOT Zalo-only -> zero Zalo warnings and must not shadow row 8
            new(7, "Lê Văn E", "0901234567", "Zalo E", "", "", "", ""),
            new(8, "Lê Văn E", "", "Zalo E", "", "", "", "")
        };

        var validated = CustomerImportPlanner.ValidateLines(lines);
        var plannedDry = CustomerImportPlanner.Plan(validated, new Dictionary<string, Guid>(), dryRun: true, existingZaloKeys);
        var plannedApply = CustomerImportPlanner.Plan(validated, new Dictionary<string, Guid>(), dryRun: false, existingZaloKeys);

        Assert.Equal("error", plannedDry[0].Status);
        Assert.Empty(plannedDry[0].Warnings);

        Assert.Equal("would_import", plannedDry[1].Status);
        Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk], plannedDry[1].Warnings);

        Assert.Equal("would_import", plannedDry[2].Status);
        Assert.Equal(
            [CustomerImportSchema.WarningZaloOnlyReimportRisk, CustomerImportSchema.WarningPossibleDuplicateZaloCustomer],
            plannedDry[2].Warnings);
        Assert.Equal("imported", plannedApply[2].Status);
        Assert.Equal(
            [CustomerImportSchema.WarningZaloOnlyReimportRisk, CustomerImportSchema.WarningPossibleDuplicateZaloCustomer],
            plannedApply[2].Warnings);

        Assert.Equal("would_import", plannedDry[3].Status);
        Assert.Equal(
            [CustomerImportSchema.WarningZaloOnlyReimportRisk, CustomerImportSchema.WarningPossibleDuplicateZaloCustomer],
            plannedDry[3].Warnings);
        Assert.Equal("imported", plannedApply[3].Status);
        Assert.Equal(
            [CustomerImportSchema.WarningZaloOnlyReimportRisk, CustomerImportSchema.WarningPossibleDuplicateZaloCustomer],
            plannedApply[3].Warnings);

        Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk], plannedDry[4].Warnings);
        Assert.Empty(plannedDry[5].Warnings);
        Assert.Equal([CustomerImportSchema.WarningZaloOnlyReimportRisk], plannedDry[6].Warnings);
    }

    [Fact]
    public void O4BuildSummaryComputesCommittedInvariantAndCarriesNewTagsDeterministically()
    {
        var readyRow = new CustomerImportRow(2, "would_import", "would_import", null, null, "+84901234567", null, null, false, ["VIP"], [], []);
        var importedRow = new CustomerImportRow(2, "imported", "imported", Guid.CreateVersion7(), "KH001000", "+84901234567", null, null, false, ["VIP"], [], []);
        var skippedRow = new CustomerImportRow(3, "skipped", "existing_phone_skipped", null, null, "+84901234568", Guid.CreateVersion7(), null, false, [], [], []);
        var errorRow = new CustomerImportRow(4, "error", "missing_contact", null, null, null, null, null, false, [], [], [new("phone", "missing_contact", "err")]);

        var drySummary = CustomerImportPlanner.BuildSummary(dryRun: true, [readyRow], 0, 0, ["VIP"]);
        Assert.True(drySummary.DryRun);
        Assert.False(drySummary.Committed);
        Assert.Equal(["VIP"], drySummary.NewTags);

        var blockedApplySummary = CustomerImportPlanner.BuildSummary(dryRun: false, [readyRow, errorRow], 0, 0, ["VIP"]);
        Assert.False(blockedApplySummary.DryRun);
        Assert.False(blockedApplySummary.Committed);
        Assert.Equal(1, blockedApplySummary.ErrorCount);
        Assert.Equal(0, blockedApplySummary.ImportedCount);
        Assert.Equal(1, blockedApplySummary.ReadyCount);
        Assert.Equal(["VIP"], blockedApplySummary.NewTags);

        var committedSummary = CustomerImportPlanner.BuildSummary(dryRun: false, [importedRow, skippedRow], 1, 0, ["VIP"]);
        Assert.False(committedSummary.DryRun);
        Assert.True(committedSummary.Committed);
        Assert.Equal(1, committedSummary.ImportedCount);
        Assert.Equal(1, committedSummary.SkippedCount);
        Assert.Equal(["VIP"], committedSummary.NewTags);

        var allSkippedCommittedSummary = CustomerImportPlanner.BuildSummary(dryRun: false, [skippedRow], 0, 0, []);
        Assert.False(allSkippedCommittedSummary.DryRun);
        Assert.True(allSkippedCommittedSummary.Committed);
        Assert.Equal(0, allSkippedCommittedSummary.ImportedCount);
        Assert.Equal(1, allSkippedCommittedSummary.SkippedCount);
        Assert.Empty(allSkippedCommittedSummary.NewTags);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void BrCrm03PlannerMissingBothPhoneAndZaloReportsMissingContact(string phone, string zalo)
    {
        var validated = CustomerImportPlanner.ValidateLines([new(2, "Khách Thiếu Liên Hệ", phone, zalo, "", "", "", "")]);
        var planned = CustomerImportPlanner.Plan(validated, new Dictionary<string, Guid>(), dryRun: true);

        Assert.Equal("error", planned[0].Status);
        Assert.Equal("missing_contact", planned[0].ReasonCode);
        Assert.Contains(planned[0].Errors, e => e.Column == "phone" && e.Code == "missing_contact");
    }

    [Theory]
    [InlineData("02438223344")]
    [InlineData("901234567")]
    [InlineData("+84000000000")]
    [InlineData("0123456789")]
    [InlineData("090123456")]
    [InlineData("09012345678")]
    [InlineData("+840901234567")]
    [InlineData("0901234567\0")]
    public void BrCrm01BrCrm03PlannerInvalidPhoneFormatsIncludingLandlineAndMissingLeadingZeroReportInvalidPhoneEvenWhenZaloPresent(
        string badPhone)
    {
        var validated = CustomerImportPlanner.ValidateLines([new(2, "Khách Sai SĐT", badPhone, "Zalo Hợp Lệ", "", "", "", "")]);
        var planned = CustomerImportPlanner.Plan(validated, new Dictionary<string, Guid>(), dryRun: true);

        Assert.Equal("error", planned[0].Status);
        Assert.Equal("invalid_phone", planned[0].ReasonCode);
        Assert.Contains(planned[0].Errors, e => e.Column == "phone" && e.Code == "invalid_phone");
    }

    [Fact]
    public void BrCrm03PlannerDuplicatePhonesInFileKeepFirstValidRowAndMarkLaterRowsWithDuplicateOfRow()
    {
        var lines = new CustomerImportLine[]
        {
            new(2, "", "0901 234 567", "", "", "", "", ""), // invalid row must NOT shadow valid row 3
            new(3, "Người Giữ Lại", "0901 234 567", "", "", "", "", ""),
            new(5, "Người Trùng 1", "+84901234567", "", "", "", "", ""),
            new(9, "Người Trùng 2", "84901-234-567", "", "", "", "", "")
        };

        var validated = CustomerImportPlanner.ValidateLines(lines);
        var planned = CustomerImportPlanner.Plan(validated, new Dictionary<string, Guid>(), dryRun: false);

        Assert.Equal("error", planned[0].Status);
        Assert.Equal("missing_full_name", planned[0].ReasonCode);

        Assert.Equal("imported", planned[1].Status);
        Assert.Equal("imported", planned[1].ReasonCode);

        Assert.Equal("skipped", planned[2].Status);
        Assert.Equal("duplicate_in_file", planned[2].ReasonCode);
        Assert.Equal(3, planned[2].DuplicateOfRow);

        Assert.Equal("skipped", planned[3].Status);
        Assert.Equal("duplicate_in_file", planned[3].ReasonCode);
        Assert.Equal(3, planned[3].DuplicateOfRow);
    }

    [Fact]
    public void BrCrm03PlannerExistingActiveDbPhoneMarksFirstValidRowExistingPhoneSkippedAndLaterDuplicateRowDuplicateInFile()
    {
        var existingId = Guid.CreateVersion7();
        var existingPhones = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["+84901234567"] = existingId
        };
        var lines = new CustomerImportLine[]
        {
            new(2, "Khách Đã Có", "0901234567", "", "", "", "", ""),
            new(4, "Khách Trùng Lần 2", "+84901234567", "", "", "", "", "")
        };

        var validated = CustomerImportPlanner.ValidateLines(lines);
        var planned = CustomerImportPlanner.Plan(validated, existingPhones, dryRun: false);

        Assert.Equal("skipped", planned[0].Status);
        Assert.Equal("existing_phone_skipped", planned[0].ReasonCode);
        Assert.Equal(existingId, planned[0].ExistingCustomerId);

        Assert.Equal("skipped", planned[1].Status);
        Assert.Equal("duplicate_in_file", planned[1].ReasonCode);
        Assert.Equal(2, planned[1].DuplicateOfRow);
    }

    [Theory]
    [InlineData("", "0901234567", "", "", "", "", "", "missing_full_name", "full_name")]
    [InlineData("LONG_NAME", "0901234567", "", "", "", "", "", "invalid_full_name", "full_name")]
    [InlineData("Tên\0Nul", "0901234567", "", "", "", "", "", "invalid_full_name", "full_name")]
    [InlineData("Hợp Lệ", "", "LONG_ZALO", "", "", "", "", "invalid_zalo_name", "zalo_name")]
    [InlineData("Hợp Lệ", "0901234567", "", "vip", "", "", "", "invalid_customer_group", "customer_group")]
    [InlineData("Hợp Lệ", "0901234567", "", "", "LONG_TAG", "", "", "invalid_tag", "tags")]
    [InlineData("Hợp Lệ", "0901234567", "", "", "", "LONG_NOTE", "", "invalid_note", "note")]
    [InlineData("Hợp Lệ", "0901234567", "", "", "", "", "maybe", "invalid_had_purchase", "had_purchase")]
    public void BrCrm03PlannerValidatesFullNameZaloCustomerGroupAndNoteLengthBounds(
        string fullName,
        string phone,
        string zalo,
        string group,
        string tags,
        string note,
        string hadPurchase,
        string expectedCode,
        string expectedColumn)
    {
        if (fullName == "LONG_NAME") fullName = new string('A', 151);
        if (zalo == "LONG_ZALO") zalo = new string('Z', 151);
        if (tags == "LONG_TAG") tags = new string('T', 61);
        if (note == "LONG_NOTE") note = new string('N', 4001);

        var validated = CustomerImportPlanner.ValidateLines([new(2, fullName, phone, zalo, group, tags, note, hadPurchase)]);
        var planned = CustomerImportPlanner.Plan(validated, new Dictionary<string, Guid>(), dryRun: true);

        Assert.Equal("error", planned[0].Status);
        Assert.Equal(expectedCode, planned[0].ReasonCode);
        Assert.Contains(planned[0].Errors, e => e.Column == expectedColumn && e.Code == expectedCode);
    }

    [Fact]
    public void TabularImportFileReadsValidCsvWithBomPlus84PhonesMultilineNoteAndSingleOrNamedXlsxUpTo2000Rows()
    {
        var csv = new StringBuilder();
        csv.Append("full_name,phone,zalo_name,customer_group,tags,note,had_purchase\n");
        for (var i = 1; i <= 2000; i++)
        {
            var note = i == 1 ? "\"Dòng 1\nDòng 2 ghi chú\"" : "";
            csv.Append(CultureInfo.InvariantCulture, $"Khách {i},+8490{i:D7},,retail,VIP,{note},\n");
        }

        var bomBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        using (var stream = new MemoryStream(bomBytes))
        {
            var rows = TabularImportFile.Read(stream, "legacy.csv", CustomerImportSchema.Table);
            Assert.Equal(2000, rows.Length);
            Assert.Equal("+84900000001", rows[0].Cell("phone"));
            Assert.Equal("Dòng 1\nDòng 2 ghi chú", rows[0].Cell("note"));
        }

        // Single-sheet XLSX named Sheet1
        var singleSheet = BuildXlsx(
            [("Sheet1", "rId1")],
            [("rId1", "worksheets/sheet1.xml",
                [
                    ["full_name", "phone", "had_purchase"],
                    ["Khách Excel 1", "0901234567", "BOOL:1"]
                ])]);
        using (var stream = new MemoryStream(singleSheet))
        {
            var rows = TabularImportFile.Read(stream, "one-sheet.xlsx", CustomerImportSchema.Table);
            Assert.Single(rows);
            Assert.Equal("Khách Excel 1", rows[0].Cell("full_name"));
            Assert.Equal("true", rows[0].Cell("had_purchase"));
        }

        // Multi-sheet XLSX containing unique KhachHang sheet
        var multiSheet = BuildXlsx(
            [("HuongDan", "rId1"), ("KhachHang", "rId2")],
            [
                ("rId1", "worksheets/sheet1.xml", [["ignored"]]),
                ("rId2", "worksheets/sheet2.xml", [["full_name", "zalo_name", "had_purchase"], ["Khách Multi", "Zalo M", "BOOL:0"]])
            ]);
        using (var stream = new MemoryStream(multiSheet))
        {
            var rows = TabularImportFile.Read(stream, "multi-sheet.xlsx", CustomerImportSchema.Table);
            Assert.Single(rows);
            Assert.Equal("Khách Multi", rows[0].Cell("full_name"));
            Assert.Equal("false", rows[0].Cell("had_purchase"));
        }
    }

    [Fact]
    public void TabularImportFileRejects2001RowsUnknownHeadersFormulasMacrosZipBombDishonestEntryAndUtf16()
    {
        // 2,001 rows -> 413 file_limit_exceeded
        var overLimit = new StringBuilder("full_name,phone\n");
        for (var i = 1; i <= 2001; i++)
            overLimit.Append(CultureInfo.InvariantCulture, $"K{i},090{i:D7}\n");
        var ex2001 = Assert.Throws<TabularImportFileException>(() =>
            TabularImportFile.Read(new MemoryStream(Encoding.UTF8.GetBytes(overLimit.ToString())), "over.csv", CustomerImportSchema.Table));
        Assert.Equal(413, ex2001.Status);
        Assert.Equal("file_limit_exceeded", ex2001.Code);

        // Unknown column -> 400 invalid_header
        var exUnknown = Assert.Throws<TabularImportFileException>(() =>
            TabularImportFile.Read(new MemoryStream(Encoding.UTF8.GetBytes("full_name,secret_col\nA,B\n")), "bad.csv", CustomerImportSchema.Table));
        Assert.Equal(400, exUnknown.Status);
        Assert.Equal("invalid_header", exUnknown.Code);

        // Missing required full_name -> 400 missing_header
        var exMissing = Assert.Throws<TabularImportFileException>(() =>
            TabularImportFile.Read(new MemoryStream(Encoding.UTF8.GetBytes("phone,zalo_name\n0901234567,Z\n")), "missing.csv", CustomerImportSchema.Table));
        Assert.Equal(400, exMissing.Status);
        Assert.Equal("missing_header", exMissing.Code);

        // UTF-16 CSV -> 400 invalid_utf8
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("full_name,phone\nA,0901234567\n")).ToArray();
        var exUtf16 = Assert.Throws<TabularImportFileException>(() =>
            TabularImportFile.Read(new MemoryStream(utf16), "utf16.csv", CustomerImportSchema.Table));
        Assert.Equal(400, exUtf16.Status);
        Assert.Equal("invalid_utf8", exUtf16.Code);

        // Multi-sheet XLSX without KhachHang -> 400 missing_sheet
        var noKhachHang = BuildXlsx(
            [("Sheet1", "rId1"), ("Sheet2", "rId2")],
            [
                ("rId1", "worksheets/sheet1.xml", [["full_name"], ["A"]]),
                ("rId2", "worksheets/sheet2.xml", [["full_name"], ["B"]])
            ]);
        var exSheet = Assert.Throws<TabularImportFileException>(() =>
            TabularImportFile.Read(new MemoryStream(noKhachHang), "nosheet.xlsx", CustomerImportSchema.Table));
        Assert.Equal(400, exSheet.Status);
        Assert.Equal("missing_sheet", exSheet.Code);

        // XLSX formula <f> -> 400 formula_not_allowed
        var formulaXlsx = BuildXlsx(
            [("KhachHang", "rId1")],
            [("rId1", "worksheets/sheet1.xml", [["full_name", "phone"], ["FORMULA:=1+1", "0901234567"]])]);
        var exFormula = Assert.Throws<TabularImportFileException>(() =>
            TabularImportFile.Read(new MemoryStream(formulaXlsx), "formula.xlsx", CustomerImportSchema.Table));
        Assert.Equal(400, exFormula.Status);
        Assert.Equal("formula_not_allowed", exFormula.Code);

        // XLSX macro vbaProject -> 415 unsupported_format
        var macroXlsx = BuildXlsx(
            [("KhachHang", "rId1")],
            [("rId1", "worksheets/sheet1.xml", [["full_name"], ["A"]])],
            extraEntries: [("xl/vbaProject.bin", [1, 2, 3])]);
        var exMacro = Assert.Throws<TabularImportFileException>(() =>
            TabularImportFile.Read(new MemoryStream(macroXlsx), "macro.xlsx", CustomerImportSchema.Table));
        Assert.Equal(415, exMacro.Status);
        Assert.Equal("unsupported_format", exMacro.Code);

        // XLSX zip-bomb exceeding 32 MiB expanded budget -> 413 file_limit_exceeded
        var bombPayload = new byte[TabularImportFile.MaxExpanded + 1024];
        Array.Fill(bombPayload, (byte)'A');
        var bombXlsx = BuildXlsx(
            [("KhachHang", "rId1")],
            [("rId1", "worksheets/sheet1.xml", [["full_name"], ["A"]])],
            extraEntries: [("xl/padding.bin", bombPayload)]);
        var exBomb = Assert.Throws<TabularImportFileException>(() =>
            TabularImportFile.Read(new MemoryStream(bombXlsx), "bomb.xlsx", CustomerImportSchema.Table));
        Assert.Equal(413, exBomb.Status);
        Assert.Equal("file_limit_exceeded", exBomb.Code);
    }

    [Fact]
    public void TabularImportFilePreservesP1ProductAndOpeningImport500RowAndSheetLimits()
    {
        var p1OpeningSchema = new ImportTableSchema(
            h => h is "sku" or "quantity" or "unit_cost_vnd",
            ["sku", "quantity", "unit_cost_vnd"],
            500,
            ImportSheetMode.SingleOnly,
            null,
            "Tệp có hơn 500 dòng dữ liệu.");

        var over500 = new StringBuilder("sku,quantity,unit_cost_vnd\n");
        for (var i = 1; i <= 501; i++)
            over500.Append(CultureInfo.InvariantCulture, $"SKU-{i:D4},1,100000\n");

        var ex = Assert.Throws<TabularImportFileException>(() =>
            TabularImportFile.Read(new MemoryStream(Encoding.UTF8.GetBytes(over500.ToString())), "opening.csv", p1OpeningSchema));
        Assert.Equal(413, ex.Status);
        Assert.Equal("file_limit_exceeded", ex.Code);

        var p1ProductSchema = new ImportTableSchema(
            h => h is "name" or "product_type_code",
            ["name", "product_type_code"],
            500,
            ImportSheetMode.ExactName,
            "SanPham",
            "Tệp có hơn 500 dòng dữ liệu.");

        var wrongSheet = BuildXlsx(
            [("Sheet1", "rId1")],
            [("rId1", "worksheets/sheet1.xml", [["name", "product_type_code"], ["Ấm", "am"]])]);
        var exSheet = Assert.Throws<TabularImportFileException>(() =>
            TabularImportFile.Read(new MemoryStream(wrongSheet), "products.xlsx", p1ProductSchema));
        Assert.Equal(400, exSheet.Status);
        Assert.Equal("missing_sheet", exSheet.Code);
    }

    [Fact]
    public void TabularImportFileRejectsDuplicateNamedSheetsEvenWhenFirstSheetOmitsRelationshipId()
    {
        var p1ProductSchema = new ImportTableSchema(
            h => h is "name" or "product_type_code",
            ["name", "product_type_code"],
            500,
            ImportSheetMode.ExactName,
            "SanPham",
            "Tệp có hơn 500 dòng dữ liệu.");

        // First "SanPham" omits r:id, second "SanPham" has valid r:id -> must still reject as duplicate (400 missing_sheet).
        var duplicateSanPham = BuildXlsx(
            [("SanPham", null), ("SanPham", "rId1")],
            [("rId1", "worksheets/sheet1.xml", [["name", "product_type_code"], ["Ấm Tử Sa", "am"]])]);
        var exP1 = Assert.Throws<TabularImportFileException>(() =>
            TabularImportFile.Read(new MemoryStream(duplicateSanPham), "dup-sanpham.xlsx", p1ProductSchema));
        Assert.Equal(400, exP1.Status);
        Assert.Equal("missing_sheet", exP1.Code);

        // Same case for P2-03 SingleOrNamed("KhachHang")
        var duplicateKhachHang = BuildXlsx(
            [("KhachHang", null), ("KhachHang", "rId1")],
            [("rId1", "worksheets/sheet1.xml", [["full_name", "phone"], ["Nguyễn Văn A", "0901234567"]])]);
        var exP203 = Assert.Throws<TabularImportFileException>(() =>
            TabularImportFile.Read(new MemoryStream(duplicateKhachHang), "dup-khachhang.xlsx", CustomerImportSchema.Table));
        Assert.Equal(400, exP203.Status);
        Assert.Equal("missing_sheet", exP203.Code);
    }

    internal static byte[] BuildXlsx(
        (string Name, string? RelId)[] sheets,
        (string RelId, string Target, string[][] Rows)[] worksheets,
        (string Path, byte[] Bytes)[]? extraEntries = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteText(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                </Types>
                """);

            var sheetsXml = string.Join("", sheets.Select((s, i) =>
                string.IsNullOrEmpty(s.RelId)
                    ? $"<sheet name=\"{EscapeXml(s.Name)}\" sheetId=\"{i + 1}\"/>"
                    : $"<sheet name=\"{EscapeXml(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"{s.RelId}\"/>"));
            WriteText(zip, "xl/workbook.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets>{sheetsXml}</sheets>
                </workbook>
                """);

            var relsXml = string.Join("", worksheets.Select(w =>
                $"<Relationship Id=\"{w.RelId}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"{w.Target}\"/>"));
            WriteText(zip, "xl/_rels/workbook.xml.rels",
                $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  {relsXml}
                </Relationships>
                """);

            foreach (var (_, target, rows) in worksheets)
            {
                var sb = new StringBuilder();
                sb.Append("""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
                for (var r = 0; r < rows.Length; r++)
                {
                    var rowNum = r + 1;
                    sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNum}\">");
                    for (var c = 0; c < rows[r].Length; c++)
                    {
                        var col = ColumnLetter(c + 1);
                        var raw = rows[r][c];
                        if (raw.StartsWith("FORMULA:", StringComparison.Ordinal))
                        {
                            sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{col}{rowNum}\"><f>{EscapeXml(raw["FORMULA:".Length..])}</f><v>2</v></c>");
                        }
                        else if (raw.StartsWith("BOOL:", StringComparison.Ordinal))
                        {
                            sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{col}{rowNum}\" t=\"b\"><v>{raw["BOOL:".Length..]}</v></c>");
                        }
                        else
                        {
                            sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{col}{rowNum}\" t=\"inlineStr\"><is><t>{EscapeXml(raw)}</t></is></c>");
                        }
                    }

                    sb.Append("</row>");
                }

                sb.Append("</sheetData></worksheet>");
                WriteText(zip, "xl/" + target, sb.ToString());
            }

            if (extraEntries is not null)
            {
                foreach (var (path, bytes) in extraEntries)
                {
                    var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    es.Write(bytes);
                }
            }
        }

        return ms.ToArray();
    }

    private static void WriteText(ZipArchive zip, string path, string text)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string ColumnLetter(int col)
    {
        var sb = new StringBuilder();
        while (col > 0)
        {
            col--;
            sb.Insert(0, (char)('A' + (col % 26)));
            col /= 26;
        }

        return sb.ToString();
    }

    private static string EscapeXml(string s)
        => s.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
