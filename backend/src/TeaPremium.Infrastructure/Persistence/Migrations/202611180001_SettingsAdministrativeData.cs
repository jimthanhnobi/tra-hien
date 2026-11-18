using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeaPremium.Infrastructure.Persistence.Migrations;

public partial class SettingsAdministrativeData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Read-only migration identity is included in a dry-run plan. No runtime DDL rights.
        migrationBuilder.Sql("GRANT SELECT ON TABLE public.\"__EFMigrationsHistory\" TO tp_app;");
        // Exact approved F-02 DDL. Roles are provisioned before migration, never invented here.
        migrationBuilder.Sql("""
CREATE TABLE platform.administrative_data_imports (
  id uuid CONSTRAINT pk_administrative_data_imports PRIMARY KEY,
  previous_import_id uuid,
  dataset_version text NOT NULL,
  release_order bigint NOT NULL,
  effective_on date NOT NULL,
  operation text NOT NULL,
  manifest jsonb NOT NULL,
  manifest_sha256 text NOT NULL,
  csv_sha256 text NOT NULL,
  plan_sha256 text NOT NULL,
  catalog_sha256 text NOT NULL,
  receipt jsonb NOT NULL,
  applied_at timestamptz NOT NULL,
  recovery_of uuid,
  re_advance_of uuid,
  CONSTRAINT ux_administrative_import_previous
    UNIQUE NULLS NOT DISTINCT (previous_import_id),
  CONSTRAINT fk_administrative_import_previous FOREIGN KEY (previous_import_id)
    REFERENCES platform.administrative_data_imports(id),
  CONSTRAINT fk_administrative_import_recovery FOREIGN KEY (recovery_of)
    REFERENCES platform.administrative_data_imports(id),
  CONSTRAINT fk_administrative_import_re_advance FOREIGN KEY (re_advance_of)
    REFERENCES platform.administrative_data_imports(id),
  CONSTRAINT ck_administrative_import_version CHECK (length(dataset_version) > 0 AND release_order > 0),
  CONSTRAINT ck_administrative_import_operation CHECK (
    (operation = 'import' AND recovery_of IS NULL AND re_advance_of IS NULL) OR
    (operation = 'recovery' AND recovery_of IS NOT NULL AND re_advance_of IS NULL) OR
    (operation = 're_advance' AND recovery_of IS NULL AND re_advance_of IS NOT NULL)),
  CONSTRAINT ck_administrative_import_genesis CHECK (
    previous_import_id IS NOT NULL OR operation = 'import'),
  CONSTRAINT ck_administrative_import_hashes CHECK (
    manifest_sha256 ~ '^[0-9a-f]{64}$' AND csv_sha256 ~ '^[0-9a-f]{64}$' AND
    plan_sha256 ~ '^[0-9a-f]{64}$' AND catalog_sha256 ~ '^[0-9a-f]{64}$'),
  CONSTRAINT ck_administrative_import_json CHECK (
    jsonb_typeof(manifest) = 'object' AND jsonb_typeof(receipt) = 'object'),
  CONSTRAINT ck_administrative_import_no_self CHECK (
    previous_import_id IS DISTINCT FROM id AND recovery_of IS DISTINCT FROM id AND
    re_advance_of IS DISTINCT FROM id)
);
GRANT SELECT, INSERT ON platform.administrative_data_imports TO tp_app;
REVOKE UPDATE, DELETE, TRUNCATE ON platform.administrative_data_imports FROM tp_app;
GRANT SELECT ON platform.administrative_data_imports TO tp_readonly;
GRANT EXECUTE ON FUNCTION pg_catalog.pg_try_advisory_xact_lock(integer, integer) TO tp_app;
""");
        // Frozen P0-09 defaults; operator changes survive idempotent/repeated deployment.
        migrationBuilder.Sql("""
INSERT INTO platform.settings(key,value) VALUES
('shipping.flat_fee_vnd','30000'),('shipping.free_threshold_vnd','1000000'),
('orders.bank_transfer_hold_hours','24'),('orders.max_quantity_per_line_web','10'),
('orders.cod_requires_call','true'),('orders.auto_confirm_on_full_payment','true'),
('orders.max_staff_discount_percent','10'),('returns.window_days','7'),
('marketing.min_days_between_messages','14'),('referral.max_rewards_per_year','20'),
('payment.methods_enabled','["cod","bank_transfer"]'),
('referral.welcome_discount_vnd','100000'),('referral.welcome_min_order_vnd','500000'),
('referral.reward_voucher','{"type":"fixed","value":100000,"max_discount_vnd":null,"min_order_vnd":500000,"valid_days":90}'),
('referral.qualify_after_days','7'),('seo.sold_unique_keep_days','180'),
('seo.handling_days','{"min":0,"max":1}'),('seo.transit_days','{"min":1,"max":5}'),
('retention.carts_days','30'),('retention.link_clicks_days','400'),
('retention.idempotency_hours','48'),('retention.notification_logs_days','180'),
('retention.abandoned_upload_hours','24'),('retention.web_vitals_days','30')
ON CONFLICT (key) DO NOTHING;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Only explicit owner tooling may call Down on a verified disposable DB.
        // Never remove seeded values: they may have been edited or used by an operator.
        migrationBuilder.Sql("""
LOCK TABLE platform.administrative_data_imports IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM platform.administrative_data_imports) THEN
    RAISE EXCEPTION 'Import history exists; use controlled recovery';
  END IF;
END $$;
DROP TABLE platform.administrative_data_imports;
""");
    }
}
