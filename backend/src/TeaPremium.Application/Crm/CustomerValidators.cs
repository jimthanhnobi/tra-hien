using System.Text.Json;
using FluentValidation;
using TeaPremium.Application.Catalog;
using TeaPremium.Domain.Crm;

namespace TeaPremium.Application.Crm;

internal static class CustomerFieldSets
{
    public static readonly HashSet<string> PatchAllowed = new(StringComparer.Ordinal)
    {
        "rowVersion", "fullName", "email", "zaloName", "customerGroup", "isVip",
        "referredByCustomerId", "contactStatus", "noteSummary"
    };

    public static readonly HashSet<string> PatchForbiddenIdentity = new(StringComparer.Ordinal)
    {
        "phone", "phoneE164", "origin", "acquisitionSource", "hadLegacyPurchase"
    };

    public static readonly HashSet<string> CorrectIdentityAllowed = new(StringComparer.Ordinal)
    {
        "rowVersion", "phone", "origin", "acquisitionSource", "hadLegacyPurchase", "reason"
    };

    public static readonly HashSet<string> CorrectIdentityTargets = new(StringComparer.Ordinal)
    {
        "phone", "origin", "acquisitionSource", "hadLegacyPurchase"
    };

    public static readonly HashSet<string> CorrectIdentityOwnerOnly = new(StringComparer.Ordinal)
    {
        "origin", "acquisitionSource", "hadLegacyPurchase"
    };

    public static readonly HashSet<string> PatchTagAllowed = new(StringComparer.Ordinal)
    {
        "name", "color"
    };

    public static readonly HashSet<string> PatchAddressAllowed = new(StringComparer.Ordinal)
    {
        "label", "receiverName", "receiverPhone", "provinceCode", "wardCode", "wardNameText", "addressLine", "isDefault", "rowVersion"
    };

    public static bool HasOwnerOnlyIdentityField(JsonElement fields)
        => fields.ValueKind == JsonValueKind.Object
           && fields.EnumerateObject().Any(p => CorrectIdentityOwnerOnly.Contains(p.Name));
}

public sealed class ListCustomersValidator : AbstractValidator<ListCustomers>
{
    public ListCustomersValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, int.MaxValue / 100);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Sort).Must(sort => sort is null || CustomerRules.SortOptions.Contains(sort))
            .WithMessage("invalid_sort");
        RuleFor(x => x.Q).MaximumLength(200).Must(CatalogInput.WithoutNul);
        RuleFor(x => x.Group).Must(g => g is null || CustomerRules.CustomerGroups.Contains(g))
            .WithMessage("invalid_customer_group");
        RuleFor(x => x.Tag).Must(t => t is null || t.Value != Guid.Empty)
            .WithMessage("invalid_tag");
        RuleFor(x => x.Interest).Must(v => CustomerFilterParser.TryParseInterest(v, out _, out _, out _))
            .WithMessage("invalid_interest");
        RuleFor(x => x.LastDeliveredBefore).Must(v => CustomerFilterParser.TryParseDateFilter(v, out _))
            .WithMessage("invalid_date");
        RuleFor(x => x.LastDeliveredAfter).Must(v => CustomerFilterParser.TryParseDateFilter(v, out _))
            .WithMessage("invalid_date");
        RuleFor(x => x.MinLtv).Must(v => v is null || v.Value >= 0)
            .WithMessage("invalid_min_ltv");
        RuleFor(x => x.ContactStatus).Must(s => s is null || CustomerRules.ContactStatuses.Contains(s))
            .WithMessage("invalid_contact_status");
        RuleFor(x => x.HasConsent).Must(v => CustomerFilterParser.TryParseConsent(v, out _, out _))
            .WithMessage("invalid_consent_filter");
    }
}

public sealed class GetCustomerValidator : AbstractValidator<GetCustomer>
{
    public GetCustomerValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}

