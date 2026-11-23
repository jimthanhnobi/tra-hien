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

    public static string CatalogAndInventory() => PlatformAndIam() + "\n" + CatalogAndInventoryGrants;

    public static string CrmSalesFinance() => CatalogAndInventory() + "\n" + CrmSalesFinanceGrants + "\n" + Preflight;

    private const string CrmSalesFinanceGrants = """
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
""";

    private const string CatalogAndInventoryGrants = """
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
""";

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
