using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF-generated migration operations use fixed array arguments.
#pragma warning disable CA1707 // Keep the owner-approved migration name P2_05_InventoryReservations.

namespace TeaPremium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P2_05_InventoryReservations : Migration
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

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservations", x => x.id);
                    table.CheckConstraint("ck_reservations_active_closed", "(status = 'active') = (closed_at IS NULL)");
                    table.CheckConstraint("ck_reservations_order_line_required", "status = 'released' OR order_line_id IS NOT NULL");
                    table.CheckConstraint("ck_reservations_quantity", "quantity > 0");
                    table.CheckConstraint("ck_reservations_status", "status IN ('active','consumed','released')");
                    table.ForeignKey(
                        name: "fk_reservations_line",
                        column: x => x.order_line_id,
                        principalSchema: "sales",
                        principalTable: "order_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_reservations_variant_id",
                        column: x => x.variant_id,
                        principalSchema: "catalog",
                        principalTable: "variants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_reservation_variant_active",
                schema: "inventory",
                table: "reservations",
                column: "variant_id",
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ux_reservation_active_line",
                schema: "inventory",
                table: "reservations",
                column: "order_line_id",
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.Sql("""
REVOKE ALL ON inventory.reservations FROM PUBLIC,tp_app,tp_readonly;
GRANT SELECT,INSERT,UPDATE,DELETE ON inventory.reservations TO tp_app;
GRANT SELECT ON inventory.reservations TO tp_readonly;
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
DO $p205_down$
DECLARE
  rel_oid oid;
  has_rows boolean;
  allowed_columns text[] := ARRAY[
    'id','variant_id','order_line_id','quantity','status','created_at','closed_at','close_reason'
  ];
  allowed_constraints text[] := ARRAY[
    'pk_reservations',
    'ck_reservations_quantity',
    'ck_reservations_status',
    'ck_reservations_active_closed',
    'ck_reservations_order_line_required',
    'fk_reservations_variant_id',
    'fk_reservations_line'
  ];
  allowed_indexes text[] := ARRAY[
    'pk_reservations',
    'ux_reservation_active_line',
    'ix_reservation_variant_active'
  ];
BEGIN
  IF current_user <> 'tp_owner' THEN
    RAISE EXCEPTION 'P2-05 Down requires a direct tp_owner login';
  END IF;
  rel_oid := to_regclass('inventory.reservations');
  IF rel_oid IS NULL THEN
    RAISE EXCEPTION 'P2-05 Down refused: inventory.reservations is missing';
  END IF;
  LOCK TABLE inventory.reservations IN ACCESS EXCLUSIVE MODE;

  SELECT EXISTS (SELECT 1 FROM inventory.reservations) INTO has_rows;
  IF has_rows THEN
    RAISE EXCEPTION 'P2-05 Down refused: data in inventory.reservations. Restore from backup or archive explicitly';
  END IF;

  IF EXISTS (SELECT FROM pg_attribute a
      WHERE a.attrelid = rel_oid AND a.attnum > 0 AND NOT a.attisdropped
        AND NOT a.attname = ANY(allowed_columns)) THEN
    RAISE EXCEPTION 'P2-05 Down refused: object/dependency outside migration scope (column)';
  END IF;

  IF EXISTS (SELECT FROM pg_constraint c
      WHERE (c.conrelid = rel_oid OR c.confrelid = rel_oid)
        AND NOT c.conname = ANY(allowed_constraints)) THEN
    RAISE EXCEPTION 'P2-05 Down refused: object/dependency outside migration scope (constraint)';
  END IF;

  IF EXISTS (SELECT FROM pg_index i JOIN pg_class ic ON ic.oid = i.indexrelid
      WHERE i.indrelid = rel_oid AND NOT ic.relname = ANY(allowed_indexes)) THEN
    RAISE EXCEPTION 'P2-05 Down refused: object/dependency outside migration scope (index)';
  END IF;

  IF EXISTS (SELECT FROM pg_trigger WHERE tgrelid = rel_oid AND NOT tgisinternal) THEN
    RAISE EXCEPTION 'P2-05 Down refused: object/dependency outside migration scope (trigger)';
  END IF;

  IF EXISTS (SELECT FROM pg_depend d WHERE d.refclassid = 'pg_type'::regclass
      AND d.refobjid IN (SELECT y.oid FROM pg_type y WHERE y.typrelid = rel_oid OR y.typelem = (SELECT reltype FROM pg_class WHERE oid = rel_oid))
      AND NOT (d.classid = 'pg_type'::regclass AND d.objid IN (SELECT typarray FROM pg_type WHERE typrelid = rel_oid))) THEN
    RAISE EXCEPTION 'P2-05 Down refused: object/dependency outside migration scope (composite type)';
  END IF;

  IF EXISTS (SELECT FROM pg_depend d WHERE d.refclassid = 'pg_class'::regclass
      AND d.refobjid = rel_oid
      AND NOT (
        (d.classid = 'pg_class'::regclass AND d.objid IN (SELECT indexrelid FROM pg_index WHERE indrelid = rel_oid))
        OR (d.classid = 'pg_class'::regclass AND d.deptype = 'i' AND d.objid = (SELECT reltoastrelid FROM pg_class WHERE oid = rel_oid))
        OR (d.classid = 'pg_constraint'::regclass AND d.objid IN (SELECT oid FROM pg_constraint WHERE conrelid = rel_oid))
        OR (d.classid = 'pg_type'::regclass AND d.objid = (SELECT reltype FROM pg_class WHERE oid = rel_oid))
        OR (d.classid = 'pg_attrdef'::regclass AND d.objid IN (SELECT oid FROM pg_attrdef WHERE adrelid = rel_oid))
        OR (d.classid = 'pg_trigger'::regclass AND d.objid IN (SELECT oid FROM pg_trigger WHERE tgrelid = rel_oid AND tgisinternal))
      )) THEN
    RAISE EXCEPTION 'P2-05 Down refused: object/dependency outside migration scope (dependency)';
  END IF;
END
$p205_down$;
""");

            migrationBuilder.DropTable(
                name: "reservations",
                schema: "inventory");
        }
    }
}
