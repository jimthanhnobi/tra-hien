using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TeaPremium.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace TeaPremium.IntegrationTests;

public sealed class DatabaseRolesTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    [Theory]
    [InlineData("tp_app", "SELECT count(*) FROM platform.settings")]
    [InlineData("tp_app", "SELECT count(*) FROM iam.staff_users")]
    [InlineData("tp_app", "SELECT count(*) FROM public.\"__EFMigrationsHistory\"")]
    [InlineData("tp_app", "SELECT public.f_unaccent('Đặng')")]
    [InlineData("tp_app", "SELECT pg_try_advisory_xact_lock(8300,9)")]
    [InlineData("tp_app", "SELECT pg_advisory_xact_lock(830081::bigint)")]
    [InlineData("tp_readonly", "SELECT count(*) FROM platform.data_protection_keys")]
    [InlineData("tp_readonly", "SELECT count(*) FROM iam.staff_users")]
    [InlineData("tp_readonly", "SELECT last_value FROM platform.data_protection_keys_id_seq")]
    public async Task RealRestrictedLoginHasRequiredPermissions(string role, string sql)
    {
        await using var connection = new NpgsqlConnection(DatabaseProvisioning.Login(fixture.ConnectionString, role));
        await connection.OpenAsync();
        Assert.Equal(role, await ScalarAsync(connection, "SELECT current_user"));
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteScalarAsync();
    }

    [Theory]
    [InlineData("tp_app", "CREATE TABLE platform.forbidden(id int)")]
    [InlineData("tp_app", "CREATE TABLE public.forbidden(id int)")]
    [InlineData("tp_app", "CREATE TEMP TABLE forbidden(id int)")]
    [InlineData("tp_app", "CREATE SCHEMA forbidden")]
    [InlineData("tp_app", "CREATE ROLE forbidden")]
    [InlineData("tp_app", "CREATE DATABASE forbidden")]
    [InlineData("tp_app", "SET ROLE tp_owner")]
    [InlineData("tp_app", "SET ROLE tp_readonly")]
    [InlineData("tp_app", "GRANT USAGE ON SCHEMA platform TO PUBLIC")]
    [InlineData("tp_readonly", "GRANT USAGE ON SCHEMA platform TO PUBLIC")]
    [InlineData("tp_app", "GRANT SELECT ON platform.data_protection_keys TO PUBLIC")]
    [InlineData("tp_readonly", "GRANT SELECT ON platform.data_protection_keys TO PUBLIC")]
    [InlineData("tp_app", "GRANT USAGE ON SEQUENCE platform.data_protection_keys_id_seq TO PUBLIC")]
    [InlineData("tp_readonly", "GRANT SELECT ON SEQUENCE platform.data_protection_keys_id_seq TO PUBLIC")]
    [InlineData("tp_app", "ALTER TABLE platform.settings ADD COLUMN forbidden int")]
    [InlineData("tp_app", "TRUNCATE platform.settings")]
    [InlineData("tp_app", "UPDATE platform.audit_logs SET changes='{}'")]
    [InlineData("tp_app", "DELETE FROM platform.audit_logs")]
    [InlineData("tp_app", "TRUNCATE platform.audit_logs")]
    [InlineData("tp_app", "UPDATE platform.administrative_data_imports SET receipt='{}'")]
    [InlineData("tp_app", "DELETE FROM platform.administrative_data_imports")]
    [InlineData("tp_app", "TRUNCATE platform.administrative_data_imports")]
    [InlineData("tp_app", "VACUUM platform.administrative_data_imports")]
    [InlineData("tp_app", "DELETE FROM public.\"__EFMigrationsHistory\"")]
    [InlineData("tp_app", "SELECT setval('platform.data_protection_keys_id_seq',1000)")]
    [InlineData("tp_readonly", "INSERT INTO platform.settings(key,value) VALUES ('forbidden','{}')")]
    [InlineData("tp_readonly", "UPDATE iam.staff_users SET is_active=false")]
    [InlineData("tp_readonly", "DELETE FROM platform.settings")]
    [InlineData("tp_readonly", "TRUNCATE platform.settings")]
    [InlineData("tp_readonly", "SELECT nextval('platform.data_protection_keys_id_seq')")]
    [InlineData("tp_readonly", "CREATE SCHEMA forbidden")]
    public async Task DeniedOperationsCannotBypassRestrictedLogin(string role, string sql)
    {
        await using var connection = new NpgsqlConnection(DatabaseProvisioning.Login(fixture.ConnectionString, role));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (sql.StartsWith("VACUUM", StringComparison.Ordinal) || sql.StartsWith("GRANT", StringComparison.Ordinal))
        {
            // PostgreSQL VACUUM/GRANT can reject the operation with a WARNING.
            var denied = false;
            connection.Notice += (_, notice) => denied |= notice.Notice.SqlState is "01000" or "01007";
            await command.ExecuteNonQueryAsync();
            Assert.True(denied);
            if (sql.StartsWith("GRANT", StringComparison.Ordinal))
            {
                Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) a WHERE n.nspname='platform' AND a.grantee=0"));
                Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace CROSS JOIN LATERAL aclexplode(c.relacl) a WHERE n.nspname IN ('platform','iam','public') AND a.grantee=0"));
            }
        }
        else
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    public async Task RuntimeDmlAndAppendOnlyInsertWorkInRealTransactions()
    {
        await using var connection = new NpgsqlConnection(fixture.RuntimeConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
INSERT INTO platform.settings(key,value) VALUES ('closure.test','1');
UPDATE platform.settings SET value='2' WHERE key='closure.test';
DELETE FROM platform.settings WHERE key='closure.test';
INSERT INTO platform.audit_logs(id,occurred_at,actor_type,entity_type,entity_key,action)
VALUES (gen_random_uuid(),now(),'system','platform.settings','closure.test','access');
INSERT INTO platform.data_protection_keys(friendly_name,xml) VALUES ('fake-closure','<fake/>');
""", connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task FrozenMigrationMatchesAuthoringHelperAndSafeDefaults()
    {
        await using var db = fixture.CreateContext();
        var script = db.GetService<IMigrator>().GenerateScript("202611180001_SettingsAdministrativeData");
        Assert.Contains(GrantsSql.PlatformAndIam(), script.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(0L, await fixture.ScalarAsync("""
SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member
WHERE r.rolname IN ('tp_owner','tp_app','tp_readonly')
"""));
        Assert.Equal(0L, await fixture.ScalarAsync("""
SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
CROSS JOIN LATERAL aclexplode(c.relacl) a
WHERE n.nspname IN ('platform','iam','public') AND a.grantee=0
"""));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
CREATE TABLE platform.closure_default_probe(id serial);
CREATE FUNCTION platform.closure_default_probe() RETURNS int LANGUAGE sql AS 'SELECT 1';
SELECT NOT has_table_privilege('tp_app','platform.closure_default_probe','INSERT')
 AND has_table_privilege('tp_readonly','platform.closure_default_probe','SELECT')
 AND has_sequence_privilege('tp_app','platform.closure_default_probe_id_seq','USAGE')
 AND NOT has_sequence_privilege('tp_readonly','platform.closure_default_probe_id_seq','USAGE')
 AND NOT has_function_privilege('tp_app','platform.closure_default_probe()','EXECUTE');
""", connection, transaction);
        Assert.Equal(true, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ExistingP0DataPasswordsAndSequenceSurviveProvisionMigrationAndRepeat()
    {
        await using var container = DatabaseProvisioning.FastBuilder().Build();
        await container.StartAsync();
        await DatabaseProvisioning.ProvisionAsync(container, "teapremium_test");
        var owner = DatabaseProvisioning.Login(container.GetConnectionString(), "tp_owner", "teapremium_test");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(owner).Options);
        await db.GetService<IMigrator>().MigrateAsync("202611180001_SettingsAdministrativeData");
        await ExecuteAsync(owner, """
UPDATE platform.settings SET value='43210' WHERE key='shipping.flat_fee_vnd';
INSERT INTO platform.provinces(code,name) VALUES ('FAKE','Fake Province');
INSERT INTO platform.wards(code,province_code,name) VALUES ('FAKE-W','FAKE','Fake Ward');
INSERT INTO platform.data_protection_keys(friendly_name,xml) VALUES ('fake-existing','<fake/>');
INSERT INTO iam.staff_users(id,email,normalized_email,display_name,is_active,must_change_password,created_at,
 email_confirmed,phone_number_confirmed,two_factor_enabled,lockout_enabled,access_failed_count,password_hash,security_stamp,concurrency_stamp)
VALUES ('00000000-0000-7000-8000-000000000099','fake@example.test','FAKE@EXAMPLE.TEST','Fake',true,true,now(),
 false,false,false,true,0,'fake-hash','fake-security','fake-concurrency');
INSERT INTO platform.audit_logs(id,occurred_at,actor_type,entity_type,entity_key,action)
VALUES ('00000000-0000-7000-8000-000000000098',now(),'system','platform.settings','shipping.flat_fee_vnd','update');
INSERT INTO platform.administrative_data_imports(id,dataset_version,release_order,effective_on,operation,manifest,
manifest_sha256,csv_sha256,plan_sha256,catalog_sha256,receipt,applied_at)
VALUES ('00000000-0000-7000-8000-000000000097','fake',1,'2026-01-01','import','{}',
 repeat('a',64),repeat('b',64),repeat('c',64),repeat('d',64),'{}',now());
""");
        var before = await FingerprintAsync(owner);
        var passwords = await PasswordFingerprintAsync(container);
        // Existing credentials are deliberately different from supplied values.
        var repeat = await container.ExecAsync(["sh", "-c",
            "export TP_OWNER_PASSWORD=\"$TP_OWNER_PASSWORD-changed\" TP_APP_PASSWORD=\"$TP_APP_PASSWORD-changed\" TP_READONLY_PASSWORD=\"$TP_READONLY_PASSWORD-changed\"; exec sh /opt/teapremium/init-roles.sh"]);
        Assert.Equal(0, repeat.ExitCode);
        await DatabaseProvisioning.MigrateAsync(owner);
        var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        await ExecuteAsync(owner, script);
        await ExecuteAsync(owner, script);
        await DatabaseProvisioning.ProvisionAsync(container, "teapremium_test");
        Assert.Equal(before, await FingerprintAsync(owner));
        Assert.Equal(passwords, await PasswordFingerprintAsync(container));
        foreach (var role in new[] { "tp_app", "tp_readonly" })
        {
            await using var connection = new NpgsqlConnection(DatabaseProvisioning.Login(owner, role));
            await connection.OpenAsync();
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM platform.administrative_data_imports"));
        }
    }

    [Fact]
    public async Task DevMigrateEntryPointWorksOnFreshDatabaseAndRerun()
    {
        await using var container = DatabaseProvisioning.FastBuilder().Build();
        await container.StartAsync();
        await DatabaseProvisioning.ProvisionAsync(container, "teapremium_test");
        var first = await RunToolAsync(container, preflightOnly: false);
        Assert.True(first.Code == 0, first.Output);
        var second = await RunToolAsync(container, preflightOnly: false);
        Assert.True(second.Code == 0, second.Output);
        var runtime = DatabaseProvisioning.Login(container.GetConnectionString(), "tp_app", "teapremium_test");
        await using var connection = new NpgsqlConnection(runtime);
        await connection.OpenAsync();
        Assert.Equal(6L, await ScalarAsync(connection, "SELECT count(*) FROM public.\"__EFMigrationsHistory\""));
    }

    [Fact]
    public async Task ControlledBackupCredentialsDoNotGrantInvestigationSensitiveColumns()
    {
        await using var container = DatabaseProvisioning.FastBuilder().Build();
        await container.StartAsync();
        await DatabaseProvisioning.ProvisionAsync(container, "teapremium_test");
        var owner = DatabaseProvisioning.Login(container.GetConnectionString(), "tp_owner", "teapremium_test");
        await DatabaseProvisioning.MigrateAsync(owner);
        var admin = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = "teapremium_test" }.ConnectionString;
        // Fixture-only case identity, never provisioned by application artifacts.
        // It receives selected non-sensitive audit columns, no backup membership.
        await ExecuteAsync(admin, """
CREATE ROLE closure_investigator LOGIN NOINHERIT PASSWORD 'Disposable-Test-Only!';
GRANT CONNECT ON DATABASE teapremium_test TO closure_investigator;
GRANT USAGE ON SCHEMA platform,iam TO closure_investigator;
GRANT SELECT(id,occurred_at,entity_type,action) ON platform.audit_logs TO closure_investigator;
""");
        await using var investigation = new NpgsqlConnection(DatabaseProvisioning.Login(owner, "closure_investigator"));
        await investigation.OpenAsync();
        await ScalarAsync(investigation, "SELECT count(id) FROM platform.audit_logs");
        foreach (var sql in new[] {
            "SELECT password_hash,security_stamp,concurrency_stamp FROM iam.staff_users",
            "SELECT value FROM iam.user_tokens", "SELECT xml FROM platform.data_protection_keys",
            "SELECT changes FROM platform.audit_logs", "SET ROLE tp_readonly", "SET ROLE tp_owner" })
        {
            await using var command = new NpgsqlCommand(sql, investigation);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        await using var backup = new NpgsqlConnection(DatabaseProvisioning.Login(owner, "tp_readonly"));
        await backup.OpenAsync();
        await ScalarAsync(backup, "SELECT count(password_hash) FROM iam.staff_users");
        await ScalarAsync(backup, "SELECT count(xml) FROM platform.data_protection_keys");
    }

    internal static async Task<string> PrepareDelegationDatabaseAsync(PostgreSqlContainer container, bool closurePending)
    {
        await container.StartAsync();
        await DatabaseProvisioning.ProvisionAsync(container, "teapremium_test");
        var owner = DatabaseProvisioning.Login(container.GetConnectionString(), "tp_owner", "teapremium_test");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(owner).Options);
        await db.GetService<IMigrator>().MigrateAsync(closurePending ? "202611180001_SettingsAdministrativeData" : null);
        return owner;
    }

    internal static async Task AssertDelegationRejectedWithoutRepairAsync(
        PostgreSqlContainer container, string owner, bool closurePending, string diagnostic)
    {
        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync();
        const string historyState = "SELECT string_agg(\"MigrationId\",',' ORDER BY \"MigrationId\") FROM public.\"__EFMigrationsHistory\"";
        var history = await ScalarAsync(connection, historyState);
        Assert.Equal(closurePending ? 3L : 6L, await ScalarAsync(connection, "SELECT count(*) FROM public.\"__EFMigrationsHistory\""));
        const string aclState = """
SELECT concat(
 (SELECT jsonb_agg(jsonb_build_array(c.oid,c.relacl::text) ORDER BY c.oid)
  FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN ('platform','iam','public')),
 (SELECT jsonb_agg(to_jsonb(m) ORDER BY m.oid) FROM pg_auth_members m))
""";
        const string publicGrants = """
SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
CROSS JOIN LATERAL aclexplode(c.relacl) a WHERE n.nspname IN ('platform','iam','public') AND a.grantee=0
""";
        var aclBefore = await ScalarAsync(connection, aclState);
        var dataBefore = await FingerprintAsync(owner);
        Assert.Equal(0L, await ScalarAsync(connection, publicGrants));
        var preflight = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner, GrantsSql.Preflight));
        Assert.Contains(diagnostic, preflight.MessageText, StringComparison.Ordinal);
        var provisioning = await container.ExecAsync(["sh", "/opt/teapremium/init-roles.sh"]);
        Assert.NotEqual(0, provisioning.ExitCode);
        Assert.Contains(diagnostic, provisioning.Stderr, StringComparison.Ordinal);
        foreach (var preflightOnly in new[] { true, false })
        {
            var entryPoint = await RunToolAsync(container, preflightOnly);
            Assert.NotEqual(0, entryPoint.Code);
            Assert.Contains(diagnostic, entryPoint.Output, StringComparison.Ordinal);
        }
        // Exercise the frozen guard itself, even when the closure is still pending.
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(owner).Options);
        var sql = db.GetService<IMigrator>().GenerateScript("202611180001_SettingsAdministrativeData");
        var frozen = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner, sql));
        Assert.Contains(diagnostic, frozen.MessageText, StringComparison.Ordinal);
        Assert.Equal(history, await ScalarAsync(connection, historyState));
        Assert.Equal(aclBefore, await ScalarAsync(connection, aclState));
        Assert.Equal(dataBefore, await FingerprintAsync(owner));
        Assert.Equal(0L, await ScalarAsync(connection, publicGrants));
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "dev-migrate.ps1"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }

    internal static async Task<(int Code, string Output)> RunToolAsync(PostgreSqlContainer container, bool preflightOnly, string? checkout = null)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell" : "pwsh")
        {
            WorkingDirectory = checkout ?? RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-File", "scripts/dev-migrate.ps1", "-Container", container.Id }) start.ArgumentList.Add(argument);
        if (preflightOnly) start.ArgumentList.Add("-PreflightOnly");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    internal static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    internal static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<object?> FingerprintAsync(string owner)
    {
        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync();
        return await ScalarAsync(connection, """
SELECT md5(concat(
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY key) FROM platform.settings t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM iam.staff_users t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY user_id,role_id) FROM iam.user_roles t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM iam.role_claims t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY code) FROM platform.provinces t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY code) FROM platform.wards t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM platform.audit_logs t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM platform.administrative_data_imports t),
 (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM platform.data_protection_keys t),
 (SELECT jsonb_build_array(last_value,is_called) FROM platform.data_protection_keys_id_seq),
 (SELECT jsonb_build_array(last_value,is_called) FROM iam.role_claims_id_seq),
 (SELECT jsonb_build_array(last_value,is_called) FROM iam.user_claims_id_seq)))
""");
    }

    internal static async Task<object?> PasswordFingerprintAsync(PostgreSqlContainer container)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        return await ScalarAsync(connection, "SELECT md5(string_agg(rolname||rolpassword,',' ORDER BY rolname)) FROM pg_authid WHERE rolname IN ('tp_owner','tp_app','tp_readonly')");
    }
}

