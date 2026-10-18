using System.Text.RegularExpressions;

namespace AiPulse.Services.Ai.Agent;

public enum SentenceVerdict
{
    /// <summary>Cites real sources and everything checkable in it is in them.</summary>
    Keep,
    /// <summary>A fact-free clarifying question; allowed without a citation.</summary>
    Question,
    /// <summary>States something but cites nothing, or cites a source that was not provided.</summary>
    Uncited,
    /// <summary>Cites sources, but a number or quoted phrase in it is not in what it cites.</summary>
    Ungrounded,
    Empty
}

public sealed record DroppedSentence(string Text, SentenceVerdict Reason);

public sealed record VerifiedAnswer(IReadOnlyList<string> Sentences, IReadOnlyList<DroppedSentence> Dropped);

/// <summary>
/// Decides, sentence by sentence, what part of a model's draft is allowed to reach the reader: it must cite a
/// provided source by number ([1]), every number in it must appear in the sources it cites (or in the question),
/// and any quoted phrase must appear there verbatim. Nothing here trusts the model's own claim of being grounded.
/// </summary>
public static class CitationVerifier
{
    private static readonly Regex Ref = new(@"\[(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex RefAfterStop = new(@"([.!?])((?:\s*\[\d+\])+)", RegexOptions.Compiled);
    private static readonly Regex MultiRef = new(@"\[(\d+(?:\s*[,;]\s*\d+)+)\]", RegexOptions.Compiled);
    private static readonly Regex SentenceSplit = new(@"(?<=[.!?\]])\s+", RegexOptions.Compiled);
    private static readonly Regex NumberPattern = new(@"\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex Quoted = new("[\"“]([^\"“”]{12,})[\"”]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Repairs the citation styles models actually produce: "[1, 5]" becomes "[1][5]" and "5 days. [1]" becomes "5 days [1].".</summary>
    public static string Normalize(string draft)
    {
        draft = MultiRef.Replace(draft, m =>
            string.Concat(Regex.Split(m.Groups[1].Value, @"\s*[,;]\s*").Select(r => $"[{r}]")));
        return RefAfterStop.Replace(draft, m => $"{m.Groups[2].Value}{m.Groups[1].Value}");
    }

    public static VerifiedAnswer Verify(string draft, string question, IReadOnlyList<string> sourceTexts)
    {
        var kept = new List<string>();
        var dropped = new List<DroppedSentence>();

        foreach (var raw in SentenceSplit.Split(Normalize(draft)))
        {
            var sentence = raw.Trim();
            if (sentence.Length == 0) continue;

            var verdict = Check(sentence, question, sourceTexts);
            if (verdict is SentenceVerdict.Keep or SentenceVerdict.Question)
                kept.Add(sentence);
            else if (verdict != SentenceVerdict.Empty)
                dropped.Add(new DroppedSentence(sentence, verdict));
        }

        return new VerifiedAnswer(kept, dropped);
    }

    public static SentenceVerdict Check(string sentence, string question, IReadOnlyList<string> sourceTexts)
    {
        var refs = Ref.Matches(sentence).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        var body = Ref.Replace(sentence, "").Trim(' ', '.', '!', '?', '…');

        if (refs.Count == 0 && IsFollowUpQuestion(sentence)) return SentenceVerdict.Question;
        if (body.Length == 0) return SentenceVerdict.Empty;
        if (refs.Count == 0 || refs.Any(r => r < 1 || r > sourceTexts.Count)) return SentenceVerdict.Uncited;

        var evidence = string.Join(' ', refs.Distinct().Select(r => sourceTexts[r - 1]));
        var allowedNumbers = Numbers(evidence + " " + question);
        if (!Numbers(Ref.Replace(sentence, "")).IsSubsetOf(allowedNumbers)) return SentenceVerdict.Ungrounded;

        var flatEvidence = Flatten(evidence);
        foreach (Match quote in Quoted.Matches(sentence))
            if (!flatEvidence.Contains(Flatten(quote.Groups[1].Value), StringComparison.Ordinal))
                return SentenceVerdict.Ungrounded;

        return SentenceVerdict.Keep;
    }

    /// <summary>Numbers as plain digits: "39,000" and "39000.0" are both "39000", and the "07" in 2026-10-07 is the "7" in "7 October".</summary>
    public static HashSet<string> Numbers(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in NumberPattern.Matches(text))
        {
            var value = m.Value.Replace(",", "");
            if (value.Contains('.')) value = value.TrimEnd('0').TrimEnd('.');
            if (!value.Contains('.')) value = value.TrimStart('0') is { Length: > 0 } t ? t : "0";
            found.Add(value);
        }
        return found;
    }

    private static bool IsFollowUpQuestion(string sentence)
    {
        var text = sentence.Trim();
        return text.EndsWith('?') && !NumberPattern.IsMatch(text) && text.Length <= 200;
    }

    private static string Flatten(string text) =>
        Spaces.Replace(text.Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"'), " ").Trim().ToLowerInvariant();
}
