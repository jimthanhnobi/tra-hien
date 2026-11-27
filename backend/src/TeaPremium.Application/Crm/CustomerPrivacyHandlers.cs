using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Application.Pricing.Contracts;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Crm;

namespace TeaPremium.Application.Crm;

internal static class CustomerPrivacyErrors
{
    public static Error CustomerMergedWithTarget(Guid mergedIntoCustomerId)
        => new("customer_merged", "Hồ sơ khách hàng đã được gộp sang hồ sơ khác.", ErrorType.Conflict,
            Data: new Dictionary<string, object?>(StringComparer.Ordinal) { ["mergedIntoCustomerId"] = mergedIntoCustomerId });

    public static Error DualAccountMergeConflict()
        => new("account_exists", "Cả hai hồ sơ khách hàng đều đã có tài khoản web, không thể gộp.", ErrorType.Conflict);

    public static Error EraseBlocked(IReadOnlyList<string> blockingReasons)
        => new("customer_erase_blocked", "Không thể xóa dữ liệu khi khách hàng còn giao dịch đang xử lý.", ErrorType.Conflict,
            Data: new Dictionary<string, object?>(StringComparer.Ordinal) { ["blockingReasons"] = blockingReasons });
}

public sealed class PutCustomerConsentHandler(ICustomerPrivacyStore store, IClock clock)
    : ICommandHandler<PutCustomerConsent, CustomerConsentsResult>
{
    public async Task<Result<CustomerConsentsResult>> Handle(PutCustomerConsent command, CancellationToken cancellationToken)
    {
        uint? expectedVersion = null;
        if (command.RowVersion is not null)
        {
            if (!CatalogVersions.TryDecode(command.RowVersion, out var parsed))
            {
                return Result.Failure<CustomerConsentsResult>(CustomerErrors.Validation("rowVersion", "invalid_row_version"));
            }

            expectedVersion = parsed;
        }

        var customer = await store.LockCustomerForConsentAsync(command.CustomerId, cancellationToken);
        if (customer is null)
        {
            return Result.Failure<CustomerConsentsResult>(CustomerErrors.NotFound());
        }

        if (string.Equals(customer.ContactStatus, "erased", StringComparison.Ordinal))
        {
            return Result.Failure<CustomerConsentsResult>(CustomerErrors.CustomerErased());
        }

        if (customer.MergedIntoCustomerId is { } mergedIntoId)
        {
            return Result.Failure<CustomerConsentsResult>(CustomerPrivacyErrors.CustomerMergedWithTarget(mergedIntoId));
        }

        if (expectedVersion is { } expected && customer.Version != expected)
        {
            return Result.Failure<CustomerConsentsResult>(CustomerErrors.ConcurrencyConflict());
        }

        var nowUtc = InteractionTimestampParser.TruncateToMicroseconds(clock.UtcNow.UtcDateTime);
        return await store.UpsertConsentAsync(
            command.CustomerId,
            command.Channel,
            command.Purpose,
            command.Status,
            command.Evidence.Trim(),
            expectedVersion,
            command.Actor.Id,
            nowUtc,
            cancellationToken);
    }
}

public sealed class MergeCustomersHandler(
    ICustomerPrivacyStore store,
    IOrderCustomerPrivacyPort ordersPort,
    IPricingCustomerMergePort pricingPort,
    IClock clock) : ICommandHandler<MergeCustomers, CustomerDetailView>
{
    public async Task<Result<CustomerDetailView>> Handle(MergeCustomers command, CancellationToken cancellationToken)
    {
        // R-P201-05 defense-in-depth guard before acquiring any database lock.
        if (command.TargetCustomerId == command.MergeCustomerId)
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.Validation("mergeCustomerId", "self_merge_not_allowed"));
        }

        uint? expectedTargetVersion = null;
        if (command.RowVersion is not null)
        {
            if (!CatalogVersions.TryDecode(command.RowVersion, out var parsed))
            {
                return Result.Failure<CustomerDetailView>(CustomerErrors.Validation("rowVersion", "invalid_row_version"));
            }

            expectedTargetVersion = parsed;
        }

        uint? expectedSourceVersion = null;
        if (command.MergeRowVersion is not null)
        {
            if (!CatalogVersions.TryDecode(command.MergeRowVersion, out var parsed))
            {
                return Result.Failure<CustomerDetailView>(CustomerErrors.Validation("mergeRowVersion", "invalid_merge_row_version"));
            }

            expectedSourceVersion = parsed;
        }

        await store.AcquirePrivacyLockAsync(cancellationToken);

        var pair = await store.LockPairForMergeAsync(command.TargetCustomerId, command.MergeCustomerId, cancellationToken);
        if (pair.Target is null || pair.Source is null)
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.NotFound());
        }

        if (string.Equals(pair.Target.ContactStatus, "erased", StringComparison.Ordinal)
            || string.Equals(pair.Source.ContactStatus, "erased", StringComparison.Ordinal))
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.CustomerErased());
        }

        if (pair.Target.MergedIntoCustomerId is { } targetMergedInto)
        {
            return Result.Failure<CustomerDetailView>(CustomerPrivacyErrors.CustomerMergedWithTarget(targetMergedInto));
        }

        if (pair.Source.MergedIntoCustomerId is { } sourceMergedInto)
        {
            return Result.Failure<CustomerDetailView>(CustomerPrivacyErrors.CustomerMergedWithTarget(sourceMergedInto));
        }

        if ((expectedTargetVersion is { } evTarget && pair.Target.Version != evTarget)
            || (expectedSourceVersion is { } evSource && pair.Source.Version != evSource))
        {
            return Result.Failure<CustomerDetailView>(CustomerErrors.ConcurrencyConflict());
        }

        if (pair.TargetHasAccount && pair.SourceHasAccount)
        {
            return Result.Failure<CustomerDetailView>(CustomerPrivacyErrors.DualAccountMergeConflict());
        }

        var nowUtc = InteractionTimestampParser.TruncateToMicroseconds(clock.UtcNow.UtcDateTime);
        var targetHadOrders = await ordersPort.HasOrdersAsync(command.TargetCustomerId, cancellationToken);

        // Lock Hierarchy DAG: crm.customers -> crm.customer_accounts -> sales.orders -> pricing.vouchers.
        var movedOrders = await ordersPort.ReassignCustomerOrdersAsync(
            command.MergeCustomerId, command.TargetCustomerId, nowUtc, cancellationToken);
        var (movedVouchers, movedRedemptions) = await pricingPort.ReassignCustomerPricingAsync(
            command.MergeCustomerId, command.TargetCustomerId, cancellationToken);
        var deliveredStats = await ordersPort.ComputeDeliveredOrderStatsAsync(
            command.TargetCustomerId, cancellationToken);

        return await store.CompleteMergeAsync(
            new CompleteMergeInput(
                command.TargetCustomerId,
                command.MergeCustomerId,
                expectedTargetVersion,
                expectedSourceVersion,
                targetHadOrders,
                movedOrders,
                movedVouchers,
                movedRedemptions,
                deliveredStats,
                nowUtc),
            cancellationToken);
    }
}