public sealed class DatabaseRoleTemplateFixture : IAsyncLifetime
{
    public const string EmptyTemplate = "tp_roles_tpl_empty";
    public const string PreClosureTemplate = "tp_roles_tpl_preclosure";
    public const string LatestTemplate = "tp_roles_tpl_latest";

    private readonly PostgreSqlContainer _container = DatabaseProvisioning.FastBuilder().Build();
    private string _adminMaintenance = "";

    public PostgreSqlContainer Container => _container;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _adminMaintenance = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = "postgres" }.ConnectionString;
        await DatabaseProvisioning.ProvisionAsync(_container, EmptyTemplate);
        await DetachAsync(EmptyTemplate);

        var preClosureOwner = await CloneTemplateAsync(EmptyTemplate, PreClosureTemplate);
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(preClosureOwner).Options))
            await db.GetService<IMigrator>().MigrateAsync("202611180001_SettingsAdministrativeData");
        await DetachAsync(PreClosureTemplate);

        var latestOwner = await CloneTemplateAsync(EmptyTemplate, LatestTemplate);
        await DatabaseProvisioning.MigrateAsync(latestOwner);
        await DetachAsync(LatestTemplate);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<(string Owner, string Admin)> ResetTargetFromTemplateAsync(string templateName)
    {
        await DropTargetAsync();
        var owner = await CloneTemplateAsync(templateName, "teapremium_test");
        var admin = new NpgsqlConnectionStringBuilder(_adminMaintenance) { Database = "teapremium_test" }.ConnectionString;
        return (owner, admin);
    }

    public async Task DropTargetAsync()
    {
        ClearPools("teapremium_test");
        await using var connection = new NpgsqlConnection(_adminMaintenance);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DROP DATABASE IF EXISTS teapremium_test WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> CloneTemplateAsync(string templateName, string targetName)
    {
        await using var connection = new NpgsqlConnection(_adminMaintenance);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{targetName}\" TEMPLATE \"{templateName}\" STRATEGY = FILE_COPY OWNER tp_owner", connection))
            await create.ExecuteNonQueryAsync();
        await using (var grants = new NpgsqlCommand(
            $"REVOKE ALL ON DATABASE \"{targetName}\" FROM PUBLIC; GRANT CONNECT ON DATABASE \"{targetName}\" TO tp_app, tp_readonly;",
            connection))
            await grants.ExecuteNonQueryAsync();
        return DatabaseProvisioning.Login(_adminMaintenance, "tp_owner", targetName);
    }

    private async Task DetachAsync(string database)
    {
        ClearPools(database);
        await using var connection = new NpgsqlConnection(_adminMaintenance);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid()",
            connection);
        command.Parameters.AddWithValue("name", database);
        await command.ExecuteNonQueryAsync();
    }

    private void ClearPools(string database)
    {
        foreach (var role in new[] { "tp_owner", "tp_app", "tp_readonly" })
        {
            using var conn = new NpgsqlConnection(DatabaseProvisioning.Login(_adminMaintenance, role, database));
            NpgsqlConnection.ClearPool(conn);
        }
        using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_adminMaintenance) { Database = database }.ConnectionString);
        NpgsqlConnection.ClearPool(admin);
    }
}

