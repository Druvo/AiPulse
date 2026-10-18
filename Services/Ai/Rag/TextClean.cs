using System.Net;
using System.Text.RegularExpressions;

namespace AiPulse.Services.Ai.Rag;

/// <summary>Turns feed HTML into plain text for indexing, prompting and number-checking.</summary>
public static class TextClean
{
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static string Plain(string? html) =>
        Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(html ?? "", " ")), " ").Trim();

    public static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd() + "…";
}
