using TeaPremium.Api.Middleware;
using TeaPremium.Application.AdministrativeData;
using TeaPremium.Application.Results;

namespace TeaPremium.Api.AdministrativeData;

// GAP-P214-01 = A (Owner-approved 2026-11-27): 2 public read-only administrative endpoints (0 migration, 0 PII).
public static class StoreAdministrativeEndpoints
{
    public static void MapStoreAdministrativeData(this IEndpointRouteBuilder endpoints)
    {
        var store = endpoints.MapGroup("/api/v1/store")
            .WithTags("Store administrative data")
            .AllowAnonymous();

        // 1. GET /api/v1/store/provinces
        store.MapGet("/provinces", async (
            HttpContext http,
            IAdministrativeReferenceData adminRef,
            CancellationToken ct) =>
        {
            if (!await adminRef.IsReadyAsync(ct))
                return Error.BusinessRule("administrative_data_not_ready", "Danh mục địa giới hành chính chưa sẵn sàng.").ToProblem(http);

            var items = await adminRef.ListActiveProvincesAsync(ct);
            return Results.Ok(new StoreProvinceListView(items));
        })
        .Produces<StoreProvinceListView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // 2. GET /api/v1/store/provinces/{code}/wards
        store.MapGet("/provinces/{code}/wards", async (
            string code,
            HttpContext http,
            IAdministrativeReferenceData adminRef,
            CancellationToken ct) =>
        {
            if (!AdministrativeProvinceCode.TryNormalize(code, out var normalizedCode))
                return new Error("validation_failed", "Mã tỉnh/thành phố không hợp lệ.", ErrorType.Validation).ToProblem(http);

            if (!await adminRef.IsReadyAsync(ct))
                return Error.BusinessRule("administrative_data_not_ready", "Danh mục địa giới hành chính chưa sẵn sàng.").ToProblem(http);

            var result = await adminRef.ListActiveWardsByProvinceAsync(normalizedCode, ct);
            return result is null
                ? new Error("not_found", "Không tìm thấy tỉnh/thành phố.", ErrorType.NotFound).ToProblem(http)
                : Results.Ok(result);
        })
        .Produces<StoreWardListView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }
}
