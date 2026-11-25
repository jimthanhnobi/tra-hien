using System.Text.Json;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Crm;

namespace TeaPremium.Application.Crm;

internal static class CustomerErrors
{
    public static Error NotFound(string message = "Không tìm thấy khách hàng.")
        => new("not_found", message, ErrorType.NotFound);

    public static Error Forbidden()
        => new("forbidden", "Bạn không có quyền thực hiện thao tác này.", ErrorType.Forbidden);

    public static Error ConcurrencyConflict()
        => new("concurrency_conflict", "Dữ liệu khách hàng đã thay đổi, vui lòng tải lại.", ErrorType.Conflict);

    public static Error CustomerErased()
        => new("customer_erased", "Hồ sơ khách hàng đã được xóa dữ liệu, không thể chỉnh sửa.", ErrorType.Conflict);

    public static Error CustomerMerged()
        => new("customer_merged", "Hồ sơ khách hàng đã được gộp, không thể chỉnh sửa.", ErrorType.Conflict);

    public static Error InvalidPhone(string field = "phone")
        => new("invalid_phone", "Số điện thoại di động Việt Nam không hợp lệ.", ErrorType.Validation,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = ["invalid_phone"] });

    public static Error ContactRequired()
        => new("contact_required", "Khách hàng phải có ít nhất số điện thoại hoặc tên Zalo.", ErrorType.Validation,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["phone"] = ["contact_required"] });

    public static Error PhoneExists(Guid? existingCustomerId)
        => new("customer_phone_exists", "Số điện thoại đã tồn tại ở hồ sơ khách hàng khác.", ErrorType.Conflict,
            Data: existingCustomerId is { } id
                ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["existingCustomerId"] = id }
                : null);

    public static Error AccountExists()
        => new("account_exists", "Khách hàng đã có tài khoản web, không thể đổi hoặc xóa số điện thoại qua thao tác này.", ErrorType.Conflict);

    public static Error Validation(string field, string code, string message = "Dữ liệu không hợp lệ.")
        => new("validation_failed", message, ErrorType.Validation,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [code] });

    public static Error AdministrativeDataNotReady()
        => Error.BusinessRule("administrative_data_not_ready", "Danh mục địa giới hành chính chưa sẵn sàng.");

    public static Error InvalidAddressLocation()
        => new("invalid_address_location", "Mã tỉnh/thành hoặc phường/xã không hợp lệ hoặc đã ngừng hoạt động.", ErrorType.Validation,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["provinceCode"] = ["invalid_address_location"] });

    public static Error? CheckMutableCustomer(CustomerRecord? customer, uint? expectedVersion)
    {
        if (customer is null) return NotFound();
        if (string.Equals(customer.ContactStatus, "erased", StringComparison.Ordinal)) return CustomerErased();
        if (customer.MergedIntoCustomerId is not null) return CustomerMerged();
        if (expectedVersion is { } expected && customer.Version != expected) return ConcurrencyConflict();
        return null;
    }
}

public sealed class ListCustomersHandler(ICustomerQueries queries) : IQueryHandler<ListCustomers, CatalogPage<CustomerSummaryView>>
{
    public async Task<Result<CatalogPage<CustomerSummaryView>>> Handle(ListCustomers query, CancellationToken cancellationToken)
    {
        if (!CustomerFilterParser.TryParseInterest(query.Interest, out var interestType, out var interestRefId, out var priceBand))
        {
            return Result.Failure<CatalogPage<CustomerSummaryView>>(CustomerErrors.Validation("interest", "invalid_interest"));
        }

        if (!CustomerFilterParser.TryParseConsent(query.HasConsent, out var consentChannel, out var consentPurpose))
        {
            return Result.Failure<CatalogPage<CustomerSummaryView>>(CustomerErrors.Validation("hasConsent", "invalid_consent_filter"));
        }

        if (!CustomerFilterParser.TryParseDateFilter(query.LastDeliveredBefore, out var beforeUtc))
        {
            return Result.Failure<CatalogPage<CustomerSummaryView>>(CustomerErrors.Validation("lastDeliveredBefore", "invalid_date"));
        }

        if (!CustomerFilterParser.TryParseDateFilter(query.LastDeliveredAfter, out var afterUtc))
        {
            return Result.Failure<CatalogPage<CustomerSummaryView>>(CustomerErrors.Validation("lastDeliveredAfter", "invalid_date"));
        }

        var q = string.IsNullOrWhiteSpace(query.Q) ? null : query.Q.Trim();
        var phoneDigits = CustomerRules.ExtractPhoneSearchDigits(q);

        var filter = new CustomerListFilter(
            query.Page,
            query.PageSize,
            query.Sort ?? "createdAt:desc",
            q,
            phoneDigits,
            query.Group,
            query.Tag,
            interestType,
            interestRefId,
            priceBand,
            beforeUtc,
            afterUtc,
            query.MinLtv,
            query.ContactStatus,
            consentChannel,
            consentPurpose);

        return Result.Success(await queries.ListAsync(filter, cancellationToken));
    }
}

