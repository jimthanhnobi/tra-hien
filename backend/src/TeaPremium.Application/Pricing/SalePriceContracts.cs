using System.Buffers.Binary;
using System.Text.Json.Serialization;
using FluentValidation;
using TeaPremium.Application.Catalog;
using TeaPremium.Application.Results;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Application.Pricing;

public sealed record SalePriceView(Guid Id, [property: JsonNumberHandling(JsonNumberHandling.Strict)] long SalePriceVnd, DateTime StartsAt, DateTime EndsAt,
    string? Note, DateTime CreatedAt, string RowVersion);

public sealed record SalePriceList(string RowVersion, IReadOnlyList<SalePriceView> Items);

public sealed record ListSalePrices(Guid VariantId) : IQuery<SalePriceList>;

public sealed record CreateSalePrice(Guid VariantId, string RowVersion, long SalePriceVnd, DateTime StartsAt,
    DateTime EndsAt, string? Note, ProductActor Actor) : ICommand<SalePriceView>;

public sealed record DeleteSalePrice(Guid VariantId, Guid SalePriceId, string RowVersion, ProductActor Actor) : ICommand<bool>;

public sealed record SalePriceVariant(Guid ProductId, string ProductSlug, uint Version, bool Active, long? BasePriceVnd);

public readonly record struct SalePriceVersion(Guid Id, uint Version)
{
    public string Encode()
    {
        Span<byte> bytes = stackalloc byte[20];
        Id.TryWriteBytes(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], Version);
        return Convert.ToBase64String(bytes);
    }

    public static bool TryDecode(string? text, out SalePriceVersion version)
    {
        version = default;
        Span<byte> bytes = stackalloc byte[20];
        if (text is null || !Convert.TryFromBase64String(text, bytes, out var written) || written != bytes.Length) return false;
        version = new(new Guid(bytes[..16]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]));
        return version.Id != Guid.Empty && version.Version != 0;
    }
}

public interface ISalePriceStore
{
    Task<SalePriceList?> ListAsync(Guid variantId, CancellationToken ct);
    Task<SalePriceVariant?> LockVariantAsync(Guid variantId, CancellationToken ct);
    Task<Result<SalePriceView>> AddAsync(SalePriceVariant variant, CreateSalePrice command, DateTime now, CancellationToken ct);
    Task<Result<bool>> DeleteAsync(SalePriceVariant variant, DeleteSalePrice command, SalePriceVersion version, CancellationToken ct);
}

public static class SalePriceTime
{
    // PostgreSQL timestamptz stores whole microseconds.
    public static DateTime ToPersistedUtc(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, DateTimeKind.Utc);
}

public sealed class CreateSalePriceValidator : AbstractValidator<CreateSalePrice>
{
    public CreateSalePriceValidator()
    {
        RuleFor(x => x.RowVersion).Must(value => CatalogVersions.TryDecode(value, out _));
        RuleFor(x => x.SalePriceVnd).Must(value => ProductRules.ValidPrice(value));
        RuleFor(x => x.StartsAt).Must(IsFiniteUtc);
        RuleFor(x => x.EndsAt).Must(IsFiniteUtc).GreaterThan(x => x.StartsAt);
        RuleFor(x => x.Note).Must(CatalogInput.WithoutNul);
    }

    private static bool IsFiniteUtc(DateTime value) => value.Kind == DateTimeKind.Utc &&
        value != DateTime.MinValue && value != DateTime.MaxValue && value.Ticks % 10 == 0;
}

public sealed class DeleteSalePriceValidator : AbstractValidator<DeleteSalePrice>
{
    public DeleteSalePriceValidator() => RuleFor(x => x.RowVersion).Must(value => SalePriceVersion.TryDecode(value, out _));
}
