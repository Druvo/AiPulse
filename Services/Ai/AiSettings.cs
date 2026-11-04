using System.Text.Json;

namespace AiPulse.Services.Ai;

/// <summary>What the rest of the app asks before it spends a model call. One place to say "no".</summary>
public interface IAiGate
{
    /// <summary>The master switch: when false, no model is called for anything.</summary>
    bool ModelCallsAllowed { get; }

    /// <summary>Whether Ask AiPulse may have a model write its answers (otherwise it quotes the sources).</summary>
    bool AskMayUseModel { get; }

    bool SummariesAllowed { get; }

    bool ExplanationsAllowed { get; }

    bool IsProviderEnabled(string providerName);
}

/// <summary>Everything allowed - used where no gate was supplied (tests, standalone use).</summary>
public sealed class OpenGate : IAiGate
{
    public static readonly OpenGate Instance = new();
    public bool ModelCallsAllowed => true;
    public bool AskMayUseModel => true;
    public bool SummariesAllowed => true;
    public bool ExplanationsAllowed => true;
    public bool IsProviderEnabled(string providerName) => true;
}

/// <summary>The switches an admin can flip while the app is running.</summary>
public sealed record AiSettings
{
    public bool Enabled { get; init; } = true;
    public bool AskUsesModel { get; init; } = true;
    public bool Summaries { get; init; } = true;
    public bool Explanations { get; init; } = true;

    /// <summary>Providers switched off by name. A provider that is not listed here is on.</summary>
    public string[] DisabledProviders { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Holds the live switches, saves them to App_Data so they survive a restart, and answers <see cref="IAiGate"/>. The
/// <c>Ai:Enabled</c> setting in configuration is a hard override: when it is false nothing in the UI can turn model calls on.
/// </summary>
public sealed class AiSettingsService : IAiGate
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly string? _path;
    private readonly bool _configurationAllows;
    private readonly object _lock = new();
    private volatile AiSettings _current = new();

    /// <param name="path">File the switches are saved in, or null to keep them in memory only.</param>
    /// <param name="configurationAllows">False when configuration forces model calls off.</param>
    public AiSettingsService(string? path, bool configurationAllows = true)
    {
        _path = path;
        _configurationAllows = configurationAllows;

        if (path is not null && File.Exists(path))
        {
            try
            {
                _current = JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(path), Json) ?? new AiSettings();
            }
            catch (JsonException)
            {
                // A damaged settings file must not stop the app; fall back to the defaults.
            }
        }
    }

    public AiSettings Current => _current;

    /// <summary>True when configuration (not the UI) is what is keeping model calls off.</summary>
    public bool ForcedOffByConfiguration => !_configurationAllows;

    public event Action? Changed;

    public void Update(Func<AiSettings, AiSettings> change)
    {
        lock (_lock)
        {
            var next = change(_current);
            _current = next with { DisabledProviders = next.DisabledProviders.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray() };

            if (_path is not null)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(_current, Json));
            }
        }

        Changed?.Invoke();
    }

    public void SetProviderEnabled(string providerName, bool enabled) =>
        Update(s => s with
        {
            DisabledProviders = enabled
                ? s.DisabledProviders.Where(n => !string.Equals(n, providerName, StringComparison.OrdinalIgnoreCase)).ToArray()
                : s.DisabledProviders.Append(providerName).ToArray()
        });

    public bool ModelCallsAllowed => _configurationAllows && _current.Enabled;
    public bool AskMayUseModel => ModelCallsAllowed && _current.AskUsesModel;
    public bool SummariesAllowed => ModelCallsAllowed && _current.Summaries;
    public bool ExplanationsAllowed => ModelCallsAllowed && _current.Explanations;

    public bool IsProviderEnabled(string providerName) =>
        !_current.DisabledProviders.Contains(providerName, StringComparer.OrdinalIgnoreCase);
}
