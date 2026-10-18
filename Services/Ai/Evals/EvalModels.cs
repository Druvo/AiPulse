using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiPulse.Services.Ai.Evals;

/// <summary>
/// One golden question. Which fields are set decides what is checked: retrieval cases name passages that must rank
/// near the top, guardrail cases name the status the agent must return, and grounding cases script a model reply
/// (right or wrong) and check what survives verification.
/// </summary>
public sealed class EvalCase
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Question { get; set; } = "";

    /// <summary>Passage ids that must all rank within the first <see cref="Within"/> results for this question.</summary>
    [JsonPropertyName("expect_sources")] public List<string>? ExpectSources { get; set; }
    public int Within { get; set; } = 5;

    /// <summary>Passage id that must be the single best match.</summary>
    [JsonPropertyName("expect_top")] public string? ExpectTop { get; set; }

    /// <summary>Answered, SmallTalk, Refused, NotFound or RateLimited.</summary>
    [JsonPropertyName("expect_status")] public string? ExpectStatus { get; set; }

    /// <summary>A status the agent must NOT return - used for questions that merely look suspicious and must not be refused.</summary>
    [JsonPropertyName("expect_not_status")] public string? ExpectNotStatus { get; set; }

    /// <summary>When set, a model that replies with exactly this text stands in for the real one.</summary>
    [JsonPropertyName("model_reply")] public string? ModelReply { get; set; }

    [JsonPropertyName("expect_contains")] public List<string>? ExpectContains { get; set; }
    [JsonPropertyName("expect_absent")] public List<string>? ExpectAbsent { get; set; }

    /// <summary>At least this many sentences must have been removed by verification.</summary>
    [JsonPropertyName("expect_dropped_min")] public int? ExpectDroppedMin { get; set; }

    [JsonPropertyName("expect_dropped_max")] public int? ExpectDroppedMax { get; set; }
}

public sealed class EvalSuite
{
    /// <summary>Minimum pass rate per category (0-1); a category without an entry must pass completely.</summary>
    public Dictionary<string, double> Thresholds { get; set; } = new();
    public List<EvalCase> Cases { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public static EvalSuite Load(string path) =>
        JsonSerializer.Deserialize<EvalSuite>(File.ReadAllText(path), Json) ?? new EvalSuite();
}

public sealed record EvalFailure(string CaseId, string Category, string Reason);

public sealed record CategoryScore(string Category, int Passed, int Total, double Threshold)
{
    public double Rate => Total == 0 ? 1 : (double)Passed / Total;
    public bool Met => Rate + 1e-9 >= Threshold;
}

public sealed record EvalReport(IReadOnlyList<CategoryScore> Categories, IReadOnlyList<EvalFailure> Failures)
{
    public int Passed => Categories.Sum(c => c.Passed);
    public int Total => Categories.Sum(c => c.Total);
    public bool Met => Categories.All(c => c.Met);
}
