using SelectionAssistant.Core.Clipboard;
using Xunit;

namespace SelectionAssistant.Core.Tests.Clipboard;

public sealed class ClipboardSearchMatcherTests
{
    private static readonly IReadOnlyList<string> Empty = Array.Empty<string>();

    // ── Empty / whitespace queries ──

    [Fact]
    public void EmptyQuery_MatchesAll()
    {
        Assert.True(ClipboardSearchMatcher.IsMatch("anything", Empty, Empty, "", ""));
    }

    [Fact]
    public void WhitespaceOnlyQuery_MatchesAll()
    {
        Assert.True(ClipboardSearchMatcher.IsMatch("anything", Empty, Empty, "", "   "));
    }

    // ── Single token, text field ──

    [Fact]
    public void SingleToken_TextSubstring_Matches()
    {
        Assert.True(ClipboardSearchMatcher.IsMatch("hello world", Empty, Empty, "", "hello"));
    }

    [Fact]
    public void SingleToken_CaseInsensitive_Matches()
    {
        Assert.True(ClipboardSearchMatcher.IsMatch("Hello World", Empty, Empty, "", "WORLD"));
    }

    [Fact]
    public void SingleToken_PinyinInitials_Matches()
    {
        // 微信 → pinyin initials "wx"
        Assert.True(ClipboardSearchMatcher.IsMatch("微信", Empty, Empty, "", "wx"));
    }

    [Fact]
    public void SingleToken_NotInText_NoMatch()
    {
        Assert.False(ClipboardSearchMatcher.IsMatch("hello world", Empty, Empty, "", "missing"));
    }

    // ── Single token, tag fields ──

    [Fact]
    public void SingleToken_HitsEntryTag_Matches()
    {
        Assert.True(ClipboardSearchMatcher.IsMatch("", new[] { "aws" }, Empty, "", "aws"));
    }

    [Fact]
    public void SingleToken_HitsCustomTag_Matches()
    {
        Assert.True(ClipboardSearchMatcher.IsMatch("", Empty, new[] { "工作" }, "", "工作"));
    }

    [Fact]
    public void SingleToken_HitsSource_Matches()
    {
        Assert.True(ClipboardSearchMatcher.IsMatch("", Empty, Empty, "chrome", "chrome"));
    }

    [Fact]
    public void SingleToken_PinyinTag_Matches()
    {
        // 工作 → pinyin initials "gz"
        Assert.True(ClipboardSearchMatcher.IsMatch("", Empty, new[] { "工作" }, "", "gz"));
    }

    // ── Multi-token AND ──

    [Fact]
    public void MultiToken_BothInText_AND_Matches()
    {
        Assert.True(ClipboardSearchMatcher.IsMatch("hello world", Empty, Empty, "", "hello world"));
    }

    [Fact]
    public void MultiToken_OneInTextOneInTag_AND_Matches()
    {
        // text contains "key", entryTags contains "aws"
        Assert.True(ClipboardSearchMatcher.IsMatch("key-2024", new[] { "aws" }, Empty, "", "aws key"));
    }

    [Fact]
    public void MultiToken_OneInTextOneInSource_AND_Matches()
    {
        Assert.True(ClipboardSearchMatcher.IsMatch("api docs", Empty, Empty, "chrome", "api chrome"));
    }

    [Fact]
    public void MultiToken_MissingOneToken_NoMatch()
    {
        // has aws tag but no stripe anywhere
        Assert.False(ClipboardSearchMatcher.IsMatch("", new[] { "aws" }, Empty, "", "aws stripe"));
    }

    [Fact]
    public void MultiToken_BothMissing_NoMatch()
    {
        Assert.False(ClipboardSearchMatcher.IsMatch("hello", Empty, Empty, "", "aws stripe"));
    }

    [Fact]
    public void MultiToken_TokenOrderIrrelevant_AND_Matches()
    {
        // "key aws" should match same as "aws key"
        Assert.True(ClipboardSearchMatcher.IsMatch("key-2024", new[] { "aws" }, Empty, "", "key aws"));
    }

    [Fact]
    public void MultiToken_ExtraWhitespace_Normalized()
    {
        // Collapsed to "aws key"
        Assert.True(ClipboardSearchMatcher.IsMatch("key-2024", new[] { "aws" }, Empty, "", "  aws   key  "));
    }

    [Fact]
    public void MultiToken_BothInTextDifferentOrder_AND_Matches()
    {
        // text "the quick brown fox", query "fox quick" — both present
        Assert.True(ClipboardSearchMatcher.IsMatch("the quick brown fox", Empty, Empty, "", "fox quick"));
    }

    // ── Edge cases ──

    [Fact]
    public void EmptyText_WithTag_StillMatches()
    {
        Assert.True(ClipboardSearchMatcher.IsMatch("", new[] { "aws" }, Empty, "", "aws"));
    }

    [Fact]
    public void EmptyTags_NoFalsePositive()
    {
        // entryTags contains an empty string — should not match arbitrary query
        Assert.False(ClipboardSearchMatcher.IsMatch("hello", new[] { "" }, Empty, "", "missing"));
    }
}
