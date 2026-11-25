using System.Text.Json;
using System.Text.Json.Serialization;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Crm;

namespace TeaPremium.UnitTests;

public sealed class CustomerValidatorsTests
{
    private static readonly CustomerActor SalesActor = new(Guid.CreateVersion7(), ["customers.read", "customers.write"], false);
    private static readonly string ValidRowVersion = CatalogVersions.Encode(42u);

    [Theory]
    [InlineData("", null, null, null, null, null)]
    [InlineData("   ", null, null, null, null, null)]
    [InlineData("Name\0Bad", null, null, null, null, null)]
    [InlineData("Valid Name", "bad-email", null, null, null, null)]
    [InlineData("Valid Name", null, "invalid_group", null, null, null)]
    [InlineData("Valid Name", null, null, "invalid_source", null, null)]
    [InlineData("Valid Name", null, null, null, "erased", null)]
    [InlineData("Valid Name", null, null, null, null, "Note\0Bad")]
    public void CreateCustomerValidatorRejectsBlankNameNulBytesInvalidEnumsAndErasedStatus(
        string fullName, string? email, string? group, string? source, string? status, string? note)
    {
        var validator = new CreateCustomerValidator();
        var cmd = new CreateCustomer(fullName, "0901234567", email, null, group, false, source, null, status, note, false, null, SalesActor);
        var result = validator.Validate(cmd);
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("""{"rowVersion":"KgAAAA==","phone":"0901234567"}""")]
    [InlineData("""{"rowVersion":"KgAAAA==","phoneE164":"+84901234567"}""")]
    [InlineData("""{"rowVersion":"KgAAAA==","origin":"web"}""")]
    [InlineData("""{"rowVersion":"KgAAAA==","acquisitionSource":"facebook"}""")]
    [InlineData("""{"rowVersion":"KgAAAA==","hadLegacyPurchase":true}""")]
    [InlineData("""{"fullName":"Missing RowVersion"}""")]
    [InlineData("""{"rowVersion":"bad","fullName":"Bad RowVersion"}""")]
    [InlineData("""{"rowVersion":"KgAAAA==","contactStatus":"erased"}""")]
    [InlineData("""{"rowVersion":"KgAAAA==","unknownField":"x"}""")]
    public void BrCrm04PatchCustomerValidatorRejectsIdentityFieldsAndRequiresValidRowVersion(string json)
    {
        var validator = new PatchCustomerValidator();
        using var doc = JsonDocument.Parse(json);
        var result = validator.Validate(new PatchCustomer(Guid.CreateVersion7(), doc.RootElement.Clone(), SalesActor));
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("""{"rowVersion":"KgAAAA==","phone":"0901234567"}""")]
    [InlineData("""{"rowVersion":"KgAAAA==","phone":"0901234567","reason":"ab"}""")]
    [InlineData("""{"rowVersion":"KgAAAA==","reason":"Valid reason but no target field"}""")]
    [InlineData("""{"phone":"0901234567","reason":"Missing row version"}""")]
    [InlineData("""{"rowVersion":"bad","phone":"0901234567","reason":"Malformed row version"}""")]
    [InlineData("""{"rowVersion":"KgAAAA==","origin":"invalid_origin","reason":"Invalid enum"}""")]
    public void BrCrm04CorrectCustomerIdentityValidatorRequiresRowVersionReasonAndAtLeastOneTargetField(string json)
    {
        var validator = new CorrectCustomerIdentityValidator();
        using var doc = JsonDocument.Parse(json);
        var result = validator.Validate(new CorrectCustomerIdentity(Guid.CreateVersion7(), doc.RootElement.Clone(), SalesActor));
        Assert.False(result.IsValid);

        var longReasonJson = JsonSerializer.Serialize(new { rowVersion = ValidRowVersion, phone = "0901234567", reason = new string('r', 501) });
        using var longDoc = JsonDocument.Parse(longReasonJson);
        Assert.False(validator.Validate(new CorrectCustomerIdentity(Guid.CreateVersion7(), longDoc.RootElement.Clone(), SalesActor)).IsValid);
    }

