namespace AiPulse.Models;

/// <summary>A persisted chat conversation with a specific local model — shown in the Playground's history sidebar.</summary>
public sealed class ChatSessionRecord
{
    public int Id { get; set; }
    public required string ModelName { get; set; }
    public string Title { get; set; } = "New chat";
    public string? SystemPrompt { get; set; }
    /// <summary>Owning user's username. Null for sessions created before multi-user support - treated as unowned/shared.</summary>
    public string? Username { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One message (user or assistant) in a chat session.</summary>
public sealed class ChatMessageRecord
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public required string Role { get; set; } // "user" or "assistant"
    public required string Content { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
