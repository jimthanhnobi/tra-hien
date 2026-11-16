namespace TeaPremium.Application.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
