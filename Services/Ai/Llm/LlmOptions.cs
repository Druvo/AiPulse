namespace AiPulse.Services.Ai.Llm;

/// <summary>
/// Bound from the "Llm" config section. Any number of providers, tried in ascending <see cref="LlmProviderOptions.Priority"/> order:
/// <code>
/// "Llm": { "Providers": [
///   { "Name": "local", "Kind": "ollama", "BaseUrl": "http://localhost:11434", "Model": "qwen2.5:7b", "Priority": 10 },
///   { "Name": "hosted", "Kind": "openai", "BaseUrl": "https://api.openai.com/v1", "Model": "gpt-4o-mini", "Priority": 20 }
/// ] }
/// </code>
/// With nothing configured, the existing "Ollama:BaseUrl" is used as a single Ollama provider.
/// API keys belong in user secrets or environment variables (Llm__Providers__1__ApiKey), never in a committed file.
/// </summary>
public sealed class LlmOptions
{
    public const string Section = "Llm";

    public List<LlmProviderOptions> Providers { get; set; } = new();
}

public sealed class LlmProviderOptions
{
    public string Name { get; set; } = "";

    /// <summary>"ollama" or "openai" (any OpenAI-compatible server: OpenAI, Azure-compatible gateways, vLLM, LM Studio, OpenRouter...).</summary>
    public string Kind { get; set; } = "ollama";

    public string BaseUrl { get; set; } = "";

    /// <summary>Empty for Ollama means "use the first installed model".</summary>
    public string Model { get; set; } = "";

    public string? ApiKey { get; set; }

    /// <summary>Lower is tried first.</summary>
    public int Priority { get; set; } = 100;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long to wait for one provider before moving on to the next. Short on purpose: a hosted model that is overloaded can
    /// take well over a minute to answer a one-line prompt, and nobody should stare at a spinner that long before the fallback kicks in.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Sent as <c>reasoning_effort</c> by OpenAI-compatible providers when set ("low", "medium", "high"; some also accept "none").
    /// Thinking models such as Gemini count hidden reasoning against the token limit, so left unset a short answer can come back empty.
    /// </summary>
    public string? ReasoningEffort { get; set; }
}
