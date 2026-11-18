namespace TeaPremium.Infrastructure.Persistence;

// Authoring helper only. Paste its output into a NEW migration; never call this
// from migration Up/Down, because future helper changes must not rewrite history.
public static class GrantsSql
{
    public static string Preflight
    {
        get
        {
            using var stream = typeof(GrantsSql).Assembly.GetManifestResourceStream("TeaPremium.DatabasePreflight.sql")
                ?? throw new InvalidOperationException("Missing database preflight resource.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        }
    }

    public static string PlatformAndIam() => Preflight + "\n" + PlatformAndIamGrants;

    private const string PlatformAndIamGrants = """
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
""";
}
