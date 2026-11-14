using System.Text.Json;

namespace AiPulse.Services;

/// <summary>What this installation calls itself and what it is about. The defaults are today's AiPulse; an admin can change them in Settings.</summary>
public sealed record SiteSettings(string Name, string Tagline, string Description, string Topic)
{
    public const int MaxName = 40, MaxTagline = 120, MaxDescription = 300, MaxTopic = 30;

    public static readonly SiteSettings Default = new(
        "AiPulse",
        "Self-hosted AI news, tools and learning dashboard",
        "A standalone, self-hosted dashboard to keep developers current on the AI world: news, jargon, the right tool for the right task, and a structured learning roadmap.",
        "AI");

    /// <summary>Trims and bounds every field and refuses markup, so the values are safe to print anywhere (page titles, meta tags, feeds, JSON).</summary>
    public static (SiteSettings? Settings, string? Error) Validate(string? name, string? tagline, string? description, string? topic)
    {
        static string Clean(string? s) => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        var n = Clean(name); var t = Clean(tagline); var d = Clean(description); var p = Clean(topic);

        if (n.Length == 0) return (null, "Give the site a name.");
        if (n.Length > MaxName) return (null, $"The name can be at most {MaxName} characters.");
        if (t.Length > MaxTagline) return (null, $"The tagline can be at most {MaxTagline} characters.");
        if (d.Length > MaxDescription) return (null, $"The description can be at most {MaxDescription} characters.");
        if (p.Length == 0) return (null, "Say what the site is about (one or two words, e.g. AI, .NET, DevOps).");
        if (p.Length > MaxTopic) return (null, $"The topic can be at most {MaxTopic} characters.");
        foreach (var field in new[] { n, t, d, p })
            if (field.IndexOfAny(new[] { '<', '>' }) >= 0 || field.Any(char.IsControl)) return (null, "Use plain text only (no < or > characters).");

        return (new SiteSettings(n, t.Length == 0 ? Default.Tagline : t, d.Length == 0 ? Default.Description : d, p), null);
    }
}

/// <summary>
/// The single source for the site's name, tagline, description and topic, so pages, titles, feeds and the mobile/API surfaces never
/// hard-code "AiPulse" or "AI". Resolution: the admin's saved choice (App_Data/site-profile.json), else the "Site" configuration
/// section, else the AiPulse defaults. Changes apply immediately.
/// </summary>
public sealed class SiteProfileService
{
    private readonly string _path;
    private readonly object _gate = new();
    private SiteSettings _current;

    public SiteProfileService(IWebHostEnvironment env, IConfiguration config) : this(Path.Combine(env.ContentRootPath, "App_Data", "site-profile.json"), config) { }

    public SiteProfileService(string path, IConfiguration? config = null)
    {
        _path = path;
        var fromConfig = config?.GetSection("Site");
        var baseline = SiteSettings.Validate(fromConfig?["Name"], fromConfig?["Tagline"], fromConfig?["Description"], fromConfig?["Topic"]).Settings ?? SiteSettings.Default;
        _current = Load() ?? (fromConfig?["Name"] is null ? SiteSettings.Default : baseline);
    }

    public SiteSettings Current { get { lock (_gate) return _current; } }

    public string Name => Current.Name;
    public string Tagline => Current.Tagline;
    public string Description => Current.Description;
    /// <summary>The subject, e.g. "AI" or ".NET". Used as an adjective: "{Topic} news".</summary>
    public string Topic => Current.Topic;
    public string TopicNews => $"{Current.Topic} news";
    /// <summary>"AiPulse - latest AI news": titles for feeds.</summary>
    public string FeedTitle => $"{Current.Name} - latest {Current.Topic} news";

    /// <summary>Saves the admin's choice. Returns an error message when a field is not acceptable.</summary>
    public string? Save(string? name, string? tagline, string? description, string? topic)
    {
        var (settings, error) = SiteSettings.Validate(name, tagline, description, topic);
        if (settings is null) return error;
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            _current = settings;
        }
        return null;
    }

    /// <summary>Back to the configured/default identity.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
            _current = SiteSettings.Default;
        }
    }

    private SiteSettings? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var s = JsonSerializer.Deserialize<SiteSettings>(File.ReadAllText(_path));
            return s is null ? null : SiteSettings.Validate(s.Name, s.Tagline, s.Description, s.Topic).Settings;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
