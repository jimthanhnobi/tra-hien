using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Results;
using TeaPremium.Domain.Crm;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Crm;

public sealed class EfCustomerStore(
    AppDbContext db,
    ICustomerQueries queries,
    IAdministrativeReferenceData adminRef,
    CustomerIdentityAudit auditWriter) : ICustomerStore
{
    private readonly Dictionary<Guid, Customer> _lockedCustomers = [];

    public async Task<string> AllocateCustomerCodeAsync(CancellationToken ct)
    {
        var sequence = await db.Database
            .SqlQueryRaw<long>("SELECT nextval('crm.customer_code_seq') AS \"Value\"")
            .SingleAsync(ct);
        return CustomerRules.FormatCustomerCode(sequence);
    }

    public Task<string> ComputeSearchTextAsync(string source, CancellationToken ct)
        => db.Database
            .SqlQuery<string>($"SELECT public.f_unaccent({source}) AS \"Value\"")
            .SingleAsync(ct);

    public Task<Guid?> FindActiveCustomerIdByPhoneAsync(string phoneE164, Guid? excludeCustomerId, CancellationToken ct)
        => db.Set<Customer>().AsNoTracking()
            .Where(x => x.PhoneE164 == phoneE164
                && x.MergedIntoCustomerId == null
                && (excludeCustomerId == null || x.Id != excludeCustomerId.Value))
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);

    public Task<bool> CustomerExistsAsync(Guid id, CancellationToken ct)
        => db.Set<Customer>().AsNoTracking().AnyAsync(x => x.Id == id, ct);

    public Task<bool> HasCustomerAccountAsync(Guid customerId, CancellationToken ct)
        => db.Set<CustomerAccount>().AsNoTracking().AnyAsync(x => x.CustomerId == customerId, ct);

    public async Task<CustomerRecord?> LockCustomerAsync(Guid id, CancellationToken ct)
    {
        var row = (await db.Set<Customer>()
            .FromSqlInterpolated($"SELECT *, xmin FROM crm.customers WHERE id = {id} FOR NO KEY UPDATE")
            .ToArrayAsync(ct)).SingleOrDefault();
        if (row is null)
        {
            return null;
        }

        _lockedCustomers[id] = row;
        return ToRecord(row);
    }

    public async Task<bool> AllTagsExistAsync(IReadOnlyCollection<Guid> tagIds, CancellationToken ct)
    {
        if (tagIds.Count == 0)
        {
            return true;
        }

        var distinct = tagIds.Distinct().ToArray();
        var count = await db.Set<Tag>().AsNoTracking().CountAsync(x => distinct.Contains(x.Id), ct);
        return count == distinct.Length;
    }

    public async Task<Result<CustomerDetailView>> CreateCustomerAsync(CustomerRecord customer, IReadOnlyList<Guid> tagIds, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Customer creation requires a transaction.");
        var entity = new Customer
        {
            Id = customer.Id,
            Code = customer.Code,
            FullName = customer.FullName,
            PhoneE164 = customer.PhoneE164,
            Email = customer.Email,
            ZaloName = customer.ZaloName,
            CustomerGroup = customer.CustomerGroup,
            IsVip = customer.IsVip,
            Origin = customer.Origin,
            AcquisitionSource = customer.AcquisitionSource,
            ReferredByCustomerId = customer.ReferredByCustomerId,
            ContactStatus = customer.ContactStatus,
            NoteSummary = customer.NoteSummary,
            HadLegacyPurchase = customer.HadLegacyPurchase,
            FirstDeliveredAt = customer.FirstDeliveredAt,
            LastDeliveredAt = customer.LastDeliveredAt,
            DeliveredOrdersCount = customer.DeliveredOrdersCount,
            LifetimeValueVnd = customer.LifetimeValueVnd,
            LastContactedAt = customer.LastContactedAt,
            SearchText = customer.SearchText,
            MergedIntoCustomerId = customer.MergedIntoCustomerId,
            ErasedAt = customer.ErasedAt,
            CreatedBy = customer.CreatedBy,
            CreatedAt = customer.CreatedAt,
            UpdatedAt = customer.UpdatedAt
        };

        db.Add(entity);
        foreach (var tagId in tagIds)
        {
            db.Add(new CustomerTag { CustomerId = entity.Id, TagId = tagId });
        }

        const string savepoint = "p202_create_customer";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex, "ux_customers_phone"))
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            var existingId = customer.PhoneE164 is null
                ? null
                : await FindActiveCustomerIdByPhoneAsync(customer.PhoneE164, null, ct);
            return Result.Failure<CustomerDetailView>(PhoneExistsError(existingId));
        }

        var detail = await queries.GetDetailAsync(entity.Id, ct);
        return detail is null
            ? throw new InvalidOperationException("Created customer could not be reloaded.")
            : Result.Success(detail);
    }

    public async Task<Result<CustomerDetailView>> UpdateCustomerAsync(
        CustomerRecord customer,
        uint expectedVersion,
        CustomerIdentityCorrectionAudit? identityAudit,
        CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Customer update requires a transaction.");
        var entity = _lockedCustomers[customer.Id];

        entity.FullName = customer.FullName;
        entity.PhoneE164 = customer.PhoneE164;
        entity.Email = customer.Email;
        entity.ZaloName = customer.ZaloName;
        entity.CustomerGroup = customer.CustomerGroup;
        entity.IsVip = customer.IsVip;
        entity.Origin = customer.Origin;
        entity.AcquisitionSource = customer.AcquisitionSource;
        entity.ReferredByCustomerId = customer.ReferredByCustomerId;
        entity.ContactStatus = customer.ContactStatus;
        entity.NoteSummary = customer.NoteSummary;
        entity.HadLegacyPurchase = customer.HadLegacyPurchase;
        entity.SearchText = customer.SearchText;
        entity.UpdatedAt = customer.UpdatedAt;

        var entry = db.Entry(entity);
        entry.Property(x => x.Version).OriginalValue = expectedVersion;
        entry.Property(x => x.UpdatedAt).IsModified = true;

        if (identityAudit is not null)
        {
            auditWriter.Record(identityAudit);
        }

        const string savepoint = "p202_update_customer";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            return Result.Failure<CustomerDetailView>(ConcurrencyError());
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex, "ux_customers_phone"))
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            var existingId = customer.PhoneE164 is null
                ? null
                : await FindActiveCustomerIdByPhoneAsync(customer.PhoneE164, customer.Id, ct);
            return Result.Failure<CustomerDetailView>(PhoneExistsError(existingId));
        }

        var detail = await queries.GetDetailAsync(entity.Id, ct);
        return detail is null
            ? throw new InvalidOperationException("Updated customer could not be reloaded.")
            : Result.Success(detail);
    }

    public async Task<Result<CustomerTagView>> CreateTagAsync(Guid id, string name, string? color, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Tag creation requires a transaction.");
        var tag = new Tag { Id = id, Name = name, Color = color };
        db.Add(tag);

        const string savepoint = "p202_create_tag";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex, "ux_tags_name"))
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            return Result.Failure<CustomerTagView>(TagNameExistsError());
        }

        return Result.Success(new CustomerTagView(tag.Id, tag.Name, tag.Color));
    }

    public async Task<Result<CustomerTagView>> UpdateTagAsync(Guid id, string? name, bool hasName, string? color, bool hasColor, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Tag update requires a transaction.");
        var tag = (await db.Set<Tag>()
            .FromSqlInterpolated($"SELECT * FROM crm.tags WHERE id = {id} FOR UPDATE")
            .ToArrayAsync(ct)).SingleOrDefault();
        if (tag is null)
        {
            return Result.Failure<CustomerTagView>(new Error("not_found", "Không tìm thấy nhãn khách hàng.", ErrorType.NotFound));
        }

        if (hasName)
        {
            tag.Name = name!;
        }

        if (hasColor)
        {
            tag.Color = color;
        }

        const string savepoint = "p202_update_tag";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex, "ux_tags_name"))
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            return Result.Failure<CustomerTagView>(TagNameExistsError());
        }

        return Result.Success(new CustomerTagView(tag.Id, tag.Name, tag.Color));
    }

    public async Task<Result<CustomerTagsResult>> ReplaceCustomerTagsAsync(
        Guid customerId,
        uint? expectedVersion,
        IReadOnlyList<Guid> tagIds,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Tag assignment requires a transaction.");
        var customer = _lockedCustomers[customerId];

        var existing = await db.Set<CustomerTag>()
            .Where(x => x.CustomerId == customerId)
            .ToArrayAsync(ct);
        var desired = tagIds.ToHashSet();
        var existingSet = existing.Select(x => x.TagId).ToHashSet();

        foreach (var row in existing)
        {
            if (!desired.Contains(row.TagId))
            {
                db.Remove(row);
            }
        }

        foreach (var tagId in desired)
        {
            if (!existingSet.Contains(tagId))
            {
                db.Add(new CustomerTag { CustomerId = customerId, TagId = tagId });
            }
        }

        customer.UpdatedAt = nowUtc;
        var entry = db.Entry(customer);
        if (expectedVersion is { } ev)
        {
            entry.Property(x => x.Version).OriginalValue = ev;
        }

        entry.Property(x => x.UpdatedAt).IsModified = true;

        const string savepoint = "p202_put_tags";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            return Result.Failure<CustomerTagsResult>(ConcurrencyError());
        }

        var assigned = await (
            from ctRow in db.Set<CustomerTag>().AsNoTracking()
            join t in db.Set<Tag>().AsNoTracking() on ctRow.TagId equals t.Id
            where ctRow.CustomerId == customerId
            orderby t.Name, t.Id
            select new CustomerTagView(t.Id, t.Name, t.Color))
            .ToArrayAsync(ct);

        return Result.Success(new CustomerTagsResult(customerId, CatalogVersions.Encode(customer.Version), assigned));
    }

    public async Task<CustomerAddressRecord?> LoadAddressAsync(Guid customerId, Guid addressId, CancellationToken ct)
    {
        var row = await db.Set<CustomerAddress>()
            .SingleOrDefaultAsync(x => x.Id == addressId && x.CustomerId == customerId, ct);
        return row is null ? null : ToAddressRecord(row);
    }

    public async Task<Result<CustomerAddressView>> CreateAddressAsync(
        CustomerAddressRecord address,
        uint? expectedCustomerVersion,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Address creation requires a transaction.");
        var customer = _lockedCustomers[address.CustomerId];

        const string savepoint = "p202_create_address";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            // Step 1: clear any existing default before inserting a new default row (ux_customer_default_address is non-deferrable).
            if (address.IsDefault)
            {
                await ClearDefaultAddressAsync(address.CustomerId, null, nowUtc, ct);
            }

            var entity = new CustomerAddress
            {
                Id = address.Id,
                CustomerId = address.CustomerId,
                Label = address.Label,
                ReceiverName = address.ReceiverName,
                ReceiverPhone = address.ReceiverPhone,
                ProvinceCode = address.ProvinceCode,
                WardCode = address.WardCode,
                WardNameText = address.WardNameText,
                AddressLine = address.AddressLine,
                IsDefault = address.IsDefault,
                CreatedAt = address.CreatedAt,
                UpdatedAt = address.UpdatedAt
            };
            db.Add(entity);

            TouchCustomer(customer, expectedCustomerVersion, nowUtc);
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);

            return Result.Success(await BuildAddressViewAsync(entity, ct));
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            return Result.Failure<CustomerAddressView>(ConcurrencyError());
        }
    }

    public async Task<Result<CustomerAddressView>> UpdateAddressAsync(
        CustomerAddressRecord address,
        uint? expectedCustomerVersion,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Address update requires a transaction.");
        var customer = _lockedCustomers[address.CustomerId];
        var entity = await db.Set<CustomerAddress>()
            .SingleOrDefaultAsync(x => x.Id == address.Id && x.CustomerId == address.CustomerId, ct);
        if (entity is null)
        {
            return Result.Failure<CustomerAddressView>(new Error("not_found", "Không tìm thấy địa chỉ khách hàng.", ErrorType.NotFound));
        }

        const string savepoint = "p202_update_address";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            if (address.IsDefault && !entity.IsDefault)
            {
                await ClearDefaultAddressAsync(address.CustomerId, entity.Id, nowUtc, ct);
            }

            entity.Label = address.Label;
            entity.ReceiverName = address.ReceiverName;
            entity.ReceiverPhone = address.ReceiverPhone;
            entity.ProvinceCode = address.ProvinceCode;
            entity.WardCode = address.WardCode;
            entity.WardNameText = address.WardNameText;
            entity.AddressLine = address.AddressLine;
            entity.IsDefault = address.IsDefault;
            entity.UpdatedAt = address.UpdatedAt;

            TouchCustomer(customer, expectedCustomerVersion, nowUtc);
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);

            return Result.Success(await BuildAddressViewAsync(entity, ct));
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            return Result.Failure<CustomerAddressView>(ConcurrencyError());
        }
    }

    public async Task<Result<bool>> DeleteAddressAsync(
        Guid customerId,
        Guid addressId,
        uint? expectedCustomerVersion,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Address deletion requires a transaction.");
        var customer = _lockedCustomers[customerId];
        var entity = await db.Set<CustomerAddress>()
            .SingleOrDefaultAsync(x => x.Id == addressId && x.CustomerId == customerId, ct);
        if (entity is null)
        {
            return Result.Failure<bool>(new Error("not_found", "Không tìm thấy địa chỉ khách hàng.", ErrorType.NotFound));
        }

        const string savepoint = "p202_delete_address";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            db.Remove(entity);
            TouchCustomer(customer, expectedCustomerVersion, nowUtc);
            await db.SaveChangesAsync(ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);
            return Result.Success(true);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            return Result.Failure<bool>(ConcurrencyError());
        }
    }

    private async Task ClearDefaultAddressAsync(Guid customerId, Guid? excludeAddressId, DateTime nowUtc, CancellationToken ct)
    {
        var currentDefaults = await db.Set<CustomerAddress>()
            .Where(x => x.CustomerId == customerId && x.IsDefault && (excludeAddressId == null || x.Id != excludeAddressId.Value))
            .ToArrayAsync(ct);
        if (currentDefaults.Length == 0)
        {
            return;
        }

        foreach (var existingDefault in currentDefaults)
        {
            existingDefault.IsDefault = false;
            existingDefault.UpdatedAt = nowUtc;
        }

        await db.SaveChangesAsync(ct);
    }

    private void TouchCustomer(Customer customer, uint? expectedCustomerVersion, DateTime nowUtc)
    {
        customer.UpdatedAt = nowUtc;
        var entry = db.Entry(customer);
        if (expectedCustomerVersion is { } ev)
        {
            entry.Property(x => x.Version).OriginalValue = ev;
        }

        entry.Property(x => x.UpdatedAt).IsModified = true;
    }

    private async Task<CustomerAddressView> BuildAddressViewAsync(CustomerAddress entity, CancellationToken ct)
    {
        var locations = await adminRef.ResolveLocationsAsync([(entity.ProvinceCode, entity.WardCode)], ct);
        locations.TryGetValue((entity.ProvinceCode, entity.WardCode), out var loc);
        return new CustomerAddressView(
            entity.Id,
            entity.CustomerId,
            entity.Label,
            entity.ReceiverName,
            entity.ReceiverPhone,
            entity.ProvinceCode,
            loc?.ProvinceName ?? "",
            entity.WardCode,
            loc?.WardName,
            entity.WardNameText,
            entity.AddressLine,
            entity.IsDefault,
            loc?.IsLocationActive ?? false,
            entity.CreatedAt,
            entity.UpdatedAt);
    }

    private static bool IsUniqueConstraint(DbUpdateException ex, string constraintName)
        => ex.InnerException is PostgresException pg
           && pg.SqlState == PostgresErrorCodes.UniqueViolation
           && string.Equals(pg.ConstraintName, constraintName, StringComparison.Ordinal);

    private static Error ConcurrencyError()
        => new("concurrency_conflict", "Dữ liệu khách hàng đã thay đổi, vui lòng tải lại.", ErrorType.Conflict);

    private static Error TagNameExistsError()
        => new("tag_name_exists", "Tên nhãn khách hàng đã tồn tại.", ErrorType.Conflict);

    private static Error PhoneExistsError(Guid? existingCustomerId)
        => new("customer_phone_exists", "Số điện thoại đã tồn tại ở hồ sơ khách hàng khác.", ErrorType.Conflict,
            Data: existingCustomerId is { } id
                ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["existingCustomerId"] = id }
                : null);

    private static CustomerRecord ToRecord(Customer row) => new()
    {
        Id = row.Id,
        Code = row.Code,
        FullName = row.FullName,
        PhoneE164 = row.PhoneE164,
        Email = row.Email,
        ZaloName = row.ZaloName,
        CustomerGroup = row.CustomerGroup,
        IsVip = row.IsVip,
        Origin = row.Origin,
        AcquisitionSource = row.AcquisitionSource,
        ReferredByCustomerId = row.ReferredByCustomerId,
        ContactStatus = row.ContactStatus,
        NoteSummary = row.NoteSummary,
        HadLegacyPurchase = row.HadLegacyPurchase,
        FirstDeliveredAt = row.FirstDeliveredAt,
        LastDeliveredAt = row.LastDeliveredAt,
        DeliveredOrdersCount = row.DeliveredOrdersCount,
        LifetimeValueVnd = row.LifetimeValueVnd,
        LastContactedAt = row.LastContactedAt,
        SearchText = row.SearchText,
        MergedIntoCustomerId = row.MergedIntoCustomerId,
        ErasedAt = row.ErasedAt,
        CreatedBy = row.CreatedBy,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        Version = row.Version
    };

    private static CustomerAddressRecord ToAddressRecord(CustomerAddress row) => new()
    {
        Id = row.Id,
        CustomerId = row.CustomerId,
        Label = row.Label,
        ReceiverName = row.ReceiverName,
        ReceiverPhone = row.ReceiverPhone,
        ProvinceCode = row.ProvinceCode,
        WardCode = row.WardCode,
        WardNameText = row.WardNameText,
        AddressLine = row.AddressLine,
        IsDefault = row.IsDefault,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt
    };
}
