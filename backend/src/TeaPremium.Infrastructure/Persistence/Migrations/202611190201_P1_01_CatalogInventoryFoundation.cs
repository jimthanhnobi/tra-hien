using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF-generated migration operations use fixed array arguments.
#pragma warning disable CA1707 // Keep the owner-approved migration name P1_01_CatalogInventoryFoundation.

namespace TeaPremium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P1_01_CatalogInventoryFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
-- Shared by provisioning, dev-migrate and GrantsSql (frozen into each migration).
DO $preflight$
DECLARE
  provisioning boolean := coalesce(current_setting('teapremium.provisioning', true), '') = 'on';
  target text := current_setting('teapremium.target', true);
  obj record;
BEGIN
  IF provisioning THEN
    IF NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
      RAISE EXCEPTION 'Provisioning requires an authorized cluster administrator';
    END IF;
  ELSIF current_user <> 'tp_owner' THEN
    RAISE EXCEPTION 'Migrations require a direct tp_owner login';
  END IF;
  FOREACH target IN ARRAY ARRAY['tp_owner','tp_app','tp_readonly'] LOOP
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = target) THEN
      IF NOT provisioning THEN
        RAISE EXCEPTION 'Missing role %. Run infra/postgres/init-roles.sql first', target;
      END IF;
    ELSIF EXISTS (SELECT FROM pg_roles WHERE rolname = target AND
      (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR NOT rolcanlogin OR rolinherit)) THEN
      RAISE EXCEPTION 'Unexpected attributes for %. Stop for owner-approved remediation', target;
    END IF;
  END LOOP;
  IF EXISTS (SELECT FROM pg_auth_members m JOIN pg_roles a ON a.oid=m.roleid
    JOIN pg_roles b ON b.oid=m.member
    WHERE a.rolname IN ('tp_owner','tp_app','tp_readonly') OR b.rolname IN ('tp_owner','tp_app','tp_readonly')) THEN
    RAISE EXCEPTION 'Unexpected role membership. Stop for owner-approved remediation';
  END IF;
  IF provisioning AND current_database() <> current_setting('teapremium.target', true) THEN RETURN; END IF;
  IF (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname=current_database()) <> 'tp_owner' THEN
    RAISE EXCEPTION 'Unexpected database owner. No automatic ownership transfer';
  END IF;
  FOR obj IN SELECT extname FROM pg_extension
    WHERE extname <> 'plpgsql' AND pg_get_userbyid(extowner) <> 'tp_owner'
  LOOP RAISE EXCEPTION 'Unexpected extension owner: %', obj.extname; END LOOP;
  IF EXISTS (SELECT FROM pg_roles r WHERE r.rolname IN ('tp_app','tp_readonly') AND
    (has_database_privilege(r.oid,current_database(),'CREATE') OR
     (NOT provisioning AND has_database_privilege(r.oid,current_database(),'TEMP')) OR
     EXISTS (SELECT FROM pg_namespace n WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
       AND has_schema_privilege(r.oid,n.oid,'CREATE')))) THEN
    RAISE EXCEPTION 'Runtime/backup has unexpected CREATE privileges';
  END IF;
  IF EXISTS (SELECT FROM pg_database db CROSS JOIN LATERAL aclexplode(db.datacl) a
    JOIN pg_roles gr ON gr.oid=a.grantee
    WHERE db.datname=current_database() AND gr.rolname IN ('tp_app','tp_readonly')
      AND (a.is_grantable OR a.privilege_type IN ('CREATE','TEMPORARY'))) THEN
    RAISE EXCEPTION 'Unexpected runtime/backup database privileges';
  END IF;
  IF EXISTS (SELECT FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) a
    JOIN pg_roles gr ON gr.oid=a.grantee
    WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
      AND gr.rolname IN ('tp_app','tp_readonly') AND a.is_grantable) THEN
    RAISE EXCEPTION 'Unexpected runtime/backup schema grant option. Stop for owner-approved remediation';
  END IF;
  FOR obj IN SELECT nspname FROM pg_namespace
    WHERE nspname !~ '^pg_' AND nspname <> 'information_schema'
      AND NOT (nspname='public' AND pg_get_userbyid(nspowner)='pg_database_owner')
      AND pg_get_userbyid(nspowner) <> 'tp_owner'
  LOOP RAISE EXCEPTION 'Unexpected schema owner: %', obj.nspname; END LOOP;
  FOR obj IN SELECT n.nspname || '.' || c.relname AS name FROM pg_class c
    JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
      AND pg_get_userbyid(c.relowner) <> 'tp_owner'
      AND NOT EXISTS (SELECT FROM pg_depend d WHERE d.classid='pg_class'::regclass AND d.objid=c.oid AND d.deptype='e')
  LOOP RAISE EXCEPTION 'Unexpected object owner: %', obj.name; END LOOP;
  FOR obj IN SELECT n.nspname || '.' || p.proname AS name FROM pg_proc p
    JOIN pg_namespace n ON n.oid=p.pronamespace
    WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
      AND pg_get_userbyid(p.proowner) <> 'tp_owner'
      AND NOT EXISTS (SELECT FROM pg_depend d WHERE d.classid='pg_proc'::regclass AND d.objid=p.oid AND d.deptype='e')
  LOOP RAISE EXCEPTION 'Unexpected function owner: %', obj.name; END LOOP;
  IF EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    CROSS JOIN LATERAL aclexplode(c.relacl) a
    WHERE n.nspname IN ('platform','iam','public') AND a.grantee=0) THEN
    RAISE EXCEPTION 'Unexpected PUBLIC object privileges';
  END IF;
  IF EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    CROSS JOIN LATERAL aclexplode(c.relacl) a JOIN pg_roles gr ON gr.oid=a.grantee
    WHERE n.nspname IN ('platform','iam','public') AND
      ((gr.rolname='tp_readonly' AND (a.privilege_type <> 'SELECT' OR a.is_grantable)) OR
       (gr.rolname='tp_app' AND (a.is_grantable OR a.privilege_type IN ('TRUNCATE','TRIGGER','REFERENCES','MAINTAIN')
         OR (c.relkind='S' AND a.privilege_type='UPDATE'))))) THEN
    RAISE EXCEPTION 'Unexpected runtime/backup object privileges';
  END IF;
  IF EXISTS (SELECT FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid
    JOIN pg_namespace n ON n.oid=c.relnamespace CROSS JOIN LATERAL aclexplode(a.attacl) acl
    WHERE n.nspname IN ('platform','iam','public') AND
      (acl.grantee=0 OR acl.grantee IN (SELECT oid FROM pg_roles WHERE rolname IN ('tp_app','tp_readonly')))) THEN
    RAISE EXCEPTION 'Unexpected column grants; explicit migration table ACLs cannot revoke them';
  END IF;
  IF EXISTS (SELECT FROM pg_default_acl d CROSS JOIN LATERAL aclexplode(d.defaclacl) a
    LEFT JOIN pg_roles gr ON gr.oid=a.grantee
    WHERE a.grantee=0 OR (gr.rolname='tp_app' AND
      NOT (d.defaclobjtype='S' AND a.privilege_type='USAGE' AND NOT a.is_grantable)) OR
      (gr.rolname='tp_readonly' AND (a.privilege_type <> 'SELECT' OR a.is_grantable))) THEN
    RAISE EXCEPTION 'Unexpected default privileges';
  END IF;
  IF EXISTS (SELECT FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) a
    JOIN pg_roles gr ON gr.oid=a.grantee
    WHERE gr.rolname IN ('tp_app','tp_readonly') AND a.is_grantable) THEN
    RAISE EXCEPTION 'Unexpected runtime/backup function grant option';
  END IF;
