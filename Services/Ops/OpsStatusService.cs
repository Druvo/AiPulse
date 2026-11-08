using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using AiPulse.Models;
using AiPulse.Services.Ai;
using AiPulse.Services.Ai.Llm;
using Microsoft.Extensions.Options;

namespace AiPulse.Services.Ops;

public sealed record SystemStatus(
    DateTimeOffset StartedAt,
    TimeSpan Uptime,
    string Version,
    string? Commit,
    string Runtime,
    string OperatingSystem,
    string Environment,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    long DatabaseBytes,
    long AppDataBytes,
    int AppDataFiles,
    long? DiskFreeBytes,
    long? DiskTotalBytes);

public sealed record RotatedHost(string Host, int Sources, int BatchSize, int PollsPerCycle, IReadOnlyList<string> NextDue);

public sealed record PollOverview(
    PollStatus Status,
    int EnabledSources,
    int DisabledSources,
    TimeSpan Interval,
    DateTimeOffset NextScheduledPoll,
    DateTimeOffset? CacheFetchedAt,
    int CachedItems,
    int HistoryItems,
    IReadOnlyList<RotatedHost> RotatedHosts);

public sealed record SourceOffender(string Name, string Category, double? UptimePercent, int FailStreak, string? LastError, DateTimeOffset? LastAttempt, double? AvgResponseMs);

public sealed record FeedHealthOverview(int Total, int Healthy, int Degraded, int Broken, int NoData, int FailingNow, IReadOnlyList<SourceOffender> WorstOffenders);

public sealed record AiProviderStatus(string Name, string Kind, string Model, int Priority, bool Enabled, int Calls, int Failures, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt);

public sealed record AiOverview(
    bool ModelCallsAllowed,
    bool ForcedOffByConfiguration,
    bool MasterSwitch,
    bool AskUsesModel,
    bool Summaries,
    bool Explanations,
    IReadOnlyList<AiProviderStatus> Providers);

public sealed record OpsSnapshot(DateTimeOffset GeneratedAt, SystemStatus System, PollOverview Poll, FeedHealthOverview FeedHealth, AiOverview Ai, IReadOnlyList<BackupFile> Backups);

/// <summary>
/// Assembles the admin operations console's read-only view of the running instance from the services that already own each piece:
/// the process itself, <see cref="PollStatusTracker"/>, <see cref="SourceHealthService"/>, <see cref="AiSettingsService"/> and
/// <see cref="OpsBackupStore"/>. Nothing here changes state.
/// </summary>
public sealed class OpsStatusService
{
    private readonly IWebHostEnvironment _env;
    private readonly PollStatusTracker _tracker;
    private readonly FeedAggregatorService _feeds;
    private readonly FeedHistoryService _history;
    private readonly KnowledgeBaseService _kb;
    private readonly SourceHealthService _health;
    private readonly AiSettingsService _ai;
    private readonly ILlmClient _llm;
    private readonly OpsBackupStore _backups;
    private readonly NotificationOptions _poll;
    private readonly ILogger<OpsStatusService> _log;

    public OpsStatusService(
        IWebHostEnvironment env, PollStatusTracker tracker, FeedAggregatorService feeds, FeedHistoryService history, KnowledgeBaseService kb,
        SourceHealthService health, AiSettingsService ai, ILlmClient llm, OpsBackupStore backups, IOptions<NotificationOptions> poll,
        ILogger<OpsStatusService> log)
    {
        _env = env;
        _tracker = tracker;
        _feeds = feeds;
        _history = history;
        _kb = kb;
        _health = health;
        _ai = ai;
        _llm = llm;
        _backups = backups;
        _poll = poll.Value;
        _log = log;
    }

    public string AppDataPath => Path.Combine(_env.ContentRootPath, "App_Data");

    public OpsSnapshot GetSnapshot() =>
        new(DateTimeOffset.UtcNow, GetSystem(), GetPoll(), GetFeedHealth(), GetAi(), _backups.List());

    public SystemStatus GetSystem()
    {
        using var process = Process.GetCurrentProcess();
        var started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        var (version, commit) = VersionInfo();

        var dbBytes = 0L;
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            dbBytes += FileLength(Path.Combine(AppDataPath, "aipulse.db" + suffix));

        var (appDataBytes, files) = DirectorySize(AppDataPath);
        var (free, total) = Disk(AppDataPath);

        return new SystemStatus(
            started,
            DateTimeOffset.UtcNow - started,
            version,
            commit,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            _env.EnvironmentName,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            dbBytes,
            appDataBytes,
            files,
            free,
            total);
    }

