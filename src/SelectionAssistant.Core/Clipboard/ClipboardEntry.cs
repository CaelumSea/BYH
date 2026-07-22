namespace SelectionAssistant.Core.Clipboard;

/// <summary>
/// A single text clipboard history entry (R54 v1 — text only). Immutable data
/// record persisted as JSON by <see cref="ClipboardHistoryStore"/>. The
/// <see cref="Group"/> and <see cref="IsSensitive"/> fields are filled by
/// <see cref="ClipboardClassifier.Classify"/> at capture time; Pinning is a
/// user state toggled from the history window and persisted.
/// </summary>
public sealed record ClipboardEntry
{
    /// <summary>Stable id (new <see cref="Guid"/> per capture). Used as the
    /// JSON/Pin key and for dedup-free LRU ordering (see CapturedAt).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The captured text. Null only when the entry was captured from a
    /// non-text format (image, files) — in R54 v1 only text is captured so this
    /// is always non-null. Kept nullable for forward-compat with R54 v2.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Source process name (e.g. <c>chrome</c>), or null when it could
    /// not be determined. Used only for display in the history row.</summary>
    public string? SourceProcessName { get; init; }

    /// <summary>Capture timestamp (UTC). Drives LRU ordering — oldest non-pinned
    /// entry is evicted when <c>MaxEntries</c> is exceeded.</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>User-pinned entries are never evicted by LRU and sort first.
    /// Toggled from the history window (Ctrl+P) and persisted to JSON.</summary>
    public bool IsPinned { get; init; }

    /// <summary>Auto-classified group for the Smart auto-group badge.</summary>
    public ClipboardGroup Group { get; init; } = ClipboardGroup.Text;

    /// <summary>True when <see cref="ClipboardClassifier"/> matched a sensitive
    /// pattern (api_key/secret/token/…). The history window masks the preview
    /// with ●●●● until clicked.</summary>
    public bool IsSensitive { get; init; }
}