END
$preflight$;
""");

            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.EnsureSchema(
                name: "media");

            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.EnsureSchema(
                name: "content");

            migrationBuilder.EnsureSchema(
                name: "pricing");

            migrationBuilder.CreateSequence(
                name: "sku_number_seq",
                schema: "catalog");

            migrationBuilder.CreateTable(
                name: "assets",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    mime_type = table.Column<string>(type: "text", nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    checksum_sha256 = table.Column<string>(type: "text", nullable: true),
                    default_alt = table.Column<string>(type: "text", nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'catalog'"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                    table.UniqueConstraint("ux_assets_storage_key", x => x.storage_key);
                    table.CheckConstraint("ck_assets_mime_type", "mime_type IN ('image/jpeg','image/png','image/webp','image/avif','video/mp4','video/quicktime')");
                    table.CheckConstraint("ck_assets_purpose", "purpose IN ('catalog','content','evidence','branding')");
                    table.CheckConstraint("ck_assets_size_by_kind", "(mime_type IN ('image/jpeg','image/png','image/webp','image/avif') AND byte_size > 0 AND byte_size <= 15728640) OR\n    (purpose = 'evidence' AND mime_type IN ('video/mp4','video/quicktime') AND byte_size > 0 AND byte_size <= 52428800)");
                });

            migrationBuilder.CreateTable(
                name: "product_types",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    sku_prefix = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    google_product_category = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_types", x => x.id);
                    table.UniqueConstraint("ux_product_types_code", x => x.code);
                    table.CheckConstraint("ck_product_types_code", "code ~ '^[a-z0-9_]{2,40}$'");
                    table.CheckConstraint("ck_product_types_sku_prefix", "sku_prefix ~ '^[A-Z]{2,4}$'");
                });

            migrationBuilder.CreateTable(
                name: "receipts",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    supplier_name = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'draft'"),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    posted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    posted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipts", x => x.id);
                    table.UniqueConstraint("ux_receipts_code", x => x.code);
                    table.CheckConstraint("ck_receipts_row", "(status = 'posted') = (posted_at IS NOT NULL)");
                    table.CheckConstraint("ck_receipts_status", "status IN ('draft','posted','cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "redirects",
                schema: "content",
                columns: table => new
                {
                    source_path = table.Column<string>(type: "text", nullable: false),
                    target_path = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    hit_count = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_redirects", x => x.source_path);
                    table.CheckConstraint("ck_redirects_row", "source_path <> target_path");
                    table.CheckConstraint("ck_redirects_source_path", "source_path ~ '^/[^?#]*$'");
                    table.CheckConstraint("ck_redirects_target_path", "target_path ~ '^/[^?#]*$'");
                });

            migrationBuilder.CreateTable(
                name: "stocktakes",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'counting'"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stocktakes", x => x.id);
                    table.UniqueConstraint("ux_stocktakes_code", x => x.code);
                    table.CheckConstraint("ck_stocktakes_status", "status IN ('counting','submitted','approved','cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "artisans",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    bio_json = table.Column<string>(type: "jsonb", nullable: true),
                    bio_html = table.Column<string>(type: "text", nullable: true),
                    portrait_media_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'draft'"),
                    focus_keyword = table.Column<string>(type: "text", nullable: true),
                    seo_title = table.Column<string>(type: "text", nullable: true),
                    seo_description = table.Column<string>(type: "text", nullable: true),
                    robots_noindex = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artisans", x => x.id);
                    table.UniqueConstraint("ux_artisans_slug", x => x.slug);
                    table.CheckConstraint("ck_artisans_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("ck_artisans_status", "status IN ('draft','published','archived')");
                    table.ForeignKey(
                        name: "fk_artisans_portrait_media_id",
                        column: x => x.portrait_media_id,
                        principalSchema: "media",
                        principalTable: "assets",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "categories",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description_html = table.Column<string>(type: "text", nullable: true),
                    body_html = table.Column<string>(type: "text", nullable: true),
                    body_json = table.Column<string>(type: "jsonb", nullable: true),
                    cover_media_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'hidden'"),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    focus_keyword = table.Column<string>(type: "text", nullable: true),
                    seo_title = table.Column<string>(type: "text", nullable: true),
                    seo_description = table.Column<string>(type: "text", nullable: true),
                    robots_noindex = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.UniqueConstraint("ux_categories_slug", x => x.slug);
                    table.CheckConstraint("ck_categories_row", "parent_id IS NULL OR parent_id <> id");
                    table.CheckConstraint("ck_categories_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("ck_categories_status", "status IN ('visible','hidden')");
                    table.ForeignKey(
                        name: "fk_categories_cover_media_id",
                        column: x => x.cover_media_id,
                        principalSchema: "media",
                        principalTable: "assets",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_categories_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "collections",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description_html = table.Column<string>(type: "text", nullable: true),
                    body_html = table.Column<string>(type: "text", nullable: true),
                    body_json = table.Column<string>(type: "jsonb", nullable: true),
                    cover_media_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'hidden'"),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    focus_keyword = table.Column<string>(type: "text", nullable: true),
                    seo_title = table.Column<string>(type: "text", nullable: true),
                    seo_description = table.Column<string>(type: "text", nullable: true),
                    robots_noindex = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collections", x => x.id);
                    table.UniqueConstraint("ux_collections_slug", x => x.slug);
                    table.CheckConstraint("ck_collections_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("ck_collections_status", "status IN ('visible','hidden')");
                    table.ForeignKey(
                        name: "fk_collections_cover_media_id",
                        column: x => x.cover_media_id,
                        principalSchema: "media",
                        principalTable: "assets",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "materials",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku_code = table.Column<string>(type: "text", nullable: true),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true),
                    description_json = table.Column<string>(type: "jsonb", nullable: true),
                    description_html = table.Column<string>(type: "text", nullable: true),
                    cover_media_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'draft'"),
                    focus_keyword = table.Column<string>(type: "text", nullable: true),
                    seo_title = table.Column<string>(type: "text", nullable: true),
                    seo_description = table.Column<string>(type: "text", nullable: true),
                    robots_noindex = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_materials", x => x.id);
                    table.UniqueConstraint("ux_materials_slug", x => x.slug);
                    table.CheckConstraint("ck_materials_kind", "kind IN ('clay','metal','glass','ceramic','porcelain','wood','stone','other')");
                    table.CheckConstraint("ck_materials_sku_code", "sku_code ~ '^[A-Z]{2,3}$' AND sku_code <> 'XX'");
                    table.CheckConstraint("ck_materials_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("ck_materials_status", "status IN ('draft','published','archived')");
                    table.ForeignKey(
                        name: "fk_materials_cover_media_id",
                        column: x => x.cover_media_id,
                        principalSchema: "media",
                        principalTable: "assets",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "upload_sessions",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    original_name = table.Column<string>(type: "text", nullable: false),
                    mime_type = table.Column<string>(type: "text", nullable: false),
                    expected_bytes = table.Column<long>(type: "bigint", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'pending'"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_upload_sessions", x => x.id);
                    table.UniqueConstraint("ux_upload_sessions_storage_key", x => x.storage_key);
                    table.CheckConstraint("ck_upload_sessions_status", "status IN ('pending','completed','expired','failed')");
                    table.CheckConstraint("ck_upload_size_by_kind", "purpose IN ('catalog','content','evidence','branding') AND (\n      (mime_type IN ('image/jpeg','image/png','image/webp','image/avif') AND expected_bytes > 0 AND expected_bytes <= 15728640) OR\n      (purpose = 'evidence' AND mime_type IN ('video/mp4','video/quicktime') AND expected_bytes > 0 AND expected_bytes <= 52428800))");
                    table.ForeignKey(
                        name: "fk_upload_sessions_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "assets",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "attribute_definitions",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    data_type = table.Column<string>(type: "text", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: true),
                    scope = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'product'"),
                    is_variant_axis = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    is_required_for_publish = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    is_filterable = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    is_visible = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true"),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attribute_definitions", x => x.id);
                    table.UniqueConstraint("ux_attribute_definitions_product_type_id_code", x => new { x.product_type_id, x.code });
                    table.CheckConstraint("ck_attr_axis", "NOT is_variant_axis OR (scope = 'variant' AND data_type IN ('number','option','text'))");
                    table.CheckConstraint("ck_attr_unit", "unit IS NULL OR data_type IN ('number','dimensions')");
                    table.CheckConstraint("ck_attribute_definitions_code", "code ~ '^[a-z0-9_]{2,40}$'");
                    table.CheckConstraint("ck_attribute_definitions_data_type", "data_type IN ('text','number','option','boolean','dimensions')");
                    table.CheckConstraint("ck_attribute_definitions_scope", "scope IN ('product','variant')");
                    table.ForeignKey(
                        name: "fk_attribute_definitions_product_type_id",
                        column: x => x.product_type_id,
                        principalSchema: "catalog",
                        principalTable: "product_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku_base = table.Column<string>(type: "text", nullable: false),
                    product_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    subtitle = table.Column<string>(type: "text", nullable: true),
                    short_description = table.Column<string>(type: "text", nullable: true),
                    description_json = table.Column<string>(type: "jsonb", nullable: true),
                    description_html = table.Column<string>(type: "text", nullable: true),
                    care_html = table.Column<string>(type: "text", nullable: true),
                    description_word_count = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    primary_category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    material_id = table.Column<Guid>(type: "uuid", nullable: true),
                    artisan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin_note = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'draft'"),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    first_published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    archived_redirect_path = table.Column<string>(type: "text", nullable: true),
                    sold_out_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    seo_keep_after_sold = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    focus_keyword = table.Column<string>(type: "text", nullable: true),
                    is_featured = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    search_text = table.Column<string>(type: "text", nullable: false, defaultValueSql: "''"),
                    seo_title = table.Column<string>(type: "text", nullable: true),
                    seo_description = table.Column<string>(type: "text", nullable: true),
                    canonical_url = table.Column<string>(type: "text", nullable: true),
                    robots_noindex = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    og_media_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.UniqueConstraint("ux_products_sku_base", x => x.sku_base);
                    table.UniqueConstraint("ux_products_slug", x => x.slug);
                    table.CheckConstraint("ck_products_name", "length(name) BETWEEN 2 AND 200");
                    table.CheckConstraint("ck_products_published", "status <> 'active' OR published_at IS NOT NULL");
                    table.CheckConstraint("ck_products_sku_base", "sku_base ~ '^[A-Z]{2,4}-[A-Z]{2,3}-[0-9]{4,}$' AND length(sku_base) <= 40");
                    table.CheckConstraint("ck_products_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("ck_products_status", "status IN ('draft','active','archived')");
                    table.CheckConstraint("ck_products_word_count", "description_word_count >= 0");
                    table.ForeignKey(
                        name: "fk_products_artisan_id",
                        column: x => x.artisan_id,
                        principalSchema: "catalog",
                        principalTable: "artisans",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_products_material_id",
                        column: x => x.material_id,
                        principalSchema: "catalog",
                        principalTable: "materials",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_products_og_media_id",
                        column: x => x.og_media_id,
                        principalSchema: "media",
                        principalTable: "assets",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_products_primary_category_id",
                        column: x => x.primary_category_id,
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_products_product_type_id",
                        column: x => x.product_type_id,
                        principalSchema: "catalog",
                        principalTable: "product_types",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "attribute_options",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    attribute_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attribute_options", x => x.id);
                    table.UniqueConstraint("ux_attribute_options_attribute_id_code", x => new { x.attribute_id, x.code });
                    table.CheckConstraint("ck_attribute_options_code", "code ~ '^[a-z0-9_]{1,60}$'");
                    table.ForeignKey(
                        name: "fk_attribute_options_attribute_id",
                        column: x => x.attribute_id,
                        principalSchema: "catalog",
                        principalTable: "attribute_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "collection_products",
                schema: "catalog",
                columns: table => new
                {
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_products", x => new { x.collection_id, x.product_id });
                    table.ForeignKey(
                        name: "fk_collection_products_collection_id",
                        column: x => x.collection_id,
                        principalSchema: "catalog",
                        principalTable: "collections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_collection_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_categories",
                schema: "catalog",
                columns: table => new
                {
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_categories", x => new { x.product_id, x.category_id });
                    table.ForeignKey(
                        name: "fk_product_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_categories_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_relations",
                schema: "catalog",
                columns: table => new
                {
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    related_product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_relations", x => new { x.product_id, x.related_product_id });
                    table.CheckConstraint("ck_product_relations_row", "product_id <> related_product_id");
                    table.ForeignKey(
                        name: "fk_product_relations_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_product_relations_related_product_id",
                        column: x => x.related_product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "variants",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    price_vnd = table.Column<long>(type: "bigint", nullable: true),
                    is_unique_piece = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    weight_g = table.Column<int>(type: "integer", nullable: true),
                    package_dims_cm = table.Column<string>(type: "jsonb", nullable: true),
                    barcode = table.Column<string>(type: "text", nullable: true),
                    low_stock_threshold = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_variants", x => x.id);
                    table.UniqueConstraint("ux_variants_sku", x => x.sku);
                    table.CheckConstraint("ck_variants_low_stock_threshold", "low_stock_threshold >= 0");
                    table.CheckConstraint("ck_variants_price", "price_vnd IS NULL OR (price_vnd > 0 AND price_vnd <= 10000000000)");
                    table.CheckConstraint("ck_variants_sku", "sku ~ '^[A-Z0-9][A-Z0-9-]{1,39}$'");
                    table.CheckConstraint("ck_variants_status", "status IN ('active','inactive')");
                    table.CheckConstraint("ck_variants_weight_g", "weight_g IS NULL OR weight_g > 0");
                    table.ForeignKey(
                        name: "fk_variants_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_attribute_values",
                schema: "catalog",
                columns: table => new
                {
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attribute_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value_text = table.Column<string>(type: "text", nullable: true),
                    value_number = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    option_id = table.Column<Guid>(type: "uuid", nullable: true),
                    value_bool = table.Column<bool>(type: "boolean", nullable: true),
                    value_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_attribute_values", x => new { x.product_id, x.attribute_id });
                    table.CheckConstraint("ck_pav_one_value", "num_nonnulls(value_text, value_number, option_id, value_bool, value_json) = 1");
                    table.ForeignKey(
                        name: "fk_product_attribute_values_attribute_id",
                        column: x => x.attribute_id,
                        principalSchema: "catalog",
                        principalTable: "attribute_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_attribute_values_option_id",
                        column: x => x.option_id,
                        principalSchema: "catalog",
                        principalTable: "attribute_options",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_product_attribute_values_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_media",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alt_text = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    is_cover = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_media", x => x.id);
                    table.UniqueConstraint("ux_product_media_product_id_asset_id", x => new { x.product_id, x.asset_id });
                    table.CheckConstraint("ck_product_media_alt_text", "length(trim(alt_text)) BETWEEN 3 AND 200");
                    table.ForeignKey(
                        name: "fk_product_media_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_media_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_product_media_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "receipt_lines",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_cost_vnd = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt_lines", x => x.id);
                    table.UniqueConstraint("ux_receipt_lines_receipt_id_variant_id", x => new { x.receipt_id, x.variant_id });
                    table.CheckConstraint("ck_receipt_lines_quantity", "quantity > 0");
                    table.CheckConstraint("ck_receipt_lines_unit_cost_vnd", "unit_cost_vnd >= 0");
                    table.ForeignKey(
                        name: "fk_receipt_lines_receipt_id",
                        column: x => x.receipt_id,
                        principalSchema: "inventory",
                        principalTable: "receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_receipt_lines_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "variants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "sale_prices",
                schema: "pricing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_price_vnd = table.Column<long>(type: "bigint", nullable: false),
                    starts_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sale_prices", x => x.id);
                    table.CheckConstraint("ck_sale_prices_row", "ends_at > starts_at");
                    table.CheckConstraint("ck_sale_prices_sale_price_vnd", "sale_price_vnd > 0");
                    table.ForeignKey(
                        name: "fk_sale_prices_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_levels",
                schema: "inventory",
                columns: table => new
                {
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_hand = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    reserved = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    avg_cost_vnd = table.Column<long>(type: "bigint", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_levels", x => x.variant_id);
                    table.CheckConstraint("ck_stock_levels_avg_cost_vnd", "avg_cost_vnd IS NULL OR avg_cost_vnd >= 0");
                    table.CheckConstraint("ck_stock_levels_on_hand", "on_hand >= 0");
                    table.CheckConstraint("ck_stock_levels_reserved", "reserved >= 0");
                    table.CheckConstraint("ck_stock_reserved", "reserved <= on_hand");
                    table.ForeignKey(
                        name: "fk_stock_levels_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_movements",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    movement_type = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_cost_vnd = table.Column<long>(type: "bigint", nullable: true),
                    on_hand_after = table.Column<int>(type: "integer", nullable: false),
                    ref_type = table.Column<string>(type: "text", nullable: true),
                    ref_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movements", x => x.id);
                    table.CheckConstraint("ck_mv_direction", "(movement_type IN ('opening','receipt','return_restock') AND quantity > 0) OR\n    (movement_type IN ('sale','damage','replacement_out') AND quantity < 0) OR\n    (movement_type IN ('adjustment','stocktake'))");
                    table.CheckConstraint("ck_mv_reason", "movement_type NOT IN ('adjustment','damage') OR length(trim(coalesce(reason,''))) >= 3");
                    table.CheckConstraint("ck_stock_movements_actor_type", "actor_type IN ('staff','system')");
                    table.CheckConstraint("ck_stock_movements_movement_type", "movement_type IN\n                   ('opening','receipt','sale','return_restock','adjustment','stocktake','damage','replacement_out')");
                    table.CheckConstraint("ck_stock_movements_on_hand_after", "on_hand_after >= 0");
                    table.CheckConstraint("ck_stock_movements_quantity", "quantity <> 0");
                    table.CheckConstraint("ck_stock_movements_ref_type", "ref_type IN ('receipt','order','return','adjustment','stocktake','import')");
                    table.CheckConstraint("ck_stock_movements_unit_cost_vnd", "unit_cost_vnd IS NULL OR unit_cost_vnd >= 0");
                    table.ForeignKey(
                        name: "fk_stock_movements_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stocktake_lines",
                schema: "inventory",
                columns: table => new
                {
                    stocktake_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counted_qty = table.Column<int>(type: "integer", nullable: false),
                    system_qty_at_count = table.Column<int>(type: "integer", nullable: false),
                    counted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    counted_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stocktake_lines", x => new { x.stocktake_id, x.variant_id });
                    table.CheckConstraint("ck_stocktake_lines_counted_qty", "counted_qty >= 0");
                    table.CheckConstraint("ck_stocktake_lines_system_qty_at_count", "system_qty_at_count >= 0");
                    table.ForeignKey(
                        name: "fk_stocktake_lines_stocktake_id",
                        column: x => x.stocktake_id,
                        principalSchema: "inventory",
                        principalTable: "stocktakes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_stocktake_lines_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "variants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "variant_attribute_values",
                schema: "catalog",
                columns: table => new
                {
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attribute_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value_text = table.Column<string>(type: "text", nullable: true),
                    value_number = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    option_id = table.Column<Guid>(type: "uuid", nullable: true),
                    value_bool = table.Column<bool>(type: "boolean", nullable: true),
                    value_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_variant_attribute_values", x => new { x.variant_id, x.attribute_id });
                    table.CheckConstraint("ck_vav_one_value", "num_nonnulls(value_text, value_number, option_id, value_bool, value_json) = 1");
                    table.ForeignKey(
                        name: "fk_variant_attribute_values_attribute_id",
                        column: x => x.attribute_id,
                        principalSchema: "catalog",
                        principalTable: "attribute_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_variant_attribute_values_option_id",
                        column: x => x.option_id,
                        principalSchema: "catalog",
                        principalTable: "attribute_options",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_variant_attribute_values_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_categories_parent",
                schema: "catalog",
                table: "categories",
                column: "parent_id");

            migrationBuilder.Sql("""
