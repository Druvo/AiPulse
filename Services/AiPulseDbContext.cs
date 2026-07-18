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
    public DbSet<WebSubSubscription> WebSubSubscriptions => Set<WebSubSubscription>();
    public DbSet<FreeApiRecord> FreeApis => Set<FreeApiRecord>();
    public DbSet<FreeApiCandidate> FreeApiCandidates => Set<FreeApiCandidate>();
    public DbSet<DiscoveryRunLogEntry> DiscoveryRunLog => Set<DiscoveryRunLogEntry>();
    public DbSet<ContentCandidate> ContentCandidates => Set<ContentCandidate>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
}
