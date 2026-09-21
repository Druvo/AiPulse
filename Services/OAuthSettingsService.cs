using System.Security.Cryptography;
using AiPulse.Models;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
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
///
/// ClientSecret is encrypted at rest (Data Protection API) so a copy of the SQLite file - a backup zip,
/// a stolen disk image - doesn't hand over a usable OAuth secret in plain text. ClientId isn't sensitive
/// on its own and stays plaintext.
/// </summary>
public sealed class OAuthSettingsService
{
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private readonly IOptionsMonitorCache<GoogleOptions> _googleCache;
    private readonly IOptionsMonitorCache<GitHubAuthenticationOptions> _githubCache;
    private readonly IDataProtector _protector;

    public OAuthSettingsService(
        IDbContextFactory<AiPulseDbContext> dbFactory,
        IOptionsMonitorCache<GoogleOptions> googleCache,
        IOptionsMonitorCache<GitHubAuthenticationOptions> githubCache,
        IDataProtectionProvider dataProtection)
    {
        _dbFactory = dbFactory;
        _googleCache = googleCache;
        _githubCache = githubCache;
        _protector = dataProtection.CreateProtector(OAuthSecretProtection.Purpose);
    }

    public async Task<List<OAuthProviderSettings>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.OAuthProviderSettings.OrderBy(s => s.Id).ToListAsync();
        foreach (var row in rows) row.ClientSecret = OAuthSecretProtection.Unprotect(_protector, row.ClientSecret);
        return rows;
    }

    public async Task<OAuthProviderSettings?> GetAsync(string provider)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.OAuthProviderSettings.FirstOrDefaultAsync(s => s.Provider == provider);
        if (row is not null) row.ClientSecret = OAuthSecretProtection.Unprotect(_protector, row.ClientSecret);
        return row;
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
        row.ClientSecret = _protector.Protect(clientSecret.Trim());
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (provider == OAuthProviders.Google)
            _googleCache.TryRemove(GoogleDefaults.AuthenticationScheme);
        else if (provider == OAuthProviders.GitHub)
            _githubCache.TryRemove(GitHubAuthenticationDefaults.AuthenticationScheme);
    }
}

/// <summary>Shared purpose string + decrypt-with-legacy-fallback, used by OAuthSettingsService and both
/// Configurator classes below so they all agree on what "encrypted" means.</summary>
internal static class OAuthSecretProtection
{
    public const string Purpose = "AiPulse.OAuthClientSecret.v1";

    /// <summary>Falls back to returning the input as-is if it doesn't decrypt - covers rows written
    /// before this encryption was added, without needing a one-off migration step.</summary>
    public static string Unprotect(IDataProtector protector, string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            return protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return value;
        }
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
internal sealed class GoogleOptionsConfigurator(IDbContextFactory<AiPulseDbContext> dbFactory, IDataProtectionProvider dataProtection) : IConfigureNamedOptions<GoogleOptions>
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector(OAuthSecretProtection.Purpose);

    public void Configure(string? name, GoogleOptions options)
    {
        if (name != GoogleDefaults.AuthenticationScheme) return;
        using var db = dbFactory.CreateDbContext();
        var row = db.OAuthProviderSettings.FirstOrDefault(s => s.Provider == OAuthProviders.Google);
        options.ClientId = string.IsNullOrWhiteSpace(row?.ClientId) ? "not-configured" : row.ClientId;
        var secret = row is null ? "" : OAuthSecretProtection.Unprotect(_protector, row.ClientSecret);
        options.ClientSecret = string.IsNullOrWhiteSpace(secret) ? "not-configured" : secret;
    }

    public void Configure(GoogleOptions options) => Configure(Options.DefaultName, options);
}

/// <summary>GitHub counterpart to <see cref="GoogleOptionsConfigurator"/> - same reasoning throughout.</summary>
internal sealed class GitHubOptionsConfigurator(IDbContextFactory<AiPulseDbContext> dbFactory, IDataProtectionProvider dataProtection) : IConfigureNamedOptions<GitHubAuthenticationOptions>
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector(OAuthSecretProtection.Purpose);

    public void Configure(string? name, GitHubAuthenticationOptions options)
    {
        if (name != GitHubAuthenticationDefaults.AuthenticationScheme) return;
        using var db = dbFactory.CreateDbContext();
        var row = db.OAuthProviderSettings.FirstOrDefault(s => s.Provider == OAuthProviders.GitHub);
        options.ClientId = string.IsNullOrWhiteSpace(row?.ClientId) ? "not-configured" : row.ClientId;
        var secret = row is null ? "" : OAuthSecretProtection.Unprotect(_protector, row.ClientSecret);
        options.ClientSecret = string.IsNullOrWhiteSpace(secret) ? "not-configured" : secret;
    }

    public void Configure(GitHubAuthenticationOptions options) => Configure(Options.DefaultName, options);
}
