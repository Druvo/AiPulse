using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using AiPulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace AiPulse.Tests.E2E;

/// <summary>One routable Razor page, found by reflection so pages added later are covered without editing the tests.</summary>
public sealed record AppRoute(string Template, Type Page, bool NeedsSignIn, string[] Roles)
{
    public bool AdminOnly => Roles.Length > 0 && !Roles.Contains("User", StringComparer.OrdinalIgnoreCase);
    public override string ToString() => $"{Template} ({Page.Name})";
}

public static class AppRoutes
{
    public static readonly IReadOnlyList<AppRoute> All = typeof(PublicSite).Assembly.GetTypes()
        .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r =>
        {
            var auth = t.GetCustomAttributes<AuthorizeAttribute>().ToList();
            var roles = auth.SelectMany(a => (a.Roles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
            return new AppRoute(r.Template, t, auth.Count > 0, roles);
        }))
        .OrderBy(r => r.Template, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>A concrete URL for a template, or null when the template has a parameter the tests have no sample value for.</summary>
    public static string? Url(string template)
    {
        var url = Regex.Replace(template, @"\{(\w+)(:[^}]*)?\??\}", m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "username" => AiPulseAppFactory.AdminUser,
            "tag" => AiPulseAppFactory.SeededTag,
            _ => "\0",
        });
        return url.Contains('\0') ? null : url;
    }

    public static IEnumerable<object[]> SignedInOnly() =>
        All.Where(r => r.NeedsSignIn && !r.AdminOnly && Url(r.Template) is not null).Select(r => new object[] { r.Template });

    public static IEnumerable<object[]> AdminOnlyRoutes() =>
        All.Where(r => r.AdminOnly && Url(r.Template) is not null).Select(r => new object[] { r.Template });

    public static IEnumerable<object[]> PublicRoutes() =>
        All.Where(r => !r.NeedsSignIn && Url(r.Template) is not null).Select(r => new object[] { r.Template });

    public static string Title(string html)
    {
        var m = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
    }

    /// <summary>Visible text of the first h1, tags stripped.</summary>
    public static string H1(string html)
    {
        var m = Regex.Match(html, "<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>|<!--.*?-->", "")).Trim() : "";
    }
}
