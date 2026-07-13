using System.Text;
using System.Text.Json;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Posts alerts to a user-configured outgoing webhook (Slack, Discord, or a generic JSON endpoint),
/// so release/watchlist notifications reach you even when no browser tab is open. The format is
/// auto-detected from the URL - no separate "webhook type" setting to get wrong.
/// </summary>
public sealed class WebhookService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WebhookService> _log;

    public WebhookService(IHttpClientFactory httpFactory, ILogger<WebhookService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<(bool Success, string? Error)> SendAsync(string webhookUrl, Alert alert, CancellationToken ct = default)
    {
        var text = $"[{alert.Kind}] {alert.Title} — {alert.SourceName}\n{alert.Link}";
        return await PostAsync(webhookUrl, text, ct);
    }

    public async Task<(bool Success, string? Error)> SendTestAsync(string webhookUrl, CancellationToken ct = default)
        => await PostAsync(webhookUrl, "AiPulse test notification — webhooks are working.", ct);

    private async Task<(bool Success, string? Error)> PostAsync(string webhookUrl, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return (false, "No webhook URL configured.");

        object payload;
        if (webhookUrl.Contains("discord.com/api/webhooks") || webhookUrl.Contains("discordapp.com/api/webhooks"))
            payload = new { content = text };
        else if (webhookUrl.Contains("hooks.slack.com"))
            payload = new { text };
        else
            payload = new { message = text, source = "AiPulse" };

        try
        {
            var client = _httpFactory.CreateClient("feeds");
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(webhookUrl, content, ct);
            if (resp.IsSuccessStatusCode)
                return (true, null);

            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogInformation("Webhook post failed: {Status} {Body}", resp.StatusCode, body);
            return (false, $"{(int)resp.StatusCode} {resp.StatusCode}");
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Webhook post threw");
            return (false, ex.Message);
        }
    }
}
