using FluentValidation;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Application.Iam;

public sealed record StaffView(Guid Id, string Email, string DisplayName, bool IsActive,
    bool MustChangePassword, bool TotpEnabled, string RowVersion, string[] Roles, string[] Permissions);
public sealed record StaffPage(StaffView[] Items, int Page, int PageSize, int TotalItems);
// Rejected authentication is a successful command outcome: failure counters MUST commit.
public sealed record LoginOutcome(StaffView? User, string? SecurityStamp, bool FactorComplete);
public sealed record TotpSetup(string SharedKey, string ProvisioningUri);
public sealed record LoginStaff(string Email, string Password, string? TotpCode) : ICommand<LoginOutcome>;
public sealed record ChangeStaffPassword(Guid Id, string CurrentPassword, string NewPassword) : ICommand<bool>;
public sealed record SetupStaffTotp(Guid Id) : ICommand<TotpSetup>;
public sealed record VerifyStaffTotp(Guid Id, string Code) : ICommand<bool>;
public sealed record CreateStaff(string Email, string DisplayName, string InitialPassword, string[] Roles,
    Guid ActorId, string ActorStamp) : ICommand<StaffView>;
public sealed record PatchStaff(Guid Id, string RowVersion, bool? IsActive, bool? Locked,
    string[]? Roles, string? NewPassword, Guid ActorId, string ActorStamp) : ICommand<StaffView>;
public sealed record BootstrapOwner(string Email, string Password) : ICommand<bool>;
public sealed record StaffSession(StaffView User, string SecurityStamp);

// This adapter owns framework Identity operations; no EF/Identity types cross the port.
public interface IStaffIdentity
{
    Task<Result<LoginOutcome>> LoginAsync(LoginStaff command, CancellationToken ct);
    Task<Result<bool>> ChangePasswordAsync(ChangeStaffPassword command, CancellationToken ct);
    Task<Result<TotpSetup>> SetupTotpAsync(SetupStaffTotp command, CancellationToken ct);
    Task<Result<bool>> VerifyTotpAsync(VerifyStaffTotp command, CancellationToken ct);
    Task<Result<StaffView>> CreateAsync(CreateStaff command, CancellationToken ct);
    Task<Result<StaffView>> PatchAsync(PatchStaff command, CancellationToken ct);
    Task<Result<bool>> BootstrapAsync(BootstrapOwner command, CancellationToken ct);
    Task<StaffSession?> SessionAsync(Guid id, CancellationToken ct);
    Task<StaffPage> ListAsync(int page, int pageSize, CancellationToken ct);
    Task<StaffView?> FindAsync(Guid id, CancellationToken ct);
}

public sealed class StaffHandlers(IStaffIdentity identity) :
    ICommandHandler<LoginStaff, LoginOutcome>, ICommandHandler<ChangeStaffPassword, bool>,
    ICommandHandler<SetupStaffTotp, TotpSetup>, ICommandHandler<VerifyStaffTotp, bool>,
    ICommandHandler<CreateStaff, StaffView>, ICommandHandler<PatchStaff, StaffView>,
    ICommandHandler<BootstrapOwner, bool>
{
    public Task<Result<LoginOutcome>> Handle(LoginStaff command, CancellationToken cancellationToken) => identity.LoginAsync(command, cancellationToken);
    public Task<Result<bool>> Handle(ChangeStaffPassword command, CancellationToken cancellationToken) => identity.ChangePasswordAsync(command, cancellationToken);
    public Task<Result<TotpSetup>> Handle(SetupStaffTotp command, CancellationToken cancellationToken) => identity.SetupTotpAsync(command, cancellationToken);
    public Task<Result<bool>> Handle(VerifyStaffTotp command, CancellationToken cancellationToken) => identity.VerifyTotpAsync(command, cancellationToken);
    public Task<Result<StaffView>> Handle(CreateStaff command, CancellationToken cancellationToken) => identity.CreateAsync(command, cancellationToken);
    public Task<Result<StaffView>> Handle(PatchStaff command, CancellationToken cancellationToken) => identity.PatchAsync(command, cancellationToken);
    public Task<Result<bool>> Handle(BootstrapOwner command, CancellationToken cancellationToken) => identity.BootstrapAsync(command, cancellationToken);
}

public sealed class LoginStaffValidator : AbstractValidator<LoginStaff>
{
    public LoginStaffValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(1024);
        RuleFor(x => x.TotpCode).Matches("^[0-9]{6}$").When(x => x.TotpCode is not null);
    }
}

public sealed class CreateStaffValidator : AbstractValidator<CreateStaff>
{
    public CreateStaffValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.InitialPassword).NotEmpty().MaximumLength(1024);
        RuleFor(x => x.Roles).NotNull().Must(StaffPermissions.ValidRoles);
    }
}

public sealed class PatchStaffValidator : AbstractValidator<PatchStaff>
{
    public PatchStaffValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.RowVersion).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Roles).Must(x => x is null || StaffPermissions.ValidRoles(x));
        RuleFor(x => x.NewPassword).NotEmpty().MaximumLength(1024).When(x => x.NewPassword is not null);
    }
}

public sealed class ChangeStaffPasswordValidator : AbstractValidator<ChangeStaffPassword>
{
    public ChangeStaffPasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(1024);
        RuleFor(x => x.NewPassword).NotEmpty().MaximumLength(1024).NotEqual(x => x.CurrentPassword);
    }
}

public sealed class VerifyStaffTotpValidator : AbstractValidator<VerifyStaffTotp>
{
    public VerifyStaffTotpValidator() => RuleFor(x => x.Code).NotNull().Matches("^[0-9]{6}$");
}

public sealed class BootstrapOwnerValidator : AbstractValidator<BootstrapOwner>
{
    public BootstrapOwnerValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(1024);
    }
}
