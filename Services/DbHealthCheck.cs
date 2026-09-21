using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiPulse.Services;

/// <summary>Confirms the SQLite file is actually reachable and queryable, not just that the process is running.</summary>
public sealed class DbHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;

    public DbHealthCheck(IDbContextFactory<AiPulseDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQLite database is not reachable.", ex);
        }
    }
}
