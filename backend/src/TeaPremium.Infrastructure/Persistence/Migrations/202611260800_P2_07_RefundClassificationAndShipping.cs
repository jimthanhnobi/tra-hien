using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1707 // Keep the owner-approved migration name P2_07_RefundClassificationAndShipping.

namespace TeaPremium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P2_07_RefundClassificationAndShipping : Migration
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

            migrationBuilder.Sql("""
DO $p207_up$
DECLARE
  rel_oid oid;
  has_rows boolean;
BEGIN
  IF current_user <> 'tp_owner' THEN
    RAISE EXCEPTION 'P2-07 Up requires a direct tp_owner login';
  END IF;
  rel_oid := to_regclass('sales.refunds');
  IF rel_oid IS NULL THEN
    RAISE EXCEPTION 'P2-07 Up refused: sales.refunds is missing';
  END IF;
  LOCK TABLE sales.refunds IN ACCESS EXCLUSIVE MODE;

  SELECT EXISTS (SELECT 1 FROM sales.refunds) INTO has_rows;
  IF has_rows THEN
    RAISE EXCEPTION 'P2-07 Up refused: existing rows in sales.refunds require explicit owner-approved classification before migration';
  END IF;
END
$p207_up$;
""");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "sales",
                table: "refunds",
                type: "text",
                nullable: false);

            migrationBuilder.AddColumn<long>(
                name: "shipping_amount_vnd",
                schema: "sales",
                table: "refunds",
                type: "bigint",
                nullable: false,
                defaultValueSql: "0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_refunds_kind",
                schema: "sales",
                table: "refunds",
                sql: "kind IN ('overpayment','return','cancellation','adjustment')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_refunds_shipping_amount_vnd",
                schema: "sales",
                table: "refunds",
                sql: "shipping_amount_vnd >= 0 AND shipping_amount_vnd <= amount_vnd");

            migrationBuilder.AddCheckConstraint(
                name: "ck_refunds_kind_return_request",
                schema: "sales",
                table: "refunds",
                sql: "(kind = 'return') = (return_request_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_refunds_kind_shipping",
                schema: "sales",
                table: "refunds",
                sql: "kind IN ('return','adjustment') OR shipping_amount_vnd = 0");

            migrationBuilder.CreateIndex(
                name: "ux_refunds_return_request",
                schema: "sales",
                table: "refunds",
                column: "return_request_id",
                unique: true,
                filter: "return_request_id IS NOT NULL");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
DO $p207_down$
DECLARE
  rel_oid oid;
  idx_oid oid;
  kind_attnum smallint;
  ship_attnum smallint;
  return_req_attnum smallint;
  has_rows boolean;
  allowed_checks text[] := ARRAY[
    'ck_refunds_kind',
    'ck_refunds_shipping_amount_vnd',
    'ck_refunds_kind_return_request',
    'ck_refunds_kind_shipping'
  ];