ALTER TABLE catalog.materials ADD CONSTRAINT ux_materials_sku_code UNIQUE (sku_code);
""");

            migrationBuilder.CreateIndex(
                name: "ix_pav_filter_number",
                schema: "catalog",
                table: "product_attribute_values",
                columns: new[] { "attribute_id", "value_number" },
                filter: "value_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pav_filter_option",
                schema: "catalog",
                table: "product_attribute_values",
                columns: new[] { "attribute_id", "option_id" },
                filter: "option_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_product_categories_cat",
                schema: "catalog",
                table: "product_categories",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ux_product_media_cover",
                schema: "catalog",
                table: "product_media",
                column: "product_id",
                unique: true,
                filter: "is_cover");

            migrationBuilder.CreateIndex(
                name: "ix_products_artisan",
                schema: "catalog",
                table: "products",
                column: "artisan_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_category",
                schema: "catalog",
                table: "products",
                column: "primary_category_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_material",
                schema: "catalog",
                table: "products",
                column: "material_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_search",
                schema: "catalog",
                table: "products",
                column: "search_text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_products_status_pub",
                schema: "catalog",
                table: "products",
                columns: new[] { "status", "published_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_products_type",
                schema: "catalog",
                table: "products",
                column: "product_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_mv_ref",
                schema: "inventory",
                table: "stock_movements",
                columns: new[] { "ref_type", "ref_id" });

            migrationBuilder.CreateIndex(
                name: "ix_mv_variant_time",
                schema: "inventory",
                table: "stock_movements",
                columns: new[] { "variant_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_upload_pending",
                schema: "media",
                table: "upload_sessions",
                column: "expires_at",
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_vav_filter_number",
                schema: "catalog",
                table: "variant_attribute_values",
                columns: new[] { "attribute_id", "value_number" },
                filter: "value_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_vav_filter_option",
                schema: "catalog",
                table: "variant_attribute_values",
                columns: new[] { "attribute_id", "option_id" },
                filter: "option_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_variants_product",
                schema: "catalog",
                table: "variants",
                columns: new[] { "product_id", "position" });
            migrationBuilder.Sql("""
