using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Inventory;
using TeaPremium.Application.Outbox;
using TeaPremium.Application.Time;
using TeaPremium.Domain;
using TeaPremium.Domain.Inventory;
using TeaPremium.Domain.Orders;
using TeaPremium.Domain.Pricing;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Inventory;

// BR-INV-03/04/09/11/12 + AC-P2-01/02/03 + BR-SEO-21.
// Lock order: Order (if existing) -> catalog.products (ORDER BY id FOR NO KEY UPDATE)
//          -> catalog.variants (ORDER BY id FOR UPDATE)
//          -> inventory.stock_levels (ORDER BY variant_id FOR UPDATE).
public sealed class EfInventoryReservationService(
    AppDbContext db,
    IOutboxWriter outbox,
    IClock clock,
    IAuditContext auditContext,
    IProductSoldOutSync soldOutSync) : IInventoryReservationService
{
    private sealed record LockedVariantRow(
        Product Product,
        Variant Variant,
        StockLevel? Level)
    {
        public int OnHand => Level?.OnHand ?? 0;
        public int Reserved => Level?.Reserved ?? 0;
        public int Available => OnHand - Reserved;
        public long? AvgCostVnd => Level?.AvgCostVnd;
    }

    private sealed record StockTransition(
        Guid VariantId,
        string ProductSlug,
        int LowStockThreshold,
        int BeforeAvailable,
        int AfterAvailable);

    public async Task<IReadOnlyDictionary<Guid, LockedOrderVariant>> LockVariantsForOrderAsync(
        IReadOnlyCollection<Guid> variantIds,
        DateTimeOffset evaluatedAt,
        CancellationToken ct)
    {
        RequireTransaction();
        var locked = await LockVariantsCoreAsync(variantIds, ct);
        if (locked.Count == 0) return new Dictionary<Guid, LockedOrderVariant>();

        var lockedIds = locked.Keys.Order().ToArray();
        var instant = evaluatedAt.UtcDateTime;
        var promos = (await db.Set<SalePrice>().AsNoTracking()
            .Where(s => lockedIds.Contains(s.VariantId) && s.StartsAt <= instant && instant < s.EndsAt)
            .Select(s => new { s.VariantId, s.SalePriceVnd, s.StartsAt, s.EndsAt })
            .ToArrayAsync(ct))
            .ToLookup(s => s.VariantId, s => new ScheduledPrice(s.SalePriceVnd, s.StartsAt, s.EndsAt));

        var result = new Dictionary<Guid, LockedOrderVariant>(locked.Count);
        foreach (var (variantId, row) in locked)
        {
            result[variantId] = new(
                row.Variant.Id,
                row.Product.Id,
                row.Variant.Sku,
                row.Product.Name,
                row.Product.Slug,
                row.Variant.Name,
                string.Equals(row.Product.Status, "active", StringComparison.Ordinal),
                string.Equals(row.Variant.Status, "active", StringComparison.Ordinal),
                row.Variant.PriceVnd,
                row.Variant.IsUniquePiece,
                row.Variant.LowStockThreshold,
                row.OnHand,
                row.Reserved,
                row.AvgCostVnd,
                promos[variantId].ToArray());
        }
        return result;
    }

    public async Task<ReservationResult> ReserveForOrderAsync(
        IReadOnlyList<OrderReservationLine> lines,
        CancellationToken ct)
    {
        RequireTransaction();
        if (lines.Count == 0)
            return ReservationResult.Failure("validation_failed", "Đơn hàng phải có ít nhất một dòng giữ hàng.");

        var ordered = lines.OrderBy(x => x.VariantId).ToArray();
        var locked = await LockVariantsCoreAsync(ordered.Select(x => x.VariantId).ToArray(), ct);

        var shortfalls = new List<OutOfStockDetail>();
        foreach (var line in ordered)
        {
            if (!locked.TryGetValue(line.VariantId, out var row) || !IsSellable(row))
                return ReservationResult.Failure("variant_not_sellable", "Biến thể không đủ điều kiện mở bán.");
            if (row.Variant.IsUniquePiece && line.Quantity > 1)
                return ReservationResult.Failure("unique_piece_violation", "Sản phẩm độc bản chỉ được giữ số lượng 1.");
            if (row.Available < line.Quantity)
                shortfalls.Add(new(line.VariantId, row.Variant.Sku, Math.Max(0, row.Available)));
        }

        if (shortfalls.Count > 0)
            return ReservationResult.Failure("out_of_stock", "Một số sản phẩm không đủ tồn kho.", shortfalls);

        var now = clock.UtcNow.UtcDateTime;
        var transitions = new List<StockTransition>(ordered.Length);
        foreach (var line in ordered)
        {
            var row = locked[line.VariantId];
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.stock_levels
                   SET reserved = reserved + {line.Quantity}, updated_at = {now}
                 WHERE variant_id = {line.VariantId} AND on_hand - reserved >= {line.Quantity}
                """, ct);

            if (affected != 1)
            {
                return ReservationResult.Failure(
                    "out_of_stock",
                    "Sản phẩm không đủ tồn kho khả dụng.",
                    [new(line.VariantId, row.Variant.Sku, Math.Max(0, row.Available))]);
            }

            db.Set<Reservation>().Add(new Reservation
            {
                Id = Guid.CreateVersion7(),
                VariantId = line.VariantId,
                OrderLineId = line.OrderLineId,
                Quantity = line.Quantity,
                Status = ReservationStatus.Active,
                CreatedAt = now,
                ClosedAt = null,
                CloseReason = null
            });

            var afterReserved = row.Reserved + line.Quantity;
            WriteStockLevelAudit(
                line.VariantId,
                row.OnHand,
                row.OnHand,
                row.Reserved,
                afterReserved,
                row.Level!.UpdatedAt,
                now);

            transitions.Add(new(
                line.VariantId,
                row.Product.Slug,
                row.Variant.LowStockThreshold,
                row.Available,
                row.OnHand - afterReserved));
        }

        EmitStockNotifications(transitions);
        await db.SaveChangesAsync(ct);
        return ReservationResult.Success();
    }

    public async Task<ReservationResult> SyncOrderLinesAsync(
        Guid orderId,
        OrderLineDelta delta,
        Func<CancellationToken, Task> persistOrderAndLinesAsync,
        CancellationToken ct)
    {
        RequireTransaction();
        var touchedVariantIds = delta.Removed.Select(x => x.VariantId)
            .Concat(delta.Added.Select(x => x.VariantId))
            .Concat(delta.QuantityIncreased.Select(x => x.After.VariantId))
            .Concat(delta.QuantityDecreased.Select(x => x.After.VariantId))
            .Distinct()
            .ToArray();

        var locked = await LockVariantsCoreAsync(touchedVariantIds, ct);

        // Pre-validate all added and increased lines before any mutation.
        var shortfalls = new List<OutOfStockDetail>();
        foreach (var added in delta.Added.OrderBy(x => x.VariantId))
        {
            if (!locked.TryGetValue(added.VariantId, out var row) || !IsSellable(row))
                return ReservationResult.Failure("variant_not_sellable", "Biến thể không đủ điều kiện mở bán.");
            if (row.Variant.IsUniquePiece && added.Quantity > 1)
                return ReservationResult.Failure("unique_piece_violation", "Sản phẩm độc bản chỉ được giữ số lượng 1.");
            if (row.Available < added.Quantity)
                shortfalls.Add(new(added.VariantId, row.Variant.Sku, Math.Max(0, row.Available)));
        }

        foreach (var (before, after) in delta.QuantityIncreased.OrderBy(x => x.After.VariantId))
        {
            if (!locked.TryGetValue(after.VariantId, out var row) || !IsSellable(row))
                return ReservationResult.Failure("variant_not_sellable", "Biến thể không đủ điều kiện mở bán.");
            if (row.Variant.IsUniquePiece && after.Quantity > 1)
                return ReservationResult.Failure("unique_piece_violation", "Sản phẩm độc bản chỉ được giữ số lượng 1.");
            var extra = after.Quantity - before.Quantity;
            if (row.Available < extra)
                shortfalls.Add(new(after.VariantId, row.Variant.Sku, Math.Max(0, row.Available)));
        }

        if (shortfalls.Count > 0)
            return ReservationResult.Failure("out_of_stock", "Một số sản phẩm không đủ tồn kho.", shortfalls.OrderBy(x => x.VariantId).ToArray());

        var orderLineIds = await db.Set<OrderLine>().AsNoTracking()
            .Where(l => l.OrderId == orderId)
            .Select(l => l.Id)
            .ToArrayAsync(ct);
        var activeReservations = await db.Set<Reservation>()
            .Where(r => r.OrderLineId != null && orderLineIds.Contains(r.OrderLineId.Value) && r.Status == ReservationStatus.Active)
            .ToDictionaryAsync(x => x.OrderLineId!.Value, ct);

        var now = clock.UtcNow.UtcDateTime;
        var transitions = new List<StockTransition>();
        var releasedEntries = new List<Reservation>();

        // Phase 1 (R-P205-01): mark removed & decreased reservations as 'released', decrement reserved,
        // and FLUSH to DB BEFORE deleting sales.order_lines so fk_reservations_line ON DELETE SET NULL
        // never hits ck_reservations_order_line_required (23514) and ux_reservation_active_line is freed.
        foreach (var removed in delta.Removed.OrderBy(x => x.VariantId))
        {
            if (!activeReservations.TryGetValue(removed.Id, out var res))
                throw new InvalidOperationException("Missing active reservation for removed order line.");
            var row = locked[removed.VariantId];
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.stock_levels
                   SET reserved = reserved - {res.Quantity}, updated_at = {now}
                 WHERE variant_id = {removed.VariantId} AND reserved >= {res.Quantity}
                """, ct);
            if (affected != 1)
                throw new InvalidOperationException("Unable to release reserved quantity for removed line.");

            res.Status = ReservationStatus.Released;
            res.ClosedAt = now;
            res.CloseReason = ReservationCloseReason.LineRemoved;
            releasedEntries.Add(res);

            var afterReserved = row.Reserved - res.Quantity;
            WriteStockLevelAudit(removed.VariantId, row.OnHand, row.OnHand, row.Reserved, afterReserved, row.Level!.UpdatedAt, now);
            transitions.Add(new(removed.VariantId, row.Product.Slug, row.Variant.LowStockThreshold, row.Available, row.OnHand - afterReserved));
        }

        foreach (var (before, after) in delta.QuantityDecreased.OrderBy(x => x.Before.VariantId))
        {
            if (!activeReservations.TryGetValue(before.Id, out var res))
                throw new InvalidOperationException("Missing active reservation for decreased order line.");
            var row = locked[before.VariantId];
            var releaseDelta = before.Quantity - after.Quantity;
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.stock_levels
                   SET reserved = reserved - {releaseDelta}, updated_at = {now}
                 WHERE variant_id = {before.VariantId} AND reserved >= {releaseDelta}
                """, ct);
            if (affected != 1)
                throw new InvalidOperationException("Unable to release reduced reserved quantity.");

            res.Status = ReservationStatus.Released;
            res.ClosedAt = now;
            res.CloseReason = ReservationCloseReason.QuantityReduced;
            releasedEntries.Add(res);

            var afterReserved = row.Reserved - releaseDelta;
            WriteStockLevelAudit(before.VariantId, row.OnHand, row.OnHand, row.Reserved, afterReserved, row.Level!.UpdatedAt, now);
            transitions.Add(new(before.VariantId, row.Product.Slug, row.Variant.LowStockThreshold, row.Available, row.OnHand - afterReserved));
        }

        if (releasedEntries.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            // Detach released reservations so PostgreSQL ON DELETE SET NULL on removed order_lines
            // cannot clash with stale OrderLineId values in EF's ChangeTracker.
            foreach (var entry in releasedEntries)
                db.Entry(entry).State = EntityState.Detached;
        }

        // Phase 2: persist order updates, DELETE removed order_lines, UPDATE modified, INSERT added.
        await persistOrderAndLinesAsync(ct);

        // Phase 3: insert new active reservations for decreased & added lines, update increased reservations.
        foreach (var (_, after) in delta.QuantityDecreased.OrderBy(x => x.After.VariantId))
        {
            db.Set<Reservation>().Add(new Reservation
            {
                Id = Guid.CreateVersion7(),
                VariantId = after.VariantId,
                OrderLineId = after.Id,
                Quantity = after.Quantity,
                Status = ReservationStatus.Active,
                CreatedAt = now,
                ClosedAt = null,
                CloseReason = null
            });
        }

        foreach (var added in delta.Added.OrderBy(x => x.VariantId))
        {
            var row = locked[added.VariantId];
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.stock_levels
                   SET reserved = reserved + {added.Quantity}, updated_at = {now}
                 WHERE variant_id = {added.VariantId} AND on_hand - reserved >= {added.Quantity}
                """, ct);
            if (affected != 1)
                return ReservationResult.Failure("out_of_stock", "Sản phẩm không đủ tồn kho khả dụng.", [new(added.VariantId, row.Variant.Sku, Math.Max(0, row.Available))]);

            db.Set<Reservation>().Add(new Reservation
            {
                Id = Guid.CreateVersion7(),
                VariantId = added.VariantId,
                OrderLineId = added.Id,
                Quantity = added.Quantity,
                Status = ReservationStatus.Active,
                CreatedAt = now,
                ClosedAt = null,
                CloseReason = null
            });

            var afterReserved = row.Reserved + added.Quantity;
            WriteStockLevelAudit(added.VariantId, row.OnHand, row.OnHand, row.Reserved, afterReserved, row.Level!.UpdatedAt, now);
            transitions.Add(new(added.VariantId, row.Product.Slug, row.Variant.LowStockThreshold, row.Available, row.OnHand - afterReserved));
        }

        foreach (var (before, after) in delta.QuantityIncreased.OrderBy(x => x.After.VariantId))
        {
            if (!activeReservations.TryGetValue(before.Id, out var res))
                throw new InvalidOperationException("Missing active reservation for increased order line.");
            var row = locked[after.VariantId];
            var extra = after.Quantity - before.Quantity;
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.stock_levels
                   SET reserved = reserved + {extra}, updated_at = {now}
                 WHERE variant_id = {after.VariantId} AND on_hand - reserved >= {extra}
                """, ct);
            if (affected != 1)
                return ReservationResult.Failure("out_of_stock", "Sản phẩm không đủ tồn kho khả dụng.", [new(after.VariantId, row.Variant.Sku, Math.Max(0, row.Available))]);

            res.Quantity = after.Quantity;
            var afterReserved = row.Reserved + extra;
            WriteStockLevelAudit(after.VariantId, row.OnHand, row.OnHand, row.Reserved, afterReserved, row.Level!.UpdatedAt, now);
            transitions.Add(new(after.VariantId, row.Product.Slug, row.Variant.LowStockThreshold, row.Available, row.OnHand - afterReserved));
        }

        EmitStockNotifications(transitions);
        await db.SaveChangesAsync(ct);
        return ReservationResult.Success();
    }

    public async Task ReleaseForOrderAsync(
        Guid orderId,
        string closeReason,
        CancellationToken ct)
    {
        RequireTransaction();
        var orderLineIds = await db.Set<OrderLine>().AsNoTracking()
            .Where(l => l.OrderId == orderId)
            .Select(l => l.Id)
            .ToArrayAsync(ct);
        var activeReservations = await db.Set<Reservation>()
            .Where(r => r.OrderLineId != null && orderLineIds.Contains(r.OrderLineId.Value) && r.Status == ReservationStatus.Active)
            .OrderBy(r => r.VariantId)
            .ToArrayAsync(ct);

        if (activeReservations.Length == 0) return;

        var locked = await LockVariantsCoreAsync(activeReservations.Select(x => x.VariantId).ToArray(), ct);
        var now = clock.UtcNow.UtcDateTime;
        var transitions = new List<StockTransition>(activeReservations.Length);

        foreach (var res in activeReservations)
        {
            var row = locked[res.VariantId];
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.stock_levels
                   SET reserved = reserved - {res.Quantity}, updated_at = {now}
                 WHERE variant_id = {res.VariantId} AND reserved >= {res.Quantity}
                """, ct);
            if (affected != 1)
                throw new InvalidOperationException("Unable to release reserved stock for cancelled order.");

            res.Status = ReservationStatus.Released;
            res.ClosedAt = now;
            res.CloseReason = closeReason;

            var afterReserved = row.Reserved - res.Quantity;
            WriteStockLevelAudit(res.VariantId, row.OnHand, row.OnHand, row.Reserved, afterReserved, row.Level!.UpdatedAt, now);
            transitions.Add(new(res.VariantId, row.Product.Slug, row.Variant.LowStockThreshold, row.Available, row.OnHand - afterReserved));
        }

        // AC-P2-02: zero stock_movements rows on order cancellation.
        EmitStockNotifications(transitions);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ConsumeReservationResult> ConsumeForOrderAsync(
        Guid orderId,
        IReadOnlyList<OrderLineItem> lines,
        string actorType,
        Guid? actorId,
        CancellationToken ct)
    {
        RequireTransaction();
        if (lines.Count == 0)
            return ConsumeReservationResult.Failure("validation_failed", "Đơn hàng không có dòng sản phẩm để xuất kho.");

        var orderedLines = lines.OrderBy(x => x.VariantId).ToArray();
        var locked = await LockVariantsCoreAsync(orderedLines.Select(x => x.VariantId).ToArray(), ct);

        var orderLineIds = await db.Set<OrderLine>().AsNoTracking()
            .Where(l => l.OrderId == orderId)
            .Select(l => l.Id)
            .ToArrayAsync(ct);
        var activeReservations = await db.Set<Reservation>()
            .Where(r => r.OrderLineId != null && orderLineIds.Contains(r.OrderLineId.Value) && r.Status == ReservationStatus.Active)
            .ToDictionaryAsync(x => x.OrderLineId!.Value, ct);

        foreach (var line in orderedLines)
        {
            if (!activeReservations.TryGetValue(line.Id, out var res) || res.Quantity != line.Quantity || res.VariantId != line.VariantId)
                return ConsumeReservationResult.Failure("reservation_mismatch", "Dòng đơn hàng không khớp bản ghi giữ hàng đang hoạt động.");
        }

        var now = clock.UtcNow.UtcDateTime;
        var unitCosts = new Dictionary<Guid, long?>(orderedLines.Length);
        var productIds = new HashSet<Guid>();

        foreach (var line in orderedLines)
        {
            var row = locked[line.VariantId];
            var res = activeReservations[line.Id];
            var avgCostVnd = row.AvgCostVnd;
            var afterOnHand = row.OnHand - line.Quantity;
            var afterReserved = row.Reserved - line.Quantity;

            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.stock_levels
                   SET on_hand = on_hand - {line.Quantity},
                       reserved = reserved - {line.Quantity},
                       updated_at = {now}
                 WHERE variant_id = {line.VariantId}
                   AND on_hand >= {line.Quantity}
                   AND reserved >= {line.Quantity}
                """, ct);
            if (affected != 1)
                return ConsumeReservationResult.Failure("out_of_stock", "Số dư tồn kho không đủ để xuất đơn hàng.");

            db.Set<StockMovement>().Add(new StockMovement
            {
                Id = Guid.CreateVersion7(),
                VariantId = line.VariantId,
                MovementType = "sale",
                Quantity = -line.Quantity,
                UnitCostVnd = avgCostVnd,
                OnHandAfter = afterOnHand,
                RefType = "order",
                RefId = orderId,
                Reason = null,
                ActorType = actorType,
                ActorId = actorId,
                OccurredAt = now,
                CreatedAt = now
            });

            res.Status = ReservationStatus.Consumed;
            res.ClosedAt = now;
            res.CloseReason = ReservationCloseReason.Shipped;

            WriteStockLevelAudit(line.VariantId, row.OnHand, afterOnHand, row.Reserved, afterReserved, row.Level!.UpdatedAt, now);
            unitCosts[line.Id] = avgCostVnd;
            productIds.Add(line.ProductId);
        }

        await db.SaveChangesAsync(ct);

        // BR-SEO-21: synchronize products.sold_out_at and emit StorefrontInvalidate in the same transaction.
        await soldOutSync.SyncSoldOutStateAsync(productIds, ct);
        return ConsumeReservationResult.Success(unitCosts);
    }

    public async Task RestockReturnedAsync(
        Guid refId,
        string refType,
        IReadOnlyList<RestockLine> lines,
        string actorType,
        Guid? actorId,
        string? reason,
        CancellationToken ct)
    {
        RequireTransaction();
        if (refType is not ("order" or "return"))
            throw new DomainException("invalid_stock_movement", "Loại chứng từ nhập hoàn phải là order hoặc return.");
        if (lines.Count == 0)
            throw new DomainException("invalid_stock_movement", "Cần ít nhất một dòng hàng để nhập hoàn kho.");

        var ordered = lines.OrderBy(x => x.VariantId).ToArray();
        var locked = await LockVariantsCoreAsync(ordered.Select(x => x.VariantId).ToArray(), ct);
        var now = clock.UtcNow.UtcDateTime;
        var transitions = new List<StockTransition>(ordered.Length);
        var productIds = new HashSet<Guid>();

        foreach (var line in ordered)
        {
            if (!locked.TryGetValue(line.VariantId, out var row))
                throw new DomainException("not_found", "Không tìm thấy biến thể để nhập hoàn kho.");

            var before = new StockBalance(row.OnHand, row.Reserved, row.AvgCostVnd);
            var change = new StockChange("return_restock", line.Quantity, line.UnitCostVnd, refType, refId, reason, actorType, actorId);
            var error = StockRules.Calculate(before, row.Variant.IsUniquePiece, change, out var after);
            if (error is not null)
                throw new DomainException(error, "Không thể nhập hoàn tồn kho theo quy tắc kho.");

            if (row.Level is null)
            {
                db.Set<StockLevel>().Add(new StockLevel
                {
                    VariantId = line.VariantId,
                    OnHand = after.OnHand,
                    Reserved = after.Reserved,
                    AvgCostVnd = after.AvgCostVnd,
                    UpdatedAt = now
                });
            }
            else
            {
                var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE inventory.stock_levels
                       SET on_hand = {after.OnHand},
                           avg_cost_vnd = {after.AvgCostVnd},
                           updated_at = {now}
                     WHERE variant_id = {line.VariantId} AND on_hand = {row.OnHand}
                    """, ct);
                if (affected != 1)
                    throw new InvalidOperationException("Unable to update stock level during return restock.");

                WriteStockLevelAudit(line.VariantId, row.OnHand, after.OnHand, row.Reserved, after.Reserved, row.Level.UpdatedAt, now);
            }

            db.Set<StockMovement>().Add(new StockMovement
            {
                Id = Guid.CreateVersion7(),
                VariantId = line.VariantId,
                MovementType = "return_restock",
                Quantity = line.Quantity,
                UnitCostVnd = line.UnitCostVnd,
                OnHandAfter = after.OnHand,
                RefType = refType,
                RefId = refId,
                Reason = reason,
                ActorType = actorType,
                ActorId = actorId,
                OccurredAt = now,
                CreatedAt = now
            });

            transitions.Add(new(line.VariantId, row.Product.Slug, row.Variant.LowStockThreshold, before.Available, after.Available));
            productIds.Add(row.Product.Id);
        }

        EmitStockNotifications(transitions);
        await db.SaveChangesAsync(ct);

        // BR-SEO-21: clear products.sold_out_at if unique piece returns to on_hand > 0 and emit StorefrontInvalidate.
        await soldOutSync.SyncSoldOutStateAsync(productIds, ct);
    }

    private async Task<Dictionary<Guid, LockedVariantRow>> LockVariantsCoreAsync(
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken ct)
    {
        var ids = variantIds.Distinct().Order().ToArray();
        if (ids.Length == 0) return [];

        var parentIds = await db.Set<Variant>().AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => x.ProductId)
            .Distinct()
            .OrderBy(x => x)
            .ToArrayAsync(ct);
        if (parentIds.Length == 0) return [];

        var products = await db.Set<Product>().FromSqlInterpolated(
            $"SELECT *, xmin FROM catalog.products WHERE id = ANY({parentIds}) ORDER BY id FOR NO KEY UPDATE")
            .AsNoTracking().ToArrayAsync(ct);
        var productById = products.ToDictionary(x => x.Id);
        var lockedProductIds = products.Select(x => x.Id).Order().ToArray();

        var variants = await db.Set<Variant>().FromSqlInterpolated(
            $"SELECT *, xmin FROM catalog.variants WHERE id = ANY({ids}) AND product_id = ANY({lockedProductIds}) ORDER BY id FOR UPDATE")
            .AsNoTracking().ToArrayAsync(ct);
        var lockedVariantIds = variants.Select(x => x.Id).Order().ToArray();

        var levels = (await db.Set<StockLevel>().FromSqlInterpolated(
            $"SELECT * FROM inventory.stock_levels WHERE variant_id = ANY({lockedVariantIds}) ORDER BY variant_id FOR UPDATE")
            .AsNoTracking().ToArrayAsync(ct)).ToDictionary(x => x.VariantId);

        var result = new Dictionary<Guid, LockedVariantRow>(variants.Length);
        foreach (var variant in variants)
        {
            result[variant.Id] = new(productById[variant.ProductId], variant, levels.GetValueOrDefault(variant.Id));
        }
        return result;
    }

    private static bool IsSellable(LockedVariantRow row) =>
        string.Equals(row.Product.Status, "active", StringComparison.Ordinal)
        && string.Equals(row.Variant.Status, "active", StringComparison.Ordinal)
        && row.Variant.PriceVnd is >= CheckoutCalculator.MinSellablePriceVnd and <= CheckoutCalculator.MaxSellablePriceVnd;

    private void WriteStockLevelAudit(
        Guid variantId,
        int beforeOnHand,
        int afterOnHand,
        int beforeReserved,
        int afterReserved,
        DateTime beforeUpdatedAt,
        DateTime afterUpdatedAt)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (beforeOnHand != afterOnHand)
        {
            fields["on_hand"] = new
            {
                before = AuditValuePolicy.Filter("inventory.stock_levels", null, "OnHand", beforeOnHand),
                after = AuditValuePolicy.Filter("inventory.stock_levels", null, "OnHand", afterOnHand)
            };
        }
        if (beforeReserved != afterReserved)
        {
            fields["reserved"] = new
            {
                before = AuditValuePolicy.Filter("inventory.stock_levels", null, "Reserved", beforeReserved),
                after = AuditValuePolicy.Filter("inventory.stock_levels", null, "Reserved", afterReserved)
            };
        }
        if (beforeUpdatedAt != afterUpdatedAt)
        {
            fields["updated_at"] = new
            {
                before = AuditValuePolicy.Filter("inventory.stock_levels", null, "UpdatedAt", beforeUpdatedAt),
                after = AuditValuePolicy.Filter("inventory.stock_levels", null, "UpdatedAt", afterUpdatedAt)
            };
        }
        if (fields.Count == 0) return;

        var actorType = auditContext.ActorType;
        var actorId = auditContext.ActorId;
        if (actorType is not ("staff" or "customer" or "system")
            || (actorType == "system" && actorId is not null)
            || (actorType != "system" && actorId is null))
            throw new InvalidOperationException("Audit actor context is invalid.");

        var correlation = auditContext.CorrelationId;
        if (correlation is not null && !Guid.TryParseExact(correlation, "N", out _)
            && !Guid.TryParseExact(correlation, "D", out _))
            correlation = null;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = afterUpdatedAt,
            ActorType = actorType,
            ActorId = actorId,
            EntityType = "inventory.stock_levels",
            EntityId = variantId,
            EntityKey = null,
            Action = "update",
            Changes = JsonSerializer.Serialize(fields),
            CorrelationId = correlation
        });
    }

    private void EmitStockNotifications(IReadOnlyList<StockTransition> transitions)
    {
        var tags = transitions
            .Where(t => (t.BeforeAvailable > 0) != (t.AfterAvailable > 0))
            .Select(t => "product:" + t.ProductSlug)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var batch in tags.Chunk(199))
            outbox.Enqueue(StorefrontInvalidate.Message, JsonSerializer.Serialize(new StorefrontInvalidate([.. batch, "catalog"])));

        foreach (var final in transitions.GroupBy(t => t.VariantId).OrderBy(g => g.Key).Select(g => g.Last()))
        {
            var key = InventoryAlert.LowStockDeduplicationKey(final.VariantId);
            if (StockRules.IsLowStock(final.AfterAvailable, final.LowStockThreshold))
                outbox.Enqueue(InventoryAlert.Message, InventoryAlert.Serialize(new(InventoryAlert.LowStock, final.VariantId)), key);
            else
                outbox.ClearDeduplicated(key);
        }
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Inventory reservation operations require an active command transaction.");
    }
}
