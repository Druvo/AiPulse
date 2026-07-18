using AiPulse.Models;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiPulse.Services;

/// <summary>
/// Admin-configurable OAuth provider credentials (Google/GitHub), stored in the DB rather than
/// appsettings.json so an admin can enable/disable and rotate them from Settings.razor without touching
/// config files or restarting AiPulse. The actual authentication handlers (registered unconditionally in
/// Program.cs) read their ClientId/ClientSecret live from here on every request via
/// GoogleOptionsConfigurator/GitHubOptionsConfigurator below - SaveAsync invalidates the options cache so
/// a change takes effect on the very next sign-in attempt, no restart needed.
/// </summary>
public sealed class OAuthSettingsService
{
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private readonly IOptionsMonitorCache<GoogleOptions> _googleCache;
    private readonly IOptionsMonitorCache<GitHubAuthenticationOptions> _githubCache;

    public OAuthSettingsService(
        IDbContextFactory<AiPulseDbContext> dbFactory,
        IOptionsMonitorCache<GoogleOptions> googleCache,
        IOptionsMonitorCache<GitHubAuthenticationOptions> githubCache)
    {
        _dbFactory = dbFactory;
        _googleCache = googleCache;
        _githubCache = githubCache;
    }

    public async Task<List<OAuthProviderSettings>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.OAuthProviderSettings.OrderBy(s => s.Id).ToListAsync();
    }

    public async Task<OAuthProviderSettings?> GetAsync(string provider)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.OAuthProviderSettings.FirstOrDefaultAsync(s => s.Provider == provider);
    }

    /// <summary>True only when the provider is enabled AND has both a client id and secret set - the check Login.razor uses to decide whether to show that provider's button.</summary>
    public async Task<bool> IsUsableAsync(string provider)
    {
        var row = await GetAsync(provider);
        return row is { Enabled: true } && !string.IsNullOrWhiteSpace(row.ClientId) && !string.IsNullOrWhiteSpace(row.ClientSecret);
    }

    public async Task SaveAsync(string provider, bool enabled, string clientId, string clientSecret)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.OAuthProviderSettings.FirstOrDefaultAsync(s => s.Provider == provider);
        if (row is null)
        {
            row = new OAuthProviderSettings { Provider = provider };
            db.OAuthProviderSettings.Add(row);
        }
        row.Enabled = enabled;
        row.ClientId = clientId.Trim();
        row.ClientSecret = clientSecret.Trim();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (provider == OAuthProviders.Google)
            _googleCache.TryRemove(GoogleDefaults.AuthenticationScheme);
        else if (provider == OAuthProviders.GitHub)
            _githubCache.TryRemove(GitHubAuthenticationDefaults.AuthenticationScheme);
    }
}

/// <summary>
/// Supplies GoogleOptions.ClientId/ClientSecret from the DB at options-creation time, instead of a fixed
/// appsettings value. Falls back to a non-empty placeholder when unconfigured - OAuthOptions.Validate()
/// runs on *every* request (ASP.NET Core's authentication middleware probes every registered
/// IAuthenticationRequestHandler scheme's CallbackPath on each request, which resolves and validates that
/// scheme's options first), so an empty ClientId/ClientSecret would break the entire app, not just Google
/// sign-in. The placeholder is never actually sent to Google: Login.razor only shows the button, and the
/// /auth/external/google endpoint only issues the challenge, when OAuthSettingsService.IsUsableAsync says
/// the provider is genuinely enabled and configured.
/// </summary>
internal sealed class GoogleOptionsConfigurator(IDbContextFactory<AiPulseDbContext> dbFactory) : IConfigureNamedOptions<GoogleOptions>
{
    public void Configure(string? name, GoogleOptions options)
    {
        if (name != GoogleDefaults.AuthenticationScheme) return;
        using var db = dbFactory.CreateDbContext();
        var row = db.OAuthProviderSettings.FirstOrDefault(s => s.Provider == OAuthProviders.Google);
        options.ClientId = string.IsNullOrWhiteSpace(row?.ClientId) ? "not-configured" : row.ClientId;
        options.ClientSecret = string.IsNullOrWhiteSpace(row?.ClientSecret) ? "not-configured" : row.ClientSecret;
    }

    public void Configure(GoogleOptions options) => Configure(Options.DefaultName, options);
}

/// <summary>GitHub counterpart to <see cref="GoogleOptionsConfigurator"/> - same reasoning throughout.</summary>
internal sealed class GitHubOptionsConfigurator(IDbContextFactory<AiPulseDbContext> dbFactory) : IConfigureNamedOptions<GitHubAuthenticationOptions>
{
    public void Configure(string? name, GitHubAuthenticationOptions options)
    {
        if (name != GitHubAuthenticationDefaults.AuthenticationScheme) return;
        using var db = dbFactory.CreateDbContext();
        var row = db.OAuthProviderSettings.FirstOrDefault(s => s.Provider == OAuthProviders.GitHub);
        options.ClientId = string.IsNullOrWhiteSpace(row?.ClientId) ? "not-configured" : row.ClientId;
        options.ClientSecret = string.IsNullOrWhiteSpace(row?.ClientSecret) ? "not-configured" : row.ClientSecret;
    }

    public void Configure(GitHubAuthenticationOptions options) => Configure(Options.DefaultName, options);
}
