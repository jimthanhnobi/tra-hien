using FluentValidation;
using TeaPremium.Application.Import;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Crm;

namespace TeaPremium.Application.Crm;

public static class CustomerImportSchema
{
    public const string PreferredSheetName = "KhachHang";
    public const string WarningZaloOnlyReimportRisk = "zalo_only_reimport_risk";
    public const string WarningPossibleDuplicateZaloCustomer = "possible_duplicate_zalo_customer";

    public static readonly string[] WarningCodes =
    [
        WarningZaloOnlyReimportRisk,
        WarningPossibleDuplicateZaloCustomer
    ];

    public static readonly IReadOnlySet<string> AllowedColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        "full_name", "phone", "zalo_name", "customer_group", "tags", "note", "had_purchase"
    };

    public static readonly string[] RequiredColumns = ["full_name"];

    public static readonly string[] RowStatuses = ["imported", "would_import", "skipped", "error"];

    public static readonly string[] ReasonCodes =
    [
        "imported",
        "would_import",
        "duplicate_in_file",
        "existing_phone_skipped",
        "missing_full_name",
        "invalid_full_name",
        "missing_contact",
        "invalid_phone",
        "invalid_zalo_name",
        "invalid_customer_group",
        "invalid_tag",
        "invalid_note",
        "invalid_had_purchase"
    ];

    public static readonly ImportTableSchema Table = new(
        AllowedColumns.Contains,
        RequiredColumns,
        CustomerRules.MaxImportRows,
        ImportSheetMode.SingleOrNamed,
        PreferredSheetName,
        "Tệp có hơn 2.000 dòng dữ liệu.");

    public static CustomerImportLine FromTabular(TabularImportLine line) => new(
        line.Row,
        line.Cell("full_name"),
        line.Cell("phone"),
        line.Cell("zalo_name"),
        line.Cell("customer_group"),
        line.Cell("tags"),
        line.Cell("note"),
        line.Cell("had_purchase"));
}

public sealed record CustomerImportLine(
    int Row,
    string FullName,
    string Phone,
    string ZaloName,
    string CustomerGroup,
    string Tags,
    string Note,
    string HadPurchase);

public sealed record CustomerImportFieldError(string Column, string Code, string Message);

public sealed record CustomerImportRow(
    int Row,
    string Status,
    string ReasonCode,
    Guid? CustomerId,
    string? CustomerCode,
    string? NormalizedPhone,
    Guid? ExistingCustomerId,
    int? DuplicateOfRow,
    bool CreateNoteInteraction,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CustomerImportFieldError> Errors);

public sealed record CustomerImportSummary(
    bool DryRun,
    bool Committed,
    int TotalRows,
    int ReadyCount,
    int ImportedCount,
    int SkippedCount,
    int DuplicateInFileCount,
    int ExistingPhoneSkippedCount,
    int ErrorCount,
    int CreatedTagsCount,
    int CreatedInteractionsCount,
    IReadOnlyList<string> NewTags);

public readonly record struct ZaloIdentityKey(string FullNameNormalized, string ZaloNameNormalized)
{
    public static ZaloIdentityKey Create(string fullName, string zaloName)
        => new(fullName.Trim().ToLowerInvariant(), zaloName.Trim().ToLowerInvariant());
}

public sealed record CustomerImportResult(
    CustomerImportSummary Summary,
    IReadOnlyList<CustomerImportRow> Rows);

public sealed record CustomerImportValidatedLine(
    int Row,
    string FullName,
    string? NormalizedPhone,
    string? ZaloName,
    string CustomerGroup,
    string[] Tags,
    string? Note,
    bool HadPurchase,
    IReadOnlyList<CustomerImportFieldError> Errors);

public sealed record CustomerImportPlannedRow(
    int Row,
    string Status,
    string ReasonCode,
    CustomerImportValidatedLine? Validated,
    Guid? ExistingCustomerId,
    int? DuplicateOfRow,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CustomerImportFieldError> Errors);

public static class CustomerImportPlanner
{
    private static readonly string[] ZaloOnlyWarnings = [CustomerImportSchema.WarningZaloOnlyReimportRisk];
    private static readonly string[] ZaloPossibleDuplicateWarnings =
    [
        CustomerImportSchema.WarningZaloOnlyReimportRisk,
        CustomerImportSchema.WarningPossibleDuplicateZaloCustomer
    ];

    public static CustomerImportValidatedLine[] ValidateLines(IReadOnlyList<CustomerImportLine> lines)
    {
        var result = new CustomerImportValidatedLine[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            result[i] = ValidateLine(lines[i]);
        }

        return result;
    }

