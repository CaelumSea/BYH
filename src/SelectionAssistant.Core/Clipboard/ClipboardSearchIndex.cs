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
        AddIfPresent(fields, text);
        foreach (string tag in entryTags)
        {
            AddIfPresent(fields, tag);
        }
        foreach (string tag in customTags)
        {
            AddIfPresent(fields, tag);
        }
        AddIfPresent(fields, source);
        _fields = fields.ToArray();
    }

    /// <summary>
    /// Applies the same OR-across-fields / AND-across-tokens semantics as
    /// <see cref="ClipboardSearchMatcher.IsMatch"/> against cached fields.
    /// </summary>
    public bool IsMatch(ClipboardSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.IsEmpty)
        {
            return true;
        }

        foreach (string token in query.Tokens)
        {
            bool tokenMatched = false;
            foreach (SearchableField field in _fields)
            {
                if (field.Matches(token))
                {
                    tokenMatched = true;
                    break;
                }
            }

            if (!tokenMatched)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddIfPresent(List<SearchableField> fields, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            fields.Add(new SearchableField(value));
        }
    }

    private sealed class SearchableField
    {
        private readonly string _value;
        private readonly string _segmentInitials;
        private readonly string _pinyinInitials;

        public SearchableField(string value)
        {
            _value = value;
            _segmentInitials = PinyinSearchHelper.ExtractSegmentInitials(value);
            _pinyinInitials = PinyinSearchHelper.ExtractPinyinInitials(value);
        }

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
