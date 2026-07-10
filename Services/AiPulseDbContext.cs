using AiPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services;

/// <summary>SQLite-backed store for dynamically managed sources (App_Data/aipulse.db).</summary>
public sealed class AiPulseDbContext : DbContext
{
    public AiPulseDbContext(DbContextOptions<AiPulseDbContext> options) : base(options) { }

    public DbSet<SourceRecord> Sources => Set<SourceRecord>();
    public DbSet<ModelUsageRecord> ModelUsage => Set<ModelUsageRecord>();
    public DbSet<ChatSessionRecord> ChatSessions => Set<ChatSessionRecord>();
    public DbSet<ChatMessageRecord> ChatMessages => Set<ChatMessageRecord>();
    public DbSet<AppUser> Users => Set<AppUser>();
}
