using Microsoft.EntityFrameworkCore;

namespace TeaPremium.Infrastructure.Persistence;

internal static class CatalogInventoryModel
{
    internal static void Configure(ModelBuilder builder)
    {
        builder.HasSequence<long>("sku_number_seq", "catalog").StartsAt(1);
        builder.Entity<MediaAsset>(b =>
        {
            b.ToTable("assets", "media", t =>
            {
                t.HasCheckConstraint("ck_assets_mime_type", "mime_type IN ('image/jpeg','image/png','image/webp','image/avif','video/mp4','video/quicktime')");
                t.HasCheckConstraint("ck_assets_purpose", "purpose IN ('catalog','content','evidence','branding')");
                t.HasCheckConstraint("ck_assets_size_by_kind", "(mime_type IN ('image/jpeg','image/png','image/webp','image/avif') AND byte_size > 0 AND byte_size <= 15728640) OR\n    (purpose = 'evidence' AND mime_type IN ('video/mp4','video/quicktime') AND byte_size > 0 AND byte_size <= 52428800)");
            });
            b.HasKey(x => x.Id).HasName("pk_assets");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.StorageKey).HasColumnName("storage_key").HasColumnType("text").IsRequired();
            b.Property(x => x.MimeType).HasColumnName("mime_type").HasColumnType("text").IsRequired();
            b.Property(x => x.ByteSize).HasColumnName("byte_size").HasColumnType("bigint").IsRequired();
            b.Property(x => x.Width).HasColumnName("width").HasColumnType("integer").IsRequired(false);
            b.Property(x => x.Height).HasColumnName("height").HasColumnType("integer").IsRequired(false);
            b.Property(x => x.ChecksumSha256).HasColumnName("checksum_sha256").HasColumnType("text").IsRequired(false);
            b.Property(x => x.DefaultAlt).HasColumnName("default_alt").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Purpose).HasColumnName("purpose").HasColumnType("text").IsRequired().HasDefaultValueSql("'catalog'");
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasIndex(x => x.StorageKey).IsUnique().HasDatabaseName("ux_assets_storage_key").HasAnnotation("TeaPremium:UniqueConstraint", true);
        });
        builder.Entity<UploadSession>(b =>
        {
            b.ToTable("upload_sessions", "media", t =>
            {
                t.HasCheckConstraint("ck_upload_sessions_status", "status IN ('pending','completed','expired','failed')");
                t.HasCheckConstraint("ck_upload_size_by_kind", "purpose IN ('catalog','content','evidence','branding') AND (\n      (mime_type IN ('image/jpeg','image/png','image/webp','image/avif') AND expected_bytes > 0 AND expected_bytes <= 15728640) OR\n      (purpose = 'evidence' AND mime_type IN ('video/mp4','video/quicktime') AND expected_bytes > 0 AND expected_bytes <= 52428800))");
            });
            b.HasKey(x => x.Id).HasName("pk_upload_sessions");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.StorageKey).HasColumnName("storage_key").HasColumnType("text").IsRequired();
            b.Property(x => x.OriginalName).HasColumnName("original_name").HasColumnType("text").IsRequired();
            b.Property(x => x.MimeType).HasColumnName("mime_type").HasColumnType("text").IsRequired();
            b.Property(x => x.ExpectedBytes).HasColumnName("expected_bytes").HasColumnType("bigint").IsRequired();
            b.Property(x => x.Purpose).HasColumnName("purpose").HasColumnType("text").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'pending'");
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.AssetId).HasColumnName("asset_id").HasColumnType("uuid").IsRequired(false);
            b.HasIndex(x => x.StorageKey).IsUnique().HasDatabaseName("ux_upload_sessions_storage_key").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.AssetId).HasConstraintName("fk_upload_sessions_asset_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_upload_pending").HasFilter("status = 'pending'");
        });
        builder.Entity<ProductType>(b =>
        {
            b.ToTable("product_types", "catalog", t =>
            {
                t.HasCheckConstraint("ck_product_types_code", "code ~ '^[a-z0-9_]{2,40}$'");
                t.HasCheckConstraint("ck_product_types_sku_prefix", "sku_prefix ~ '^[A-Z]{2,4}$'");
            });
            b.HasKey(x => x.Id).HasName("pk_product_types");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.Code).HasColumnName("code").HasColumnType("text").IsRequired();
            b.Property(x => x.SkuPrefix).HasColumnName("sku_prefix").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Name).HasColumnName("name").HasColumnType("text").IsRequired();
            b.Property(x => x.Description).HasColumnName("description").HasColumnType("text").IsRequired(false);
            b.Property(x => x.GoogleProductCategory).HasColumnName("google_product_category").HasColumnType("text").IsRequired(false);
            b.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.IsActive).HasColumnName("is_active").HasColumnType("boolean").IsRequired().HasDefaultValue(true);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_product_types_code").HasAnnotation("TeaPremium:UniqueConstraint", true);
        });
        builder.Entity<AttributeDefinition>(b =>
        {
            b.ToTable("attribute_definitions", "catalog", t =>
            {
                t.HasCheckConstraint("ck_attribute_definitions_code", "code ~ '^[a-z0-9_]{2,40}$'");
                t.HasCheckConstraint("ck_attribute_definitions_data_type", "data_type IN ('text','number','option','boolean','dimensions')");
                t.HasCheckConstraint("ck_attribute_definitions_scope", "scope IN ('product','variant')");
                t.HasCheckConstraint("ck_attr_unit", "unit IS NULL OR data_type IN ('number','dimensions')");
                t.HasCheckConstraint("ck_attr_axis", "NOT is_variant_axis OR (scope = 'variant' AND data_type IN ('number','option','text'))");
            });
            b.HasKey(x => x.Id).HasName("pk_attribute_definitions");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.ProductTypeId).HasColumnName("product_type_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Code).HasColumnName("code").HasColumnType("text").IsRequired();
            b.Property(x => x.Name).HasColumnName("name").HasColumnType("text").IsRequired();
            b.Property(x => x.DataType).HasColumnName("data_type").HasColumnType("text").IsRequired();
            b.Property(x => x.Unit).HasColumnName("unit").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Scope).HasColumnName("scope").HasColumnType("text").IsRequired().HasDefaultValueSql("'product'");
            b.Property(x => x.IsVariantAxis).HasColumnName("is_variant_axis").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.IsRequiredForPublish).HasColumnName("is_required_for_publish").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.IsFilterable).HasColumnName("is_filterable").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.IsVisible).HasColumnName("is_visible").HasColumnType("boolean").IsRequired().HasDefaultValue(true);
            b.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.IsActive).HasColumnName("is_active").HasColumnType("boolean").IsRequired().HasDefaultValue(true);
            b.HasOne<ProductType>().WithMany().HasForeignKey(x => x.ProductTypeId).HasConstraintName("fk_attribute_definitions_product_type_id").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.ProductTypeId, x.Code }).IsUnique().HasDatabaseName("ux_attribute_definitions_product_type_id_code").HasAnnotation("TeaPremium:UniqueConstraint", true);
        });
        builder.Entity<AttributeOption>(b =>
        {
            b.ToTable("attribute_options", "catalog", t =>
            {
                t.HasCheckConstraint("ck_attribute_options_code", "code ~ '^[a-z0-9_]{1,60}$'");
            });
            b.HasKey(x => x.Id).HasName("pk_attribute_options");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.AttributeId).HasColumnName("attribute_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Code).HasColumnName("code").HasColumnType("text").IsRequired();
            b.Property(x => x.Label).HasColumnName("label").HasColumnType("text").IsRequired();
            b.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.IsActive).HasColumnName("is_active").HasColumnType("boolean").IsRequired().HasDefaultValue(true);
            b.HasOne<AttributeDefinition>().WithMany().HasForeignKey(x => x.AttributeId).HasConstraintName("fk_attribute_options_attribute_id").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.AttributeId, x.Code }).IsUnique().HasDatabaseName("ux_attribute_options_attribute_id_code").HasAnnotation("TeaPremium:UniqueConstraint", true);
        });
        builder.Entity<Material>(b =>
        {
            b.ToTable("materials", "catalog", t =>
            {
                t.HasCheckConstraint("ck_materials_sku_code", "sku_code ~ '^[A-Z]{2,3}$' AND sku_code <> 'XX'");
                t.HasCheckConstraint("ck_materials_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                t.HasCheckConstraint("ck_materials_kind", "kind IN ('clay','metal','glass','ceramic','porcelain','wood','stone','other')");
                t.HasCheckConstraint("ck_materials_status", "status IN ('draft','published','archived')");
            });
            b.Property(x => x.FirstPublishedAt).HasColumnName("first_published_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            b.HasKey(x => x.Id).HasName("pk_materials");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.SkuCode).HasColumnName("sku_code").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Slug).HasColumnName("slug").HasColumnType("text").IsRequired();
            b.Property(x => x.Name).HasColumnName("name").HasColumnType("text").IsRequired();
            b.Property(x => x.Kind).HasColumnName("kind").HasColumnType("text").IsRequired();
            b.Property(x => x.Summary).HasColumnName("summary").HasColumnType("text").IsRequired(false);
            b.Property(x => x.DescriptionJson).HasColumnName("description_json").HasColumnType("jsonb").IsRequired(false);
            b.Property(x => x.DescriptionHtml).HasColumnName("description_html").HasColumnType("text").IsRequired(false);
            b.Property(x => x.CoverMediaId).HasColumnName("cover_media_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'draft'");
            b.Property(x => x.FocusKeyword).HasColumnName("focus_keyword").HasColumnType("text").IsRequired(false);
            b.Property(x => x.SeoTitle).HasColumnName("seo_title").HasColumnType("text").IsRequired(false);
            b.Property(x => x.SeoDescription).HasColumnName("seo_description").HasColumnType("text").IsRequired(false);
            b.Property(x => x.RobotsNoindex).HasColumnName("robots_noindex").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            // UNIQUE constraints are annotated indexes in EF: alternate keys would
            // force NOT NULL and prevent editing business identifiers/slugs.
            // Migration SQL creates the exact DDL constraints.
            b.HasIndex(x => x.SkuCode).IsUnique().HasDatabaseName("ux_materials_sku_code").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasAnnotation("TeaPremium:NullableUniqueConstraint", "ux_materials_sku_code (sku_code)");
            b.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_materials_slug").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.CoverMediaId).HasConstraintName("fk_materials_cover_media_id").OnDelete(DeleteBehavior.NoAction);
        });
        builder.Entity<Artisan>(b =>
        {
            b.ToTable("artisans", "catalog", t =>
            {
                t.HasCheckConstraint("ck_artisans_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                t.HasCheckConstraint("ck_artisans_status", "status IN ('draft','published','archived')");
            });
            b.Property(x => x.FirstPublishedAt).HasColumnName("first_published_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            b.HasKey(x => x.Id).HasName("pk_artisans");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.Slug).HasColumnName("slug").HasColumnType("text").IsRequired();
            b.Property(x => x.Name).HasColumnName("name").HasColumnType("text").IsRequired();
            b.Property(x => x.Title).HasColumnName("title").HasColumnType("text").IsRequired(false);
            b.Property(x => x.BioJson).HasColumnName("bio_json").HasColumnType("jsonb").IsRequired(false);
            b.Property(x => x.BioHtml).HasColumnName("bio_html").HasColumnType("text").IsRequired(false);
            b.Property(x => x.PortraitMediaId).HasColumnName("portrait_media_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'draft'");
            b.Property(x => x.FocusKeyword).HasColumnName("focus_keyword").HasColumnType("text").IsRequired(false);
            b.Property(x => x.SeoTitle).HasColumnName("seo_title").HasColumnType("text").IsRequired(false);
            b.Property(x => x.SeoDescription).HasColumnName("seo_description").HasColumnType("text").IsRequired(false);
            b.Property(x => x.RobotsNoindex).HasColumnName("robots_noindex").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_artisans_slug").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.PortraitMediaId).HasConstraintName("fk_artisans_portrait_media_id").OnDelete(DeleteBehavior.NoAction);
        });
        builder.Entity<Category>(b =>
        {
            b.ToTable("categories", "catalog", t =>
            {
                t.HasCheckConstraint("ck_categories_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                t.HasCheckConstraint("ck_categories_status", "status IN ('visible','hidden')");
                t.HasCheckConstraint("ck_categories_row", "parent_id IS NULL OR parent_id <> id");
            });
            b.Property(x => x.FirstPublishedAt).HasColumnName("first_published_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            b.HasKey(x => x.Id).HasName("pk_categories");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.ParentId).HasColumnName("parent_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Slug).HasColumnName("slug").HasColumnType("text").IsRequired();
            b.Property(x => x.Name).HasColumnName("name").HasColumnType("text").IsRequired();
            b.Property(x => x.DescriptionHtml).HasColumnName("description_html").HasColumnType("text").IsRequired(false);
            b.Property(x => x.BodyHtml).HasColumnName("body_html").HasColumnType("text").IsRequired(false);
            b.Property(x => x.BodyJson).HasColumnName("body_json").HasColumnType("jsonb").IsRequired(false);
            b.Property(x => x.CoverMediaId).HasColumnName("cover_media_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'hidden'");
            b.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.FocusKeyword).HasColumnName("focus_keyword").HasColumnType("text").IsRequired(false);
            b.Property(x => x.SeoTitle).HasColumnName("seo_title").HasColumnType("text").IsRequired(false);
            b.Property(x => x.SeoDescription).HasColumnName("seo_description").HasColumnType("text").IsRequired(false);
            b.Property(x => x.RobotsNoindex).HasColumnName("robots_noindex").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasOne<Category>().WithMany().HasForeignKey(x => x.ParentId).HasConstraintName("fk_categories_parent_id").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_categories_slug").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.CoverMediaId).HasConstraintName("fk_categories_cover_media_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => x.ParentId).HasDatabaseName("ix_categories_parent");
        });
        builder.Entity<Product>(b =>
        {
            b.ToTable("products", "catalog", t =>
            {
                t.HasCheckConstraint("ck_products_sku_base", "sku_base ~ '^[A-Z]{2,4}-[A-Z]{2,3}-[0-9]{4,}$' AND length(sku_base) <= 40");
                t.HasCheckConstraint("ck_products_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                t.HasCheckConstraint("ck_products_name", "length(name) BETWEEN 2 AND 200");
                t.HasCheckConstraint("ck_products_word_count", "description_word_count >= 0");
                t.HasCheckConstraint("ck_products_status", "status IN ('draft','active','archived')");
                t.HasCheckConstraint("ck_products_published", "status <> 'active' OR published_at IS NOT NULL");
            });
            b.HasKey(x => x.Id).HasName("pk_products");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.SkuBase).HasColumnName("sku_base").HasColumnType("text").IsRequired();
            b.Property(x => x.ProductTypeId).HasColumnName("product_type_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Slug).HasColumnName("slug").HasColumnType("text").IsRequired();
            b.Property(x => x.Name).HasColumnName("name").HasColumnType("text").IsRequired();
            b.Property(x => x.Subtitle).HasColumnName("subtitle").HasColumnType("text").IsRequired(false);
            b.Property(x => x.ShortDescription).HasColumnName("short_description").HasColumnType("text").IsRequired(false);
            b.Property(x => x.DescriptionJson).HasColumnName("description_json").HasColumnType("jsonb").IsRequired(false);
            b.Property(x => x.DescriptionHtml).HasColumnName("description_html").HasColumnType("text").IsRequired(false);
            b.Property(x => x.CareHtml).HasColumnName("care_html").HasColumnType("text").IsRequired(false);
            b.Property(x => x.DescriptionWordCount).HasColumnName("description_word_count").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.PrimaryCategoryId).HasColumnName("primary_category_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.MaterialId).HasColumnName("material_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.ArtisanId).HasColumnName("artisan_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.OriginNote).HasColumnName("origin_note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'draft'");
            b.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.FirstPublishedAt).HasColumnName("first_published_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.ArchivedRedirectPath).HasColumnName("archived_redirect_path").HasColumnType("text").IsRequired(false);
            b.Property(x => x.SoldOutAt).HasColumnName("sold_out_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.SeoKeepAfterSold).HasColumnName("seo_keep_after_sold").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.FocusKeyword).HasColumnName("focus_keyword").HasColumnType("text").IsRequired(false);
            b.Property(x => x.IsFeatured).HasColumnName("is_featured").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.SearchText).HasColumnName("search_text").HasColumnType("text").IsRequired().HasDefaultValueSql("''");
            b.Property(x => x.SeoTitle).HasColumnName("seo_title").HasColumnType("text").IsRequired(false);
            b.Property(x => x.SeoDescription).HasColumnName("seo_description").HasColumnType("text").IsRequired(false);
            b.Property(x => x.CanonicalUrl).HasColumnName("canonical_url").HasColumnType("text").IsRequired(false);
            b.Property(x => x.RobotsNoindex).HasColumnName("robots_noindex").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.OgMediaId).HasColumnName("og_media_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.UpdatedBy).HasColumnName("updated_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            b.HasIndex(x => x.SkuBase).IsUnique().HasDatabaseName("ux_products_sku_base").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<ProductType>().WithMany().HasForeignKey(x => x.ProductTypeId).HasConstraintName("fk_products_product_type_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_products_slug").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<Category>().WithMany().HasForeignKey(x => x.PrimaryCategoryId).HasConstraintName("fk_products_primary_category_id").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<Material>().WithMany().HasForeignKey(x => x.MaterialId).HasConstraintName("fk_products_material_id").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<Artisan>().WithMany().HasForeignKey(x => x.ArtisanId).HasConstraintName("fk_products_artisan_id").OnDelete(DeleteBehavior.NoAction);
            b.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.OgMediaId).HasConstraintName("fk_products_og_media_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => new { x.Status, x.PublishedAt }).HasDatabaseName("ix_products_status_pub").IsDescending(false, true);
            b.HasIndex(x => x.ProductTypeId).HasDatabaseName("ix_products_type");
            b.HasIndex(x => x.PrimaryCategoryId).HasDatabaseName("ix_products_category");
            b.HasIndex(x => x.MaterialId).HasDatabaseName("ix_products_material");
            b.HasIndex(x => x.ArtisanId).HasDatabaseName("ix_products_artisan");
            b.HasIndex(x => x.SearchText).HasDatabaseName("ix_products_search").HasMethod("gin").HasOperators("gin_trgm_ops");
        });
        builder.Entity<ProductCategory>(b =>
        {
            b.ToTable("product_categories", "catalog", t =>
            {
            });
            b.HasKey(x => new { x.ProductId, x.CategoryId }).HasName("pk_product_categories");
            b.Property(x => x.ProductId).HasColumnName("product_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.CategoryId).HasColumnName("category_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).HasConstraintName("fk_product_categories_product_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).HasConstraintName("fk_product_categories_category_id").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.CategoryId).HasDatabaseName("ix_product_categories_cat");
        });
        builder.Entity<ProductRelation>(b =>
        {
            b.ToTable("product_relations", "catalog", t =>
            {
                t.HasCheckConstraint("ck_product_relations_row", "product_id <> related_product_id");
            });
            b.HasKey(x => new { x.ProductId, x.RelatedProductId }).HasName("pk_product_relations");
            b.Property(x => x.ProductId).HasColumnName("product_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.RelatedProductId).HasColumnName("related_product_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).HasConstraintName("fk_product_relations_product_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.RelatedProductId).HasConstraintName("fk_product_relations_related_product_id").OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Variant>(b =>
        {
            b.ToTable("variants", "catalog", t =>
            {
                t.HasCheckConstraint("ck_variants_sku", "sku ~ '^[A-Z0-9][A-Z0-9-]{1,39}$'");
                t.HasCheckConstraint("ck_variants_price", "price_vnd IS NULL OR (price_vnd > 0 AND price_vnd <= 10000000000)");
                t.HasCheckConstraint("ck_variants_weight_g", "weight_g IS NULL OR weight_g > 0");
                t.HasCheckConstraint("ck_variants_low_stock_threshold", "low_stock_threshold >= 0");
                t.HasCheckConstraint("ck_variants_status", "status IN ('active','inactive')");
            });
            b.HasKey(x => x.Id).HasName("pk_variants");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.ProductId).HasColumnName("product_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Sku).HasColumnName("sku").HasColumnType("text").IsRequired();
            b.Property(x => x.Name).HasColumnName("name").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Position).HasColumnName("position").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.PriceVnd).HasColumnName("price_vnd").HasColumnType("bigint").IsRequired(false);
            b.Property(x => x.IsUniquePiece).HasColumnName("is_unique_piece").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.WeightG).HasColumnName("weight_g").HasColumnType("integer").IsRequired(false);
            b.Property(x => x.PackageDimsCm).HasColumnName("package_dims_cm").HasColumnType("jsonb").IsRequired(false);
            b.Property(x => x.Barcode).HasColumnName("barcode").HasColumnType("text").IsRequired(false);
            b.Property(x => x.LowStockThreshold).HasColumnName("low_stock_threshold").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'active'");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).HasConstraintName("fk_variants_product_id").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.Sku).IsUnique().HasDatabaseName("ux_variants_sku").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasIndex(x => new { x.ProductId, x.Position }).HasDatabaseName("ix_variants_product");
        });
        builder.Entity<ProductAttributeValue>(b =>
        {
            b.ToTable("product_attribute_values", "catalog", t =>
            {
                t.HasCheckConstraint("ck_pav_one_value", "num_nonnulls(value_text, value_number, option_id, value_bool, value_json) = 1");
            });
            b.HasKey(x => new { x.ProductId, x.AttributeId }).HasName("pk_product_attribute_values");
            b.Property(x => x.ProductId).HasColumnName("product_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.AttributeId).HasColumnName("attribute_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.ValueText).HasColumnName("value_text").HasColumnType("text").IsRequired(false);
            b.Property(x => x.ValueNumber).HasColumnName("value_number").HasColumnType("numeric(12,2)").IsRequired(false);
            b.Property(x => x.OptionId).HasColumnName("option_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.ValueBool).HasColumnName("value_bool").HasColumnType("boolean").IsRequired(false);
            b.Property(x => x.ValueJson).HasColumnName("value_json").HasColumnType("jsonb").IsRequired(false);
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).HasConstraintName("fk_product_attribute_values_product_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<AttributeDefinition>().WithMany().HasForeignKey(x => x.AttributeId).HasConstraintName("fk_product_attribute_values_attribute_id").OnDelete(DeleteBehavior.Restrict);
            b.HasOne<AttributeOption>().WithMany().HasForeignKey(x => x.OptionId).HasConstraintName("fk_product_attribute_values_option_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => new { x.AttributeId, x.OptionId }).HasDatabaseName("ix_pav_filter_option").HasFilter("option_id IS NOT NULL");
            b.HasIndex(x => new { x.AttributeId, x.ValueNumber }).HasDatabaseName("ix_pav_filter_number").HasFilter("value_number IS NOT NULL");
        });
        builder.Entity<VariantAttributeValue>(b =>
        {
            b.ToTable("variant_attribute_values", "catalog", t =>
            {
                t.HasCheckConstraint("ck_vav_one_value", "num_nonnulls(value_text, value_number, option_id, value_bool, value_json) = 1");
            });
            b.HasKey(x => new { x.VariantId, x.AttributeId }).HasName("pk_variant_attribute_values");
            b.Property(x => x.VariantId).HasColumnName("variant_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.AttributeId).HasColumnName("attribute_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.ValueText).HasColumnName("value_text").HasColumnType("text").IsRequired(false);
            b.Property(x => x.ValueNumber).HasColumnName("value_number").HasColumnType("numeric(12,2)").IsRequired(false);
            b.Property(x => x.OptionId).HasColumnName("option_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.ValueBool).HasColumnName("value_bool").HasColumnType("boolean").IsRequired(false);
            b.Property(x => x.ValueJson).HasColumnName("value_json").HasColumnType("jsonb").IsRequired(false);
            b.HasOne<Variant>().WithMany().HasForeignKey(x => x.VariantId).HasConstraintName("fk_variant_attribute_values_variant_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<AttributeDefinition>().WithMany().HasForeignKey(x => x.AttributeId).HasConstraintName("fk_variant_attribute_values_attribute_id").OnDelete(DeleteBehavior.Restrict);
            b.HasOne<AttributeOption>().WithMany().HasForeignKey(x => x.OptionId).HasConstraintName("fk_variant_attribute_values_option_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => new { x.AttributeId, x.OptionId }).HasDatabaseName("ix_vav_filter_option").HasFilter("option_id IS NOT NULL");
            b.HasIndex(x => new { x.AttributeId, x.ValueNumber }).HasDatabaseName("ix_vav_filter_number").HasFilter("value_number IS NOT NULL");
        });
        builder.Entity<ProductMedia>(b =>
        {
            b.ToTable("product_media", "catalog", t =>
            {
                t.HasCheckConstraint("ck_product_media_alt_text", "length(trim(alt_text)) BETWEEN 3 AND 200");
            });
            b.HasKey(x => x.Id).HasName("pk_product_media");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.ProductId).HasColumnName("product_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.VariantId).HasColumnName("variant_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.AssetId).HasColumnName("asset_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.AltText).HasColumnName("alt_text").HasColumnType("text").IsRequired();
            b.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.IsCover).HasColumnName("is_cover").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).HasConstraintName("fk_product_media_product_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Variant>().WithMany().HasForeignKey(x => x.VariantId).HasConstraintName("fk_product_media_variant_id").OnDelete(DeleteBehavior.SetNull);
            b.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.AssetId).HasConstraintName("fk_product_media_asset_id").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.ProductId, x.AssetId }).IsUnique().HasDatabaseName("ux_product_media_product_id_asset_id").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasIndex(x => x.ProductId).HasDatabaseName("ux_product_media_cover").IsUnique().HasFilter("is_cover");
        });
        builder.Entity<CollectionRecord>(b =>
        {
            b.ToTable("collections", "catalog", t =>
            {
                t.HasCheckConstraint("ck_collections_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                t.HasCheckConstraint("ck_collections_status", "status IN ('visible','hidden')");
            });
            b.Property(x => x.FirstPublishedAt).HasColumnName("first_published_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            b.HasKey(x => x.Id).HasName("pk_collections");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.Slug).HasColumnName("slug").HasColumnType("text").IsRequired();
            b.Property(x => x.Name).HasColumnName("name").HasColumnType("text").IsRequired();
            b.Property(x => x.DescriptionHtml).HasColumnName("description_html").HasColumnType("text").IsRequired(false);
            b.Property(x => x.BodyHtml).HasColumnName("body_html").HasColumnType("text").IsRequired(false);
            b.Property(x => x.BodyJson).HasColumnName("body_json").HasColumnType("jsonb").IsRequired(false);
            b.Property(x => x.CoverMediaId).HasColumnName("cover_media_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'hidden'");
            b.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.FocusKeyword).HasColumnName("focus_keyword").HasColumnType("text").IsRequired(false);
            b.Property(x => x.SeoTitle).HasColumnName("seo_title").HasColumnType("text").IsRequired(false);
            b.Property(x => x.SeoDescription).HasColumnName("seo_description").HasColumnType("text").IsRequired(false);
            b.Property(x => x.RobotsNoindex).HasColumnName("robots_noindex").HasColumnType("boolean").IsRequired().HasDefaultValueSql("false");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_collections_slug").HasAnnotation("TeaPremium:UniqueConstraint", true);
            b.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.CoverMediaId).HasConstraintName("fk_collections_cover_media_id").OnDelete(DeleteBehavior.NoAction);
        });
        builder.Entity<CollectionProduct>(b =>
        {
            b.ToTable("collection_products", "catalog", t =>
            {
            });
            b.HasKey(x => new { x.CollectionId, x.ProductId }).HasName("pk_collection_products");
            b.Property(x => x.CollectionId).HasColumnName("collection_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.ProductId).HasColumnName("product_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.HasOne<CollectionRecord>().WithMany().HasForeignKey(x => x.CollectionId).HasConstraintName("fk_collection_products_collection_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).HasConstraintName("fk_collection_products_product_id").OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<StockLevel>(b =>
        {
            b.ToTable("stock_levels", "inventory", t =>
            {
                t.HasCheckConstraint("ck_stock_levels_on_hand", "on_hand >= 0");
                t.HasCheckConstraint("ck_stock_levels_reserved", "reserved >= 0");
                t.HasCheckConstraint("ck_stock_levels_avg_cost_vnd", "avg_cost_vnd IS NULL OR avg_cost_vnd >= 0");
                t.HasCheckConstraint("ck_stock_reserved", "reserved <= on_hand");
            });
            b.HasKey(x => x.VariantId).HasName("pk_stock_levels");
            b.Property(x => x.VariantId).HasColumnName("variant_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.OnHand).HasColumnName("on_hand").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.Reserved).HasColumnName("reserved").HasColumnType("integer").IsRequired().HasDefaultValueSql("0");
            b.Property(x => x.AvgCostVnd).HasColumnName("avg_cost_vnd").HasColumnType("bigint").IsRequired(false);
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasOne<Variant>().WithMany().HasForeignKey(x => x.VariantId).HasConstraintName("fk_stock_levels_variant_id").OnDelete(DeleteBehavior.Restrict);
            b.HasAnnotation("TeaPremium:ExpressionIndex", "ix_stock_available ((on_hand - reserved))");
        });
        builder.Entity<StockMovement>(b =>
        {
            b.ToTable("stock_movements", "inventory", t =>
            {
                t.HasCheckConstraint("ck_stock_movements_movement_type", "movement_type IN\n                   ('opening','receipt','sale','return_restock','adjustment','stocktake','damage','replacement_out')");
                t.HasCheckConstraint("ck_stock_movements_quantity", "quantity <> 0");
                t.HasCheckConstraint("ck_stock_movements_unit_cost_vnd", "unit_cost_vnd IS NULL OR unit_cost_vnd >= 0");
                t.HasCheckConstraint("ck_stock_movements_on_hand_after", "on_hand_after >= 0");
                t.HasCheckConstraint("ck_stock_movements_ref_type", "ref_type IN ('receipt','order','return','adjustment','stocktake','import')");
                t.HasCheckConstraint("ck_stock_movements_actor_type", "actor_type IN ('staff','system')");
                t.HasCheckConstraint("ck_mv_direction", "(movement_type IN ('opening','receipt','return_restock') AND quantity > 0) OR\n    (movement_type IN ('sale','damage','replacement_out') AND quantity < 0) OR\n    (movement_type IN ('adjustment','stocktake'))");
                t.HasCheckConstraint("ck_mv_reason", "movement_type NOT IN ('adjustment','damage') OR length(trim(coalesce(reason,''))) >= 3");
            });
            b.HasKey(x => x.Id).HasName("pk_stock_movements");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.VariantId).HasColumnName("variant_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.MovementType).HasColumnName("movement_type").HasColumnType("text").IsRequired();
            b.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("integer").IsRequired();
            b.Property(x => x.UnitCostVnd).HasColumnName("unit_cost_vnd").HasColumnType("bigint").IsRequired(false);
            b.Property(x => x.OnHandAfter).HasColumnName("on_hand_after").HasColumnType("integer").IsRequired();
            b.Property(x => x.RefType).HasColumnName("ref_type").HasColumnType("text").IsRequired(false);
            b.Property(x => x.RefId).HasColumnName("ref_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.Reason).HasColumnName("reason").HasColumnType("text").IsRequired(false);
            b.Property(x => x.ActorType).HasColumnName("actor_type").HasColumnType("text").IsRequired();
            b.Property(x => x.ActorId).HasColumnName("actor_id").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasOne<Variant>().WithMany().HasForeignKey(x => x.VariantId).HasConstraintName("fk_stock_movements_variant_id").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.VariantId, x.OccurredAt }).HasDatabaseName("ix_mv_variant_time").IsDescending(false, true);
            b.HasIndex(x => new { x.RefType, x.RefId }).HasDatabaseName("ix_mv_ref");
        });
        builder.Entity<Receipt>(b =>
        {
            b.ToTable("receipts", "inventory", t =>
            {
                t.HasCheckConstraint("ck_receipts_status", "status IN ('draft','posted','cancelled')");
                t.HasCheckConstraint("ck_receipts_row", "(status = 'posted') = (posted_at IS NOT NULL)");
            });
            b.HasKey(x => x.Id).HasName("pk_receipts");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.Code).HasColumnName("code").HasColumnType("text").IsRequired();
            b.Property(x => x.SupplierName).HasColumnName("supplier_name").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Note).HasColumnName("note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'draft'");
            b.Property(x => x.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.PostedAt).HasColumnName("posted_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.PostedBy).HasColumnName("posted_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_receipts_code").HasAnnotation("TeaPremium:UniqueConstraint", true);
        });
        builder.Entity<ReceiptLine>(b =>
        {
            b.ToTable("receipt_lines", "inventory", t =>
            {
                t.HasCheckConstraint("ck_receipt_lines_quantity", "quantity > 0");
                t.HasCheckConstraint("ck_receipt_lines_unit_cost_vnd", "unit_cost_vnd >= 0");
            });
            b.HasKey(x => x.Id).HasName("pk_receipt_lines");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.ReceiptId).HasColumnName("receipt_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.VariantId).HasColumnName("variant_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("integer").IsRequired();
            b.Property(x => x.UnitCostVnd).HasColumnName("unit_cost_vnd").HasColumnType("bigint").IsRequired();
            b.HasOne<Receipt>().WithMany().HasForeignKey(x => x.ReceiptId).HasConstraintName("fk_receipt_lines_receipt_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Variant>().WithMany().HasForeignKey(x => x.VariantId).HasConstraintName("fk_receipt_lines_variant_id").OnDelete(DeleteBehavior.NoAction);
            b.HasIndex(x => new { x.ReceiptId, x.VariantId }).IsUnique().HasDatabaseName("ux_receipt_lines_receipt_id_variant_id").HasAnnotation("TeaPremium:UniqueConstraint", true);
        });
        builder.Entity<Stocktake>(b =>
        {
            b.ToTable("stocktakes", "inventory", t =>
            {
                t.HasCheckConstraint("ck_stocktakes_status", "status IN ('counting','submitted','approved','cancelled')");
            });
            b.HasKey(x => x.Id).HasName("pk_stocktakes");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.Code).HasColumnName("code").HasColumnType("text").IsRequired();
            b.Property(x => x.Note).HasColumnName("note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired().HasDefaultValueSql("'counting'");
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.ApprovedBy).HasColumnName("approved_by").HasColumnType("uuid").IsRequired(false);
            b.Property(x => x.ApprovedAt).HasColumnName("approved_at").HasColumnType("timestamp with time zone").IsRequired(false);
            b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_stocktakes_code").HasAnnotation("TeaPremium:UniqueConstraint", true);
        });
        builder.Entity<StocktakeLine>(b =>
        {
            b.ToTable("stocktake_lines", "inventory", t =>
            {
                t.HasCheckConstraint("ck_stocktake_lines_counted_qty", "counted_qty >= 0");
                t.HasCheckConstraint("ck_stocktake_lines_system_qty_at_count", "system_qty_at_count >= 0");
            });
            b.HasKey(x => new { x.StocktakeId, x.VariantId }).HasName("pk_stocktake_lines");
            b.Property(x => x.StocktakeId).HasColumnName("stocktake_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.VariantId).HasColumnName("variant_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.CountedQty).HasColumnName("counted_qty").HasColumnType("integer").IsRequired();
            b.Property(x => x.SystemQtyAtCount).HasColumnName("system_qty_at_count").HasColumnType("integer").IsRequired();
            b.Property(x => x.CountedAt).HasColumnName("counted_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.CountedBy).HasColumnName("counted_by").HasColumnType("uuid").IsRequired();
            b.HasOne<Stocktake>().WithMany().HasForeignKey(x => x.StocktakeId).HasConstraintName("fk_stocktake_lines_stocktake_id").OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Variant>().WithMany().HasForeignKey(x => x.VariantId).HasConstraintName("fk_stocktake_lines_variant_id").OnDelete(DeleteBehavior.NoAction);
        });
        builder.Entity<SalePrice>(b =>
        {
            b.ToTable("sale_prices", "pricing", t =>
            {
                t.HasCheckConstraint("ck_sale_prices_sale_price_vnd", "sale_price_vnd > 0");
                t.HasCheckConstraint("ck_sale_prices_row", "ends_at > starts_at");
            });
            b.HasKey(x => x.Id).HasName("pk_sale_prices");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            b.Property(x => x.VariantId).HasColumnName("variant_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.SalePriceVnd).HasColumnName("sale_price_vnd").HasColumnType("bigint").IsRequired();
            b.Property(x => x.StartsAt).HasColumnName("starts_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.EndsAt).HasColumnName("ends_at").HasColumnType("timestamp with time zone").IsRequired();
            b.Property(x => x.Note).HasColumnName("note").HasColumnType("text").IsRequired(false);
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.HasOne<Variant>().WithMany().HasForeignKey(x => x.VariantId).HasConstraintName("fk_sale_prices_variant_id").OnDelete(DeleteBehavior.Cascade);
            b.HasAnnotation("TeaPremium:Exclusion", "ex_sale_prices_no_overlap USING gist\n    (variant_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)");
        });
        builder.Entity<Redirect>(b =>
        {
            b.ToTable("redirects", "content", t =>
            {
                t.HasCheckConstraint("ck_redirects_source_path", "source_path ~ '^/[^?#]*$'");
                t.HasCheckConstraint("ck_redirects_target_path", "target_path ~ '^/[^?#]*$'");
                t.HasCheckConstraint("ck_redirects_row", "source_path <> target_path");
            });
            b.HasKey(x => x.SourcePath).HasName("pk_redirects");
            b.Property(x => x.SourcePath).HasColumnName("source_path").HasColumnType("text").IsRequired();
            b.Property(x => x.TargetPath).HasColumnName("target_path").HasColumnType("text").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("now()");
            b.Property(x => x.HitCount).HasColumnName("hit_count").HasColumnType("bigint").IsRequired().HasDefaultValueSql("0");
        });

    }
}
