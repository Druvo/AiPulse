using AiPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services;

/// <summary>
/// Persists Playground chat sessions and their messages in SQLite, so old conversations survive app
/// restarts. Same EnsureCreated caveat as ModelUsageService: existing databases don't automatically gain
/// new tables, so schema is reconciled with a one-time raw-SQL check on first use.
/// </summary>
public sealed class ChatHistoryService
{
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private bool _schemaEnsured;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    public ChatHistoryService(IDbContextFactory<AiPulseDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<ChatSessionRecord>> GetSessionsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var all = await db.ChatSessions.ToListAsync();
        return all.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public async Task<List<ChatMessageRecord>> GetMessagesAsync(int sessionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var msgs = await db.ChatMessages.Where(m => m.SessionId == sessionId).ToListAsync();
        return msgs.OrderBy(m => m.Id).ToList();
    }

    public async Task<ChatSessionRecord> CreateSessionAsync(string modelName, string? systemPrompt = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var now = DateTimeOffset.Now;
        var session = new ChatSessionRecord
        {
            ModelName = modelName,
            Title = "New chat",
            SystemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    public async Task RenameSessionAsync(int sessionId, string title)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var session = await db.ChatSessions.FindAsync(sessionId);
        if (session is not null)
        {
            session.Title = string.IsNullOrWhiteSpace(title) ? session.Title : title.Trim();
            await db.SaveChangesAsync();
        }
    }

    public async Task<ChatMessageRecord> AddMessageAsync(int sessionId, string role, string content)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var message = new ChatMessageRecord { SessionId = sessionId, Role = role, Content = content, CreatedAt = DateTimeOffset.Now };
        db.ChatMessages.Add(message);

        var session = await db.ChatSessions.FindAsync(sessionId);
        if (session is not null)
        {
            session.UpdatedAt = DateTimeOffset.Now;
            if (session.Title == "New chat" && role == "user")
                session.Title = content.Length > 50 ? content[..50] + "…" : content;
        }

        await db.SaveChangesAsync();
        return message;
    }

    public async Task DeleteMessageAsync(int messageId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var message = await db.ChatMessages.FindAsync(messageId);
        if (message is not null)
        {
            db.ChatMessages.Remove(message);
            await db.SaveChangesAsync();
        }
    }

    public async Task DeleteSessionAsync(int sessionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var msgs = db.ChatMessages.Where(m => m.SessionId == sessionId);
        db.ChatMessages.RemoveRange(msgs);

        var session = await db.ChatSessions.FindAsync(sessionId);
        if (session is not null) db.ChatSessions.Remove(session);

        await db.SaveChangesAsync();
    }

    private async Task EnsureSchemaAsync(AiPulseDbContext db)
    {
        if (_schemaEnsured) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaEnsured) return;

            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            await using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='ChatSessions'";
                var exists = await check.ExecuteScalarAsync() is not null;
                if (!exists)
                {
                    await using var create = conn.CreateCommand();
                    create.CommandText = """
                        CREATE TABLE "ChatSessions" (
                            "Id" INTEGER NOT NULL CONSTRAINT "PK_ChatSessions" PRIMARY KEY AUTOINCREMENT,
                            "ModelName" TEXT NOT NULL,
                            "Title" TEXT NOT NULL,
                            "SystemPrompt" TEXT NULL,
                            "CreatedAt" TEXT NOT NULL,
                            "UpdatedAt" TEXT NOT NULL
                        )
                        """;
                    await create.ExecuteNonQueryAsync();
                }
                else
                {
                    // Existing DB from before SystemPrompt existed - add the column if it's missing.
                    await using var pragma = conn.CreateCommand();
                    pragma.CommandText = "PRAGMA table_info(ChatSessions)";
                    await using var reader = await pragma.ExecuteReaderAsync();
                    var hasSystemPrompt = false;
                    while (await reader.ReadAsync())
                    {
                        if (string.Equals(reader.GetString(reader.GetOrdinal("name")), "SystemPrompt", StringComparison.OrdinalIgnoreCase))
                        {
                            hasSystemPrompt = true;
                            break;
                        }
                    }
                    await reader.DisposeAsync();

                    if (!hasSystemPrompt)
                    {
                        await using var alter = conn.CreateCommand();
                        alter.CommandText = """ALTER TABLE "ChatSessions" ADD COLUMN "SystemPrompt" TEXT NULL""";
                        await alter.ExecuteNonQueryAsync();
                    }
                }
            }

            await using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='ChatMessages'";
                var exists = await check.ExecuteScalarAsync() is not null;
                if (!exists)
                {
                    await using var create = conn.CreateCommand();
                    create.CommandText = """
                        CREATE TABLE "ChatMessages" (
                            "Id" INTEGER NOT NULL CONSTRAINT "PK_ChatMessages" PRIMARY KEY AUTOINCREMENT,
                            "SessionId" INTEGER NOT NULL,
                            "Role" TEXT NOT NULL,
                            "Content" TEXT NOT NULL,
                            "CreatedAt" TEXT NOT NULL
                        )
                        """;
                    await create.ExecuteNonQueryAsync();
                }
            }

            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
