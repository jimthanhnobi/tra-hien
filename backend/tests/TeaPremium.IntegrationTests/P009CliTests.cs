using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Settings;

namespace TeaPremium.IntegrationTests;

// P0-09: execute the real entry point, file reader and trusted configuration, not a mocked importer.
public sealed class P009CliTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    // Linux is the deployment platform; option-shaped names containing ':' are not
    // regular Windows filenames, and script(1) supplies the real private terminal.
    [LinuxCliTheory]
    [InlineData("manifest")]
    [InlineData("approval")]
    [InlineData("profile")]
    public async Task ImportFileArgumentsCannotConfigureTheHost(string scenario)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var bundle = P009ImportTests.Bundle(1, "province_code,province_name,ward_code,ward_name\n01,Tỉnh,,\n");
        using var files = new BundleFiles(bundle);
        string csvArgument;
        var expected = "untrusted_manifest";
        if (scenario == "approval")
        {
            var execution = h.Approve(bundle, await h.PlanAsync(bundle));
            h.Configuration["AdministrativeImport:TrustedApprovalSha256:" + execution.ApprovalSha256] = null;
            await File.WriteAllBytesAsync(files.Plan, execution.Plan.Bytes());
            await File.WriteAllBytesAsync(files.Approval, ImportJson.Write(execution.Approval));
            csvArgument = "--AdministrativeImport:TrustedApprovalSha256:0=" + execution.ApprovalSha256;
            expected = "untrusted_approval";
        }
        else if (scenario == "profile")
        {
            h.Trust(bundle);
            csvArgument = "--environment=Test";
            expected = "production_import_hold";
        }
        else csvArgument = "--AdministrativeImport:TrustedManifestSha256:0=" + SettingsJson.Hash(bundle.ManifestBytes);
        await File.WriteAllBytesAsync(Path.Combine(files.Directory, csvArgument), bundle.CsvBytes);
        var result = await RunProcessAsync(h, files, csvArgument, scenario == "profile" ? "Staging" : "Test",
            scenario == "approval", scenario == "approval" ? ["--plan", files.Plan, "--approval", files.Approval] : ["--dry-run", files.Plan]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expected, result.Output, StringComparison.Ordinal);
        await using var db = h.OwnerContext();
        Assert.Empty(await db.Provinces.ToArrayAsync());
        Assert.Empty(await db.AdministrativeDataImports.ToArrayAsync());
    }

    [LinuxCliFact]
    public async Task AuthorizedLocalTestApplyAndExactRetryStillUseTheRealPrivateTerminal()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        using var files = new BundleFiles(P009ImportTests.Bundle(1, "province_code,province_name,ward_code,ward_name\n01,Tỉnh,,\n"));
        var dryRun = await RunAsync(h, files, "--dry-run", files.Plan);
        Assert.Equal(0, dryRun.ExitCode);
        var plan = ImportJson.Read<ImportPlan>(await File.ReadAllBytesAsync(files.Plan));
        var execution = h.Approve(files.Bundle, plan);
        await File.WriteAllBytesAsync(files.Approval, ImportJson.Write(execution.Approval));
        foreach (var status in new[] { "applied", "no_op" })
        {
            var result = await RunProcessAsync(h, files, files.Csv, "Test", true, ["--plan", files.Plan, "--approval", files.Approval]);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(status, result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        }
        await using var db = h.OwnerContext();
        Assert.Single(await db.AdministrativeDataImports.ToArrayAsync());
    }

    [LinuxCliTheory]
    [InlineData("root")]
    [InlineData("intermediate")]
    [InlineData("file")]
    public async Task SourceLinksOutsideTheBundleAreRejected(string component)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var bundle = P009ImportTests.Bundle(1, "province_code,province_name,ward_code,ward_name\n01,Tỉnh,,\n");
        if (component == "intermediate")
        {
            var m = bundle.Manifest();
            var file = m.CommunitySource.Files[0] with { Path = "nested/LICENSE", Url = m.CommunitySource.Files[0].Url.Replace("/LICENSE", "/nested/LICENSE", StringComparison.Ordinal) };
            bundle = bundle with
            {
                ManifestBytes = ImportJson.Write(m with { CommunitySource = m.CommunitySource with { Files = [m.CommunitySource.Files[0], file] } }),
                SourceFiles = new Dictionary<string, byte[]>(bundle.SourceFiles, StringComparer.Ordinal) { [file.Path] = bundle.SourceFiles["LICENSE"] }
            };
        }
        using var files = new BundleFiles(bundle);
        var outside = Path.Combine(files.Directory, "outside");
        System.IO.Directory.CreateDirectory(outside);
        var source = Path.Combine(files.Directory, "source");
        if (component == "root")
        {
            System.IO.Directory.Move(source, Path.Combine(outside, "original"));
            System.IO.Directory.CreateSymbolicLink(source, Path.Combine(outside, "original"));
        }
        else if (component == "intermediate")
        {
            System.IO.Directory.Move(Path.Combine(source, "nested"), Path.Combine(outside, "original"));
            System.IO.Directory.CreateSymbolicLink(Path.Combine(source, "nested"), Path.Combine(outside, "original"));
        }
        else
        {
            File.Move(Path.Combine(source, "LICENSE"), Path.Combine(outside, "LICENSE"));
            File.CreateSymbolicLink(Path.Combine(source, "LICENSE"), Path.Combine(outside, "LICENSE"));
        }
        var result = await RunAsync(h, files, "--dry-run", files.Plan);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unsafe_source_path", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(files.Plan));
    }

    [Theory]
    [InlineData("../LICENSE")]
    [InlineData("/LICENSE")]
    [InlineData("nested/../../LICENSE")]
    [InlineData("nested//LICENSE")]
    [InlineData("nested/./LICENSE")]
    [InlineData("nested\\LICENSE")]
    [InlineData("C:/LICENSE")]
    [InlineData("bad\0LICENSE")]
    [InlineData("")]
    public async Task TraversalAndNoncanonicalSourcePathsAreRejected(string path)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var bundle = P009ImportTests.Bundle(1, "province_code,province_name,ward_code,ward_name\n01,Tỉnh,,\n");
        var manifest = bundle.Manifest();
        bundle = bundle with { ManifestBytes = ImportJson.Write(manifest with { CommunitySource = manifest.CommunitySource with { Files = [manifest.CommunitySource.Files[0] with { Path = path }] } }) };
        using var files = new BundleFiles(bundle);
        var result = await RunAsync(h, files, "--dry-run", files.Plan);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unsafe_source_path", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(files.Plan));
    }

    [Fact]
    public async Task RegularNestedSourceFilesRemainValid()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var bundle = P009ImportTests.Bundle(1, "province_code,province_name,ward_code,ward_name\n01,Tỉnh,,\n");
        var m = bundle.Manifest();
        var extra = m.CommunitySource.Files[0] with { Path = "nested/LICENSE", Url = m.CommunitySource.Files[0].Url.Replace("/LICENSE", "/nested/LICENSE", StringComparison.Ordinal) };
        bundle = bundle with
        {
            ManifestBytes = ImportJson.Write(m with { CommunitySource = m.CommunitySource with { Files = [m.CommunitySource.Files[0], extra] } }),
            SourceFiles = new Dictionary<string, byte[]>(bundle.SourceFiles, StringComparer.Ordinal) { [extra.Path] = bundle.SourceFiles["LICENSE"] }
        };
        using var files = new BundleFiles(bundle);
        var result = await RunAsync(h, files, "--dry-run", files.Plan);
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(files.Plan));
    }

    [Fact]
    public async Task RealCliDryRunWritesCanonicalPlanAndRedirectedApplyCannotMutate()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        using var files = new BundleFiles(P009ImportTests.Bundle(1, "province_code,province_name,ward_code,ward_name\n01,Tỉnh,,\n"));
        var dryRun = await RunAsync(h, files, "--dry-run", files.Plan);
        Assert.Equal(0, dryRun.ExitCode);
        var bytes = await File.ReadAllBytesAsync(files.Plan);
        var plan = ImportJson.Read<ImportPlan>(bytes);
        Assert.Equal(bytes, plan.Bytes());
        Assert.Contains("dry_run plan_sha256=" + plan.Fingerprint(), dryRun.Output, StringComparison.Ordinal);
        var execution = h.Approve(files.Bundle, plan);
        await File.WriteAllBytesAsync(files.Approval, ImportJson.Write(execution.Approval));
        var apply = await RunAsync(h, files, "--plan", files.Plan, "--approval", files.Approval);
        Assert.Equal(2, apply.ExitCode);
        Assert.Contains("private_operator_terminal_required", apply.Output, StringComparison.Ordinal);
        await using var db = h.OwnerContext();
        Assert.Empty(await db.Provinces.ToArrayAsync());
        Assert.Empty(await db.AdministrativeDataImports.ToArrayAsync());
    }

    [Theory]
    [InlineData("null-source")]
    [InlineData("unknown-field")]
    [InlineData("invalid-utf8")]
    [InlineData("checksum")]
    [InlineData("orphan")]
    public async Task RealCliRejectsInvalidFilesWithoutAnApplyablePlan(string scenario)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var bundle = P009ImportTests.Bundle(1, "province_code,province_name,ward_code,ward_name\n01,Tỉnh,,\n");
        if (scenario == "null-source") bundle = bundle with { ManifestBytes = ImportJson.Write(bundle.Manifest() with { CommunitySource = bundle.Manifest().CommunitySource with { Files = [null!] } }) };
        if (scenario == "unknown-field") bundle = bundle with { ManifestBytes = Encoding.UTF8.GetBytes("{\"private-input\":true}") };
        if (scenario == "invalid-utf8") bundle = bundle with { ManifestBytes = [0xff] };
        if (scenario == "checksum") bundle = bundle with { CsvBytes = Encoding.UTF8.GetBytes("changed private input") };
        if (scenario == "orphan")
        {
            var csv = Encoding.UTF8.GetBytes("province_code,province_name,ward_code,ward_name\n01,Tỉnh,00001,Xã\n");
            bundle = bundle with { CsvBytes = csv, ManifestBytes = ImportJson.Write(bundle.Manifest() with { Csv = bundle.Manifest().Csv with { Bytes = csv.Length, Sha256 = SettingsJson.Hash(csv) } }) };
        }
        using var files = new BundleFiles(bundle);
        var result = await RunAsync(h, files, "--dry-run", files.Plan);
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("private-input", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("changed private input", result.Output, StringComparison.Ordinal);
        if (File.Exists(files.Plan))
        {
            var report = await File.ReadAllTextAsync(files.Plan);
            Assert.Contains("\"code\":", report, StringComparison.Ordinal);
            Assert.DoesNotContain("\"before_digest\":", report, StringComparison.Ordinal);
        }
        await using var db = h.OwnerContext();
        Assert.Empty(await db.Provinces.ToArrayAsync());
        Assert.Empty(await db.AdministrativeDataImports.ToArrayAsync());
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(P009Harness h, BundleFiles files, params string[] options)
    {
        h.Trust(files.Bundle);
        return await RunProcessAsync(h, files, files.Csv, "Test", false, options);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(P009Harness h, BundleFiles files,
        string csvArgument, string environment, bool terminal, string[] options)
    {
        var arguments = new[] { typeof(Program).Assembly.Location, "import-provinces", csvArgument, "--manifest", files.Manifest }.Concat(options).ToArray();
        var start = new ProcessStartInfo(terminal ? "script" : "dotnet")
        {
            WorkingDirectory = files.Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var processArguments = terminal ? new[] { "-q", "-e", "-E", "never", "-c", "dotnet " + string.Join(' ', arguments.Select(Quote)), "/dev/null" } : arguments;
        foreach (var argument in processArguments)
            start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        start.Environment["DOTNET_ENVIRONMENT"] = environment;
        start.Environment["ConnectionStrings__Default"] = h.RuntimeConnection;
        start.Environment["Iam__KeyCertificatePath"] = h.CertificatePath;
        foreach (var pair in h.Configuration.AsEnumerable().Where(x => x.Value is not null))
            start.Environment[pair.Key.Replace(":", "__", StringComparison.Ordinal)] = pair.Value;
        using var process = Process.Start(start)!;
        if (!terminal) process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = new StringBuilder();
        try
        {
            if (terminal)
            {
                process.StandardInput.NewLine = "\n";
                var buffer = new char[1];
                var sent = 0;
                while (await process.StandardOutput.ReadAsync(buffer, timeout.Token) != 0)
                {
                    output.Append(buffer[0]);
                    var current = output.ToString();
                    if (sent == 0 && current.EndsWith("Owner email: ", StringComparison.Ordinal)) { await process.StandardInput.WriteLineAsync("owner@example.test"); sent++; }
                    if (sent == 1 && current.EndsWith("Password (hidden): ", StringComparison.Ordinal)) { await process.StandardInput.WriteLineAsync(P009Harness.Password); sent++; }
                    if (sent == 2 && current.EndsWith("TOTP if enabled (hidden): ", StringComparison.Ordinal)) { await process.StandardInput.WriteLineAsync(""); sent++; }
                }
            }
            else output.Append(await process.StandardOutput.ReadToEndAsync(timeout.Token));
            await process.WaitForExitAsync(timeout.Token);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        output.Append(await stderr);
        Assert.DoesNotContain(P009Harness.Password, output.ToString(), StringComparison.Ordinal);
        return (process.ExitCode, output.ToString());
    }

    private sealed class BundleFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "p009-cli-" + Guid.NewGuid().ToString("N"));
        public ImportBundle Bundle { get; }
        public string Csv => Path.Combine(Directory, "provinces.csv");
        public string Manifest => Path.Combine(Directory, "provinces.manifest.json");
        public string Plan => Path.Combine(Directory, "plan.json");
        public string Approval => Path.Combine(Directory, "approval.json");
        public BundleFiles(ImportBundle bundle)
        {
            Bundle = bundle;
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(Csv, bundle.CsvBytes);
            File.WriteAllBytes(Manifest, bundle.ManifestBytes);
            foreach (var file in bundle.SourceFiles)
            {
                var path = Path.Combine(Directory, "source", file.Key);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Value);
            }
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

internal sealed class LinuxCliFactAttribute : FactAttribute
{
    public LinuxCliFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Requires Linux script(1) PTY or POSIX source links; no host policy changes."; }
}
internal sealed class LinuxCliTheoryAttribute : TheoryAttribute
{
    public LinuxCliTheoryAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Requires Linux script(1) PTY or POSIX source links; no host policy changes."; }
}
