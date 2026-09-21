using AiPulse.Models;

namespace AiPulse.Services;

public sealed partial class ReadingStateService
{
    // --- Export path ---

    public string? ObsidianExportPath
    {
        get { EnsureLoaded(); lock (_lock) return _state!.ObsidianExportPath; }
    }

    public void SetObsidianExportPath(string? path)
    {
        EnsureLoaded();
        lock (_lock) { _state!.ObsidianExportPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim(); Save(); }
    }

    // --- Welcome banner ---

    public bool WelcomeDismissed
    {
        get { EnsureLoaded(); lock (_lock) return _state!.WelcomeDismissed; }
    }

    public void DismissWelcome()
    {
        EnsureLoaded();
        lock (_lock) { _state!.WelcomeDismissed = true; Save(); }
    }

    // --- Outbound webhook ---

    public string? WebhookUrl
    {
        get { EnsureLoaded(); lock (_lock) return _state!.WebhookUrl; }
    }

    public void SetWebhookUrl(string? url)
    {
        EnsureLoaded();
        lock (_lock) { _state!.WebhookUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim(); Save(); }
    }

    // --- Per-keyword webhook routes (additional to the single catch-all WebhookUrl above) ---

    public IReadOnlyList<WebhookRoute> WebhookRoutes
    {
        get { EnsureLoaded(); lock (_lock) return _state!.WebhookRoutes.ToList(); }
    }

    public void AddWebhookRoute(string url, IEnumerable<string> keywords)
    {
        EnsureLoaded();
        url = url.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (_lock)
        {
            _state!.WebhookRoutes.Add(new WebhookRoute
            {
                Url = url,
                Keywords = keywords.Select(k => k.Trim()).Where(k => k.Length > 0).ToList()
            });
            Save();
        }
    }

    public void RemoveWebhookRoute(Guid id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _state!.WebhookRoutes.RemoveAll(r => r.Id == id);
            Save();
        }
    }

    // --- Dashboard widgets ---

    public IReadOnlyList<DashboardWidget> DashboardWidgets
    {
        get { EnsureLoaded(); lock (_lock) return _state!.DashboardWidgets.ToList(); }
    }

    public void AddDashboardWidget(string title, string type, string content)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _state!.DashboardWidgets.Add(new DashboardWidget { Title = title.Trim(), Type = type, Content = content.Trim() });
            Save();
        }
    }

    public void RemoveDashboardWidget(Guid id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var existing = _state!.DashboardWidgets.FirstOrDefault(w => w.Id == id);
            if (existing is not null) { _state.DashboardWidgets.Remove(existing); Save(); }
        }
    }
}