public sealed class DatabaseRolesObjectDelegationTests(DatabaseRoleTemplateFixture fixture) : IClassFixture<DatabaseRoleTemplateFixture>
{
    public static IEnumerable<object[]> ObjectDelegationCases()
    {
        // DEC-30/A01 D11: allowed privileges must never imply delegation.
        var cases = new (string Object, string Role, string Privilege, bool Sequence)[]
        {
            ("platform.data_protection_keys", "tp_readonly", "SELECT", false),
            ("iam.staff_users", "tp_readonly", "SELECT", false),
            ("public.\"__EFMigrationsHistory\"", "tp_readonly", "SELECT", false),
            ("platform.data_protection_keys_id_seq", "tp_readonly", "SELECT", true),
            ("platform.settings", "tp_app", "SELECT", false),
            ("platform.settings", "tp_app", "UPDATE", false),
            ("platform.settings", "tp_app", "DELETE", false),
            ("platform.audit_logs", "tp_app", "INSERT", false),
            ("platform.administrative_data_imports", "tp_app", "INSERT", false),
            ("platform.data_protection_keys_id_seq", "tp_app", "USAGE", true)
        };
        foreach (var item in cases)
            foreach (var closurePending in new[] { false, true })
                yield return [item.Object, item.Role, item.Privilege, item.Sequence, closurePending];
    }

