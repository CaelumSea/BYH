namespace SelectionAssistant.Core.Clipboard;

/// <summary>
/// Result of evaluating a <see cref="ClipboardSearchQuery"/> against one
/// clipboard entry's searchable fields. Extends the legacy boolean match with
/// a <see cref="TagHit"/> signal so the UI can prioritize entries whose query
/// tokens hit a tag field (EntryTag or CustomTag) over entries that matched
/// only the body text. NativeAOT-safe: plain readonly record struct.
/// </summary>
/// <remarks>
/// <b>TagHit semantics</b>: true when ANY query token matched a tag field
/// (EntryTag or CustomTag). Both tag kinds are treated equally — the ranking
/// only distinguishes "matched a tag" from "matched text/source only", not
/// which tag kind. Multi-token AND is preserved: a row is a match only when
/// every token hit some field; TagHit is then true if any of those token
/// hits landed on a tag.
/// </remarks>
public readonly record struct ClipboardMatchScore
{
    /// <summary>True when the entry satisfies the query (every token matched
    /// some field, or the query is empty). Equivalent to legacy
    /// <see cref="ClipboardSearchIndex.IsMatch"/> semantics.</summary>
    public required bool IsMatch { get; init; }

    /// <summary>True when at least one query token matched a tag field
    /// (EntryTag or CustomTag). Only meaningful when <see cref="IsMatch"/> is
    /// true. Used by the UI to sort tag-hitting matches ahead of text-only
    /// matches. Always false when <see cref="IsMatch"/> is false.</summary>
    public required bool TagHit { get; init; }

    /// <summary>Score for an empty query (matches everything, no tag hit).
    /// Equivalent to the legacy empty-query path of <see cref="ClipboardSearchIndex.IsMatch"/>.</summary>
    public static readonly ClipboardMatchScore MatchAll = new() { IsMatch = true, TagHit = false };

    /// <summary>Score for a non-matching entry.</summary>
    public static readonly ClipboardMatchScore NoMatch = new() { IsMatch = false, TagHit = false };
}

/// <summary>
/// Categorizes a searchable field so <see cref="ClipboardSearchIndex.ScoreMatch"/>
/// can tell whether a token hit landed on a tag (ranked higher) versus the body
/// text or source. Pure metadata carried alongside each
/// <see cref="ClipboardSearchIndex"/>'s precomputed field.
/// </summary>
internal enum FieldKind
{
    /// <summary>The full entry body (primary field).</summary>
    Text,

    /// <summary>A free-form per-entry annotation tag (e.g. "AWS", "Stripe").</summary>
    EntryTag,

    /// <summary>A custom-tab assignment (e.g. "工作", "项目").</summary>
    CustomTag,

    /// <summary>The source process name (e.g. "chrome").</summary>
    Source,
}
