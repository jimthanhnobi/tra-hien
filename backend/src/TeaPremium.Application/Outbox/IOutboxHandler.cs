namespace TeaPremium.Application.Outbox;

/// <summary>
/// Handlers must make redelivery of the same message ID safe. Delivery is at least once:
/// a process can stop after a side effect and before the processed marker commits.
/// </summary>
public interface IOutboxHandler
{
    string MessageType { get; }
    Task HandleAsync(Guid messageId, string payload, CancellationToken cancellationToken);
}
