using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TeaPremium.Domain.Orders;

// 03-database §1 / BR-ORD-10: TP + yyMMdd (Asia/Ho_Chi_Minh) + 4 random chars excluding 0,O,1,I,L.
public static partial class OrderCode
{
    public const string Prefix = "TP";
    public const int SuffixLength = 4;
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static bool IsValid(string? code) =>
        !string.IsNullOrEmpty(code) && SchemaPattern().IsMatch(code);

    public static bool IsGeneratedFormat(string? code)
    {
        if (!IsValid(code)) return false;
        for (var i = code!.Length - SuffixLength; i < code.Length; i++)
        {
            if (!Alphabet.Contains(code[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public static string Format(DateOnly vietnamDate, ReadOnlySpan<char> suffix)
    {
        if (suffix.Length != SuffixLength)
            throw new DomainException("invalid_order_code", "Hậu tố mã đơn hàng phải gồm đúng 4 ký tự.");
        foreach (var ch in suffix)
        {
            if (!Alphabet.Contains(ch, StringComparison.Ordinal))
                throw new DomainException("invalid_order_code", "Hậu tố mã đơn hàng chứa ký tự không hợp lệ.");
        }

        return string.Create(12, (vietnamDate, suffix[0], suffix[1], suffix[2], suffix[3]), static (span, state) =>
        {
            span[0] = 'T';
            span[1] = 'P';
            state.vietnamDate.TryFormat(span.Slice(2, 6), out _, "yyMMdd", CultureInfo.InvariantCulture);
            span[8] = state.Item2;
            span[9] = state.Item3;
            span[10] = state.Item4;
            span[11] = state.Item5;
        });
    }

    public static string GenerateRandom(DateOnly vietnamDate)
    {
        Span<char> suffix = stackalloc char[SuffixLength];
        for (var i = 0; i < SuffixLength; i++)
            suffix[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return Format(vietnamDate, suffix);
    }

    [GeneratedRegex("^TP[0-9]{6}[A-Z0-9]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaPattern();
}
