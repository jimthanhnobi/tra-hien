using System.Security.Claims;
using System.Text.Json.Serialization;
using TeaPremium.Api.Iam;
using TeaPremium.Api.Middleware;
using TeaPremium.Application.Media;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Media;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UploadIntentRequest(string FileName, string MimeType, long ByteSize, string Purpose, string? SlugHint = null);

public static class MediaEndpoints
{
    public static void MapMedia(this IEndpointRouteBuilder endpoints)
    {
        var media = endpoints.MapGroup("/api/v1/admin/media").WithTags("Media").RequireAuthorization(AdminSecurity.Ready).RequireRateLimiting("media");
        media.MapPost("/upload-intents", async (UploadIntentRequest request, HttpContext http, ICommandHandler<CreateUpload, UploadIntent> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(request.FileName, request.MimeType, request.ByteSize, request.Purpose, request.SlugHint, Actor(http)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        }).Produces<UploadIntent>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
        media.MapPost("/upload-intents/{id:guid}/complete", async (Guid id, HttpContext http, ICommandHandler<CompleteUpload, AssetView> handler, CancellationToken ct) =>
        {
            var result = await handler.Handle(new(id, Actor(http)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        }).Produces<AssetView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(422).ProducesProblem(429).ProducesProblem(503);
        media.MapGet("", async (HttpContext http, IQueryHandler<ListMedia, MediaPage> handler, CancellationToken ct,
            string? q = null, string? purpose = null, int page = 1, int pageSize = 20) =>
        {
            var result = await handler.Handle(new(q, purpose, page, pageSize, Actor(http)), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem(http);
        }).RequireAuthorization("catalog.read").Produces<MediaPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
    }
    private static MediaActor Actor(HttpContext http) => new(Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!),
        http.User.FindAll("permission").Select(x => x.Value).ToArray());
}
