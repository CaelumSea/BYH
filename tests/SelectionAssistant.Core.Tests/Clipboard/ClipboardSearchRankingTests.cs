using SelectionAssistant.Core.Clipboard;
using Xunit;

namespace SelectionAssistant.Core.Tests.Clipboard;

/// <summary>
/// Tests for <see cref="ClipboardMatchRanker.OrderByRank{T}"/>: the stable
/// three-key sort (tag-hit desc, archived asc, original index asc) that the
/// clipboard-history window uses to surface tag-hitting matches ahead of
/// text-only matches.
/// </summary>
public sealed class ClipboardSearchRankingTests
{
    private static (string Item, ClipboardMatchScore Score, bool IsArchived, int Index) Entry(
        string item, bool tagHit, bool archived, int index) =>
        (item, new ClipboardMatchScore { IsMatch = true, TagHit = tagHit }, archived, index);

    [Fact]
    public void TagHits_SortBefore_TextOnlyMatches()
    {
        // Input order deliberately interleaves tag-hit and text-only entries;
        // after ranking, both tag-hit entries come first (in their input order),
        // then both text-only entries (in their input order).
        var scored = new List<(string, ClipboardMatchScore, bool, int)>
        {
            Entry("text-a", tagHit: false, archived: false, index: 0),
            Entry("tag-a", tagHit: true, archived: false, index: 1),
            Entry("text-b", tagHit: false, archived: false, index: 2),
            Entry("tag-b", tagHit: true, archived: false, index: 3),
        };

        List<string> result = ClipboardMatchRanker.OrderByRank(scored);

        Assert.Equal(new[] { "tag-a", "tag-b", "text-a", "text-b" }, result);
    }

    [Fact]
    public void TagHitGroup_KeepsOriginalOrder()
    {
        // Within the tag-hit group, original index (time-desc) is preserved.
        var scored = new List<(string, ClipboardMatchScore, bool, int)>
        {
            Entry("newer", tagHit: true, archived: false, index: 0),
            Entry("older", tagHit: true, archived: false, index: 1),
        };

        List<string> result = ClipboardMatchRanker.OrderByRank(scored);

        Assert.Equal(new[] { "newer", "older" }, result);
    }

    [Fact]
    public void TextOnlyGroup_KeepsOriginalOrder()
    {
        var scored = new List<(string, ClipboardMatchScore, bool, int)>
        {
            Entry("first", tagHit: false, archived: false, index: 5),
            Entry("second", tagHit: false, archived: false, index: 6),
        };

        List<string> result = ClipboardMatchRanker.OrderByRank(scored);

        Assert.Equal(new[] { "first", "second" }, result);
    }

    [Fact]
    public void Live_SortsBefore_Archived_WithinTagHitGroup()
    {
        // Both tag-hit, but one is archived: live first, then archived.
        var scored = new List<(string, ClipboardMatchScore, bool, int)>
        {
            Entry("archived-tag", tagHit: true, archived: true, index: 0),
            Entry("live-tag", tagHit: true, archived: false, index: 1),
        };

        List<string> result = ClipboardMatchRanker.OrderByRank(scored);

        Assert.Equal(new[] { "live-tag", "archived-tag" }, result);
    }

    [Fact]
    public void Live_SortsBefore_Archived_WithinTextOnlyGroup()
    {
        var scored = new List<(string, ClipboardMatchScore, bool, int)>
        {
            Entry("archived-text", tagHit: false, archived: true, index: 0),
            Entry("live-text", tagHit: false, archived: false, index: 1),
        };

        List<string> result = ClipboardMatchRanker.OrderByRank(scored);

        Assert.Equal(new[] { "live-text", "archived-text" }, result);
    }

    [Fact]
    public void TagHitArchived_SortsBefore_TextOnlyLive()
    {
        // Tag-hit outranks text-only even when the tag-hit entry is archived and
        // the text-only entry is live. Tag-hit is the primary key.
        var scored = new List<(string, ClipboardMatchScore, bool, int)>
        {
            Entry("live-text", tagHit: false, archived: false, index: 0),
            Entry("archived-tag", tagHit: true, archived: true, index: 1),
        };

        List<string> result = ClipboardMatchRanker.OrderByRank(scored);

        Assert.Equal(new[] { "archived-tag", "live-text" }, result);
    }

    [Fact]
    public void FullRank_TagHitLive_TagHitArchived_TextLive_TextArchived()
    {
        // The complete ordering: all tag-hit (live then archived), then all
        // text-only (live then archived). Within each subgroup, input order.
        var scored = new List<(string, ClipboardMatchScore, bool, int)>
        {
            Entry("text-archived", tagHit: false, archived: true, index: 0),
            Entry("text-live", tagHit: false, archived: false, index: 1),
            Entry("tag-archived", tagHit: true, archived: true, index: 2),
            Entry("tag-live", tagHit: true, archived: false, index: 3),
        };

        List<string> result = ClipboardMatchRanker.OrderByRank(scored);

        Assert.Equal(
            new[] { "tag-live", "tag-archived", "text-live", "text-archived" },
            result);
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyList()
    {
        var scored = new List<(string, ClipboardMatchScore, bool, int)>();

        List<string> result = ClipboardMatchRanker.OrderByRank(scored);

        Assert.Empty(result);
    }

    [Fact]
    public void SingleEntry_ReturnedUnchanged()
    {
        var scored = new List<(string, ClipboardMatchScore, bool, int)>
        {
            Entry("only", tagHit: false, archived: false, index: 0),
        };

        List<string> result = ClipboardMatchRanker.OrderByRank(scored);

        Assert.Equal(new[] { "only" }, result);
    }

    [Fact]
    public void InputList_NotMutated()
    {
        var scored = new List<(string, ClipboardMatchScore, bool, int)>
        {
            Entry("a", tagHit: false, archived: false, index: 0),
            Entry("b", tagHit: true, archived: false, index: 1),
        };
        var snapshot = scored.ToList();

        _ = ClipboardMatchRanker.OrderByRank(scored);

        // The input list order must be unchanged (OrderByRank returns a new list).
        Assert.Equal(snapshot, scored);
    }

    [Fact]
    public void EndToEnd_TagQuery_RanksTaggedEntryFirst()
    {
        // "aws" matches only the tag (not the body of either entry), so the
        // AWS-tagged entry has TagHit=true and the other has no match.
        var tagged = new ClipboardSearchIndex("api key value", ["AWS"], [], "chrome");
        var untagged = new ClipboardSearchIndex("api key value", [], [], "warp");
        ClipboardSearchQuery query = ClipboardSearchQuery.Parse("aws");

        ClipboardMatchScore taggedScore = tagged.ScoreMatch(query);
        ClipboardMatchScore untaggedScore = untagged.ScoreMatch(query);

        Assert.True(taggedScore.IsMatch);
        Assert.True(taggedScore.TagHit);
        Assert.False(untaggedScore.IsMatch);  // "aws" not in body/source → no match
    }
}
