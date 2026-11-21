using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.Infrastructure.Catalog;

public sealed partial class EfStoreCatalogRead
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Filter(Guid Id, string Scope, Guid? OptionId, decimal? Min, decimal? Max);
    private sealed record CardRow(string Slug, string Name, string? Subtitle, string? ShortDescription,
        Guid VariantId, long EffectivePrice, long BasePrice, int Stock, Guid? CoverId, string? Alt);

    public async Task<StoreProductPage?> ProductsAsync(StoreProductQuery query, CancellationToken ct)
    {
        var at = clock.UtcNow.UtcDateTime;
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var filters = await ResolveFiltersAsync(query, ct);
        if (filters is null) return null;

        var tokens = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var normalized = await db.Database.SqlQuery<string>($"SELECT public.f_unaccent({query.Q}) AS \"Value\"").SingleAsync(ct);
            tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        }
        var result = await db.Database.SqlQuery<string>(ProductsSql(query, filters, tokens, at)).ToListAsync(ct);
        using var payload = JsonDocument.Parse(result.Single());
        var rows = payload.RootElement.GetProperty("items").Deserialize<CardRow[]>(Json)!;
        var images = await ImagesAsync(rows.Select(row => row.CoverId), ct);
        var items = rows.Select(row => new StoreProductCard(row.Slug, row.Name, row.Subtitle, row.ShortDescription,
            row.VariantId, Offer(row.BasePrice, row.EffectivePrice), Availability(row.Stock), LowStock(row.Stock),
            Image(images, row.CoverId, row.Alt))).ToArray();
        return new(items, query.Page, query.PageSize, payload.RootElement.GetProperty("total").GetInt64(),
            payload.RootElement.GetProperty("facets").Deserialize<StoreFacet[]>(Json)!);
    }

    private async Task<Filter[]?> ResolveFiltersAsync(StoreProductQuery query, CancellationToken ct)
    {
        if (query.Attributes.Length == 0) return [];
        var codes = query.Attributes.Select(x => x.Code).ToArray();
        var definitions = await (
            from definition in db.Set<AttributeDefinition>()
            join type in db.Set<ProductType>() on definition.ProductTypeId equals type.Id
            where definition.IsActive && definition.IsVisible && definition.IsFilterable
                && codes.Contains(definition.Code) && (query.Type == null || type.Code == query.Type)
            select new
            {
                definition.Id,
                definition.Code,
                definition.Scope,
                definition.DataType,
                definition.Unit,
                Options = db.Set<AttributeOption>().Where(o => o.AttributeId == definition.Id && o.IsActive)
                    .Select(o => new { o.Id, o.Code }).ToArray()
            }).AsSingleQuery().ToListAsync(ct);
        var result = new List<Filter>();
        foreach (var input in query.Attributes)
        {
            var matches = definitions.Where(x => x.Code == input.Code).ToArray();
            if (matches.Select(x => (x.Scope, x.DataType, x.Unit)).Distinct().Count() != 1) return null;
            var before = result.Count;
            foreach (var definition in matches)
            {
                var option = definition.Options.SingleOrDefault(x => x.Code == input.Option);
                if (definition.DataType == "option" && option is not null)
                    result.Add(new(definition.Id, definition.Scope, option.Id, null, null));
                if (definition.DataType == "number" && input.Min.HasValue && input.Min <= input.Max)
                    result.Add(new(definition.Id, definition.Scope, null, input.Min, input.Max));
            }
            // Keep the key even when a particular product type lacks the selected option.
            if (result.Count == before) return null;
        }
        return result.ToArray();
    }

    private static FormattableString ProductsSql(StoreProductQuery q, Filter[] filters, string[] tokens, DateTime at)
    {
        var input = JsonSerializer.Serialize(filters, Json);
        var codes = q.Attributes.Select(x => x.Code).ToArray();
        var offset = ((long)q.Page - 1) * q.PageSize;
        // BR-CAT-10/11/15, BR-PRC-01/02, BR-INV-02. Bind all user input, including sort and the
        // JSON containing validated attribute IDs. strpos is avoided so trigram LIKE remains usable.
        return $"""
        WITH RECURSIVE args AS (
          SELECT {q.Category}::text category, {q.Collection}::text collection, {q.Type}::text type,
            {q.Material}::text material, {q.Artisan}::text artisan, {q.PriceMin}::bigint price_min,
            {q.PriceMax}::bigint price_max, {q.InStock}::boolean in_stock, {at}::timestamptz instant,
            {q.Sort}::text sort, {codes}::text[] codes
        ),
        visible AS (
          SELECT id, parent_id, slug, name, 1 depth FROM catalog.categories WHERE parent_id IS NULL AND status='visible'
          UNION ALL
          SELECT c.id,c.parent_id,c.slug,c.name,v.depth+1 FROM catalog.categories c JOIN visible v ON c.parent_id=v.id
          WHERE c.status='visible' AND v.depth<3
        ),
        category_paths AS (
          SELECT id descendant,id ancestor FROM visible
          UNION ALL
          SELECT path.descendant,parent.id FROM category_paths path
          JOIN visible node ON node.id=path.ancestor JOIN visible parent ON parent.id=node.parent_id
        ),
        descendants AS (
          SELECT v.id FROM visible v,args a WHERE v.slug=a.category
          UNION ALL SELECT v.id FROM visible v JOIN descendants d ON v.parent_id=d.id
        ),
        tokens AS (
          SELECT replace(replace(replace(token, chr(92), chr(92)||chr(92)), '%', chr(92)||'%'), '_', chr(92)||'_') pattern
          FROM unnest({tokens}::text[]) token
          WHERE token <> ''
        ),
        filters AS (
          SELECT f.*, d.code FROM jsonb_to_recordset({input}::jsonb)
            AS f(id uuid, scope text, "optionId" uuid, min numeric, max numeric)
          JOIN catalog.attribute_definitions d ON d.id=f.id
        ),
        candidates AS (
          SELECT p.id,p.slug,p.name,p.subtitle,p.short_description,p.published_at,p.created_at,
                 t.id type_id,t.code type_code,t.name type_name,m.slug material_slug,m.name material_name,
                 ar.slug artisan_slug,ar.name artisan_name
          FROM catalog.products p JOIN catalog.product_types t ON t.id=p.product_type_id
          LEFT JOIN catalog.materials m ON m.id=p.material_id AND m.status='published'
          LEFT JOIN catalog.artisans ar ON ar.id=p.artisan_id AND ar.status='published'
          CROSS JOIN args a
          WHERE p.status='active' AND (a.type IS NULL OR a.type=t.code)
            AND (a.material IS NULL OR a.material=m.slug)
            AND (a.artisan IS NULL OR a.artisan=ar.slug)
            AND (a.category IS NULL OR EXISTS (
              SELECT 1 FROM catalog.product_categories pc JOIN descendants d ON d.id=pc.category_id WHERE pc.product_id=p.id))
            AND (a.collection IS NULL OR EXISTS (
              SELECT 1 FROM catalog.collection_products cp JOIN catalog.collections c ON c.id=cp.collection_id
              WHERE cp.product_id=p.id AND c.status='visible' AND c.slug=a.collection))
            AND NOT EXISTS (SELECT 1 FROM tokens WHERE p.search_text NOT LIKE '%'||pattern||'%')
            AND NOT EXISTS (
              SELECT 1 FROM unnest(a.codes) k(code)
              WHERE EXISTS (SELECT 1 FROM filters f WHERE f.code=k.code AND f.scope='product')
                AND NOT EXISTS (
                  SELECT 1 FROM filters f JOIN catalog.product_attribute_values v ON v.attribute_id=f.id
                  WHERE f.code=k.code AND f.scope='product' AND v.product_id=p.id
                    AND (v.option_id=f."optionId" OR v.value_number BETWEEN f.min AND f.max)))
        ),
        priced AS (
          SELECT v.product_id,v.id,v.position,p.type_id,p.type_code,v.price_vnd base_price,
            least(v.price_vnd, s.sale_price_vnd) effective_price,
            coalesce(l.on_hand-l.reserved,0) stock
          FROM candidates p JOIN catalog.variants v ON v.product_id=p.id
          CROSS JOIN args a
          LEFT JOIN pricing.sale_prices s ON s.variant_id=v.id AND s.starts_at<=a.instant AND a.instant<s.ends_at
          LEFT JOIN inventory.stock_levels l ON l.variant_id=v.id
          WHERE v.status='active' AND v.price_vnd BETWEEN 1 AND 10000000000
        ),
        matching AS (
          SELECT v.* FROM priced v CROSS JOIN args a
          WHERE (a.price_min IS NULL OR v.effective_price>=a.price_min)
            AND (a.price_max IS NULL OR v.effective_price<=a.price_max)
            AND (NOT a.in_stock OR v.stock>0)
            AND NOT EXISTS (
              SELECT 1 FROM unnest(a.codes) k(code)
              WHERE EXISTS (SELECT 1 FROM filters f WHERE f.code=k.code AND f.scope='variant')
                AND NOT EXISTS (
                  SELECT 1 FROM filters f JOIN catalog.variant_attribute_values x ON x.attribute_id=f.id
                  WHERE f.code=k.code AND f.scope='variant' AND x.variant_id=v.id
                    AND (x.option_id=f."optionId" OR x.value_number BETWEEN f.min AND f.max)))
        ),
        chosen AS (
          SELECT DISTINCT ON (product_id) * FROM matching
          ORDER BY product_id,(stock>0) DESC,effective_price,position,id
        ),
        ranked AS (
          SELECT p.*,v.id variant_id,v.effective_price,v.base_price,v.stock,
            row_number() OVER (ORDER BY
              CASE WHEN a.sort='price_asc' THEN v.effective_price END,
              CASE WHEN a.sort='price_desc' THEN v.effective_price END DESC,
              coalesce(p.published_at,p.created_at) DESC,p.id) rank
          FROM chosen v JOIN candidates p ON p.id=v.product_id CROSS JOIN args a
        ),
        page AS (
          SELECT p.*,pm.asset_id cover_id,pm.alt_text
          FROM (SELECT * FROM ranked ORDER BY rank LIMIT {q.PageSize} OFFSET {offset}) p
          LEFT JOIN catalog.product_media pm ON pm.product_id=p.id AND pm.is_cover AND (
            pm.variant_id IS NULL OR EXISTS (SELECT 1 FROM priced x WHERE x.id=pm.variant_id AND x.product_id=p.id))
        ),
        attribute_values AS (
          SELECT x.product_id,p.type_id,p.type_code,x.attribute_id,x.option_id,x.value_number
          FROM ranked p JOIN catalog.product_attribute_values x ON x.product_id=p.id
          UNION ALL
          SELECT m.product_id,m.type_id,m.type_code,x.attribute_id,x.option_id,x.value_number
          FROM matching m JOIN catalog.variant_attribute_values x ON x.variant_id=m.id
        ),
        facet_rows AS (
          SELECT id product_id,'type' key,type_code value,type_name label,NULL::text type FROM ranked
          UNION ALL SELECT id,'material',material_slug,material_name,NULL FROM ranked WHERE material_slug IS NOT NULL
          UNION ALL SELECT id,'artisan',artisan_slug,artisan_name,NULL FROM ranked WHERE artisan_slug IS NOT NULL
          UNION ALL SELECT p.id,'category',v.slug,v.name,NULL
            FROM ranked p JOIN catalog.product_categories pc ON pc.product_id=p.id JOIN category_paths path ON path.descendant=pc.category_id JOIN visible v ON v.id=path.ancestor
          UNION ALL SELECT p.id,'collection',c.slug,c.name,NULL
            FROM ranked p JOIN catalog.collection_products cp ON cp.product_id=p.id
            JOIN catalog.collections c ON c.id=cp.collection_id AND c.status='visible'
          UNION ALL SELECT x.product_id,'attr.'||d.code,coalesce(o.code,x.value_number::text),
            coalesce(o.label,x.value_number::text),x.type_code
            FROM attribute_values x JOIN catalog.attribute_definitions d ON d.id=x.attribute_id AND d.product_type_id=x.type_id
            LEFT JOIN catalog.attribute_options o ON o.id=x.option_id AND o.attribute_id=d.id AND o.is_active
            WHERE d.is_active AND d.is_visible AND d.is_filterable
              AND (d.data_type='option' AND o.id IS NOT NULL OR d.data_type='number' AND x.value_number IS NOT NULL)
        ),
        facets AS (
          SELECT key,value,label,type,count(DISTINCT product_id) "productCount"
          FROM facet_rows GROUP BY key,value,label,type
        )
        SELECT jsonb_build_object('total',(SELECT count(*) FROM ranked), 'items',coalesce((
          SELECT jsonb_agg(jsonb_build_object(
            'slug',slug,'name',name,'subtitle',subtitle,'shortDescription',short_description,
            'variantId',variant_id,'effectivePrice',effective_price,'basePrice',base_price,
            'stock',stock,'coverId',cover_id,'alt',alt_text) ORDER BY rank) FROM page),'[]'::jsonb),
          'facets',coalesce((SELECT jsonb_agg(to_jsonb(f) ORDER BY key,type,value,label) FROM facets f),'[]'::jsonb))::text AS "Value"
        """;
    }
}
