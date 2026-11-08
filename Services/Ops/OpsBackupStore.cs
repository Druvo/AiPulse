using System.Text.RegularExpressions;

namespace AiPulse.Services.Ops;

public sealed record BackupFile(string Name, long Bytes, DateTimeOffset CreatedAt);

/// <summary>
/// Keeps on-demand App_Data backups (made by <see cref="BackupService"/>) as zip files on the server, for the operations console's
/// "Create backup now". They are written OUTSIDE App_Data (default: a "backups" folder next to it, or <c>Ops:BackupDirectory</c>),
/// because BackupService zips everything under App_Data: backups kept inside it would be swallowed by every later backup.
/// Nothing is ever deleted here; pruning old files is left to the operator (see docs/OPERATIONS.md).
/// </summary>
public sealed partial class OpsBackupStore
{
    private readonly BackupService _backup;
    private readonly ILogger<OpsBackupStore> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OpsBackupStore(BackupService backup, IWebHostEnvironment env, IConfiguration config, ILogger<OpsBackupStore> log)
    {
        _backup = backup;
        _log = log;
        var configured = config["Ops:BackupDirectory"];
        Directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "backups")
            : Path.GetFullPath(configured, env.ContentRootPath);
    }

    public string Directory { get; }

    [GeneratedRegex(@"^aipulse-backup-\d{8}-\d{6}(-\d+)?\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupName();

    public static bool IsValidName(string? name) => name is not null && BackupName().IsMatch(name);

    public IReadOnlyList<BackupFile> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return Array.Empty<BackupFile>();
            return new DirectoryInfo(Directory).EnumerateFiles("aipulse-backup-*.zip")
                .Where(f => IsValidName(f.Name))
                .Select(f => new BackupFile(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
                .OrderByDescending(f => f.CreatedAt)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not list backups in {Dir}", Directory);
            return Array.Empty<BackupFile>();
        }
    }

    /// <summary>Full path of an existing backup, or null for anything that is not one of ours (no path tricks).</summary>
    public string? PathOf(string name)
    {
        if (!IsValidName(name)) return null;
        var path = Path.Combine(Directory, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Zips App_Data into a new timestamped file. One at a time: a double click must not write two zips at once.</summary>
    public async Task<BackupFile> CreateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var bytes = await Task.Run(_backup.CreateBackupZip, ct);

            var stem = $"aipulse-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var name = stem + ".zip";
            for (var n = 2; File.Exists(Path.Combine(Directory, name)); n++) name = $"{stem}-{n}.zip";

            var path = Path.Combine(Directory, name);
            var temp = path + ".partial";
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path);
            _log.LogInformation("Created backup {Name} ({Bytes} bytes) in {Dir}", name, bytes.Length, Directory);
            return new BackupFile(name, bytes.Length, DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }
}
