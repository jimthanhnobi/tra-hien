using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm.Contracts;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Domain.Crm;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Crm;

// 02-architecture.md §3 / CrmModuleBoundaryRule: strictly touches only crm.customers (and AuditLog via interceptor).
public sealed class EfCustomerOrderResolver(AppDbContext db, IClock clock) : ICustomerOrderResolver
{
    public async Task<Result<ResolvedOrderCustomer>> ResolveExistingAsync(Guid customerId, CancellationToken ct)
    {
        // Lock crm.customers FOR SHARE inside an active transaction so concurrent Erase/Merge (FOR NO KEY UPDATE)
        // strictly serialize against order creation without blocking parallel orders for the same customer.
        Customer? row;
        if (db.Database.CurrentTransaction is not null)
        {
            row = (await db.Set<Customer>()
                .FromSqlInterpolated($"SELECT *, xmin FROM crm.customers WHERE id = {customerId} FOR SHARE")
                .AsNoTracking()
                .ToArrayAsync(ct)).SingleOrDefault();
        }
        else
        {
            row = await db.Set<Customer>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == customerId, ct);
        }

        if (row is null)
            return Result.Failure<ResolvedOrderCustomer>(new("not_found", "Không tìm thấy khách hàng.", ErrorType.NotFound));

        if (string.Equals(row.ContactStatus, "erased", StringComparison.Ordinal))
            return Result.Failure<ResolvedOrderCustomer>(new("customer_erased", "Hồ sơ khách hàng đã được xóa dữ liệu, không thể tạo đơn.", ErrorType.Conflict));

        if (row.MergedIntoCustomerId is not null)
            return Result.Failure<ResolvedOrderCustomer>(new("customer_merged", "Hồ sơ khách hàng đã được gộp, không thể tạo đơn.", ErrorType.Conflict));

        return Result.Success(new ResolvedOrderCustomer(
            row.Id,
            row.Code,
            row.FullName,
            row.PhoneE164,
            row.Email,
            row.AcquisitionSource,
            row.HadLegacyPurchase,
            row.DeliveredOrdersCount,
            IsNewlyCreated: false));
    }

    public async Task<Result<ResolvedOrderCustomer>> CreateQuickAdminCustomerAsync(
        string fullName,
        string phone,
        Guid staffId,
        CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Quick customer creation requires an active transaction.");

        if (string.IsNullOrWhiteSpace(fullName) || !CatalogInput.WithoutNul(fullName)
            || fullName.Trim().Length is < 1 or > CustomerRules.MaxFullNameLength)
        {
            return Result.Failure<ResolvedOrderCustomer>(new(
                "validation_failed",
                "Họ tên khách hàng không hợp lệ.",
                ErrorType.Validation,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["newCustomer.fullName"] = ["invalid_full_name"] }));
        }

        if (!CustomerRules.TryNormalizePhone(phone, out var phoneE164) || phoneE164 is null)
        {
            return Result.Failure<ResolvedOrderCustomer>(new(
                "invalid_phone",
                "Số điện thoại di động Việt Nam không hợp lệ.",
                ErrorType.Validation,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["newCustomer.phone"] = ["invalid_phone"] }));
        }

        // BR-CRM-02: check duplicate active phone before allocating crm.customer_code_seq.
        var existingId = await FindActiveCustomerIdByPhoneAsync(phoneE164, ct);
        if (existingId is not null)
            return Result.Failure<ResolvedOrderCustomer>(PhoneExistsError(existingId));

        var trimmedName = fullName.Trim();
        var sequence = await db.Database
            .SqlQueryRaw<long>("SELECT nextval('crm.customer_code_seq') AS \"Value\"")
            .SingleAsync(ct);
        var code = CustomerRules.FormatCustomerCode(sequence);
        var searchSource = CustomerRules.BuildSearchSource(trimmedName, null, code, phoneE164, null);
        var searchText = await db.Database
            .SqlQuery<string>($"SELECT public.f_unaccent({searchSource}) AS \"Value\"")
            .SingleAsync(ct);

        var nowUtc = clock.UtcNow.UtcDateTime;
        var entity = new Customer
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            FullName = trimmedName,
            PhoneE164 = phoneE164,
            Email = null,
            ZaloName = null,
            CustomerGroup = "retail",
            IsVip = false,
            Origin = "admin",
            AcquisitionSource = "unknown",
            ReferredByCustomerId = null,
            ContactStatus = "active",
            NoteSummary = null,
            HadLegacyPurchase = false,
            FirstDeliveredAt = null,
            LastDeliveredAt = null,
            DeliveredOrdersCount = 0,
            LifetimeValueVnd = 0L,
            LastContactedAt = null,
            SearchText = searchText,
            MergedIntoCustomerId = null,
            ErasedAt = null,
            CreatedBy = staffId,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc
        };

        // Snapshot previously tracked entities (e.g. outer IdempotencyKey reservation) so savepoint rollback only detaches new rows.
        var prior = db.ChangeTracker.Entries()
            .Select(x => x.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        const string savepoint = "p206_quick_customer";
        await tx.CreateSavepointAsync(savepoint, ct);
        db.Set<Customer>().Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_customers_phone"
        })
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            foreach (var entry in db.ChangeTracker.Entries().Where(x => !prior.Contains(x.Entity)).ToArray())
                entry.State = EntityState.Detached;
            await tx.ReleaseSavepointAsync(savepoint, ct);

            var racedId = await FindActiveCustomerIdByPhoneAsync(phoneE164, ct);
            return Result.Failure<ResolvedOrderCustomer>(PhoneExistsError(racedId));
        }

        return Result.Success(new ResolvedOrderCustomer(
            entity.Id,
            entity.Code,
            entity.FullName,
            entity.PhoneE164,
            entity.Email,
            entity.AcquisitionSource,
            entity.HadLegacyPurchase,
            entity.DeliveredOrdersCount,
            IsNewlyCreated: true));
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveCustomerCodesAsync(
        IReadOnlyCollection<Guid> customerIds,
        CancellationToken ct)
    {
        if (customerIds.Count == 0)
            return new Dictionary<Guid, string>();

        var distinct = customerIds.Distinct().ToArray();
        var rows = await db.Set<Customer>().AsNoTracking()
            .Where(x => distinct.Contains(x.Id))
            .Select(x => new { x.Id, x.Code })
            .ToArrayAsync(ct);

        return rows.ToDictionary(x => x.Id, x => x.Code);
    }

    private Task<Guid?> FindActiveCustomerIdByPhoneAsync(string phoneE164, CancellationToken ct) =>
        db.Set<Customer>().AsNoTracking()
            .Where(x => x.PhoneE164 == phoneE164 && x.MergedIntoCustomerId == null)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);

    private static Error PhoneExistsError(Guid? existingCustomerId) => new(
        "customer_phone_exists",
        "Số điện thoại đã tồn tại ở hồ sơ khách hàng khác.",
        ErrorType.Conflict,
        Data: existingCustomerId is { } id
            ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["existingCustomerId"] = id }
            : null);
}
