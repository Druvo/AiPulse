using System.Diagnostics;

namespace AiPulse.Services;

/// <summary>
/// Best-effort, cross-platform detection of this machine's RAM and (if an NVIDIA GPU is present) VRAM,
/// used to give a rough "will this model run on your PC" signal on the Explore page. Computed once and
/// cached - hardware doesn't change while the app is running.
/// </summary>
public sealed class SystemInfoService
{
    private readonly ILogger<SystemInfoService> _log;
    private double? _ramGb;
    private double? _vramGb;
    private string? _gpuName;
    private bool _detected;

    public SystemInfoService(ILogger<SystemInfoService> log)
    {
        _log = log;
    }

    public (double? RamGb, double? VramGb, string? GpuName) Detect()
    {
        if (_detected)
            return (_ramGb, _vramGb, _gpuName);

        try
        {
            // TotalAvailableMemoryBytes reflects the machine's total physical memory (or the container
            // limit, if containerized) - a reliable cross-platform proxy without P/Invoke or WMI.
            var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (bytes > 0) _ramGb = Math.Round(bytes / 1_073_741_824.0, 1);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't detect total RAM");
        }

        try
        {
            (_vramGb, _gpuName) = DetectNvidiaGpu();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't detect GPU/VRAM");
        }

        _detected = true;
        return (_ramGb, _vramGb, _gpuName);
    }

    /// <summary>
    /// Where Ollama actually stores pulled model blobs, and how much free space is left there - this is
    /// what determines whether a pull will succeed, not RAM. Honors OLLAMA_MODELS if set (checking both
    /// User and Machine scope, since Ollama may have been installed as a service); otherwise falls back
    /// to Ollama's documented default location.
    /// </summary>
    public (string Path, double? FreeGb) DetectOllamaStorage()
    {
        var path = Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(path))
            path = Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.Machine);
        if (string.IsNullOrWhiteSpace(path))
            path = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        if (string.IsNullOrWhiteSpace(path))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, ".ollama", "models");
        }

        double? freeGb = null;
        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady) freeGb = Math.Round(drive.AvailableFreeSpace / 1_073_741_824.0, 1);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't determine free space at Ollama's model storage path");
        }

        return (path, freeGb);
    }

    /// <summary>Uses nvidia-smi if present (works the same way on Windows and Linux). Returns null if there's no NVIDIA GPU or the driver/tool isn't installed.</summary>
    private static (double? VramGb, string? Name) DetectNvidiaGpu()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc is null) return (null, null);

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return (null, null);

            var firstLine = output.Split('\n')[0].Trim();
            var parts = firstLine.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !double.TryParse(parts[1], out var mib)) return (null, null);
            return (Math.Round(mib / 1024.0, 1), parts[0]);
        }
        catch
        {
            // nvidia-smi not on PATH - no NVIDIA GPU, or drivers not installed. Not an error.
            return (null, null);
        }
    }
}
