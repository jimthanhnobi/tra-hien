using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using TeaPremium.Application.Time;

namespace TeaPremium.Api.Inventory;

internal sealed class OpeningPreviewToken(IDataProtectionProvider provider, IClock clock)
{
    public const string Header = "X-Inventory-Preview-Token";
    private const int DigestSize = 32;
    private const int PrefixSize = 16 + DigestSize + 8;
    private readonly IDataProtector protector = provider.CreateProtector("teapremium.inventory.opening-preview.v1");

    public string Protect(Guid staff, byte[] digest, Guid[] variants)
    {
        var payload = new byte[PrefixSize + 16 * variants.Length];
        staff.TryWriteBytes(payload);
        digest.CopyTo(payload, 16);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16 + DigestSize), clock.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
        for (var i = 0; i < variants.Length; i++)
            variants[i].TryWriteBytes(payload.AsSpan(PrefixSize + i * 16, 16));
        return WebEncoders.Base64UrlEncode(protector.Protect(payload));
    }

    public Guid[]? Verify(string? token, Guid staff, byte[] digest, int count)
    {
        if (token is not { Length: > 0 and <= 12000 }) return null;
        byte[] payload;
        try { payload = protector.Unprotect(WebEncoders.Base64UrlDecode(token)); }
        catch (Exception error) when (error is FormatException or CryptographicException) { return null; }

        if (payload.Length != PrefixSize + 16 * count || new Guid(payload.AsSpan(0, 16)) != staff
            || !CryptographicOperations.FixedTimeEquals(payload.AsSpan(16, DigestSize), digest)
            || BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(16 + DigestSize)) <= clock.UtcNow.ToUnixTimeSeconds())
            return null;

        var variants = new Guid[count];
        for (var i = 0; i < count; i++)
            variants[i] = new Guid(payload.AsSpan(PrefixSize + i * 16, 16));
        return variants;
    }
}