public sealed class EraseCustomerHandler(
    ICustomerPrivacyStore store,
    IOrderCustomerPrivacyPort ordersPort,
    IClock clock) : ICommandHandler<EraseCustomer, CustomerEraseResult>
{
    public async Task<Result<CustomerEraseResult>> Handle(EraseCustomer command, CancellationToken cancellationToken)
    {
        uint? expectedVersion = null;
        if (command.RowVersion is not null)
        {
            if (!CatalogVersions.TryDecode(command.RowVersion, out var parsed))
            {
                return Result.Failure<CustomerEraseResult>(CustomerErrors.Validation("rowVersion", "invalid_row_version"));
            }

            expectedVersion = parsed;
        }

        await store.AcquirePrivacyLockAsync(cancellationToken);

        var locked = await store.LockForEraseAsync(command.CustomerId, cancellationToken);
        if (locked.Customer is null)
        {
            return Result.Failure<CustomerEraseResult>(CustomerErrors.NotFound());
        }

        if (locked.Customer.MergedIntoCustomerId is { } mergedIntoId)
        {
            return Result.Failure<CustomerEraseResult>(CustomerPrivacyErrors.CustomerMergedWithTarget(mergedIntoId));
        }

        if (string.Equals(locked.Customer.ContactStatus, "erased", StringComparison.Ordinal))
        {
            return Result.Failure<CustomerEraseResult>(CustomerErrors.CustomerErased());
        }

        if (expectedVersion is { } expected && locked.Customer.Version != expected)
        {
            return Result.Failure<CustomerEraseResult>(CustomerErrors.ConcurrencyConflict());
        }

        var blockingReasons = await ordersPort.CheckEraseBlockersAsync(locked.AllCustomerIds, cancellationToken);
        if (blockingReasons.Count > 0)
        {
            return Result.Failure<CustomerEraseResult>(CustomerPrivacyErrors.EraseBlocked(blockingReasons));
        }

        var nowUtc = InteractionTimestampParser.TruncateToMicroseconds(clock.UtcNow.UtcDateTime);
        if (locked.AccountIds.Count > 0)
        {
            await ordersPort.DeleteAccountCartsAsync(locked.AccountIds, cancellationToken);
        }

        var anonymizedOrdersCount = await ordersPort.AnonymizeCustomerOrdersAsync(
            locked.AllCustomerIds,
            CustomerRules.AnonymizedText,
            CustomerRules.AnonymizedOrderPhone,
            nowUtc,
            cancellationToken);

        return await store.CompleteEraseAsync(
            new CompleteEraseInput(
                command.CustomerId,
                command.Reason.Trim(),
                command.RequestChannel,
                expectedVersion,
                command.Actor.Id,
                anonymizedOrdersCount,
                nowUtc),
            cancellationToken);
    }
}

public sealed class ExportCustomerDataHandler(
    ICustomerPrivacyStore store,
    IOrderCustomerPrivacyPort ordersPort,
    IClock clock) : ICommandHandler<ExportCustomerData, CustomerDataExportView>
{
    public async Task<Result<CustomerDataExportView>> Handle(ExportCustomerData command, CancellationToken cancellationToken)
    {
        var snapshot = await store.LoadForExportAsync(command.CustomerId, cancellationToken);
        if (snapshot is null)
        {
            return Result.Failure<CustomerDataExportView>(CustomerErrors.NotFound());
        }

        var orders = await ordersPort.ListOrdersForExportAsync(command.CustomerId, cancellationToken);
        await store.RecordExportAccessAuditAsync(command.CustomerId, cancellationToken);

        var exportedAt = InteractionTimestampParser.TruncateToMicroseconds(clock.UtcNow.UtcDateTime);
        return Result.Success(new CustomerDataExportView(
            exportedAt,
            snapshot.Customer,
            snapshot.Tags,
            snapshot.Addresses,
            snapshot.Account,
            snapshot.Consents,
            snapshot.Interests,
            snapshot.Interactions,
            snapshot.DataRequests,
            orders));
    }
}
