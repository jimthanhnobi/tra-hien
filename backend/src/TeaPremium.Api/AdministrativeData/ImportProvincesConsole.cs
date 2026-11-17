using System.Text;
using System.Text.Json;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Settings;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.AdministrativeData;

public static class ImportProvincesConsole
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken ct = default)
    {
        try
        {
            if (args.Length is not (6 or 8) || args[0] != "import-provinces" || args[2] != "--manifest"
                || args[4] is not ("--dry-run" or "--plan")
                || args[4] == "--plan" && (args.Length != 8 || args[6] != "--approval")
                || args[4] == "--dry-run" && args.Length != 6) return Usage();
            var csv = await ReadBoundedAsync(args[1], ct);
            var manifestBytes = await ReadBoundedAsync(args[3], ct);
            var manifest = ImportJson.Read<ImportManifest>(manifestBytes);
            ImportValidation.Require(manifest.CommunitySource?.Files is { Length: > 0 and <= 32 }, "invalid_source_identity");
            var sourceRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[3]))!, "source");
            var source = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in manifest.CommunitySource!.Files)
            {
                var path = Path.GetFullPath(Path.Combine(sourceRoot, file.Path));
                if (!path.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new ImportRejectedException("unsafe_source_path");
                source.Add(file.Path, await ReadBoundedAsync(path, ct));
            }
            var bundle = new ImportBundle(csv, manifestBytes, source);
            await using var scope = services.CreateAsyncScope();
            if (args[4] == "--dry-run")
            {
                var result = await scope.ServiceProvider.GetRequiredService<IQueryHandler<PlanAdministrativeImport, ImportPlan>>().Handle(new(bundle), ct);
                if (!result.IsSuccess)
                {
                    await File.WriteAllBytesAsync(args[5], ImportJson.Write(new
                    {
                        schema_version = "vn-admin-plan/1",
                        code = result.Error.Code,
                        errors = result.Error.ValidationErrors ?? new Dictionary<string, string[]> { ["input"] = ["Input hoặc hồ sơ xác minh không hợp lệ."] }
                    }), ct);
                    Console.Error.WriteLine(result.Error.Code); return 1;
                }
                await File.WriteAllBytesAsync(args[5], result.Value.Bytes(), ct);
                Console.WriteLine($"dry_run plan_sha256={result.Value.Fingerprint()}");
                foreach (var summary in result.Value.Changes.GroupBy(x => (x.Kind, x.Action)))
                    Console.WriteLine($"{summary.Key.Kind} {summary.Key.Action}: {summary.Count()}");
                return 0;
            }
            if (Console.IsInputRedirected) throw new ImportRejectedException("private_operator_terminal_required");
            var planBytes = await ReadBoundedAsync(args[5], ct);
            var approvalBytes = await ReadBoundedAsync(args[7], ct);
            var plan = ImportJson.Read<ImportPlan>(planBytes);
            if (!plan.Bytes().AsSpan().SequenceEqual(planBytes)) throw new ImportRejectedException("noncanonical_plan");
            var approval = ImportJson.Read<ImportApproval>(approvalBytes);
            if (!ImportJson.Write(approval).AsSpan().SequenceEqual(approvalBytes)) throw new ImportRejectedException("noncanonical_approval");
            Console.Write("Owner email: "); var email = Console.ReadLine() ?? "";
            Console.Write("Password (hidden): "); var password = Hidden();
            Console.Write("TOTP if enabled (hidden): "); var totp = Hidden();
            var login = await scope.ServiceProvider.GetRequiredService<ICommandHandler<LoginStaff, LoginOutcome>>()
                .Handle(new(email, password, totp.Length == 0 ? null : totp), ct);
            if (!login.IsSuccess || login.Value.User is null || !login.Value.FactorComplete
                || !login.Value.User.Roles.Contains("owner", StringComparer.Ordinal)) throw new ImportRejectedException("operator_not_authorized");
            await using var applyScope = services.CreateAsyncScope();
            var execution = new ImportExecution(plan, approval, SettingsJson.Hash(approvalBytes), login.Value.User.Id, false);
            var applied = await applyScope.ServiceProvider.GetRequiredService<ICommandHandler<ApplyAdministrativeImport, ImportOutcome>>()
                .Handle(new(bundle, execution), ct);
            Console.WriteLine(applied.IsSuccess ? applied.Value.Status : applied.Error.Code);
            return applied.IsSuccess ? 0 : 1;
        }
        catch (Exception e) when (e is IOException or JsonException or ImportRejectedException or FormatException
            or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(e is ImportRejectedException rejected ? rejected.Code : "invalid_import_input");
            return 2;
        }
    }
    private static int Usage()
    {
        Console.Error.WriteLine("import-provinces <csv> --manifest <manifest.json> --dry-run <plan.json> | --plan <plan.json> --approval <approval.json>");
        return 2;
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        if (file.Length > AdministrativeCsv.MaxBytes) throw new ImportRejectedException("import_input_too_large");
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, ct);
        if (stream.Length > AdministrativeCsv.MaxBytes) throw new ImportRejectedException("import_input_too_large");
        return stream.ToArray();
    }
    private static string Hidden()
    {
        var result = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return result.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (result.Length > 0) result.Length--; }
            else if (!char.IsControl(key.KeyChar) && result.Length < 1024) result.Append(key.KeyChar);
        }
    }
}