BEGIN
  IF current_user <> 'tp_owner' THEN
    RAISE EXCEPTION 'P2-07 Down requires a direct tp_owner login';
  END IF;
  rel_oid := to_regclass('sales.refunds');
  IF rel_oid IS NULL THEN
    RAISE EXCEPTION 'P2-07 Down refused: sales.refunds is missing';
  END IF;
  LOCK TABLE sales.refunds IN ACCESS EXCLUSIVE MODE;

  SELECT EXISTS (SELECT 1 FROM sales.refunds) INTO has_rows;
  IF has_rows THEN
    RAISE EXCEPTION 'P2-07 Down refused: data in sales.refunds. Restore from backup or archive explicitly';
  END IF;

  SELECT attnum INTO kind_attnum FROM pg_attribute
    WHERE attrelid = rel_oid AND attname = 'kind' AND attnum > 0 AND NOT attisdropped;
  SELECT attnum INTO ship_attnum FROM pg_attribute
    WHERE attrelid = rel_oid AND attname = 'shipping_amount_vnd' AND attnum > 0 AND NOT attisdropped;
  SELECT attnum INTO return_req_attnum FROM pg_attribute
    WHERE attrelid = rel_oid AND attname = 'return_request_id' AND attnum > 0 AND NOT attisdropped;
  IF kind_attnum IS NULL OR ship_attnum IS NULL OR return_req_attnum IS NULL THEN
    RAISE EXCEPTION 'P2-07 Down refused: expected P2-07 columns missing on sales.refunds';
  END IF;

  idx_oid := to_regclass('sales.ux_refunds_return_request');
  IF idx_oid IS NULL OR NOT EXISTS (
      SELECT 1
      FROM pg_index i
      JOIN pg_class c ON c.oid = i.indexrelid
      WHERE i.indexrelid = idx_oid
        AND c.relkind = 'i'
        AND i.indrelid = rel_oid
        AND i.indisunique = true
        AND i.indnatts = 1
        AND i.indnkeyatts = 1
        AND i.indkey[0] = return_req_attnum
        AND i.indexprs IS NULL
        AND pg_get_expr(i.indpred, i.indrelid) = '(return_request_id IS NOT NULL)') THEN
    RAISE EXCEPTION 'P2-07 Down refused: ux_refunds_return_request missing or drifted outside migration scope';
  END IF;

  IF (
    SELECT count(*)
    FROM (VALUES
      ('ck_refunds_kind', 'CHECK ((kind = ANY (ARRAY[''overpayment''::text, ''return''::text, ''cancellation''::text, ''adjustment''::text])))'),
      ('ck_refunds_shipping_amount_vnd', 'CHECK (((shipping_amount_vnd >= 0) AND (shipping_amount_vnd <= amount_vnd)))'),
      ('ck_refunds_kind_return_request', 'CHECK (((kind = ''return''::text) = (return_request_id IS NOT NULL)))'),
      ('ck_refunds_kind_shipping', 'CHECK (((kind = ANY (ARRAY[''return''::text, ''adjustment''::text])) OR (shipping_amount_vnd = 0)))')
    ) AS expected(conname, condef)
    JOIN pg_constraint c
      ON c.conrelid = rel_oid
     AND c.contype = 'c'
     AND c.conname = expected.conname
     AND pg_get_constraintdef(c.oid) = expected.condef
  ) <> 4 THEN
    RAISE EXCEPTION 'P2-07 Down refused: expected P2-07 CHECK constraint missing or drifted outside migration scope';
  END IF;

  IF EXISTS (SELECT FROM pg_attribute a
      WHERE a.attrelid = rel_oid AND a.attnum IN (kind_attnum, ship_attnum) AND a.attacl IS NOT NULL) THEN
    RAISE EXCEPTION 'P2-07 Down refused: unexpected column ACL on sales.refunds(kind, shipping_amount_vnd)';
  END IF;

  IF EXISTS (SELECT FROM pg_depend d
      WHERE d.refclassid = 'pg_class'::regclass
        AND d.refobjid = rel_oid
        AND d.refobjsubid IN (kind_attnum, ship_attnum)
        AND NOT (
          (d.classid = 'pg_constraint'::regclass AND d.objid IN (
            SELECT oid FROM pg_constraint WHERE conrelid = rel_oid AND contype = 'c' AND conname = ANY(allowed_checks)
          ))
          OR (d.classid = 'pg_attrdef'::regclass AND d.objid IN (
            SELECT oid FROM pg_attrdef WHERE adrelid = rel_oid AND adnum = ship_attnum
          ))
          OR (d.classid = 'pg_class'::regclass AND d.objid = idx_oid)
        )) THEN
    RAISE EXCEPTION 'P2-07 Down refused: object/dependency outside migration scope on sales.refunds';
  END IF;

  IF EXISTS (SELECT FROM pg_depend d
      WHERE d.refclassid = 'pg_class'::regclass AND d.refobjid = idx_oid) THEN
    RAISE EXCEPTION 'P2-07 Down refused: object/dependency outside migration scope on ux_refunds_return_request';
  END IF;
END
$p207_down$;
""");

            migrationBuilder.DropIndex(
                name: "ux_refunds_return_request",
                schema: "sales",
                table: "refunds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_refunds_kind_shipping",
                schema: "sales",
                table: "refunds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_refunds_kind_return_request",
                schema: "sales",
                table: "refunds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_refunds_shipping_amount_vnd",
                schema: "sales",
                table: "refunds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_refunds_kind",
                schema: "sales",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "shipping_amount_vnd",
                schema: "sales",
                table: "refunds");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "sales",
                table: "refunds");
        }
    }
}