public sealed class GetCustomerHandler(ICustomerQueries queries) : IQueryHandler<GetCustomer, CustomerDetailView>
{
    public async Task<Result<CustomerDetailView>> Handle(GetCustomer query, CancellationToken cancellationToken)
    {
        var detail = await queries.GetDetailAsync(query.Id, cancellationToken);
        return detail is null ? Result.Failure<CustomerDetailView>(CustomerErrors.NotFound()) : Result.Success(detail);
    }
}

public sealed class ListCustomerOrdersHandler(ICustomerQueries customers, ICustomerOrderQueries orders)
    : IQueryHandler<ListCustomerOrders, CatalogPage<CustomerOrderSummaryView>>
{
    public async Task<Result<CatalogPage<CustomerOrderSummaryView>>> Handle(ListCustomerOrders query, CancellationToken cancellationToken)
    {
        if (!await customers.ExistsAsync(query.CustomerId, cancellationToken))
        {
            return Result.Failure<CatalogPage<CustomerOrderSummaryView>>(CustomerErrors.NotFound());
        }

        return Result.Success(await orders.ListByCustomerAsync(query.CustomerId, query.Page, query.PageSize, cancellationToken));
    }
}

public sealed class CreateCustomerHandler(ICustomerStore store, IClock clock) : ICommandHandler<CreateCustomer, CustomerDetailView>
{
    public async Task<Result<CustomerDetailView>> Handle(CreateCustomer command, CancellationToken cancellationToken)
    {
        if (!CustomerRules.TryNormalizePhone(command.Phone, out var phoneE164))
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.InvalidPhone("phone"));
        }

        var fullName = command.FullName.Trim();
        var email = command.Email?.Trim();
        var zaloName = command.ZaloName?.Trim();
        var noteSummary = NormalizeOptionalText(command.NoteSummary);
        var customerGroup = command.CustomerGroup ?? "retail";
        var acquisitionSource = command.AcquisitionSource ?? "unknown";
        var contactStatus = command.ContactStatus ?? "active";

        if (!CustomerRules.HasValidContact(phoneE164, zaloName, contactStatus, null))
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.ContactRequired());
        }

        if (phoneE164 is not null)
        {
            var existingId = await store.FindActiveCustomerIdByPhoneAsync(phoneE164, null, cancellationToken);
            if (existingId is not null)
            {
                return Result.Failure<CustomerDetailView>(CustomerErrors.PhoneExists(existingId));
            }
        }

        if (command.ReferredByCustomerId is { } referrerId && !await store.CustomerExistsAsync(referrerId, cancellationToken))
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.Validation("referredByCustomerId", "referred_customer_not_found"));
        }

        var distinctTagIds = (command.TagIds ?? []).Distinct().ToArray();
        if (distinctTagIds.Length > 0 && !await store.AllTagsExistAsync(distinctTagIds, cancellationToken))
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.Validation("tagIds", "tag_not_found"));
        }

        var now = clock.UtcNow.UtcDateTime;
        var code = await store.AllocateCustomerCodeAsync(cancellationToken);
        var searchSource = CustomerRules.BuildSearchSource(fullName, zaloName, code, phoneE164, email);
        var searchText = await store.ComputeSearchTextAsync(searchSource, cancellationToken);

        var record = new CustomerRecord
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            FullName = fullName,
            PhoneE164 = phoneE164,
            Email = email,
            ZaloName = zaloName,
            CustomerGroup = customerGroup,
            IsVip = command.IsVip,
            Origin = "admin",
            AcquisitionSource = acquisitionSource,
            ReferredByCustomerId = command.ReferredByCustomerId,
            ContactStatus = contactStatus,
            NoteSummary = noteSummary,
            HadLegacyPurchase = command.HadLegacyPurchase,
            DeliveredOrdersCount = 0,
            LifetimeValueVnd = 0,
            SearchText = searchText,
            CreatedBy = command.Actor.Id,
            CreatedAt = now,
            UpdatedAt = now
        };

        return await store.CreateCustomerAsync(record, distinctTagIds, cancellationToken);
    }

    internal static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class PatchCustomerHandler(ICustomerStore store, IClock clock) : ICommandHandler<PatchCustomer, CustomerDetailView>
{
    public async Task<Result<CustomerDetailView>> Handle(PatchCustomer command, CancellationToken cancellationToken)
    {
        var fields = command.Fields;
        var rowVersionRaw = fields.GetProperty("rowVersion").GetString();
        if (!CatalogVersions.TryDecode(rowVersionRaw, out var expectedVersion))
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.Validation("rowVersion", "invalid_row_version"));
        }

        var customer = await store.LockCustomerAsync(command.Id, cancellationToken);
        if (CustomerErrors.CheckMutableCustomer(customer, expectedVersion) is { } stateError)
        {
            return Result.Failure<CustomerDetailView>(stateError);
        }

        var target = customer!;
        if (fields.TryGetProperty("fullName", out var fullNameProp))
        {
            target.FullName = fullNameProp.GetString()!.Trim();
        }

        if (fields.TryGetProperty("email", out var emailProp))
        {
            target.Email = emailProp.ValueKind == JsonValueKind.Null ? null : emailProp.GetString()!.Trim();
        }

        if (fields.TryGetProperty("zaloName", out var zaloProp))
        {
            target.ZaloName = zaloProp.ValueKind == JsonValueKind.Null ? null : zaloProp.GetString()!.Trim();
        }

        if (fields.TryGetProperty("customerGroup", out var groupProp))
        {
            target.CustomerGroup = groupProp.GetString()!;
        }

        if (fields.TryGetProperty("isVip", out var vipProp))
        {
            target.IsVip = vipProp.GetBoolean();
        }

        if (fields.TryGetProperty("referredByCustomerId", out var refProp))
        {
            Guid? referredBy = refProp.ValueKind == JsonValueKind.Null
                ? null
                : Guid.ParseExact(refProp.GetString()!, "D");
            if (CustomerRules.IsSelfReferral(target.Id, referredBy))
            {
                return Result.Failure<CustomerDetailView>(CustomerErrors.Validation("referredByCustomerId", "self_referral_not_allowed"));
            }

            if (referredBy is { } refId && !await store.CustomerExistsAsync(refId, cancellationToken))
            {
                return Result.Failure<CustomerDetailView>(CustomerErrors.Validation("referredByCustomerId", "referred_customer_not_found"));
            }

            target.ReferredByCustomerId = referredBy;
        }

        if (fields.TryGetProperty("contactStatus", out var statusProp))
        {
            target.ContactStatus = statusProp.GetString()!;
        }

        if (fields.TryGetProperty("noteSummary", out var noteProp))
        {
            target.NoteSummary = noteProp.ValueKind == JsonValueKind.Null
                ? null
                : CreateCustomerHandler.NormalizeOptionalText(noteProp.GetString());
        }

        if (!CustomerRules.HasValidContact(target.PhoneE164, target.ZaloName, target.ContactStatus, target.MergedIntoCustomerId))
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.ContactRequired());
        }

        var searchSource = CustomerRules.BuildSearchSource(target.FullName, target.ZaloName, target.Code, target.PhoneE164, target.Email);
        target.SearchText = await store.ComputeSearchTextAsync(searchSource, cancellationToken);
        target.UpdatedAt = clock.UtcNow.UtcDateTime;

        return await store.UpdateCustomerAsync(target, expectedVersion, null, cancellationToken);
    }
}

