namespace AiPulse.Models;

public static class WebSubStatuses
{
    /// <summary>Subscribe request sent to the hub; waiting for its verification GET callback.</summary>
    public const string Pending = "Pending";
    /// <summary>Hub verified the subscription; content pushes will be accepted until LeaseExpiresAt.</summary>
    public const string Verified = "Verified";
    /// <summary>The hub rejected the subscription request or verification failed.</summary>
    public const string Failed = "Failed";
}

/// <summary>
/// EF Core-mapped WebSub (PubSubHubbub) subscription for one source. A hub pushes fresh content to our
/// callback URL instead of us polling - see WebSubService for the subscribe/verify/renew flow.
/// </summary>
public sealed class WebSubSubscription
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    /// <summary>The feed URL we subscribed to (must match what the hub reports back on verification).</summary>
    public required string Topic { get; set; }
    public required string HubUrl { get; set; }
    /// <summary>Per-subscription secret used to HMAC-sign pushed content, so forged pushes can be rejected.</summary>
    public required string Secret { get; set; }
    public string Status { get; set; } = WebSubStatuses.Pending;
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastPushAt { get; set; }
    public int FailCount { get; set; }
}