    [Theory]
    [MemberData(nameof(ObjectDelegationCases))]
    public async Task ObjectGrantOptionDriftStopsBeforeMigrationWithoutRepair(
        string relation, string role, string privilege, bool sequence, bool closurePending)
    {
        var (owner, _) = await fixture.ResetTargetFromTemplateAsync(
            closurePending ? DatabaseRoleTemplateFixture.PreClosureTemplate : DatabaseRoleTemplateFixture.LatestTemplate);
        try
        {
            await using var connection = new NpgsqlConnection(owner);
            await connection.OpenAsync();
            var privilegeFunction = sequence ? "has_sequence_privilege" : "has_table_privilege";
            // These are fixed theory values, never identifiers from external input.
            if (!closurePending)
            {
                Assert.Equal(true, await DatabaseRolesTests.ScalarAsync(connection, $"SELECT {privilegeFunction}('{role}','{relation}','{privilege}')"));
                Assert.Equal(false, await DatabaseRolesTests.ScalarAsync(connection, $"SELECT {privilegeFunction}('{role}','{relation}','{privilege} WITH GRANT OPTION')"));
            }
            await DatabaseRolesTests.ExecuteAsync(owner, $"GRANT {privilege} ON {(sequence ? "SEQUENCE" : "TABLE")} {relation} TO {role} WITH GRANT OPTION");
            Assert.Equal(true, await DatabaseRolesTests.ScalarAsync(connection, $"""
SELECT a.is_grantable FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
WHERE c.oid='{relation}'::regclass AND a.grantee='{role}'::regrole AND a.privilege_type='{privilege}'
"""));
            Assert.Equal(true, await DatabaseRolesTests.ScalarAsync(connection, $"SELECT {privilegeFunction}('{role}','{relation}','{privilege} WITH GRANT OPTION')"));
            await DatabaseRolesTests.AssertDelegationRejectedWithoutRepairAsync(fixture.Container, owner, closurePending, "Unexpected runtime/backup object privileges");
        }
        finally
        {
            await fixture.DropTargetAsync();
        }
    }
}