    public static CustomerImportValidatedLine ValidateLine(CustomerImportLine line)
    {
        var errors = new List<CustomerImportFieldError>();

        var rawFullName = line.FullName ?? "";
        var fullName = rawFullName.Trim();
        if (fullName.Length == 0)
        {
            errors.Add(new("full_name", "missing_full_name", "Thiếu họ tên khách hàng."));
        }
        else if (!CustomerRules.IsValidPostgresText(fullName) || fullName.Length > CustomerRules.MaxFullNameLength)
        {
            errors.Add(new("full_name", "invalid_full_name", "Họ tên không hợp lệ hoặc vượt quá 150 ký tự."));
        }

        var rawPhone = (line.Phone ?? "").Trim();
        var rawZalo = (line.ZaloName ?? "").Trim();
        string? normalizedPhone = null;
        string? zaloName = rawZalo.Length > 0 ? rawZalo : null;

        if (rawPhone.Length == 0 && rawZalo.Length == 0)
        {
            errors.Add(new("phone", "missing_contact", "Khách hàng phải có ít nhất số điện thoại hoặc tên Zalo."));
        }
        else
        {
            if (rawPhone.Length > 0 && !CustomerRules.TryNormalizePhone(rawPhone, out normalizedPhone))
            {
                errors.Add(new("phone", "invalid_phone", "Số điện thoại di động Việt Nam không hợp lệ."));
            }

            if (rawZalo.Length > 0 && (!CustomerRules.IsValidPostgresText(rawZalo) || rawZalo.Length > CustomerRules.MaxZaloNameLength))
            {
                errors.Add(new("zalo_name", "invalid_zalo_name", "Tên Zalo không hợp lệ hoặc vượt quá 150 ký tự."));
            }
        }

        var rawGroup = (line.CustomerGroup ?? "").Trim();
        var customerGroup = "retail";
        if (rawGroup.Length > 0)
        {
            if (CustomerRules.CustomerGroups.Contains(rawGroup))
            {
                customerGroup = rawGroup;
            }
            else
            {
                errors.Add(new("customer_group", "invalid_customer_group", "Nhóm khách hàng phải là retail, wholesale hoặc reseller."));
            }
        }

        if (!CustomerRules.TryParseImportTags(line.Tags, out var tags))
        {
            errors.Add(new("tags", "invalid_tag", "Nhãn không hợp lệ (tối đa 60 ký tự mỗi nhãn, tối đa 50 nhãn/dòng)."));
        }

        var rawNote = (line.Note ?? "").Trim();
        string? note = null;
        if (rawNote.Length > 0)
        {
            if (!CustomerRules.IsValidPostgresText(rawNote) || rawNote.Length > CustomerRules.MaxInteractionContentLength)
            {
                errors.Add(new("note", "invalid_note", "Ghi chú không hợp lệ hoặc vượt quá 4.000 ký tự."));
            }
            else
            {
                note = rawNote;
            }
        }

        if (!CustomerRules.TryParseImportHadPurchase(line.HadPurchase, out var hadPurchase))
        {
            errors.Add(new("had_purchase", "invalid_had_purchase", "Cột had_purchase chỉ nhận true, false, 1, 0 hoặc để trống."));
        }

        return new CustomerImportValidatedLine(
            line.Row,
            fullName,
            normalizedPhone,
            zaloName,
            customerGroup,
            tags,
            note,
            hadPurchase,
            errors);
    }

    public static CustomerImportPlannedRow[] Plan(
        IReadOnlyList<CustomerImportValidatedLine> validated,
        IReadOnlyDictionary<string, Guid> existingActivePhones,
        bool dryRun,
        IReadOnlySet<ZaloIdentityKey>? existingActiveZaloKeys = null)
    {
        var seenPhonesInFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenZaloKeysInFile = new HashSet<ZaloIdentityKey>();
        var planned = new CustomerImportPlannedRow[validated.Count];
        var readyStatus = dryRun ? "would_import" : "imported";

        for (var i = 0; i < validated.Count; i++)
        {
            var line = validated[i];
            if (line.Errors.Count > 0)
            {
                // Invalid rows never claim a phone or Zalo identity slot so later valid rows are not shadowed.
                planned[i] = new CustomerImportPlannedRow(
                    line.Row,
                    "error",
                    line.Errors[0].Code,
                    null,
                    null,
                    null,
                    [],
                    line.Errors);
                continue;
            }

            if (line.NormalizedPhone is { } phone)
            {
                if (seenPhonesInFile.TryGetValue(phone, out var firstRow))
                {
                    planned[i] = new CustomerImportPlannedRow(
                        line.Row,
                        "skipped",
                        "duplicate_in_file",
                        line,
                        null,
                        firstRow,
                        [],
                        []);
                    continue;
                }

                seenPhonesInFile[phone] = line.Row;
                if (existingActivePhones.TryGetValue(phone, out var existingId))
                {
                    planned[i] = new CustomerImportPlannedRow(
                        line.Row,
                        "skipped",
                        "existing_phone_skipped",
                        line,
                        existingId,
                        null,
                        [],
                        []);
                    continue;
                }

                planned[i] = new CustomerImportPlannedRow(
                    line.Row,
                    readyStatus,
                    readyStatus,
                    line,
                    null,
                    null,
                    [],
                    []);
                continue;
            }

            var zaloKey = ZaloIdentityKey.Create(line.FullName, line.ZaloName!);
            var isPossibleDuplicate = (existingActiveZaloKeys is not null && existingActiveZaloKeys.Contains(zaloKey))
                || seenZaloKeysInFile.Contains(zaloKey);
            seenZaloKeysInFile.Add(zaloKey);

            planned[i] = new CustomerImportPlannedRow(
                line.Row,
                readyStatus,
                readyStatus,
                line,
                null,
                null,
                isPossibleDuplicate ? ZaloPossibleDuplicateWarnings : ZaloOnlyWarnings,
                []);
        }

        return planned;
    }

