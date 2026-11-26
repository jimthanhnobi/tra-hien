using System.Text.Json;
using TeaPremium.Application.Crm;

namespace TeaPremium.UnitTests;

public sealed class InteractionValidatorsTests
{
    private static readonly CustomerActor SalesActor = new(Guid.CreateVersion7(), ["customers.read", "customers.write"], false);

    [Theory]
    [InlineData("invalid_channel", "consultation", "Tư vấn ấm", null, null)]
    [InlineData("zalo_personal", "campaign_message", "Tin chiến dịch", null, null)] // GAP-P204-01 = A
    [InlineData("zalo_personal", "unknown_kind", "Nội dung", null, null)]
    [InlineData("zalo_personal", "note", "", null, null)]
    [InlineData("zalo_personal", "note", "   ", null, null)]
    [InlineData("zalo_personal", "note", "Chứa\0NUL", null, null)]
    [InlineData("zalo_personal", "note", "Hợp lệ", "2026-11-25T03:30:00", null)] // missing UTC offset
    [InlineData("zalo_personal", "note", "Hợp lệ", "2026-11-25", null)] // date-only
    [InlineData("zalo_personal", "note", "Hợp lệ", "2026-11-25 03:30:00Z", null)] // missing 'T'
    [InlineData("zalo_personal", "note", "Hợp lệ", "Tue, 24 Nov 2026 10:00:00 +07:00", null)] // RFC 2822 containing 'T'
    [InlineData("zalo_personal", "note", "Hợp lệ", null, "Thu, 26 Nov 2026 10:00:00 +07:00")] // RFC 2822 on followUpAt
    [InlineData("zalo_personal", "note", "Hợp lệ", null, "2026-11-26T09:00:00")] // missing UTC offset on followUpAt
    [InlineData("zalo_personal", "note", "Hợp lệ", "2026-11-25T03:30:00+0700", null)] // offset missing colon
    public void CreateInteractionValidatorEnforcesChannelManualKindContentAndExplicitUtcOffsets(
        string channel,
        string kind,
        string content,
        string? occurredAt,
        string? followUpAt)
    {
        var validator = new CreateCustomerInteractionValidator();
        var cmd = new CreateCustomerInteraction(Guid.CreateVersion7(), channel, kind, content, occurredAt, null, followUpAt, SalesActor);
        Assert.False(validator.Validate(cmd).IsValid);

        // Guid.Empty orderId rejected
        var emptyOrder = new CreateCustomerInteraction(Guid.CreateVersion7(), "phone", "inquiry", "Hỏi ấm", null, Guid.Empty, null, SalesActor);
        Assert.False(validator.Validate(emptyOrder).IsValid);

        // Valid request with explicit +07:00 and Z offsets passes
        var valid = new CreateCustomerInteraction(
            Guid.CreateVersion7(),
            "zalo_personal",
            "consultation",
            "Tư vấn ấm Tử Sa 🍵",
            "2026-11-25T10:30:00+07:00",
            Guid.CreateVersion7(),
            "2026-11-26T02:00:00Z",
            SalesActor);
        Assert.True(validator.Validate(valid).IsValid);
    }

