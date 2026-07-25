namespace SelectionAssistant.Core.Clipboard;

/// <summary>
/// R54 v1.1: tag data for clipboard history. Separated from the main
/// <c>clipboard-history.json</c> (whose schema is frozen at v1) so that adding
/// tags/assignments never rewrites the (potentially large) history file and
/// stays a pure backward-compatible addition. Persisted as
/// <c>clipboard-history-tags.json</c> by <c>ClipboardTagStore</c>.
/// </summary>
/// <remarks>
/// <b>Two concepts:</b>
/// <list type="bullet">
///   <item><b>Built-in tabs</b> (全部/链接/代码/JSON/命令/数字/联系人/敏感/★置顶/❤收藏)
///   are <b>not</b> stored here — they're derived from
///   <see cref="ClipboardEntry.Group"/> and <see cref="ClipboardEntry.IsPinned"/>
///   at filter time. Only the special <c>❤收藏</c> and user tags need stored
///   assignments (收藏 is conventionally <see cref="FavoriteTagName"/>).</item>
///   <item><b>Custom tags</b> (<see cref="CustomTags"/>) are user-created names;
///   <see cref="Assignments"/> maps an entry id → set of tag names (any mix of
///   custom + the favorite name).</item>
/// </list>
/// </remarks>
public sealed record ClipboardTagData
{
    /// <summary>The well-known tag name used for the ❤收藏 built-in tab.
    /// Entries assigned this name appear under the 收藏 tab. Kept as a constant
    /// so the UI and store agree without magic strings.</summary>
    public const string FavoriteTagName = "❤收藏";

    /// <summary>User-created custom tag names (display order preserved). Built-in
    /// group/pin tabs are never listed here. List position <b>is</b> the display
    /// order — reordering a tag is just reordering this list.</summary>
    public IReadOnlyList<string> CustomTags { get; init; } = [];

    /// <summary>Entry id → set of tag names assigned to it. A tag name may be a
    /// custom tag or <see cref="FavoriteTagName"/>. Empty sets/missing keys mean
    /// no tags. Stored as <see cref="IReadOnlyDictionary"/> for immutability.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlySet<string>> Assignments { get; init; } =
        new Dictionary<Guid, IReadOnlySet<string>>();

    /// <summary>R54 v1.2: custom tag name → emoji icon (e.g. "工作" → "💼").
    /// Tags without an entry here show the default <c>#</c> prefix. Kept as a
    /// parallel map (rather than changing <see cref="CustomTags"/> to objects)
    /// so the existing string-based helpers stay untouched. Schema v2 only;
    /// v1 files load with an empty map (seamless upgrade).</summary>
    public IReadOnlyDictionary<string, string> TagIcons { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static ClipboardTagData Empty { get; } = new();

    /// <summary>True when <paramref name="entryId"/> is assigned the given tag
    /// (case-sensitive, ordinal). Convenience over
    /// <see cref="Assignments"/>.</summary>
    public bool HasTag(Guid entryId, string tagName) =>
        Assignments.TryGetValue(entryId, out IReadOnlySet<string>? tags) &&
        tags.Contains(tagName);

    /// <summary>R54 v1.2: the emoji icon for <paramref name="tagName"/>, or null
    /// when no icon is set. Convenience over <see cref="TagIcons"/>.</summary>
    public string? IconFor(string tagName) =>
        TagIcons.TryGetValue(tagName, out string? icon) && !string.IsNullOrEmpty(icon)
            ? icon
            : null;
}
