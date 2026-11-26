using System.Text.Json;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Crm;

namespace TeaPremium.Application.Crm;

internal static class InteractionErrors
{
    public static Error NotFound()
        => new("not_found", "Không tìm thấy tương tác khách hàng.", ErrorType.NotFound);

    public static Error CampaignInteractionImmutable()
        => new("campaign_interaction_immutable", "Không thể chỉnh sửa tương tác chiến dịch được hệ thống tạo tự động.", ErrorType.Conflict);

    public static Error FollowUpNotScheduled()
        => new("follow_up_not_scheduled", "Tương tác này chưa có lịch hẹn chăm sóc.", ErrorType.Conflict);

    public static Error FollowUpAlreadyCompleted()
        => new("follow_up_already_completed", "Lịch hẹn chăm sóc đã được hoàn thành.", ErrorType.Conflict);

    public static Error OrderNotFound()
        => CustomerErrors.Validation("orderId", "order_not_found", "Không tìm thấy đơn hàng.");

    public static Error OrderCustomerMismatch()
        => CustomerErrors.Validation("orderId", "order_customer_mismatch", "Đơn hàng không thuộc khách hàng này.");
}

public sealed class ListCustomerInteractionsHandler(ICustomerQueries customers, IInteractionQueries interactions)
    : IQueryHandler<ListCustomerInteractions, CatalogPage<CustomerInteractionView>>
{
    public async Task<Result<CatalogPage<CustomerInteractionView>>> Handle(ListCustomerInteractions query, CancellationToken cancellationToken)
    {
        if (!await customers.ExistsAsync(query.CustomerId, cancellationToken))
        {
            return Result.Failure<CatalogPage<CustomerInteractionView>>(CustomerErrors.NotFound());
        }

        return Result.Success(await interactions.ListByCustomerAsync(query.CustomerId, query.Page, query.PageSize, cancellationToken));
    }
}

public sealed class CreateCustomerInteractionHandler(
    IInteractionStore store,
    ICustomerOrderQueries orderQueries,
    IClock clock) : ICommandHandler<CreateCustomerInteraction, CustomerInteractionView>
{
    public async Task<Result<CustomerInteractionView>> Handle(CreateCustomerInteraction command, CancellationToken cancellationToken)
    {
        var rawNowUtc = clock.UtcNow.UtcDateTime;

        DateTime rawOccurredAtUtc;
        if (command.OccurredAt is null)
        {
            rawOccurredAtUtc = rawNowUtc;
        }
        else if (!InteractionTimestampParser.TryParseUtc(command.OccurredAt, out rawOccurredAtUtc))
        {
            return Result.Failure<CustomerInteractionView>(CustomerErrors.Validation("occurredAt", "invalid_occurred_at"));
        }

        // Strict BR-CRM-06 check at full tick precision BEFORE microsecond truncation (zero positive clock skew).
        if (!InteractionRules.IsOccurredAtValid(rawOccurredAtUtc, rawNowUtc))
        {
            return Result.Failure<CustomerInteractionView>(CustomerErrors.Validation("occurredAt", "occurred_at_in_future"));
        }

        var nowUtc = InteractionTimestampParser.TruncateToMicroseconds(rawNowUtc);
        var occurredAtUtc = InteractionTimestampParser.TruncateToMicroseconds(rawOccurredAtUtc);

        DateTime? followUpAtUtc = null;
        if (command.FollowUpAt is not null)
        {
            if (!InteractionTimestampParser.TryParseUtc(command.FollowUpAt, out var parsedFollowUp))
            {
                return Result.Failure<CustomerInteractionView>(CustomerErrors.Validation("followUpAt", "invalid_follow_up_at"));
            }

            followUpAtUtc = InteractionTimestampParser.TruncateToMicroseconds(parsedFollowUp);
        }

        if (!InteractionRules.IsFollowUpAfterOccurred(occurredAtUtc, followUpAtUtc))
        {
            return Result.Failure<CustomerInteractionView>(
                CustomerErrors.Validation("followUpAt", "follow_up_before_or_equal_occurred_at"));
        }

        var customer = await store.LockCustomerAsync(command.CustomerId, cancellationToken);
        if (CustomerErrors.CheckMutableCustomer(customer, expectedVersion: null) is { } stateError)
        {
            return Result.Failure<CustomerInteractionView>(stateError);
        }

        if (command.OrderId is { } orderId)
        {
            var orderRef = await orderQueries.FindOrderReferenceAsync(orderId, cancellationToken);
            if (orderRef is null)
            {
                return Result.Failure<CustomerInteractionView>(InteractionErrors.OrderNotFound());
            }

            if (orderRef.CustomerId != command.CustomerId)
            {
                return Result.Failure<CustomerInteractionView>(InteractionErrors.OrderCustomerMismatch());
            }
        }

        var record = new InteractionRecord
        {
            Id = Guid.CreateVersion7(),
            CustomerId = command.CustomerId,
            StaffId = command.Actor.Id,
            Channel = command.Channel,
            Kind = command.Kind,
            Content = command.Content.Trim(),
            OccurredAt = occurredAtUtc,
            OrderId = command.OrderId,
            CampaignId = null,
            FollowUpAt = followUpAtUtc,
            FollowUpDoneAt = null,
            FollowUpResult = null,
            CreatedAt = nowUtc
        };

        return await store.CreateAsync(record, nowUtc, cancellationToken);
    }
}

