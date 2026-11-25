using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Crm;

public sealed class EfCustomerQueries(AppDbContext db, IAdministrativeReferenceData adminRef) : ICustomerQueries
{
    public async Task<CatalogPage<CustomerSummaryView>> ListAsync(CustomerListFilter filter, CancellationToken ct)
    {
        var query = db.Set<Customer>().AsNoTracking()
            .Where(x => x.MergedIntoCustomerId == null);

        if (filter.Q is { Length: > 0 } rawQ)
        {
            var normalized = await db.Database
                .SqlQuery<string>($"SELECT public.f_unaccent({rawQ}) AS \"Value\"")
                .SingleAsync(ct);

            if (filter.PhoneSearchDigits is { Length: > 0 } digits)
            {
                query = query.Where(x => x.SearchText.Contains(normalized)
                    || (x.PhoneE164 != null && x.PhoneE164.EndsWith(digits)));
            }
            else
            {
                query = query.Where(x => x.SearchText.Contains(normalized));
            }
        }

        if (filter.Group is not null)
        {
            query = query.Where(x => x.CustomerGroup == filter.Group);
        }

        if (filter.TagId is { } tagId)
        {
            query = query.Where(x => db.Set<CustomerTag>().Any(t => t.CustomerId == x.Id && t.TagId == tagId));
        }

        if (filter.InterestType is not null)
        {
            if (filter.InterestType == "price_band")
            {
                var band = filter.InterestPriceBand!;
                query = query.Where(x => db.Set<CustomerInterest>()
                    .Any(i => i.CustomerId == x.Id && i.InterestType == "price_band" && i.PriceBand == band));
            }
            else
            {
                var kind = filter.InterestType;
                var refId = filter.InterestRefId!.Value;
                query = query.Where(x => db.Set<CustomerInterest>()
                    .Any(i => i.CustomerId == x.Id && i.InterestType == kind && i.RefId == refId));
            }
        }

        if (filter.LastDeliveredAfterUtc is { } afterUtc)
        {
            query = query.Where(x => x.LastDeliveredAt != null && x.LastDeliveredAt >= afterUtc);
        }

        if (filter.LastDeliveredBeforeUtc is { } beforeUtc)
        {
            query = query.Where(x => x.LastDeliveredAt != null && x.LastDeliveredAt < beforeUtc);
        }

        if (filter.MinLtv is { } minLtv)
        {
            query = query.Where(x => x.LifetimeValueVnd >= minLtv);
        }

        if (filter.ContactStatus is not null)
        {
            query = query.Where(x => x.ContactStatus == filter.ContactStatus);
        }

        if (filter.ConsentChannel is not null && filter.ConsentPurpose is not null)
        {
            var channel = filter.ConsentChannel;
            var purpose = filter.ConsentPurpose;
            query = query.Where(x => db.Set<Consent>()
                .Any(c => c.CustomerId == x.Id && c.Channel == channel && c.Purpose == purpose && c.Status == "granted"));
        }

        var total = await query.LongCountAsync(ct);

        IOrderedQueryable<Customer> ordered = filter.Sort switch
        {
            "createdAt:asc" => query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id),
            "lastDeliveredAt:desc" => query.OrderByDescending(x => x.LastDeliveredAt != null).ThenByDescending(x => x.LastDeliveredAt).ThenByDescending(x => x.Id),
            "lastDeliveredAt:asc" => query.OrderByDescending(x => x.LastDeliveredAt != null).ThenBy(x => x.LastDeliveredAt).ThenBy(x => x.Id),
            "lifetimeValueVnd:desc" => query.OrderByDescending(x => x.LifetimeValueVnd).ThenByDescending(x => x.Id),
            "lifetimeValueVnd:asc" => query.OrderBy(x => x.LifetimeValueVnd).ThenBy(x => x.Id),
            "fullName:asc" => query.OrderBy(x => x.FullName).ThenBy(x => x.Id),
            "fullName:desc" => query.OrderByDescending(x => x.FullName).ThenByDescending(x => x.Id),
            _ => query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
        };

        var rows = await ordered
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToArrayAsync(ct);

        var customerIds = rows.Select(x => x.Id).ToArray();
        var tagsByCustomer = customerIds.Length == 0
            ? new Dictionary<Guid, List<CustomerTagView>>()
            : (await (
                from ctRow in db.Set<CustomerTag>().AsNoTracking()
                join t in db.Set<Tag>().AsNoTracking() on ctRow.TagId equals t.Id
                where customerIds.Contains(ctRow.CustomerId)
                orderby t.Name, t.Id
                select new { ctRow.CustomerId, t.Id, t.Name, t.Color })
                .ToArrayAsync(ct))
                .GroupBy(x => x.CustomerId)
                .ToDictionary(g => g.Key, g => g.Select(x => new CustomerTagView(x.Id, x.Name, x.Color)).ToList());

        var items = rows.Select(c => new CustomerSummaryView(
            c.Id,
            c.Code,
            c.FullName,
            c.PhoneE164,
            c.Email,
            c.ZaloName,
            c.CustomerGroup,
            c.IsVip,
            c.Origin,
            c.AcquisitionSource,
            c.ContactStatus,
            c.HadLegacyPurchase,
            c.DeliveredOrdersCount,
            c.LifetimeValueVnd,
            c.LastDeliveredAt,
            c.LastContactedAt,
            c.CreatedAt,
            c.UpdatedAt,
            CatalogVersions.Encode(c.Version),
            tagsByCustomer.TryGetValue(c.Id, out var tags) ? tags : [])).ToArray();

