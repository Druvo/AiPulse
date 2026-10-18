using System.Text.RegularExpressions;

namespace AiPulse.Services.Ai.Agent;

public enum GuardCategory { Empty, TooLong, Injection }

public sealed record GuardResult(bool Allowed, GuardCategory? Category = null, string? Message = null);

/// <summary>
/// Pattern-based guards around the model: the question going in, the passages put in front of it, and the text coming out.
/// They are a cheap first line, not a promise - the citation check is what stops an unsupported answer.
/// </summary>
public static class Guardrails
{
    public const string RefusalMessage =
        "I can't help with that request. Ask me about AI news, tools, models or concepts instead.";

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Phrases that try to rewrite the assistant's instructions. Each needs an override verb plus an instruction-like
    // object, so ordinary questions ("how do I ignore previous commits in git?") do not trip it.
    private static readonly Regex Injection = new(string.Join("|",
        @"\b(?:ignore|disregard|forget|override|bypass)\s+(?:all\s+|any\s+|the\s+|your\s+|my\s+|of\s+)*(?:previous|prior|above|earlier|preceding|system|safety|original)\s+(?:rules?|instructions?|prompts?|guidelines?|restrictions?|messages?)\b",
        @"\b(?:ignore|disregard|forget)\s+(?:all|any|your)\s+(?:rules|instructions|guidelines|restrictions)\b",
        @"\byou are now\b",
        @"\bact as (?:an? )?(?:dan|unrestricted|unfiltered|jailbroken)\b",
        // Extracting the prompt is blocked; merely mentioning one ("how do I write a good system prompt?") is a normal question here.
        @"\b(?:reveal|show|print|repeat|leak|output|display)\s+(?:me\s+)?(?:your|the)\s+(?:system|hidden|initial|original|secret)\s+(?:prompt|instructions?)\b",
        @"\b(?:what(?:'s|\s+is|\s+are)|tell\s+me|give\s+me)\s+(?:me\s+)?your\s+(?:system\s+|hidden\s+|initial\s+|original\s+)?(?:prompt|instructions?)\b",
        @"\bdeveloper mode\b",
        @"\bjailbreak(?:ed|ing)?\b",
        @"\bdo anything now\b",
        @"<\|im_(?:start|end)\|>",
        @"\[/?INST\]"), Opts);

    private static readonly Regex[] Secrets =
    {
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)", Opts),
        new(@"\bsk-[A-Za-z0-9_\-]{20,}", Opts),
        new(@"\bgh[pousr]_[A-Za-z0-9]{30,}", Opts),
        new(@"\bAKIA[0-9A-Z]{16}\b", Opts),
        new(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}", Opts),
        new(@"\bBearer\s+[A-Za-z0-9._\-]{20,}", Opts),
    };

    private static readonly Regex Control = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);

    public static GuardResult CheckQuestion(string? question, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new GuardResult(false, GuardCategory.Empty, "Type a question first.");

        if (question.Length > maxLength)
            return new GuardResult(false, GuardCategory.TooLong, $"Keep questions under {maxLength} characters.");

        if (Injection.IsMatch(question))
            return new GuardResult(false, GuardCategory.Injection, RefusalMessage);

        return new GuardResult(true);
    }

    /// <summary>
    /// Retrieved text comes from feeds and forum posts anyone can write, so it is data, never instructions. Instruction-like
    /// phrases are cut out before the model sees it.
    /// </summary>
    public static string NeutralizeSource(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return Injection.Replace(Control.Replace(text, " "), "[removed]");
    }

    /// <summary>Masks credential-shaped strings so a model that echoes one back cannot leak it into the page.</summary>
    public static string RedactOutput(string text)
    {
        foreach (var pattern in Secrets)
            text = pattern.Replace(text, "[redacted]");
        return text;
    }
}
