using System.Text.Json;
using TeaPremium.Infrastructure.Persistence;

namespace TeaPremium.IntegrationTests;

public sealed class AuditValuePolicyTests
{
    [Theory]
    [InlineData("shipping.flat_fee_vnd", "30000", "30000")]
    [InlineData("retention.carts_days", "30", "30")]
    [InlineData("orders.cod_requires_call", "true", "true")]
    [InlineData("shipping.free_threshold_vnd", "null", "null")]
    [InlineData("seo.brand_name", "\"Public brand\"", "\"[redacted]\"")]
    [InlineData("site.unknown", "12345", "\"[redacted]\"")]
    public void SettingRootUsesExplicitAllowlist(string key, string raw, string expected)
    {
        var sanitized = AuditValuePolicy.Filter("platform.settings", key, "Value", raw);
        Assert.Equal(expected, JsonSerializer.Serialize(sanitized));
    }

    [Fact]
    public void KnownNestedSafeValuesRemainVisibleButContactDataDoesNot()
    {
        const string raw = "{\"has_store\":true,\"address\":{\"phone\":\"0901234567\",\"email\":\"secret@example.com\"},\"contacts\":[{\"notes\":\"private\"},{\"receiver_name\":\"Private Person\"}]}";
        var sanitized = JsonSerializer.Serialize(AuditValuePolicy.Filter("platform.settings", "seo.organization", "Value", raw));
        using var json = JsonDocument.Parse(sanitized);
        Assert.True(json.RootElement.GetProperty("has_store").GetBoolean());
        Assert.Equal("[redacted]", json.RootElement.GetProperty("address").GetProperty("phone").GetString());
        Assert.Equal("[redacted]", json.RootElement.GetProperty("contacts")[0].GetProperty("notes").GetString());
        Assert.Equal("[redacted]", json.RootElement.GetProperty("contacts")[1].GetProperty("receiver_name").GetString());
        Assert.DoesNotContain("0901234567", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Person", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownJsonPropertyNameAndValueAreBothHidden()
    {
        const string raw = "{\"phone0901234567\":\"secret@example.com\",\"items\":[{\"email\":\"other@example.com\",\"amount_vnd\":123}]}";
        var sanitized = JsonSerializer.Serialize(AuditValuePolicy.Filter("platform.settings", "site.unknown", "Value", raw));
        Assert.Contains("unclassified", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("phone0901234567", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("other@example.com", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("123", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void BankAndContactListsNeverKeepRawValues()
    {
        var bank = JsonSerializer.Serialize(AuditValuePolicy.Filter("platform.settings", "payment.bank", "Value",
            "{\"bin\":9704,\"bank_name\":\"Bank\",\"account_number\":\"123456789\",\"account_name\":\"Private Person\"}"));
        var emails = JsonSerializer.Serialize(AuditValuePolicy.Filter("platform.settings", "notify.shop_emails", "Value",
            "[\"secret@example.com\",\"other@example.com\"]"));
        Assert.DoesNotContain("9704", bank, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", bank, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Person", bank, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", emails, StringComparison.Ordinal);
        Assert.DoesNotContain("other@example.com", emails, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitEnumsAndNumericRangesRemainUseful()
    {
        var methods = JsonSerializer.Serialize(AuditValuePolicy.Filter("platform.settings", "payment.methods_enabled", "Value",
            "[\"cod\",\"bank_transfer\",\"unexpected\"]"));
        Assert.Equal("[\"cod\",\"bank_transfer\",\"[redacted]\"]", methods);
        var ranges = JsonSerializer.Serialize(AuditValuePolicy.Filter("platform.settings", "seo.handling_days", "Value",
            "{\"min\":0,\"max\":2}"));
        Assert.Contains("\"min\":0", ranges, StringComparison.Ordinal);
        Assert.Contains("\"max\":2", ranges, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Phone")]
    [InlineData("Email")]
    [InlineData("AddressLine")]
    [InlineData("FullName")]
    [InlineData("ReceiverName")]
    [InlineData("Notes")]
    [InlineData("PasswordHash")]
    [InlineData("AccessToken")]
    [InlineData("UnknownText")]
    public void EntityStringFieldsFailClosed(string field)
    {
        Assert.Equal("[redacted]", AuditValuePolicy.Filter("audit_test.customers", null, field, "private"));
    }

    [Fact]
    public void EntityStatusAndMoneyCanBeSafeWhenClassified()
    {
        Assert.Equal("active", AuditValuePolicy.Filter("audit_test.customers", null, "Status", "active"));
        Assert.Equal(30000L, AuditValuePolicy.Filter("audit_test.customers", null, "AmountVnd", 30000L));
        Assert.Equal("[redacted]", AuditValuePolicy.Filter("audit_test.customers", null, "Status", "secret@example.com"));
    }

    [Fact]
    public void InvalidJsonErrorDoesNotContainRawValue()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            AuditValuePolicy.Filter("platform.settings", "site.unknown", "Value", "{secret@example.com"));
        Assert.Equal("Audited JSON value is invalid.", error.Message);
        Assert.DoesNotContain("secret@example.com", error.ToString(), StringComparison.Ordinal);
    }
}
