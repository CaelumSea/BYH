namespace SelectionAssistant.Core.Clipboard;

/// <summary>
/// Stable ranker for clipboard search matches. Sorts matches so that tag-hitting
/// entries (a query token matched an EntryTag or CustomTag field) precede
/// text-only matches, live entries precede archived ones within each group, and
/// the original snapshot order (= time-desc) is preserved as the final
/// tiebreaker. Pure function over a pre-collected list of scored matches — no
/// allocations beyond the output list. NativeAOT-safe.
/// </summary>
/// <remarks>
/// <b>Why a separate type</b>: the ranking comparator is a three-key stable
/// sort (tag-hit descending, archived ascending, index ascending) and is worth
/// unit-testing in isolation. The UI's <c>ApplyIndexedFilterAsync</c> collects
/// <see cref="ClipboardMatchScore"/> per row and delegates the ordering here so
/// the rule lives in one place and is covered by
/// <c>ClipboardSearchRankingTests</c>.
/// </remarks>
public static class ClipboardMatchRanker
{
    /// <summary>Orders the scored matches: tag-hit first (desc), then live
    /// before archived, then by the caller-supplied <paramref name="Index"/>
    /// (stable, ascending = preserves the input order). Returns a new list;
    /// the input is not mutated.</summary>
    /// <param name="scored">Matched entries with their score, archived flag, and
    /// their index in the pre-ranking candidate list (snapshot time-desc
    /// order). All entries MUST already satisfy
    /// <see cref="ClipboardMatchScore.IsMatch"/> == true — non-matches should
    /// be filtered out before calling this.</param>
    public static List<T> OrderByRank<T>(
        IReadOnlyList<(T Item, ClipboardMatchScore Score, bool IsArchived, int Index)> scored)
    {
        var sorted = new List<(T Item, ClipboardMatchScore Score, bool IsArchived, int Index)>(scored);
        sorted.Sort((a, b) =>
        {
            // 1. Tag-hit descending: true ranks before false. Users expect
            //    tagged items (the ones they deliberately labeled) to surface
            //    above plain-text matches when both match the query.
            if (a.Score.TagHit != b.Score.TagHit)
            {
                return b.Score.TagHit.CompareTo(a.Score.TagHit);
            }
            // 2. Archived flag ascending: live (false) before archived (true),
            //    within each tag-hit group. Live entries are more relevant.
            int aArch = a.IsArchived ? 1 : 0;
            int bArch = b.IsArchived ? 1 : 0;
            if (aArch != bArch)
            {
                return aArch.CompareTo(bArch);
            }
            // 3. Stable: original index ascending (= snapshot time-desc order).
            return a.Index.CompareTo(b.Index);
        });
        var result = new List<T>(sorted.Count);
        foreach (var entry in sorted)
        {
            result.Add(entry.Item);
        }
        return result;
    }
}
