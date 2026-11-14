namespace AiPulse.Services;

public enum DeviceKind { Other, Ios, Android, Desktop }

/// <summary>Picks which install instructions to lead with. Only a hint for the page layout: every platform's steps are always shown.</summary>
public static class AppInstall
{
    public static DeviceKind Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return DeviceKind.Desktop;
        var ua = userAgent;
        // iPadOS 13+ Safari identifies as a Mac; touch-capable iPads are handled client-side, desktop Macs stay "Desktop".
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPod", StringComparison.OrdinalIgnoreCase))
            return DeviceKind.Ios;
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Android;
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) || ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) || ua.Contains("CrOS", StringComparison.OrdinalIgnoreCase))
            return DeviceKind.Desktop;
        return DeviceKind.Other;
    }

    /// <summary>The address people scan or share: this site's own /app page, built from the request origin so it is right on every deployment.</summary>
    public static string PageUrl(string baseUri) => baseUri.TrimEnd('/') + "/app";

    /// <summary>
    /// Installing needs a secure context. https hosts and localhost qualify; a plain-http address on a LAN does not, and the browser will
    /// only offer "add a shortcut" there instead of installing the app.
    /// </summary>
    public static bool CanInstallFrom(string baseUri)
    {
        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }
}