public sealed class ListCustomerOrdersValidator : AbstractValidator<ListCustomerOrders>
{
    public ListCustomerOrdersValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Page).InclusiveBetween(1, int.MaxValue / 100);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class CreateCustomerValidator : AbstractValidator<CreateCustomer>
{
    public CreateCustomerValidator()
    {
        RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.FullName)
            .NotNull()
            .Must(CatalogInput.WithoutNul)
            .Must(v => v is not null && v.Trim().Length is >= 1 and <= 150)
            .WithMessage("invalid_full_name");
        RuleFor(x => x.Phone)
            .Must(CatalogInput.WithoutNul)
            .MaximumLength(30);
        RuleFor(x => x.Email)
            .Must(CatalogInput.WithoutNul)
            .Must(e => e is null || CustomerRules.IsValidEmail(e.Trim()))
            .WithMessage("invalid_email");
        RuleFor(x => x.ZaloName)
            .Must(CatalogInput.WithoutNul)
            .Must(z => z is null || z.Trim().Length is >= 1 and <= 150)
            .WithMessage("invalid_zalo_name");
        RuleFor(x => x.CustomerGroup)
            .Must(g => g is null || CustomerRules.CustomerGroups.Contains(g))
            .WithMessage("invalid_customer_group");
        RuleFor(x => x.AcquisitionSource)
            .Must(s => s is null || CustomerRules.AcquisitionSources.Contains(s))
            .WithMessage("invalid_acquisition_source");
        RuleFor(x => x.ReferredByCustomerId)
            .Must(r => r is null || r.Value != Guid.Empty)
            .WithMessage("invalid_referred_by");
        RuleFor(x => x.ContactStatus)
            .Must(s => s is null || CustomerRules.WritableContactStatuses.Contains(s))
            .WithMessage("invalid_contact_status");
        RuleFor(x => x.NoteSummary)
            .Must(CatalogInput.WithoutNul)
            .Must(n => n is null || n.Trim().Length <= 2000)
            .WithMessage("invalid_note_summary");
        RuleFor(x => x.TagIds)
            .Must(ids => ids is null || (ids.Length <= 50 && ids.All(id => id != Guid.Empty)))
            .WithMessage("invalid_tag_ids");
    }
}

public sealed class PatchCustomerValidator : AbstractValidator<PatchCustomer>
{
    public PatchCustomerValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Actor.Id).NotEmpty();
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

            var hasRowVersion = false;
            foreach (var property in fields.EnumerateObject())
            {
                if (CustomerFieldSets.PatchForbiddenIdentity.Contains(property.Name))
                {
                    context.AddFailure(property.Name, "identity_field_requires_correction");
                    continue;
                }

                if (!CustomerFieldSets.PatchAllowed.Contains(property.Name))
                {
                    context.AddFailure(property.Name, "unknown_field");
                    continue;
                }

                switch (property.Name)
                {
                    case "rowVersion":
                        hasRowVersion = true;
                        if (property.Value.ValueKind != JsonValueKind.String
                            || !CatalogVersions.TryDecode(property.Value.GetString(), out _))
                        {
                            context.AddFailure("rowVersion", "invalid_row_version");
                        }
                        break;
                    case "fullName":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || property.Value.GetString()!.Trim().Length is < 1 or > 150)
                        {
                            context.AddFailure("fullName", "invalid_full_name");
                        }
                        break;
                    case "email":
                        if (property.Value.ValueKind != JsonValueKind.Null
                            && (property.Value.ValueKind != JsonValueKind.String
                                || !CustomerRules.IsValidEmail(property.Value.GetString()!.Trim())))
                        {
                            context.AddFailure("email", "invalid_email");
                        }
                        break;
                    case "zaloName":
                        if (property.Value.ValueKind != JsonValueKind.Null
                            && (property.Value.ValueKind != JsonValueKind.String
                                || property.Value.GetString()!.Trim().Length is < 1 or > 150))
                        {
                            context.AddFailure("zaloName", "invalid_zalo_name");
                        }
                        break;
                    case "customerGroup":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || !CustomerRules.CustomerGroups.Contains(property.Value.GetString()!))
                        {
                            context.AddFailure("customerGroup", "invalid_customer_group");
                        }
                        break;
                    case "isVip":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            context.AddFailure("isVip", "invalid_is_vip");
                        }
                        break;
                    case "referredByCustomerId":
                        if (property.Value.ValueKind != JsonValueKind.Null
                            && (property.Value.ValueKind != JsonValueKind.String
                                || !Guid.TryParseExact(property.Value.GetString(), "D", out var refId)
                                || refId == Guid.Empty))
                        {
                            context.AddFailure("referredByCustomerId", "invalid_referred_by");
                        }
                        break;
                    case "contactStatus":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || !CustomerRules.WritableContactStatuses.Contains(property.Value.GetString()!))
                        {
                            context.AddFailure("contactStatus", "invalid_contact_status");
                        }
                        break;
                    case "noteSummary":
                        if (property.Value.ValueKind != JsonValueKind.Null
                            && (property.Value.ValueKind != JsonValueKind.String
                                || property.Value.GetString()!.Trim().Length > 2000))
                        {
                            context.AddFailure("noteSummary", "invalid_note_summary");
                        }
                        break;
                }
            }

            if (!hasRowVersion)
            {
                context.AddFailure("rowVersion", "row_version_required");
            }
        });
    }
}

