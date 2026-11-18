\set ON_ERROR_STOP on
\set ECHO none
\set VERBOSITY terse
-- Establish and verify protection before reading/interpolating credentials.
\ir protect-credentials.sql
\getenv tp_database TP_DATABASE
\getenv tp_owner_password TP_OWNER_PASSWORD
\getenv tp_app_password TP_APP_PASSWORD
\getenv tp_readonly_password TP_READONLY_PASSWORD
SELECT set_config('teapremium.target', :'tp_database', false) AS target,
       set_config('teapremium.provisioning', 'on', false) AS mode \gset
DO $$ BEGIN
  IF current_setting('teapremium.target') !~ '^[a-z][a-z0-9_]{0,62}$'
    OR current_setting('teapremium.target') IN ('postgres','template0','template1') THEN
    RAISE EXCEPTION 'TP_DATABASE must be a separate application database (lowercase identifier)';
  END IF;
END $$;
\ir preflight.sql
SELECT EXISTS (SELECT FROM pg_database WHERE datname=:'tp_database') AS db_exists \gset
\if :db_exists
  \connect :tp_database
  \ir protect-credentials.sql
  SELECT set_config('teapremium.target', :'tp_database', false) AS target,
         set_config('teapremium.provisioning', 'on', false) AS mode \gset
  \ir preflight.sql
\endif
-- Check all existing state before creating anything. Never ALTER ROLE/password.
SELECT format('CREATE ROLE %I LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L', name, password)
FROM (VALUES ('tp_owner', :'tp_owner_password'), ('tp_app', :'tp_app_password'),
             ('tp_readonly', :'tp_readonly_password')) AS desired(name,password)
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname=name)
\gexec
-- CREATE DATABASE is deliberately outside a transaction.
SELECT format('CREATE DATABASE %I OWNER tp_owner', :'tp_database')
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname=:'tp_database')
\gexec
\connect :tp_database
\ir protect-credentials.sql
BEGIN;
SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC; GRANT CONNECT ON DATABASE %I TO tp_app,tp_readonly', :'tp_database', :'tp_database') \gexec
REVOKE ALL ON SCHEMA public FROM PUBLIC;
-- Bootstrap trusted extensions as their real migration owner. Extension member
-- objects remain owned by PostgreSQL's bootstrap superuser by PostgreSQL design.
SET LOCAL ROLE tp_owner;
CREATE EXTENSION IF NOT EXISTS unaccent;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS btree_gist;
RESET ROLE;
-- Built-ins/extension members are not owned by tp_owner. Delegate only these
-- function grants, never a privileged role membership. Migrations grant runtime.
REVOKE EXECUTE ON FUNCTION pg_catalog.pg_try_advisory_xact_lock(integer,integer),
  pg_catalog.pg_advisory_xact_lock(bigint), pg_catalog.pg_try_advisory_lock(bigint),
  pg_catalog.pg_advisory_unlock(bigint), public.unaccent(text), public.unaccent(regdictionary,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION pg_catalog.pg_try_advisory_xact_lock(integer,integer),
  pg_catalog.pg_advisory_xact_lock(bigint), pg_catalog.pg_try_advisory_lock(bigint),
  pg_catalog.pg_advisory_unlock(bigint), public.unaccent(text), public.unaccent(regdictionary,text) TO tp_owner WITH GRANT OPTION;
COMMIT;
