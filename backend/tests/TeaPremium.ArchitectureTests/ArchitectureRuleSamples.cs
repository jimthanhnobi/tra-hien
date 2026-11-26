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

// P2-03 / G4 P2-02 carry-over [P2]: deliberate compiled CRM boundary violations & positive control.
internal static class CrmDirectDbSetPropertySample
{
    public static int Read(AppDbContext db) => db.Provinces.Local.Count;
}

internal static class CrmGenericSetMethodSample
{
    public static object Read(AppDbContext db) => db.Set<Order>();
}

internal static class CrmAsyncMoveNextLinqSample
{
    // Outer signature mentions only AppDbContext, CancellationToken and Task<int>; Variant only appears inside MoveNext().
    public static async Task<int> CountAsync(AppDbContext db, CancellationToken ct)
    {
        await Task.Yield();
        return await db.Set<Variant>().CountAsync(ct);
    }
}

internal static class CrmLambdaClosureSample
{
    public static Func<object> Deferred(AppDbContext db) => () => db.Set<Product>();
}

internal static class CrmTypeCheckAndTokenSample
{
    // Uses isinst (Ward) and ldtoken (Reservation) so Release builds never optimize the references out of IL.
    public static bool Inspect(object? value) => value is Ward || typeof(Reservation) == value?.GetType();
}

internal static class CrmForeignModuleInfraSample
{
    public static object ReferenceForeign(AppDbContext db) => new TeaPremium.Infrastructure.Orders.EfCustomerOrderQueries(db);
}

[AttributeUsage(AttributeTargets.ReturnValue)]
internal sealed class SampleReturnTypeHintAttribute(Type hintType) : Attribute
{
    public Type HintType { get; } = hintType;
}

internal static class CrmReturnAttributeSample
{
    [return: SampleReturnTypeHint(typeof(Order))]
    public static int Read() => 1;
}

internal static class CrmAllowedOwnEntitySample
{
    public static async Task<int> CountOwnAsync(AppDbContext db, CancellationToken ct)
    {
        await Task.Yield();
        db.AuditLogs.Add(new AuditLog { Id = Guid.CreateVersion7() });
        var customers = await db.Set<Customer>().CountAsync(ct);
        var tags = await db.Set<Tag>().CountAsync(ct);
        var interactions = await db.Set<Interaction>().CountAsync(ct);
        return customers + tags + interactions;
    }
}
