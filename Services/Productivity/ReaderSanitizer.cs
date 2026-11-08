using System.Net;
using System.Text;
using HtmlAgilityPack;

namespace AiPulse.Services.Productivity;

/// <summary>
/// Strict allow-list HTML sanitiser for the reading view. It does not clean the input in place: it walks the parsed tree and writes a
/// brand new document containing only allowed tags and attributes, with every text node re-encoded. So anything the parser
/// misunderstands can at worst lose formatting, never smuggle markup through.
/// <list type="bullet">
/// <item>Dropped with their contents: script, style, iframe, object, embed, form controls, svg, math, template, noscript, head...</item>
/// <item>Unknown tags are unwrapped (their text kept); h1 becomes h2 (the page already has the title as h1).</item>
/// <item>Attributes: only href on a, src/alt/width/height on img, colspan/rowspan on cells. No style, class, id, on* handlers.</item>
/// <item>Links: http(s)/mailto only, resolved against the article URL, and always target=_blank rel="noopener nofollow noreferrer".</item>
/// <item>Images: http(s) only, lazy-loaded, no referrer.</item>
/// </list>
/// </summary>
public static class ReaderSanitizer
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "h2", "h3", "h4", "h5", "h6", "strong", "b", "em", "i", "u", "s", "del", "ins", "mark", "small", "sub", "sup",
        "blockquote", "q", "cite", "ul", "ol", "li", "dl", "dt", "dd", "a", "code", "pre", "kbd", "samp", "figure", "figcaption",
        "img", "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "hr", "abbr", "time"
    };

    private static readonly HashSet<string> DropWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "form", "input", "button", "select", "textarea",
        "option", "svg", "math", "template", "noscript", "head", "title", "meta", "link", "base", "canvas", "audio", "video", "source",
        "track", "picture", "nav", "aside", "footer", "header", "dialog", "portal"
    };

    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase) { "br", "hr", "img" };

    public const int MaxOutputChars = 200_000;

    public static string Sanitize(string? html, string? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var doc = new HtmlDocument { OptionFixNestedTags = true };
        doc.LoadHtml(html);
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri);
        var sb = new StringBuilder();
        foreach (var child in doc.DocumentNode.ChildNodes)
            Write(child, sb, baseUri, 0);
        var result = sb.ToString().Trim();
        return result.Length > MaxOutputChars ? result[..MaxOutputChars] : result;
    }

    private static void Write(HtmlNode node, StringBuilder sb, Uri? baseUri, int depth)
    {
        if (depth > 60 || sb.Length > MaxOutputChars) return;
        switch (node.NodeType)
        {
            case HtmlNodeType.Text:
                // HtmlAgilityPack keeps entities raw; decode then encode so the output is uniformly, safely escaped.
                sb.Append(WebUtility.HtmlEncode(WebUtility.HtmlDecode(node.InnerText)));
                return;
            case HtmlNodeType.Comment:
                return;
            case HtmlNodeType.Document:
                foreach (var c in node.ChildNodes) Write(c, sb, baseUri, depth + 1);
                return;
        }

        var name = node.Name.ToLowerInvariant();
        if (DropWithContent.Contains(name)) return;
        if (name == "h1") name = "h2";

        if (!Allowed.Contains(name))
        {
            // Block-ish wrappers (div/section/article/span...) are unwrapped; a div becomes a paragraph break so text does not run together.
            var block = name is "div" or "section" or "article" or "main" or "center";
            foreach (var c in node.ChildNodes) Write(c, sb, baseUri, depth + 1);
            if (block) sb.Append('\n');
            return;
        }

        var attrs = new StringBuilder();
        switch (name)
        {
            case "a":
                var href = SafeUrl(node.GetAttributeValue("href", ""), baseUri, allowMailto: true);
                if (href is null)
                {
                    foreach (var c in node.ChildNodes) Write(c, sb, baseUri, depth + 1);
                    return;
                }
                Attr(attrs, "href", href);
                Attr(attrs, "target", "_blank");
                Attr(attrs, "rel", "noopener nofollow noreferrer");
                break;
            case "img":
                var src = SafeUrl(node.GetAttributeValue("src", ""), baseUri, allowMailto: false);
                if (src is null) return;
                Attr(attrs, "src", src);
                Attr(attrs, "alt", node.GetAttributeValue("alt", ""));
                foreach (var dim in new[] { "width", "height" })
                    if (int.TryParse(node.GetAttributeValue(dim, ""), out var px) && px > 0 && px < 5000) Attr(attrs, dim, px.ToString());
                Attr(attrs, "loading", "lazy");
                Attr(attrs, "referrerpolicy", "no-referrer");
                break;
            case "td":
            case "th":
                foreach (var span in new[] { "colspan", "rowspan" })
                    if (int.TryParse(node.GetAttributeValue(span, ""), out var n) && n is > 1 and < 100) Attr(attrs, span, n.ToString());
                break;
        }

        sb.Append('<').Append(name).Append(attrs).Append('>');
        if (Void.Contains(name)) return;
        foreach (var c in node.ChildNodes) Write(c, sb, baseUri, depth + 1);
        sb.Append("</").Append(name).Append('>');
    }

    private static void Attr(StringBuilder sb, string name, string value) =>
        sb.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');

    /// <summary>Resolves against the base and returns the URL only if it is http(s) (or mailto: where allowed).</summary>
    public static string? SafeUrl(string? raw, Uri? baseUri, bool allowMailto)
    {
        var value = WebUtility.HtmlDecode(raw ?? "").Trim();
        // Browsers ignore embedded whitespace/control chars in schemes ("jav\tascript:"); strip them before judging the scheme.
        value = new string(value.Where(c => !char.IsControl(c)).ToArray());
        if (value.Length == 0 || value.StartsWith('#')) return null;

        if (value.StartsWith("//")) value = "https:" + value;
        Uri? uri;
        var hasScheme = System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9+.-]*:");
        if (hasScheme)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return null;
        }
        else if (baseUri is null || !Uri.TryCreate(baseUri, value, out uri)) return null;
        // "//host/path" parses as file: on some platforms; only accept web schemes.
        if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) return uri.AbsoluteUri;
        if (allowMailto && uri.Scheme == Uri.UriSchemeMailto) return uri.OriginalString;
        return null;
    }

    /// <summary>Plain text (e.g. the extracted full text) into safe paragraphs: blank lines split paragraphs, everything is encoded.</summary>
    public static string ParagraphsFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var paras = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new StringBuilder();
        foreach (var p in paras)
            sb.Append("<p>").Append(WebUtility.HtmlEncode(p).Replace("\n", "<br>")).Append("</p>");
        return sb.ToString();
    }

    /// <summary>Visible words in sanitised HTML (tags stripped), for the reading-time estimate.</summary>
    public static int WordCount(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return 0;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        // Join text nodes with spaces: InnerText would glue "two</b></p><p>three" into one word.
        var text = string.Join(' ', doc.DocumentNode.DescendantsAndSelf().Where(n => n.NodeType == HtmlNodeType.Text).Select(n => WebUtility.HtmlDecode(n.InnerText)));
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>Minutes at 230 words per minute, at least 1 for any text at all.</summary>
    public static int ReadingMinutes(int words) => words <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(words / 230.0));
}
