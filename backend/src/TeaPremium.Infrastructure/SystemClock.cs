using TeaPremium.Application.Time;

namespace TeaPremium.Infrastructure;

public sealed class SystemClock(TimeProvider timeProvider) : IClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
