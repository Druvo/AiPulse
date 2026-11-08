using System.Globalization;
using System.Text;
using AiPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services.Ops;

/// <summary>Filters for the audit-log viewer and its CSV export. Dates are whole UTC days, both ends inclusive.</summary>
public sealed record AuditLogFilter(string? Action = null, string? User = null, DateOnly? From = null, DateOnly? To = null)
{
    public static AuditLogFilter FromQuery(string? action, string? user, string? from, string? to) => new(
        string.IsNullOrWhiteSpace(action) ? null : action.Trim(),
        string.IsNullOrWhiteSpace(user) ? null : user.Trim(),
        DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null,
        DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null);

    /// <summary>The same filter as a query string (no leading '?'), for links such as the CSV export.</summary>
    public string ToQueryString()
    {
        var parts = new List<string>();
        if (Action is not null) parts.Add("action=" + Uri.EscapeDataString(Action));
        if (User is not null) parts.Add("user=" + Uri.EscapeDataString(User));
        if (From is { } f) parts.Add("from=" + f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (To is { } t) parts.Add("to=" + t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return string.Join("&", parts);
    }
}

/// <summary>
/// Read-only queries over the AuditLog table (written by UserService and ExternalAuthService) for the operations console.
/// Action and user filters run in SQL. The date range is applied in memory: EF Core's SQLite provider cannot translate comparisons on
/// DateTimeOffset columns (the same limitation UserService.GetAuditLogAsync works around by ordering on Id), and the rows are small.
/// </summary>
public sealed class AuditLogQueryService
{
    /// <summary>Hard cap on a CSV export, so one click cannot build an unbounded response.</summary>
    public const int MaxExportRows = 50_000;

    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;

    public AuditLogQueryService(IDbContextFactory<AiPulseDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<IReadOnlyList<string>> GetActionsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AuditLog.Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<AuditLogEntry> Rows, int Total)> QueryAsync(AuditLogFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var all = await LoadAsync(filter, ct);
        return (all.Skip((page - 1) * pageSize).Take(pageSize).ToList(), all.Count);
    }

    public async Task<string> ExportCsvAsync(AuditLogFilter filter, CancellationToken ct = default)
    {
        var rows = await LoadAsync(filter, ct);
        var sb = new StringBuilder();
        sb.Append("Id,TimestampUtc,Action,ActorUserId,ActorUsername,TargetUserId,TargetUsername,Detail\r\n");
        foreach (var r in rows.Take(MaxExportRows))
        {
            sb.Append(r.Id.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(r.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))).Append(',')
              .Append(Csv(r.Action)).Append(',')
              .Append(r.ActorUserId?.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(r.ActorUsername)).Append(',')
              .Append(r.TargetUserId?.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(r.TargetUsername)).Append(',')
              .Append(Csv(r.Detail)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// RFC 4180 quoting, plus a leading apostrophe on anything a spreadsheet would run as a formula: usernames and details are
    /// user-controlled, so a value like "=HYPERLINK(...)" must open as text.
    /// </summary>
    public static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || value.StartsWith(' ') || value.EndsWith(' ')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private async Task<List<AuditLogEntry>> LoadAsync(AuditLogFilter filter, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<AuditLogEntry> q = db.AuditLog.AsNoTracking();
        if (filter.Action is { } action) q = q.Where(a => a.Action == action);
        if (filter.User is { } user)
        {
            var u = user.ToLower();
            q = q.Where(a => (a.ActorUsername != null && a.ActorUsername.ToLower().Contains(u))
                          || (a.TargetUsername != null && a.TargetUsername.ToLower().Contains(u)));
        }

        var rows = await q.OrderByDescending(a => a.Id).ToListAsync(ct);
        if (filter.From is null && filter.To is null) return rows;

        var from = filter.From is { } f ? new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : DateTimeOffset.MinValue;
        var to = filter.To is { } t ? new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : DateTimeOffset.MaxValue;
        return rows.Where(r => r.TimestampUtc >= from && r.TimestampUtc < to).ToList();
    }
}
