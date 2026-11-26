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

    [Theory]
    [InlineData("crm.customers", "FullName", "Đặng Trần Hiên")]
    [InlineData("crm.customers", "PhoneE164", "+84901234567")]
    [InlineData("crm.customers", "Email", "hien@example.test")]
    [InlineData("crm.customers", "ZaloName", "Trà Hiên")]
    [InlineData("crm.customers", "NoteSummary", "Ghi chú riêng tư")]
    [InlineData("crm.customers", "SearchText", "dang tran hien 0901234567 hien@example.test")]
    [InlineData("crm.customer_addresses", "Label", "Nhà riêng")]
    [InlineData("crm.customer_addresses", "ReceiverName", "Nguyễn Văn A")]
    [InlineData("crm.customer_addresses", "ReceiverPhone", "+84901234567")]
    [InlineData("crm.customer_addresses", "WardNameText", "Phường Bến Nghé")]
    [InlineData("crm.customer_addresses", "AddressLine", "123 Nguyễn Huệ")]
    [InlineData("crm.tags", "Name", "Khách VIP Hà Nội")]
    [InlineData("crm.customer_accounts", "LoginPhone", "+84901234567")]
    [InlineData("crm.customer_accounts", "PasswordHash", "AQAAAAIAAYag...")]
    [InlineData("crm.customer_accounts", "Email", "account@example.test")]
    [InlineData("crm.customer_accounts", "SecurityStamp", "secret-stamp-value")]
    public void CrmEntitiesAllowlistOnlySafeFieldsAndRedactEveryPiiFieldIncludingSearchText(string entityType, string property, string value)
    {
        Assert.Equal("[redacted]", AuditValuePolicy.Filter(entityType, null, property, value));

        var now = new DateTime(2026, 11, 23, 7, 0, 0, DateTimeKind.Utc);
        var id = Guid.CreateVersion7();
        Assert.Equal("KH001000", AuditValuePolicy.Filter("crm.customers", null, "Code", "KH001000"));
        Assert.Equal("retail", AuditValuePolicy.Filter("crm.customers", null, "CustomerGroup", "retail"));
        Assert.Equal(true, AuditValuePolicy.Filter("crm.customers", null, "IsVip", true));
        Assert.Equal("admin", AuditValuePolicy.Filter("crm.customers", null, "Origin", "admin"));
        Assert.Equal("zalo_personal", AuditValuePolicy.Filter("crm.customers", null, "AcquisitionSource", "zalo_personal"));
        Assert.Equal("active", AuditValuePolicy.Filter("crm.customers", null, "ContactStatus", "active"));
        Assert.Equal(1500000L, AuditValuePolicy.Filter("crm.customers", null, "LifetimeValueVnd", 1500000L));
        Assert.Equal("79", AuditValuePolicy.Filter("crm.customer_addresses", null, "ProvinceCode", "79"));
        Assert.Equal("79001", AuditValuePolicy.Filter("crm.customer_addresses", null, "WardCode", "79001"));
        Assert.Equal(true, AuditValuePolicy.Filter("crm.customer_addresses", null, "IsDefault", true));
        Assert.Equal("#1A2B3C", AuditValuePolicy.Filter("crm.tags", null, "Color", "#1A2B3C"));
        Assert.Equal(id, AuditValuePolicy.Filter("crm.customer_accounts", null, "CustomerId", id));
        Assert.Equal("active", AuditValuePolicy.Filter("crm.customer_accounts", null, "Status", "active"));
        Assert.Equal(now, AuditValuePolicy.Filter("crm.customer_accounts", null, "PhoneVerifiedAt", now));
        Assert.Equal(now, AuditValuePolicy.Filter("crm.customer_accounts", null, "EmailVerifiedAt", now));
    }

    [Theory]
    [InlineData("crm.customers", "Code", "0901234567")]
    [InlineData("crm.customers", "CustomerGroup", "secret@example.test")]
    [InlineData("crm.customers", "Origin", "Nguyen Van A")]
    [InlineData("crm.customers", "AcquisitionSource", "0901234567")]
    [InlineData("crm.customers", "ContactStatus", "private")]
    [InlineData("crm.customer_addresses", "ProvinceCode", "TP HCM")]
    [InlineData("crm.customer_addresses", "WardCode", "Ben Nghe")]
    [InlineData("crm.tags", "Color", "0901234567")]
    [InlineData("crm.customer_accounts", "Status", "secret@example.test")]
    [InlineData("crm.customer_accounts", "EmailVerifiedAt", "secret@example.test")]
    public void CrmSafeEnumAndCodeFieldsRejectSpoofedPiiStrings(string entityType, string property, string spoofedValue)
    {
        Assert.Equal("[redacted]", AuditValuePolicy.Filter(entityType, null, property, spoofedValue));
    }

    [Theory]
    [InlineData("Content", "Khách quen số bàn 02439998888 tại 99 Trần Hưng Đạo")]
    [InlineData("FollowUpResult", "Đã gọi lại anh Hiên 0901234567")]
    [InlineData("UnknownField", "PII text")]
    public void CrmInteractionsAllowlistSafeMetadataAndRedactContentAndFollowUpResult(string property, string secretValue)
    {
        Assert.Equal("[redacted]", AuditValuePolicy.Filter("crm.interactions", null, property, secretValue));

        var now = new DateTime(2026, 11, 25, 7, 0, 0, DateTimeKind.Utc);
        var id = Guid.CreateVersion7();
        Assert.Equal(id, AuditValuePolicy.Filter("crm.interactions", null, "CustomerId", id));
        Assert.Equal(id, AuditValuePolicy.Filter("crm.interactions", null, "StaffId", id));
        Assert.Equal(id, AuditValuePolicy.Filter("crm.interactions", null, "OrderId", id));
        Assert.Equal(id, AuditValuePolicy.Filter("crm.interactions", null, "CampaignId", id));
        Assert.Equal("other", AuditValuePolicy.Filter("crm.interactions", null, "Channel", "other"));
        Assert.Equal("note", AuditValuePolicy.Filter("crm.interactions", null, "Kind", "note"));
        Assert.Equal(now, AuditValuePolicy.Filter("crm.interactions", null, "OccurredAt", now));
        Assert.Equal(now, AuditValuePolicy.Filter("crm.interactions", null, "FollowUpAt", now));
        Assert.Equal(now, AuditValuePolicy.Filter("crm.interactions", null, "FollowUpDoneAt", now));
        Assert.Equal(now, AuditValuePolicy.Filter("crm.interactions", null, "CreatedAt", now));
    }

    [Theory]
    [InlineData("Channel", "0901234567")]
    [InlineData("Channel", "secret@example.test")]
    [InlineData("Kind", "Nguyen Van A")]
    [InlineData("OccurredAt", "0901234567")]
    public void CrmInteractionsRejectSpoofedChannelOrKindStrings(string property, string spoofedValue)
    {
        Assert.Equal("[redacted]", AuditValuePolicy.Filter("crm.interactions", null, property, spoofedValue));
    }

    [Theory]
    [InlineData("sales.payments", "BankReference", "FT261125-NGUYEN-VAN-A-0901234567")]
    [InlineData("sales.payments", "Note", "Khach chuyen khoan qua VCB 0901234567")]
    [InlineData("sales.payments", "VoidReason", "Nhap nham so tien cua anh Minh")]
    [InlineData("sales.refunds", "BankReference", "FT261125-REFUND-STK-123456789")]
    [InlineData("sales.refunds", "Reason", "Hoan tien cho chi Lan 0901234567")]
    [InlineData("sales.return_requests", "Description", "Am bi vo quai khi giao cho anh Nam")]
    [InlineData("sales.return_requests", "DecisionNote", "Duyet hoan tien theo anh Minh")]
    public void PaymentRefundAndReturnEntitiesRedactSensitiveFieldsAndAllowOnlyTypedSafeFields(
        string entityType,
        string sensitiveProperty,
        string sensitiveValue)
    {
        Assert.Equal("[redacted]", AuditValuePolicy.Filter(entityType, null, sensitiveProperty, sensitiveValue));

        var now = new DateTime(2026, 11, 25, 7, 0, 0, DateTimeKind.Utc);
        var id = Guid.CreateVersion7();

        Assert.Equal(id, AuditValuePolicy.Filter("sales.payments", null, "OrderId", id));
        Assert.Equal("bank_transfer", AuditValuePolicy.Filter("sales.payments", null, "Method", "bank_transfer"));
        Assert.Equal("manual", AuditValuePolicy.Filter("sales.payments", null, "Provider", "manual"));
        Assert.Equal(500_000L, AuditValuePolicy.Filter("sales.payments", null, "AmountVnd", 500_000L));
        Assert.Equal("voided", AuditValuePolicy.Filter("sales.payments", null, "Status", "voided"));
        Assert.Equal("awaiting_settlement", AuditValuePolicy.Filter("sales.payments", null, "Status", "awaiting_settlement"));
        Assert.Equal(now, AuditValuePolicy.Filter("sales.payments", null, "ConfirmedAt", now));
        Assert.Equal(now, AuditValuePolicy.Filter("sales.payments", null, "ReceivedAt", now));
        Assert.Equal(id, AuditValuePolicy.Filter("sales.payments", null, "CodSettlementId", id));

        Assert.Equal(id, AuditValuePolicy.Filter("sales.refunds", null, "OrderId", id));
        Assert.Equal(id, AuditValuePolicy.Filter("sales.refunds", null, "ReturnRequestId", id));
        Assert.Equal("overpayment", AuditValuePolicy.Filter("sales.refunds", null, "Kind", "overpayment"));
        Assert.Equal("return", AuditValuePolicy.Filter("sales.refunds", null, "Kind", "return"));
        Assert.Equal("cancellation", AuditValuePolicy.Filter("sales.refunds", null, "Kind", "cancellation"));
        Assert.Equal("adjustment", AuditValuePolicy.Filter("sales.refunds", null, "Kind", "adjustment"));
        Assert.Equal(200_000L, AuditValuePolicy.Filter("sales.refunds", null, "AmountVnd", 200_000L));
        Assert.Equal(30_000L, AuditValuePolicy.Filter("sales.refunds", null, "ShippingAmountVnd", 30_000L));
        Assert.Equal("cash", AuditValuePolicy.Filter("sales.refunds", null, "Method", "cash"));
        Assert.Equal(now, AuditValuePolicy.Filter("sales.refunds", null, "RefundedAt", now));

        Assert.Equal("DT-261125-001", AuditValuePolicy.Filter("sales.return_requests", null, "Code", "DT-261125-001"));
        Assert.Equal("damaged_in_transit", AuditValuePolicy.Filter("sales.return_requests", null, "Reason", "damaged_in_transit"));
        Assert.Equal("resolved", AuditValuePolicy.Filter("sales.return_requests", null, "Status", "resolved"));
        Assert.Equal("refund", AuditValuePolicy.Filter("sales.return_requests", null, "Resolution", "refund"));
        Assert.Equal(now, AuditValuePolicy.Filter("sales.return_requests", null, "ResolvedAt", now));
    }

    [Theory]
    [InlineData("sales.payments", "Method", "secret@example.test")]
    [InlineData("sales.payments", "Provider", "0901234567")]
    [InlineData("sales.payments", "Status", "Nguyen Van A")]
    [InlineData("sales.payments", "ConfirmedAt", "2026-11-25 0901234567")]
    [InlineData("sales.refunds", "Kind", "secret-pii")]
    [InlineData("sales.refunds", "Method", "cod")]
    [InlineData("sales.refunds", "AmountVnd", "500000")]
    [InlineData("sales.refunds", "ShippingAmountVnd", "30000")]
    [InlineData("sales.return_requests", "Code", "0901234567")]
    [InlineData("sales.return_requests", "Status", "secret")]
    [InlineData("sales.return_requests", "Resolution", "0901234567")]
    public void PaymentRefundAndReturnSafeFieldsRejectSpoofedStrings(string entityType, string property, string spoofedValue)
    {
        Assert.Equal("[redacted]", AuditValuePolicy.Filter(entityType, null, property, spoofedValue));
    }
}
