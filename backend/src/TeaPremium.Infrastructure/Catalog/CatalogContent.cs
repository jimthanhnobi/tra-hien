using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using TeaPremium.Application.Catalog;
using TeaPremium.Infrastructure.Media;

namespace TeaPremium.Infrastructure.Catalog;

// BR-CNT-02 / BR-SEO-35: use an HTML parser/sanitizer, never regex-based HTML security.
public sealed class CatalogContentSanitizer(MediaStorageOptions media) : ICatalogContent
{
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "h2", "h3", "h4", "ul", "ol", "li", "blockquote", "table", "thead", "tbody", "tr", "td", "th", "figure", "figcaption", "br" };
    private static readonly HashSet<string> RemovedBlocks = new(StringComparer.OrdinalIgnoreCase)
        { "div", "section", "article", "main", "aside", "nav", "header", "footer", "address", "h1", "h5", "h6", "pre", "dl", "dt", "dd", "form", "fieldset", "legend", "details", "summary", "hr" };
    public CatalogContent Sanitize(string? json, string? html)
    {
        try { return SanitizeCore(json, html); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new ArgumentException("Invalid rich content.", nameof(json), exception); }
    }
    private CatalogContent SanitizeCore(string? json, string? html)
    {
        if (json is not null)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.GetProperty("type").GetString() != "doc") throw new ArgumentException("Rich content must be a document.");
            html = Render(document.RootElement, 0);
        }
        var parser = new HtmlParser();
        var raw = parser.ParseDocument("<!doctype html><html><body>" + (html ?? "") + "</body></html>");
        // Drop the subtrees before sanitization; keeping children must not expose script/style text.
        foreach (var node in raw.QuerySelectorAll("script,style,template,noscript")) node.Remove();
        // KeepChildNodes unwraps unsupported block tags. Persist separators before
        // unwrapping so subsequent reads, SEO and publication counts cannot join words.
        foreach (var node in raw.QuerySelectorAll("*").Where(x => RemovedBlocks.Contains(x.LocalName)).ToArray())
        {
            var parent = node.Parent!;
            parent.InsertBefore(raw.CreateTextNode(" "), node);
            parent.InsertBefore(raw.CreateTextNode(" "), node.NextSibling);
        }
        var sanitizer = new HtmlSanitizer { KeepChildNodes = true };
        sanitizer.AllowedTags.Clear(); sanitizer.AllowedAttributes.Clear(); sanitizer.AllowedCssProperties.Clear();
        foreach (var tag in Blocks.Where(x => x != "div").Concat(["strong", "em", "a", "img"])) sanitizer.AllowedTags.Add(tag);
        foreach (var attribute in new[] { "href", "rel", "src", "alt", "width", "height", "colspan", "rowspan" }) sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedSchemes.Clear(); sanitizer.AllowedSchemes.Add("https"); sanitizer.AllowedSchemes.Add("http");
        var clean = parser.ParseDocument("<!doctype html><html><body>" + sanitizer.Sanitize(raw.Body!.InnerHtml) + "</body></html>");
        foreach (var image in clean.QuerySelectorAll("img"))
            if (!ImageUrl(image.GetAttribute("src"))) image.Remove();
        foreach (var link in clean.QuerySelectorAll("a"))
        {
            var href = link.GetAttribute("href");
            if (!LinkUrl(href)) { link.RemoveAttribute("href"); link.RemoveAttribute("rel"); }
            else if (Uri.TryCreate(href, UriKind.Absolute, out _)) link.SetAttribute("rel", "noopener nofollow");
            else link.RemoveAttribute("rel");
        }
        var text = new StringBuilder();
        Visible(clean.Body!, text);
        var plain = CatalogSeo.Normalize(text.ToString()); // parser has already decoded entities exactly once
        return new(json, clean.Body!.InnerHtml, plain, plain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }
    private static void Visible(INode node, StringBuilder text)
    {
        if (node is IText literal) { text.Append(literal.Data); return; }
        var block = node is IElement element && Blocks.Contains(element.LocalName);
        if (block) text.Append(' ');
        foreach (var child in node.ChildNodes) Visible(child, text);
        if (block) text.Append(' ');
    }
    private bool ImageUrl(string? value) => Uri.TryCreate(media.PublicBaseUrl, UriKind.Absolute, out var origin)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0
        && uri.Authority.Equals(origin.Authority, StringComparison.OrdinalIgnoreCase) && uri.Scheme == origin.Scheme
        && uri.AbsolutePath.StartsWith(origin.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal);
    private static bool LinkUrl(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) && !value.Contains('\\')
        && (value.StartsWith('#') || value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal)
            || Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0);
    private string Render(JsonElement node, int depth)
    {
        if (depth > 32 || node.ValueKind != JsonValueKind.Object || node.EnumerateObject().Any(x => x.Name is not ("type" or "attrs" or "content" or "text" or "marks")))
            throw new ArgumentException("Invalid rich content node.");
        var type = node.GetProperty("type").GetString();
        var inner = new StringBuilder();
        if (node.TryGetProperty("content", out var children)) foreach (var child in children.EnumerateArray()) inner.Append(Render(child, depth + 1));
        if (type == "text")
        {
            var text = WebUtility.HtmlEncode(node.GetProperty("text").GetString() ?? "");
            if (node.TryGetProperty("marks", out var marks)) foreach (var mark in marks.EnumerateArray())
            {
                if (mark.EnumerateObject().Any(x => x.Name is not ("type" or "attrs"))) throw new ArgumentException("Unsupported mark fields.");
                var markType = mark.GetProperty("type").GetString();
                if (mark.TryGetProperty("attrs", out var markAttrs) && (markAttrs.ValueKind != JsonValueKind.Object
                    || markAttrs.EnumerateObject().Any(x => x.Name is not ("href" or "rel" or "target")))) throw new ArgumentException("Unsupported mark attributes.");
                text = markType switch
                {
                    "bold" => "<strong>" + text + "</strong>", "italic" => "<em>" + text + "</em>",
                    "link" => "<a href=\"" + WebUtility.HtmlEncode(UrlAttribute(mark.GetProperty("attrs"), "href", false)) + "\">" + text + "</a>",
                    _ => throw new ArgumentException("Unsupported rich text mark.")
                };
            }
            return text;
        }
        var attrs = node.TryGetProperty("attrs", out var attributes) ? attributes : default;
        if (attrs.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)) throw new ArgumentException("Invalid node attributes.");
        if (attrs.ValueKind == JsonValueKind.Object && attrs.EnumerateObject().Any(x => x.Name is not ("level" or "src" or "alt" or "width" or "height" or "colspan" or "rowspan" or "colwidth" or "start")))
            throw new ArgumentException("Unsupported node attributes.");
        if (type == "image")
        {
            var src = UrlAttribute(attrs, "src", true);
            var alt = attrs.TryGetProperty("alt", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : "";
            return "<img src=\"" + WebUtility.HtmlEncode(src) + "\" alt=\"" + WebUtility.HtmlEncode(alt) + "\">";
        }
        var tag = type switch
        {
            "doc" => "", "paragraph" => "p", "heading" => Heading(attrs), "bulletList" => "ul", "orderedList" => "ol", "listItem" => "li",
            "blockquote" => "blockquote", "hardBreak" => "br", "table" => "table", "tableRow" => "tr", "tableCell" => "td", "tableHeader" => "th",
            "figure" => "figure", "figcaption" => "figcaption", _ => throw new ArgumentException("Unsupported rich content node.")
        };
        return tag.Length == 0 ? inner.ToString() : tag == "br" ? "<br>" : "<" + tag + ">" + inner + "</" + tag + ">";
    }
    private static string Heading(JsonElement attrs) => attrs.TryGetProperty("level", out var level) && level.TryGetInt32(out var value) && value is >= 2 and <= 4
        ? "h" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) : throw new ArgumentException("Invalid heading level.");
    private string UrlAttribute(JsonElement attrs, string name, bool image)
    {
        if (attrs.ValueKind != JsonValueKind.Object || !attrs.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Missing content URL.");
        var url = value.GetString();
        if (image ? !ImageUrl(url) : !LinkUrl(url)) throw new ArgumentException("Invalid content URL.");
        return url!;
    }
}
