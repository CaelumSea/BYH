using SelectionAssistant.Core.Clipboard;
using Xunit;

namespace SelectionAssistant.Core.Tests.Clipboard;

public sealed class PinyinSearchHelperTests
{
    // ── ExtractPinyinInitials: coverage of previously-missing characters ──
    // These chars were NOT in the old ~600-entry table and silently dropped,
    // so searches like "jlp" (纪录片) used to miss. The 7762-char Unihan
    // kMandarin table covers them.

    [Theory]
    [InlineData("纪录片", "jlp")]      // 纪j 录l 片p — all three were missing before
    [InlineData("微信", "wx")]         // baseline still works
    [InlineData("工作", "gz")]         // baseline still works
    [InlineData("小旺AI截图", "xwjt")]  // mixed CJK + ASCII: A/I skipped, 小x 旺w 截j 图t
    [InlineData("哲学", "zx")]         // 哲 was missing
    [InlineData("下载", "xz")]         // 载 was missing
    [InlineData("神态", "st")]         // 奕/态-class chars
    [InlineData("重复", "zf")]         // 多音字: 重→z (primary reading zhòng-weight)
    [InlineData("长度", "zd")]         // 多音字: 长→z (primary reading zhǎng-grow)
    public void ExtractPinyinInitials_CoversCommonChars(string input, string expected)
    {
        Assert.Equal(expected, PinyinSearchHelper.ExtractPinyinInitials(input));
    }

    [Fact]
    public void ExtractPinyinInitials_EmptyOrAscii_ReturnsEmptyOrSkips()
    {
        Assert.Equal(string.Empty, PinyinSearchHelper.ExtractPinyinInitials(""));
        Assert.Equal(string.Empty, PinyinSearchHelper.ExtractPinyinInitials("hello world"));
        // ASCII inside CJK text is skipped, CJK mapped.
        Assert.Equal("xw", PinyinSearchHelper.ExtractPinyinInitials("小旺AI"));
    }

    // ── MatchesQuery end-to-end via pinyin tier ──

    [Theory]
    [InlineData("纪录片完整版", "jlp")]   // query is substring of initials "jlpwb"
    [InlineData("纪录片完整版", "jl")]    // query is substring of initials
    [InlineData("微信公众号", "wx")]      // query is substring of initials "wxgzh"
    public void MatchesQuery_PinyinInitials_Substring_Hits(string candidate, string query)
    {
        Assert.True(PinyinSearchHelper.MatchesQuery(candidate, query));
    }

    [Fact]
    public void MatchesQuery_NonMatchingPinyin_DoesNotHitViaPinyin()
    {
        // "qq" is not a substring of any realistic pinyin-initial string here.
        Assert.False(PinyinSearchHelper.MatchesQuery("微信", "qq"));
    }
}
