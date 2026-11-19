using FluentValidation;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Application.Media;

// P1-02 / FR-CAT-06, FR-RET-01 / 02 §5.10, 05 §4.2. Actor permissions come from validated IAM.
public sealed record MediaActor(Guid Id, string[] Permissions)
{
    public bool Has(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);
    public bool CanWrite(string purpose) => Has(purpose switch
    {
        "catalog" => "catalog.write",
        "content" or "branding" => "content.write",
        "evidence" => "returns.write",
        _ => ""
    });
}
public sealed record CreateUpload(string FileName, string MimeType, long ByteSize, string Purpose, string? SlugHint, MediaActor Actor) : ICommand<UploadIntent>;
public sealed record CompleteUpload(Guid Id, MediaActor Actor) : ICommand<AssetView>;
public sealed record ListMedia(string? Q, string? Purpose, int Page, int PageSize, MediaActor Actor) : IQuery<MediaPage>;
public sealed record UploadIntent(Guid UploadId, string PutUrl, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);
// Storage keys, original filenames and actor IDs are deliberately absent from responses.
public sealed record AssetView(Guid Id, string MimeType, long ByteSize, int? Width, int? Height,
    string? ChecksumSha256, string? DefaultAlt, string Purpose, DateTimeOffset CreatedAt, string Url, DateTimeOffset? UrlExpiresAt);
public sealed record MediaPage(IReadOnlyList<AssetView> Items, int Page, int PageSize, long TotalItems);
public interface IMediaWorkflow
{
    Task<Result<UploadIntent>> CreateAsync(CreateUpload command, CancellationToken ct);
    Task<Result<AssetView>> CompleteAsync(CompleteUpload command, CancellationToken ct);
    Task<Result<MediaPage>> ListAsync(ListMedia query, CancellationToken ct);
}
public sealed class CreateUploadHandler(IMediaWorkflow workflow) : ICommandHandler<CreateUpload, UploadIntent>
{
    public Task<Result<UploadIntent>> Handle(CreateUpload command, CancellationToken cancellationToken) => workflow.CreateAsync(command, cancellationToken);
}
public sealed class CompleteUploadHandler(IMediaWorkflow workflow) : ICommandHandler<CompleteUpload, AssetView>
{
    public Task<Result<AssetView>> Handle(CompleteUpload command, CancellationToken cancellationToken) => workflow.CompleteAsync(command, cancellationToken);
}
public sealed class ListMediaHandler(IMediaWorkflow workflow) : IQueryHandler<ListMedia, MediaPage>
{
    public Task<Result<MediaPage>> Handle(ListMedia query, CancellationToken cancellationToken) => workflow.ListAsync(query, cancellationToken);
}
public static class MediaRules
{
    public static bool Purpose(string? value) => value is "catalog" or "content" or "branding" or "evidence";
    public static bool Size(string? mime, string? purpose, long bytes) => bytes > 0 && (mime switch
    {
        "image/jpeg" or "image/png" or "image/webp" or "image/avif" => bytes <= 15 * 1024 * 1024,
        "video/mp4" or "video/quicktime" => purpose == "evidence" && bytes <= 50 * 1024 * 1024,
        _ => false
    });
}
public sealed class CreateUploadValidator : AbstractValidator<CreateUpload>
{
    public CreateUploadValidator()
    {
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(255).Must(x => x is not null && !x.Any(char.IsControl) && !x.Contains('/') && !x.Contains('\\'));
        RuleFor(x => x.Purpose).Must(MediaRules.Purpose);
        RuleFor(x => x.ByteSize).Must((x, bytes) => MediaRules.Size(x.MimeType, x.Purpose, bytes));
        RuleFor(x => x.SlugHint).MaximumLength(80).Matches("\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z").When(x => x.SlugHint is not null);
        RuleFor(x => x.Actor.Id).NotEmpty();
    }
}
public sealed class CompleteUploadValidator : AbstractValidator<CompleteUpload>
{
    public CompleteUploadValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Actor.Id).NotEmpty(); }
}
public sealed class ListMediaValidator : AbstractValidator<ListMedia>
{
    public ListMediaValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, int.MaxValue / 100); RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Purpose).Must(MediaRules.Purpose).When(x => x.Purpose is not null);
        RuleFor(x => x.Q).MaximumLength(100);
    }
}
