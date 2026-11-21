using System.Data;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Domain.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

public sealed partial class EfStoreCatalogRead
{
    private sealed class TaxonomyRow
    {
        public required string Slug { get; init; }
        public required string Name { get; init; }
        public string? Summary { get; init; }
        public string? Html { get; init; }
        public string? Body { get; init; }
        public string? Kind { get; init; }
        public Guid? Media { get; init; }
        public string? Title { get; init; }
        public string? Description { get; init; }
        public bool Noindex { get; init; }
        public int Position { get; init; }
    }

    private IQueryable<TaxonomyRow> Taxonomy(StoreTaxonomy kind) => kind switch
    {
        StoreTaxonomy.Category => VisibleCategories.Select(c => new TaxonomyRow
        {
            Slug = c.Slug,
            Name = c.Name,
            Html = c.DescriptionHtml,
            Body = c.BodyHtml,
            Media = c.CoverMediaId,
            Title = c.SeoTitle,
            Description = c.SeoDescription,
            Noindex = c.RobotsNoindex,
            Position = c.SortOrder
        }),
        StoreTaxonomy.Collection => db.Set<CollectionRecord>().Where(c => c.Status == "visible").Select(c => new TaxonomyRow
        {
            Slug = c.Slug,
            Name = c.Name,
            Html = c.DescriptionHtml,
            Body = c.BodyHtml,
            Media = c.CoverMediaId,
            Title = c.SeoTitle,
            Description = c.SeoDescription,
            Noindex = c.RobotsNoindex,
            Position = c.SortOrder
        }),
        StoreTaxonomy.Material => db.Set<Material>().Where(c => c.Status == "published").Select(c => new TaxonomyRow
        {
            Slug = c.Slug,
            Name = c.Name,
            Summary = c.Summary,
            Html = c.DescriptionHtml,
            Kind = c.Kind,
            Media = c.CoverMediaId,
            Title = c.SeoTitle,
            Description = c.SeoDescription,
            Noindex = c.RobotsNoindex,
            Position = c.SortOrder
        }),
        StoreTaxonomy.Artisan => db.Set<Artisan>().Where(c => c.Status == "published").Select(c => new TaxonomyRow
        {
            Slug = c.Slug,
            Name = c.Name,
            Summary = c.Title,
            Html = c.BioHtml,
            Media = c.PortraitMediaId,
            Title = c.SeoTitle,
            Description = c.SeoDescription,
            Noindex = c.RobotsNoindex,
            Position = 0
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };


    public async Task<StoreTaxonomySummary[]> TaxonomyListAsync(StoreTaxonomy kind, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var rows = await Taxonomy(kind).OrderBy(x => x.Position).ThenBy(x => x.Slug)
            .Select(x => new { x.Slug, x.Name, x.Summary, x.Media }).ToArrayAsync(ct);
        var assets = await ImagesAsync(rows.Select(x => x.Media), ct);
        return rows.Select(x => new StoreTaxonomySummary(x.Slug, x.Name, x.Summary, Image(assets, x.Media))).ToArray();
    }

    public async Task<StoreTaxonomyDetail?> TaxonomyAsync(StoreTaxonomy kind, string slug, CancellationToken ct)
    {
        if (slug.Contains('\0')) return null;
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var row = await Taxonomy(kind).SingleOrDefaultAsync(x => x.Slug == slug, ct);
        if (row is null) return null;
        var assets = await ImagesAsync([row.Media], ct);
        var seoKind = kind switch
        {
            StoreTaxonomy.Material => CatalogKind.Material,
            StoreTaxonomy.Artisan => CatalogKind.Artisan,
            StoreTaxonomy.Collection => CatalogKind.Collection,
            _ => CatalogKind.Category
        };
        var entry = new CatalogEntry { Name = row.Name, Summary = row.Summary, SeoTitle = row.Title, SeoDescription = row.Description };
        var seo = CatalogSeo.Resolve(entry, seoKind, content.Sanitize(null, row.Html).Text, await catalog.BrandAsync(ct));
        return new(row.Slug, row.Name, row.Summary, row.Html, row.Body, row.Kind, Image(assets, row.Media),
            new(seo.Title, seo.Description, SiteIndexable && !row.Noindex));
    }

    public async Task<StoreNavigation> NavigationAsync(CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var categories = await VisibleCategories.OrderBy(x => x.SortOrder).ThenBy(x => x.Slug)
            .Select(x => new { x.Id, x.ParentId, x.Slug, x.Name }).ToArrayAsync(ct);
        var byParent = categories.ToLookup(x => x.ParentId);
        StoreCategoryNode[] Children(Guid? parent) => byParent[parent]
            .Select(x => new StoreCategoryNode(x.Slug, x.Name, Children(x.Id))).ToArray();
        var collections = await db.Set<CollectionRecord>().Where(x => x.Status == "visible")
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Slug).Select(x => new StoreReference(x.Slug, x.Name)).ToArrayAsync(ct);
        return new(Children(null), collections);
    }
}