    [Theory]
    [InlineData(0, 20, null, null, null, null, null, null, null)]
    [InlineData(1, 101, null, null, null, null, null, null, null)]
    [InlineData(1, 20, "unknown:asc", null, null, null, null, null, null)]
    [InlineData(1, 20, null, "bad_group", null, null, null, null, null)]
    [InlineData(1, 20, null, null, "material:not-a-guid", null, null, null, null)]
    [InlineData(1, 20, null, null, "price_band:invalid_band", null, null, null, null)]
    [InlineData(1, 20, null, null, null, "2026-13-40", null, null, null)]
    [InlineData(1, 20, null, null, null, null, "2026-11-23T00:00:00", null, null)]
    [InlineData(1, 20, null, null, null, null, null, -1L, null)]
    [InlineData(1, 20, null, null, null, null, null, null, "sms:unknown_purpose")]
    public void ListCustomersValidatorValidatesPaginationSortInterestHasConsentAndDateFilters(
        int page, int pageSize, string? sort, string? group, string? interest,
        string? before, string? after, long? minLtv, string? hasConsent)
    {
        var validator = new ListCustomersValidator();
        var query = new ListCustomers(page, pageSize, sort, null, group, null, interest, before, after, minLtv, null, hasConsent);
        Assert.False(validator.Validate(query).IsValid);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Name\0Bad", null)]
    [InlineData("Valid", "red")]
    [InlineData("Valid", "#12345")]
    [InlineData("Valid", "#ZZZZZZ")]
    public void TagValidatorsEnforceTrimmedNameBoundsHexColorAndDistinctTagIds(string name, string? color)
    {
        var createValidator = new CreateTagValidator();
        Assert.False(createValidator.Validate(new CreateTag(name, color)).IsValid);
        Assert.False(createValidator.Validate(new CreateTag(new string('a', 61), "#1A2B3C")).IsValid);
        Assert.True(createValidator.Validate(new CreateTag("VIP", "#1A2B3C")).IsValid);

        var putValidator = new PutCustomerTagsValidator();
        Assert.False(putValidator.Validate(new PutCustomerTags(Guid.CreateVersion7(), [Guid.Empty], null)).IsValid);
        Assert.False(putValidator.Validate(new PutCustomerTags(Guid.CreateVersion7(), [], "bad")).IsValid);
        Assert.True(putValidator.Validate(new PutCustomerTags(Guid.CreateVersion7(), [Guid.CreateVersion7()], ValidRowVersion)).IsValid);
    }

    [Theory]
    [InlineData("", "0901234567", "79", "79001", null, "123 Le Loi")]
    [InlineData("Receiver", "", "79", "79001", null, "123 Le Loi")]
    [InlineData("Receiver", "0901234567", "7", "79001", null, "123 Le Loi")]
    [InlineData("Receiver", "0901234567", "79", "790", null, "123 Le Loi")]
    [InlineData("Receiver", "0901234567", "79", null, null, "123 Le Loi")]
    [InlineData("Receiver", "0901234567", "79", null, "   ", "123 Le Loi")]
    [InlineData("Receiver", "0901234567", "79", "79001", null, "")]
    public void CustomerAddressValidatorsEnforceRequiredFieldsPhoneAndWardFallbackRule(
        string receiverName, string receiverPhone, string provinceCode, string? wardCode, string? wardNameText, string addressLine)
    {
        var validator = new CreateCustomerAddressValidator();
        var cmd = new CreateCustomerAddress(Guid.CreateVersion7(), null, receiverName, receiverPhone, provinceCode, wardCode, wardNameText, addressLine, false, null);
        Assert.False(validator.Validate(cmd).IsValid);

        var validFallback = new CreateCustomerAddress(Guid.CreateVersion7(), "Nhà riêng", "Nguyễn An", "0901234567", "79", null, "Phường cũ", "123 Lê Lợi", true, ValidRowVersion);
        Assert.True(validator.Validate(validFallback).IsValid);
    }

    [Fact]
    public void CustomerInputRejectsDuplicateJsonPropertiesAndUnmappedMembers()
    {
        var docOptions = new JsonDocumentOptions { AllowDuplicateProperties = false };
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse("""{"fullName":"A","fullName":"B"}""", docOptions));

        var strictOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict
        };
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<StrictProbe>("""{"name":"A","extra":1}""", strictOptions));
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<StrictProbe>("""{"name":"A","name":"B"}""", strictOptions));
    }

    private sealed record StrictProbe(string Name);
}
