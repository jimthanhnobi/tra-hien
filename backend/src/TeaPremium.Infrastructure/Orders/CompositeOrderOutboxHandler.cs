using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TeaPremium.Application.Orders;
using TeaPremium.Application.Outbox;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Orders;

// G22: exactly one CompositeOrderOutboxHandler is registered per Order* message type
// to stay compatible with OutboxBatchProcessor.SingleOrDefault.
public sealed partial class CompositeOrderOutboxHandler(
    string messageType,
    AppDbContext db,
    IEnumerable<IOrderOutboxStep> steps,
    ILogger<CompositeOrderOutboxHandler> logger) : IOutboxHandler
{
    public string MessageType { get; } = messageType;

    public async Task HandleAsync(Guid messageId, string payload, CancellationToken cancellationToken)
    {
        if (!OrderOutboxMessage.TryParse(payload, out var parsed))
            throw new InvalidOperationException("Invalid order outbox payload.");

        var exists = await db.Set<Order>().AsNoTracking()
            .AnyAsync(x => x.Id == parsed.OrderId, cancellationToken);
        if (!exists)
        {
            LogMissingOrder(logger, messageId, MessageType, parsed.OrderId, parsed.OrderCode);
            return;
        }

        var orderedSteps = steps
            .Where(s => string.Equals(s.MessageType, MessageType, StringComparison.Ordinal))
            .OrderBy(s => s.Order)
            .ToArray();

        foreach (var step in orderedSteps)
            await step.HandleAsync(messageId, parsed, cancellationToken);

        LogProcessed(logger, messageId, MessageType, parsed.OrderId, parsed.OrderCode, parsed.Status, orderedSteps.Length);
    }

    [LoggerMessage(
        EventId = 1,
        EventName = "OrderOutboxProcessed",
        Level = LogLevel.Information,
        Message = "Processed order outbox message: messageId={MessageId}, messageType={MessageType}, orderId={OrderId}, orderCode={OrderCode}, status={Status}, stepCount={StepCount}")]
    private static partial void LogProcessed(
        ILogger logger,
        Guid messageId,
        string messageType,
        Guid orderId,
        string orderCode,
        string status,
        int stepCount);

    [LoggerMessage(
        EventId = 2,
        EventName = "OrderOutboxOrderMissing",
        Level = LogLevel.Warning,
        Message = "Skipping order outbox message for missing order: messageId={MessageId}, messageType={MessageType}, orderId={OrderId}, orderCode={OrderCode}")]
    private static partial void LogMissingOrder(
        ILogger logger,
        Guid messageId,
        string messageType,
        Guid orderId,
        string orderCode);
}
