using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeaPremium.Application.Notifications;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Time;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Notifications;

// Single-channel, single-recipient delivery consumer.
// Persists platform.notification_logs via an independent short scope + pg_advisory_xact_lock
// so failed attempts survive OutboxBatchProcessor savepoint rollbacks and post-send crashes never duplicate.
public sealed partial class NotificationDeliveryHandler(
    AppDbContext db,
    IServiceScopeFactory scopeFactory,
    IEnumerable<INotificationChannel> channels,
    NotificationOptions options,
    IClock clock,
    ILogger<NotificationDeliveryHandler> logger) : IOutboxHandler
{
    public string MessageType => NotificationContracts.DeliveryMessageType;

    public async Task HandleAsync(Guid messageId, string payload, CancellationToken cancellationToken)
    {
        if (!NotificationDeliveryPayload.TryParse(payload, out var delivery))
            throw new NotificationDeliveryException("invalid_notification_payload");

        if (!IsChannelConfigured(delivery.Channel))
        {
            LogSkippedUnconfigured(logger, messageId, delivery.Channel, delivery.Template, delivery.RefType, delivery.RefId);
            return;
        }

        var recipient = await ResolveRecipientAsync(delivery.Channel, delivery.RecipientHash, cancellationToken);
        if (recipient is null)
        {
            LogSkippedRecipientDisabled(logger, messageId, delivery.Channel, delivery.Template, delivery.RefType, delivery.RefId);
            return;
        }

        var channel = channels.SingleOrDefault(x => string.Equals(x.Code, delivery.Channel, StringComparison.Ordinal))
            ?? throw new NotificationDeliveryException("notification_channel_unavailable");

        var (alreadySent, logId) = await BeginDeliveryLogAsync(delivery, cancellationToken);
        if (alreadySent)
        {
            LogSkippedAlreadySent(logger, messageId, delivery.Channel, delivery.Template, delivery.RefType, delivery.RefId, logId);
            return;
        }

        var message = new NotificationMessage(
            delivery.Channel,
            delivery.Template,
            recipient,
            delivery.Subject,
            delivery.TextBody,
            delivery.HtmlBody,
            delivery.RefType,
            delivery.RefId);

        try
        {
            await channel.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await MarkFailedAsync(logId, "delivery_cancelled");
            throw;
        }
        catch (NotificationDeliveryException ex)
        {
            await MarkFailedAsync(logId, ex.ErrorCode);
            LogDeliveryFailed(logger, messageId, delivery.Channel, delivery.Template, delivery.RefType, delivery.RefId, logId, ex.ErrorCode);
            throw;
        }
        catch (Exception)
        {
            const string safeCode = "notification_delivery_failed";
            await MarkFailedAsync(logId, safeCode);
            LogDeliveryFailed(logger, messageId, delivery.Channel, delivery.Template, delivery.RefType, delivery.RefId, logId, safeCode);
            throw new NotificationDeliveryException(safeCode);
        }

        await MarkSentAsync(logId);
        LogDeliverySent(logger, messageId, delivery.Channel, delivery.Template, delivery.RefType, delivery.RefId, logId);
    }

    private bool IsChannelConfigured(string channel) => channel switch
    {
        NotificationChannels.Telegram => options.IsTelegramConfigured,
        NotificationChannels.Email => options.IsSmtpConfigured,
        _ => false
    };

    private async Task<string?> ResolveRecipientAsync(string channel, string recipientHash, CancellationToken ct)
    {
        if (channel == NotificationChannels.Telegram)
        {
            var telegram = await NotificationSettingsReader.ReadTelegramAsync(db, ct);
            if (!telegram.IsEnabled) return null;
            return string.Equals(NotificationContracts.HashRecipient(telegram.ChatId!), recipientHash, StringComparison.Ordinal)
                ? telegram.ChatId
                : null;
        }

        if (channel == NotificationChannels.Email)
        {
            var emails = await NotificationSettingsReader.ReadShopEmailsAsync(db, ct);
            foreach (var email in emails.Emails)
            {
                if (string.Equals(NotificationContracts.HashRecipient(NotificationContracts.NormalizeEmail(email)), recipientHash, StringComparison.Ordinal))
                    return email;
            }
        }

        return null;
    }

    private async Task<(bool AlreadySent, Guid LogId)> BeginDeliveryLogAsync(NotificationDeliveryPayload delivery, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var logDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await logDb.Database.BeginTransactionAsync(ct);

        var lockKey = ComputeAdvisoryLockKey(delivery);
        await logDb.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);

        var existing = await logDb.NotificationLogs
            .Where(x => x.RefType == delivery.RefType
                     && x.RefId == delivery.RefId
                     && x.Channel == delivery.Channel
                     && x.Template == delivery.Template
                     && x.RecipientHash == delivery.RecipientHash)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is { Status: "sent" })
        {
            await tx.CommitAsync(ct);
            return (true, existing.Id);
        }

        if (existing is null)
        {
            existing = new NotificationLog
            {
                Id = Guid.CreateVersion7(),
                Channel = delivery.Channel,
                Template = delivery.Template,
                RecipientHash = delivery.RecipientHash,
                RefType = delivery.RefType,
                RefId = delivery.RefId,
                Status = "queued",
                Error = null,
                CreatedAt = clock.UtcNow.UtcDateTime,
                SentAt = null
            };
            logDb.NotificationLogs.Add(existing);
        }
        else
        {
            existing.Status = "queued";
            existing.Error = null;
        }

        await logDb.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (false, existing.Id);
    }

    private async Task MarkSentAsync(Guid logId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var logDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await logDb.NotificationLogs.SingleOrDefaultAsync(x => x.Id == logId, CancellationToken.None);
        if (row is null) return;
        row.Status = "sent";
        row.SentAt = clock.UtcNow.UtcDateTime;
        row.Error = null;
        await logDb.SaveChangesAsync(CancellationToken.None);
    }

    private async Task MarkFailedAsync(Guid logId, string safeErrorCode)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var logDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await logDb.NotificationLogs.SingleOrDefaultAsync(x => x.Id == logId, CancellationToken.None);
        if (row is null) return;
        row.Status = "failed";
        row.Error = safeErrorCode;
        await logDb.SaveChangesAsync(CancellationToken.None);
    }

    internal static long ComputeAdvisoryLockKey(NotificationDeliveryPayload delivery)
    {
        var input = $"TeaPremium.Notification:{delivery.Channel}:{delivery.Template}:{delivery.RefType}:{delivery.RefId:D}:{delivery.RecipientHash}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return BinaryPrimitives.ReadInt64BigEndian(bytes);
    }

    [LoggerMessage(EventId = 1, EventName = "NotificationDeliverySent", Level = LogLevel.Information,
        Message = "Notification sent: messageId={MessageId}, channel={Channel}, template={Template}, refType={RefType}, refId={RefId}, logId={LogId}")]
    private static partial void LogDeliverySent(ILogger logger, Guid messageId, string channel, string template, string refType, Guid refId, Guid logId);

    [LoggerMessage(EventId = 2, EventName = "NotificationDeliveryFailed", Level = LogLevel.Warning,
        Message = "Notification failed: messageId={MessageId}, channel={Channel}, template={Template}, refType={RefType}, refId={RefId}, logId={LogId}, errorCode={ErrorCode}")]
    private static partial void LogDeliveryFailed(ILogger logger, Guid messageId, string channel, string template, string refType, Guid refId, Guid logId, string errorCode);

    [LoggerMessage(EventId = 3, EventName = "NotificationDeliverySkippedAlreadySent", Level = LogLevel.Information,
        Message = "Notification already sent; skipping duplicate delivery: messageId={MessageId}, channel={Channel}, template={Template}, refType={RefType}, refId={RefId}, logId={LogId}")]
    private static partial void LogSkippedAlreadySent(ILogger logger, Guid messageId, string channel, string template, string refType, Guid refId, Guid logId);

    [LoggerMessage(EventId = 4, EventName = "NotificationDeliverySkippedUnconfigured", Level = LogLevel.Information,
        Message = "Notification channel not configured; completing as no-op: messageId={MessageId}, channel={Channel}, template={Template}, refType={RefType}, refId={RefId}")]
    private static partial void LogSkippedUnconfigured(ILogger logger, Guid messageId, string channel, string template, string refType, Guid refId);

    [LoggerMessage(EventId = 5, EventName = "NotificationDeliverySkippedRecipientDisabled", Level = LogLevel.Information,
        Message = "Notification recipient disabled or changed; completing as no-op: messageId={MessageId}, channel={Channel}, template={Template}, refType={RefType}, refId={RefId}")]
    private static partial void LogSkippedRecipientDisabled(ILogger logger, Guid messageId, string channel, string template, string refType, Guid refId);
}

