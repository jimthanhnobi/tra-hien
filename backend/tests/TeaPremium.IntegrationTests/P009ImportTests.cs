using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Settings;
using TeaPremium.Application.UseCases;

namespace TeaPremium.IntegrationTests;

public sealed class P009ImportTests(PlatformDatabaseFixture fixture) : IClassFixture<PlatformDatabaseFixture>
{
    private const string Header = "province_code,province_name,ward_code,ward_name\n";

    [Theory]
    [InlineData("missing")]
    [InlineData("revoked")]
    [InlineData("inactive")]
    [InlineData("nonowner")]
    public async Task ApplyRevalidatesAuthenticatedOwnerSessionBeforeAnyMutation(string scenario)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var bundle = Bundle(1, Header + "01,Tỉnh,,\n");
        var execution = h.Approve(bundle, await h.PlanAsync(bundle));
        if (scenario == "missing") execution = execution with { OperatorStamp = null };
        if (scenario == "revoked") await h.ExecuteAsync("UPDATE iam.staff_users SET security_stamp='revoked'");
        if (scenario == "inactive") await h.ExecuteAsync("UPDATE iam.staff_users SET is_active=false");
        if (scenario == "nonowner") await h.ExecuteAsync("DELETE FROM iam.user_roles");
        var result = await h.ApplyAsync(bundle, execution);
        Assert.False(result.IsSuccess);
        Assert.Equal("operator_not_authorized", result.Error.Code);
        await using var db = h.OwnerContext();
        Assert.Empty(await db.Provinces.ToArrayAsync());
        Assert.Empty(await db.AdministrativeDataImports.ToArrayAsync());
    }

    [Fact]
    public async Task WardSqlFailureRollsBackProvincesAlreadySavedInTheSameCommand()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var bundle = Bundle(1, Header + "01,Tỉnh,,\n01,Tỉnh,00001,Xã\n");
        var execution = h.Approve(bundle, await h.PlanAsync(bundle));
        await h.ExecuteAsync("CREATE FUNCTION platform.p009_fail_ward() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected ward SQL failure'; END $$; CREATE TRIGGER p009_fail_ward BEFORE INSERT ON platform.wards FOR EACH ROW EXECUTE FUNCTION platform.p009_fail_ward()");
        await Assert.ThrowsAsync<DbUpdateException>(() => h.ApplyAsync(bundle, execution));
        await using var db = h.OwnerContext();
        Assert.Empty(await db.Provinces.ToArrayAsync());
        Assert.Empty(await db.Wards.ToArrayAsync());
        Assert.Empty(await db.AdministrativeDataImports.ToArrayAsync());
    }

    [Fact]
    public async Task ReferenceImpactChangedAfterDryRunRejectsThePlanWithoutRewritingHistory()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var first = await Apply(h, Bundle(1, Header + "01,Tỉnh,,\n01,Tỉnh,00001,Xã cũ\n"));
        await h.ExecuteAsync("INSERT INTO crm.customers(id,code,full_name,phone_e164,origin) VALUES ('11111111-1111-1111-1111-111111111110','KH009001','P009 Fixture','+84900009001','admin')");
        var bundle = Bundle(2, Header + "01,Tỉnh,,\n01,Tỉnh,00001,Xã mới\n", first);
        var execution = h.Approve(bundle, await h.PlanAsync(bundle));
        await h.ExecuteAsync("INSERT INTO crm.customer_addresses(id,customer_id,receiver_name,receiver_phone,province_code,ward_code,address_line) VALUES('11111111-1111-1111-1111-111111111111','11111111-1111-1111-1111-111111111110','Receiver','+84900009002','01','00001','1 St')");
        var result = await h.ApplyAsync(bundle, execution);
        Assert.False(result.IsSuccess);
        Assert.Equal("stale_plan", result.Error.Code);
        await using var db = h.OwnerContext();
        Assert.Equal("Xã cũ", (await db.Wards.SingleAsync()).Name);
        Assert.Equal(1, await db.AdministrativeDataImports.CountAsync());
        Assert.Equal("00001", await db.Database.SqlQueryRaw<string>("SELECT ward_code AS \"Value\" FROM crm.customer_addresses").SingleAsync());
    }

    [Fact]
    public async Task CancellationAfterProvinceSaveRollsBackAndReleasesImportLock()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var bundle = Bundle(1, Header + "01,Tỉnh,,\n01,Tỉnh,00001,Xã\n");
        var execution = h.Approve(bundle, await h.PlanAsync(bundle));
        await h.ExecuteAsync("CREATE FUNCTION platform.p009_wait_ward() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_sleep(10); RETURN NEW; END $$; CREATE TRIGGER p009_wait_ward BEFORE INSERT ON platform.wards FOR EACH ROW EXECUTE FUNCTION platform.p009_wait_ward()");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var scope = h.Factory.Services.CreateAsyncScope();
        var pending = scope.ServiceProvider.GetRequiredService<ICommandHandler<ApplyAdministrativeImport, ImportOutcome>>()
            .Handle(new(bundle, execution), cancellation.Token);
        await using var db = h.OwnerContext();
        var sleeping = false;
        try
        {
            for (var attempt = 0; attempt < 100 && !sleeping && !pending.IsCompleted; attempt++)
            {
                sleeping = await fixture.ObserveSleepingAsync(h.Database);
                if (!sleeping) await Task.Delay(25);
            }
        }
        finally { await cancellation.CancelAsync(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(sleeping, "Cancellation must occur after province SaveChanges, inside the ward SQL.");
        Assert.Empty(await db.Provinces.ToArrayAsync());
        Assert.Empty(await db.Wards.ToArrayAsync());
        Assert.Empty(await db.AdministrativeDataImports.ToArrayAsync());
        await using var connection = new NpgsqlConnection(h.RuntimeConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(8300,9)", connection, transaction);
        Assert.Equal(true, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("bad-operation", "23514")]
    [InlineData("bad-hash", "23514")]
    [InlineData("recovery-genesis", "23514")]
    [InlineData("self-parent", "23514")]
    [InlineData("missing-parent", "23503")]
    [InlineData("duplicate-genesis", "23505")]
    public async Task LedgerConstraintsAreEnforcedByPostgres(string scenario, string state)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await using var connection = new NpgsqlConnection(h.RuntimeConnection); await connection.OpenAsync();
        var id = Guid.NewGuid();
        if (scenario == "duplicate-genesis")
        {
            await using var first = new NpgsqlCommand(InsertEventSql, connection);
            EventParameters(first, Guid.NewGuid(), null, "import", new string('a', 64), null);
            await first.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(InsertEventSql, connection);
        EventParameters(command, id, scenario == "self-parent" ? id : scenario == "missing-parent" ? Guid.NewGuid() : null,
            scenario == "bad-operation" ? "bad" : scenario == "recovery-genesis" ? "recovery" : "import",
            scenario == "bad-hash" ? "invalid" : new string('a', 64), scenario == "recovery-genesis" ? id : null);
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }

    [Theory]
    [InlineData("recovery")]
    [InlineData("re_advance")]
    public async Task FailedRecoveryAndReadvanceRollBackCatalogAndHistory(string operation)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var a = Bundle(1, Header + "01,Tỉnh A,,\n"); var e1 = await Apply(h, a);
        var b = Bundle(2, Header + "01,Tỉnh B,,\n", e1); var e2 = await Apply(h, b);
        var recovery = With(a, a.Manifest() with
        {
            BaseImportId = e2,
            Operation = "recovery",
            RecoveryOf = e2,
            TargetImportId = e1,
            Reason = "correction",
            IncidentRecord = "incident:test",
            ReAdvancePath = [e2]
        });
        var current = recovery;
        if (operation == "re_advance")
        {
            var e3 = await Apply(h, recovery);
            current = With(b, b.Manifest() with { BaseImportId = e3, Operation = "re_advance", ReAdvanceOf = e2, RecoveryEventId = e3, PathPosition = 0 });
        }
        var plan = await h.PlanAsync(current); var execution = h.Approve(current, plan);
        await h.ExecuteAsync("CREATE FUNCTION platform.p009_fail_recovery() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected recovery failure'; END $$; CREATE TRIGGER p009_fail_recovery BEFORE INSERT ON platform.administrative_data_imports FOR EACH ROW EXECUTE FUNCTION platform.p009_fail_recovery()");
        await Assert.ThrowsAsync<DbUpdateException>(() => h.ApplyAsync(current, execution));
        await using var db = h.OwnerContext();
        Assert.Equal(operation == "recovery" ? 2 : 3, await db.AdministrativeDataImports.CountAsync());
        Assert.Equal(operation == "recovery" ? "Tỉnh B" : "Tỉnh A", (await db.Provinces.SingleAsync()).Name);
        await h.ExecuteAsync("DROP TRIGGER p009_fail_recovery ON platform.administrative_data_imports");
        Assert.True((await h.ApplyAsync(current, execution)).IsSuccess);
    }

    [Fact]
    public async Task ActualProductionEnvironmentCannotSelectLocalTestProfile()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var production = new TestEnvironment { EnvironmentName = "Production" };
        var guard = new TeaPremium.Infrastructure.AdministrativeData.ImportEnvironment(h.Configuration, production);
        Assert.Equal("Production", guard.Profile);
        Assert.Equal("production_import_hold", Assert.Throws<ImportRejectedException>(() => guard.Target(h.RuntimeConnection)).Code);
    }

    private const string InsertEventSql = "INSERT INTO platform.administrative_data_imports(id,previous_import_id,dataset_version,release_order,effective_on,operation,manifest,manifest_sha256,csv_sha256,plan_sha256,catalog_sha256,receipt,applied_at,recovery_of) VALUES(@id,@prev,'test',1,'2026-01-01',@op,'{}',@hash,@hash,@hash,@hash,'{}',now(),@recovery)";
    private static void EventParameters(NpgsqlCommand command, Guid id, Guid? previous, string operation, string hash, Guid? recovery)
    {
        command.Parameters.AddWithValue("id", id); command.Parameters.Add(new("prev", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)previous ?? DBNull.Value });
        command.Parameters.AddWithValue("op", operation); command.Parameters.AddWithValue("hash", hash);
        command.Parameters.Add(new("recovery", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)recovery ?? DBNull.Value });
    }
    private sealed class TestEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task RestrictedRuntimeRoleOwnsTransactionLockButCannotRewriteLedger()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        await using var a = new NpgsqlConnection(h.RuntimeConnection); await a.OpenAsync();
        await using var b = new NpgsqlConnection(h.RuntimeConnection); await b.OpenAsync();
        await using (var identity = new NpgsqlCommand("SELECT current_user,session_user,rolsuper,pg_has_role(current_user,'tp_owner','MEMBER') FROM pg_roles WHERE rolname=current_user", a))
        await using (var reader = await identity.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync()); Assert.Equal("tp_app", reader.GetString(0)); Assert.Equal("tp_app", reader.GetString(1));
            Assert.False(reader.GetBoolean(2)); Assert.False(reader.GetBoolean(3));
        }
        foreach (var privilege in new[] { "UPDATE", "DELETE", "TRUNCATE", "MAINTAIN" })
        {
            await using var check = new NpgsqlCommand("SELECT has_table_privilege(current_user,'platform.administrative_data_imports',@p)", a);
            check.Parameters.AddWithValue("p", privilege); Assert.Equal(false, await check.ExecuteScalarAsync());
        }
        await using (var tx = await a.BeginTransactionAsync())
        {
            await using var first = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(8300,9)", a, tx); Assert.Equal(true, await first.ExecuteScalarAsync());
            await using var secondTx = await b.BeginTransactionAsync();
            await using var second = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(8300,9)", b, secondTx); Assert.Equal(false, await second.ExecuteScalarAsync());
            await tx.RollbackAsync(); Assert.Equal(true, await second.ExecuteScalarAsync()); await secondTx.CommitAsync();
        }
        foreach (var sql in new[] { "UPDATE platform.administrative_data_imports SET dataset_version='bad'", "DELETE FROM platform.administrative_data_imports", "TRUNCATE platform.administrative_data_imports" })
        {
            await using var command = new NpgsqlCommand(sql, a);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
    }

    [Fact]
    public async Task DryRunZeroWritesApplyAndCurrentTailNoopAreRealPostgres()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var bundle = Bundle(1, Header + "01,Thành phố Hà Nội,,\n01,Thành phố Hà Nội,00004,Phường Ba Đình\n02,Tỉnh khác,,\n");
        var plan = await h.PlanAsync(bundle);
        await using var db = h.OwnerContext();
        Assert.Empty(await db.Provinces.ToArrayAsync()); Assert.Empty(await db.AdministrativeDataImports.ToArrayAsync());
        var auditCount = await db.AuditLogs.CountAsync();
        await using var readinessScope = h.Factory.Services.CreateAsyncScope();
        var references = readinessScope.ServiceProvider.GetRequiredService<IAdministrativeReferenceData>();
        Assert.False(await references.IsReadyAsync(default));
        var execution = h.Approve(bundle, plan);
        var applied = await h.ApplyAsync(bundle, execution); Assert.True(applied.IsSuccess, applied.IsSuccess ? "" : applied.Error.Code);
        Assert.Equal("applied", applied.Value.Status);
        var row = await db.AdministrativeDataImports.SingleAsync();
        var noop = await h.ApplyAsync(bundle, execution); Assert.True(noop.IsSuccess, noop.IsSuccess ? "" : noop.Error.Code); Assert.Equal("no_op", noop.Value.Status);
        Assert.Equal(1, await db.AdministrativeDataImports.CountAsync()); Assert.Equal(row.AppliedAt, (await db.AdministrativeDataImports.AsNoTracking().SingleAsync()).AppliedAt);
        Assert.Equal(auditCount, await db.AuditLogs.CountAsync());
        Assert.Equal("01", (await db.Wards.SingleAsync()).ProvinceCode); Assert.Equal("00004", (await db.Wards.SingleAsync()).Code);
        Assert.True(await references.IsReadyAsync(default));
        Assert.True(await references.IsActiveSelectionAsync("01", "00004", default));
        Assert.False(await references.IsActiveSelectionAsync("02", "00004", default));
    }

    [Fact]
    public async Task RecoveryThenReadvanceAppendsAndHighWaterNeverFalls()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var a = Bundle(1, Header + "01,Tỉnh A,,\n01,Tỉnh A,00001,Xã cũ\n");
        var e1 = await Apply(h, a);
        var b = Bundle(2, Header + "01,Tỉnh B,,\n01,Tỉnh B,00001,Phường mới\n", e1);
        var originalBPlan = await h.PlanAsync(b);
        var e2 = await Apply(h, b);
        var recovery = With(a, a.Manifest() with
        {
            BaseImportId = e2,
            Operation = "recovery",
            RecoveryOf = e2,
            TargetImportId = e1,
            Reason = "Verified correction",
            IncidentRecord = "incident:disposable",
            ReAdvancePath = [e2]
        });
        var recoveryPlan = await h.PlanAsync(recovery); Assert.Equal(2, recoveryPlan.HistoricalHighWater);
        var e3 = await Apply(h, recovery);
        var forward = With(b, b.Manifest() with { BaseImportId = e3, Operation = "re_advance", ReAdvanceOf = e2, RecoveryEventId = e3, PathPosition = 0 });
        var plan = await h.PlanAsync(forward); Assert.Equal(2, plan.HistoricalHighWater); Assert.Equal(1, plan.ActiveReleaseOrder);
        Assert.Equal(e3, plan.RecoveryEventId); Assert.Equal(e2, plan.ReAdvanceOf); Assert.Equal(0, plan.PathPosition);
        var differentBytes = Encoding.UTF8.GetBytes(Header + "01,Khác payload,,\n");
        var different = forward with
        {
            CsvBytes = differentBytes,
            ManifestBytes = ImportJson.Write(forward.Manifest() with
            {
                Csv = forward.Manifest().Csv with { Bytes = differentBytes.Length, Sha256 = SettingsJson.Hash(differentBytes) },
                Expected = new(1, 0, 1, 1)
            })
        };
        h.Trust(different);
        await using (var rejectedScope = h.Factory.Services.CreateAsyncScope())
        {
            var rejected = await rejectedScope.ServiceProvider.GetRequiredService<TeaPremium.Application.UseCases.IQueryHandler<PlanAdministrativeImport, ImportPlan>>()
                .Handle(new(different), default);
            Assert.False(rejected.IsSuccess); Assert.Equal("release_payload_changed", rejected.Error.Code);
        }
        var stale = await h.ApplyAsync(b, h.Approve(b, originalBPlan)); Assert.False(stale.IsSuccess); Assert.Equal("stale_plan", stale.Error.Code);
        var execution = h.Approve(forward, plan);
        var contenders = await Task.WhenAll(h.ApplyAsync(forward, execution), h.ApplyAsync(forward, execution));
        Assert.Contains(contenders, x => x.IsSuccess && x.Value.Status == "applied");
        Assert.All(contenders, x => Assert.True(x.IsSuccess || x.Error.Code == "import_busy"));
        Assert.Equal("no_op", (await h.ApplyAsync(forward, execution)).Value.Status);
        await using var db = h.OwnerContext(); var events = await db.AdministrativeDataImports.ToArrayAsync();
        Assert.Equal(4, events.Length); Assert.Equal(2, events.Where(x => x.Operation == "import").Max(x => x.ReleaseOrder));
        Assert.Equal(e2, Assert.Single(events, x => x.Operation == "re_advance").ReAdvanceOf);
        using var receipt = System.Text.Json.JsonDocument.Parse(Assert.Single(events, x => x.Operation == "recovery").Receipt);
        Assert.Equal("incident:disposable", receipt.RootElement.GetProperty("incident_record").GetString());
        Assert.Equal("Tỉnh B", (await db.Provinces.SingleAsync()).Name);
        Assert.Equal("Phường mới", (await db.Wards.SingleAsync()).Name);
    }

    [Fact]
    public async Task BusyStaleConcurrentAndSqlRollbackDoNotAppendPartially()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var bundle = Bundle(1, Header + "01,Tỉnh,,\n01,Tỉnh,00001,Xã\n");
        var plan = await h.PlanAsync(bundle); var execution = h.Approve(bundle, plan);
        await using (var connection = new NpgsqlConnection(h.RuntimeConnection))
        {
            await connection.OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(8300,9)", connection, tx); await command.ExecuteNonQueryAsync();
            var busy = await h.ApplyAsync(bundle, execution); Assert.False(busy.IsSuccess); Assert.Equal("import_busy", busy.Error.Code);
            await tx.RollbackAsync();
        }
        await h.ExecuteAsync("CREATE FUNCTION platform.p009_fail_import() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected ledger failure'; END $$; CREATE TRIGGER p009_fail_import BEFORE INSERT ON platform.administrative_data_imports FOR EACH ROW EXECUTE FUNCTION platform.p009_fail_import()");
        await Assert.ThrowsAsync<DbUpdateException>(() => h.ApplyAsync(bundle, execution));
        await using var db = h.OwnerContext();
        Assert.Empty(await db.Provinces.ToArrayAsync()); Assert.Empty(await db.Wards.ToArrayAsync()); Assert.Empty(await db.AdministrativeDataImports.ToArrayAsync());
        await h.ExecuteAsync("DROP TRIGGER p009_fail_import ON platform.administrative_data_imports");
        var concurrent = await Task.WhenAll(h.ApplyAsync(bundle, execution), h.ApplyAsync(bundle, execution));
        Assert.Contains(concurrent, x => x.IsSuccess && x.Value.Status == "applied");
        Assert.All(concurrent, x => Assert.True(x.IsSuccess || x.Error.Code == "import_busy"));
        Assert.Equal(1, await db.AdministrativeDataImports.CountAsync());
    }

    [Fact]
    public async Task PinnedFixtureNormalizesToApprovedChecksumAndImportsWithoutNetwork()
    {
        var sources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in ImportValidation.PinnedFiles)
        {
            await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v5.2.0", file.Key.Replace("/", "__", StringComparison.Ordinal) + ".gz"));
            await using var gzip = new GZipStream(stream, CompressionMode.Decompress); using var bytes = new MemoryStream(); await gzip.CopyToAsync(bytes);
            sources[file.Key] = bytes.ToArray(); Assert.Equal(file.Value, SettingsJson.Hash(bytes.ToArray()));
        }
        var csv = AdministrativeCsv.NormalizePinnedJson(sources["json/full_json_generated_data_vn_units.json"]);
        Assert.Equal(ImportValidation.PinnedCsvSha256, SettingsJson.Hash(csv)); Assert.Equal(154871, csv.Length);
        var starter = Bundle(1, Encoding.UTF8.GetString(csv));
        var m = starter.Manifest() with
        {
            DatasetVersion = "v5.2.0",
            EffectiveOn = new(2026, 9, 20),
            CommunitySource = new(ImportValidation.Repository, "v5.2.0", ImportValidation.PinnedCommit,
                sources.Select(x => new SourceFile(x.Key, $"https://raw.githubusercontent.com/{ImportValidation.Repository}/{ImportValidation.PinnedCommit}/{x.Key}", x.Value.Length, SettingsJson.Hash(x.Value))).ToArray(), "MIT", ImportValidation.PinnedFiles["LICENSE"])
        };
        var bundle = new ImportBundle(csv, ImportJson.Write(m), sources);
        var parsed = ImportValidation.Validate(bundle, new(2026, 11, 18), false);
        Assert.Equal(34, parsed.Catalog.Provinces.Length); Assert.Equal(3321, parsed.Catalog.Wards.Length); Assert.Equal(3355, parsed.DataRows);
        await using var h = await P009Harness.CreateAsync(fixture); await Apply(h, bundle);
        await using var db = h.OwnerContext(); Assert.Equal(34, await db.Provinces.CountAsync()); Assert.Equal(3321, await db.Wards.CountAsync());
    }

    [Fact]
    public async Task DeltaAbsentRowsPersistFullMissingDeactivateAndReferencesAreNeverRewritten()
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var e1 = await Apply(h, Bundle(1, Header + "01,Tỉnh A,,\n01,Tỉnh A,00001,Xã\n02,Tỉnh B,,\n"));
        // Real P2-01 CRM/Sales reference fixtures (no CRM/checkout workflow implementation).
        await h.ExecuteAsync("""
INSERT INTO crm.customers(id,code,full_name,phone_e164,origin)
 VALUES ('11111111-1111-1111-1111-111111111110','KH009002','P009 Fixture','+84900009001','admin');
INSERT INTO crm.customer_addresses(id,customer_id,receiver_name,receiver_phone,province_code,ward_code,address_line)
 VALUES ('11111111-1111-1111-1111-111111111111','11111111-1111-1111-1111-111111111110','Receiver','+84900009002','01','00001','1 St');
INSERT INTO sales.orders(id,code,origin,sales_channel,customer_id,status,payment_method,contact_name,contact_phone,receiver_name,receiver_phone,province_code,province_name,ward_code,ward_name,address_line,subtotal_vnd,total_vnd,acquisition_source,is_first_purchase,placed_at)
 VALUES ('11111111-1111-1111-1111-111111111111','TP261123P009','admin','phone','11111111-1111-1111-1111-111111111110','pending','cod','P009 Fixture','+84900009001','Receiver','+84900009002','01','snapshot province','00001','snapshot ward','1 St',1000,1000,'direct',true,now());
""");
        var delta = Bundle(2, Header + "02,Tỉnh B,,\n02,Tỉnh B,00001,Phường mới\n", e1, "delta");
        var plan = await h.PlanAsync(delta); Assert.Equal(2, plan.ReferenceCount); Assert.Contains(plan.Changes, x => x.Action == "reparent");
        var e2 = await Apply(h, delta);
        await using var db = h.OwnerContext(); Assert.True((await db.Provinces.SingleAsync(x => x.Code == "01")).IsActive);
        var full = Bundle(3, Header + "02,Tỉnh B,,\n", e2);
        await Apply(h, full); Assert.False((await db.Wards.AsNoTracking().SingleAsync()).IsActive);
        Assert.Equal("01", await db.Database.SqlQueryRaw<string>("SELECT province_code AS \"Value\" FROM crm.customer_addresses").SingleAsync());
        Assert.Equal("snapshot ward", await db.Database.SqlQueryRaw<string>("SELECT ward_name AS \"Value\" FROM sales.orders").SingleAsync());
        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("202611170919_StaffIam"));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("unknown-manifest")]
    [InlineData("stale")]
    [InlineData("no-approval")]
    [InlineData("production")]
    public async Task InvalidOrUntrustedInputsNeverMutate(string scenario)
    {
        await using var h = await P009Harness.CreateAsync(fixture);
        var b = Bundle(1, Header + "01,Tỉnh,,\n"); var plan = await h.PlanAsync(b); var execution = h.Approve(b, plan);
        if (scenario == "checksum") b = b with { CsvBytes = Encoding.UTF8.GetBytes(Header + "01,Khác,,\n") };
        if (scenario == "unknown-manifest") h.Configuration["AdministrativeImport:TrustedManifestSha256:" + SettingsJson.Hash(b.ManifestBytes)] = null;
        if (scenario == "stale") execution = execution with { Plan = execution.Plan with { BeforeDigest = new string('b', 64) } };
        if (scenario == "no-approval") execution = execution with { Approval = null };
        if (scenario == "production") execution = execution with { Plan = execution.Plan with { Profile = "Production" } };
        var result = await h.ApplyAsync(b, execution); Assert.False(result.IsSuccess);
        await using var db = h.OwnerContext(); Assert.Empty(await db.Provinces.ToArrayAsync()); Assert.Empty(await db.AdministrativeDataImports.ToArrayAsync());
    }
    private static async Task<Guid> Apply(P009Harness h, ImportBundle bundle)
    {
        var p = await h.PlanAsync(bundle); var result = await h.ApplyAsync(bundle, h.Approve(bundle, p));
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.Code); return result.Value.EventId!.Value;
    }
    private static ImportBundle With(ImportBundle bundle, ImportManifest manifest) => bundle with { ManifestBytes = ImportJson.Write(manifest) };
    internal static ImportBundle Bundle(long ordinal, string csv, Guid? previous = null, string mode = "full")
    {
        var bytes = Encoding.UTF8.GetBytes(csv); var p = AdministrativeCsv.Parse(bytes);
        var sources = new Dictionary<string, byte[]> { ["LICENSE"] = Encoding.UTF8.GetBytes("MIT synthetic fixture") };
        var commit = new string('a', 40); var file = new SourceFile("LICENSE", $"https://raw.githubusercontent.com/{ImportValidation.Repository}/{commit}/LICENSE", sources["LICENSE"].Length, SettingsJson.Hash(sources["LICENSE"]));
        var m = new ImportManifest("vn-admin-manifest/1", "vn-administrative-units", "test-" + ordinal, ordinal, new(2026, 1, (int)ordinal), mode, "import", "vn-admin-csv/1",
            new(ImportValidation.Repository, "test-" + ordinal, commit, [file], "MIT", file.Sha256),
            new(null, null, null, [], [], "NOT_VERIFIED_LOCAL_FIXTURE", null, null, mode == "full" ? "complete test fixture" : null),
            new("provinces.csv", bytes.Length, SettingsJson.Hash(bytes), "utf-8", ",", "LF", false),
            new(p.Catalog.Provinces.Length, p.Catalog.Wards.Length, p.ProvinceOnlyRows, p.DataRows), new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), previous, new([], []), "test evidence");
        return new(bytes, ImportJson.Write(m), sources);
    }
}
