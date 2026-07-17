namespace AiPulse.Services;

/// <summary>One transient notification, rendered by <see cref="Components.Shared.ToastHost"/>.</summary>
public sealed class ToastMessage
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Text { get; init; }
    public string? ActionText { get; init; }
    public Func<Task>? OnAction { get; init; }
    public int DurationMs { get; init; } = 5000;
}

/// <summary>
/// Scoped (one per signed-in circuit, same as ReadingStateService) home for transient confirmations - the
/// single toast host every page can call instead of each one hand-rolling its own inline alert with its
/// own clear-on-next-action logic. Reserve inline `alert-*` divs for state that should stay visible
/// (validation errors); anything transient ("Password changed.", "Exported to ...", "Marked N items read")
/// goes through here instead.
/// </summary>
public sealed class ToastService
{
    public event Action<ToastMessage>? OnShow;
    public event Action<Guid>? OnDismiss;

    public void Show(string text, int durationMs = 5000) =>
        OnShow?.Invoke(new ToastMessage { Text = text, DurationMs = durationMs });

    /// <summary>A toast with an inline action button (e.g. "Undo") - the button's own click dismisses it immediately, running <paramref name="onAction"/> first.</summary>
    public void ShowWithAction(string text, string actionText, Func<Task> onAction, int durationMs = 6000) =>
        OnShow?.Invoke(new ToastMessage { Text = text, ActionText = actionText, OnAction = onAction, DurationMs = durationMs });

    public void Dismiss(Guid id) => OnDismiss?.Invoke(id);
}