public sealed class CorrectCustomerIdentityValidator : AbstractValidator<CorrectCustomerIdentity>
{
    public CorrectCustomerIdentityValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Actor.Id).NotEmpty();
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

            var hasRowVersion = false;
            var hasReason = false;
            var hasTarget = false;

            foreach (var property in fields.EnumerateObject())
            {
                if (!CustomerFieldSets.CorrectIdentityAllowed.Contains(property.Name))
                {
                    context.AddFailure(property.Name, "unknown_field");
                    continue;
                }

                if (CustomerFieldSets.CorrectIdentityTargets.Contains(property.Name))
                {
                    hasTarget = true;
                }

                switch (property.Name)
                {
                    case "rowVersion":
                        hasRowVersion = true;
                        if (property.Value.ValueKind != JsonValueKind.String
                            || !CatalogVersions.TryDecode(property.Value.GetString(), out _))
                        {
                            context.AddFailure("rowVersion", "invalid_row_version");
                        }
                        break;
                    case "reason":
                        hasReason = true;
                        if (property.Value.ValueKind != JsonValueKind.String
                            || property.Value.GetString()!.Trim().Length is < 3 or > 500)
                        {
                            context.AddFailure("reason", "invalid_reason");
                        }
                        break;
                    case "phone":
                        if (property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                        {
                            context.AddFailure("phone", "invalid_phone");
                        }
                        else if (property.Value.ValueKind == JsonValueKind.String
                            && property.Value.GetString()!.Length > 30)
                        {
                            context.AddFailure("phone", "invalid_phone");
                        }
                        break;
                    case "origin":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || !CustomerRules.Origins.Contains(property.Value.GetString()!))
                        {
                            context.AddFailure("origin", "invalid_origin");
                        }
                        break;
                    case "acquisitionSource":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || !CustomerRules.AcquisitionSources.Contains(property.Value.GetString()!))
                        {
                            context.AddFailure("acquisitionSource", "invalid_acquisition_source");
                        }
                        break;
                    case "hadLegacyPurchase":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            context.AddFailure("hadLegacyPurchase", "invalid_had_legacy_purchase");
                        }
                        break;
                }
            }

            if (!hasRowVersion)
            {
                context.AddFailure("rowVersion", "row_version_required");
            }

            if (!hasReason)
            {
                context.AddFailure("reason", "reason_required");
            }

            if (!hasTarget)
            {
                context.AddFailure("body", "identity_field_required");
            }
        });
    }
}

