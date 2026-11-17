namespace TeaPremium.Application.Outbox;

/// <summary>
/// Enqueue inside the command's IUnitOfWork transaction. The message is committed
/// together with the business changes and is never published directly by a command.
/// </summary>
public interface IOutboxWriter
{
    void Enqueue(string type, string payload, DateTimeOffset? availableAt = null);
}