    public PollOverview GetPoll()
    {
        var status = _tracker.Snapshot();
        var sources = _kb.Sources;
        var enabled = sources.Where(s => s.Enabled).ToList();
        var interval = TimeSpan.FromMinutes(Math.Max(1, _poll.PollMinutes));
        var cache = _feeds.PeekCache();

        // The round the next poll will use: the aggregator's own counter if it reports one, otherwise the polls seen so far.
        var nextRound = status.ReportedRound is { } reported ? reported + 1 : status.PollsCompleted;
        var (due, _) = FeedPollOrder.SelectForRound(enabled, nextRound);
        var dueNames = due.Select(s => s.Name).ToHashSet();

        var rotated = enabled
            .GroupBy(s => FeedDomainThrottle.GetHost(s.Url) ?? "")
            .Select(g => (Host: g.Key, Sources: g.ToList(), Batch: FeedPollOrder.BatchSizeFor(g.Key)))
            .Where(g => g.Batch is { } b && g.Sources.Count > b)
            .Select(g => new RotatedHost(
                g.Host,
                g.Sources.Count,
                g.Batch!.Value,
                (int)Math.Ceiling(g.Sources.Count / (double)g.Batch.Value),
                g.Sources.Where(s => dueNames.Contains(s.Name)).Select(s => s.Name).ToList()))
            .OrderBy(h => h.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PollOverview(
            status,
            enabled.Count,
            sources.Count - enabled.Count,
            interval,
            _tracker.NextScheduledPoll(interval),
            cache?.FetchedAt,
            cache?.Items.Count ?? 0,
            _history.Items.Count,
            rotated);
    }

    public FeedHealthOverview GetFeedHealth(int worst = 8)
    {
        var rows = _kb.Sources.Where(s => s.Enabled).Select(s =>
        {
            var snap = _health.GetSnapshot(s.Name);
            return (Source: s, Snap: snap, Streak: _feeds.GetFailureStreak(s.Name));
        }).ToList();

        int healthy = 0, degraded = 0, broken = 0, noData = 0;
        foreach (var row in rows)
        {
            if (row.Snap?.UptimePercent is not { } pct) noData++;
            else if (pct >= 90) healthy++;
            else if (pct >= 60) degraded++;
            else broken++;
        }

        var offenders = rows
            .Where(r => r.Streak > 0 || r.Snap?.LastSuccess == false || r.Snap?.UptimePercent is < 90)
            .OrderByDescending(r => r.Streak)
            .ThenBy(r => r.Snap?.UptimePercent ?? 0)
            .ThenBy(r => r.Source.Name, StringComparer.OrdinalIgnoreCase)
            .Take(worst)
            .Select(r => new SourceOffender(r.Source.Name, r.Source.Category, r.Snap?.UptimePercent, r.Streak,
                Trim(r.Snap?.LastError, 240), r.Snap?.LastAttempt, r.Snap?.AvgResponseMs))
            .ToList();

        return new FeedHealthOverview(rows.Count, healthy, degraded, broken, noData,
            rows.Count(r => r.Streak > 0 || r.Snap?.LastSuccess == false), offenders);
    }

    public AiOverview GetAi()
    {
        var settings = _ai.Current;
        var stats = _llm.Stats.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var providers = _llm.AllProviders
            .OrderBy(p => p.Priority)
            .Select(p =>
            {
                stats.TryGetValue(p.Name, out var st);
                return new AiProviderStatus(p.Name, p.Kind, p.Model, p.Priority, _ai.IsProviderEnabled(p.Name),
                    st?.Calls ?? 0, st?.Failures ?? 0, st?.LastSuccessAt, st?.LastFailureAt);
            })
            .ToList();

        return new AiOverview(_ai.ModelCallsAllowed, _ai.ForcedOffByConfiguration, settings.Enabled, settings.AskUsesModel,
            settings.Summaries, settings.Explanations, providers);
    }

    /// <summary>
    /// Version from the assembly's informational version. The .NET 8 SDK appends "+&lt;commit sha&gt;" when it builds from a git
    /// checkout; a deploy that builds elsewhere can set AIPULSE_COMMIT instead.
    /// </summary>
    public static (string Version, string? Commit) VersionInfo()
    {
        var info = typeof(OpsStatusService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(OpsStatusService).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        var plus = info.IndexOf('+');
        var version = plus >= 0 ? info[..plus] : info;
        var commit = System.Environment.GetEnvironmentVariable("AIPULSE_COMMIT");
        if (string.IsNullOrWhiteSpace(commit) && plus >= 0) commit = info[(plus + 1)..];
        if (commit is { Length: > 12 }) commit = commit[..12];
        return (version, string.IsNullOrWhiteSpace(commit) ? null : commit);
    }

    private static long FileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private (long Bytes, int Files) DirectorySize(string dir)
    {
        long bytes = 0;
        var files = 0;
        try
        {
            if (!Directory.Exists(dir)) return (0, 0);
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                bytes += FileLength(file);
                files++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Could not size {Dir}", dir);
        }
        return (bytes, files);
    }

    /// <summary>Free and total space of the volume holding <paramref name="path"/>: the mount with the longest matching root, so it is right on Linux too.</summary>
    private (long? Free, long? Total) Disk(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var drive = DriveInfo.GetDrives()
                .Where(d => { try { return d.IsReady; } catch { return false; } })
                .Where(d => full.StartsWith(d.RootDirectory.FullName, comparison))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return drive is null ? (null, null) : (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read free disk space for {Path}", path);
            return (null, null);
        }
    }

    private static string? Trim(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max] + "…";
}
