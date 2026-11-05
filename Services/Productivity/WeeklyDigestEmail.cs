using System.Security.Cryptography;
using AiPulse.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiPulse.Services.Productivity;

/// <summary>Bound from "Digest" in appsettings (all optional).</summary>
public sealed class DigestOptions
{
    /// <summary>Public address used for links in emails (e.g. https://aipulse.example.com). Falls back to WebSub:PublicBaseUrl, then localhost.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>How often the sender looks for users who are due.</summary>
    public int CheckMinutes { get; set; } = 60;
    /// <summary>Users emailed per pass, so a large install spreads the sending out.</summary>
    public int MaxPerPass { get; set; } = 50;
}

/// <summary>Per-user digest and onboarding switches (one row per user, created on first write).</summary>
public sealed class ProductivitySettingsService
{
    private readonly ProductivityDb _db;
    public ProductivitySettingsService(ProductivityDb db) => _db = db;

    public async Task<UserProductivitySettings> GetAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        return await db.UserProductivitySettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct)
            ?? new UserProductivitySettings { UserId = userId };
    }

    public async Task<UserProductivitySettings> UpdateAsync(int userId, Action<UserProductivitySettings> change, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        var row = await db.UserProductivitySettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (row is null)
        {
            row = new UserProductivitySettings { UserId = userId };
            db.UserProductivitySettings.Add(row);
        }
        change(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>Opts in or out. Opting in rotates the unsubscribe nonce, so links from an earlier subscription stop working.</summary>
    public Task<UserProductivitySettings> SetDigestAsync(int userId, bool optIn, CancellationToken ct = default) =>
        UpdateAsync(userId, s =>
        {
            if (optIn && !s.DigestOptIn) s.DigestNonce = NewNonce();
            if (optIn && string.IsNullOrEmpty(s.DigestNonce)) s.DigestNonce = NewNonce();
            s.DigestOptIn = optIn;
        }, ct);

    public async Task<List<UserProductivitySettings>> DigestSubscribersAsync(CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        return await db.UserProductivitySettings.AsNoTracking().Where(s => s.DigestOptIn).ToListAsync(ct);
    }

    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
}

/// <summary>Signed, URL-safe unsubscribe tokens: "userId:nonce" protected with ASP.NET Core Data Protection (keys in App_Data/dp-keys).</summary>
public sealed class DigestUnsubscribeTokens
{
    private readonly IDataProtector _protector;
    public DigestUnsubscribeTokens(IDataProtectionProvider provider) => _protector = provider.CreateProtector("AiPulse.WeeklyDigest.Unsubscribe.v1");

    public string Create(int userId, string nonce) => _protector.Protect($"{userId}:{nonce}");

    /// <summary>The (userId, nonce) inside a token, or null when it is forged, corrupted or from another install.</summary>
    public (int UserId, string Nonce)? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1000) return null;
        try
        {
            var raw = _protector.Unprotect(token);
            var sep = raw.IndexOf(':');
            if (sep <= 0 || !int.TryParse(raw[..sep], out var id)) return null;
            return (id, raw[(sep + 1)..]);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}

/// <summary>When a user is due their weekly email. Pure, so the "at most once a week" rule is testable with any clock.</summary>
public static class DigestSchedule
{
    /// <summary>A little slack so a pass that runs a few minutes early does not push the email a whole extra check interval back.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromHours(1);

    public static bool IsDue(UserProductivitySettings s, DateTime nowUtc) =>
        s.DigestOptIn && (s.DigestLastSentUtc is null || nowUtc - s.DigestLastSentUtc.Value >= TimeSpan.FromDays(7) - Slack);
}

/// <summary>Builds and sends the weekly digest emails. Separate from the hosted loop so it can be run (and tested) one pass at a time.</summary>
public sealed class WeeklyDigestSender
{
    private readonly ProductivitySettingsService _settings;
    private readonly UserService _users;
    private readonly FeedHistoryService _history;
    private readonly IEmailSender _email;
    private readonly DigestUnsubscribeTokens _tokens;
    private readonly DigestOptions _options;
    private readonly string _baseUrl;
    private readonly TimeProvider _time;
    private readonly ILogger<WeeklyDigestSender> _log;

    public WeeklyDigestSender(ProductivitySettingsService settings, UserService users, FeedHistoryService history, IEmailSender email,
        DigestUnsubscribeTokens tokens, IOptions<DigestOptions> options, IConfiguration config, TimeProvider time, ILogger<WeeklyDigestSender> log)
    {
        _settings = settings;
        _users = users;
        _history = history;
        _email = email;
        _tokens = tokens;
        _options = options.Value;
        _baseUrl = ResolveBaseUrl(_options.BaseUrl, config["WebSub:PublicBaseUrl"]);
        _time = time;
        _log = log;
    }

    public static string ResolveBaseUrl(string? configured, string? fallback)
    {
        foreach (var candidate in new[] { configured, fallback })
            if (Uri.TryCreate(candidate?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                return u.GetLeftPart(UriPartial.Authority) + u.AbsolutePath.TrimEnd('/');
        return "http://localhost:5000";
    }

    public string UnsubscribeUrl(int userId, string nonce) => $"{_baseUrl}/digest/unsubscribe?token={Uri.EscapeDataString(_tokens.Create(userId, nonce))}";

    /// <summary>One pass: every opted-in, approved user with an email address who is due gets the digest. Returns how many were sent.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var due = (await _settings.DigestSubscribersAsync(ct)).Where(s => DigestSchedule.IsDue(s, now.UtcDateTime)).ToList();
        if (due.Count == 0) return 0;

        var digest = WeeklyDigest.LastSevenDays(_history.Items, now);
        if (digest.IsEmpty)
        {
            _log.LogInformation("Weekly digest skipped: nothing was collected in the last seven days");
            return 0;
        }

        var users = (await _users.GetAllAsync()).ToDictionary(u => u.Id);
        var sent = 0;
        foreach (var s in due.Take(Math.Max(1, _options.MaxPerPass)))
        {
            if (!users.TryGetValue(s.UserId, out var user) || user.Status != UserStatuses.Approved || string.IsNullOrWhiteSpace(user.Email))
                continue;

            var nonce = s.DigestNonce;
            if (string.IsNullOrEmpty(nonce))
                nonce = (await _settings.UpdateAsync(s.UserId, x => x.DigestNonce = ProductivitySettingsService.NewNonce(), ct)).DigestNonce;

            // Recorded before sending: if the process dies mid-send the user misses one email rather than getting two.
            await _settings.UpdateAsync(s.UserId, x => x.DigestLastSentUtc = now.UtcDateTime, ct);

            var unsubscribe = UnsubscribeUrl(s.UserId, nonce);
            var subject = $"This week in AI · {WeeklyDigest.RangeText(digest.From, digest.To)}";
            var text = WeeklyDigest.ToPlainText(digest, _baseUrl, unsubscribe);
            var html = WeeklyDigest.ToHtmlEmail(digest, _baseUrl, unsubscribe);
            var headers = new Dictionary<string, string>
            {
                ["List-Unsubscribe"] = $"<{unsubscribe}>",
            };
            try
            {
                await _email.SendAsync(user.Email!, subject, text, html, headers, ct);
                sent++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Weekly digest to user {UserId} failed", s.UserId);
            }
        }
        if (sent > 0) _log.LogInformation("Weekly digest sent to {Count} user(s){Mode}", sent, _email.Enabled ? "" : " (logged only: SMTP is not configured)");
        return sent;
    }

    /// <summary>Applies an unsubscribe token. True when the token was valid and current (the user is now opted out).</summary>
    public async Task<bool> UnsubscribeAsync(string? token, CancellationToken ct = default)
    {
        var parsed = _tokens.Read(token);
        if (parsed is null) return false;
        var (userId, nonce) = parsed.Value;
        var s = await _settings.GetAsync(userId, ct);
        if (string.IsNullOrEmpty(s.DigestNonce) || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(s.DigestNonce), System.Text.Encoding.UTF8.GetBytes(nonce)))
            return false;
        await _settings.SetDigestAsync(userId, false, ct);
        return true;
    }
}

/// <summary>The hosted loop around <see cref="WeeklyDigestSender"/>.</summary>
public sealed class WeeklyDigestBackgroundService : BackgroundService
{
    private readonly WeeklyDigestSender _sender;
    private readonly DigestOptions _options;
    private readonly ILogger<WeeklyDigestBackgroundService> _log;

    public WeeklyDigestBackgroundService(WeeklyDigestSender sender, IOptions<DigestOptions> options, ILogger<WeeklyDigestBackgroundService> log)
    {
        _sender = sender;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(5, _options.CheckMinutes)));
        do
        {
            try { await _sender.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Weekly digest pass failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
