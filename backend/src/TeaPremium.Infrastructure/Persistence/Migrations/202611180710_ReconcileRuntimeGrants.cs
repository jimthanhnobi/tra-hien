using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeaPremium.Infrastructure.Persistence.Migrations;

public partial class ReconcileRuntimeGrants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Fixed authoring-time output of GrantsSql.PlatformAndIam(), A01/D11.
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
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep least-privilege ACLs on rollback. Reverting to unsafe/missing grants
        // is not a safe downgrade; no data, role, key or sequence is removed.
    }
}