    [Fact]
    public void CreateInteractionValidatorEmitsOnlyCleanMachineCodesWhenRequiredFieldsAreNull()
    {
        var validator = new CreateCustomerInteractionValidator();
        var cmd = new CreateCustomerInteraction(Guid.CreateVersion7(), null!, null!, null!, null, null, null, SalesActor);

        var result = validator.Validate(cmd);
        Assert.False(result.IsValid);

        var byProperty = result.Errors
            .GroupBy(e => e.PropertyName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray(), StringComparer.Ordinal);

        Assert.Equal(["invalid_channel"], byProperty["channel"]);
        Assert.Equal(["invalid_interaction_kind"], byProperty["kind"]);
        Assert.Equal(["invalid_content"], byProperty["content"]);
        Assert.Equal(3, byProperty.Count);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"unknownField":"x"}""")]
    [InlineData("""{"campaignId":"01936000-0000-7000-8000-000000000001"}""")]
    [InlineData("""{"followUpDoneAt":"2026-11-25T10:00:00Z"}""")]
    [InlineData("""{"followUpResult":"Đã gọi"}""")]
    [InlineData("""{"customerId":"01936000-0000-7000-8000-000000000001"}""")]
    [InlineData("""{"staffId":"01936000-0000-7000-8000-000000000001"}""")]
    [InlineData("""{"kind":"campaign_message"}""")]
    [InlineData("""{"channel":null}""")]
    [InlineData("""{"kind":null}""")]
    [InlineData("""{"content":null}""")]
    [InlineData("""{"content":"   "}""")]
    [InlineData("""{"occurredAt":null}""")]
    [InlineData("""{"occurredAt":"2026-11-25T10:00:00"}""")]
    [InlineData("""{"occurredAt":"Tue, 24 Nov 2026 10:00:00 +07:00"}""")]
    [InlineData("""{"followUpAt":"2026-11-26T10:00:00"}""")]
    [InlineData("""{"followUpAt":"Thu, 26 Nov 2026 10:00:00 +07:00"}""")]
    [InlineData("""{"orderId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"orderId":"not-a-guid"}""")]
    public void PatchInteractionValidatorRejectsEmptyPatchUnknownFieldsCampaignMessageAndCompletionFields(string json)
    {
        var validator = new PatchCustomerInteractionValidator();
        using var doc = JsonDocument.Parse(json);
        var cmd = new PatchCustomerInteraction(Guid.CreateVersion7(), Guid.CreateVersion7(), doc.RootElement.Clone(), SalesActor);
        Assert.False(validator.Validate(cmd).IsValid);

        // Null is allowed on orderId and followUpAt (clears link / cancels pending follow-up)
        using var clearDoc = JsonDocument.Parse("""{"orderId":null,"followUpAt":null}""");
        var clearCmd = new PatchCustomerInteraction(Guid.CreateVersion7(), Guid.CreateVersion7(), clearDoc.RootElement.Clone(), SalesActor);
        Assert.True(validator.Validate(clearCmd).IsValid);
    }

    [Theory]
    [InlineData(null, "follow_up_result_required")]
    [InlineData("", "follow_up_result_required")]
    [InlineData("   \t\n ", "follow_up_result_required")]
    [InlineData("Kết quả\0NUL", "invalid_follow_up_result")]
    public void BrCrm06CompleteFollowUpValidatorRequiresNonBlankTrimmedResultWithoutNulBytes(string? resultText, string expectedErrorCode)
    {
        var validator = new CompleteInteractionFollowUpValidator();
        var res = validator.Validate(new CompleteInteractionFollowUp(Guid.CreateVersion7(), Guid.CreateVersion7(), resultText!, SalesActor));
        Assert.False(res.IsValid);
        Assert.Contains(res.Errors, e => e.PropertyName == "result" && e.ErrorMessage == expectedErrorCode);

        // > 4000 runes rejected
        var tooLong = string.Concat(Enumerable.Repeat("🍵", 4001));
        var longRes = validator.Validate(new CompleteInteractionFollowUp(Guid.CreateVersion7(), Guid.CreateVersion7(), tooLong, SalesActor));
        Assert.False(longRes.IsValid);
        Assert.Contains(longRes.Errors, e => e.PropertyName == "result" && e.ErrorMessage == "invalid_follow_up_result");

        // 4000 runes accepted
        var maxValid = string.Concat(Enumerable.Repeat("🍵", 4000));
        Assert.True(validator.Validate(new CompleteInteractionFollowUp(Guid.CreateVersion7(), Guid.CreateVersion7(), maxValid, SalesActor)).IsValid);
    }

    [Theory]
    [InlineData(null, 1, 20, true)]
    [InlineData("today", 1, 100, true)]
    [InlineData("overdue", 2, 1, true)]
    [InlineData("week", 1, 50, true)]
    [InlineData("", 1, 20, false)]
    [InlineData("tomorrow", 1, 20, false)]
    [InlineData("TODAY", 1, 20, false)]
    [InlineData("today", 0, 20, false)]
    [InlineData("today", 1, 0, false)]
    [InlineData("today", 1, 101, false)]
    public void ListFollowUpsValidatorEnforcesDueEnumAndPaginationBounds(string? due, int page, int pageSize, bool expectedValid)
    {
        var validator = new ListFollowUpsValidator();
        Assert.Equal(expectedValid, validator.Validate(new ListFollowUps(due, page, pageSize)).IsValid);
    }

    [Theory]
    [InlineData(true, 1, 20, true)]
    [InlineData(true, 1, 100, true)]
    [InlineData(false, 1, 20, false)]
    [InlineData(true, 0, 20, false)]
    [InlineData(true, 1, 0, false)]
    [InlineData(true, 1, 101, false)]
    public void ListCustomerInteractionsValidatorEnforcesCustomerIdAndPaginationBounds(
        bool validCustomerId,
        int page,
        int pageSize,
        bool expectedValid)
    {
        var validator = new ListCustomerInteractionsValidator();
        var customerId = validCustomerId ? Guid.CreateVersion7() : Guid.Empty;
        Assert.Equal(expectedValid, validator.Validate(new ListCustomerInteractions(customerId, page, pageSize)).IsValid);
    }

    [Fact]
    public void InteractionJsonBindingContractsDisallowDuplicateAndUnmappedProperties()
    {
        // Duplicate JSON keys rejected by StrictDocumentOptions
        Assert.ThrowsAny<JsonException>(() =>
            JsonDocument.Parse("""{"channel":"phone","channel":"email"}""", InteractionJson.StrictDocumentOptions));

        // Unmapped properties (including campaignId / followUpDoneAt / followUpResult) rejected by StrictSerializerOptions
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<StrictCreateProbe>(
                """{"channel":"phone","kind":"note","content":"Hi","campaignId":"01936000-0000-7000-8000-000000000001"}""",
                InteractionJson.StrictSerializerOptions));
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<StrictCreateProbe>(
                """{"channel":"phone","kind":"note","content":"Hi","followUpDoneAt":"2026-11-25T10:00:00Z"}""",
                InteractionJson.StrictSerializerOptions));
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<StrictCreateProbe>(
                """{"channel":"phone","kind":"note","content":"Hi","followUpResult":"Done"}""",
                InteractionJson.StrictSerializerOptions));
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<StrictCreateProbe>(
                """{"channel":"phone","channel":"web","kind":"note","content":"Hi"}""",
                InteractionJson.StrictSerializerOptions));
    }

    private sealed record StrictCreateProbe(
        string? Channel = null,
        string? Kind = null,
        string? Content = null,
        string? OccurredAt = null,
        Guid? OrderId = null,
        string? FollowUpAt = null);
}
