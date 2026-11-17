using System.Text.Json;
using TeaPremium.Application.Settings;

namespace TeaPremium.UnitTests;

public sealed class SettingsValidationTests
{
    [Theory]
    [InlineData("shipping", "{\"shipping.flat_fee_vnd\":0,\"shipping.free_threshold_vnd\":null}")]
    [InlineData("orders", "{\"orders.max_staff_discount_percent\":100,\"orders.cod_requires_call\":false}")]
    [InlineData("payment", "{\"payment.methods_enabled\":[\"cod\"]}")]
    [InlineData("referral", "{\"referral.reward_voucher\":{\"type\":\"fixed\",\"value\":100000,\"max_discount_vnd\":null,\"min_order_vnd\":500000,\"valid_days\":90}}")]
    [InlineData("site", "{\"site.name\":\"Trà Hiên\",\"site.zalo_url\":\"https://zalo.me/example\"}")]
    public void ApprovedTypesAndBoundariesAreAccepted(string group, string json)
        => Assert.True(new PutSettingsValidator().Validate(new PutSettings(group, new string('a', 64), JsonSerializer.Deserialize<JsonElement>(json), Guid.NewGuid())).IsValid);

    [Theory]
    [InlineData("shipping", "{\"orders.cod_requires_call\":true}")]
    [InlineData("shipping", "{\"shipping.flat_fee_vnd\":-1}")]
    [InlineData("shipping", "{\"shipping.flat_fee_vnd\":1.5}")]
    [InlineData("shipping", "{\"shipping.flat_fee_vnd\":\"30000\"}")]
    [InlineData("orders", "{\"orders.cod_requires_call\":null}")]
    [InlineData("orders", "{\"orders.max_staff_discount_percent\":101}")]
    [InlineData("payment", "{\"payment.methods_enabled\":[\"cod\",\"cod\"]}")]
    [InlineData("payment", "{\"payment.bank\":{\"bin\":\"123456\"}}")]
    [InlineData("site", "{\"site.zalo_url\":\"javascript:alert(1)\"}")]
    [InlineData("site", "{\"site.unknown\":\"private input\"}")]
    [InlineData("shipping", "{\"shipping.flat_fee_vnd\":1,\"shipping.flat_fee_vnd\":2}")]
    [InlineData("shipping", "[]")]
    [InlineData("unknown", "{}")]
    public void InvalidKeysTypesShapesAndGroupsAreRejected(string group, string json)
    {
        var result = new PutSettingsValidator().Validate(new PutSettings(group, new string('a', 64), JsonSerializer.Deserialize<JsonElement>(json), Guid.NewGuid()));
        Assert.False(result.IsValid);
        Assert.DoesNotContain("private input", string.Join(' ', result.Errors));
    }

    [Fact]
    public void SeedValuesAreValidAndUnknownBusinessDataIsAbsent()
    {
        foreach (var pair in SettingsCatalog.Defaults)
        {
            var values = JsonSerializer.Deserialize<JsonElement>("{" + JsonSerializer.Serialize(pair.Key) + ":" + pair.Value + "}");
            Assert.Empty(SettingsCatalog.Validate(pair.Key.Split('.')[0], values));
        }
        Assert.DoesNotContain("payment.bank", SettingsCatalog.Defaults.Keys);
        Assert.DoesNotContain(SettingsCatalog.Defaults.Keys, x => x.StartsWith("site.", StringComparison.Ordinal) || x.StartsWith("notify.", StringComparison.Ordinal));
    }

    [Fact]
    public void CanonicalJsonPreservesUnicodeAndIgnoresObjectPropertyOrder()
    {
        Assert.Equal(SettingsJson.Canonical("{\"b\":2,\"a\":\"Trà Hiên\"}"), SettingsJson.Canonical("{\"a\":\"Trà Hiên\",\"b\":2}"));
        Assert.Contains("Trà Hiên", SettingsJson.Canonical("{\"name\":\"Trà Hiên\"}"), StringComparison.Ordinal);
    }
}