CREATE INDEX ix_stock_available ON inventory.stock_levels ((on_hand - reserved));
ALTER TABLE pricing.sale_prices ADD CONSTRAINT ex_sale_prices_no_overlap EXCLUDE USING gist
  (variant_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&);
REVOKE ALL ON SCHEMA public,platform,iam FROM PUBLIC;
GRANT USAGE ON SCHEMA public,platform,iam TO tp_app,tp_readonly;
REVOKE ALL ON ALL TABLES IN SCHEMA platform,iam FROM tp_app,tp_readonly;
GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA platform,iam TO tp_app;
GRANT SELECT ON ALL TABLES IN SCHEMA platform,iam TO tp_readonly;
REVOKE UPDATE,DELETE,TRUNCATE,MAINTAIN ON platform.audit_logs,platform.administrative_data_imports FROM tp_app;
REVOKE ALL ON public."__EFMigrationsHistory" FROM PUBLIC,tp_app,tp_readonly;
GRANT SELECT ON public."__EFMigrationsHistory" TO tp_app,tp_readonly;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA platform,iam FROM PUBLIC,tp_app,tp_readonly;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA platform,iam TO tp_app;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA platform,iam TO tp_readonly;
REVOKE ALL ON FUNCTION public.f_unaccent(text) FROM PUBLIC,tp_app,tp_readonly;
GRANT EXECUTE ON FUNCTION public.f_unaccent(text),public.unaccent(text),public.unaccent(regdictionary,text),
  pg_catalog.pg_try_advisory_xact_lock(integer,integer), pg_catalog.pg_advisory_xact_lock(bigint),
  pg_catalog.pg_try_advisory_lock(bigint),pg_catalog.pg_advisory_unlock(bigint) TO tp_app;
