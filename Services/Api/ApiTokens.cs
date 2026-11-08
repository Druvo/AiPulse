using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services.Api;

/// <summary>One personal API token as stored. The token itself is never stored, only its SHA-256 hash and a short display prefix.</summary>
public sealed record ApiTokenInfo(
    int Id, int UserId, string Name, string Prefix, string Scopes,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt)
{
    public bool IsRevoked => RevokedAt is not null;
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;
    public bool IsActive(DateTimeOffset now) => !IsRevoked && !IsExpired(now);
    public IReadOnlyList<string> ScopeList => Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>The result of creating a token: the plaintext (shown to the user exactly once) and what was stored.</summary>
public sealed record ApiTokenCreated(string Token, ApiTokenInfo Info);

public enum ApiTokenFailure { None, Missing, Malformed, Unknown, Revoked, Expired, UserInactive, InsufficientScope }

/// <summary>Who a valid bearer token speaks for.</summary>
public sealed record ApiPrincipal(
    int UserId, string Username, string Role, int TokenId, string TokenName, string Prefix,
    IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt);

public sealed record ApiTokenValidation(ApiPrincipal? Principal, ApiTokenFailure Failure)
{
    public bool IsValid => Principal is not null && Failure == ApiTokenFailure.None;
    public static ApiTokenValidation Fail(ApiTokenFailure f) => new(null, f);
}

/// <summary>
/// The token format: "aip_" followed by 43 base64url characters (32 random bytes). The fixed prefix lets secret scanners
/// and people recognise an AiPulse token; the first 12 characters are kept as a non-secret label so a user can tell their
/// tokens apart in Settings. Pure functions, no I/O.
/// </summary>
public static class ApiTokenFormat
{
    public const string Marker = "aip_";
    public const int RandomBytes = 32;
    public const int BodyLength = 43; // base64url of 32 bytes, no padding
    public const int Length = 4 + BodyLength;
    public const int DisplayPrefixLength = 12;

    /// <summary>The only scope today. Every token is read-only; the API has no write endpoints.</summary>
    public const string ReadScope = "read";
    public static readonly IReadOnlyList<string> KnownScopes = new[] { ReadScope };

    public static string Generate() => Marker + Base64Url(RandomNumberGenerator.GetBytes(RandomBytes));

    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != Length || !token.StartsWith(Marker, StringComparison.Ordinal)) return false;
        for (var i = Marker.Length; i < token.Length; i++)
        {
            var c = token[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
        }
        return true;
    }

    /// <summary>Lowercase hex SHA-256. Tokens carry 256 bits of randomness, so a fast unsalted hash is the right tool (no dictionary to attack).</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string DisplayPrefix(string token) => token.Length <= DisplayPrefixLength ? token : token[..DisplayPrefixLength];

    /// <summary>Reads "Bearer &lt;token&gt;" (scheme case-insensitive). Returns null when the header is absent or uses another scheme.</summary>
    public static string? FromAuthorizationHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var trimmed = header.Trim();
        const string scheme = "Bearer ";
        if (trimmed.Length <= scheme.Length || !trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        return trimmed[scheme.Length..].Trim();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Create, list, revoke and validate personal API tokens. Stored in the ApiTokens table of App_Data/aipulse.db through plain
/// SQL on the app's own SQLite connection, so the shared EF model (and every other feature's migrations) is untouched. The table
/// is created on first use with the same "check sqlite_master, CREATE if missing, PRAGMA table_info and ALTER in any missing
/// column" pattern as UserService.EnsureSchemaAsync, so it works on fresh installs and on databases created long before tokens
/// existed. Stateless apart from that one-time schema check, so both the API and the Settings component can construct one.
/// </summary>
public sealed class ApiTokenService
{
    public const int MaxActiveTokensPerUser = 20;
    public const int MaxNameLength = 60;
    public static readonly TimeSpan MinLifetime = TimeSpan.FromDays(1);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(366);
    /// <summary>LastUsedAt is a hint, not an audit log: write it at most once a minute per token so busy clients do not turn every read into a write.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private static readonly HashSet<string> EnsuredDatabases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim SchemaLock = new(1, 1);

    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private readonly TimeProvider _time;

    public ApiTokenService(IDbContextFactory<AiPulseDbContext> dbFactory, TimeProvider? time = null)
    {
        _dbFactory = dbFactory;
        _time = time ?? TimeProvider.System;
    }

    public static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return "Give the token a name, e.g. \"laptop script\".";
        if (trimmed.Length > MaxNameLength) return $"Keep the name to {MaxNameLength} characters or fewer.";
        if (trimmed.Any(char.IsControl)) return "The name cannot contain control characters.";
        return null;
    }

    public async Task<(ApiTokenCreated? Created, string? Error)> CreateAsync(int userId, string? name, TimeSpan? lifetime, IEnumerable<string>? scopes = null)
    {
        var nameError = ValidateName(name);
        if (nameError is not null) return (null, nameError);
        if (lifetime is { } l && (l < MinLifetime || l > MaxLifetime))
            return (null, "Expiry must be between 1 day and 1 year, or never.");

        var scopeList = (scopes ?? new[] { ApiTokenFormat.ReadScope }).Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        if (scopeList.Count == 0 || scopeList.Any(s => !ApiTokenFormat.KnownScopes.Contains(s)))
            return (null, "Unknown scope. The only scope is \"read\".");

        var now = _time.GetUtcNow();
        var existing = await ListAsync(userId);
        var trimmedName = name!.Trim();
        if (existing.Count(t => t.IsActive(now)) >= MaxActiveTokensPerUser)
            return (null, $"You already have {MaxActiveTokensPerUser} active tokens. Revoke one you no longer use first.");
        if (existing.Any(t => t.IsActive(now) && string.Equals(t.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
            return (null, "You already have an active token with that name.");

        var token = ApiTokenFormat.Generate();
        var prefix = ApiTokenFormat.DisplayPrefix(token);
        var expires = lifetime is { } life ? now + life : (DateTimeOffset?)null;
        var scopeText = string.Join(' ', scopeList);

        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "ApiTokens" ("UserId", "Name", "Prefix", "TokenHash", "Scopes", "CreatedAt", "ExpiresAt")
            VALUES (@user, @name, @prefix, @hash, @scopes, @created, @expires);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@user", userId);
        cmd.Parameters.AddWithValue("@name", trimmedName);
        cmd.Parameters.AddWithValue("@prefix", prefix);
        cmd.Parameters.AddWithValue("@hash", ApiTokenFormat.Hash(token));
        cmd.Parameters.AddWithValue("@scopes", scopeText);
        cmd.Parameters.AddWithValue("@created", Format(now));
        cmd.Parameters.AddWithValue("@expires", (object?)FormatOrNull(expires) ?? DBNull.Value);
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        return (new ApiTokenCreated(token, new ApiTokenInfo(id, userId, trimmedName, prefix, scopeText, now, expires, null, null)), null);
    }

    /// <summary>The user's tokens, newest first, including revoked and expired ones (Settings shows their status).</summary>
    public async Task<IReadOnlyList<ApiTokenInfo>> ListAsync(int userId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Id", "UserId", "Name", "Prefix", "Scopes", "CreatedAt", "ExpiresAt", "LastUsedAt", "RevokedAt"
            FROM "ApiTokens" WHERE "UserId" = @user ORDER BY "Id" DESC
            """;
        cmd.Parameters.AddWithValue("@user", userId);
        var list = new List<ApiTokenInfo>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(ReadInfo(reader));
        return list;
    }

    /// <summary>Revokes one of the user's own tokens. False when it does not exist, belongs to someone else, or is already revoked.</summary>
    public async Task<bool> RevokeAsync(int userId, int tokenId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """UPDATE "ApiTokens" SET "RevokedAt" = @now WHERE "Id" = @id AND "UserId" = @user AND "RevokedAt" IS NULL""";
        cmd.Parameters.AddWithValue("@now", Format(_time.GetUtcNow()));
        cmd.Parameters.AddWithValue("@id", tokenId);
        cmd.Parameters.AddWithValue("@user", userId);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    /// <summary>Revokes every active token a user has (e.g. "sign out everywhere"). Returns how many were revoked.</summary>
    public async Task<int> RevokeAllAsync(int userId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """UPDATE "ApiTokens" SET "RevokedAt" = @now WHERE "UserId" = @user AND "RevokedAt" IS NULL""";
        cmd.Parameters.AddWithValue("@now", Format(_time.GetUtcNow()));
        cmd.Parameters.AddWithValue("@user", userId);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Checks a presented token: well formed, known (by hash), not revoked, not expired, owner still an approved account, and
    /// carrying <paramref name="requiredScope"/>. On success, records LastUsedAt (at most once per <see cref="LastUsedResolution"/>).
    /// </summary>
    public async Task<ApiTokenValidation> ValidateAsync(string? token, string requiredScope = ApiTokenFormat.ReadScope)
    {
        if (string.IsNullOrWhiteSpace(token)) return ApiTokenValidation.Fail(ApiTokenFailure.Missing);
        if (!ApiTokenFormat.IsWellFormed(token)) return ApiTokenValidation.Fail(ApiTokenFailure.Malformed);

        await using var conn = await OpenAsync();
        ApiTokenInfo info;
        string? username, role, status;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT t."Id", t."UserId", t."Name", t."Prefix", t."Scopes", t."CreatedAt", t."ExpiresAt", t."LastUsedAt", t."RevokedAt",
                       u."Username", u."Role", u."Status"
                FROM "ApiTokens" t LEFT JOIN "Users" u ON u."Id" = t."UserId"
                WHERE t."TokenHash" = @hash
                """;
            cmd.Parameters.AddWithValue("@hash", ApiTokenFormat.Hash(token));
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return ApiTokenValidation.Fail(ApiTokenFailure.Unknown);
            info = ReadInfo(reader);
            username = reader.IsDBNull(9) ? null : reader.GetString(9);
            role = reader.IsDBNull(10) ? null : reader.GetString(10);
            status = reader.IsDBNull(11) ? null : reader.GetString(11);
        }

        var now = _time.GetUtcNow();
        if (info.IsRevoked) return ApiTokenValidation.Fail(ApiTokenFailure.Revoked);
        if (info.IsExpired(now)) return ApiTokenValidation.Fail(ApiTokenFailure.Expired);
        if (username is null || status != Models.UserStatuses.Approved) return ApiTokenValidation.Fail(ApiTokenFailure.UserInactive);
        if (!info.ScopeList.Contains(requiredScope, StringComparer.OrdinalIgnoreCase)) return ApiTokenValidation.Fail(ApiTokenFailure.InsufficientScope);

        if (info.LastUsedAt is null || now - info.LastUsedAt.Value >= LastUsedResolution)
        {
            await using var touch = conn.CreateCommand();
            touch.CommandText = """UPDATE "ApiTokens" SET "LastUsedAt" = @now WHERE "Id" = @id""";
            touch.Parameters.AddWithValue("@now", Format(now));
            touch.Parameters.AddWithValue("@id", info.Id);
            await touch.ExecuteNonQueryAsync();
        }

        return new ApiTokenValidation(
            new ApiPrincipal(info.UserId, username, role ?? Models.UserRoles.User, info.Id, info.Name, info.Prefix, info.ScopeList, info.ExpiresAt),
            ApiTokenFailure.None);
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        string connectionString;
        await using (var db = await _dbFactory.CreateDbContextAsync())
            connectionString = db.Database.GetConnectionString() ?? throw new InvalidOperationException("No SQLite connection string configured.");

        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await EnsureSchemaAsync(conn, connectionString);
        return conn;
    }

    /// <summary>Creates ApiTokens (and its indexes) when missing and adds any column a newer version introduced. Idempotent.</summary>
    internal static async Task EnsureSchemaAsync(SqliteConnection conn, string key)
    {
        lock (EnsuredDatabases) if (EnsuredDatabases.Contains(key)) return;
        await SchemaLock.WaitAsync();
        try
        {
            lock (EnsuredDatabases) if (EnsuredDatabases.Contains(key)) return;

            await using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ApiTokens'";
                var exists = Convert.ToInt64(await check.ExecuteScalarAsync()) > 0;
                if (!exists)
                {
                    await using var create = conn.CreateCommand();
                    create.CommandText = """
                        CREATE TABLE IF NOT EXISTS "ApiTokens" (
                            "Id" INTEGER NOT NULL CONSTRAINT "PK_ApiTokens" PRIMARY KEY AUTOINCREMENT,
                            "UserId" INTEGER NOT NULL,
                            "Name" TEXT NOT NULL,
                            "Prefix" TEXT NOT NULL,
                            "TokenHash" TEXT NOT NULL,
                            "Scopes" TEXT NOT NULL DEFAULT 'read',
                            "CreatedAt" TEXT NOT NULL,
                            "ExpiresAt" TEXT NULL,
                            "LastUsedAt" TEXT NULL,
                            "RevokedAt" TEXT NULL
                        )
                        """;
                    await create.ExecuteNonQueryAsync();
                }
                else
                {
                    var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    await using (var pragma = conn.CreateCommand())
                    {
                        pragma.CommandText = "PRAGMA table_info(ApiTokens)";
                        await using var reader = await pragma.ExecuteReaderAsync();
                        while (await reader.ReadAsync()) existingCols.Add(reader.GetString(reader.GetOrdinal("name")));
                    }
                    var columns = new (string Name, string Def)[]
                    {
                        ("Scopes", "TEXT NOT NULL DEFAULT 'read'"),
                        ("ExpiresAt", "TEXT NULL"),
                        ("LastUsedAt", "TEXT NULL"),
                        ("RevokedAt", "TEXT NULL"),
                    };
                    foreach (var (name, def) in columns)
                    {
                        if (existingCols.Contains(name)) continue;
                        await using var alter = conn.CreateCommand();
                        alter.CommandText = $"""ALTER TABLE "ApiTokens" ADD COLUMN "{name}" {def}""";
                        await alter.ExecuteNonQueryAsync();
                    }
                }
            }

            await using (var index = conn.CreateCommand())
            {
                index.CommandText = """
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ApiTokens_TokenHash" ON "ApiTokens" ("TokenHash");
                    CREATE INDEX IF NOT EXISTS "IX_ApiTokens_UserId" ON "ApiTokens" ("UserId");
                    """;
                await index.ExecuteNonQueryAsync();
            }

            lock (EnsuredDatabases) EnsuredDatabases.Add(key);
        }
        finally
        {
            SchemaLock.Release();
        }
    }

    private static ApiTokenInfo ReadInfo(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4),
        Parse(r.GetString(5)), ParseOrNull(r, 6), ParseOrNull(r, 7), ParseOrNull(r, 8));

    private static string Format(DateTimeOffset d) => d.ToUniversalTime().ToString("o");
    private static string? FormatOrNull(DateTimeOffset? d) => d is { } v ? Format(v) : null;
    private static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? ParseOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Parse(r.GetString(i));
}