public sealed class DatabaseRolesDatabaseDriftTests(DatabaseRoleTemplateFixture fixture) : IClassFixture<DatabaseRoleTemplateFixture>
{
    [Theory]
    [InlineData("ALTER DATABASE teapremium_test OWNER TO postgres; GRANT CONNECT ON DATABASE teapremium_test TO tp_owner", "Unexpected database owner")]
    [InlineData("CREATE SCHEMA unexpected AUTHORIZATION postgres", "Unexpected schema owner")]
    [InlineData("CREATE TABLE public.unexpected(id int)", "Unexpected object owner")]
    [InlineData("CREATE EXTENSION hstore", "Unexpected extension owner")]
    [InlineData("GRANT CREATE ON SCHEMA public TO tp_app", "unexpected CREATE")]
    [InlineData("GRANT USAGE ON SCHEMA public TO tp_app WITH GRANT OPTION", "Unexpected runtime/backup schema grant option")]
    [InlineData("GRANT USAGE ON SCHEMA public TO tp_readonly WITH GRANT OPTION", "Unexpected runtime/backup schema grant option")]
    [InlineData("SET ROLE tp_owner; CREATE SCHEMA platform; GRANT USAGE ON SCHEMA platform TO tp_app WITH GRANT OPTION", "Unexpected runtime/backup schema grant option")]
    [InlineData("SET ROLE tp_owner; CREATE SCHEMA iam; GRANT USAGE ON SCHEMA iam TO tp_readonly WITH GRANT OPTION", "Unexpected runtime/backup schema grant option")]
    [InlineData("SET ROLE tp_owner; CREATE TABLE platform_placeholder(id int); GRANT TRUNCATE ON platform_placeholder TO tp_app", "Unexpected runtime/backup")]
    [InlineData("SET ROLE tp_owner; CREATE TABLE platform_placeholder(id int); GRANT UPDATE(id) ON platform_placeholder TO tp_app", "Unexpected column grants")]
    [InlineData("ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner GRANT INSERT ON TABLES TO tp_app", "Unexpected default privileges")]
    [InlineData("GRANT TEMP ON DATABASE teapremium_test TO tp_app", "unexpected CREATE")]
    [InlineData("SET ROLE tp_owner; CREATE SEQUENCE public.unexpected_seq; GRANT UPDATE ON SEQUENCE public.unexpected_seq TO tp_app", "Unexpected runtime/backup object")]
    [InlineData("SET ROLE tp_owner; CREATE FUNCTION public.unexpected_function() RETURNS int LANGUAGE sql AS 'SELECT 1'; GRANT EXECUTE ON FUNCTION public.unexpected_function() TO tp_app WITH GRANT OPTION", "Unexpected runtime/backup function")]
    public async Task PreflightFailsBeforeAnyMigrationDdl(string mutation, string expected)
    {
        var (_, admin) = await fixture.ResetTargetFromTemplateAsync(DatabaseRoleTemplateFixture.EmptyTemplate);
        try
        {
            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(mutation, connection);
                await command.ExecuteNonQueryAsync();
            }
            var result = await DatabaseRolesTests.RunToolAsync(fixture.Container, preflightOnly: false);
            Assert.NotEqual(0, result.Code);
            Assert.Contains(expected, result.Output, StringComparison.Ordinal);
            await using var verify = new NpgsqlConnection(admin);
            await verify.OpenAsync();
            Assert.Equal(DBNull.Value, await DatabaseRolesTests.ScalarAsync(verify, "SELECT to_regclass('public.\"__EFMigrationsHistory\"')::text"));
            var provision = await fixture.Container.ExecAsync(["sh", "/opt/teapremium/init-roles.sh"]);
            Assert.NotEqual(0, provision.ExitCode);
        }
        finally
        {
            await fixture.DropTargetAsync();
        }
    }

    [Theory]
    [InlineData("platform", "tp_app")]
    [InlineData("platform", "tp_readonly")]
    [InlineData("iam", "tp_app")]
    [InlineData("iam", "tp_readonly")]
    [InlineData("public", "tp_app")]
    [InlineData("public", "tp_readonly")]
    public async Task ExistingDatabaseSchemaDelegationDriftStopsWithoutRepair(string schema, string role)
    {
        var (owner, _) = await fixture.ResetTargetFromTemplateAsync(DatabaseRoleTemplateFixture.LatestTemplate);
        try
        {
            // Schema/role values are only this fixed theory's identifiers.
            await DatabaseRolesTests.ExecuteAsync(owner, $"GRANT USAGE ON SCHEMA {schema} TO {role} WITH GRANT OPTION");
            var before = await DatabaseRolesTests.FingerprintAsync(owner);
            var provisioning = await fixture.Container.ExecAsync(["sh", "/opt/teapremium/init-roles.sh"]);
            Assert.NotEqual(0, provisioning.ExitCode);
            var migration = await DatabaseRolesTests.RunToolAsync(fixture.Container, preflightOnly: false);
            Assert.NotEqual(0, migration.Code);
            Assert.Contains("Unexpected runtime/backup schema grant option", migration.Output, StringComparison.Ordinal);
            Assert.Equal(before, await DatabaseRolesTests.FingerprintAsync(owner));
            await using var connection = new NpgsqlConnection(owner);
            await connection.OpenAsync();
            Assert.Equal(6L, await DatabaseRolesTests.ScalarAsync(connection, "SELECT count(*) FROM public.\"__EFMigrationsHistory\""));
            Assert.Equal(true, await DatabaseRolesTests.ScalarAsync(connection, $"""
SELECT a.is_grantable FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) a
JOIN pg_roles gr ON gr.oid=a.grantee WHERE n.nspname='{schema}' AND gr.rolname='{role}' AND a.privilege_type='USAGE'
"""));
        }
        finally
        {
            await fixture.DropTargetAsync();
        }
    }
}

