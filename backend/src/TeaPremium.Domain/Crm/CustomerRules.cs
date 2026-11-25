using System.Globalization;

namespace TeaPremium.Domain.Crm;

public static class CustomerRules
{
    // BR-CRM-11 (P2-12): anonymized sentinel matches DB regex ^\+84[0-9]{9}$ while remaining rejected as user input.
    public const string AnonymizedOrderPhone = "+84000000000";

    public static readonly IReadOnlySet<string> CustomerGroups = new HashSet<string>(StringComparer.Ordinal)
    {
        "retail", "wholesale", "reseller"
    };

    public static readonly IReadOnlySet<string> Origins = new HashSet<string>(StringComparer.Ordinal)
    {
        "legacy_import", "web", "admin"
    };

    public static readonly IReadOnlySet<string> AcquisitionSources = new HashSet<string>(StringComparer.Ordinal)
    {
        "unknown", "legacy", "google_organic", "google_ads", "facebook",
        "zalo_personal", "zalo_oa", "referral", "direct", "offline", "other"
    };

    public static readonly IReadOnlySet<string> ContactStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "active", "do_not_contact", "erased"
    };

    public static readonly IReadOnlySet<string> WritableContactStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "active", "do_not_contact"
    };

    public static readonly IReadOnlySet<string> InterestTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "material", "artisan", "product_type", "category", "price_band"
    };

    public static readonly IReadOnlySet<string> PriceBands = new HashSet<string>(StringComparer.Ordinal)
    {
        "under_1m", "1m_3m", "3m_10m", "over_10m"
    };

    public static readonly IReadOnlySet<string> ConsentChannels = new HashSet<string>(StringComparer.Ordinal)
    {
        "zalo_personal", "zalo_oa", "sms", "email", "phone"
    };

    public static readonly IReadOnlySet<string> ConsentPurposes = new HashSet<string>(StringComparer.Ordinal)
    {
        "marketing_new_products", "marketing_promotions", "customer_care"
    };

    public static readonly IReadOnlySet<string> SortOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "createdAt:desc", "createdAt:asc",
        "lastDeliveredAt:desc", "lastDeliveredAt:asc",
        "lifetimeValueVnd:desc", "lifetimeValueVnd:asc",
        "fullName:asc", "fullName:desc"
    };

    // BR-CRM-01 + R-P201-04: Vietnamese mobile numbers only (second digit in {3,5,7,8,9}) -> +84xxxxxxxxx.
    public static bool TryNormalizePhone(string? raw, out string? e164)
    {
        if (raw is null)
        {
            e164 = null;
            return true;
        }

        if (raw.Contains('\0'))
        {
            e164 = null;
            return false;
        }

        Span<char> buffer = stackalloc char[16];
        var length = 0;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || ch is '.' or '-' or '(' or ')')
            {
                continue;
            }

            if (length >= buffer.Length)
            {
                e164 = null;
                return false;
            }

            buffer[length++] = ch;
        }

        var cleaned = buffer[..length];
        if (cleaned.Length == 10 && cleaned[0] == '0' && IsVietnameseMobilePrefix(cleaned[1]) && AllAsciiDigits(cleaned[2..]))
        {
            e164 = string.Concat("+84", cleaned[1..]);
            return true;
        }

        if (cleaned.Length == 11 && cleaned[0] == '8' && cleaned[1] == '4' && IsVietnameseMobilePrefix(cleaned[2]) && AllAsciiDigits(cleaned[3..]))
        {
            e164 = string.Concat("+", cleaned);
            return true;
        }

        if (cleaned.Length == 12 && cleaned[0] == '+' && cleaned[1] == '8' && cleaned[2] == '4' && IsVietnameseMobilePrefix(cleaned[3]) && AllAsciiDigits(cleaned[4..]))
        {
            e164 = new string(cleaned);
            return true;
        }

        e164 = null;
        return false;
    }

    public static string? ToLocalSearchPhone(string? phoneE164)
    {
        if (string.IsNullOrEmpty(phoneE164))
        {
            return null;
        }

        return phoneE164.StartsWith("+84", StringComparison.Ordinal) && phoneE164.Length == 12
            ? string.Concat("0", phoneE164.AsSpan(3))
            : phoneE164;
    }

    // BR-CRM-14: full_name || zalo_name || code || local 0xxxxxxxxx phone || email.
    public static string BuildSearchSource(string fullName, string? zaloName, string code, string? phoneE164, string? email)
    {
        var parts = new List<string>(5);
        AddPart(parts, fullName);
        AddPart(parts, zaloName);
        AddPart(parts, code);
        AddPart(parts, ToLocalSearchPhone(phoneE164));
        AddPart(parts, email);
        return string.Join(' ', parts);
    }

    // 03-database.md §3.7: extract ASCII phone digits for phone_e164 tail search when query is a phone fragment.
    public static string? ExtractPhoneSearchDigits(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Contains('\0'))
        {
            return null;
        }

        Span<char> digits = stackalloc char[16];
        var count = 0;
        var hasPlus = false;
        foreach (var ch in query)
        {
            if (char.IsWhiteSpace(ch) || ch is '.' or '-' or '(' or ')')
            {
                continue;
            }

            if (ch == '+')
            {
                if (hasPlus || count > 0)
                {
                    return null;
                }

                hasPlus = true;
                continue;
            }

            if (!IsAsciiDigit(ch))
            {
                return null;
            }

            if (count >= digits.Length)
            {
                return null;
            }

            digits[count++] = ch;
        }

        if (count < 3 || count > 12)
        {
            return null;
        }

        var span = digits[..count];
        if (hasPlus)
        {
            if (span.Length < 5 || span[0] != '8' || span[1] != '4')
            {
                return null;
            }

            return new string(span[2..]);
        }

        if (span.Length >= 5 && span[0] == '8' && span[1] == '4' && IsVietnameseMobilePrefix(span[2]))
        {
            return new string(span[2..]);
        }

        if (span.Length >= 5 && span[0] == '0' && IsVietnameseMobilePrefix(span[1]))
        {
            return new string(span[1..]);
        }

        return new string(span);
    }

    // ASSUMPTION-P202-03: KH + at least 6 zero-padded digits (KH001000..KH999999, KH1000000...).
    public static string FormatCustomerCode(long sequence)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Customer code sequence must be positive.");
        }

        return "KH" + sequence.ToString("D6", CultureInfo.InvariantCulture);
    }

    public static bool IsValidCustomerCode(string? code)
        => code is { Length: >= 8 }
           && code.StartsWith("KH", StringComparison.Ordinal)
           && AllAsciiDigits(code.AsSpan(2));

    public static bool HasValidContact(string? phoneE164, string? zaloName, string contactStatus, Guid? mergedIntoCustomerId)
        => string.Equals(contactStatus, "erased", StringComparison.Ordinal)
           || mergedIntoCustomerId is not null
           || !string.IsNullOrWhiteSpace(phoneE164)
           || !string.IsNullOrWhiteSpace(zaloName);

    public static bool IsSelfReferral(Guid customerId, Guid? referredByCustomerId)
        => referredByCustomerId is { } referrer && referrer == customerId;

    // Matches PostgreSQL check constraint: email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'
    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrEmpty(email) || email.Length > 254 || email.Contains('\0'))
        {
            return false;
        }

        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@') || at >= email.Length - 3)
        {
            return false;
        }

        foreach (var ch in email)
        {
            if (char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        var domain = email.AsSpan(at + 1);
        var dot = domain.IndexOf('.');
        return dot > 0 && dot < domain.Length - 1 && domain[^1] != '.';
    }

    public static bool IsValidHexColor(string? color)
    {
        if (color is null || color.Length != 7 || color[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < 7; i++)
        {
            var ch = color[i];
            if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidProvinceCode(string? code)
        => code is { Length: 2 } && AllAsciiDigits(code);

    public static bool IsValidWardCode(string? code)
        => code is { Length: 5 } && AllAsciiDigits(code);

    private static bool IsVietnameseMobilePrefix(char ch) => ch is '3' or '5' or '7' or '8' or '9';

    private static bool IsAsciiDigit(char ch) => ch is >= '0' and <= '9';

    private static bool AllAsciiDigits(ReadOnlySpan<char> span)
    {
        foreach (var ch in span)
        {
            if (!IsAsciiDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddPart(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value.Trim());
        }
    }
}
