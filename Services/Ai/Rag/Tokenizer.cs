using System.Text.RegularExpressions;

namespace AiPulse.Services.Ai.Rag;

/// <summary>Lowercasing word splitter with a light suffix stemmer, used identically for documents and queries.</summary>
public static class Tokenizer
{
    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    private static readonly (string From, string To)[] Special =
    {
        ("c#", "csharp"), ("f#", "fsharp"), ("c++", "cpp"), (".net", "dotnet"), ("node.js", "nodejs")
    };

    private static readonly HashSet<string> Protected = new(Special.Select(x => x.To), StringComparer.Ordinal);

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "of", "to", "in", "on", "for", "with", "is", "are", "was", "were", "be",
        "been", "it", "its", "this", "that", "these", "those", "what", "whats", "which", "who", "how", "why", "when",
        "where", "do", "does", "did", "can", "could", "should", "would", "will", "about", "as", "at", "by", "from",
        "into", "than", "then", "there", "their", "they", "you", "your", "i", "me", "my", "we", "our", "us", "not",
        "no", "so", "if", "just", "also", "has", "have", "had", "tell", "explain", "give", "show", "please", "some",
        "any", "more", "most", "very", "much", "many", "get", "use", "used", "using", "latest", "new"
    };

    /// <summary>All tokens, stop words kept out, stemmed.</summary>
    public static List<string> Tokens(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var lower = text.ToLowerInvariant();
        foreach (var (from, to) in Special)
            lower = lower.Replace(from, " " + to + " ");

        foreach (Match m in Word.Matches(lower))
        {
            var token = m.Value;
            if (Stop.Contains(token)) continue;
            result.Add(Protected.Contains(token) ? token : Stem(token));
        }

        return result;
    }

    public static string Stem(string token)
    {
        if (token.Length < 4 || !IsAsciiLetters(token)) return token;

        if (token.EndsWith("ies") && token.Length > 4) return token[..^3] + "y";
        if (token.EndsWith("sses")) return token[..^2];
        if (token.EndsWith("s") && !token.EndsWith("ss") && !token.EndsWith("us") && !token.EndsWith("is")) token = token[..^1];
        if (token.Length > 5 && token.EndsWith("ing")) return token[..^3];
        if (token.Length > 4 && token.EndsWith("ed")) return token[..^2];
        return token;
    }

    private static bool IsAsciiLetters(string s)
    {
        foreach (var c in s)
            if (c is < 'a' or > 'z') return false;
        return true;
    }
}
