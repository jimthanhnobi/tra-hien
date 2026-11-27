using FluentValidation;
using TeaPremium.Application.Catalog;
using TeaPremium.Domain.Crm;

namespace TeaPremium.Application.Crm;

public sealed class PutCustomerConsentValidator : AbstractValidator<PutCustomerConsent>
{
    public PutCustomerConsentValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.Channel)
            .NotNull()
            .Must(ch => ch is not null && CustomerRules.ConsentChannels.Contains(ch))
            .WithMessage("invalid_consent_channel");
        RuleFor(x => x.Purpose)
            .NotNull()
            .Must(p => p is not null && CustomerRules.ConsentPurposes.Contains(p))
            .WithMessage("invalid_consent_purpose");
        RuleFor(x => x.Status)
            .NotNull()
            .Must(s => s is not null && CustomerRules.ConsentStatuses.Contains(s))
            .WithMessage("invalid_consent_status");
        RuleFor(x => x.Evidence)
            .NotNull()
            .Must(CatalogInput.WithoutNul)
            .Must(CustomerRules.IsValidPostgresText)
            .Must(e => e is not null && e.Trim().Length is >= CustomerRules.MinConsentEvidenceLength and <= CustomerRules.MaxConsentEvidenceLength)
            .WithMessage("invalid_consent_evidence");
        RuleFor(x => x.RowVersion)
            .Must(rv => rv is null || CatalogVersions.TryDecode(rv, out _))
            .WithMessage("invalid_row_version");
    }
}

public sealed class MergeCustomersValidator : AbstractValidator<MergeCustomers>
{
    public MergeCustomersValidator()
    {
        RuleFor(x => x.TargetCustomerId).NotEmpty();
        RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.MergeCustomerId)
            .NotEmpty()
            .WithMessage("invalid_merge_customer_id");
        RuleFor(x => x.MergeCustomerId)
            .Must((cmd, mergeId) => mergeId == Guid.Empty || mergeId != cmd.TargetCustomerId)
            .OverridePropertyName("mergeCustomerId")
            .WithMessage("self_merge_not_allowed");
        RuleFor(x => x.RowVersion)
            .Must(rv => rv is null || CatalogVersions.TryDecode(rv, out _))
            .WithMessage("invalid_row_version");
        RuleFor(x => x.MergeRowVersion)
            .Must(rv => rv is null || CatalogVersions.TryDecode(rv, out _))
            .WithMessage("invalid_merge_row_version");
    }
}

public sealed class EraseCustomerValidator : AbstractValidator<EraseCustomer>
{
    public EraseCustomerValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Actor.Id).NotEmpty();
        RuleFor(x => x.Reason)
            .NotNull()
            .Must(CatalogInput.WithoutNul)
            .Must(CustomerRules.IsValidPostgresText)
            .Must(r => r is not null && r.Trim().Length is >= CustomerRules.MinEraseReasonLength and <= CustomerRules.MaxEraseReasonLength)
            .WithMessage("invalid_erase_reason");
        RuleFor(x => x.RequestChannel)
            .NotNull()
            .Must(ch => ch is not null && CustomerRules.DataRequestChannels.Contains(ch))
            .WithMessage("invalid_request_channel");
        RuleFor(x => x.RowVersion)
            .Must(rv => rv is null || CatalogVersions.TryDecode(rv, out _))
            .WithMessage("invalid_row_version");
    }
}

public sealed class ExportCustomerDataValidator : AbstractValidator<ExportCustomerData>
{
    public ExportCustomerDataValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Actor.Id).NotEmpty();
    }
}
