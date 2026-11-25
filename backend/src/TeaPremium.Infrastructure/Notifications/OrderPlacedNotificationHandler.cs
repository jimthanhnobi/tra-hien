using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TeaPremium.Application.Notifications;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Time;
using TeaPremium.Domain.Orders;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Notifications;

// BR-PLT-03 / FR-CRT-06 / BR-ORD-09 / G22: registered as an IOrderOutboxStep under P2-05's
// CompositeOrderOutboxHandler so exactly one IOutboxHandler exists per Order* message type.
// Reads only non-PII, non-cost order fields and fans out per-channel NotificationDelivery rows.
public sealed partial class OrderPlacedNotificationHandler(
    AppDbContext db,
    NotificationOptions options,
    IClock clock,
    ILogger<OrderPlacedNotificationHandler> logger) : IOrderOutboxStep
{
    public string MessageType => OrderEventTypes.OrderPlaced;
    public int Order => 100;

    public async Task HandleAsync(Guid messageId, OrderOutboxPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var orderRow = await db.Set<Persistence.Order>().AsNoTracking()
            .Where(x => x.Id == payload.OrderId)
            .Select(x => new
            {
                x.Id,
                x.Code,
                x.TotalVnd,
                x.PaymentMethod,
                x.IsBackfilled,
                ItemCount = db.Set<OrderLine>().Where(l => l.OrderId == x.Id).Sum(l => (int?)l.Quantity) ?? 0
            })
            .SingleOrDefaultAsync(ct);

        if (orderRow is null)
            return;

        // BR-ORD-09: backfilled historical orders never trigger notifications.
        if (orderRow.IsBackfilled)
        {
            LogSkippedBackfilled(logger, messageId, orderRow.Id);
            return;
        }

        if (orderRow.ItemCount <= 0)
            return;

        var order = new OrderPlacedNotification(
            orderRow.Id,
            orderRow.Code,
            orderRow.TotalVnd,
            orderRow.PaymentMethod,
            orderRow.ItemCount,
            orderRow.IsBackfilled);

        var nowUtc = clock.UtcNow.UtcDateTime;
        var occurredAtUtc = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.Id == messageId)
            .Select(x => (DateTime?)x.OccurredAt)
            .SingleOrDefaultAsync(ct) ?? payload.OccurredAt.UtcDateTime;

        var (telegram, shopEmails) = await NotificationSettingsReader.ReadNotifyGroupAsync(db, ct);
        var rendered = NotificationTemplates.RenderOrderPlacedShop(order, options.AdminBaseUrl);
        var enqueued = 0;

        if (options.IsTelegramConfigured
            && telegram.IsEnabled
            && NotificationContracts.ShouldDeliverForWatermark(occurredAtUtc, telegram.UpdatedAtUtc))
        {
            var recipientHash = NotificationContracts.HashRecipient(telegram.ChatId!);
            if (!await AlreadySentAsync(NotificationChannels.Telegram, order.OrderId, recipientHash, ct))
            {
                EnqueueDelivery(new NotificationDeliveryPayload(
                    NotificationChannels.Telegram,
                    NotificationTemplateNames.OrderPlacedShop,
                    NotificationRefTypes.Order,
                    order.OrderId,
                    recipientHash,
                    Subject: null,
                    TextBody: rendered.TelegramHtml,
                    HtmlBody: rendered.TelegramHtml), nowUtc);
                enqueued++;
            }
        }

        if (options.IsSmtpConfigured
            && shopEmails.IsEnabled
            && NotificationContracts.ShouldDeliverForWatermark(occurredAtUtc, shopEmails.UpdatedAtUtc))
        {
            foreach (var email in shopEmails.Emails)
            {
                var recipientHash = NotificationContracts.HashRecipient(NotificationContracts.NormalizeEmail(email));
                if (await AlreadySentAsync(NotificationChannels.Email, order.OrderId, recipientHash, ct))
                    continue;

                EnqueueDelivery(new NotificationDeliveryPayload(
                    NotificationChannels.Email,
                    NotificationTemplateNames.OrderPlacedShop,
                    NotificationRefTypes.Order,
                    order.OrderId,
                    recipientHash,
                    Subject: rendered.EmailSubject,
                    TextBody: rendered.EmailPlainText,
                    HtmlBody: rendered.EmailHtml), nowUtc);
                enqueued++;
            }
        }

        LogOrderPlacedFannedOut(logger, messageId, order.OrderId, enqueued);
    }

    private Task<bool> AlreadySentAsync(string channel, Guid orderId, string recipientHash, CancellationToken ct) =>
        db.NotificationLogs.AsNoTracking().AnyAsync(x =>
            x.RefType == NotificationRefTypes.Order
            && x.RefId == orderId
            && x.Channel == channel
            && x.Template == NotificationTemplateNames.OrderPlacedShop
            && x.RecipientHash == recipientHash
            && x.Status == "sent", ct);

    private void EnqueueDelivery(NotificationDeliveryPayload delivery, DateTime nowUtc)
    {
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

    [LoggerMessage(EventId = 1, EventName = "OrderPlacedNotificationFannedOut", Level = LogLevel.Information,
        Message = "OrderPlaced notification fan-out completed: messageId={MessageId}, orderId={OrderId}, deliveriesEnqueued={DeliveriesEnqueued}")]
    private static partial void LogOrderPlacedFannedOut(ILogger logger, Guid messageId, Guid orderId, int deliveriesEnqueued);

    [LoggerMessage(EventId = 2, EventName = "OrderPlacedNotificationSkippedBackfilled", Level = LogLevel.Information,
        Message = "OrderPlaced notification skipped for backfilled order: messageId={MessageId}, orderId={OrderId}")]
    private static partial void LogSkippedBackfilled(ILogger logger, Guid messageId, Guid orderId);
}
