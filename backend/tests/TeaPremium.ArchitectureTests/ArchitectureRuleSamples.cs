using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeaPremium.Application.Time;
using TeaPremium.Domain;
using TeaPremium.Infrastructure.Persistence;
using ClockAlias = System.DateTime;
using static System.DateTime;

namespace TeaPremium.ArchitectureTests.Samples;

// Deliberately invalid compiled test-only examples, never added to production assemblies.
internal sealed class DependsOnEf { public DbContext? Value { get; set; } }
internal sealed class DependsOnNpgsql { public NpgsqlConnection? Value { get; set; } }
internal sealed class DependsOnInfrastructure { public AppDbContext? Value { get; set; } }
internal sealed class DependsOnApplication { public IClock? Value { get; set; } }
internal sealed class DependsOnApi { public TeaPremium.Api.Middleware.CorrelationIdMiddleware? Value { get; set; } }
internal sealed class DomainValue { public Money Value { get; set; } }
internal static class LocalDateTime { public static DateTime Read() => ClockAlias.Now; }
internal static class UtcDateTime { public static DateTime Read() => DateTime.UtcNow; }
internal static class TodayDateTime { public static DateTime Read() => DateTime.Today; }
internal static class OffsetClock { public static DateTimeOffset Read() => DateTimeOffset.UtcNow; }
internal static class StaticImportClock { public static DateTime Read() => Now; }
internal static class SystemProviderClock { public static DateTimeOffset Read() => TimeProvider.System.GetUtcNow(); }
internal static class LambdaClock { public static Func<DateTime> Read() => () => DateTime.Now; }
internal static class AsyncClock
{
    public static async Task<DateTime> ReadAsync()
    {
        await Task.Yield();
        return DateTime.UtcNow;
    }
}
internal sealed class InjectedClock(IClock clock)
{
    // A comment/string mentioning DateTime.Now must not cause a false positive.
    public static string Description => "DateTime.Now";
    public DateTimeOffset Read() => clock.UtcNow;
}
