using System.Text.Json;
using System.Text.Json.Serialization;
using AiPulse.Models;
using AiPulse.Services.Ops;

// In AiPulse.Services (not .Ops) so Program.cs's existing using covers the two calls.
namespace AiPulse.Services;

/// <summary>Wiring for the admin operations console (<c>/ops</c>): two calls from Program.cs.</summary>
public static class OpsConsoleExtensions
{
    public const string AdminRole = UserRoles.Admin;

    private static readonly JsonSerializerOptions StatusJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary><c>builder.Services.AddOpsConsole();</c></summary>
    public static IServiceCollection AddOpsConsole(this IServiceCollection services)
    {
        services.AddSingleton<PollStatusTracker>();
        services.AddSingleton<OpsBackupStore>();
        services.AddSingleton<OpsStatusService>();
        services.AddSingleton<AuditLogQueryService>();
        services.AddHostedService<PollStatusAttacher>();
        return services;
    }

    /// <summary>
    /// <c>app.MapOpsEndpoints();</c> - admin-only machine endpoints. The public <c>/health</c> check stays as it is; this richer
    /// status carries paths, versions and error text, so it is never anonymous.
    /// </summary>
    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        var ops = app.MapGroup("/ops").RequireAuthorization(policy => policy.RequireRole(AdminRole));

        ops.MapGet("/status.json", (OpsStatusService status, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(status.GetSnapshot(), StatusJson);
        });

        ops.MapGet("/audit.csv", async (AuditLogQueryService audit, HttpContext http, string? action, string? user, string? from, string? to) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var csv = await audit.ExportCsvAsync(AuditLogFilter.FromQuery(action, user, from, to), http.RequestAborted);
            return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray(),
                "text/csv; charset=utf-8", $"aipulse-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
        });

        ops.MapGet("/backups/{name}", (string name, OpsBackupStore backups, HttpContext http) =>
        {
            var path = backups.PathOf(name);
            if (path is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(path, "application/zip", name);
        });

        return app;
    }

    /// <summary>Hooks the tracker to the aggregator's events at startup (it must be listening before the first poll).</summary>
    private sealed class PollStatusAttacher : IHostedService
    {
        private readonly PollStatusTracker _tracker;
        private readonly FeedAggregatorService _feeds;
        private readonly KnowledgeBaseService _kb;

        public PollStatusAttacher(PollStatusTracker tracker, FeedAggregatorService feeds, KnowledgeBaseService kb)
        {
            _tracker = tracker;
            _feeds = feeds;
            _kb = kb;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _tracker.Attach(_feeds, () => _kb.Sources.Count(s => s.Enabled));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _tracker.Detach(_feeds);
            return Task.CompletedTask;
        }
    }
}