// GAP-03 3A hooks for immediate Telegram alerts on active reconciliation mismatch and non-NotificationDelivery dead outbox.
public interface IOutboxDeadNotifier
{
    Task TryEnqueueAsync(AppDbContext db, OutboxMessage deadMessage, DateTime nowUtc, CancellationToken cancellationToken);
}

public interface IInventoryMismatchNotifier
{
    Task TryEnqueueAsync(AppDbContext db, Guid sourceMessageId, InventoryReconciliationAlertModel alert, CancellationToken cancellationToken);
}

public sealed class TechnicalAlertNotifier(NotificationOptions options, IClock clock)
    : IOutboxDeadNotifier, IInventoryMismatchNotifier
{
    public async Task TryEnqueueAsync(AppDbContext db, OutboxMessage deadMessage, DateTime nowUtc, CancellationToken cancellationToken)
    {
        // Never enqueue a Telegram alert when NotificationDelivery itself turns dead (prevents infinite loop).
        if (string.Equals(deadMessage.Type, NotificationContracts.DeliveryMessageType, StringComparison.Ordinal))
            return;
        if (!options.IsTelegramConfigured)
            return;

        var telegram = await NotificationSettingsReader.ReadTelegramAsync(db, cancellationToken);
        if (!telegram.IsEnabled || !NotificationContracts.ShouldDeliverForWatermark(deadMessage.OccurredAt, telegram.UpdatedAtUtc))
            return;

        var recipientHash = NotificationContracts.HashRecipient(telegram.ChatId!);
        var alreadySent = await db.NotificationLogs.AsNoTracking()
            .AnyAsync(x => x.RefType == NotificationRefTypes.OutboxDead
                        && x.RefId == deadMessage.Id
                        && x.Channel == NotificationChannels.Telegram
                        && x.Template == NotificationTemplateNames.OutboxDeadShop
                        && x.RecipientHash == recipientHash
                        && x.Status == "sent", cancellationToken);
        if (alreadySent) return;

        var html = NotificationTemplates.RenderOutboxDeadShop(
            new OutboxDeadAlertModel(deadMessage.Id, deadMessage.Type, deadMessage.Attempts, deadMessage.LastError ?? "handler_failed"),
            options.AdminBaseUrl);

        var delivery = new NotificationDeliveryPayload(
            NotificationChannels.Telegram,
            NotificationTemplateNames.OutboxDeadShop,
            NotificationRefTypes.OutboxDead,
            deadMessage.Id,
            recipientHash,
            Subject: null,
            TextBody: html,
            HtmlBody: html);

        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = NotificationContracts.DeliveryMessageType,
            Payload = NotificationDeliveryPayload.Serialize(delivery),
            OccurredAt = nowUtc,
            AvailableAt = nowUtc,
            Attempts = 0,
            Status = "pending"
        });
    }

    public async Task TryEnqueueAsync(AppDbContext db, Guid sourceMessageId, InventoryReconciliationAlertModel alert, CancellationToken cancellationToken)
    {
        if (!options.IsTelegramConfigured)
            return;

        var now = clock.UtcNow;
        var todayVn = BusinessCalendar.ToVietnamDate(now);
        if (alert.VietnamDate != todayVn)
            return;

        var sourceOccurredAt = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.Id == sourceMessageId)
            .Select(x => (DateTime?)x.OccurredAt)
            .SingleOrDefaultAsync(cancellationToken);
        if (sourceOccurredAt is null)
            return;

        var telegram = await NotificationSettingsReader.ReadTelegramAsync(db, cancellationToken);
        if (!telegram.IsEnabled || !NotificationContracts.ShouldDeliverForWatermark(sourceOccurredAt.Value, telegram.UpdatedAtUtc))
            return;

        var refId = NotificationContracts.InventoryReconciliationRefId(alert.VariantId, alert.VietnamDate);
        var recipientHash = NotificationContracts.HashRecipient(telegram.ChatId!);

        var alreadySent = await db.NotificationLogs.AsNoTracking()
            .AnyAsync(x => x.RefType == NotificationRefTypes.InventoryReconciliation
                        && x.RefId == refId
                        && x.Channel == NotificationChannels.Telegram
                        && x.Template == NotificationTemplateNames.InventoryReconciliationShop
                        && x.RecipientHash == recipientHash
                        && x.Status == "sent", cancellationToken);
        if (alreadySent) return;

        var dayStartUtc = BusinessCalendar.ToUtc(todayVn, TimeOnly.MinValue).UtcDateTime;
        var nextDayStartUtc = BusinessCalendar.ToUtc(todayVn.AddDays(1), TimeOnly.MinValue).UtcDateTime;
        var todaysPending = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.Type == NotificationContracts.DeliveryMessageType
                     && x.Status == "pending"
                     && x.OccurredAt >= dayStartUtc
                     && x.OccurredAt < nextDayStartUtc)
            .Select(x => x.Payload)
            .ToListAsync(cancellationToken);

        if (todaysPending.Any(p => NotificationDeliveryPayload.TryParse(p, out var existing)
                && existing.Channel == NotificationChannels.Telegram
                && existing.Template == NotificationTemplateNames.InventoryReconciliationShop
                && existing.RefType == NotificationRefTypes.InventoryReconciliation
                && existing.RefId == refId
                && existing.RecipientHash == recipientHash))
            return;

        var html = NotificationTemplates.RenderInventoryReconciliationShop(alert, options.AdminBaseUrl);
        var delivery = new NotificationDeliveryPayload(
            NotificationChannels.Telegram,
            NotificationTemplateNames.InventoryReconciliationShop,
            NotificationRefTypes.InventoryReconciliation,
            refId,
            recipientHash,
            Subject: null,
            TextBody: html,
            HtmlBody: html);

        var nowUtc = now.UtcDateTime;
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = NotificationContracts.DeliveryMessageType,
            Payload = NotificationDeliveryPayload.Serialize(delivery),
            OccurredAt = nowUtc,
            AvailableAt = nowUtc,
            Attempts = 0,
            Status = "pending"
        });
    }
}
