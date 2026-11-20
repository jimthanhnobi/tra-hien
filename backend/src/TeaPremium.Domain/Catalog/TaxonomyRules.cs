using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TeaPremium.Domain.Catalog;

public enum CatalogKind { Category, Collection, Material, Artisan }

// P1-04 / BR-CAT-01/08/09. No persistence, HTTP or framework dependencies.
public static partial class TaxonomyRules
{
    public static string Prefix(CatalogKind kind) => kind switch
    {
        CatalogKind.Category => "/danh-muc/",
        CatalogKind.Collection => "/bo-suu-tap/",
        CatalogKind.Material => "/chat-lieu/",
        CatalogKind.Artisan => "/nghe-nhan/",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static bool IsPublic(string status) => status is "visible" or "published";
    public static string Slug(string name)
    {
        var text = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                text.Append(c is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(c));
        var slug = NonSlug().Replace(text.ToString(), "-").Trim('-');
        return slug[..Math.Min(80, slug.Length)].TrimEnd('-');
    }
    public static string Candidate(string slug, int attempt)
    {
        var suffix = attempt == 1 ? "" : "-" + attempt.ToString(CultureInfo.InvariantCulture);
        return slug[..Math.Min(slug.Length, 80 - suffix.Length)].TrimEnd('-') + suffix;
    }
    public static bool ValidSlug(string? slug) => slug is { Length: > 0 and <= 80 } && SlugPattern().IsMatch(slug);

    public static bool ValidTree(IReadOnlyDictionary<Guid, Guid?> parents)
    {
        foreach (var id in parents.Keys)
        {
            var seen = new HashSet<Guid>();
            Guid? cursor = id;
            while (cursor is { } node)
            {
                if (!seen.Add(node) || seen.Count > 3 || !parents.TryGetValue(node, out cursor)) return false;
            }
        }
        return true;
    }

    public static Dictionary<string, string> Redirects(IReadOnlyDictionary<string, string> original, string? oldPath, string newPath)
    {
        var result = new Dictionary<string, string>(original, StringComparer.Ordinal);
        result.Remove(newPath); // reuse a historical URL; its live entity wins
        if (oldPath is null || oldPath == newPath) return result;
        // Also flatten transitive inbound links from other prefixes, without changing unrelated links.
        foreach (var source in original.Keys)
        {
            if (source == newPath) continue;
            var cursor = source;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (original.TryGetValue(cursor, out var target) && seen.Add(cursor))
            {
                if (target == oldPath) { result[source] = newPath; break; }
                cursor = target;
            }
        }
        result[oldPath] = newPath;
        foreach (var source in result.Keys)
        {
            if (result[source] == source || (result[source] == newPath && result.ContainsKey(newPath)))
                throw new DomainException("redirect_conflict", "Đường dẫn chuyển hướng không hợp lệ.");
        }
        return result;
    }
    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)] private static partial Regex NonSlug();
    [GeneratedRegex("\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant)] private static partial Regex SlugPattern();
}

public interface ITaxonomyRecord
{
    Guid Id { get; set; }
    string Name { get; set; }
    string Slug { get; set; }
    string Status { get; set; }
    string? FocusKeyword { get; set; }
    string? SeoTitle { get; set; }
    string? SeoDescription { get; set; }
    bool RobotsNoindex { get; set; }
    DateTime? FirstPublishedAt { get; set; }
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
    uint Version { get; set; }
}
