using System.Security.Cryptography;
using System.Text.Json;

namespace AiPulse.Services;

public sealed record AndroidAppInfo(string Version, int VersionCode, string FileName, string Sha256, long Size, string FilePath);

/// <summary>
/// The Android app (an APK built from the AiPulseMobile project) offered on /app. The files live in <c>App_Data/downloads</c> rather than
/// in the repository or the published site: an APK is tens of megabytes of build output, and App_Data survives deploys and is backed
/// up. Drop <c>latest.json</c> (written by the app's build script) and the APK it names into that folder and the download appears;
/// remove them and it disappears. The checksum in latest.json is verified against the real file before anything is offered, so a
/// half-copied or swapped file is never advertised as the signed build.
/// </summary>
public sealed class AppDownloads
{
    public const string Folder = "downloads";
    public const string MetaFile = "latest.json";

    private readonly string _dir;
    private readonly object _gate = new();
    private (DateTime MetaStamp, DateTime FileStamp, long Length, AndroidAppInfo? Info)? _cache;

    public AppDownloads(IWebHostEnvironment env) : this(Path.Combine(env.ContentRootPath, "App_Data", Folder)) { }

    public AppDownloads(string directory) => _dir = directory;

    public string Directory => _dir;

    /// <summary>The Android build to offer, or null when none is installed or the files do not agree.</summary>
    public AndroidAppInfo? Android()
    {
        var metaPath = Path.Combine(_dir, MetaFile);
        if (!File.Exists(metaPath)) return null;

        lock (_gate)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = doc.RootElement;
                var name = root.GetProperty("file").GetString();
                // A plain file name only: this value is joined to a path, so no separators or traversal.
                if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || !name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) return null;
                var path = Path.Combine(_dir, name);
                if (!File.Exists(path)) return null;

                var meta = File.GetLastWriteTimeUtc(metaPath);
                var file = new FileInfo(path);
                if (_cache is { } c && c.MetaStamp == meta && c.FileStamp == file.LastWriteTimeUtc && c.Length == file.Length) return c.Info;

                var expected = root.GetProperty("sha256").GetString() ?? "";
                var actual = Sha256Of(path);
                var info = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
                    ? new AndroidAppInfo(root.GetProperty("version").GetString() ?? "", root.TryGetProperty("versionCode", out var vc) && vc.TryGetInt32(out var code) ? code : 0,
                        name, actual, file.Length, path)
                    : null;
                _cache = (meta, file.LastWriteTimeUtc, file.Length, info);
                return info;
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>"38.4 MB": what people expect to see next to a download button.</summary>
    public static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB" : $"{Math.Max(1, bytes / 1024d):0} KB";
}
