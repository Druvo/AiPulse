namespace AiPulse.Services.Productivity;

/// <summary>What the app can detect about a user's setup, gathered by the checklist component.</summary>
public sealed record OnboardingSignals(int FollowedTags, int FollowedSources, int SavedSearches, bool AppInstalled);

public sealed record OnboardingStep(string Key, string Title, string Description, string Href, string ActionLabel, string Icon, bool Done, bool CanTickManually);

/// <summary>The first-run checklist: which steps exist, which are done (detected or ticked by hand), and overall progress.</summary>
public static class OnboardingSteps
{
    public const string Topics = "topics";
    public const string Sources = "sources";
    public const string SavedSearch = "saved-search";
    public const string Install = "install";
    public const string TwoFactor = "2fa";

    public static IReadOnlyList<OnboardingStep> Steps(OnboardingSignals s, IReadOnlySet<string> manuallyDone)
    {
        bool Manual(string key) => manuallyDone.Contains(key);
        return new[]
        {
            new OnboardingStep(Topics, "Pick topics you care about", "Follow a few tags so your feed and digest lean towards them.",
                "/settings", "Choose topics", "trending-up", s.FollowedTags > 0, false),
            new OnboardingStep(Sources, "Follow a source or two", "Use the follow button on any story in the News Feed.",
                "/news", "Open News Feed", "rss", s.FollowedSources > 0, false),
            new OnboardingStep(SavedSearch, "Save a search", "Keep a query and get alerted when new stories match it.",
                "/saved", "Create a saved search", "search", s.SavedSearches > 0, false),
            new OnboardingStep(Install, "Install the app", "Add AiPulse to your home screen or dock for one-tap access.",
                "/help", "How to install", "download", s.AppInstalled || Manual(Install), true),
            new OnboardingStep(TwoFactor, "Protect your account", "Turn on two-step sign-in and keep a strong, unique password.",
                "/account/security", "Account security", "key", Manual(TwoFactor), true),
        };
    }

    public static (int Done, int Total) Progress(IReadOnlyList<OnboardingStep> steps) => (steps.Count(s => s.Done), steps.Count);

    /// <summary>Manually ticked steps as stored ("install,2fa"), restricted to the steps that can be ticked by hand.</summary>
    public static HashSet<string> ParseManual(string? stored) =>
        (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k is Install or TwoFactor).ToHashSet(StringComparer.Ordinal);

    public static string Toggle(string? stored, string key, bool done)
    {
        var set = ParseManual(stored);
        if (key is not (Install or TwoFactor)) return string.Join(',', set.OrderBy(k => k, StringComparer.Ordinal));
        if (done) set.Add(key); else set.Remove(key);
        return string.Join(',', set.OrderBy(k => k, StringComparer.Ordinal));
    }
}
