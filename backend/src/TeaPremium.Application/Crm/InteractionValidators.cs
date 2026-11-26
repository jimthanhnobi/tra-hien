using System.Text.Json;
using FluentValidation;
using TeaPremium.Application.Catalog;
using TeaPremium.Domain.Crm;

namespace TeaPremium.Application.Crm;

internal static class InteractionFieldSets
{
    public static readonly HashSet<string> PatchAllowed = new(StringComparer.Ordinal)
    {
        "channel", "kind", "content", "occurredAt", "orderId", "followUpAt"
    };
}

public sealed class ListCustomerInteractionsValidator : AbstractValidator<ListCustomerInteractions>
{
    public ListCustomerInteractionsValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty().OverridePropertyName("customerId").WithMessage("invalid_customer_id");
        RuleFor(x => x.Page).InclusiveBetween(1, int.MaxValue / 100).OverridePropertyName("page").WithMessage("invalid_page");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).OverridePropertyName("pageSize").WithMessage("invalid_page_size");
    }
}

public sealed class ListFollowUpsValidator : AbstractValidator<ListFollowUps>
{
    public ListFollowUpsValidator()
    {
        RuleFor(x => x.Due)
            .Must(due => due is null || InteractionRules.FollowUpDueModes.Contains(due))
            .OverridePropertyName("due")
            .WithMessage("invalid_due_filter");
        RuleFor(x => x.Page).InclusiveBetween(1, int.MaxValue / 100).OverridePropertyName("page").WithMessage("invalid_page");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).OverridePropertyName("pageSize").WithMessage("invalid_page_size");
    }
}

public sealed class CreateCustomerInteractionValidator : AbstractValidator<CreateCustomerInteraction>
{
    public CreateCustomerInteractionValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty().OverridePropertyName("customerId").WithMessage("invalid_customer_id");
        RuleFor(x => x.Actor.Id).NotEmpty().OverridePropertyName("actor").WithMessage("invalid_actor");
        RuleFor(x => x.Channel)
            .Must(c => c is not null && InteractionRules.Channels.Contains(c))
            .OverridePropertyName("channel")
            .WithMessage("invalid_channel");
        RuleFor(x => x.Kind)
            .Must(k => k is not null && InteractionRules.ManualKinds.Contains(k))
            .OverridePropertyName("kind")
            .WithMessage("invalid_interaction_kind");
        RuleFor(x => x.Content)
            .Must(InteractionRules.IsValidText)
            .OverridePropertyName("content")
            .WithMessage("invalid_content");
        RuleFor(x => x.OccurredAt)
            .Must(v => v is null || InteractionTimestampParser.TryParseUtc(v, out _))
            .OverridePropertyName("occurredAt")
            .WithMessage("invalid_occurred_at");
        RuleFor(x => x.OrderId)
            .Must(id => id is null || id.Value != Guid.Empty)
            .OverridePropertyName("orderId")
            .WithMessage("invalid_order_id");
        RuleFor(x => x.FollowUpAt)
            .Must(v => v is null || InteractionTimestampParser.TryParseUtc(v, out _))
            .OverridePropertyName("followUpAt")
            .WithMessage("invalid_follow_up_at");
    }
}

public sealed class PatchCustomerInteractionValidator : AbstractValidator<PatchCustomerInteraction>
{
    public PatchCustomerInteractionValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty().OverridePropertyName("customerId").WithMessage("invalid_customer_id");
        RuleFor(x => x.InteractionId).NotEmpty().OverridePropertyName("interactionId").WithMessage("invalid_interaction_id");
        RuleFor(x => x.Actor.Id).NotEmpty().OverridePropertyName("actor").WithMessage("invalid_actor");
        RuleFor(x => x.Fields).Custom((fields, context) =>
        {
            if (fields.ValueKind != JsonValueKind.Object)
            {
                context.AddFailure("body", "invalid_body");
                return;
            }

            if (!CatalogInput.WithoutNul(fields))
            {
                context.AddFailure("body", "nul_not_allowed");
                return;
            }

            var hasField = false;
            foreach (var property in fields.EnumerateObject())
            {
                if (!InteractionFieldSets.PatchAllowed.Contains(property.Name))
                {
                    context.AddFailure(property.Name, "unknown_field");
                    continue;
                }

                hasField = true;
                switch (property.Name)
                {
                    case "channel":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || !InteractionRules.Channels.Contains(property.Value.GetString()!))
                        {
                            context.AddFailure("channel", "invalid_channel");
                        }
                        break;
                    case "kind":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || !InteractionRules.ManualKinds.Contains(property.Value.GetString()!))
                        {
                            context.AddFailure("kind", "invalid_interaction_kind");
                        }
                        break;
                    case "content":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || !InteractionRules.IsValidText(property.Value.GetString()))
                        {
                            context.AddFailure("content", "invalid_content");
                        }
                        break;
                    case "occurredAt":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || !InteractionTimestampParser.TryParseUtc(property.Value.GetString(), out _))
                        {
                            context.AddFailure("occurredAt", "invalid_occurred_at");
                        }
                        break;
                    case "orderId":
                        if (property.Value.ValueKind != JsonValueKind.Null
                            && (property.Value.ValueKind != JsonValueKind.String
                                || !Guid.TryParseExact(property.Value.GetString(), "D", out var orderId)
                                || orderId == Guid.Empty))
                        {
                            context.AddFailure("orderId", "invalid_order_id");
                        }
                        break;
                    case "followUpAt":
                        if (property.Value.ValueKind != JsonValueKind.Null
                            && (property.Value.ValueKind != JsonValueKind.String
                                || !InteractionTimestampParser.TryParseUtc(property.Value.GetString(), out _)))
                        {
                            context.AddFailure("followUpAt", "invalid_follow_up_at");
                        }
                        break;
                }
            }

            if (!hasField)
            {
                context.AddFailure("body", "empty_patch");
            }
        });
    }
}

public sealed class CompleteInteractionFollowUpValidator : AbstractValidator<CompleteInteractionFollowUp>
{
    public CompleteInteractionFollowUpValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty().OverridePropertyName("customerId").WithMessage("invalid_customer_id");
        RuleFor(x => x.InteractionId).NotEmpty().OverridePropertyName("interactionId").WithMessage("invalid_interaction_id");
        RuleFor(x => x.Actor.Id).NotEmpty().OverridePropertyName("actor").WithMessage("invalid_actor");
        RuleFor(x => x.Result)
            .Cascade(CascadeMode.Stop)
            .Must(r => !string.IsNullOrWhiteSpace(r))
            .WithMessage("follow_up_result_required")
            .Must(InteractionRules.IsValidText)
            .WithMessage("invalid_follow_up_result")
            .OverridePropertyName("result");
    }
}