public sealed class DatabaseRolesClusterPreflightTests
{
    [Theory]
    [InlineData("ALTER ROLE tp_readonly RENAME TO missing_backup", "Missing role")]
    [InlineData("ALTER ROLE tp_app CREATEDB", "Unexpected attributes")]
    [InlineData("GRANT tp_readonly TO tp_app", "Unexpected role membership")]
    [InlineData("CREATE ROLE closure_delegate; GRANT USAGE ON SCHEMA public TO closure_delegate WITH GRANT OPTION; GRANT closure_delegate TO tp_app", "Unexpected role membership")]
    public async Task ClusterRolePreflightFailsBeforeAnyMigrationDdl(string mutation, string expected)
    {
        await using var container = DatabaseProvisioning.FastBuilder().Build();
        await container.StartAsync();
        await DatabaseProvisioning.ProvisionAsync(container, "teapremium_test");
        var admin = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = "teapremium_test" }.ConnectionString;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(mutation, connection);
            await command.ExecuteNonQueryAsync();
        }
        var result = await DatabaseRolesTests.RunToolAsync(container, preflightOnly: false);
        Assert.NotEqual(0, result.Code);
        Assert.Contains(expected, result.Output, StringComparison.Ordinal);
        await using var verify = new NpgsqlConnection(admin);
        await verify.OpenAsync();
        Assert.Equal(DBNull.Value, await DatabaseRolesTests.ScalarAsync(verify, "SELECT to_regclass('public.\"__EFMigrationsHistory\"')::text"));
        // Missing role is a supported provisioning repair; other drift must stop.
        if (!mutation.Contains("RENAME", StringComparison.Ordinal))
        {
            var provision = await container.ExecAsync(["sh", "/opt/teapremium/init-roles.sh"]);
            Assert.NotEqual(0, provision.ExitCode);
        }
    }

    [Theory]
    [InlineData("tp_app", false)]
    [InlineData("tp_app", true)]
    [InlineData("tp_readonly", false)]
    [InlineData("tp_readonly", true)]
    public async Task InheritedObjectDelegationIsRejectedBeforeMigration(string role, bool closurePending)
    {
        await using var container = DatabaseProvisioning.FastBuilder().Build();
        var owner = await DatabaseRolesTests.PrepareDelegationDatabaseAsync(container, closurePending);
        var admin = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = "teapremium_test" }.ConnectionString;
        await DatabaseRolesTests.ExecuteAsync(admin, $"""
CREATE ROLE closure_delegate NOLOGIN;
GRANT SELECT ON platform.data_protection_keys TO closure_delegate WITH GRANT OPTION;
GRANT closure_delegate TO {role} WITH INHERIT TRUE;
""");
        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync();
        Assert.Equal(0L, await DatabaseRolesTests.ScalarAsync(connection, $"""
SELECT count(*) FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
WHERE c.oid='platform.data_protection_keys'::regclass AND a.grantee='{role}'::regrole AND a.is_grantable
"""));
        Assert.Equal(true, await DatabaseRolesTests.ScalarAsync(connection, $"SELECT has_table_privilege('{role}','platform.data_protection_keys','SELECT WITH GRANT OPTION')"));
        await DatabaseRolesTests.AssertDelegationRejectedWithoutRepairAsync(container, owner, closurePending, "Unexpected role membership");
    }
}

