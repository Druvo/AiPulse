using System.Security.Cryptography;
using System.Text;
using AiPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services;

/// <summary>Options bound from the "WebSub" section of appsettings.json.</summary>
public sealed class WebSubOptions
{
    /// <summary>
    /// The public URL this app is reachable at (e.g. https://aipulse.druvium.xyz), used to build the hub
    /// callback URL. Empty/unset = WebSub is fully disabled - a hub can't call back to "localhost".
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Requested subscription lifetime. Hubs may grant a shorter one; we respect whatever they return.</summary>
    public int RequestedLeaseSeconds { get; set; } = 432_000; // 5 days
}

/// <summary>
/// WebSub (formerly PubSubHubbub) client: subscribes to any feed that advertises a hub, so updates get
/// pushed to us instead of waiting for the next poll. Entirely opt-in and inert unless PublicBaseUrl is
/// configured - most feeds don't declare a hub at all, so this only activates for the ones that do
/// (e.g. YouTube channel feeds via Google's public hub).
/// </summary>
public sealed class WebSubService
{
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly WebSubOptions _opt;
    private readonly ILogger<WebSubService> _log;
    private bool _schemaEnsured;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    public WebSubService(
        IDbContextFactory<AiPulseDbContext> dbFactory,
        IHttpClientFactory httpFactory,
        Microsoft.Extensions.Options.IOptions<WebSubOptions> opt,
        ILogger<WebSubService> log)
    {
        _dbFactory = dbFactory;
        _httpFactory = httpFactory;
        _opt = opt.Value;
        _log = log;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_opt.PublicBaseUrl);

    private string CallbackUrl(int sourceId) => $"{_opt.PublicBaseUrl!.TrimEnd('/')}/websub/callback/{sourceId}";

    /// <summary>True if this source already has a Verified, non-expired subscription (no need to re-subscribe).</summary>
    public async Task<bool> HasActiveSubscriptionAsync(int sourceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        var sub = await db.WebSubSubscriptions.FirstOrDefaultAsync(s => s.SourceId == sourceId);
        return sub is { Status: WebSubStatuses.Verified } && (sub.LeaseExpiresAt is null || sub.LeaseExpiresAt > DateTimeOffset.Now);
    }