public sealed class PatchCustomerInteractionHandler(
    IInteractionStore store,
    ICustomerOrderQueries orderQueries,
    IClock clock) : ICommandHandler<PatchCustomerInteraction, CustomerInteractionView>
{
    public async Task<Result<CustomerInteractionView>> Handle(PatchCustomerInteraction command, CancellationToken cancellationToken)
    {
        var rawNowUtc = clock.UtcNow.UtcDateTime;
        var nowUtc = InteractionTimestampParser.TruncateToMicroseconds(rawNowUtc);
        var fields = command.Fields;

        // Lock parent customer FIRST (strict lock order: crm.customers -> crm.interactions).
        var customer = await store.LockCustomerAsync(command.CustomerId, cancellationToken);
        if (CustomerErrors.CheckMutableCustomer(customer, expectedVersion: null) is { } stateError)
        {
            return Result.Failure<CustomerInteractionView>(stateError);
        }

        var existing = await store.LockInteractionAsync(command.CustomerId, command.InteractionId, cancellationToken);
        if (existing is null)
        {
            return Result.Failure<CustomerInteractionView>(InteractionErrors.NotFound());
        }

        // GAP-P204-01 = A (OWNER_APPROVED 2026-11-26): zero PATCH bypass on existing campaign_message rows.
        if (string.Equals(existing.Kind, InteractionRules.CampaignMessageKind, StringComparison.Ordinal))
        {
            return Result.Failure<CustomerInteractionView>(InteractionErrors.CampaignInteractionImmutable());
        }

        var hasFollowUpAt = fields.TryGetProperty("followUpAt", out var followUpProp);
        if (existing.FollowUpDoneAt is not null && hasFollowUpAt)
        {
            return Result.Failure<CustomerInteractionView>(InteractionErrors.FollowUpAlreadyCompleted());
        }

        var oldKind = existing.Kind;
        var oldOccurredAt = existing.OccurredAt;

        if (fields.TryGetProperty("channel", out var channelProp))
        {
            existing.Channel = channelProp.GetString()!;
        }

        if (fields.TryGetProperty("kind", out var kindProp))
        {
            existing.Kind = kindProp.GetString()!;
        }

        if (fields.TryGetProperty("content", out var contentProp))
        {
            existing.Content = contentProp.GetString()!.Trim();
        }

        if (fields.TryGetProperty("occurredAt", out var occurredProp))
        {
            if (!InteractionTimestampParser.TryParseUtc(occurredProp.GetString(), out var parsedOccurred))
            {
                return Result.Failure<CustomerInteractionView>(CustomerErrors.Validation("occurredAt", "invalid_occurred_at"));
            }

            if (!InteractionRules.IsOccurredAtValid(parsedOccurred, rawNowUtc))
            {
                return Result.Failure<CustomerInteractionView>(CustomerErrors.Validation("occurredAt", "occurred_at_in_future"));
            }

            existing.OccurredAt = InteractionTimestampParser.TruncateToMicroseconds(parsedOccurred);
        }

        if (hasFollowUpAt)
        {
            if (followUpProp.ValueKind == JsonValueKind.Null)
            {
                existing.FollowUpAt = null;
            }
            else
            {
                if (!InteractionTimestampParser.TryParseUtc(followUpProp.GetString(), out var parsedFollowUp))
                {
                    return Result.Failure<CustomerInteractionView>(CustomerErrors.Validation("followUpAt", "invalid_follow_up_at"));
                }

                existing.FollowUpAt = InteractionTimestampParser.TruncateToMicroseconds(parsedFollowUp);
            }
        }

        if (!InteractionRules.IsFollowUpAfterOccurred(existing.OccurredAt, existing.FollowUpAt))
        {
            return Result.Failure<CustomerInteractionView>(
                CustomerErrors.Validation("followUpAt", "follow_up_before_or_equal_occurred_at"));
        }

        if (fields.TryGetProperty("orderId", out var orderProp))
        {
            if (orderProp.ValueKind == JsonValueKind.Null)
            {
                existing.OrderId = null;
            }
            else
            {
                var orderId = Guid.ParseExact(orderProp.GetString()!, "D");
                var orderRef = await orderQueries.FindOrderReferenceAsync(orderId, cancellationToken);
                if (orderRef is null)
                {
                    return Result.Failure<CustomerInteractionView>(InteractionErrors.OrderNotFound());
                }

                if (orderRef.CustomerId != command.CustomerId)
                {
                    return Result.Failure<CustomerInteractionView>(InteractionErrors.OrderCustomerMismatch());
                }

                existing.OrderId = orderId;
            }
        }

        var recomputeLastContacted = !string.Equals(oldKind, existing.Kind, StringComparison.Ordinal)
                                     || oldOccurredAt != existing.OccurredAt;

        return await store.UpdateAsync(existing, recomputeLastContacted, nowUtc, cancellationToken);
    }
}

