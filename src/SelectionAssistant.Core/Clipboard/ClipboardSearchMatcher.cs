namespace SelectionAssistant.Core.Clipboard;

/// <summary>
/// Multi-field, multi-token search matcher for clipboard history entries.
/// R101 (full-text search enhancement): composes <see cref="PinyinSearchHelper"/>
/// across a row's searchable fields and supports space-separated multi-token
/// AND queries.
/// </summary>
/// <remarks>
/// <para><b>Field composition (OR within a token)</b>: a single token matches
/// if it hits ANY of <paramref name="text"/>, <paramref name="entryTags"/>,
/// <paramref name="customTags"/>, or <paramref name="source"/>. So searching
/// "aws" finds an entry whose body has "aws" OR whose EntryTags contains "aws"
/// OR whose CustomTags contains "#aws". Mirrors how users mentally model search:
/// "I tagged this aws, so searching aws should find it."</para>
/// <para><b>Token composition (AND across tokens)</b>: a multi-token query
/// (e.g. "aws key") matches only if EVERY token hits some field. This is the
/// search-box industry standard (Google/GitHub/VSCode) — adding words narrows
/// results. Single-token behavior is unchanged from legacy
/// <see cref="PinyinSearchHelper.MatchesQuery"/>.</para>
/// <para><b>Why no GroupLabel</b>: Group is auto-classified (Link/Code/Sensitive…).
/// Searching "link" should find text containing "link", not "every entry the
/// classifier put in the Link group" (that's what the Link tab is for). Adding
/// GroupLabel would cause false positives (searching "code" matches every Code
/// entry even if its body has no "code").</para>
/// </remarks>
public static class ClipboardSearchMatcher
{
    /// <summary>
    /// Returns true if the given fields match <paramref name="query"/>.
    /// Empty/whitespace query returns true (defensive — caller usually guards
    /// this, but the matcher stays correct if called directly).
    /// </summary>
    /// <param name="text">Full entry body (primary field — most queries land here).</param>
    /// <param name="entryTags">Per-entry annotation tags (e.g. AWS, Stripe).</param>
    /// <param name="customTags">Custom-tab assignments (e.g. 工作, 项目).</param>
    /// <param name="source">Source process name (e.g. chrome, vscode).</param>
    /// <param name="query">User-typed search query. Whitespace-split into tokens;
    /// each token must match some field (AND across tokens).</param>
    public static bool IsMatch(
        string text,
        IReadOnlyList<string> entryTags,
        IReadOnlyList<string> customTags,
        string source,
        string query)
    {
        // Trim once. Empty query = match all.
        string trimmed = query.AsSpan().Trim().ToString();
        if (trimmed.Length == 0) return true;

        // Whitespace split that also collapses consecutive separators.
        // Options.RemoveEmptyEntries means "  aws   key  " → ["aws", "key"].
        string[] tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return true;

        // Single-token fast path — identical to legacy MatchesQuery(Text) +
        // extended fields, no multi-token overhead.
        if (tokens.Length == 1)
        {
            return MatchesSingleToken(text, entryTags, customTags, source, tokens[0]);
        }

        // Multi-token AND: every token must hit some field.
        foreach (string token in tokens)
        {
            if (!MatchesSingleToken(text, entryTags, customTags, source, token))
            {
                return false;
            }
        }
        return true;
    }

    private static bool MatchesSingleToken(
        string text,
        IReadOnlyList<string> entryTags,
        IReadOnlyList<string> customTags,
        string source,
        string token)
    {
        // Primary field — full body.
        if (text.Length > 0 && PinyinSearchHelper.MatchesQuery(text, token))
        {
            return true;
        }

        // Per-entry annotation tags.
        foreach (string tag in entryTags)
        {
            if (tag.Length > 0 && PinyinSearchHelper.MatchesQuery(tag, token))
            {
                return true;
            }
        }

        // Custom-tab assignments.
        foreach (string tag in customTags)
        {
            if (tag.Length > 0 && PinyinSearchHelper.MatchesQuery(tag, token))
            {
                return true;
            }
        }

        // Source process — secondary field.
        if (source.Length > 0 && PinyinSearchHelper.MatchesQuery(source, token))
        {
            return true;
        }

        return false;
    }
}