public sealed class CorrectCustomerIdentityHandler(ICustomerStore store, IClock clock) : ICommandHandler<CorrectCustomerIdentity, CustomerDetailView>
{
    public async Task<Result<CustomerDetailView>> Handle(CorrectCustomerIdentity command, CancellationToken cancellationToken)
    {
        var fields = command.Fields;
        if (!command.Actor.IsOwner && CustomerFieldSets.HasOwnerOnlyIdentityField(fields))
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.Forbidden());
        }

        var rowVersionRaw = fields.GetProperty("rowVersion").GetString();
        if (!CatalogVersions.TryDecode(rowVersionRaw, out var expectedVersion))
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.Validation("rowVersion", "invalid_row_version"));
        }

        var reason = fields.GetProperty("reason").GetString()!.Trim();
        var hasPhone = fields.TryGetProperty("phone", out var phoneProp);
        var hasOrigin = fields.TryGetProperty("origin", out var originProp);
        var hasAcquisitionSource = fields.TryGetProperty("acquisitionSource", out var acquisitionProp);
        var hasHadLegacyPurchase = fields.TryGetProperty("hadLegacyPurchase", out var legacyProp);

        string? normalizedPhone = null;
        if (hasPhone && phoneProp.ValueKind == JsonValueKind.String)
        {
            if (!CustomerRules.TryNormalizePhone(phoneProp.GetString(), out normalizedPhone))
            {
                return Result.Failure<CustomerDetailView>(CustomerErrors.InvalidPhone("phone"));
            }
        }

        var customer = await store.LockCustomerAsync(command.Id, cancellationToken);
        if (CustomerErrors.CheckMutableCustomer(customer, expectedVersion) is { } stateError)
        {
            return Result.Failure<CustomerDetailView>(stateError);
        }

        var target = customer!;

        // GAP-P202-01 = C (locked): if customer already has a crm.customer_accounts row,
        // changing or clearing phone via correct-identity is rejected with 409 account_exists (follow-up P3-05).
        if (hasPhone && await store.HasCustomerAccountAsync(target.Id, cancellationToken))
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.AccountExists());
        }

        var changedFields = new List<string>(4);
        var phoneChanged = false;
        string? beforeOrigin = null;
        string? afterOrigin = null;
        string? beforeAcquisition = null;
        string? afterAcquisition = null;
        bool? beforeLegacy = null;
        bool? afterLegacy = null;

        if (hasPhone)
        {
            var newPhone = phoneProp.ValueKind == JsonValueKind.Null ? null : normalizedPhone;
            if (!CustomerRules.HasValidContact(newPhone, target.ZaloName, target.ContactStatus, target.MergedIntoCustomerId))
            {
                return Result.Failure<CustomerDetailView>(CustomerErrors.ContactRequired());
            }

            if (newPhone is not null && !string.Equals(newPhone, target.PhoneE164, StringComparison.Ordinal))
            {
                var existingId = await store.FindActiveCustomerIdByPhoneAsync(newPhone, target.Id, cancellationToken);
                if (existingId is not null)
                {
                    return Result.Failure<CustomerDetailView>(CustomerErrors.PhoneExists(existingId));
                }
            }

            if (!string.Equals(target.PhoneE164, newPhone, StringComparison.Ordinal))
            {
                phoneChanged = true;
                changedFields.Add("phone_e164");
                target.PhoneE164 = newPhone;
            }
        }

        if (hasOrigin)
        {
            var nextOrigin = originProp.GetString()!;
            if (!string.Equals(target.Origin, nextOrigin, StringComparison.Ordinal))
            {
                beforeOrigin = target.Origin;
                afterOrigin = nextOrigin;
                changedFields.Add("origin");
                target.Origin = nextOrigin;
            }
        }

        if (hasAcquisitionSource)
        {
            var nextSource = acquisitionProp.GetString()!;
            if (!string.Equals(target.AcquisitionSource, nextSource, StringComparison.Ordinal))
            {
                beforeAcquisition = target.AcquisitionSource;
                afterAcquisition = nextSource;
                changedFields.Add("acquisition_source");
                target.AcquisitionSource = nextSource;
            }
        }

        if (hasHadLegacyPurchase)
        {
            var nextLegacy = legacyProp.GetBoolean();
            if (target.HadLegacyPurchase != nextLegacy)
            {
                beforeLegacy = target.HadLegacyPurchase;
                afterLegacy = nextLegacy;
                changedFields.Add("had_legacy_purchase");
                target.HadLegacyPurchase = nextLegacy;
            }
        }

        var searchSource = CustomerRules.BuildSearchSource(target.FullName, target.ZaloName, target.Code, target.PhoneE164, target.Email);
        target.SearchText = await store.ComputeSearchTextAsync(searchSource, cancellationToken);
        target.UpdatedAt = clock.UtcNow.UtcDateTime;

        var audit = new CustomerIdentityCorrectionAudit(
            target.Id,
            changedFields.ToArray(),
            beforeOrigin,
            afterOrigin,
            beforeAcquisition,
            afterAcquisition,
            beforeLegacy,
            afterLegacy,
            phoneChanged,
            reason);

        return await store.UpdateCustomerAsync(target, expectedVersion, audit, cancellationToken);
    }
}

