using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm;
using TeaPremium.Application.Iam;
using TeaPremium.Application.Orders.Contracts;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Crm;

public sealed class EfInteractionQueries(
    AppDbContext db,
    IStaffDirectory staffDirectory,
    ICustomerOrderQueries orderQueries) : IInteractionQueries
{
    public async Task<CatalogPage<CustomerInteractionView>> ListByCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Set<Interaction>().AsNoTracking()
            .Where(x => x.CustomerId == customerId);

        var total = await query.LongCountAsync(ct);

        var rows = await query
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(ct);

        if (rows.Length == 0)
        {
            return new CatalogPage<CustomerInteractionView>([], page, pageSize, total);
        }

        // R-P201-03: left-lookup staff display names via read-only directory so system/unknown staff_id rows are never dropped.
        var staffNames = await staffDirectory.ResolveDisplayNamesAsync(rows.Select(x => x.StaffId).ToArray(), ct);
        var orderIds = rows.Where(x => x.OrderId is not null).Select(x => x.OrderId!.Value).ToArray();
        var orderCodes = await orderQueries.ResolveOrderCodesAsync(orderIds, ct);

        var items = rows.Select(row =>
        {
            staffNames.TryGetValue(row.StaffId, out var staffDisplayName);
            string? orderCode = null;
            if (row.OrderId is { } oid)
            {
                orderCodes.TryGetValue(oid, out orderCode);
            }

            return new CustomerInteractionView(
                row.Id,
                row.CustomerId,
                row.StaffId,
                staffDisplayName,
                row.Channel,
                row.Kind,
                row.Content,
                row.OccurredAt,
                row.OrderId,
                orderCode,
                row.CampaignId,
                row.FollowUpAt,
                row.FollowUpDoneAt,
                row.FollowUpResult,
                row.CreatedAt);
        }).ToArray();

        return new CatalogPage<CustomerInteractionView>(items, page, pageSize, total);
    }

    public async Task<CatalogPage<FollowUpItemView>> ListFollowUpsAsync(FollowUpWindow window, int page, int pageSize, CancellationToken ct)
    {
        // Matches partial index ix_interactions_follow_up (follow_up_at IS NOT NULL AND follow_up_done_at IS NULL)
        // and excludes erased/merged customers per ASSUMPTION-P204-01/03 = A.
        var query =
            from i in db.Set<Interaction>().AsNoTracking()
            join c in db.Set<Customer>().AsNoTracking() on i.CustomerId equals c.Id
            where i.FollowUpAt != null
                && i.FollowUpDoneAt == null
                && i.FollowUpAt < window.CutoffExclusiveUtc
                && c.ContactStatus != "erased"
                && c.MergedIntoCustomerId == null
            select new
            {
                Interaction = i,
                CustomerCode = c.Code,
                CustomerFullName = c.FullName,
                CustomerPhoneE164 = c.PhoneE164,
                CustomerZaloName = c.ZaloName,
                CustomerGroup = c.CustomerGroup,
                CustomerIsVip = c.IsVip,
                CustomerContactStatus = c.ContactStatus
            };

        var total = await query.LongCountAsync(ct);

        var rows = await query
            .OrderBy(x => x.Interaction.FollowUpAt)
            .ThenBy(x => x.Interaction.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(ct);

        if (rows.Length == 0)
        {
            return new CatalogPage<FollowUpItemView>([], page, pageSize, total);
        }

        var staffNames = await staffDirectory.ResolveDisplayNamesAsync(rows.Select(x => x.Interaction.StaffId).ToArray(), ct);
        var orderIds = rows.Where(x => x.Interaction.OrderId is not null).Select(x => x.Interaction.OrderId!.Value).ToArray();
        var orderCodes = await orderQueries.ResolveOrderCodesAsync(orderIds, ct);

        var items = rows.Select(row =>
        {
            var i = row.Interaction;
            staffNames.TryGetValue(i.StaffId, out var staffDisplayName);
            string? orderCode = null;
            if (i.OrderId is { } oid)
            {
                orderCodes.TryGetValue(oid, out orderCode);
            }

            var followUpAt = i.FollowUpAt!.Value;
            return new FollowUpItemView(
                i.Id,
                i.CustomerId,
                row.CustomerCode,
                row.CustomerFullName,
                row.CustomerPhoneE164,
                row.CustomerZaloName,
                row.CustomerGroup,
                row.CustomerIsVip,
                row.CustomerContactStatus,
                i.StaffId,
                staffDisplayName,
                i.Channel,
                i.Kind,
                i.Content,
                i.OccurredAt,
                i.OrderId,
                orderCode,
                followUpAt,
                followUpAt < window.NowUtc,
                i.CreatedAt);
        }).ToArray();

        return new CatalogPage<FollowUpItemView>(items, page, pageSize, total);
    }
}