public sealed class CreateTagValidator : AbstractValidator<CreateTag>
{
    public CreateTagValidator()
    {
        RuleFor(x => x.Name)
            .NotNull()
            .Must(CatalogInput.WithoutNul)
            .Must(v => v is not null && v.Trim().Length is >= 1 and <= 60)
            .WithMessage("invalid_tag_name");
        RuleFor(x => x.Color)
            .Must(CatalogInput.WithoutNul)
            .Must(c => c is null || CustomerRules.IsValidHexColor(c))
            .WithMessage("invalid_tag_color");
    }
}

public sealed class PatchTagValidator : AbstractValidator<PatchTag>
{
    public PatchTagValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
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
                if (!CustomerFieldSets.PatchTagAllowed.Contains(property.Name))
                {
                    context.AddFailure(property.Name, "unknown_field");
                    continue;
                }

                hasField = true;
                if (property.Name == "name")
                {
                    if (property.Value.ValueKind != JsonValueKind.String
                        || property.Value.GetString()!.Trim().Length is < 1 or > 60)
                    {
                        context.AddFailure("name", "invalid_tag_name");
                    }
                }
                else if (property.Name == "color")
                {
                    if (property.Value.ValueKind != JsonValueKind.Null
                        && (property.Value.ValueKind != JsonValueKind.String
                            || !CustomerRules.IsValidHexColor(property.Value.GetString())))
                    {
                        context.AddFailure("color", "invalid_tag_color");
                    }
                }
            }

            if (!hasField)
            {
                context.AddFailure("body", "empty_patch");
            }
        });
    }
}

public sealed class PutCustomerTagsValidator : AbstractValidator<PutCustomerTags>
{
    public PutCustomerTagsValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.TagIds)
            .NotNull()
            .Must(ids => ids is not null && ids.Length <= 50 && ids.All(id => id != Guid.Empty))
            .WithMessage("invalid_tag_ids");
        RuleFor(x => x.RowVersion)
            .Must(rv => rv is null || CatalogVersions.TryDecode(rv, out _))
            .WithMessage("invalid_row_version");
    }
}

public sealed class ListCustomerAddressesValidator : AbstractValidator<ListCustomerAddresses>
{
    public ListCustomerAddressesValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
    }
}

public sealed class GetCustomerAddressValidator : AbstractValidator<GetCustomerAddress>
{
    public GetCustomerAddressValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.AddressId).NotEmpty();
    }
}

public sealed class CreateCustomerAddressValidator : AbstractValidator<CreateCustomerAddress>
{
    public CreateCustomerAddressValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Label)
            .Must(CatalogInput.WithoutNul)
            .Must(l => l is null || l.Trim().Length <= 80)
            .WithMessage("invalid_label");
        RuleFor(x => x.ReceiverName)
            .NotNull()
            .Must(CatalogInput.WithoutNul)
            .Must(v => v is not null && v.Trim().Length is >= 1 and <= 150)
            .WithMessage("invalid_receiver_name");
        RuleFor(x => x.ReceiverPhone)
            .NotNull()
            .Must(CatalogInput.WithoutNul)
            .Must(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 30)
            .WithMessage("invalid_phone");
        RuleFor(x => x.ProvinceCode)
            .NotNull()
            .Must(CustomerRules.IsValidProvinceCode)
            .WithMessage("invalid_province_code");
        RuleFor(x => x.WardCode)
            .Must(w => w is null || CustomerRules.IsValidWardCode(w))
            .WithMessage("invalid_ward_code");
        RuleFor(x => x.WardNameText)
            .Must(CatalogInput.WithoutNul)
            .Must(w => w is null || w.Trim().Length <= 150)
            .WithMessage("invalid_ward_name_text");
        RuleFor(x => x)
            .Must(x => x.WardCode is not null || !string.IsNullOrWhiteSpace(x.WardNameText))
            .WithName("wardCode")
            .WithMessage("ward_required");
        RuleFor(x => x.AddressLine)
            .NotNull()
            .Must(CatalogInput.WithoutNul)
            .Must(v => v is not null && v.Trim().Length is >= 1 and <= 255)
            .WithMessage("invalid_address_line");
        RuleFor(x => x.RowVersion)
            .Must(rv => rv is null || CatalogVersions.TryDecode(rv, out _))
            .WithMessage("invalid_row_version");
    }
}

