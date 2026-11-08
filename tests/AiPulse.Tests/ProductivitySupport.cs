using AiPulse.Models;
using AiPulse.Services;
using AiPulse.Services.Productivity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeClock : TimeProvider
{
    public FakeClock(DateTimeOffset now) => Now = now;
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A throwaway SQLite file per test, with the productivity schema bootstrapped on first use.</summary>
internal sealed class TestProductivityDb : IDbContextFactory<ProductivityDbContext>, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "aipulse-prod-" + Guid.NewGuid().ToString("N") + ".db");

    public TestProductivityDb() => Db = new ProductivityDb(this);

    public ProductivityDb Db { get; }
    public string ConnectionString => $"Data Source={_path}";

    public ProductivityDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ProductivityDbContext>().UseSqlite(ConnectionString).Options);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { /* best effort */ }
    }
}

/// <summary>Records every email instead of sending it.</summary>
internal sealed class FakeEmailSender : IEmailSender
{
    public sealed record Sent(string To, string Subject, string Text, string? Html, IReadOnlyDictionary<string, string>? Headers);
    public List<Sent> Messages { get; } = new();
    public bool Enabled => true;

    public Task SendAsync(string toEmail, string subject, string bodyText, CancellationToken ct = default)
    {
        Messages.Add(new Sent(toEmail, subject, bodyText, null, null));
        return Task.CompletedTask;
    }

    public Task SendAsync(string toEmail, string subject, string bodyText, string? bodyHtml, IReadOnlyDictionary<string, string>? headers, CancellationToken ct = default)
    {
        Messages.Add(new Sent(toEmail, subject, bodyText, bodyHtml, headers));
        return Task.CompletedTask;
    }
}

internal static class Items
{
    public static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public static FeedItem Make(string title, string link = "", string source = "Source A", string[]? tags = null, string summary = "",
        DateTimeOffset? published = null, string contentType = "News", string[]? alsoSeenOn = null, string? fullText = null) => new()
    {
        Title = title,
        Link = link.Length > 0 ? link : "https://example.test/" + Uri.EscapeDataString(title.ToLowerInvariant().Replace(' ', '-')),
        SourceName = source,
        Category = "News",
        Tags = tags ?? Array.Empty<string>(),
        Summary = summary,
        Published = published ?? Now.AddHours(-1),
        ContentType = contentType,
        AlsoSeenOn = alsoSeenOn ?? Array.Empty<string>(),
        FullText = fullText
    };
}
