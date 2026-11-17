namespace TeaPremium.Application.Iam;

// DEC-14/15/16; exact matrix in 02-architecture.md §7. No implicit owner wildcard.
public static class StaffPermissions
{
    public static readonly IReadOnlyDictionary<string, string[]> ByRole = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["owner"] = ["catalog.read", "catalog.write", "catalog.publish", "pricing.write", "costs.read",
            "inventory.read", "inventory.write", "inventory.approve", "orders.read", "orders.write", "orders.override_price",
            "payments.write", "refunds.write", "returns.write", "customers.read", "customers.write", "customers.export",
            "customers.erase", "customers.merge", "marketing.write", "marketing.spend", "content.write", "content.publish",
            "finance.read", "finance.write", "settings.write", "staff.manage", "audit.read"],
        ["sales"] = ["catalog.read", "inventory.read", "inventory.write", "orders.read", "orders.write", "orders.override_price",
            "payments.write", "returns.write", "customers.read", "customers.write", "marketing.write"],
        ["content"] = ["catalog.read", "catalog.write", "catalog.publish", "inventory.read", "content.write", "content.publish"]
    };

    public static bool ValidRoles(string[]? roles) => roles is not null && roles.Length <= 3
        && roles.Distinct(StringComparer.Ordinal).Count() == roles.Length && roles.All(ByRole.ContainsKey);
}
