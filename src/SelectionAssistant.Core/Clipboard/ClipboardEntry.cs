namespace SelectionAssistant.Core.Clipboard;

/// <summary>
/// A single clipboard history entry (R54 v1 text-only; v2 adds image entries).
/// Immutable data record persisted as JSON by <see cref="ClipboardHistoryStore"/>.
/// The <see cref="Group"/> and <see cref="IsSensitive"/> fields are filled by
/// <see cref="ClipboardClassifier.Classify"/> at capture time; Pinning is a
/// user state toggled from the history window and persisted.
/// </summary>
/// <remarks>
/// <b>v2 image entries:</b> <see cref="Kind"/> == <see cref="ClipboardEntryKind.Image"/>
/// leaves <see cref="Text"/> empty and carries <see cref="ImageFileName"/> (a PNG
/// in <c>ClipboardImagesDirectory</c>). They are never sensitive and always
/// <see cref="ClipboardGroup.Text"/>. The classifier is skipped for images.
/// </remarks>
public sealed record ClipboardEntry
{
    /// <summary>Stable id (new <see cref="Guid"/> per capture). Used as the
    /// JSON/Pin key and for dedup-free LRU ordering (see CapturedAt).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>R54 v2: discriminates text vs image. Text entries (and all v1
    /// entries) carry <see cref="Text"/>; image entries carry
    /// <see cref="ImageFileName"/> instead. Defaults to <see cref="ClipboardEntryKind.Text"/>
    /// so legacy schema-v1 records decode correctly.</summary>
    public ClipboardEntryKind Kind { get; init; } = ClipboardEntryKind.Text;

    /// <summary>The captured text. Empty for image entries
    /// (<see cref="Kind"/> == <see cref="ClipboardEntryKind.Image"/>). In R54 v1
    /// this is always non-null (text-only capture).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>R54 v2: the PNG file name (no directory) for image entries, stored
    /// under <c>ClipboardImagesDirectory</c>. Null for text entries. Named by Guid
    /// to avoid collisions; content-hash-suffixed for dedup.</summary>
    public string? ImageFileName { get; init; }

    /// <summary>Source process name (e.g. <c>chrome</c>), or null when it could
    /// not be determined. Used only for display in the history row.</summary>
    public string? SourceProcessName { get; init; }

    /// <summary>Capture timestamp (UTC). Drives LRU ordering — oldest non-pinned
    /// entry is evicted when <c>MaxEntries</c> is exceeded.</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>User-pinned entries are never evicted by LRU and sort first.
    /// Toggled from the history window (Ctrl+P) and persisted to JSON.</summary>
    public bool IsPinned { get; init; }

    /// <summary>Auto-classified group (from <see cref="ClipboardClassifier"/> at
    /// capture time). Drives the badge when <see cref="GroupOverride"/> is null.
    /// Never mutated after capture — user corrections go into
    /// <see cref="GroupOverride"/>.</summary>
    public ClipboardGroup Group { get; init; } = ClipboardGroup.Text;

    /// <summary>R54 v2: user correction of the auto-classified group. When non-
    /// null, the built-in tabs (Links/Code/Commands/Sensitive/…) and the badge
    /// use this value instead of <see cref="Group"/>. This is how the user fixes
    /// a wrong auto-classification — either pulling a missed secret into
    /// Sensitive, or moving a false-positive out. <b>Sensitive is special:</b>
    /// overriding <em>to</em> Sensitive also sets <see cref="IsSensitive"/> (so
    /// the entry gets masked + DPAPI-encrypted at rest); overriding <em>out of</em>
    /// Sensitive clears it. null = revert to automatic classification
    /// (<see cref="Group"/>). Images cannot be overridden. New in schema v5;
    /// missing on load = null (backward compatible with v1–v4).</summary>
    public ClipboardGroup? GroupOverride { get; init; }

    /// <summary>True when <see cref="ClipboardClassifier"/> matched a sensitive
    /// pattern (api_key/secret/token/…) — or when the user manually overrode the
    /// group to Sensitive. The history window masks the preview with ●●●● until
    /// clicked, and the store DPAPI-encrypts the text at rest.</summary>
    public bool IsSensitive { get; init; }

    /// <summary>R54 v2: free-form per-entry annotation tags (e.g. "AWS",
    /// "Stripe"). Shown inline as badges on the row's meta line so a glance
    /// tells you which key/snippet this is. <b>Independent of the custom-tag
    /// tab system</b> — these never become nav tabs, never appear in the
    /// "Move to…" submenu, and are added via a separate "Add tag…" right-click
    /// entry. Modified via <c>with</c> expression (same pattern as
    /// <see cref="IsPinned"/>); persisted in clipboard-history.json (schema v4)
    /// as the "entryTags" array. Empty by default; missing field on load =
    /// empty (backward compatible with v1/v2/v3 files).</summary>
    public IReadOnlyList<string> EntryTags { get; init; } = [];
}
