namespace AiPulse.Services.Ai;

/// <summary>Bound from the "Ai" config section; every value has a working default.</summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>How many passages are put in front of the model.</summary>
    public int TopK { get; set; } = 6;

    /// <summary>The share of the question's meaningful words a passage must match to count as relevant.</summary>
    public double MinCoverage { get; set; } = 0.5;

    /// <summary>Questions one person may ask per rolling hour (0 = unlimited).</summary>
    public int MaxQuestionsPerHour { get; set; } = 30;

    /// <summary>Item summaries one person may request per rolling hour; a summary already saved for an item is free and does not count (0 = unlimited).</summary>
    public int MaxSummariesPerHour { get; set; } = 60;

    /// <summary>Newest feed items indexed for answers (the saved history holds far more).</summary>
    public int NewsDocLimit { get; set; } = 4000;

    public int CorpusRefreshMinutes { get; set; } = 10;

    public int MaxQuestionLength { get; set; } = 500;
}