public sealed class DatabaseRolesCredentialProtectionTests
{
    [Fact]
    public async Task FreshVolumeInitHookUsesRealWrapper()
    {
        await using var container = DatabaseProvisioning.FastBuilder()
            .WithResourceMapping(new FileInfo(Path.Combine(DatabaseProvisioning.Artifacts, "init-roles.sh")), "/docker-entrypoint-initdb.d/")
            .Build();
        await container.StartAsync();
        var owner = DatabaseProvisioning.Login(container.GetConnectionString(), "tp_owner", "teapremium_test");
        await DatabaseProvisioning.MigrateAsync(owner);
        await using var connection = new NpgsqlConnection(DatabaseProvisioning.Login(owner, "tp_app"));
        await connection.OpenAsync();
        Assert.Equal(6L, await DatabaseRolesTests.ScalarAsync(connection, "SELECT count(*) FROM public.\"__EFMigrationsHistory\""));
    }

    [Fact]
    public async Task PasswordPunctuationWorksWithoutEchoingCredentials()
    {
        var secret = Guid.NewGuid().ToString("N") + "';$\\\"!";
        await using var container = DatabaseProvisioning.Builder()
            .WithEnvironment("TP_OWNER_PASSWORD", secret)
            .WithEnvironment("TP_APP_PASSWORD", secret)
            .WithEnvironment("TP_READONLY_PASSWORD", secret)
            .WithCommand("-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off",
                "-c", "log_statement=all", "-c", "log_min_error_statement=error")
            .Build();
        await container.StartAsync();
        var result = await container.ExecAsync(["sh", "/opt/teapremium/init-roles.sh"]);
        Assert.Equal(0, result.ExitCode);
        foreach (var role in new[] { "tp_owner", "tp_app", "tp_readonly" })
        {
            var login = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            { Database = "teapremium_test", Username = role, Password = secret };
            await using var connection = new NpgsqlConnection(login.ConnectionString);
            await connection.OpenAsync();
        }
        var logs = await container.GetLogsAsync();
        Assert.True(!(result.Stdout + result.Stderr + logs.Stdout + logs.Stderr).Contains(secret, StringComparison.Ordinal),
            "Provisioning must not expose a credential in console/server logs.");
    }

    [Theory]
    [InlineData("repeat")]
    [InlineData("shell_trace")]
    [InlineData("read_only")]
    [InlineData("unprivileged")]
    [InlineData("verification")]
    public async Task CredentialProtectionFailsClosedAndSurvivesAggressiveLogging(string scenario)
    {
        // The unique marker detects raw AND SQL-escaped passwords without ever
        // including a credential in assertion output/evidence on failure.
        var marker = Guid.NewGuid().ToString("N");
        var passwords = new[] { marker + "-owner-';$\"!", marker + "-app-';$\"!", marker + "-backup-';$\"!" };
        await using var container = DatabaseProvisioning.Builder()
            .WithEnvironment("TP_OWNER_PASSWORD", passwords[0])
            .WithEnvironment("TP_APP_PASSWORD", passwords[1])
            .WithEnvironment("TP_READONLY_PASSWORD", passwords[2])
            .WithCommand("-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off",
                "-c", "log_statement=all", "-c", "log_duration=on",
                "-c", "log_min_duration_statement=0", "-c", "log_min_duration_sample=0",
                "-c", "log_statement_sample_rate=1", "-c", "log_transaction_sample_rate=1",
                "-c", "log_min_error_statement=error")
            .Build();
        await container.StartAsync();
        if (scenario is not ("repeat" or "shell_trace"))
        {
            string[] command;
            if (scenario == "unprivileged")
            {
                await DatabaseRolesTests.ExecuteAsync(container.GetConnectionString(), "CREATE ROLE closure_no_config LOGIN NOINHERIT");
                command = ["env", "PGUSER=closure_no_config", "sh", "/opt/teapremium/init-roles.sh"];
            }
            else if (scenario == "verification")
            {
                // Fixture-only fault: SET succeeds but leaves an unsafe effective
                // setting. The real guard must reject it before credential SQL.
                command = ["sh", "-c", """
cp -R /opt/teapremium /tmp/blocked
sed -i "s/SET log_statement = 'none';/SET log_statement = 'all';/" /tmp/blocked/protect-credentials.sql
export PGUSER="$POSTGRES_USER" PGDATABASE=postgres PGPASSWORD="$POSTGRES_PASSWORD"
exec psql -X -q -v ON_ERROR_STOP=1 -f /tmp/blocked/init-roles.sql
"""];
            }
            else
            {
                command = ["env", "PGOPTIONS=-c default_transaction_read_only=on", "sh", "/opt/teapremium/init-roles.sh"];
            }
            var failure = await container.ExecAsync(command);
            Assert.True(failure.ExitCode != 0, "Unsafe/failed provisioning must fail closed.");
            if (scenario == "verification")
                Assert.Contains("Credential logging protection could not be verified", failure.Stderr, StringComparison.Ordinal);
            AssertCredentialOutputIsSafe(marker, failure.Stdout + failure.Stderr);
            await using var admin = new NpgsqlConnection(container.GetConnectionString());
            await admin.OpenAsync();
            Assert.Equal(0L, await DatabaseRolesTests.ScalarAsync(admin, "SELECT count(*) FROM pg_roles WHERE rolname IN ('tp_owner','tp_app','tp_readonly')"));
        }
        var first = await container.ExecAsync(scenario == "shell_trace"
            ? ["sh", "-x", "/opt/teapremium/init-roles.sh"] : ["sh", "/opt/teapremium/init-roles.sh"]);
        Assert.True(first.ExitCode == 0, "Protected provisioning must succeed.");
        var before = await DatabaseRolesTests.PasswordFingerprintAsync(container);
        var repeat = await container.ExecAsync(["sh", "-c", """
export TP_OWNER_PASSWORD="$TP_OWNER_PASSWORD-changed" TP_APP_PASSWORD="$TP_APP_PASSWORD-changed" TP_READONLY_PASSWORD="$TP_READONLY_PASSWORD-changed"
exec sh /opt/teapremium/init-roles.sh
"""]);
        Assert.True(repeat.ExitCode == 0, "Protected existing-database provisioning must succeed.");
        Assert.Equal(before, await DatabaseRolesTests.PasswordFingerprintAsync(container));
        var roles = new[] { "tp_owner", "tp_app", "tp_readonly" };
        for (var index = 0; index < roles.Length; index++)
        {
            var login = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            { Database = "teapremium_test", Username = roles[index], Password = passwords[index] };
            await using var connection = new NpgsqlConnection(login.ConnectionString);
            await connection.OpenAsync();
        }
        await using (var admin = new NpgsqlConnection(container.GetConnectionString()))
        {
            await admin.OpenAsync();
            Assert.Equal(true, await DatabaseRolesTests.ScalarAsync(admin, """
SELECT current_setting('log_statement')='all' AND current_setting('log_duration')='on'
 AND current_setting('log_min_duration_statement')='0' AND current_setting('log_min_duration_sample')='0'
 AND current_setting('log_statement_sample_rate')='1' AND current_setting('log_transaction_sample_rate')='1'
 AND current_setting('log_min_error_statement')='error'
"""));
        }
        var logs = await container.GetLogsAsync();
        AssertCredentialOutputIsSafe(marker, first.Stdout + first.Stderr + repeat.Stdout + repeat.Stderr + logs.Stdout + logs.Stderr);
    }