        return new CatalogPage<CustomerSummaryView>(items, filter.Page, filter.PageSize, total);
    }

    public async Task<CustomerDetailView?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        var customer = await db.Set<Customer>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (customer is null)
        {
            return null;
        }

        var tags = await (
            from ctRow in db.Set<CustomerTag>().AsNoTracking()
            join t in db.Set<Tag>().AsNoTracking() on ctRow.TagId equals t.Id
            where ctRow.CustomerId == id
            orderby t.Name, t.Id
            select new CustomerTagView(t.Id, t.Name, t.Color))
            .ToArrayAsync(ct);

        var addresses = await ListAddressesAsync(id, ct);

        var interests = await db.Set<CustomerInterest>().AsNoTracking()
            .Where(x => x.CustomerId == id)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Id)
            .Select(x => new CustomerInterestView(x.Id, x.InterestType, x.RefId, x.PriceBand, x.Source, x.Score, x.UpdatedAt))
            .ToArrayAsync(ct);

        var consents = await db.Set<Consent>().AsNoTracking()
            .Where(x => x.CustomerId == id)
            .OrderBy(x => x.Channel)
            .ThenBy(x => x.Purpose)
            .ThenBy(x => x.Id)
            .Select(x => new CustomerConsentView(x.Id, x.Channel, x.Purpose, x.Status, x.Source, x.CapturedAt))
            .ToArrayAsync(ct);

        var account = await db.Set<CustomerAccount>().AsNoTracking()
            .Where(x => x.CustomerId == id)
            .Select(x => new CustomerAccountSummaryView(
                true,
                x.Status,
                x.PhoneVerifiedAt,
                x.EmailVerifiedAt,
                x.HistoryUnlockedAt,
                x.LastLoginAt))
            .SingleOrDefaultAsync(ct)
            ?? new CustomerAccountSummaryView(false, null, null, null, null, null);

        var stats = new CustomerStatsView(
            customer.HadLegacyPurchase,
            customer.DeliveredOrdersCount,
            customer.LifetimeValueVnd,
            customer.FirstDeliveredAt,
            customer.LastDeliveredAt,
            customer.LastContactedAt);

        return new CustomerDetailView(
            customer.Id,
            customer.Code,
            customer.FullName,
            customer.PhoneE164,
            customer.Email,
            customer.ZaloName,
            customer.CustomerGroup,
            customer.IsVip,
            customer.Origin,
            customer.AcquisitionSource,
            customer.ReferredByCustomerId,
            customer.ContactStatus,
            customer.NoteSummary,
            customer.MergedIntoCustomerId,
            customer.ErasedAt,
            customer.CreatedAt,
            customer.UpdatedAt,
            CatalogVersions.Encode(customer.Version),
            stats,
            tags,
            addresses,
            interests,
            consents,
            account);
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct)
        => db.Set<Customer>().AsNoTracking().AnyAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<CustomerTagView>> ListTagsAsync(CancellationToken ct)
        => await db.Set<Tag>().AsNoTracking()
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Select(x => new CustomerTagView(x.Id, x.Name, x.Color))
            .ToArrayAsync(ct);

    public async Task<IReadOnlyList<CustomerAddressView>> ListAddressesAsync(Guid customerId, CancellationToken ct)
    {
        var rows = await db.Set<CustomerAddress>().AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToArrayAsync(ct);

        if (rows.Length == 0)
        {
            return [];
        }

        var keys = rows.Select(r => (r.ProvinceCode, r.WardCode)).ToArray();
        var locations = await adminRef.ResolveLocationsAsync(keys, ct);

        return rows.Select(r =>
        {
            locations.TryGetValue((r.ProvinceCode, r.WardCode), out var loc);
            return new CustomerAddressView(
                r.Id,
                r.CustomerId,
                r.Label,
                r.ReceiverName,
                r.ReceiverPhone,
                r.ProvinceCode,
                loc?.ProvinceName ?? "",
                r.WardCode,
                loc?.WardName,
                r.WardNameText,
                r.AddressLine,
                r.IsDefault,
                loc?.IsLocationActive ?? false,
                r.CreatedAt,
                r.UpdatedAt);
        }).ToArray();
    }

    public async Task<CustomerAddressView?> GetAddressAsync(Guid customerId, Guid addressId, CancellationToken ct)
    {
        var row = await db.Set<CustomerAddress>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == addressId && x.CustomerId == customerId, ct);
        if (row is null)
        {
            return null;
        }

        var locations = await adminRef.ResolveLocationsAsync([(row.ProvinceCode, row.WardCode)], ct);
        locations.TryGetValue((row.ProvinceCode, row.WardCode), out var loc);

        return new CustomerAddressView(
            row.Id,
            row.CustomerId,
            row.Label,
            row.ReceiverName,
            row.ReceiverPhone,
            row.ProvinceCode,
            loc?.ProvinceName ?? "",
            row.WardCode,
            loc?.WardName,
            row.WardNameText,
            row.AddressLine,
            row.IsDefault,
            loc?.IsLocationActive ?? false,
            row.CreatedAt,
            row.UpdatedAt);
    }
}