-- No blanket default DML: new append-only tables must not become writable.
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA platform,iam GRANT SELECT ON TABLES TO tp_readonly;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA platform,iam GRANT USAGE ON SEQUENCES TO tp_app;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA platform,iam GRANT SELECT ON SEQUENCES TO tp_readonly;
REVOKE ALL ON SCHEMA media,catalog,inventory,pricing,content FROM PUBLIC,tp_app,tp_readonly;
GRANT USAGE ON SCHEMA media,catalog,inventory,pricing,content TO tp_app,tp_readonly;
REVOKE ALL ON media.assets,media.upload_sessions,catalog.product_types,catalog.attribute_definitions,catalog.attribute_options,catalog.materials,catalog.artisans,catalog.categories,catalog.products,catalog.product_categories,catalog.product_relations,catalog.variants,catalog.product_attribute_values,catalog.variant_attribute_values,catalog.product_media,catalog.collections,catalog.collection_products,inventory.stock_levels,inventory.stock_movements,inventory.receipts,inventory.receipt_lines,inventory.stocktakes,inventory.stocktake_lines,pricing.sale_prices,content.redirects FROM PUBLIC,tp_app,tp_readonly;
GRANT SELECT,INSERT,UPDATE,DELETE ON media.assets,media.upload_sessions,catalog.product_types,catalog.attribute_definitions,catalog.attribute_options,catalog.materials,catalog.artisans,catalog.categories,catalog.products,catalog.product_categories,catalog.product_relations,catalog.variants,catalog.product_attribute_values,catalog.variant_attribute_values,catalog.product_media,catalog.collections,catalog.collection_products,inventory.stock_levels,inventory.stock_movements,inventory.receipts,inventory.receipt_lines,inventory.stocktakes,inventory.stocktake_lines,pricing.sale_prices,content.redirects TO tp_app;
GRANT SELECT ON media.assets,media.upload_sessions,catalog.product_types,catalog.attribute_definitions,catalog.attribute_options,catalog.materials,catalog.artisans,catalog.categories,catalog.products,catalog.product_categories,catalog.product_relations,catalog.variants,catalog.product_attribute_values,catalog.variant_attribute_values,catalog.product_media,catalog.collections,catalog.collection_products,inventory.stock_levels,inventory.stock_movements,inventory.receipts,inventory.receipt_lines,inventory.stocktakes,inventory.stocktake_lines,pricing.sale_prices,content.redirects TO tp_readonly;
REVOKE UPDATE,DELETE,TRUNCATE,MAINTAIN ON inventory.stock_movements FROM tp_app;
REVOKE ALL ON SEQUENCE catalog.sku_number_seq FROM PUBLIC,tp_app,tp_readonly;
GRANT USAGE ON SEQUENCE catalog.sku_number_seq TO tp_app;
GRANT SELECT ON SEQUENCE catalog.sku_number_seq TO tp_readonly;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA media,catalog,inventory,pricing,content GRANT SELECT ON TABLES TO tp_readonly;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA media,catalog,inventory,pricing,content GRANT USAGE ON SEQUENCES TO tp_app;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA media,catalog,inventory,pricing,content GRANT SELECT ON SEQUENCES TO tp_readonly;
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
-- Disposable DB only, no active writers. Lock the full owned set before checking data.
DO $p101_down$
DECLARE target text; occupied boolean;
  owned text[] := ARRAY['media.assets','media.upload_sessions','catalog.product_types','catalog.attribute_definitions','catalog.attribute_options','catalog.materials','catalog.artisans','catalog.categories','catalog.products','catalog.product_categories','catalog.product_relations','catalog.variants','catalog.product_attribute_values','catalog.variant_attribute_values','catalog.product_media','catalog.collections','catalog.collection_products','inventory.stock_levels','inventory.stock_movements','inventory.receipts','inventory.receipt_lines','inventory.stocktakes','inventory.stocktake_lines','pricing.sale_prices','content.redirects'];
  relations text[] := ARRAY['media.assets','media.upload_sessions','catalog.product_types','catalog.attribute_definitions','catalog.attribute_options','catalog.materials','catalog.artisans','catalog.categories','catalog.products','catalog.product_categories','catalog.product_relations','catalog.variants','catalog.product_attribute_values','catalog.variant_attribute_values','catalog.product_media','catalog.collections','catalog.collection_products','inventory.stock_levels','inventory.stock_movements','inventory.receipts','inventory.receipt_lines','inventory.stocktakes','inventory.stocktake_lines','pricing.sale_prices','content.redirects','catalog.sku_number_seq','media.pk_assets','media.ux_assets_storage_key','media.pk_upload_sessions','media.ux_upload_sessions_storage_key','media.ix_upload_pending','catalog.pk_product_types','catalog.ux_product_types_code','catalog.pk_attribute_definitions','catalog.ux_attribute_definitions_product_type_id_code','catalog.pk_attribute_options','catalog.ux_attribute_options_attribute_id_code','catalog.pk_materials','catalog.ux_materials_sku_code','catalog.ux_materials_slug','catalog.pk_artisans','catalog.ux_artisans_slug','catalog.pk_categories','catalog.ux_categories_slug','catalog.ix_categories_parent','catalog.pk_products','catalog.ux_products_sku_base','catalog.ux_products_slug','catalog.ix_products_status_pub','catalog.ix_products_type','catalog.ix_products_category','catalog.ix_products_material','catalog.ix_products_artisan','catalog.ix_products_search','catalog.pk_product_categories','catalog.ix_product_categories_cat','catalog.pk_product_relations','catalog.pk_variants','catalog.ux_variants_sku','catalog.ix_variants_product','catalog.pk_product_attribute_values','catalog.ix_pav_filter_option','catalog.ix_pav_filter_number','catalog.pk_variant_attribute_values','catalog.ix_vav_filter_option','catalog.ix_vav_filter_number','catalog.pk_product_media','catalog.ux_product_media_product_id_asset_id','catalog.ux_product_media_cover','catalog.pk_collections','catalog.ux_collections_slug','catalog.pk_collection_products','inventory.pk_stock_levels','inventory.ix_stock_available','inventory.pk_stock_movements','inventory.ix_mv_variant_time','inventory.ix_mv_ref','inventory.pk_receipts','inventory.ux_receipts_code','inventory.pk_receipt_lines','inventory.ux_receipt_lines_receipt_id_variant_id','inventory.pk_stocktakes','inventory.ux_stocktakes_code','inventory.pk_stocktake_lines','pricing.pk_sale_prices','pricing.ex_sale_prices_no_overlap','content.pk_redirects'];
  constraints text[] := ARRAY['media.assets:pk_assets','media.assets:ux_assets_storage_key','media.assets:ck_assets_mime_type','media.assets:ck_assets_purpose','media.assets:ck_assets_size_by_kind','media.upload_sessions:pk_upload_sessions','media.upload_sessions:ux_upload_sessions_storage_key','media.upload_sessions:ck_upload_sessions_status','media.upload_sessions:fk_upload_sessions_asset_id','media.upload_sessions:ck_upload_size_by_kind','catalog.product_types:pk_product_types','catalog.product_types:ux_product_types_code','catalog.product_types:ck_product_types_code','catalog.product_types:ck_product_types_sku_prefix','catalog.attribute_definitions:pk_attribute_definitions','catalog.attribute_definitions:fk_attribute_definitions_product_type_id','catalog.attribute_definitions:ck_attribute_definitions_code','catalog.attribute_definitions:ck_attribute_definitions_data_type','catalog.attribute_definitions:ck_attribute_definitions_scope','catalog.attribute_definitions:ux_attribute_definitions_product_type_id_code','catalog.attribute_definitions:ck_attr_unit','catalog.attribute_definitions:ck_attr_axis','catalog.attribute_options:pk_attribute_options','catalog.attribute_options:fk_attribute_options_attribute_id','catalog.attribute_options:ck_attribute_options_code','catalog.attribute_options:ux_attribute_options_attribute_id_code','catalog.materials:pk_materials','catalog.materials:ux_materials_sku_code','catalog.materials:ck_materials_sku_code','catalog.materials:ux_materials_slug','catalog.materials:ck_materials_slug','catalog.materials:ck_materials_kind','catalog.materials:fk_materials_cover_media_id','catalog.materials:ck_materials_status','catalog.artisans:pk_artisans','catalog.artisans:ux_artisans_slug','catalog.artisans:ck_artisans_slug','catalog.artisans:fk_artisans_portrait_media_id','catalog.artisans:ck_artisans_status','catalog.categories:pk_categories','catalog.categories:fk_categories_parent_id','catalog.categories:ux_categories_slug','catalog.categories:ck_categories_slug','catalog.categories:fk_categories_cover_media_id','catalog.categories:ck_categories_status','catalog.categories:ck_categories_row','catalog.products:pk_products','catalog.products:ux_products_sku_base','catalog.products:ck_products_sku_base','catalog.products:fk_products_product_type_id','catalog.products:ux_products_slug','catalog.products:ck_products_slug','catalog.products:ck_products_name','catalog.products:ck_products_word_count','catalog.products:fk_products_primary_category_id','catalog.products:fk_products_material_id','catalog.products:fk_products_artisan_id','catalog.products:ck_products_status','catalog.products:fk_products_og_media_id','catalog.products:ck_products_published','catalog.product_categories:fk_product_categories_product_id','catalog.product_categories:fk_product_categories_category_id','catalog.product_categories:pk_product_categories','catalog.product_relations:fk_product_relations_product_id','catalog.product_relations:fk_product_relations_related_product_id','catalog.product_relations:pk_product_relations','catalog.product_relations:ck_product_relations_row','catalog.variants:pk_variants','catalog.variants:fk_variants_product_id','catalog.variants:ux_variants_sku','catalog.variants:ck_variants_sku','catalog.variants:ck_variants_price','catalog.variants:ck_variants_weight_g','catalog.variants:ck_variants_low_stock_threshold','catalog.variants:ck_variants_status','catalog.product_attribute_values:fk_product_attribute_values_product_id','catalog.product_attribute_values:fk_product_attribute_values_attribute_id','catalog.product_attribute_values:fk_product_attribute_values_option_id','catalog.product_attribute_values:pk_product_attribute_values','catalog.product_attribute_values:ck_pav_one_value','catalog.variant_attribute_values:fk_variant_attribute_values_variant_id','catalog.variant_attribute_values:fk_variant_attribute_values_attribute_id','catalog.variant_attribute_values:fk_variant_attribute_values_option_id','catalog.variant_attribute_values:pk_variant_attribute_values','catalog.variant_attribute_values:ck_vav_one_value','catalog.product_media:pk_product_media','catalog.product_media:fk_product_media_product_id','catalog.product_media:fk_product_media_variant_id','catalog.product_media:fk_product_media_asset_id','catalog.product_media:ck_product_media_alt_text','catalog.product_media:ux_product_media_product_id_asset_id','catalog.collections:pk_collections','catalog.collections:ux_collections_slug','catalog.collections:ck_collections_slug','catalog.collections:fk_collections_cover_media_id','catalog.collections:ck_collections_status','catalog.collection_products:fk_collection_products_collection_id','catalog.collection_products:fk_collection_products_product_id','catalog.collection_products:pk_collection_products','inventory.stock_levels:pk_stock_levels','inventory.stock_levels:fk_stock_levels_variant_id','inventory.stock_levels:ck_stock_levels_on_hand','inventory.stock_levels:ck_stock_levels_reserved','inventory.stock_levels:ck_stock_levels_avg_cost_vnd','inventory.stock_levels:ck_stock_reserved','inventory.stock_movements:pk_stock_movements','inventory.stock_movements:fk_stock_movements_variant_id','inventory.stock_movements:ck_stock_movements_movement_type','inventory.stock_movements:ck_stock_movements_quantity','inventory.stock_movements:ck_stock_movements_unit_cost_vnd','inventory.stock_movements:ck_stock_movements_on_hand_after','inventory.stock_movements:ck_stock_movements_ref_type','inventory.stock_movements:ck_stock_movements_actor_type','inventory.stock_movements:ck_mv_direction','inventory.stock_movements:ck_mv_reason','inventory.receipts:pk_receipts','inventory.receipts:ux_receipts_code','inventory.receipts:ck_receipts_status','inventory.receipts:ck_receipts_row','inventory.receipt_lines:pk_receipt_lines','inventory.receipt_lines:fk_receipt_lines_receipt_id','inventory.receipt_lines:fk_receipt_lines_variant_id','inventory.receipt_lines:ck_receipt_lines_quantity','inventory.receipt_lines:ck_receipt_lines_unit_cost_vnd','inventory.receipt_lines:ux_receipt_lines_receipt_id_variant_id','inventory.stocktakes:pk_stocktakes','inventory.stocktakes:ux_stocktakes_code','inventory.stocktakes:ck_stocktakes_status','inventory.stocktake_lines:fk_stocktake_lines_stocktake_id','inventory.stocktake_lines:fk_stocktake_lines_variant_id','inventory.stocktake_lines:ck_stocktake_lines_counted_qty','inventory.stocktake_lines:ck_stocktake_lines_system_qty_at_count','inventory.stocktake_lines:pk_stocktake_lines','pricing.sale_prices:pk_sale_prices','pricing.sale_prices:fk_sale_prices_variant_id','pricing.sale_prices:ck_sale_prices_sale_price_vnd','pricing.sale_prices:ck_sale_prices_row','pricing.sale_prices:ex_sale_prices_no_overlap','content.redirects:pk_redirects','content.redirects:ck_redirects_source_path','content.redirects:ck_redirects_target_path','content.redirects:ck_redirects_row'];
  columns text[] := ARRAY['media.assets:id','media.assets:storage_key','media.assets:mime_type','media.assets:byte_size','media.assets:width','media.assets:height','media.assets:checksum_sha256','media.assets:default_alt','media.assets:purpose','media.assets:created_by','media.assets:created_at','media.upload_sessions:id','media.upload_sessions:storage_key','media.upload_sessions:original_name','media.upload_sessions:mime_type','media.upload_sessions:expected_bytes','media.upload_sessions:purpose','media.upload_sessions:status','media.upload_sessions:created_by','media.upload_sessions:created_at','media.upload_sessions:expires_at','media.upload_sessions:completed_at','media.upload_sessions:asset_id','catalog.product_types:id','catalog.product_types:code','catalog.product_types:sku_prefix','catalog.product_types:name','catalog.product_types:description','catalog.product_types:google_product_category','catalog.product_types:sort_order','catalog.product_types:is_active','catalog.product_types:created_at','catalog.product_types:updated_at','catalog.attribute_definitions:id','catalog.attribute_definitions:product_type_id','catalog.attribute_definitions:code','catalog.attribute_definitions:name','catalog.attribute_definitions:data_type','catalog.attribute_definitions:unit','catalog.attribute_definitions:scope','catalog.attribute_definitions:is_variant_axis','catalog.attribute_definitions:is_required_for_publish','catalog.attribute_definitions:is_filterable','catalog.attribute_definitions:is_visible','catalog.attribute_definitions:sort_order','catalog.attribute_definitions:is_active','catalog.attribute_options:id','catalog.attribute_options:attribute_id','catalog.attribute_options:code','catalog.attribute_options:label','catalog.attribute_options:sort_order','catalog.attribute_options:is_active','catalog.materials:id','catalog.materials:sku_code','catalog.materials:slug','catalog.materials:name','catalog.materials:kind','catalog.materials:summary','catalog.materials:description_json','catalog.materials:description_html','catalog.materials:cover_media_id','catalog.materials:status','catalog.materials:focus_keyword','catalog.materials:seo_title','catalog.materials:seo_description','catalog.materials:robots_noindex','catalog.materials:sort_order','catalog.materials:created_at','catalog.materials:updated_at','catalog.artisans:id','catalog.artisans:slug','catalog.artisans:name','catalog.artisans:title','catalog.artisans:bio_json','catalog.artisans:bio_html','catalog.artisans:portrait_media_id','catalog.artisans:status','catalog.artisans:focus_keyword','catalog.artisans:seo_title','catalog.artisans:seo_description','catalog.artisans:robots_noindex','catalog.artisans:created_at','catalog.artisans:updated_at','catalog.categories:id','catalog.categories:parent_id','catalog.categories:slug','catalog.categories:name','catalog.categories:description_html','catalog.categories:body_html','catalog.categories:body_json','catalog.categories:cover_media_id','catalog.categories:status','catalog.categories:sort_order','catalog.categories:focus_keyword','catalog.categories:seo_title','catalog.categories:seo_description','catalog.categories:robots_noindex','catalog.categories:created_at','catalog.categories:updated_at','catalog.products:id','catalog.products:sku_base','catalog.products:product_type_id','catalog.products:slug','catalog.products:name','catalog.products:subtitle','catalog.products:short_description','catalog.products:description_json','catalog.products:description_html','catalog.products:care_html','catalog.products:description_word_count','catalog.products:primary_category_id','catalog.products:material_id','catalog.products:artisan_id','catalog.products:origin_note','catalog.products:status','catalog.products:published_at','catalog.products:first_published_at','catalog.products:archived_redirect_path','catalog.products:sold_out_at','catalog.products:seo_keep_after_sold','catalog.products:focus_keyword','catalog.products:is_featured','catalog.products:search_text','catalog.products:seo_title','catalog.products:seo_description','catalog.products:canonical_url','catalog.products:robots_noindex','catalog.products:og_media_id','catalog.products:created_by','catalog.products:updated_by','catalog.products:created_at','catalog.products:updated_at','catalog.product_categories:product_id','catalog.product_categories:category_id','catalog.product_relations:product_id','catalog.product_relations:related_product_id','catalog.product_relations:sort_order','catalog.variants:id','catalog.variants:product_id','catalog.variants:sku','catalog.variants:name','catalog.variants:position','catalog.variants:price_vnd','catalog.variants:is_unique_piece','catalog.variants:weight_g','catalog.variants:package_dims_cm','catalog.variants:barcode','catalog.variants:low_stock_threshold','catalog.variants:status','catalog.variants:created_at','catalog.variants:updated_at','catalog.product_attribute_values:product_id','catalog.product_attribute_values:attribute_id','catalog.product_attribute_values:value_text','catalog.product_attribute_values:value_number','catalog.product_attribute_values:option_id','catalog.product_attribute_values:value_bool','catalog.product_attribute_values:value_json','catalog.variant_attribute_values:variant_id','catalog.variant_attribute_values:attribute_id','catalog.variant_attribute_values:value_text','catalog.variant_attribute_values:value_number','catalog.variant_attribute_values:option_id','catalog.variant_attribute_values:value_bool','catalog.variant_attribute_values:value_json','catalog.product_media:id','catalog.product_media:product_id','catalog.product_media:variant_id','catalog.product_media:asset_id','catalog.product_media:alt_text','catalog.product_media:sort_order','catalog.product_media:is_cover','catalog.collections:id','catalog.collections:slug','catalog.collections:name','catalog.collections:description_html','catalog.collections:body_html','catalog.collections:body_json','catalog.collections:cover_media_id','catalog.collections:status','catalog.collections:sort_order','catalog.collections:focus_keyword','catalog.collections:seo_title','catalog.collections:seo_description','catalog.collections:robots_noindex','catalog.collections:created_at','catalog.collections:updated_at','catalog.collection_products:collection_id','catalog.collection_products:product_id','catalog.collection_products:sort_order','inventory.stock_levels:variant_id','inventory.stock_levels:on_hand','inventory.stock_levels:reserved','inventory.stock_levels:avg_cost_vnd','inventory.stock_levels:updated_at','inventory.stock_movements:id','inventory.stock_movements:variant_id','inventory.stock_movements:movement_type','inventory.stock_movements:quantity','inventory.stock_movements:unit_cost_vnd','inventory.stock_movements:on_hand_after','inventory.stock_movements:ref_type','inventory.stock_movements:ref_id','inventory.stock_movements:reason','inventory.stock_movements:actor_type','inventory.stock_movements:actor_id','inventory.stock_movements:occurred_at','inventory.stock_movements:created_at','inventory.receipts:id','inventory.receipts:code','inventory.receipts:supplier_name','inventory.receipts:note','inventory.receipts:status','inventory.receipts:received_at','inventory.receipts:posted_at','inventory.receipts:posted_by','inventory.receipts:created_by','inventory.receipts:created_at','inventory.receipt_lines:id','inventory.receipt_lines:receipt_id','inventory.receipt_lines:variant_id','inventory.receipt_lines:quantity','inventory.receipt_lines:unit_cost_vnd','inventory.stocktakes:id','inventory.stocktakes:code','inventory.stocktakes:note','inventory.stocktakes:status','inventory.stocktakes:created_by','inventory.stocktakes:created_at','inventory.stocktakes:submitted_at','inventory.stocktakes:approved_by','inventory.stocktakes:approved_at','inventory.stocktake_lines:stocktake_id','inventory.stocktake_lines:variant_id','inventory.stocktake_lines:counted_qty','inventory.stocktake_lines:system_qty_at_count','inventory.stocktake_lines:counted_at','inventory.stocktake_lines:counted_by','pricing.sale_prices:id','pricing.sale_prices:variant_id','pricing.sale_prices:sale_price_vnd','pricing.sale_prices:starts_at','pricing.sale_prices:ends_at','pricing.sale_prices:note','pricing.sale_prices:created_by','pricing.sale_prices:created_at','content.redirects:source_path','content.redirects:target_path','content.redirects:created_at','content.redirects:hit_count'];
