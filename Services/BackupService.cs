using System.IO.Compression;

namespace AiPulse.Services;

/// <summary>
/// Zips/restores the entire App_Data directory (SQLite DB, feed history, reading state, Obsidian
/// exports) so self-hosters can back up or migrate their data without touching the filesystem directly.
/// </summary>
public sealed class BackupService
{
    private readonly string _appDataDir;

    public BackupService(IWebHostEnvironment env)
    {
        _appDataDir = Path.Combine(env.ContentRootPath, "App_Data");
    }

    public byte[] CreateBackupZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(_appDataDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(_appDataDir, file);
                var entry = zip.CreateEntry(relativePath, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                // The SQLite DB can be briefly held open (read/write) by the app itself - CreateEntryFromFile's
                // default sharing is too restrictive for that, so open it manually with a permissive share mode.
                using var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                sourceStream.CopyTo(entryStream);
            }
        }
        return ms.ToArray();
    }

    public async Task RestoreFromZipAsync(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry, nothing to extract

            var destPath = Path.Combine(_appDataDir, entry.FullName);
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            await using var entryStream = entry.Open();
            await using var fileStream = File.Create(destPath);
            await entryStream.CopyToAsync(fileStream);
        }
    }
}