public sealed class CompleteInteractionFollowUpHandler(IInteractionStore store, IClock clock)
    : ICommandHandler<CompleteInteractionFollowUp, CustomerInteractionView>
{
    public async Task<Result<CustomerInteractionView>> Handle(CompleteInteractionFollowUp command, CancellationToken cancellationToken)
    {
        // Lock parent customer FIRST (strict lock order: crm.customers -> crm.interactions).
        var customer = await store.LockCustomerAsync(command.CustomerId, cancellationToken);
        if (CustomerErrors.CheckMutableCustomer(customer, expectedVersion: null) is { } stateError)
        {
            return Result.Failure<CustomerInteractionView>(stateError);
        }

        var existing = await store.LockInteractionAsync(command.CustomerId, command.InteractionId, cancellationToken);
        if (existing is null)
        {
            return Result.Failure<CustomerInteractionView>(InteractionErrors.NotFound());
        }

        if (existing.FollowUpAt is null)
        {
            return Result.Failure<CustomerInteractionView>(InteractionErrors.FollowUpNotScheduled());
        }

        if (existing.FollowUpDoneAt is not null)
        {
            return Result.Failure<CustomerInteractionView>(InteractionErrors.FollowUpAlreadyCompleted());
        }

        return await store.CompleteFollowUpAsync(
            command.CustomerId,
            command.InteractionId,
            InteractionTimestampParser.TruncateToMicroseconds(clock.UtcNow.UtcDateTime),
            command.Result.Trim(),
            cancellationToken);
    }
}

public sealed class ListFollowUpsHandler(IInteractionQueries queries, IClock clock)
    : IQueryHandler<ListFollowUps, CatalogPage<FollowUpItemView>>
{
    public async Task<Result<CatalogPage<FollowUpItemView>>> Handle(ListFollowUps query, CancellationToken cancellationToken)
    {
        var window = FollowUpWindowCalculator.Compute(query.Due, clock.UtcNow);
        return Result.Success(await queries.ListFollowUpsAsync(window, query.Page, query.PageSize, cancellationToken));
    }
}