public sealed class ListTagsHandler(ICustomerQueries queries) : IQueryHandler<ListTags, IReadOnlyList<CustomerTagView>>
{
    public async Task<Result<IReadOnlyList<CustomerTagView>>> Handle(ListTags query, CancellationToken cancellationToken)
        => Result.Success(await queries.ListTagsAsync(cancellationToken));
}

public sealed class CreateTagHandler(ICustomerStore store) : ICommandHandler<CreateTag, CustomerTagView>
{
    public Task<Result<CustomerTagView>> Handle(CreateTag command, CancellationToken cancellationToken)
        => store.CreateTagAsync(Guid.CreateVersion7(), command.Name.Trim(), command.Color, cancellationToken);
}

public sealed class PatchTagHandler(ICustomerStore store) : ICommandHandler<PatchTag, CustomerTagView>
{
    public Task<Result<CustomerTagView>> Handle(PatchTag command, CancellationToken cancellationToken)
    {
        var hasName = command.Fields.TryGetProperty("name", out var nameProp);
        var hasColor = command.Fields.TryGetProperty("color", out var colorProp);
        var name = hasName ? nameProp.GetString()!.Trim() : null;
        var color = hasColor && colorProp.ValueKind != JsonValueKind.Null ? colorProp.GetString() : null;
        return store.UpdateTagAsync(command.Id, name, hasName, color, hasColor, cancellationToken);
    }
}

