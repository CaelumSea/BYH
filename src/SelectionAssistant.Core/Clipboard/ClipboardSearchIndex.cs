namespace SelectionAssistant.Core.Clipboard;

/// <summary>
/// Immutable, precomputed search representation for one clipboard entry.
/// The full text is retained by reference (never truncated or copied), while
/// segment and pinyin initials are derived once instead of on every keystroke.
/// </summary>
public sealed class ClipboardSearchIndex
{
    private readonly SearchableField[] _fields;

    public ClipboardSearchIndex(
        string text,
        IReadOnlyList<string> entryTags,
        IReadOnlyList<string> customTags,
        string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(entryTags);
        ArgumentNullException.ThrowIfNull(customTags);
        ArgumentNullException.ThrowIfNull(source);

        var fields = new List<SearchableField>(2 + entryTags.Count + customTags.Count);
        AddIfPresent(fields, text, FieldKind.Text);
        foreach (string tag in entryTags)
        {
            AddIfPresent(fields, tag, FieldKind.EntryTag);
        }
        foreach (string tag in customTags)
        {
            AddIfPresent(fields, tag, FieldKind.CustomTag);
        }
        AddIfPresent(fields, source, FieldKind.Source);
        _fields = fields.ToArray();
    }

    /// <summary>
    /// Applies the same OR-across-fields / AND-across-tokens semantics as
    /// <see cref="ClipboardSearchMatcher.IsMatch"/> against cached fields.
    /// Delegates to <see cref="ScoreMatch"/> so the boolean path and the ranked
    /// path share one implementation — semantics stay identical for callers
    /// that only need the boolean result.
    /// </summary>
    public bool IsMatch(ClipboardSearchQuery query) => ScoreMatch(query).IsMatch;

    /// <summary>
    /// Evaluates <paramref name="query"/> against the cached fields and returns
    /// a <see cref="ClipboardMatchScore"/> carrying both the boolean match
    /// result and whether any query token hit a tag field (EntryTag or
    /// CustomTag). The UI uses <see cref="ClipboardMatchScore.TagHit"/> to sort
    /// tag-hitting matches ahead of text-only matches.
    /// </summary>
    /// <remarks>
    /// <b>AND across tokens, OR within a token</b> — same composition as the
    /// legacy boolean matcher. <see cref="ClipboardMatchScore.TagHit"/> is true
    /// when at least one of the matched tokens landed on a tag field, regardless
    /// of whether other tokens matched only text/source.
    /// </remarks>
    public ClipboardMatchScore ScoreMatch(ClipboardSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.IsEmpty)
        {
            return ClipboardMatchScore.MatchAll;
        }

        bool allTokensMatched = true;
        bool anyTokenHitTag = false;
        foreach (string token in query.Tokens)
        {
            bool tokenMatched = false;
            bool tokenHitTag = false;
            foreach (SearchableField field in _fields)
            {
                if (!field.Matches(token))
                {
                    continue;
                }
                tokenMatched = true;
                if (field.Kind is FieldKind.EntryTag or FieldKind.CustomTag)
                {
                    tokenHitTag = true;
                }
                // Keep scanning: a field of a different kind might also match,
                // but we only need one tag hit to set tokenHitTag, and matching
                // is cheap. Breaking early would skip setting tokenHitTag if a
                // tag field comes after the text field in _fields order.
            }

            if (!tokenMatched)
            {
                allTokensMatched = false;
                break;
            }
            if (tokenHitTag)
            {
                anyTokenHitTag = true;
            }
        }

        return allTokensMatched
            ? new ClipboardMatchScore { IsMatch = true, TagHit = anyTokenHitTag }
            : ClipboardMatchScore.NoMatch;
    }

    private static void AddIfPresent(List<SearchableField> fields, string value, FieldKind kind)
    {
        if (!string.IsNullOrEmpty(value))
        {
            fields.Add(new SearchableField(value, kind));
        }
    }

    private sealed class SearchableField
    {
        private readonly string _value;
        private readonly string _segmentInitials;
        private readonly string _pinyinInitials;

        public SearchableField(string value, FieldKind kind)
        {
            _value = value;
            Kind = kind;
            _segmentInitials = PinyinSearchHelper.ExtractSegmentInitials(value);
            _pinyinInitials = PinyinSearchHelper.ExtractPinyinInitials(value);
        }

        public FieldKind Kind { get; }

        public bool Matches(string token)
        {
            if (_value.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (MatchesSegmentInitials(_segmentInitials, token))
            {
                return true;
            }

            return _pinyinInitials.Length > 0 &&
                   _pinyinInitials.Contains(token, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesSegmentInitials(string initials, string token)
        {
            if (initials.Length == 0 || token.Length == 0)
            {
                return false;
            }

            int tokenIndex = 0;
            foreach (char c in initials)
            {
                if (char.ToLowerInvariant(c) == char.ToLowerInvariant(token[tokenIndex]))
                {
                    tokenIndex++;
                    if (tokenIndex == token.Length)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}

/// <summary>
/// A query parsed once per text change. Whitespace is collapsed into tokens;
/// tokens retain the legacy case-insensitive matching behavior.
/// </summary>
public sealed class ClipboardSearchQuery
{
    private ClipboardSearchQuery(string[] tokens)
    {
        Tokens = tokens;
        NormalizedText = string.Join(' ', tokens);
    }

    public IReadOnlyList<string> Tokens { get; }

    public string NormalizedText { get; }

    public bool IsEmpty => Tokens.Count == 0;

    public static ClipboardSearchQuery Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ClipboardSearchQuery([]);
        }

        string[] tokens = query.AsSpan().Trim().ToString()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return new ClipboardSearchQuery(tokens);
    }
}