    public static CustomerImportSummary BuildSummary(
        bool dryRun,
        IReadOnlyList<CustomerImportRow> rows,
        int createdTagsCount,
        int createdInteractionsCount,
        IReadOnlyList<string> newTags)
    {
        var readyCount = 0;
        var importedCount = 0;
        var duplicateInFileCount = 0;
        var existingPhoneSkippedCount = 0;
        var errorCount = 0;

        foreach (var row in rows)
        {
            switch (row.ReasonCode)
            {
                case "would_import":
                    readyCount++;
                    break;
                case "imported":
                    readyCount++;
                    importedCount++;
                    break;
                case "duplicate_in_file":
                    duplicateInFileCount++;
                    break;
                case "existing_phone_skipped":
                    existingPhoneSkippedCount++;
                    break;
                default:
                    if (string.Equals(row.Status, "error", StringComparison.Ordinal))
                    {
                        errorCount++;
                    }

                    break;
            }
        }

        var committed = !dryRun && errorCount == 0;
        return new CustomerImportSummary(
            dryRun,
            committed,
            rows.Count,
            readyCount,
            importedCount,
            duplicateInFileCount + existingPhoneSkippedCount,
            duplicateInFileCount,
            existingPhoneSkippedCount,
            errorCount,
            createdTagsCount,
            createdInteractionsCount,
            newTags);
    }
}

public interface ICustomerImportStore
{
    Task<CustomerImportResult> PreviewAsync(IReadOnlyList<CustomerImportLine> lines, CancellationToken ct);
    Task<Result<CustomerImportResult>> ApplyAsync(IReadOnlyList<CustomerImportLine> lines, Guid actorId, DateTime nowUtc, CancellationToken ct);
}

public sealed record PreviewCustomerImport(IReadOnlyList<CustomerImportLine> Lines, CustomerActor Actor) : IQuery<CustomerImportResult>;

public sealed record ApplyCustomerImport(IReadOnlyList<CustomerImportLine> Lines, CustomerActor Actor) : ICommand<CustomerImportResult>;

public sealed class PreviewCustomerImportValidator : AbstractValidator<PreviewCustomerImport>
{
    public PreviewCustomerImportValidator()
    {
        RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.Lines).NotNull().Must(lines => lines is { Count: >= 1 and <= CustomerRules.MaxImportRows })
            .WithMessage("invalid_import_rows");
    }
}

public sealed class ApplyCustomerImportValidator : AbstractValidator<ApplyCustomerImport>
{
    public ApplyCustomerImportValidator()
    {
        RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.Lines).NotNull().Must(lines => lines is { Count: >= 1 and <= CustomerRules.MaxImportRows })
            .WithMessage("invalid_import_rows");
    }
}

public sealed class PreviewCustomerImportHandler(ICustomerImportStore store) : IQueryHandler<PreviewCustomerImport, CustomerImportResult>
{
    public async Task<Result<CustomerImportResult>> Handle(PreviewCustomerImport query, CancellationToken cancellationToken)
    {
        if (!query.Actor.Has("customers.write"))
        {
            return Result.Failure<CustomerImportResult>(CustomerErrors.Forbidden());
        }

        var result = await store.PreviewAsync(query.Lines, cancellationToken);
        return Result.Success(result);
    }
}

public sealed class ApplyCustomerImportHandler(ICustomerImportStore store, IClock clock) : ICommandHandler<ApplyCustomerImport, CustomerImportResult>
{
    public async Task<Result<CustomerImportResult>> Handle(ApplyCustomerImport command, CancellationToken cancellationToken)
    {
        if (!command.Actor.Has("customers.write"))
        {
            return Result.Failure<CustomerImportResult>(CustomerErrors.Forbidden());
        }

        return await store.ApplyAsync(command.Lines, command.Actor.Id, clock.UtcNow.UtcDateTime, cancellationToken);
    }
}