public sealed class PutCustomerTagsHandler(ICustomerStore store, IClock clock) : ICommandHandler<PutCustomerTags, CustomerTagsResult>
{
    public async Task<Result<CustomerTagsResult>> Handle(PutCustomerTags command, CancellationToken cancellationToken)
    {
        uint? expectedVersion = null;
        if (command.RowVersion is not null)
        {
            if (!CatalogVersions.TryDecode(command.RowVersion, out var parsed))
            {
                return Result.Failure<CustomerTagsResult>(CustomerErrors.Validation("rowVersion", "invalid_row_version"));
            }

            expectedVersion = parsed;
        }

        var customer = await store.LockCustomerAsync(command.CustomerId, cancellationToken);
        if (CustomerErrors.CheckMutableCustomer(customer, expectedVersion) is { } stateError)
        {
            return Result.Failure<CustomerTagsResult>(stateError);
        }

        var distinctTagIds = command.TagIds.Distinct().ToArray();
        if (distinctTagIds.Length > 0 && !await store.AllTagsExistAsync(distinctTagIds, cancellationToken))
        {
            return Result.Failure<CustomerTagsResult>(CustomerErrors.Validation("tagIds", "tag_not_found"));
        }

        return await store.ReplaceCustomerTagsAsync(command.CustomerId, expectedVersion, distinctTagIds, clock.UtcNow.UtcDateTime, cancellationToken);
    }
}

public sealed class ListCustomerAddressesHandler(ICustomerQueries queries) : IQueryHandler<ListCustomerAddresses, IReadOnlyList<CustomerAddressView>>
{
    public async Task<Result<IReadOnlyList<CustomerAddressView>>> Handle(ListCustomerAddresses query, CancellationToken cancellationToken)
    {
        if (!await queries.ExistsAsync(query.CustomerId, cancellationToken))
        {
            return Result.Failure<IReadOnlyList<CustomerAddressView>>(CustomerErrors.NotFound());
        }

        return Result.Success(await queries.ListAddressesAsync(query.CustomerId, cancellationToken));
    }
}