public sealed class PatchCustomerAddressValidator : AbstractValidator<PatchCustomerAddress>
{
    public PatchCustomerAddressValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.AddressId).NotEmpty();
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

            var hasMutableField = false;
            foreach (var property in fields.EnumerateObject())
            {
                if (!CustomerFieldSets.PatchAddressAllowed.Contains(property.Name))
                {
                    context.AddFailure(property.Name, "unknown_field");
                    continue;
                }

                if (property.Name != "rowVersion")
                {
                    hasMutableField = true;
                }

                switch (property.Name)
                {
                    case "rowVersion":
                        if (property.Value.ValueKind != JsonValueKind.Null
                            && (property.Value.ValueKind != JsonValueKind.String
                                || !CatalogVersions.TryDecode(property.Value.GetString(), out _)))
                        {
                            context.AddFailure("rowVersion", "invalid_row_version");
                        }
                        break;
                    case "label":
                        if (property.Value.ValueKind != JsonValueKind.Null
                            && (property.Value.ValueKind != JsonValueKind.String
                                || property.Value.GetString()!.Trim().Length > 80))
                        {
                            context.AddFailure("label", "invalid_label");
                        }
                        break;
                    case "receiverName":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || property.Value.GetString()!.Trim().Length is < 1 or > 150)
                        {
                            context.AddFailure("receiverName", "invalid_receiver_name");
                        }
                        break;
                    case "receiverPhone":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(property.Value.GetString())
                            || property.Value.GetString()!.Length > 30)
                        {
                            context.AddFailure("receiverPhone", "invalid_phone");
                        }
                        break;
                    case "provinceCode":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || !CustomerRules.IsValidProvinceCode(property.Value.GetString()))
                        {
                            context.AddFailure("provinceCode", "invalid_province_code");
                        }
                        break;
                    case "wardCode":
                        if (property.Value.ValueKind != JsonValueKind.Null
                            && (property.Value.ValueKind != JsonValueKind.String
                                || !CustomerRules.IsValidWardCode(property.Value.GetString())))
                        {
                            context.AddFailure("wardCode", "invalid_ward_code");
                        }
                        break;
                    case "wardNameText":
                        if (property.Value.ValueKind != JsonValueKind.Null
                            && (property.Value.ValueKind != JsonValueKind.String
                                || property.Value.GetString()!.Trim().Length > 150))
                        {
                            context.AddFailure("wardNameText", "invalid_ward_name_text");
                        }
                        break;
                    case "addressLine":
                        if (property.Value.ValueKind != JsonValueKind.String
                            || property.Value.GetString()!.Trim().Length is < 1 or > 255)
                        {
                            context.AddFailure("addressLine", "invalid_address_line");
                        }
                        break;
                    case "isDefault":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            context.AddFailure("isDefault", "invalid_is_default");
                        }
                        break;
                }
            }

            if (!hasMutableField)
            {
                context.AddFailure("body", "empty_patch");
            }
        });
    }
}

public sealed class DeleteCustomerAddressValidator : AbstractValidator<DeleteCustomerAddress>
{
    public DeleteCustomerAddressValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.AddressId).NotEmpty();
        RuleFor(x => x.RowVersion)
            .Must(rv => rv is null || CatalogVersions.TryDecode(rv, out _))
            .WithMessage("invalid_row_version");
    }
}
