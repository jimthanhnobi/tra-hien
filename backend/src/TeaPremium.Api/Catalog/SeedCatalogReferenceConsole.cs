using Npgsql;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Catalog;

public static class SeedCatalogReferenceConsole
{
    // No command-line configuration overrides; Production always requires a separate task/authorization.
    public static string? Preflight(string[] args, string environment, string? connection)
    {
        if (args is not ["seed-catalog-reference"]) return "catalog_seed_usage";
        if (environment is not ("Development" or "Test")) return "catalog_seed_production_hold";
        try
        {
            var target = new NpgsqlConnectionStringBuilder(connection ?? "");
            if (target.Host is not ("localhost" or "127.0.0.1" or "::1" or "host.docker.internal")
                || target.Username != "tp_app"
                || !(target.Database?.StartsWith("teapremium_dev", StringComparison.Ordinal) == true
                    || target.Database?.StartsWith("teapremium_test", StringComparison.Ordinal) == true)) return "catalog_seed_unsafe_target";
        }
        catch (ArgumentException) { return "catalog_seed_unsafe_target"; }
        return null;
    }
    public static async Task<int> RunAsync(IServiceProvider services)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<SeedCatalogReference, CatalogSeedOutcome>>()
                .Handle(new(), CancellationToken.None);
            if (!result.IsSuccess) { Console.Error.WriteLine(result.Error.Code); return 3; }
            var added = result.Value;
            Console.WriteLine($"catalog_seed_ok types={added.ProductTypesAdded} attributes={added.AttributesAdded} options={added.OptionsAdded} materials={added.MaterialsAdded}");
            return 0;
        }
        catch (Exception)
        {
            // Never emit provider messages, connection strings, SQL or owner-edited metadata.
            Console.Error.WriteLine("catalog_seed_failed"); return 1;
        }
    }
}