BEGIN
  FOREACH target IN ARRAY owned LOOP
    EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE', target);
  END LOOP;
  FOREACH target IN ARRAY owned LOOP
    EXECUTE format('SELECT EXISTS (SELECT FROM %s)', target) INTO occupied;
    IF occupied THEN RAISE EXCEPTION 'P1-01 Down refused: data in %; roll forward or restore an approved backup', target; END IF;
  END LOOP;
  -- Reject namespace-owned objects beyond the table/type/constraint catalogs
  -- checked below (collations, conversions, operators, text search, etc.).
  -- Only the per-schema table/sequence default ACLs created by Up are allowed.
  SELECT pg_describe_object(d.classid,d.objid,d.objsubid) INTO target
  FROM pg_depend d JOIN pg_namespace n ON n.oid=d.refobjid
  WHERE d.refclassid='pg_namespace'::regclass
    AND n.nspname IN ('media','catalog','inventory','pricing','content')
    AND NOT (
      d.classid IN ('pg_class'::regclass,'pg_proc'::regclass,'pg_type'::regclass,'pg_constraint'::regclass)
      OR (d.classid='pg_default_acl'::regclass AND d.objid IN
        (SELECT oid FROM pg_default_acl WHERE defaclrole='tp_owner'::regrole
          AND defaclnamespace=n.oid AND defaclobjtype IN ('r','S')))
    )
  LIMIT 1;
  IF FOUND THEN
    RAISE EXCEPTION 'P1-01 Down refused: object/dependency outside migration scope (namespace object: %)', target;
  END IF;
  IF EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
      WHERE n.nspname IN ('media','catalog','inventory','pricing','content') AND NOT n.nspname||'.'||c.relname = ANY(relations)) THEN
    RAISE EXCEPTION 'P1-01 Down refused: object/dependency outside migration scope (relation)';
  END IF;
  IF EXISTS (SELECT FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname IN ('media','catalog','inventory','pricing','content')) THEN
    RAISE EXCEPTION 'P1-01 Down refused: object/dependency outside migration scope (function)';
  END IF;
  IF EXISTS (SELECT FROM pg_type y JOIN pg_namespace n ON n.oid=y.typnamespace
      WHERE n.nspname IN ('media','catalog','inventory','pricing','content') AND NOT (y.typrelid IN (SELECT unnest(owned)::regclass)
        OR y.typelem IN (SELECT reltype FROM pg_class WHERE oid IN (SELECT unnest(owned)::regclass)))) THEN
    RAISE EXCEPTION 'P1-01 Down refused: object/dependency outside migration scope (type)';
  END IF;
  IF EXISTS (SELECT FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace
      WHERE c.oid IN (SELECT unnest(owned)::regclass) AND a.attnum>0 AND NOT a.attisdropped
        AND NOT n.nspname||'.'||c.relname||':'||a.attname = ANY(columns)) THEN
    RAISE EXCEPTION 'P1-01 Down refused: object/dependency outside migration scope (column)';
  END IF;
  IF EXISTS (SELECT FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace
      WHERE (c.conrelid IN (SELECT unnest(owned)::regclass) OR c.confrelid IN (SELECT unnest(owned)::regclass))
        AND NOT n.nspname||'.'||t.relname||':'||c.conname = ANY(constraints)) THEN
    RAISE EXCEPTION 'P1-01 Down refused: object/dependency outside migration scope (constraint)';
  END IF;
  IF EXISTS (SELECT FROM pg_trigger WHERE tgrelid IN (SELECT unnest(owned)::regclass) AND NOT tgisinternal) THEN
    RAISE EXCEPTION 'P1-01 Down refused: object/dependency outside migration scope (trigger)';
  END IF;
  -- Functions can depend on a table's composite/array type without a direct
  -- relation dependency. Reject those before any DROP, too.
  IF EXISTS (SELECT FROM pg_depend d WHERE d.refclassid='pg_type'::regclass
    AND d.refobjid IN (SELECT y.oid FROM pg_type y WHERE y.typrelid IN (SELECT unnest(owned)::regclass)
      OR y.typelem IN (SELECT reltype FROM pg_class WHERE oid IN (SELECT unnest(owned)::regclass)))
    AND NOT (d.classid='pg_type'::regclass AND d.objid IN
      (SELECT typarray FROM pg_type WHERE typrelid IN (SELECT unnest(owned)::regclass)))) THEN
    RAISE EXCEPTION 'P1-01 Down refused: object/dependency outside migration scope (composite type)';
  END IF;
  IF EXISTS (SELECT FROM pg_depend d WHERE d.refclassid='pg_class'::regclass
      AND d.refobjid IN (SELECT unnest(owned || ARRAY['catalog.sku_number_seq'])::regclass)
      AND NOT (
        (d.classid='pg_class'::regclass AND d.objid IN (SELECT unnest(relations)::regclass))
        OR (d.classid='pg_class'::regclass AND d.deptype='i' AND d.objid IN
          (SELECT reltoastrelid FROM pg_class WHERE oid IN (SELECT unnest(owned)::regclass)))
        OR (d.classid='pg_constraint'::regclass AND d.objid IN (SELECT oid FROM pg_constraint WHERE conrelid IN (SELECT unnest(owned)::regclass)))
        OR (d.classid='pg_type'::regclass AND d.objid IN (SELECT reltype FROM pg_class WHERE oid IN (SELECT unnest(owned)::regclass)))
        OR (d.classid='pg_attrdef'::regclass AND d.objid IN (SELECT oid FROM pg_attrdef WHERE adrelid IN (SELECT unnest(owned)::regclass)))
        OR (d.classid='pg_trigger'::regclass AND d.objid IN (SELECT oid FROM pg_trigger WHERE tgrelid IN (SELECT unnest(owned)::regclass) AND tgisinternal))
      )) THEN
    RAISE EXCEPTION 'P1-01 Down refused: object/dependency outside migration scope (dependency)';
  END IF;
END
$p101_down$;
""");

            migrationBuilder.DropTable(
                name: "collection_products",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_attribute_values",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_categories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_media",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_relations",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "receipt_lines",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "redirects",
                schema: "content");

            migrationBuilder.DropTable(
                name: "sale_prices",
                schema: "pricing");

            migrationBuilder.DropTable(
                name: "stock_levels",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "stock_movements",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "stocktake_lines",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "upload_sessions",
                schema: "media");

            migrationBuilder.DropTable(
                name: "variant_attribute_values",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "collections",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "receipts",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "stocktakes",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "attribute_options",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "variants",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "attribute_definitions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "products",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "artisans",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "materials",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "categories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_types",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "assets",
                schema: "media");

            migrationBuilder.DropSequence(
                name: "sku_number_seq",
                schema: "catalog");
            migrationBuilder.Sql("""
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA media,catalog,inventory,pricing,content REVOKE SELECT ON TABLES FROM tp_readonly;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA media,catalog,inventory,pricing,content REVOKE USAGE ON SEQUENCES FROM tp_app;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA media,catalog,inventory,pricing,content REVOKE SELECT ON SEQUENCES FROM tp_readonly;
""");
        }
    }
}