public sealed class GetCustomerAddressHandler(ICustomerQueries queries) : IQueryHandler<GetCustomerAddress, CustomerAddressView>
{
    public async Task<Result<CustomerAddressView>> Handle(GetCustomerAddress query, CancellationToken cancellationToken)
    {
        var address = await queries.GetAddressAsync(query.CustomerId, query.AddressId, cancellationToken);
        return address is null
            ? Result.Failure<CustomerAddressView>(CustomerErrors.NotFound("Không tìm thấy địa chỉ khách hàng."))
            : Result.Success(address);
    }
}

public sealed class CreateCustomerAddressHandler(ICustomerStore store, IAdministrativeReferenceData adminRef, IClock clock)
    : ICommandHandler<CreateCustomerAddress, CustomerAddressView>
{
    public async Task<Result<CustomerAddressView>> Handle(CreateCustomerAddress command, CancellationToken cancellationToken)
    {
        if (!CustomerRules.TryNormalizePhone(command.ReceiverPhone, out var normalizedPhone) || normalizedPhone is null)
        {
            return Result.Failure<CustomerAddressView>(CustomerErrors.InvalidPhone("receiverPhone"));
        }

        uint? expectedVersion = null;
        if (command.RowVersion is not null)
        {
            if (!CatalogVersions.TryDecode(command.RowVersion, out var parsed))
            {
                return Result.Failure<CustomerAddressView>(CustomerErrors.Validation("rowVersion", "invalid_row_version"));
            }

            expectedVersion = parsed;
        }

        var customer = await store.LockCustomerAsync(command.CustomerId, cancellationToken);
        if (CustomerErrors.CheckMutableCustomer(customer, expectedVersion) is { } stateError)
        {
            return Result.Failure<CustomerAddressView>(stateError);
        }

        if (!await adminRef.IsReadyAsync(cancellationToken))
        {
            return Result.Failure<CustomerAddressView>(CustomerErrors.AdministrativeDataNotReady());
        }

        if (!await adminRef.IsActiveSelectionAsync(command.ProvinceCode, command.WardCode, cancellationToken))
        {
            return Result.Failure<CustomerAddressView>(CustomerErrors.InvalidAddressLocation());
        }

        var now = clock.UtcNow.UtcDateTime;
        var address = new CustomerAddressRecord
        {
            Id = Guid.CreateVersion7(),
            CustomerId = command.CustomerId,
            Label = CreateCustomerHandler.NormalizeOptionalText(command.Label),
            ReceiverName = command.ReceiverName.Trim(),
            ReceiverPhone = normalizedPhone,
            ProvinceCode = command.ProvinceCode,
            WardCode = command.WardCode,
            WardNameText = CreateCustomerHandler.NormalizeOptionalText(command.WardNameText),
            AddressLine = command.AddressLine.Trim(),
            IsDefault = command.IsDefault,
            CreatedAt = now,
            UpdatedAt = now
        };

        return await store.CreateAddressAsync(address, expectedVersion, now, cancellationToken);
    }
}