    /// <summary>Sends a subscribe request to the hub. The subscription stays Pending until the hub's verification GET arrives.</summary>
    public async Task SubscribeAsync(int sourceId, string topicUrl, string hubUrl, CancellationToken ct = default)
    {
        if (!Enabled) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var existing = await db.WebSubSubscriptions.FirstOrDefaultAsync(s => s.SourceId == sourceId, ct);
        if (existing is null)
        {
            existing = new WebSubSubscription { SourceId = sourceId, Topic = topicUrl, HubUrl = hubUrl, Secret = secret };
            db.WebSubSubscriptions.Add(existing);
        }
        else
        {
            existing.Topic = topicUrl;
            existing.HubUrl = hubUrl;
            existing.Secret = secret;
            existing.Status = WebSubStatuses.Pending;
        }
        await db.SaveChangesAsync(ct);

        try
        {
            var client = _httpFactory.CreateClient("feeds");
            var form = new Dictionary<string, string>
            {
                ["hub.mode"] = "subscribe",
                ["hub.topic"] = topicUrl,
                ["hub.callback"] = CallbackUrl(sourceId),
                ["hub.secret"] = secret,
                ["hub.lease_seconds"] = _opt.RequestedLeaseSeconds.ToString()
            };
            using var resp = await client.PostAsync(hubUrl, new FormUrlEncodedContent(form), ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogInformation("WebSub subscribe rejected by hub {Hub} for source {SourceId}: {Status}", hubUrl, sourceId, resp.StatusCode);
                await MarkFailedAsync(sourceId, ct);
            }
            // 2xx (usually 202 Accepted) means the hub will verify asynchronously via a GET to our callback.
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "WebSub subscribe request failed for source {SourceId}", sourceId);
            await MarkFailedAsync(sourceId, ct);
        }
    }

    private async Task MarkFailedAsync(int sourceId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var sub = await db.WebSubSubscriptions.FirstOrDefaultAsync(s => s.SourceId == sourceId, ct);
        if (sub is null) return;
        sub.Status = WebSubStatuses.Failed;
        sub.FailCount++;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Handles the hub's verification GET. Returns the challenge string to echo back (200 OK), or null if
    /// this subscription/topic isn't recognized (caller should respond 404).
    /// </summary>
    public async Task<string?> HandleVerificationAsync(int sourceId, string? mode, string? topic, string? challenge, int? leaseSeconds)
    {
        if (string.IsNullOrEmpty(challenge)) return null;

        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        var sub = await db.WebSubSubscriptions.FirstOrDefaultAsync(s => s.SourceId == sourceId);
        if (sub is null || !string.Equals(sub.Topic, topic, StringComparison.OrdinalIgnoreCase))
            return null; // don't confirm a subscription/topic we didn't ask for

        if (mode == "unsubscribe")
        {
            sub.Status = WebSubStatuses.Failed;
            await db.SaveChangesAsync();
            return challenge;
        }

        sub.Status = WebSubStatuses.Verified;
        sub.LeaseExpiresAt = DateTimeOffset.Now.AddSeconds(leaseSeconds ?? _opt.RequestedLeaseSeconds);
        await db.SaveChangesAsync();
        _log.LogInformation("WebSub subscription verified for source {SourceId}, lease until {Expiry}", sourceId, sub.LeaseExpiresAt);
        return challenge;
    }

    /// <summary>Verifies the HMAC signature on a content push. Returns false (reject) for anything that doesn't check out.</summary>
    public async Task<bool> VerifyPushSignatureAsync(int sourceId, byte[] body, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader)) return false;

        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        var sub = await db.WebSubSubscriptions.FirstOrDefaultAsync(s => s.SourceId == sourceId);
        if (sub is not { Status: WebSubStatuses.Verified }) return false;

        var key = Encoding.UTF8.GetBytes(sub.Secret);
        var parts = signatureHeader.Split('=', 2);
        if (parts.Length != 2) return false;

        var expectedHex = parts[1];
        byte[] computed = parts[0].ToLowerInvariant() switch
        {
            "sha1" => new HMACSHA1(key).ComputeHash(body),
            "sha256" => new HMACSHA256(key).ComputeHash(body),
            _ => Array.Empty<byte>()
        };
        if (computed.Length == 0) return false;

        var computedHex = Convert.ToHexString(computed).ToLowerInvariant();
        var match = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computedHex), Encoding.ASCII.GetBytes(expectedHex.ToLowerInvariant()));
        if (match)
        {
            sub.LastPushAt = DateTimeOffset.Now;
            await db.SaveChangesAsync();
        }
        return match;
    }

    /// <summary>Re-subscribes to any Verified subscription whose lease is running out soon.</summary>
    public async Task RenewExpiringAsync(IReadOnlyList<FeedSource> currentSources, CancellationToken ct = default)
    {
        if (!Enabled) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var cutoff = DateTimeOffset.Now.AddHours(12);
        var expiring = await db.WebSubSubscriptions
            .Where(s => s.Status == WebSubStatuses.Verified && s.LeaseExpiresAt != null && s.LeaseExpiresAt < cutoff)
            .ToListAsync(ct);

        foreach (var sub in expiring)
        {
            var source = currentSources.FirstOrDefault(s => s.Id == sub.SourceId);
            if (source is null) continue; // source was deleted since - leave the row, next cleanup pass can remove it
            await SubscribeAsync(sub.SourceId, source.Url, sub.HubUrl, ct);
        }
    }

    /// <summary>Status per source, for display on the Sources page. Loaded once and cached by the caller.</summary>
    public async Task<Dictionary<int, WebSubSubscription>> GetAllStatusesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        return await db.WebSubSubscriptions.ToDictionaryAsync(s => s.SourceId);
    }

    private async Task EnsureSchemaAsync(AiPulseDbContext db)
    {
        if (_schemaEnsured) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaEnsured) return;

            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='WebSubSubscriptions'";
            var exists = await check.ExecuteScalarAsync() is not null;
            if (!exists)
            {
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE "WebSubSubscriptions" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_WebSubSubscriptions" PRIMARY KEY AUTOINCREMENT,
                        "SourceId" INTEGER NOT NULL,
                        "Topic" TEXT NOT NULL,
                        "HubUrl" TEXT NOT NULL,
                        "Secret" TEXT NOT NULL,
                        "Status" TEXT NOT NULL,
                        "LeaseExpiresAt" TEXT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "LastPushAt" TEXT NULL,
                        "FailCount" INTEGER NOT NULL DEFAULT 0
                    )
                    """;
                await create.ExecuteNonQueryAsync();
            }

            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
