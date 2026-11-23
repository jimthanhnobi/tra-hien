using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF-generated migration operations use fixed array arguments.
#pragma warning disable CA1707 // Keep the owner-approved migration name P2_01_CrmSalesFinanceFoundation.

namespace TeaPremium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P2_01_CrmSalesFinanceFoundation : Migration
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
    WHERE n.nspname IN ('platform','iam','public','media','catalog','inventory','pricing','content','crm','sales','finance') AND a.grantee=0) THEN
    RAISE EXCEPTION 'Unexpected PUBLIC object privileges';
  END IF;
  IF EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    CROSS JOIN LATERAL aclexplode(c.relacl) a JOIN pg_roles gr ON gr.oid=a.grantee
    WHERE n.nspname IN ('platform','iam','public','media','catalog','inventory','pricing','content','crm','sales','finance') AND
      ((gr.rolname='tp_readonly' AND (a.privilege_type <> 'SELECT' OR a.is_grantable)) OR
       (gr.rolname='tp_app' AND (a.is_grantable OR a.privilege_type IN ('TRUNCATE','TRIGGER','REFERENCES','MAINTAIN')
         OR (c.relkind='S' AND a.privilege_type='UPDATE')
         OR ((n.nspname,c.relname) IN (('sales','orders'),('sales','payments'),('sales','refunds'),('sales','order_status_history'),('crm','customers')) AND a.privilege_type='DELETE')
         OR ((n.nspname,c.relname) IN (('platform','audit_logs'),('platform','administrative_data_imports'),('inventory','stock_movements')) AND a.privilege_type IN ('UPDATE','DELETE')))))) THEN
    RAISE EXCEPTION 'Unexpected runtime/backup object privileges';
  END IF;
  IF EXISTS (SELECT FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid
    JOIN pg_namespace n ON n.oid=c.relnamespace CROSS JOIN LATERAL aclexplode(a.attacl) acl
    WHERE n.nspname IN ('platform','iam','public','media','catalog','inventory','pricing','content','crm','sales','finance') AND
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
                name: "crm");

            migrationBuilder.EnsureSchema(
                name: "sales");

            migrationBuilder.EnsureSchema(
                name: "finance");

            migrationBuilder.CreateSequence(
                name: "customer_code_seq",
                schema: "crm",
                startValue: 1000L);

            migrationBuilder.CreateTable(
                name: "customers",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    phone_e164 = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    zalo_name = table.Column<string>(type: "text", nullable: true),
                    customer_group = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'retail'"),
                    is_vip = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    origin = table.Column<string>(type: "text", nullable: false),
                    acquisition_source = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'unknown'"),
                    referred_by_customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contact_status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'"),
                    note_summary = table.Column<string>(type: "text", nullable: true),
                    had_legacy_purchase = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    first_delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivered_orders_count = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    lifetime_value_vnd = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    last_contacted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    search_text = table.Column<string>(type: "text", nullable: false, defaultValueSql: "''"),
                    merged_into_customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    erased_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                    table.UniqueConstraint("ux_customers_code", x => x.code);
                    table.CheckConstraint("ck_customer_contact", "contact_status = 'erased' OR merged_into_customer_id IS NOT NULL OR phone_e164 IS NOT NULL OR zalo_name IS NOT NULL");
                    table.CheckConstraint("ck_customer_not_self_ref", "referred_by_customer_id IS NULL OR referred_by_customer_id <> id");
                    table.CheckConstraint("ck_customers_acquisition_source", "acquisition_source IN ('unknown','legacy','google_organic','google_ads','facebook','zalo_personal','zalo_oa','referral','direct','offline','other')");
                    table.CheckConstraint("ck_customers_contact_status", "contact_status IN ('active','do_not_contact','erased')");
                    table.CheckConstraint("ck_customers_customer_group", "customer_group IN ('retail','wholesale','reseller')");
                    table.CheckConstraint("ck_customers_delivered_orders_count", "delivered_orders_count >= 0");
                    table.CheckConstraint("ck_customers_email", "email IS NULL OR email ~ '^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$'");
                    table.CheckConstraint("ck_customers_full_name", "length(trim(full_name)) >= 1");
                    table.CheckConstraint("ck_customers_lifetime_value_vnd", "lifetime_value_vnd >= 0");
                    table.CheckConstraint("ck_customers_origin", "origin IN ('legacy_import','web','admin')");
                    table.CheckConstraint("ck_customers_phone_e164", "phone_e164 ~ '^\\+84[0-9]{9}$'");
                    table.ForeignKey(
                        name: "fk_customers_merged_into_customer_id",
                        column: x => x.merged_into_customer_id,
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_customers_referred_by_customer_id",
                        column: x => x.referred_by_customer_id,
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "tags",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    color = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tags", x => x.id);
                    table.UniqueConstraint("ux_tags_name", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "vouchers",
                schema: "pricing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    discount_type = table.Column<string>(type: "text", nullable: false),
                    discount_value = table.Column<long>(type: "bigint", nullable: false),
                    max_discount_vnd = table.Column<long>(type: "bigint", nullable: true),
                    min_order_vnd = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    starts_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    usage_limit = table.Column<int>(type: "integer", nullable: true),
                    per_customer_limit = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "1"),
                    bound_customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    used_count = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vouchers", x => x.id);
                    table.UniqueConstraint("ux_vouchers_code", x => x.code);
                    table.CheckConstraint("ck_vouchers_code", "code ~ '^[A-Z0-9]{4,20}$'");
                    table.CheckConstraint("ck_vouchers_dates", "ends_at > starts_at");
                    table.CheckConstraint("ck_vouchers_discount_type", "discount_type IN ('fixed','percent')");
                    table.CheckConstraint("ck_vouchers_discount_value", "discount_value > 0");
                    table.CheckConstraint("ck_vouchers_kind", "kind IN ('referral_reward','manual')");
                    table.CheckConstraint("ck_vouchers_max_discount_vnd", "max_discount_vnd IS NULL OR max_discount_vnd > 0");
                    table.CheckConstraint("ck_vouchers_min_order_vnd", "min_order_vnd >= 0");
                    table.CheckConstraint("ck_vouchers_per_customer_limit", "per_customer_limit > 0");
                    table.CheckConstraint("ck_vouchers_percent_bounds", "discount_type <> 'percent' OR discount_value BETWEEN 1 AND 50");
                    table.CheckConstraint("ck_vouchers_status", "status IN ('active','disabled')");
                    table.CheckConstraint("ck_vouchers_usage_cap", "usage_limit IS NULL OR used_count <= usage_limit");
                    table.CheckConstraint("ck_vouchers_usage_limit", "usage_limit IS NULL OR usage_limit > 0");
                    table.CheckConstraint("ck_vouchers_used_count", "used_count >= 0");
                });

            migrationBuilder.CreateTable(
                name: "cod_settlements",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    carrier_name = table.Column<string>(type: "text", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: true),
                    settled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    gross_cod_vnd = table.Column<long>(type: "bigint", nullable: false),
                    fee_vnd = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    net_received_vnd = table.Column<long>(type: "bigint", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cod_settlements", x => x.id);
                    table.CheckConstraint("ck_cod_settlements_fee_vnd", "fee_vnd >= 0");
                    table.CheckConstraint("ck_cod_settlements_gross_cod_vnd", "gross_cod_vnd >= 0");
                    table.CheckConstraint("ck_cod_settlements_row", "net_received_vnd = gross_cod_vnd - fee_vnd");
                });

            migrationBuilder.CreateTable(
                name: "expenses",
                schema: "finance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    subcategory = table.Column<string>(type: "text", nullable: true),
                    amount_vnd = table.Column<long>(type: "bigint", nullable: false),
                    incurred_on = table.Column<DateOnly>(type: "date", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expenses", x => x.id);
                    table.CheckConstraint("ck_expenses_amount_vnd", "amount_vnd > 0");
                    table.CheckConstraint("ck_expenses_category", "category IN ('marketing','fixed','packaging','shipping_other','payment_fee','damage_other','other')");
                    table.CheckConstraint("ck_expenses_fixed_first_day", "category <> 'fixed' OR extract(day FROM incurred_on) = 1");
                    table.CheckConstraint("ck_expenses_marketing_channel", "category <> 'marketing' OR channel IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "customer_tags",
                schema: "crm",
                columns: table => new
                {
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_tags", x => new { x.customer_id, x.tag_id });
                    table.ForeignKey(
                        name: "fk_customer_tags_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_customer_tags_tag_id",
                        column: x => x.tag_id,
                        principalSchema: "crm",
                        principalTable: "tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "customer_addresses",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: true),
                    receiver_name = table.Column<string>(type: "text", nullable: false),
                    receiver_phone = table.Column<string>(type: "text", nullable: false),
                    province_code = table.Column<string>(type: "text", nullable: false),
                    ward_code = table.Column<string>(type: "text", nullable: true),
                    ward_name_text = table.Column<string>(type: "text", nullable: true),
                    address_line = table.Column<string>(type: "text", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_addresses", x => x.id);
                    table.CheckConstraint("ck_customer_addresses_receiver_phone", "receiver_phone ~ '^\\+84[0-9]{9}$'");
                    table.CheckConstraint("ck_customer_addresses_row", "ward_code IS NOT NULL OR ward_name_text IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_customer_addresses_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_customer_addresses_province_code",
                        column: x => x.province_code,
                        principalSchema: "platform",
                        principalTable: "provinces",
                        principalColumn: "code");
                    table.ForeignKey(
                        name: "fk_customer_addresses_ward_code",
                        column: x => x.ward_code,
                        principalSchema: "platform",
                        principalTable: "wards",
                        principalColumn: "code");
                });

            migrationBuilder.CreateTable(
                name: "customer_accounts",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_phone = table.Column<string>(type: "text", nullable: true),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    email_verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    phone_verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    history_unlocked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'"),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    lockout_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_accounts", x => x.id);
                    table.UniqueConstraint("ux_customer_accounts_customer_id", x => x.customer_id);
                    table.CheckConstraint("ck_account_login_phone", "status = 'disabled' OR login_phone IS NOT NULL");
                    table.CheckConstraint("ck_customer_accounts_login_phone", "login_phone ~ '^\\+84[0-9]{9}$'");
                    table.CheckConstraint("ck_customer_accounts_status", "status IN ('active','locked','disabled')");
                    table.ForeignKey(
                        name: "fk_customer_accounts_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_logins",
                schema: "crm",
                columns: table => new
                {
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_logins", x => new { x.provider, x.provider_key });
                    table.CheckConstraint("ck_customer_logins_provider", "provider IN ('google','zalo')");
                    table.ForeignKey(
                        name: "fk_customer_logins_account_id",
                        column: x => x.account_id,
                        principalSchema: "crm",
                        principalTable: "customer_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "interactions",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    follow_up_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    follow_up_done_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    follow_up_result = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interactions", x => x.id);
                    table.CheckConstraint("ck_interactions_channel", "channel IN ('zalo_personal','zalo_oa','phone','facebook','in_person','email','web','other')");
                    table.CheckConstraint("ck_interactions_content", "length(content) BETWEEN 1 AND 4000");
                    table.CheckConstraint("ck_interactions_kind", "kind IN ('inquiry','consultation','quote','follow_up','complaint','note','campaign_message')");
                    table.CheckConstraint("ck_interactions_row", "follow_up_done_at IS NULL OR follow_up_at IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_interactions_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "customer_interests",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    interest_type = table.Column<string>(type: "text", nullable: false),
                    ref_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price_band = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "1"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_interests", x => x.id);
                    table.CheckConstraint("ck_customer_interests_interest_type", "interest_type IN ('material','artisan','product_type','category','price_band')");
                    table.CheckConstraint("ck_customer_interests_price_band", "price_band IN ('under_1m','1m_3m','3m_10m','over_10m')");
                    table.CheckConstraint("ck_customer_interests_price_band_match", "(interest_type = 'price_band') = (price_band IS NOT NULL)");
                    table.CheckConstraint("ck_customer_interests_ref_id_match", "(interest_type = 'price_band') = (ref_id IS NULL)");
                    table.CheckConstraint("ck_customer_interests_score", "score > 0");
                    table.CheckConstraint("ck_customer_interests_source", "source IN ('order','manual')");
                    table.ForeignKey(
                        name: "fk_customer_interests_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "consents",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    evidence = table.Column<string>(type: "text", nullable: false),
                    captured_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    captured_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consents", x => x.id);
                    table.UniqueConstraint("ux_consents_customer_id_channel_purpose", x => new { x.customer_id, x.channel, x.purpose });
                    table.CheckConstraint("ck_consents_channel", "channel IN ('zalo_personal','zalo_oa','sms','email','phone')");
                    table.CheckConstraint("ck_consents_evidence", "length(trim(evidence)) >= 3");
                    table.CheckConstraint("ck_consents_purpose", "purpose IN ('marketing_new_products','marketing_promotions','customer_care')");
                    table.CheckConstraint("ck_consents_source", "source IN ('checkout','account','staff','import','link')");
                    table.CheckConstraint("ck_consents_status", "status IN ('granted','revoked')");
                    table.ForeignKey(
                        name: "fk_consents_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "data_requests",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'open'"),
                    channel = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    handled_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_requests", x => x.id);
                    table.CheckConstraint("ck_data_requests_request_type", "request_type IN ('export','erase','correct','withdraw_consent')");
                    table.CheckConstraint("ck_data_requests_status", "status IN ('open','completed','rejected')");
                    table.ForeignKey(
                        name: "fk_data_requests_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "carts",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    customer_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_carts", x => x.id);
                    table.UniqueConstraint("ux_carts_token_hash", x => x.token_hash);
                });

            migrationBuilder.CreateTable(
                name: "cart_items",
                schema: "sales",
                columns: table => new
                {
                    cart_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    added_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cart_items", x => new { x.cart_id, x.variant_id });
                    table.CheckConstraint("ck_cart_items_quantity", "quantity BETWEEN 1 AND 99");
                    table.ForeignKey(
                        name: "fk_cart_items_cart_id",
                        column: x => x.cart_id,
                        principalSchema: "sales",
                        principalTable: "carts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_cart_items_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    sales_channel = table.Column<string>(type: "text", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    payment_method = table.Column<string>(type: "text", nullable: false),
                    payment_status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'unpaid'"),
                    payment_due_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    contact_name = table.Column<string>(type: "text", nullable: false),
                    contact_phone = table.Column<string>(type: "text", nullable: false),
                    contact_email = table.Column<string>(type: "text", nullable: true),
                    receiver_name = table.Column<string>(type: "text", nullable: false),
                    receiver_phone = table.Column<string>(type: "text", nullable: false),
                    province_code = table.Column<string>(type: "text", nullable: false),
                    province_name = table.Column<string>(type: "text", nullable: false),
                    ward_code = table.Column<string>(type: "text", nullable: true),
                    ward_name = table.Column<string>(type: "text", nullable: false),
                    address_line = table.Column<string>(type: "text", nullable: false),
                    subtotal_vnd = table.Column<long>(type: "bigint", nullable: false),
                    discount_vnd = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    shipping_fee_vnd = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    total_vnd = table.Column<long>(type: "bigint", nullable: false),
                    discount_note = table.Column<string>(type: "text", nullable: true),
                    voucher_id = table.Column<Guid>(type: "uuid", nullable: true),
                    referral_code = table.Column<string>(type: "text", nullable: true),
                    acquisition_source = table.Column<string>(type: "text", nullable: false),
                    is_first_purchase = table.Column<bool>(type: "boolean", nullable: false),
                    tracking_link_id = table.Column<Guid>(type: "uuid", nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    utm = table.Column<string>(type: "jsonb", nullable: true),
                    customer_note = table.Column<string>(type: "text", nullable: true),
                    internal_note = table.Column<string>(type: "text", nullable: true),
                    status_reason = table.Column<string>(type: "text", nullable: true),
                    placed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    shipped_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    invoice_number = table.Column<string>(type: "text", nullable: true),
                    invoice_issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_backfilled = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false"),
                    created_by_staff_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.UniqueConstraint("ux_orders_code", x => x.code);
                    table.CheckConstraint("ck_order_cancelled", "(status = 'cancelled') = (cancelled_at IS NOT NULL)");
                    table.CheckConstraint("ck_order_delivered", "(status = 'delivered') = (delivered_at IS NOT NULL)");
                    table.CheckConstraint("ck_order_discount", "discount_vnd <= subtotal_vnd");
                    table.CheckConstraint("ck_order_due", "payment_method <> 'bank_transfer' OR origin <> 'web' OR payment_due_at IS NOT NULL");
                    table.CheckConstraint("ck_order_total", "total_vnd = subtotal_vnd - discount_vnd + shipping_fee_vnd");
                    table.CheckConstraint("ck_order_web_channel", "origin <> 'web' OR sales_channel = 'web'");
                    table.CheckConstraint("ck_orders_code", "code ~ '^TP[0-9]{6}[A-Z0-9]{4}$'");
                    table.CheckConstraint("ck_orders_contact_phone", "contact_phone ~ '^\\+84[0-9]{9}$'");
                    table.CheckConstraint("ck_orders_discount_vnd", "discount_vnd >= 0");
                    table.CheckConstraint("ck_orders_origin", "origin IN ('web','admin')");
                    table.CheckConstraint("ck_orders_payment_method", "payment_method IN ('cod','bank_transfer')");
                    table.CheckConstraint("ck_orders_payment_status", "payment_status IN ('unpaid','partially_paid','paid','partially_refunded','refunded')");
                    table.CheckConstraint("ck_orders_receiver_phone", "receiver_phone ~ '^\\+84[0-9]{9}$'");
                    table.CheckConstraint("ck_orders_sales_channel", "sales_channel IN ('web','zalo_personal','zalo_oa','facebook','phone','in_person','other')");
                    table.CheckConstraint("ck_orders_shipping_fee_vnd", "shipping_fee_vnd >= 0");
                    table.CheckConstraint("ck_orders_status", "status IN ('pending','confirmed','shipping','delivered','delivery_failed','cancelled')");
                    table.CheckConstraint("ck_orders_subtotal_vnd", "subtotal_vnd >= 0");
                    table.CheckConstraint("ck_orders_total_vnd", "total_vnd >= 0");
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    product_name = table.Column<string>(type: "text", nullable: false),
                    variant_name = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    list_price_vnd = table.Column<long>(type: "bigint", nullable: false),
                    unit_price_vnd = table.Column<long>(type: "bigint", nullable: false),
                    price_source = table.Column<string>(type: "text", nullable: false),
                    price_override_reason = table.Column<string>(type: "text", nullable: true),
                    line_total_vnd = table.Column<long>(type: "bigint", nullable: false),
                    unit_cost_vnd = table.Column<long>(type: "bigint", nullable: true),
                    returned_quantity = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0"),
                    review_requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_lines", x => x.id);
                    table.UniqueConstraint("ux_order_lines_order_id_variant_id", x => new { x.order_id, x.variant_id });
                    table.CheckConstraint("ck_line_override", "price_source <> 'override' OR length(trim(coalesce(price_override_reason,''))) >= 3");
                    table.CheckConstraint("ck_line_returned", "returned_quantity BETWEEN 0 AND quantity");
                    table.CheckConstraint("ck_line_total", "line_total_vnd = quantity * unit_price_vnd");
                    table.CheckConstraint("ck_order_lines_list_price_vnd", "list_price_vnd >= 0");
                    table.CheckConstraint("ck_order_lines_price_source", "price_source IN ('list','sale','override')");
                    table.CheckConstraint("ck_order_lines_quantity", "quantity BETWEEN 1 AND 1000");
                    table.CheckConstraint("ck_order_lines_unit_cost_vnd", "unit_cost_vnd IS NULL OR unit_cost_vnd >= 0");
                    table.CheckConstraint("ck_order_lines_unit_price_vnd", "unit_price_vnd >= 0");
                    table.ForeignKey(
                        name: "fk_order_lines_order_id",
                        column: x => x.order_id,
                        principalSchema: "sales",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_order_lines_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_order_lines_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "variants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "order_status_history",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: false),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_status_history", x => x.id);
                    table.CheckConstraint("ck_order_status_history_actor_type", "actor_type IN ('staff','customer','system')");
                    table.ForeignKey(
                        name: "fk_order_status_history_order_id",
                        column: x => x.order_id,
                        principalSchema: "sales",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'manual'"),
                    amount_vnd = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    bank_reference = table.Column<string>(type: "text", nullable: true),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cod_settlement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    void_reason = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    confirmed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount_vnd", "amount_vnd > 0");
                    table.CheckConstraint("ck_payments_awaiting_cod", "status <> 'awaiting_settlement' OR method = 'cod'");
                    table.CheckConstraint("ck_payments_confirmed", "(status = 'confirmed') = (confirmed_at IS NOT NULL)");
                    table.CheckConstraint("ck_payments_method", "method IN ('bank_transfer','cod','cash')");
                    table.CheckConstraint("ck_payments_provider", "provider IN ('manual','sepay')");
                    table.CheckConstraint("ck_payments_status", "status IN ('awaiting_settlement','confirmed','voided')");
                    table.CheckConstraint("ck_payments_void_reason", "status <> 'voided' OR void_reason IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_payments_cod_settlement_id",
                        column: x => x.cod_settlement_id,
                        principalSchema: "sales",
                        principalTable: "cod_settlements",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_payments_order_id",
                        column: x => x.order_id,
                        principalSchema: "sales",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "return_requests",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'requested'"),
                    resolution = table.Column<string>(type: "text", nullable: true),
                    decision_note = table.Column<string>(type: "text", nullable: true),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_return_requests", x => x.id);
                    table.UniqueConstraint("ux_return_requests_code", x => x.code);
                    table.CheckConstraint("ck_return_requests_reason", "reason IN ('damaged_in_transit','defective','wrong_item')");
                    table.CheckConstraint("ck_return_requests_resolution", "resolution IN ('refund','replacement')");
                    table.CheckConstraint("ck_return_requests_row", "status NOT IN ('approved','resolved') OR resolution IS NOT NULL");
                    table.CheckConstraint("ck_return_requests_status", "status IN ('requested','approved','rejected','resolved')");
                    table.ForeignKey(
                        name: "fk_return_requests_order_id",
                        column: x => x.order_id,
                        principalSchema: "sales",
                        principalTable: "orders",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "return_lines",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    return_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    restockable = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_return_lines", x => x.id);
                    table.UniqueConstraint("ux_return_lines_return_request_id_order_line_id", x => new { x.return_request_id, x.order_line_id });
                    table.CheckConstraint("ck_return_lines_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_return_lines_order_line_id",
                        column: x => x.order_line_id,
                        principalSchema: "sales",
                        principalTable: "order_lines",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_return_lines_return_request_id",
                        column: x => x.return_request_id,
                        principalSchema: "sales",
                        principalTable: "return_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "return_evidence",
                schema: "sales",
                columns: table => new
                {
                    return_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_return_evidence", x => new { x.return_request_id, x.asset_id });
                    table.ForeignKey(
                        name: "fk_return_evidence_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "assets",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_return_evidence_return_request_id",
                        column: x => x.return_request_id,
                        principalSchema: "sales",
                        principalTable: "return_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    return_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount_vnd = table.Column<long>(type: "bigint", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    bank_reference = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    refunded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("ck_refunds_amount_vnd", "amount_vnd > 0");
                    table.CheckConstraint("ck_refunds_method", "method IN ('bank_transfer','cash')");
                    table.ForeignKey(
                        name: "fk_refunds_order_id",
                        column: x => x.order_id,
                        principalSchema: "sales",
                        principalTable: "orders",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_refunds_return_request_id",
                        column: x => x.return_request_id,
                        principalSchema: "sales",
                        principalTable: "return_requests",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "shipments",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'order'"),
                    return_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'manual'"),
                    carrier_name = table.Column<string>(type: "text", nullable: false),
                    tracking_code = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'created'"),
                    cod_amount_vnd = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "0"),
                    shipping_cost_vnd = table.Column<long>(type: "bigint", nullable: true),
                    return_cost_vnd = table.Column<long>(type: "bigint", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    picked_up_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    returned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipments", x => x.id);
                    table.CheckConstraint("ck_shipments_cod_amount_vnd", "cod_amount_vnd >= 0");
                    table.CheckConstraint("ck_shipments_provider", "provider IN ('manual','ghn','ghtk')");
                    table.CheckConstraint("ck_shipments_purpose", "purpose IN ('order','replacement')");
                    table.CheckConstraint("ck_shipments_return_cost_vnd", "return_cost_vnd IS NULL OR return_cost_vnd >= 0");
                    table.CheckConstraint("ck_shipments_row", "purpose <> 'replacement' OR return_request_id IS NOT NULL");
                    table.CheckConstraint("ck_shipments_shipping_cost_vnd", "shipping_cost_vnd IS NULL OR shipping_cost_vnd >= 0");
                    table.CheckConstraint("ck_shipments_status", "status IN ('created','picked_up','in_transit','delivered','failed','returned','cancelled')");
                    table.ForeignKey(
                        name: "fk_shipments_order_id",
                        column: x => x.order_id,
                        principalSchema: "sales",
                        principalTable: "orders",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_shipments_return_request_id",
                        column: x => x.return_request_id,
                        principalSchema: "sales",
                        principalTable: "return_requests",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "shipment_events",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipment_events", x => x.id);
                    table.CheckConstraint("ck_shipment_events_source", "source IN ('manual','webhook')");
                    table.ForeignKey(
                        name: "fk_shipment_events_shipment_id",
                        column: x => x.shipment_id,
                        principalSchema: "sales",
                        principalTable: "shipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "voucher_redemptions",
                schema: "pricing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    voucher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_vnd = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'applied'"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voucher_redemptions", x => x.id);
                    table.UniqueConstraint("ux_voucher_redemptions_order_id", x => x.order_id);
                    table.CheckConstraint("ck_voucher_redemptions_amount_vnd", "amount_vnd > 0");
                    table.CheckConstraint("ck_voucher_redemptions_status", "status IN ('applied','reverted')");
                    table.ForeignKey(
                        name: "fk_voucher_redemptions_voucher_id",
                        column: x => x.voucher_id,
                        principalSchema: "pricing",
                        principalTable: "vouchers",
                        principalColumn: "id");
                });

            migrationBuilder.Sql("""
ALTER TABLE crm.customer_accounts ADD CONSTRAINT ux_customer_accounts_login_phone UNIQUE (login_phone);
CREATE UNIQUE INDEX ux_account_email ON crm.customer_accounts (lower(email)) WHERE email IS NOT NULL;
CREATE UNIQUE INDEX ux_customer_interest ON crm.customer_interests (customer_id, interest_type, coalesce(ref_id, '00000000-0000-0000-0000-000000000000'::uuid), coalesce(price_band, ''), source);
""");

            migrationBuilder.CreateIndex(
                name: "ix_cart_expires",
                schema: "sales",
                table: "carts",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ux_cart_account",
                schema: "sales",
                table: "carts",
                column: "customer_account_id",
                unique: true,
                filter: "customer_account_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_customer_default_address",
                schema: "crm",
                table: "customer_addresses",
                column: "customer_id",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_interest_lookup",
                schema: "crm",
                table: "customer_interests",
                columns: new[] { "interest_type", "ref_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_tags_tag",
                schema: "crm",
                table: "customer_tags",
                column: "tag_id");

            migrationBuilder.CreateIndex(
                name: "ix_customers_group",
                schema: "crm",
                table: "customers",
                column: "customer_group");

            migrationBuilder.CreateIndex(
                name: "ix_customers_last_delivered",
                schema: "crm",
                table: "customers",
                column: "last_delivered_at");

            migrationBuilder.CreateIndex(
                name: "ix_customers_search",
                schema: "crm",
                table: "customers",
                column: "search_text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ux_customers_phone",
                schema: "crm",
                table: "customers",
                column: "phone_e164",
                unique: true,
                filter: "phone_e164 IS NOT NULL AND merged_into_customer_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_expenses_date",
                schema: "finance",
                table: "expenses",
                columns: new[] { "incurred_on", "category" });

            migrationBuilder.CreateIndex(
                name: "ix_interactions_customer",
                schema: "crm",
                table: "interactions",
                columns: new[] { "customer_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_interactions_follow_up",
                schema: "crm",
                table: "interactions",
                column: "follow_up_at",
                filter: "follow_up_at IS NOT NULL AND follow_up_done_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_product",
                schema: "sales",
                table: "order_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_variant",
                schema: "sales",
                table: "order_lines",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_history",
                schema: "sales",
                table: "order_status_history",
                columns: new[] { "order_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_campaign",
                schema: "sales",
                table: "orders",
                column: "campaign_id",
                filter: "campaign_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_orders_contact_phone",
                schema: "sales",
                table: "orders",
                column: "contact_phone");

            migrationBuilder.CreateIndex(
                name: "ix_orders_customer",
                schema: "sales",
                table: "orders",
                columns: new[] { "customer_id", "placed_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_orders_delivered",
                schema: "sales",
                table: "orders",
                column: "delivered_at",
                filter: "delivered_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_orders_due",
                schema: "sales",
                table: "orders",
                column: "payment_due_at",
                filter: "status = 'pending' AND payment_method = 'bank_transfer' AND payment_status = 'unpaid'");

            migrationBuilder.CreateIndex(
                name: "ix_orders_payment",
                schema: "sales",
                table: "orders",
                columns: new[] { "payment_status", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_status_placed",
                schema: "sales",
                table: "orders",
                columns: new[] { "status", "placed_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_payments_awaiting",
                schema: "sales",
                table: "payments",
                column: "created_at",
                filter: "status = 'awaiting_settlement'");

            migrationBuilder.CreateIndex(
                name: "ix_payments_order",
                schema: "sales",
                table: "payments",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ux_payments_bank_ref",
                schema: "sales",
                table: "payments",
                columns: new[] { "provider", "bank_reference" },
                unique: true,
                filter: "bank_reference IS NOT NULL AND status <> 'voided'");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_order",
                schema: "sales",
                table: "refunds",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ux_shipment_active_order",
                schema: "sales",
                table: "shipments",
                column: "order_id",
                unique: true,
                filter: "purpose = 'order' AND status NOT IN ('cancelled','failed','returned')");

            migrationBuilder.CreateIndex(
                name: "ux_shipment_tracking",
                schema: "sales",
                table: "shipments",
                columns: new[] { "carrier_name", "tracking_code" },
                unique: true,
                filter: "tracking_code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_redemptions_voucher_customer",
                schema: "pricing",
                table: "voucher_redemptions",
                columns: new[] { "voucher_id", "customer_id" },
                filter: "status = 'applied'");

            migrationBuilder.AddForeignKey(
                name: "fk_vouchers_customer",
                schema: "pricing",
                table: "vouchers",
                column: "bound_customer_id",
                principalSchema: "crm",
                principalTable: "customers",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_carts_customer_account_id",
                schema: "sales",
                table: "carts",
                column: "customer_account_id",
                principalSchema: "crm",
                principalTable: "customer_accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_orders_customer_id",
                schema: "sales",
                table: "orders",
                column: "customer_id",
                principalSchema: "crm",
                principalTable: "customers",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_orders_customer_account_id",
                schema: "sales",
                table: "orders",
                column: "customer_account_id",
                principalSchema: "crm",
                principalTable: "customer_accounts",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_orders_voucher_id",
                schema: "sales",
                table: "orders",
                column: "voucher_id",
                principalSchema: "pricing",
                principalTable: "vouchers",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_interactions_order",
                schema: "crm",
                table: "interactions",
                column: "order_id",
                principalSchema: "sales",
                principalTable: "orders",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_redemptions_order",
                schema: "pricing",
                table: "voucher_redemptions",
                column: "order_id",
                principalSchema: "sales",
                principalTable: "orders",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_redemptions_customer",
                schema: "pricing",
                table: "voucher_redemptions",
                column: "customer_id",
                principalSchema: "crm",
                principalTable: "customers",
                principalColumn: "id");

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
    WHERE n.nspname IN ('platform','iam','public','media','catalog','inventory','pricing','content','crm','sales','finance') AND a.grantee=0) THEN
    RAISE EXCEPTION 'Unexpected PUBLIC object privileges';
  END IF;
  IF EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    CROSS JOIN LATERAL aclexplode(c.relacl) a JOIN pg_roles gr ON gr.oid=a.grantee
    WHERE n.nspname IN ('platform','iam','public','media','catalog','inventory','pricing','content','crm','sales','finance') AND
      ((gr.rolname='tp_readonly' AND (a.privilege_type <> 'SELECT' OR a.is_grantable)) OR
       (gr.rolname='tp_app' AND (a.is_grantable OR a.privilege_type IN ('TRUNCATE','TRIGGER','REFERENCES','MAINTAIN')
         OR (c.relkind='S' AND a.privilege_type='UPDATE')
         OR ((n.nspname,c.relname) IN (('sales','orders'),('sales','payments'),('sales','refunds'),('sales','order_status_history'),('crm','customers')) AND a.privilege_type='DELETE')
         OR ((n.nspname,c.relname) IN (('platform','audit_logs'),('platform','administrative_data_imports'),('inventory','stock_movements')) AND a.privilege_type IN ('UPDATE','DELETE')))))) THEN
    RAISE EXCEPTION 'Unexpected runtime/backup object privileges';
  END IF;
  IF EXISTS (SELECT FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid
    JOIN pg_namespace n ON n.oid=c.relnamespace CROSS JOIN LATERAL aclexplode(a.attacl) acl
    WHERE n.nspname IN ('platform','iam','public','media','catalog','inventory','pricing','content','crm','sales','finance') AND
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
REVOKE ALL ON SCHEMA crm,sales,finance FROM PUBLIC,tp_app,tp_readonly;
GRANT USAGE ON SCHEMA crm,sales,finance TO tp_app,tp_readonly;
REVOKE ALL ON crm.customers,crm.tags,crm.customer_tags,crm.customer_addresses,crm.customer_accounts,crm.customer_logins,crm.interactions,crm.customer_interests,crm.consents,crm.data_requests,pricing.vouchers,pricing.voucher_redemptions,sales.carts,sales.cart_items,sales.orders,sales.order_lines,sales.order_status_history,sales.cod_settlements,sales.payments,sales.return_requests,sales.return_lines,sales.return_evidence,sales.refunds,sales.shipments,sales.shipment_events,finance.expenses FROM PUBLIC,tp_app,tp_readonly;
GRANT SELECT,INSERT,UPDATE,DELETE ON crm.tags,crm.customer_tags,crm.customer_addresses,crm.customer_accounts,crm.customer_logins,crm.interactions,crm.customer_interests,crm.consents,crm.data_requests,pricing.vouchers,pricing.voucher_redemptions,sales.carts,sales.cart_items,sales.order_lines,sales.cod_settlements,sales.return_requests,sales.return_lines,sales.return_evidence,sales.shipments,sales.shipment_events,finance.expenses TO tp_app;
GRANT SELECT,INSERT,UPDATE ON crm.customers,sales.orders,sales.order_status_history,sales.payments,sales.refunds TO tp_app;
REVOKE DELETE,TRUNCATE,MAINTAIN ON crm.customers,sales.orders,sales.order_status_history,sales.payments,sales.refunds FROM tp_app;
GRANT SELECT ON crm.customers,crm.tags,crm.customer_tags,crm.customer_addresses,crm.customer_accounts,crm.customer_logins,crm.interactions,crm.customer_interests,crm.consents,crm.data_requests,pricing.vouchers,pricing.voucher_redemptions,sales.carts,sales.cart_items,sales.orders,sales.order_lines,sales.order_status_history,sales.cod_settlements,sales.payments,sales.return_requests,sales.return_lines,sales.return_evidence,sales.refunds,sales.shipments,sales.shipment_events,finance.expenses TO tp_readonly;
REVOKE ALL ON SEQUENCE crm.customer_code_seq FROM PUBLIC,tp_app,tp_readonly;
GRANT USAGE ON SEQUENCE crm.customer_code_seq TO tp_app;
GRANT SELECT ON SEQUENCE crm.customer_code_seq TO tp_readonly;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA crm,sales,finance GRANT SELECT ON TABLES TO tp_readonly;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA crm,sales,finance GRANT USAGE ON SEQUENCES TO tp_app;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA crm,sales,finance GRANT SELECT ON SEQUENCES TO tp_readonly;
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
    WHERE n.nspname IN ('platform','iam','public','media','catalog','inventory','pricing','content','crm','sales','finance') AND a.grantee=0) THEN
    RAISE EXCEPTION 'Unexpected PUBLIC object privileges';
  END IF;
  IF EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    CROSS JOIN LATERAL aclexplode(c.relacl) a JOIN pg_roles gr ON gr.oid=a.grantee
    WHERE n.nspname IN ('platform','iam','public','media','catalog','inventory','pricing','content','crm','sales','finance') AND
      ((gr.rolname='tp_readonly' AND (a.privilege_type <> 'SELECT' OR a.is_grantable)) OR
       (gr.rolname='tp_app' AND (a.is_grantable OR a.privilege_type IN ('TRUNCATE','TRIGGER','REFERENCES','MAINTAIN')
         OR (c.relkind='S' AND a.privilege_type='UPDATE')
         OR ((n.nspname,c.relname) IN (('sales','orders'),('sales','payments'),('sales','refunds'),('sales','order_status_history'),('crm','customers')) AND a.privilege_type='DELETE')
         OR ((n.nspname,c.relname) IN (('platform','audit_logs'),('platform','administrative_data_imports'),('inventory','stock_movements')) AND a.privilege_type IN ('UPDATE','DELETE')))))) THEN
    RAISE EXCEPTION 'Unexpected runtime/backup object privileges';
  END IF;
  IF EXISTS (SELECT FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid
    JOIN pg_namespace n ON n.oid=c.relnamespace CROSS JOIN LATERAL aclexplode(a.attacl) acl
    WHERE n.nspname IN ('platform','iam','public','media','catalog','inventory','pricing','content','crm','sales','finance') AND
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
-- Disposable DB only, no active writers. Lock the full owned set before checking data.
DO $p201_down$
DECLARE target text; occupied boolean;
  owned text[] := ARRAY['crm.customers','crm.tags','crm.customer_tags','crm.customer_addresses','crm.customer_accounts','crm.customer_logins','crm.interactions','crm.customer_interests','crm.consents','crm.data_requests','pricing.vouchers','pricing.voucher_redemptions','sales.carts','sales.cart_items','sales.orders','sales.order_lines','sales.order_status_history','sales.cod_settlements','sales.payments','sales.return_requests','sales.return_lines','sales.return_evidence','sales.refunds','sales.shipments','sales.shipment_events','finance.expenses'];
  relations text[] := ARRAY['crm.customer_code_seq','crm.customers','crm.pk_customers','crm.ux_customers_code','crm.ux_customers_phone','crm.ix_customers_search','crm.ix_customers_last_delivered','crm.ix_customers_group','crm.tags','crm.pk_tags','crm.ux_tags_name','crm.customer_tags','crm.pk_customer_tags','crm.ix_customer_tags_tag','crm.customer_addresses','crm.pk_customer_addresses','crm.ux_customer_default_address','crm.customer_accounts','crm.pk_customer_accounts','crm.ux_customer_accounts_customer_id','crm.ux_customer_accounts_login_phone','crm.ux_account_email','crm.customer_logins','crm.pk_customer_logins','crm.interactions','crm.pk_interactions','crm.ix_interactions_customer','crm.ix_interactions_follow_up','crm.customer_interests','crm.pk_customer_interests','crm.ux_customer_interest','crm.ix_interest_lookup','crm.consents','crm.pk_consents','crm.ux_consents_customer_id_channel_purpose','crm.data_requests','crm.pk_data_requests','pricing.vouchers','pricing.pk_vouchers','pricing.ux_vouchers_code','pricing.voucher_redemptions','pricing.pk_voucher_redemptions','pricing.ux_voucher_redemptions_order_id','pricing.ix_redemptions_voucher_customer','sales.carts','sales.pk_carts','sales.ux_carts_token_hash','sales.ux_cart_account','sales.ix_cart_expires','sales.cart_items','sales.pk_cart_items','sales.orders','sales.pk_orders','sales.ux_orders_code','sales.ix_orders_status_placed','sales.ix_orders_customer','sales.ix_orders_delivered','sales.ix_orders_payment','sales.ix_orders_due','sales.ix_orders_campaign','sales.ix_orders_contact_phone','sales.order_lines','sales.pk_order_lines','sales.ux_order_lines_order_id_variant_id','sales.ix_order_lines_variant','sales.ix_order_lines_product','sales.order_status_history','sales.pk_order_status_history','sales.ix_order_history','sales.cod_settlements','sales.pk_cod_settlements','sales.payments','sales.pk_payments','sales.ix_payments_order','sales.ix_payments_awaiting','sales.ux_payments_bank_ref','sales.return_requests','sales.pk_return_requests','sales.ux_return_requests_code','sales.return_lines','sales.pk_return_lines','sales.ux_return_lines_return_request_id_order_line_id','sales.return_evidence','sales.pk_return_evidence','sales.refunds','sales.pk_refunds','sales.ix_refunds_order','sales.shipments','sales.pk_shipments','sales.ux_shipment_active_order','sales.ux_shipment_tracking','sales.shipment_events','sales.pk_shipment_events','finance.expenses','finance.pk_expenses','finance.ix_expenses_date'];
  p1_pricing_relations text[] := ARRAY['pricing.sale_prices','pricing.pk_sale_prices','pricing.ex_sale_prices_no_overlap'];
  constraints text[] := ARRAY['crm.customers:pk_customers','crm.customers:ux_customers_code','crm.customers:ck_customer_contact','crm.customers:ck_customer_not_self_ref','crm.customers:ck_customers_acquisition_source','crm.customers:ck_customers_contact_status','crm.customers:ck_customers_customer_group','crm.customers:ck_customers_delivered_orders_count','crm.customers:ck_customers_email','crm.customers:ck_customers_full_name','crm.customers:ck_customers_lifetime_value_vnd','crm.customers:ck_customers_origin','crm.customers:ck_customers_phone_e164','crm.customers:fk_customers_merged_into_customer_id','crm.customers:fk_customers_referred_by_customer_id','crm.tags:pk_tags','crm.tags:ux_tags_name','crm.customer_tags:pk_customer_tags','crm.customer_tags:fk_customer_tags_customer_id','crm.customer_tags:fk_customer_tags_tag_id','crm.customer_addresses:pk_customer_addresses','crm.customer_addresses:ck_customer_addresses_receiver_phone','crm.customer_addresses:ck_customer_addresses_row','crm.customer_addresses:fk_customer_addresses_customer_id','crm.customer_addresses:fk_customer_addresses_province_code','crm.customer_addresses:fk_customer_addresses_ward_code','crm.customer_accounts:pk_customer_accounts','crm.customer_accounts:ux_customer_accounts_customer_id','crm.customer_accounts:ux_customer_accounts_login_phone','crm.customer_accounts:ck_account_login_phone','crm.customer_accounts:ck_customer_accounts_login_phone','crm.customer_accounts:ck_customer_accounts_status','crm.customer_accounts:fk_customer_accounts_customer_id','crm.customer_logins:pk_customer_logins','crm.customer_logins:ck_customer_logins_provider','crm.customer_logins:fk_customer_logins_account_id','crm.interactions:pk_interactions','crm.interactions:ck_interactions_channel','crm.interactions:ck_interactions_content','crm.interactions:ck_interactions_kind','crm.interactions:ck_interactions_row','crm.interactions:fk_interactions_customer_id','crm.interactions:fk_interactions_order','crm.customer_interests:pk_customer_interests','crm.customer_interests:ck_customer_interests_interest_type','crm.customer_interests:ck_customer_interests_price_band','crm.customer_interests:ck_customer_interests_price_band_match','crm.customer_interests:ck_customer_interests_ref_id_match','crm.customer_interests:ck_customer_interests_score','crm.customer_interests:ck_customer_interests_source','crm.customer_interests:fk_customer_interests_customer_id','crm.consents:pk_consents','crm.consents:ux_consents_customer_id_channel_purpose','crm.consents:ck_consents_channel','crm.consents:ck_consents_evidence','crm.consents:ck_consents_purpose','crm.consents:ck_consents_source','crm.consents:ck_consents_status','crm.consents:fk_consents_customer_id','crm.data_requests:pk_data_requests','crm.data_requests:ck_data_requests_request_type','crm.data_requests:ck_data_requests_status','crm.data_requests:fk_data_requests_customer_id','pricing.vouchers:pk_vouchers','pricing.vouchers:ux_vouchers_code','pricing.vouchers:ck_vouchers_code','pricing.vouchers:ck_vouchers_dates','pricing.vouchers:ck_vouchers_discount_type','pricing.vouchers:ck_vouchers_discount_value','pricing.vouchers:ck_vouchers_kind','pricing.vouchers:ck_vouchers_max_discount_vnd','pricing.vouchers:ck_vouchers_min_order_vnd','pricing.vouchers:ck_vouchers_per_customer_limit','pricing.vouchers:ck_vouchers_percent_bounds','pricing.vouchers:ck_vouchers_status','pricing.vouchers:ck_vouchers_usage_cap','pricing.vouchers:ck_vouchers_usage_limit','pricing.vouchers:ck_vouchers_used_count','pricing.vouchers:fk_vouchers_customer','pricing.voucher_redemptions:pk_voucher_redemptions','pricing.voucher_redemptions:ux_voucher_redemptions_order_id','pricing.voucher_redemptions:ck_voucher_redemptions_amount_vnd','pricing.voucher_redemptions:ck_voucher_redemptions_status','pricing.voucher_redemptions:fk_voucher_redemptions_voucher_id','pricing.voucher_redemptions:fk_redemptions_order','pricing.voucher_redemptions:fk_redemptions_customer','sales.carts:pk_carts','sales.carts:ux_carts_token_hash','sales.carts:fk_carts_customer_account_id','sales.cart_items:pk_cart_items','sales.cart_items:ck_cart_items_quantity','sales.cart_items:fk_cart_items_cart_id','sales.cart_items:fk_cart_items_variant_id','sales.orders:pk_orders','sales.orders:ux_orders_code','sales.orders:ck_order_cancelled','sales.orders:ck_order_delivered','sales.orders:ck_order_discount','sales.orders:ck_order_due','sales.orders:ck_order_total','sales.orders:ck_order_web_channel','sales.orders:ck_orders_code','sales.orders:ck_orders_contact_phone','sales.orders:ck_orders_discount_vnd','sales.orders:ck_orders_origin','sales.orders:ck_orders_payment_method','sales.orders:ck_orders_payment_status','sales.orders:ck_orders_receiver_phone','sales.orders:ck_orders_sales_channel','sales.orders:ck_orders_shipping_fee_vnd','sales.orders:ck_orders_status','sales.orders:ck_orders_subtotal_vnd','sales.orders:ck_orders_total_vnd','sales.orders:fk_orders_customer_account_id','sales.orders:fk_orders_customer_id','sales.orders:fk_orders_voucher_id','sales.order_lines:pk_order_lines','sales.order_lines:ux_order_lines_order_id_variant_id','sales.order_lines:ck_line_override','sales.order_lines:ck_line_returned','sales.order_lines:ck_line_total','sales.order_lines:ck_order_lines_list_price_vnd','sales.order_lines:ck_order_lines_price_source','sales.order_lines:ck_order_lines_quantity','sales.order_lines:ck_order_lines_unit_cost_vnd','sales.order_lines:ck_order_lines_unit_price_vnd','sales.order_lines:fk_order_lines_order_id','sales.order_lines:fk_order_lines_product_id','sales.order_lines:fk_order_lines_variant_id','sales.order_status_history:pk_order_status_history','sales.order_status_history:ck_order_status_history_actor_type','sales.order_status_history:fk_order_status_history_order_id','sales.cod_settlements:pk_cod_settlements','sales.cod_settlements:ck_cod_settlements_fee_vnd','sales.cod_settlements:ck_cod_settlements_gross_cod_vnd','sales.cod_settlements:ck_cod_settlements_row','sales.payments:pk_payments','sales.payments:ck_payments_amount_vnd','sales.payments:ck_payments_awaiting_cod','sales.payments:ck_payments_confirmed','sales.payments:ck_payments_method','sales.payments:ck_payments_provider','sales.payments:ck_payments_status','sales.payments:ck_payments_void_reason','sales.payments:fk_payments_cod_settlement_id','sales.payments:fk_payments_order_id','sales.return_requests:pk_return_requests','sales.return_requests:ux_return_requests_code','sales.return_requests:ck_return_requests_reason','sales.return_requests:ck_return_requests_resolution','sales.return_requests:ck_return_requests_row','sales.return_requests:ck_return_requests_status','sales.return_requests:fk_return_requests_order_id','sales.return_lines:pk_return_lines','sales.return_lines:ux_return_lines_return_request_id_order_line_id','sales.return_lines:ck_return_lines_quantity','sales.return_lines:fk_return_lines_order_line_id','sales.return_lines:fk_return_lines_return_request_id','sales.return_evidence:pk_return_evidence','sales.return_evidence:fk_return_evidence_asset_id','sales.return_evidence:fk_return_evidence_return_request_id','sales.refunds:pk_refunds','sales.refunds:ck_refunds_amount_vnd','sales.refunds:ck_refunds_method','sales.refunds:fk_refunds_order_id','sales.refunds:fk_refunds_return_request_id','sales.shipments:pk_shipments','sales.shipments:ck_shipments_cod_amount_vnd','sales.shipments:ck_shipments_provider','sales.shipments:ck_shipments_purpose','sales.shipments:ck_shipments_return_cost_vnd','sales.shipments:ck_shipments_row','sales.shipments:ck_shipments_shipping_cost_vnd','sales.shipments:ck_shipments_status','sales.shipments:fk_shipments_order_id','sales.shipments:fk_shipments_return_request_id','sales.shipment_events:pk_shipment_events','sales.shipment_events:ck_shipment_events_source','sales.shipment_events:fk_shipment_events_shipment_id','finance.expenses:pk_expenses','finance.expenses:ck_expenses_amount_vnd','finance.expenses:ck_expenses_category','finance.expenses:ck_expenses_fixed_first_day','finance.expenses:ck_expenses_marketing_channel'];
  columns text[] := ARRAY['crm.customers:id','crm.customers:code','crm.customers:full_name','crm.customers:phone_e164','crm.customers:email','crm.customers:zalo_name','crm.customers:customer_group','crm.customers:is_vip','crm.customers:origin','crm.customers:acquisition_source','crm.customers:referred_by_customer_id','crm.customers:contact_status','crm.customers:note_summary','crm.customers:had_legacy_purchase','crm.customers:first_delivered_at','crm.customers:last_delivered_at','crm.customers:delivered_orders_count','crm.customers:lifetime_value_vnd','crm.customers:last_contacted_at','crm.customers:search_text','crm.customers:merged_into_customer_id','crm.customers:erased_at','crm.customers:created_by','crm.customers:created_at','crm.customers:updated_at','crm.tags:id','crm.tags:name','crm.tags:color','crm.customer_tags:customer_id','crm.customer_tags:tag_id','crm.customer_addresses:id','crm.customer_addresses:customer_id','crm.customer_addresses:label','crm.customer_addresses:receiver_name','crm.customer_addresses:receiver_phone','crm.customer_addresses:province_code','crm.customer_addresses:ward_code','crm.customer_addresses:ward_name_text','crm.customer_addresses:address_line','crm.customer_addresses:is_default','crm.customer_addresses:created_at','crm.customer_addresses:updated_at','crm.customer_accounts:id','crm.customer_accounts:customer_id','crm.customer_accounts:login_phone','crm.customer_accounts:password_hash','crm.customer_accounts:email','crm.customer_accounts:email_verified_at','crm.customer_accounts:phone_verified_at','crm.customer_accounts:history_unlocked_at','crm.customer_accounts:status','crm.customer_accounts:failed_login_count','crm.customer_accounts:lockout_end','crm.customer_accounts:security_stamp','crm.customer_accounts:created_at','crm.customer_accounts:last_login_at','crm.customer_logins:provider','crm.customer_logins:provider_key','crm.customer_logins:account_id','crm.customer_logins:email','crm.customer_logins:created_at','crm.interactions:id','crm.interactions:customer_id','crm.interactions:staff_id','crm.interactions:channel','crm.interactions:kind','crm.interactions:content','crm.interactions:occurred_at','crm.interactions:order_id','crm.interactions:campaign_id','crm.interactions:follow_up_at','crm.interactions:follow_up_done_at','crm.interactions:follow_up_result','crm.interactions:created_at','crm.customer_interests:id','crm.customer_interests:customer_id','crm.customer_interests:interest_type','crm.customer_interests:ref_id','crm.customer_interests:price_band','crm.customer_interests:source','crm.customer_interests:score','crm.customer_interests:updated_at','crm.consents:id','crm.consents:customer_id','crm.consents:channel','crm.consents:purpose','crm.consents:status','crm.consents:source','crm.consents:evidence','crm.consents:captured_at','crm.consents:captured_by','crm.data_requests:id','crm.data_requests:customer_id','crm.data_requests:request_type','crm.data_requests:status','crm.data_requests:channel','crm.data_requests:note','crm.data_requests:requested_at','crm.data_requests:completed_at','crm.data_requests:handled_by','pricing.vouchers:id','pricing.vouchers:code','pricing.vouchers:kind','pricing.vouchers:discount_type','pricing.vouchers:discount_value','pricing.vouchers:max_discount_vnd','pricing.vouchers:min_order_vnd','pricing.vouchers:starts_at','pricing.vouchers:ends_at','pricing.vouchers:usage_limit','pricing.vouchers:per_customer_limit','pricing.vouchers:bound_customer_id','pricing.vouchers:used_count','pricing.vouchers:status','pricing.vouchers:created_by','pricing.vouchers:created_at','pricing.voucher_redemptions:id','pricing.voucher_redemptions:voucher_id','pricing.voucher_redemptions:order_id','pricing.voucher_redemptions:customer_id','pricing.voucher_redemptions:amount_vnd','pricing.voucher_redemptions:status','pricing.voucher_redemptions:created_at','sales.carts:id','sales.carts:token_hash','sales.carts:customer_account_id','sales.carts:created_at','sales.carts:updated_at','sales.carts:expires_at','sales.cart_items:cart_id','sales.cart_items:variant_id','sales.cart_items:quantity','sales.cart_items:added_at','sales.orders:id','sales.orders:code','sales.orders:origin','sales.orders:sales_channel','sales.orders:customer_id','sales.orders:customer_account_id','sales.orders:status','sales.orders:payment_method','sales.orders:payment_status','sales.orders:payment_due_at','sales.orders:contact_name','sales.orders:contact_phone','sales.orders:contact_email','sales.orders:receiver_name','sales.orders:receiver_phone','sales.orders:province_code','sales.orders:province_name','sales.orders:ward_code','sales.orders:ward_name','sales.orders:address_line','sales.orders:subtotal_vnd','sales.orders:discount_vnd','sales.orders:shipping_fee_vnd','sales.orders:total_vnd','sales.orders:discount_note','sales.orders:voucher_id','sales.orders:referral_code','sales.orders:acquisition_source','sales.orders:is_first_purchase','sales.orders:tracking_link_id','sales.orders:campaign_id','sales.orders:utm','sales.orders:customer_note','sales.orders:internal_note','sales.orders:status_reason','sales.orders:placed_at','sales.orders:confirmed_at','sales.orders:shipped_at','sales.orders:delivered_at','sales.orders:failed_at','sales.orders:cancelled_at','sales.orders:invoice_number','sales.orders:invoice_issued_at','sales.orders:is_backfilled','sales.orders:created_by_staff_id','sales.orders:created_at','sales.orders:updated_at','sales.order_lines:id','sales.order_lines:order_id','sales.order_lines:variant_id','sales.order_lines:product_id','sales.order_lines:sku','sales.order_lines:product_name','sales.order_lines:variant_name','sales.order_lines:quantity','sales.order_lines:list_price_vnd','sales.order_lines:unit_price_vnd','sales.order_lines:price_source','sales.order_lines:price_override_reason','sales.order_lines:line_total_vnd','sales.order_lines:unit_cost_vnd','sales.order_lines:returned_quantity','sales.order_lines:review_requested_at','sales.order_status_history:id','sales.order_status_history:order_id','sales.order_status_history:from_status','sales.order_status_history:to_status','sales.order_status_history:actor_type','sales.order_status_history:actor_id','sales.order_status_history:reason','sales.order_status_history:occurred_at','sales.cod_settlements:id','sales.cod_settlements:carrier_name','sales.cod_settlements:reference','sales.cod_settlements:settled_at','sales.cod_settlements:gross_cod_vnd','sales.cod_settlements:fee_vnd','sales.cod_settlements:net_received_vnd','sales.cod_settlements:note','sales.cod_settlements:created_by','sales.cod_settlements:created_at','sales.payments:id','sales.payments:order_id','sales.payments:method','sales.payments:provider','sales.payments:amount_vnd','sales.payments:status','sales.payments:bank_reference','sales.payments:received_at','sales.payments:cod_settlement_id','sales.payments:note','sales.payments:void_reason','sales.payments:created_by','sales.payments:created_at','sales.payments:confirmed_by','sales.payments:confirmed_at','sales.return_requests:id','sales.return_requests:code','sales.return_requests:order_id','sales.return_requests:reason','sales.return_requests:description','sales.return_requests:status','sales.return_requests:resolution','sales.return_requests:decision_note','sales.return_requests:requested_at','sales.return_requests:decided_by','sales.return_requests:decided_at','sales.return_requests:resolved_at','sales.return_requests:created_by','sales.return_lines:id','sales.return_lines:return_request_id','sales.return_lines:order_line_id','sales.return_lines:quantity','sales.return_lines:restockable','sales.return_evidence:return_request_id','sales.return_evidence:asset_id','sales.refunds:id','sales.refunds:order_id','sales.refunds:return_request_id','sales.refunds:amount_vnd','sales.refunds:method','sales.refunds:bank_reference','sales.refunds:reason','sales.refunds:refunded_at','sales.refunds:created_by','sales.refunds:created_at','sales.shipments:id','sales.shipments:order_id','sales.shipments:purpose','sales.shipments:return_request_id','sales.shipments:provider','sales.shipments:carrier_name','sales.shipments:tracking_code','sales.shipments:status','sales.shipments:cod_amount_vnd','sales.shipments:shipping_cost_vnd','sales.shipments:return_cost_vnd','sales.shipments:note','sales.shipments:created_by','sales.shipments:created_at','sales.shipments:picked_up_at','sales.shipments:delivered_at','sales.shipments:failed_at','sales.shipments:returned_at','sales.shipment_events:id','sales.shipment_events:shipment_id','sales.shipment_events:status','sales.shipment_events:note','sales.shipment_events:source','sales.shipment_events:occurred_at','sales.shipment_events:created_by','finance.expenses:id','finance.expenses:category','finance.expenses:subcategory','finance.expenses:amount_vnd','finance.expenses:incurred_on','finance.expenses:channel','finance.expenses:campaign_id','finance.expenses:description','finance.expenses:created_by','finance.expenses:created_at'];
BEGIN
  IF current_user <> 'tp_owner' THEN
    RAISE EXCEPTION 'Migrations require a direct tp_owner login';
  END IF;
  FOREACH target IN ARRAY owned LOOP
    EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE', target);
  END LOOP;
  FOREACH target IN ARRAY owned LOOP
    EXECUTE format('SELECT EXISTS (SELECT FROM %s)', target) INTO occupied;
    IF occupied THEN RAISE EXCEPTION 'P2-01 Down refused: data in %; roll forward or restore an approved backup', target; END IF;
  END LOOP;
  SELECT pg_describe_object(d.classid,d.objid,d.objsubid) INTO target
  FROM pg_depend d JOIN pg_namespace n ON n.oid=d.refobjid
  WHERE d.refclassid='pg_namespace'::regclass
    AND n.nspname IN ('crm','sales','finance','pricing')
    AND NOT (
      d.classid IN ('pg_class'::regclass,'pg_proc'::regclass,'pg_type'::regclass,'pg_constraint'::regclass)
      OR (d.classid='pg_default_acl'::regclass AND d.objid IN
        (SELECT oid FROM pg_default_acl WHERE defaclrole='tp_owner'::regrole
          AND defaclnamespace=n.oid AND defaclobjtype IN ('r','S')))
    )
  LIMIT 1;
  IF FOUND THEN
    RAISE EXCEPTION 'P2-01 Down refused: object/dependency outside migration scope (namespace object: %)', target;
  END IF;
  IF EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
      WHERE (n.nspname IN ('crm','sales','finance') AND NOT n.nspname||'.'||c.relname = ANY(relations))
         OR (n.nspname='pricing' AND NOT n.nspname||'.'||c.relname = ANY(relations || p1_pricing_relations))) THEN
    RAISE EXCEPTION 'P2-01 Down refused: object/dependency outside migration scope (relation)';
  END IF;
  IF EXISTS (SELECT FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname IN ('crm','sales','finance','pricing')) THEN
    RAISE EXCEPTION 'P2-01 Down refused: object/dependency outside migration scope (function)';
  END IF;
  IF EXISTS (SELECT FROM pg_type y JOIN pg_namespace n ON n.oid=y.typnamespace
      WHERE (n.nspname IN ('crm','sales','finance') AND NOT (y.typrelid IN (SELECT unnest(owned)::regclass)
              OR y.typelem IN (SELECT reltype FROM pg_class WHERE oid IN (SELECT unnest(owned)::regclass))))
         OR (n.nspname='pricing' AND NOT (y.typrelid IN (SELECT unnest(owned || ARRAY['pricing.sale_prices'])::regclass)
              OR y.typelem IN (SELECT reltype FROM pg_class WHERE oid IN (SELECT unnest(owned || ARRAY['pricing.sale_prices'])::regclass))))) THEN
    RAISE EXCEPTION 'P2-01 Down refused: object/dependency outside migration scope (type)';
  END IF;
  IF EXISTS (SELECT FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace
      WHERE c.oid IN (SELECT unnest(owned)::regclass) AND a.attnum>0 AND NOT a.attisdropped
        AND NOT n.nspname||'.'||c.relname||':'||a.attname = ANY(columns)) THEN
    RAISE EXCEPTION 'P2-01 Down refused: object/dependency outside migration scope (column)';
  END IF;
  IF EXISTS (SELECT FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace
      WHERE (c.conrelid IN (SELECT unnest(owned)::regclass) OR c.confrelid IN (SELECT unnest(owned)::regclass))
        AND NOT n.nspname||'.'||t.relname||':'||c.conname = ANY(constraints)) THEN
    RAISE EXCEPTION 'P2-01 Down refused: object/dependency outside migration scope (constraint)';
  END IF;
  IF EXISTS (SELECT FROM pg_trigger WHERE tgrelid IN (SELECT unnest(owned)::regclass) AND NOT tgisinternal) THEN
    RAISE EXCEPTION 'P2-01 Down refused: object/dependency outside migration scope (trigger)';
  END IF;
  IF EXISTS (SELECT FROM pg_depend d WHERE d.refclassid='pg_type'::regclass
    AND d.refobjid IN (SELECT y.oid FROM pg_type y WHERE y.typrelid IN (SELECT unnest(owned)::regclass)
      OR y.typelem IN (SELECT reltype FROM pg_class WHERE oid IN (SELECT unnest(owned)::regclass)))
    AND NOT (d.classid='pg_type'::regclass AND d.objid IN
      (SELECT typarray FROM pg_type WHERE typrelid IN (SELECT unnest(owned)::regclass)))) THEN
    RAISE EXCEPTION 'P2-01 Down refused: object/dependency outside migration scope (composite type)';
  END IF;
  IF EXISTS (SELECT FROM pg_depend d WHERE d.refclassid='pg_class'::regclass
      AND d.refobjid IN (SELECT unnest(owned || ARRAY['crm.customer_code_seq'])::regclass)
      AND NOT (
        (d.classid='pg_class'::regclass AND d.objid IN (SELECT unnest(relations)::regclass))
        OR (d.classid='pg_class'::regclass AND d.deptype='i' AND d.objid IN
          (SELECT reltoastrelid FROM pg_class WHERE oid IN (SELECT unnest(owned)::regclass)))
        OR (d.classid='pg_constraint'::regclass AND d.objid IN (SELECT oid FROM pg_constraint WHERE conrelid IN (SELECT unnest(owned)::regclass)))
        OR (d.classid='pg_type'::regclass AND d.objid IN (SELECT reltype FROM pg_class WHERE oid IN (SELECT unnest(owned)::regclass)))
        OR (d.classid='pg_attrdef'::regclass AND d.objid IN (SELECT oid FROM pg_attrdef WHERE adrelid IN (SELECT unnest(owned)::regclass)))
        OR (d.classid='pg_trigger'::regclass AND d.objid IN (SELECT oid FROM pg_trigger WHERE tgrelid IN (SELECT unnest(owned)::regclass) AND tgisinternal))
      )) THEN
    RAISE EXCEPTION 'P2-01 Down refused: object/dependency outside migration scope (dependency)';
  END IF;
END
$p201_down$;
""");

            migrationBuilder.DropForeignKey(
                name: "fk_interactions_order",
                schema: "crm",
                table: "interactions");

            migrationBuilder.DropForeignKey(
                name: "fk_redemptions_order",
                schema: "pricing",
                table: "voucher_redemptions");

            migrationBuilder.DropForeignKey(
                name: "fk_redemptions_customer",
                schema: "pricing",
                table: "voucher_redemptions");

            migrationBuilder.DropForeignKey(
                name: "fk_orders_voucher_id",
                schema: "sales",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "fk_orders_customer_account_id",
                schema: "sales",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "fk_orders_customer_id",
                schema: "sales",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "fk_carts_customer_account_id",
                schema: "sales",
                table: "carts");

            migrationBuilder.DropForeignKey(
                name: "fk_vouchers_customer",
                schema: "pricing",
                table: "vouchers");

            migrationBuilder.DropTable(
                name: "shipment_events",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "shipments",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "refunds",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "return_evidence",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "return_lines",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "voucher_redemptions",
                schema: "pricing");

            migrationBuilder.DropTable(
                name: "return_requests",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "order_status_history",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "order_lines",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "cart_items",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "interactions",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "carts",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "customer_logins",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "data_requests",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "consents",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "customer_interests",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "customer_accounts",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "customer_addresses",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "customer_tags",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "vouchers",
                schema: "pricing");

            migrationBuilder.DropTable(
                name: "expenses",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "cod_settlements",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "tags",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "customers",
                schema: "crm");

            migrationBuilder.DropSequence(
                name: "customer_code_seq",
                schema: "crm");

            migrationBuilder.Sql("""
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA crm,sales,finance REVOKE SELECT ON TABLES FROM tp_readonly;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA crm,sales,finance REVOKE USAGE ON SEQUENCES FROM tp_app;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_owner IN SCHEMA crm,sales,finance REVOKE SELECT ON SEQUENCES FROM tp_readonly;
REVOKE ALL ON SCHEMA crm,sales,finance FROM PUBLIC,tp_app,tp_readonly;
DROP SCHEMA crm;
DROP SCHEMA sales;
DROP SCHEMA finance;
""");
        }
    }
}
