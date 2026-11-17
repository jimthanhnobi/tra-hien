using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using TeaPremium.Application.Time;

namespace TeaPremium.Infrastructure.Iam;

// RFC 6238 / Identity authenticator format: base32, SHA1, 30 seconds, 6 digits.
// Must run under the SAME locked staff row and transaction as authentication/enrollment.
public sealed class StaffTotp(IClock clock) : AuthenticatorTokenProvider<StaffUser>
{
    internal const string Provider = "TeaPremium.Staff";
    internal const string LastStep = "totp.last_step";

    public override async Task<bool> ValidateAsync(string purpose, string token, UserManager<StaffUser> manager, StaffUser user)
    {
        var key = await manager.GetAuthenticatorKeyAsync(user);
        var previous = await manager.GetAuthenticationTokenAsync(user, Provider, LastStep);
        var step = Match(key, token, clock.UtcNow, previous);
        if (step is null) return false;
        var result = await manager.SetAuthenticationTokenAsync(user, Provider, LastStep, step.Value.ToString(CultureInfo.InvariantCulture));
        return result.Succeeded;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "RFC 6238 / Identity authenticator interoperability requires HMAC-SHA1; this is not password hashing.")]
    internal static long? Match(string? key, string? token, DateTimeOffset now, string? previous)
    {
        if (key is null || token is null || token.Length != 6 || token.Any(c => c is < '0' or > '9')) return null;
        var last = long.TryParse(previous, CultureInfo.InvariantCulture, out var value) ? value : -1;
        var step = now.ToUnixTimeSeconds() / 30;
        var keyBytes = Decode(key);
        try
        {
            // ±1 step for clock skew. A consumed or older step can never be reused.
            Span<byte> counter = stackalloc byte[8];
            for (var candidate = step + 1; candidate >= step - 1; candidate--)
            {
                if (candidate <= last) continue;
                BinaryPrimitives.WriteInt64BigEndian(counter, candidate);
                var hash = HMACSHA1.HashData(keyBytes, counter);
                var offset = hash[^1] & 15;
                var number = (BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & int.MaxValue) % 1000000;
                var expected = System.Text.Encoding.ASCII.GetBytes(number.ToString("D6", CultureInfo.InvariantCulture));
                if (CryptographicOperations.FixedTimeEquals(expected, System.Text.Encoding.ASCII.GetBytes(token))) return candidate;
            }
            return null;
        }
        finally { CryptographicOperations.ZeroMemory(keyBytes); }
    }

    private static byte[] Decode(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in key)
        {
            var value = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0) throw new CryptographicException("Invalid authenticator key.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes.Add((byte)(buffer >> bits));
        }
        return bytes.ToArray();
    }
}