    private static void AssertCredentialOutputIsSafe(string marker, string output)
    {
        Assert.True(!output.Contains(marker, StringComparison.Ordinal), "Credential marker must never appear in client/server logs.");
        Assert.True(!output.Contains("AS desired(name,password)", StringComparison.Ordinal)
            && !output.Contains("CREATE ROLE tp_", StringComparison.Ordinal)
            && !output.Contains("CREATE ROLE \"tp_", StringComparison.Ordinal), "Credential-bearing SQL must never appear in logs.");
    }
}

public sealed class DatabaseRolesDevMigrateCheckoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevMigrateRestoresCleanCheckoutAndStopsOnLockedRestoreFailure(bool brokenLock)
    {
        await using var container = DatabaseProvisioning.FastBuilder().Build();
        await container.StartAsync();
        await DatabaseProvisioning.ProvisionAsync(container, "teapremium_test");
        var source = DatabaseRolesTests.RepositoryRoot();
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var checkout = Path.GetFullPath(Path.Combine(tempRoot, "closure-clean-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(tempRoot, checkout, StringComparison.Ordinal);
        Directory.CreateDirectory(checkout);
        try
        {
            foreach (var path in new[] { "backend", ".config", "infra/postgres" })
                CopySourceDirectory(Path.Combine(source, path), Path.Combine(checkout, path));
            Directory.CreateDirectory(Path.Combine(checkout, "scripts"));
            File.Copy(Path.Combine(source, "scripts", "dev-migrate.ps1"), Path.Combine(checkout, "scripts", "dev-migrate.ps1"));
            File.Copy(Path.Combine(source, "global.json"), Path.Combine(checkout, "global.json"));
            File.Copy(Path.Combine(source, ".editorconfig"), Path.Combine(checkout, ".editorconfig"));
            Assert.Empty(Directory.EnumerateDirectories(checkout, "bin", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateDirectories(checkout, "obj", SearchOption.AllDirectories));
            var preflight = await DatabaseRolesTests.RunToolAsync(container, preflightOnly: true, checkout);
            Assert.True(preflight.Code == 0, preflight.Output);
            Assert.Empty(Directory.EnumerateFiles(checkout, "project.assets.json", SearchOption.AllDirectories));
            if (brokenLock)
            {
                // Change a requested locked dependency, leaving the project unchanged.
                var lockPath = Path.Combine(checkout, "backend", "src", "TeaPremium.Infrastructure", "packages.lock.json");
                var content = await File.ReadAllTextAsync(lockPath);
                Assert.Contains("[10.0.15, )", content, StringComparison.Ordinal);
                await File.WriteAllTextAsync(lockPath, content.Replace("[10.0.15, )", "[10.0.14, )", StringComparison.Ordinal));
            }
            var first = await DatabaseRolesTests.RunToolAsync(container, preflightOnly: false, checkout);
            var owner = DatabaseProvisioning.Login(container.GetConnectionString(), "tp_owner", "teapremium_test");
            await using var connection = new NpgsqlConnection(owner);
            await connection.OpenAsync();
            if (brokenLock)
            {
                Assert.NotEqual(0, first.Code);
                Assert.Contains("Infrastructure locked restore failed", first.Output, StringComparison.Ordinal);
                Assert.Equal(DBNull.Value, await DatabaseRolesTests.ScalarAsync(connection, "SELECT to_regclass('public.\"__EFMigrationsHistory\"')::text"));
            }
            else
            {
                Assert.True(first.Code == 0, first.Output);
                var second = await DatabaseRolesTests.RunToolAsync(container, preflightOnly: false, checkout);
                Assert.True(second.Code == 0, second.Output);
                Assert.Equal(6L, await DatabaseRolesTests.ScalarAsync(connection, "SELECT count(*) FROM public.\"__EFMigrationsHistory\""));
            }
        }
        finally
        {
            // Only the unique test-owned temporary checkout, never the source tree.
            Directory.Delete(checkout, recursive: true);
        }
    }

    private static void CopySourceDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            if (Path.GetFileName(directory) is not ("bin" or "obj"))
                CopySourceDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