public sealed class PatchCustomerAddressHandler(ICustomerStore store, IAdministrativeReferenceData adminRef, IClock clock)
    : ICommandHandler<PatchCustomerAddress, CustomerAddressView>
{
    public async Task<Result<CustomerAddressView>> Handle(PatchCustomerAddress command, CancellationToken cancellationToken)
    {
        var fields = command.Fields;
        uint? expectedVersion = null;
        if (fields.TryGetProperty("rowVersion", out var rvProp) && rvProp.ValueKind != JsonValueKind.Null)
        {
            if (!CatalogVersions.TryDecode(rvProp.GetString(), out var parsed))
            {
                return Result.Failure<CustomerAddressView>(CustomerErrors.Validation("rowVersion", "invalid_row_version"));
            }

            expectedVersion = parsed;
        }

        var customer = await store.LockCustomerAsync(command.CustomerId, cancellationToken);
        if (CustomerErrors.CheckMutableCustomer(customer, expectedVersion) is { } stateError)
        {
            return Result.Failure<CustomerAddressView>(stateError);
        }

        var existing = await store.LoadAddressAsync(command.CustomerId, command.AddressId, cancellationToken);
        if (existing is null)
        {
            return Result.Failure<CustomerAddressView>(CustomerErrors.NotFound("Không tìm thấy địa chỉ khách hàng."));
        }

        var oldProvince = existing.ProvinceCode;
        var oldWard = existing.WardCode;

        if (fields.TryGetProperty("label", out var labelProp))
        {
            existing.Label = labelProp.ValueKind == JsonValueKind.Null
                ? null
                : CreateCustomerHandler.NormalizeOptionalText(labelProp.GetString());
        }

        if (fields.TryGetProperty("receiverName", out var nameProp))
        {
            existing.ReceiverName = nameProp.GetString()!.Trim();
        }

        if (fields.TryGetProperty("receiverPhone", out var phoneProp))
        {
            if (!CustomerRules.TryNormalizePhone(phoneProp.GetString(), out var normalizedPhone) || normalizedPhone is null)
            {
                return Result.Failure<CustomerAddressView>(CustomerErrors.InvalidPhone("receiverPhone"));
            }

            existing.ReceiverPhone = normalizedPhone;
        }

        if (fields.TryGetProperty("provinceCode", out var provinceProp))
        {
            existing.ProvinceCode = provinceProp.GetString()!;
        }

        if (fields.TryGetProperty("wardCode", out var wardProp))
        {
            existing.WardCode = wardProp.ValueKind == JsonValueKind.Null ? null : wardProp.GetString();
        }

        if (fields.TryGetProperty("wardNameText", out var wardTextProp))
        {
            existing.WardNameText = wardTextProp.ValueKind == JsonValueKind.Null
                ? null
                : CreateCustomerHandler.NormalizeOptionalText(wardTextProp.GetString());
        }

        if (fields.TryGetProperty("addressLine", out var lineProp))
        {
            existing.AddressLine = lineProp.GetString()!.Trim();
        }

        if (fields.TryGetProperty("isDefault", out var defaultProp))
        {
            existing.IsDefault = defaultProp.GetBoolean();
        }

        if (existing.WardCode is null && string.IsNullOrWhiteSpace(existing.WardNameText))
        {
            return Result.Failure<CustomerAddressView>(CustomerErrors.Validation("wardCode", "ward_required"));
        }

        // 03-database.md §3.6: only revalidate administrative codes when provinceCode or wardCode actually changed.
        var locationChanged = !string.Equals(oldProvince, existing.ProvinceCode, StringComparison.Ordinal)
                              || !string.Equals(oldWard, existing.WardCode, StringComparison.Ordinal);
        if (locationChanged)
        {
            if (!await adminRef.IsReadyAsync(cancellationToken))
            {
                return Result.Failure<CustomerAddressView>(CustomerErrors.AdministrativeDataNotReady());
            }

            if (!await adminRef.IsActiveSelectionAsync(existing.ProvinceCode, existing.WardCode, cancellationToken))
            {
                return Result.Failure<CustomerAddressView>(CustomerErrors.InvalidAddressLocation());
            }
        }

        var now = clock.UtcNow.UtcDateTime;
        existing.UpdatedAt = now;

        return await store.UpdateAddressAsync(existing, expectedVersion, now, cancellationToken);
    }
}

public sealed class DeleteCustomerAddressHandler(ICustomerStore store, IClock clock) : ICommandHandler<DeleteCustomerAddress, bool>
{
    public async Task<Result<bool>> Handle(DeleteCustomerAddress command, CancellationToken cancellationToken)
    {
        uint? expectedVersion = null;
        if (command.RowVersion is not null)
        {
            if (!CatalogVersions.TryDecode(command.RowVersion, out var parsed))
            {
                return Result.Failure<bool>(CustomerErrors.Validation("rowVersion", "invalid_row_version"));
            }

            expectedVersion = parsed;
        }

        var customer = await store.LockCustomerAsync(command.CustomerId, cancellationToken);
        if (CustomerErrors.CheckMutableCustomer(customer, expectedVersion) is { } stateError)
        {
            return Result.Failure<bool>(stateError);
        }

        return await store.DeleteAddressAsync(command.CustomerId, command.AddressId, expectedVersion, clock.UtcNow.UtcDateTime, cancellationToken);
    }
}
