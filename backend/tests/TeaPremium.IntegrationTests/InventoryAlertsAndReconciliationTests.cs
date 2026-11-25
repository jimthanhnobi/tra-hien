using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Results;
using TeaPremium.Application.Time;
using TeaPremium.Application.UseCases;
using TeaPremium.Domain.Inventory;
using TeaPremium.Infrastructure.Inventory;
using TeaPremium.Infrastructure.Jobs;
using TeaPremium.Infrastructure.Outbox;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed partial class InventoryApiTests
{
    private const string Alerts = Root + "/alerts";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task P110LowStockThresholdBoundariesTransactionalEnqueueRollbackAndDeduplication()
    {
        await using var h = await ProductHarness.Create(fixture);

        // 1. Threshold = 0 is disabled even when available = 0 or 1.
        var disabledProduct = await h.Create("Disabled threshold");
        var disabled = Assert.Single(disabledProduct.Variants);
        Assert.Equal(0, disabled.LowStockThreshold);
        Assert.Equal(0L, await AlertOutboxCount(h));
        Assert.True((await PostStock(h, disabled.Id, new("opening", 1, 1000L, "import", null, null, "staff", h.App.OwnerId))).IsSuccess);
        Assert.True((await PostStock(h, disabled.Id, new("damage", -1, 1000L, "adjustment", null, "broken", "staff", h.App.OwnerId))).IsSuccess);
        Assert.Equal(0L, await AlertOutboxCount(h));

        // 2. Catalog variant create with lowStockThreshold > 0 (available = 0 <= 3) enqueues 1 low_stock alert.
        var createdLow = await h.Create("Created with threshold", new
        {
            name = "Created with threshold",
            productTypeId = h.Type,
            materialId = h.Material,
            defaultVariant = new { lowStockThreshold = 3 }
        });
        var vCreated = Assert.Single(createdLow.Variants);
        Assert.Equal([new(InventoryAlert.LowStock, vCreated.Id)], await PendingAlerts(h));
        await ClearAlertOutbox(h);

        // Adding a second variant with threshold > 0 alerts ONLY the new variant, not the untouched default variant.
        createdLow = await h.Change(createdLow, "/variants", new
        {
            name = "245ml",
            lowStockThreshold = 2,
            attributes = new[] { new TeaPremium.Application.Catalog.ProductValue(h.VariantAttribute, ValueNumber: 245) }
        });
        var vSecond = createdLow.Variants.Single(x => x.Id != vCreated.Id);
        Assert.Equal([new(InventoryAlert.LowStock, vSecond.Id)], await PendingAlerts(h));
        await ClearAlertOutbox(h);

        // Unrelated product PATCH does not re-emit alerts for existing low-stock variants.
        createdLow = await h.Patch(createdLow, new { subtitle = "Cập nhật phụ đề" });
        Assert.Empty(await PendingAlerts(h));

        // 3. Exact boundaries: available = threshold + 1 (no alert) vs available = threshold (alert), including reserved > 0.
        var boundaryProduct = await h.Create("Boundary teapot");
        var vBoundary = Assert.Single(boundaryProduct.Variants);
        boundaryProduct = await h.Change(boundaryProduct, "/variants/" + vBoundary.Id,
            new { lowStockThreshold = 5 }, HttpMethod.Patch, vBoundary.RowVersion);
        vBoundary = Assert.Single(boundaryProduct.Variants);
        Assert.Equal([new(InventoryAlert.LowStock, vBoundary.Id)], await PendingAlerts(h));
        await ClearAlertOutbox(h);

        // Stock write to available = 6 (threshold + 1) -> 0 alerts.
        Assert.True((await PostStock(h, vBoundary.Id, new("opening", 6, 50_000L, "import", null, null, "staff", h.App.OwnerId))).IsSuccess);
        Assert.Empty(await PendingAlerts(h));
        Assert.Equal(0, (await h.App.Client.GetFromJsonAsync<InventoryAlertsView>(Alerts))!.LowStockItems.Count(x => x.VariantId == vBoundary.Id));

        // Stock write to available = 5 (== threshold) -> 1 alert.
        Assert.True((await PostStock(h, vBoundary.Id, new("damage", -1, 50_000L, "adjustment", null, "chip", "staff", h.App.OwnerId))).IsSuccess);
        Assert.Equal([new(InventoryAlert.LowStock, vBoundary.Id)], await PendingAlerts(h));
        Assert.Contains((await h.App.Client.GetFromJsonAsync<InventoryAlertsView>(Alerts))!.LowStockItems,
            x => x.VariantId == vBoundary.Id && x.Available == 5 && x.LowStockThreshold == 5);
        await ClearAlertOutbox(h);

        // Catalog threshold update boundary: onHand = 5, reserved = 1 -> available = 4.
        // Lowering threshold to 3 (available = threshold + 1) -> 0 alerts; raising threshold to 4 (available = threshold) -> 1 alert.
        await h.App.ExecuteAsync($"UPDATE inventory.stock_levels SET reserved = 1 WHERE variant_id = '{vBoundary.Id}'");
        boundaryProduct = await h.Change(boundaryProduct, "/variants/" + vBoundary.Id,
            new { lowStockThreshold = 3 }, HttpMethod.Patch, vBoundary.RowVersion);
        vBoundary = Assert.Single(boundaryProduct.Variants);
        Assert.Empty(await PendingAlerts(h));

        boundaryProduct = await h.Change(boundaryProduct, "/variants/" + vBoundary.Id,
            new { lowStockThreshold = 4 }, HttpMethod.Patch, vBoundary.RowVersion);
        vBoundary = Assert.Single(boundaryProduct.Variants);
        Assert.Equal([new(InventoryAlert.LowStock, vBoundary.Id)], await PendingAlerts(h));
        await ClearAlertOutbox(h);

        // 4. Multi-write for the same variant inside a single transaction deduplicates to 1 outbox message,
        // and clears if a subsequent write in the same transaction replenishes above threshold.
        var multiVariant = Assert.Single((await h.Create("Multi receipt", new
        {
            name = "Multi receipt",
            productTypeId = h.Type,
            defaultVariant = new { lowStockThreshold = 5 }
        })).Variants);
        await ClearAlertOutbox(h);

        await using (var scope = h.App.Factory.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var stock = scope.ServiceProvider.GetRequiredService<IInventoryStockStore>();
            var mutation = scope.ServiceProvider.GetRequiredService<InventoryMutation>();

            var committed = await uow.ExecuteAsync(async ct =>
            {
                var p1 = (await stock.LockAsync([multiVariant.Id], ct))[multiVariant.Id];
                Assert.Null(InventoryMutation.Plan(p1, new("receipt", 2, 10_000L, "receipt", Guid.CreateVersion7(), null, "staff", h.App.OwnerId), out var s1));
                mutation.Stage([s1!]);

                var p2 = (await stock.LockAsync([multiVariant.Id], ct))[multiVariant.Id];
                Assert.Null(InventoryMutation.Plan(p2, new("receipt", 2, 10_000L, "receipt", Guid.CreateVersion7(), null, "staff", h.App.OwnerId), out var s2));
                mutation.Stage([s2!]);
                return Result.Success(true);
            }, default);
            Assert.True(committed.IsSuccess);
        }
        Assert.Equal(2L, await Scalar(h, $"SELECT count(*) FROM inventory.stock_movements WHERE variant_id='{multiVariant.Id}'"));
        Assert.Equal([new(InventoryAlert.LowStock, multiVariant.Id)], await PendingAlerts(h));
        await ClearAlertOutbox(h);

        // 5. Rollback on BOTH stock writes and Catalog threshold writes leaves 0 InventoryAlert messages.
        await using (var scope = h.App.Factory.Services.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var stock = scope.ServiceProvider.GetRequiredService<IInventoryStockStore>();
            var mutation = scope.ServiceProvider.GetRequiredService<InventoryMutation>();

            var rolledBack = await uow.ExecuteAsync(async ct =>
            {
                var pos = (await stock.LockAsync([multiVariant.Id], ct))[multiVariant.Id];
                Assert.Null(InventoryMutation.Plan(pos, new("adjustment", 1, 10_000L, "adjustment", Guid.CreateVersion7(), "count", "staff", h.App.OwnerId), out var post));
                mutation.Stage([post!]);
                return Result.Failure<bool>(Error.BusinessRule("abort_test", "abort"));
            }, default);
            Assert.False(rolledBack.IsSuccess);
        }
        Assert.Empty(await PendingAlerts(h));

        await h.App.ExecuteAsync("""
CREATE FUNCTION platform.test_reject_alert_commit() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.type = 'InventoryAlert' THEN RAISE EXCEPTION 'simulated commit failure'; END IF;
  RETURN NEW;
END $$;
CREATE CONSTRAINT TRIGGER test_reject_alert_commit AFTER INSERT ON platform.outbox_messages
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.test_reject_alert_commit();
""");
        using (var failedCatalog = await h.App.MutateAsync(HttpMethod.Post, ProductApiTests.Root, new
        {
            name = "Rolled back product",
            productTypeId = h.Type,
            defaultVariant = new { lowStockThreshold = 2 }
        }))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failedCatalog.StatusCode);
        }
        using (var failedAdjustment = await h.App.MutateAsync(HttpMethod.Post, Adjustments, new
        {
            variantId = multiVariant.Id,
            quantityDelta = 1,
            type = "adjustment",
            reason = "Trigger rollback"
        }))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failedAdjustment.StatusCode);
        }
        await h.App.ExecuteAsync("DROP TRIGGER test_reject_alert_commit ON platform.outbox_messages; DROP FUNCTION platform.test_reject_alert_commit()");
        Assert.Empty(await PendingAlerts(h));
        Assert.Equal(4L, await Scalar(h, $"SELECT on_hand::bigint FROM inventory.stock_levels WHERE variant_id='{multiVariant.Id}'"));

        // 6. End-to-end HTTP receipt post (POST /api/v1/admin/receipts/{id}/post) enqueues 1 low_stock alert when post-write <= threshold.
        var receiptDraft = await NewReceipt(h, new ReceiptLineInput(multiVariant.Id, 1, 12_000L));
        using var receiptPosted = await PostReceipt(h, Receipts + "/" + receiptDraft.Document.Id + "/post", receiptDraft.Document.RowVersion, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.OK, receiptPosted.StatusCode);
        Assert.Equal([new(InventoryAlert.LowStock, multiVariant.Id)], await PendingAlerts(h));
    }

    [Fact]
    public async Task P110ConsumerIsIdempotentSkipsStaleAlertsAndLogsWithoutPiiOrCosts()
    {
        var logs = new AlertLogCollector();
        await using var h = await ProductHarness.Create(fixture, services =>
            services.AddSingleton<ILoggerProvider>(logs));

        var artisanId = Guid.CreateVersion7();
        await h.App.ExecuteAsync($"INSERT INTO catalog.artisans(id,slug,name,status,first_published_at) VALUES ('{artisanId}','chu-ban-p110','Chu Bân','published',now())");
        var product = await h.Create("Ấm Chu Bân Đặc Biệt", new
        {
            name = "Ấm Chu Bân Đặc Biệt",
            productTypeId = h.Type,
            artisanId,
            defaultVariant = new { lowStockThreshold = 3 }
        });
        var variant = Assert.Single(product.Variants);
        await h.App.ExecuteAsync("DELETE FROM platform.outbox_messages WHERE type <> 'InventoryAlert'");

        const long secretCost = 987_654_321L;
        Assert.True((await PostStock(h, variant.Id, new("opening", 2, secretCost, "import", null, null, "staff", h.App.OwnerId))).IsSuccess);
        await h.App.ExecuteAsync("DELETE FROM platform.outbox_messages WHERE type <> 'InventoryAlert'");

        // Two pending low_stock messages exist for the same variant (1 from create, 1 from opening stock <= threshold).
        Assert.Equal(2L, await AlertOutboxCount(h));
        var beforeFingerprint = await StockAndAuditFingerprint(h);

        await using (var scope = h.App.Factory.Services.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
            Assert.Equal(2, await processor.ProcessAsync(10, default));

            // Direct duplicate redelivery of the same messageId is also safe and mutates no DB state.
            var handler = scope.ServiceProvider.GetServices<IOutboxHandler>().Single(x => x.MessageType == InventoryAlert.Message);
            await handler.HandleAsync(Guid.CreateVersion7(), InventoryAlert.Serialize(new(InventoryAlert.LowStock, variant.Id)), default);
        }

        Assert.Equal(beforeFingerprint, await StockAndAuditFingerprint(h));
        Assert.Equal(2L, await Scalar(h, "SELECT count(*) FROM platform.outbox_messages WHERE type='InventoryAlert' AND status='processed'"));
        Assert.Equal(3, logs.Entries.Count);
        Assert.All(logs.Entries, entry =>
        {
            Assert.Equal("InventoryLowStockAlert", entry.EventName);
            Assert.DoesNotContain("987654321", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Chu Bân", entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Ấm Chu Bân", entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cost", entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.All(entry.State, kv =>
            {
                Assert.DoesNotContain("cost", kv.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("987654321", kv.Value?.ToString() ?? "", StringComparison.Ordinal);
            });
        });

        // Stale case 1: stock replenished to available = 4 (> threshold 3) before queued alert runs -> safe no-op.
        logs.Entries.Clear();
        var staleReplenishedId = await InsertRawAlert(h, InventoryAlert.Serialize(new(InventoryAlert.LowStock, variant.Id)));
        Assert.True((await PostStock(h, variant.Id, new("receipt", 2, secretCost, "receipt", Guid.CreateVersion7(), null, "staff", h.App.OwnerId))).IsSuccess);
        await using (var scope = h.App.Factory.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default) >= 1);
        Assert.Equal("processed", await OutboxStatus(h, staleReplenishedId));
        Assert.Empty(logs.Entries);

        // Stale case 2: stock drops back to 2 (<= 3) enqueueing an alert, then threshold is lowered to 0 before delivery -> safe no-op.
        Assert.True((await PostStock(h, variant.Id, new("damage", -2, secretCost, "adjustment", null, "broken", "staff", h.App.OwnerId))).IsSuccess);
        var pendingBeforeDisable = await PendingAlertIds(h);
        Assert.Single(pendingBeforeDisable);
        var currentProduct = await h.Get(product.Id);
        var currentVariant = Assert.Single(currentProduct.Variants);
        await h.Change(currentProduct, "/variants/" + variant.Id, new { lowStockThreshold = 0 }, HttpMethod.Patch, currentVariant.RowVersion);
        await using (var scope = h.App.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default);
        Assert.Equal("processed", await OutboxStatus(h, pendingBeforeDisable[0]));
        Assert.Empty(logs.Entries);

        // Active vs stale reconciliation_mismatch consumer check.
        await h.App.ExecuteAsync($"UPDATE inventory.stock_levels SET on_hand = 99 WHERE variant_id = '{variant.Id}'");
        var mismatchMsgId = await InsertRawAlert(h, InventoryAlert.Serialize(new(InventoryAlert.ReconciliationMismatch, variant.Id, new DateOnly(2026, 11, 23))));
        await using (var scope = h.App.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default);
        Assert.Equal("processed", await OutboxStatus(h, mismatchMsgId));
        var mismatchLog = Assert.Single(logs.Entries);
        Assert.Equal("InventoryReconciliationMismatchAlert", mismatchLog.EventName);
        Assert.DoesNotContain("987654321", mismatchLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cost", mismatchLog.Message, StringComparison.OrdinalIgnoreCase);

        logs.Entries.Clear();
        await h.App.ExecuteAsync($"UPDATE inventory.stock_levels SET on_hand = 2 WHERE variant_id = '{variant.Id}'");
        var resolvedMismatchId = await InsertRawAlert(h, InventoryAlert.Serialize(new(InventoryAlert.ReconciliationMismatch, variant.Id, new DateOnly(2026, 11, 23))));
        await using (var scope = h.App.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default);
        Assert.Equal("processed", await OutboxStatus(h, resolvedMismatchId));
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task P110MalformedPayloadAndHandlerFailureBackoffToDeadAtTenAttemptsAndUpdateDeadCount()
    {
        var clock = new ManualTimeClock(new(2026, 11, 22, 18, 0, 0, TimeSpan.Zero));
        await using var h = await ProductHarness.Create(fixture, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.RemoveAll<IClock>();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IClock>(clock);
        });
        await h.App.ExecuteAsync("DELETE FROM platform.outbox_messages");

        // Malformed payload drives exponential backoff min(2^attempts, 360) minutes up to 10 attempts -> dead.
        var badAlertId = await InsertRawAlert(h, "{\"kind\":\"invalid_kind\",\"variantId\":\"01933000-0000-7000-8000-000000000099\"}", clock.UtcNow.UtcDateTime);
        // Unrelated dead outbox row must NOT inflate deadAlertOutboxCount.
        await h.App.ExecuteAsync($$"""
INSERT INTO platform.outbox_messages(id,type,payload,occurred_at,available_at,attempts,status,last_error)
VALUES ('{{Guid.CreateVersion7()}}','StorefrontInvalidate','{"Tags":["catalog"]}',now(),now(),10,'dead','handler_failed');
""");

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await using (var scope = h.App.Factory.Services.CreateAsyncScope())
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(5, default));

            await using var db = h.App.OwnerContext();
            var row = await db.OutboxMessages.SingleAsync(x => x.Id == badAlertId);
            Assert.Equal(attempt, row.Attempts);
            Assert.Equal("handler_failed", row.LastError);

            if (attempt < 10)
            {
                Assert.Equal("pending", row.Status);
                var expectedBackoff = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempt), 360));
                Assert.Equal(clock.UtcNow.UtcDateTime + expectedBackoff, row.AvailableAt);
                Assert.Equal(0L, (await h.App.Client.GetFromJsonAsync<InventoryAlertsView>(Alerts))!.DeadAlertOutboxCount);
                clock.Advance(expectedBackoff);
            }
            else
            {
                Assert.Equal("dead", row.Status);
            }
        }

        // Also prove handler runtime exception follows the exact same 10-attempt exhaustion to dead.
        var throwingId = await InsertRawAlert(h, InventoryAlert.Serialize(new(InventoryAlert.LowStock, Guid.CreateVersion7())), clock.UtcNow.UtcDateTime);
        const string privateError = "telegram-token-SECRET customer@example.test";
        var failingHandler = new ThrowingInventoryAlertHandler(privateError);
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await using (var db = h.App.OwnerContext())
                Assert.Equal(1, await new OutboxBatchProcessor(db, clock, [failingHandler]).ProcessAsync(1, default));
            await using var verifyDb = h.App.OwnerContext();
            var row = await verifyDb.OutboxMessages.SingleAsync(x => x.Id == throwingId);
            Assert.DoesNotContain("SECRET", row.LastError ?? "", StringComparison.Ordinal);
            if (attempt < 10)
                clock.Advance(TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempt), 360)));
            else
                Assert.Equal("dead", row.Status);
        }

        // 14.5 hours elapsed without HTTP activity during the second backoff loop, expiring the 8-hour AdminCookie.
        await h.App.LoginAsync();
        var view = (await h.App.Client.GetFromJsonAsync<InventoryAlertsView>(Alerts))!;
        Assert.Equal(2L, view.DeadAlertOutboxCount);
    }

    [Fact]
    public async Task P110ReconcileInventoryEnforcesVietnamScheduleAdvisoryLockMissingRowAndNeverTouchesReservationsOrBalances()
    {
        // Start at 2026-11-23 01:29:59 +07:00 = 2026-11-22 18:29:59Z (1 second before VN 01:30).
        var clock = new ManualTimeClock(new(2026, 11, 22, 18, 29, 59, TimeSpan.Zero));
        await using var h = await ProductHarness.Create(fixture, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.RemoveAll<IClock>();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IClock>(clock);
        });

        var matched = Assert.Single((await h.Create("Matched variant")).Variants);
        var emptyNoRow = Assert.Single((await h.Create("Empty without stock row")).Variants);
        var mismatchedLevel = Assert.Single((await h.Create("Corrupted stock level")).Variants);
        var missingLevelWithMoves = Assert.Single((await h.Create("Missing stock level with ledger")).Variants);
        var reservedMismatch = Assert.Single((await h.Create("Reserved mismatch variant")).Variants);
        var closedReservationsMatched = Assert.Single((await h.Create("Closed reservations matched variant")).Variants);

        Assert.True((await PostStock(h, matched.Id, new("opening", 10, 50_000L, "import", null, null, "staff", h.App.OwnerId))).IsSuccess);
        Assert.True((await PostStock(h, matched.Id, new("damage", -2, 50_000L, "adjustment", null, "test", "staff", h.App.OwnerId))).IsSuccess);
        Assert.True((await PostStock(h, mismatchedLevel.Id, new("opening", 5, 80_000L, "import", null, null, "staff", h.App.OwnerId))).IsSuccess);
        Assert.True((await PostStock(h, reservedMismatch.Id, new("opening", 4, 60_000L, "import", null, null, "staff", h.App.OwnerId))).IsSuccess);
        Assert.True((await PostStock(h, closedReservationsMatched.Id, new("opening", 4, 60_000L, "import", null, null, "staff", h.App.OwnerId))).IsSuccess);

        // Inject ledger & reservation mismatches directly as owner (BR-INV-13):
        // 1) mismatchedLevel has stock_levels.on_hand = 7 vs SUM(movements) = 5;
        // 2) missingLevelWithMoves has NO stock_levels row (COALESCE = 0) vs SUM(movements) = 3;
        // 3) reservedMismatch has stock_levels.on_hand = 4 == ledger, reserved = 2 vs SUM(active reservations) = 0;
        // 4) closedReservationsMatched has reserved = 0 and a released reservation (with order_line_id IS NULL) -> MUST NOT alert.
        await h.App.ExecuteAsync($"""
UPDATE inventory.stock_levels SET on_hand = 7 WHERE variant_id = '{mismatchedLevel.Id}';
INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,unit_cost_vnd,on_hand_after,actor_type,occurred_at)
VALUES ('{Guid.CreateVersion7()}','{missingLevelWithMoves.Id}','opening',3,40000,3,'system',now());
UPDATE inventory.stock_levels SET reserved = 2 WHERE variant_id = '{reservedMismatch.Id}';
INSERT INTO inventory.reservations(id,variant_id,order_line_id,quantity,status,created_at,closed_at,close_reason)
VALUES ('{Guid.CreateVersion7()}','{closedReservationsMatched.Id}',NULL,2,'released',now(),now(),'line_removed');
DELETE FROM platform.outbox_messages;
""");
        Assert.Equal(0L, await Scalar(h, $"SELECT count(*) FROM inventory.stock_levels WHERE variant_id='{emptyNoRow.Id}' OR variant_id='{missingLevelWithMoves.Id}'"));

        var balancesBefore = await StockAndAuditFingerprint(h);
        // Guard BOTH stock_levels and reservations at the PostgreSQL trigger level so any auto-mutation attempt immediately aborts.
        await h.App.ExecuteAsync("""
CREATE FUNCTION inventory.test_forbid_stock_level_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'ReconcileInventory must never mutate stock_levels or reservations';
END $$;
CREATE TRIGGER test_forbid_stock_level_mutation
  BEFORE INSERT OR UPDATE OR DELETE ON inventory.stock_levels
  FOR EACH STATEMENT EXECUTE FUNCTION inventory.test_forbid_stock_level_mutation();
CREATE TRIGGER test_forbid_reservation_mutation
  BEFORE INSERT OR UPDATE OR DELETE ON inventory.reservations
  FOR EACH STATEMENT EXECUTE FUNCTION inventory.test_forbid_stock_level_mutation();
""");

        using var sqlObserver = new DatabaseCommandCapture(h.App.Database);
        using var subscription = DiagnosticListener.AllListeners.Subscribe(sqlObserver);

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = h.App.RuntimeConnection }).Build();
        var scopes = h.App.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        using var job1 = new ReconcileInventory(scopes, config, clock, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReconcileInventory>.Instance);
        using var job2 = new ReconcileInventory(scopes, config, clock, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReconcileInventory>.Instance);

        // 1. Before 01:30 VN (01:29:59 +07:00): skipped.
        Assert.True(await job1.RunOnceAsync());
        Assert.Empty(await PendingAlerts(h));

        // 2. Advance 1 second -> 2026-11-23 01:30:00 +07:00.
        clock.Advance(TimeSpan.FromSeconds(1));

        // Real 2-instance advisory lock contention: pause job1 inside ExecuteJobAsync while it holds
        // the PostgreSQL session advisory lock, prove job2.RunOnceAsync() returns false, then release.
        Task<bool> run1;
        await using (var tableBlocker = new NpgsqlConnection(h.App.OwnerConnection))
        {
            await tableBlocker.OpenAsync();
            await using var held = await tableBlocker.BeginTransactionAsync();
            await using (var lockCmd = new NpgsqlCommand("LOCK TABLE catalog.variants IN ACCESS EXCLUSIVE MODE", tableBlocker, held))
                await lockCmd.ExecuteNonQueryAsync();

            run1 = job1.RunOnceAsync();
            await WaitUntilAsync(async () =>
            {
                await using var observer = new NpgsqlConnection(h.App.OwnerConnection);
                await observer.OpenAsync();
                await using var check = new NpgsqlCommand(
                    "SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND granted AND ((classid::bigint << 32) | (objid::bigint & 4294967295)) = @key",
                    observer);
                check.Parameters.Add("key", NpgsqlDbType.Bigint).Value = PeriodicJob.AdvisoryLockKey(ReconcileInventory.JobName);
                return (long)(await check.ExecuteScalarAsync())! == 1L;
            });

            Assert.False(await job2.RunOnceAsync());
            await held.RollbackAsync();
        }
        Assert.True(await run1);

        var vnDateDay1 = new DateOnly(2026, 11, 23);
        var expectedDay1 = new[] { mismatchedLevel.Id, missingLevelWithMoves.Id, reservedMismatch.Id }
            .Order()
            .Select(id => new InventoryAlert(InventoryAlert.ReconciliationMismatch, id, vnDateDay1))
            .ToArray();
        Assert.Equal(expectedDay1, await PendingAlerts(h));

        // 3. Later on the same Vietnam calendar date (02:00 VN and 23:59:59 VN), both existing instances
        // AND a brand-new restarted instance are idempotent no-ops.
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(await job1.RunOnceAsync());
        Assert.True(await job2.RunOnceAsync());
        using (var restarted = new ReconcileInventory(scopes, config, clock, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReconcileInventory>.Instance))
        {
            Assert.True(await restarted.RunOnceAsync());
        }
        clock.Advance(TimeSpan.FromHours(21) + TimeSpan.FromMinutes(59));
        Assert.True(await job1.RunOnceAsync());
        Assert.Equal(3L, await AlertOutboxCount(h));

        // 4. Next Vietnam day at 01:29:00 +07:00 is skipped; advancing timer by 5 minutes past 01:30 +07:00
        // triggers background ExecuteAsync once for 2026-11-24.
        clock.SetUtc(BusinessCalendar.ToUtc(new DateOnly(2026, 11, 24), new TimeOnly(1, 29)));
        await job1.StartAsync(CancellationToken.None);
        try
        {
            // Initial tick at 01:29 VN -> skipped (still 3 alerts from day 1).
            await WaitUntilAsync(async () =>
            {
                clock.Advance(ReconcileInventory.PollInterval);
                return await AlertOutboxCount(h) == 6L;
            });
        }
        finally
        {
            await job1.StopAsync(CancellationToken.None);
        }

        // 5. Prove ReconcileInventory never mutated stock_levels, stock_movements, reservations, or audit_logs;
        // then also exercise GET /alerts and InventoryAlertHandler under the same SQL & trigger guards.
        Assert.Equal(balancesBefore, await StockAndAuditFingerprint(h));
        var stockOnlyBefore = await CatalogInventoryFixture.ScalarAsync<string>(h.App.OwnerConnection, """
SELECT concat_ws('|',
  (SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY variant_id)::text,'') FROM inventory.stock_levels s),
  (SELECT coalesce(jsonb_agg(to_jsonb(m) ORDER BY id)::text,'') FROM inventory.stock_movements m),
  (SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY id)::text,'') FROM inventory.reservations r))
""");
        await h.App.LoginAsync();
        var alertsResponse = (await h.App.Client.GetFromJsonAsync<InventoryAlertsView>(Alerts))!;
        // ASSUMPTION-P205-01 = B: AlertsAsync remains unchanged (on_hand vs ledger mismatches only).
        Assert.Equal(2L, alertsResponse.MismatchTotal);
        await using (var scope = h.App.Factory.Services.CreateAsyncScope())
            Assert.Equal(6, await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(10, default));

        Assert.Equal(stockOnlyBefore, await CatalogInventoryFixture.ScalarAsync<string>(h.App.OwnerConnection, """
SELECT concat_ws('|',
  (SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY variant_id)::text,'') FROM inventory.stock_levels s),
  (SELECT coalesce(jsonb_agg(to_jsonb(m) ORDER BY id)::text,'') FROM inventory.stock_movements m),
  (SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY id)::text,'') FROM inventory.reservations r))
"""));
        Assert.NotEmpty(sqlObserver.Commands);
        Assert.All(sqlObserver.Commands, sql =>
        {
            Assert.DoesNotContain("UPDATE \"inventory\".\"stock_levels\"", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("INSERT INTO \"inventory\".\"stock_levels\"", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE FROM \"inventory\".\"stock_levels\"", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE \"inventory\".\"reservations\"", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("INSERT INTO \"inventory\".\"reservations\"", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE FROM \"inventory\".\"reservations\"", sql, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task P110AlertsEndpointEnforcesPermissionsBoundsListsAndOmitsAllCostFields()
    {
        await using var h = await ProductHarness.Create(fixture);
        const long secretCost = 999_888_777L;

        var pA = await h.Create("Ấm A", new { name = "Ấm A", productTypeId = h.Type, defaultVariant = new { lowStockThreshold = 5 } });
        var pB = await h.Create("Ấm B", new { name = "Ấm B", productTypeId = h.Type, defaultVariant = new { lowStockThreshold = 4 } });
        var pHealthy = await h.Create("Ấm Đủ Tồn", new { name = "Ấm Đủ Tồn", productTypeId = h.Type, defaultVariant = new { lowStockThreshold = 2 } });
        var vA = Assert.Single(pA.Variants);
        var vB = Assert.Single(pB.Variants);
        var vHealthy = Assert.Single(pHealthy.Variants);

        Assert.True((await PostStock(h, vA.Id, new("opening", 3, secretCost, "import", null, null, "staff", h.App.OwnerId))).IsSuccess);
        Assert.True((await PostStock(h, vB.Id, new("opening", 4, secretCost, "import", null, null, "staff", h.App.OwnerId))).IsSuccess);
        Assert.True((await PostStock(h, vHealthy.Id, new("opening", 3, secretCost, "import", null, null, "staff", h.App.OwnerId))).IsSuccess);

        var vMissingRowMismatch = Assert.Single((await h.Create("Ấm Thiếu Stock Row")).Variants);
        await h.App.ExecuteAsync($$"""
UPDATE inventory.stock_levels SET on_hand = 9 WHERE variant_id = '{{vHealthy.Id}}';
INSERT INTO inventory.stock_movements(id,variant_id,movement_type,quantity,unit_cost_vnd,on_hand_after,actor_type,occurred_at)
VALUES ('{{Guid.CreateVersion7()}}','{{vMissingRowMismatch.Id}}','opening',2,{{secretCost}},2,'system',now());
INSERT INTO platform.outbox_messages(id,type,payload,occurred_at,available_at,attempts,status,last_error)
VALUES ('{{Guid.CreateVersion7()}}','InventoryAlert','{"kind":"low_stock","variantId":"{{vA.Id}}"}',now(),now(),10,'dead','handler_failed');
""");

        // Anonymous, HTTP, unready, and restricted content staff (inventory.read only) are rejected.
        using var anonymous = h.App.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        using (var unauth = await anonymous.GetAsync(Alerts))
            await Problem(unauth, HttpStatusCode.Unauthorized, "unauthenticated");

        using var httpClient = h.App.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("http://localhost") });
        using (var insecure = await httpClient.GetAsync(Alerts))
            await Problem(insecure, HttpStatusCode.BadRequest, "https_required");

        var unready = await ProductImportTests.CreateStaff(h, "sales", false);
        var content = await ProductImportTests.CreateStaff(h, "content", true);
        var sales = await ProductImportTests.CreateStaff(h, "sales", true);

        await h.App.LoginAsync(unready);
        using (var deniedUnready = await h.App.Client.GetAsync(Alerts))
            await Problem(deniedUnready, HttpStatusCode.Forbidden, "forbidden");

        await h.App.LoginAsync(content, P009Harness.Password + "-Changed");
        using (var deniedContent = await h.App.Client.GetAsync(Alerts))
            await Problem(deniedContent, HttpStatusCode.Forbidden, "forbidden");

        // Validation bounds on limit (1..100).
        await h.App.LoginAsync(sales, P009Harness.Password + "-Changed");
        foreach (var bad in new[] { Alerts + "?limit=0", Alerts + "?limit=-1", Alerts + "?limit=101" })
        {
            using var invalid = await h.App.Client.GetAsync(bad);
            await Problem(invalid, HttpStatusCode.BadRequest, "validation_failed");
        }

        // Both sales (without costs.read) and owner (with costs.read) get 200 OK with zero cost fields in raw JSON.
        foreach (var loginAsOwner in new[] { false, true })
        {
            if (loginAsOwner) await h.App.LoginAsync();
            var raw = await h.App.Client.GetStringAsync(Alerts);
            Assert.DoesNotContain("999888777", raw, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(raw);
            AssertNoCostProperty(json.RootElement);

            var full = JsonSerializer.Deserialize<InventoryAlertsView>(raw, WebJson)!;
            Assert.Equal(1L, full.DeadAlertOutboxCount);
            Assert.Equal(2L, full.LowStockTotal);
            Assert.Equal(2, full.LowStockItems.Count);
            Assert.Contains(full.LowStockItems, x => x.VariantId == vA.Id && x.Available == 3 && x.LowStockThreshold == 5);
            Assert.Contains(full.LowStockItems, x => x.VariantId == vB.Id && x.Available == 4 && x.LowStockThreshold == 4);

            Assert.Equal(2L, full.MismatchTotal);
            Assert.Equal(2, full.Mismatches.Count);
            Assert.Contains(full.Mismatches, x => x.VariantId == vHealthy.Id && x.HasStockLevel && x.OnHand == 9 && x.LedgerQuantity == 3 && x.Delta == -6);
            Assert.Contains(full.Mismatches, x => x.VariantId == vMissingRowMismatch.Id && !x.HasStockLevel && x.OnHand == 0 && x.LedgerQuantity == 2 && x.Delta == 2);

            var bounded = (await h.App.Client.GetFromJsonAsync<InventoryAlertsView>(Alerts + "?limit=1"))!;
            Assert.Equal(2L, bounded.LowStockTotal);
            Assert.Single(bounded.LowStockItems);
            Assert.Equal(2L, bounded.MismatchTotal);
            Assert.Single(bounded.Mismatches);
        }
    }

    private static void AssertNoCostProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                Assert.DoesNotContain("cost", prop.Name, StringComparison.OrdinalIgnoreCase);
                AssertNoCostProperty(prop.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AssertNoCostProperty(item);
        }
    }

    private static Task<long> AlertOutboxCount(ProductHarness h) =>
        Scalar(h, "SELECT count(*) FROM platform.outbox_messages WHERE type='InventoryAlert'");

    private static Task ClearAlertOutbox(ProductHarness h) =>
        h.App.ExecuteAsync("DELETE FROM platform.outbox_messages WHERE type='InventoryAlert'");

    private static async Task<InventoryAlert[]> PendingAlerts(ProductHarness h)
    {
        await using var db = h.App.OwnerContext();
        var payloads = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.Type == InventoryAlert.Message && x.Status == "pending")
            .OrderBy(x => x.Id)
            .Select(x => x.Payload)
            .ToArrayAsync();
        return payloads
            .Select(p => { Assert.True(InventoryAlert.TryParse(p, out var parsed)); return parsed; })
            .OrderBy(x => x.VariantId)
            .ThenBy(x => x.Kind, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<Guid[]> PendingAlertIds(ProductHarness h)
    {
        await using var db = h.App.OwnerContext();
        return await db.OutboxMessages.AsNoTracking()
            .Where(x => x.Type == InventoryAlert.Message && x.Status == "pending")
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToArrayAsync();
    }

    private static async Task<Guid> InsertRawAlert(ProductHarness h, string payloadJson, DateTime? nowUtc = null)
    {
        var id = Guid.CreateVersion7();
        var ts = nowUtc ?? DateTime.UtcNow;
        await using var db = h.App.OwnerContext();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = id,
            Type = InventoryAlert.Message,
            Payload = payloadJson,
            OccurredAt = ts,
            AvailableAt = ts,
            Attempts = 0,
            Status = "pending"
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<string> OutboxStatus(ProductHarness h, Guid id)
    {
        await using var db = h.App.OwnerContext();
        return (await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == id)).Status;
    }

    private static Task<string> StockAndAuditFingerprint(ProductHarness h) =>
        CatalogInventoryFixture.ScalarAsync<string>(h.App.OwnerConnection, """
SELECT concat_ws('|',
  (SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY variant_id)::text,'') FROM inventory.stock_levels s),
  (SELECT coalesce(jsonb_agg(to_jsonb(m) ORDER BY id)::text,'') FROM inventory.stock_movements m),
  (SELECT count(*) FROM platform.audit_logs))
""");

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(25);
        }
        Assert.True(await predicate(), "Timed out waiting for scheduled reconciliation.");
    }

    private sealed class ThrowingInventoryAlertHandler(string message) : IOutboxHandler
    {
        public string MessageType => InventoryAlert.Message;
        public Task HandleAsync(Guid messageId, string payload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }

    private sealed class DatabaseCommandCapture(string databaseName) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];
        public ConcurrentQueue<string> Commands { get; } = new();

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == DbLoggerCategory.Name)
                lock (_subscriptions) _subscriptions.Add(value.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == RelationalEventId.CommandExecuting.Name
                && value.Value is CommandEventData data
                && string.Equals(data.Command.Connection?.Database, databaseName, StringComparison.Ordinal))
            {
                Commands.Enqueue(data.Command.CommandText);
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }

        public void Dispose()
        {
            lock (_subscriptions)
            {
                foreach (var sub in _subscriptions) sub.Dispose();
                _subscriptions.Clear();
            }
        }
    }

    private sealed class AlertLogCollector : ILoggerProvider
    {
        public ConcurrentQueue<(string EventName, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(InventoryAlertHandler).FullName ? new Sink(Entries) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<(string EventName, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var pairs = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
                entries.Enqueue((eventId.Name ?? "", formatter(state, exception), pairs.ToArray()));
            }
        }
    }

    private sealed class ManualTimeClock(DateTimeOffset initialUtc) : TimeProvider, IClock
    {
        private readonly Lock _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = initialUtc;

        public DateTimeOffset UtcNow => GetUtcNow();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync) return _utcNow;
        }

        public void SetUtc(DateTimeOffset value)
        {
            ManualTimer[] due;
            lock (_sync)
            {
                _utcNow = value;
                due = ReadyTimers();
            }
            foreach (var timer in due) timer.Fire();
        }

        public void Advance(TimeSpan delta)
        {
            ManualTimer[] due;
            lock (_sync)
            {
                _utcNow += delta;
                due = ReadyTimers();
            }
            foreach (var timer in due) timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new ManualTimer(this, callback, state, dueTime, period, _utcNow);
                _timers.Add(timer);
                return timer;
            }
        }

        private ManualTimer[] ReadyTimers() =>
            _timers.Where(t => t.TryConsume(_utcNow)).ToArray();

        private void Remove(ManualTimer timer)
        {
            lock (_sync) _timers.Remove(timer);
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeClock _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private DateTimeOffset? _nextDue;
            private TimeSpan _period;
            private bool _disposed;

            public ManualTimer(ManualTimeClock owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period, DateTimeOffset now)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                _period = period;
                _nextDue = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
            }

            public bool TryConsume(DateTimeOffset now)
            {
                if (_disposed || _nextDue is null || now < _nextDue.Value) return false;
                _nextDue = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : now + _period;
                return true;
            }

            public void Fire()
            {
                if (!_disposed) _callback(_state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._sync)
                {
                    if (_disposed) return false;
                    _period = period;
                    _nextDue = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._utcNow + dueTime;
                    return true;
                }
            }

            public void Dispose()
            {
                _disposed = true;
                _owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
