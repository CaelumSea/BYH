using System.Diagnostics;
using SelectionAssistant.Core.Clipboard;
using Xunit;
using Xunit.Abstractions;

namespace SelectionAssistant.Core.Tests.Clipboard;

public sealed class ClipboardSearchIndexTests(ITestOutputHelper output)
{
    public static TheoryData<string, string[], string[], string, string> ParityCases => new()
    {
        { "anything", [], [], "", "" },
        { "anything", [], [], "", "   " },
        { "Hello World", [], [], "", "WORLD" },
        { "helloWorld", [], [], "", "hw" },
        { "微信公众平台", [], [], "", "wx" },
        { "key-2024", ["aws"], [], "", "aws key" },
        { "api docs", [], [], "chrome", "api chrome" },
        { "", [], ["工作"], "", "gz" },
        { "missing", ["AWS"], ["项目"], "warp", "aws xm" },
        { "missing", ["AWS"], ["项目"], "warp", "aws stripe" },
        { "hello", [""], [], "", "missing" },
        { "alpha beta", [], [], "", "beta alpha" },
        { "alpha beta", [], [], "", "  alpha   beta  " },
    };

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void IndexedMatcher_PreservesLegacySemantics(
        string text,
        string[] entryTags,
        string[] customTags,
        string source,
        string query)
    {
        bool expected = ClipboardSearchMatcher.IsMatch(
            text,
            entryTags,
            customTags,
            source,
            query);
        var index = new ClipboardSearchIndex(text, entryTags, customTags, source);

        Assert.Equal(expected, index.IsMatch(ClipboardSearchQuery.Parse(query)));
    }

    [Fact]
    public void Query_Parse_CollapsesWhitespaceForIncrementalComparison()
    {
        ClipboardSearchQuery query = ClipboardSearchQuery.Parse("  aws\t key  ");

        Assert.Equal(new[] { "aws", "key" }, query.Tokens);
        Assert.Equal("aws key", query.NormalizedText);
        Assert.False(query.IsEmpty);
    }

    [Fact]
    public void SegmentIndex_PreservesLegacyGreedyScanInputs()
    {
        const string candidate = "alphaBeta_Charlie.中文delta";
        string extracted = PinyinSearchHelper.ExtractSegmentInitials(candidate);

        // Exact segment-start characters used by the legacy scanner. Keeping
        // separators here is intentional: this test protects behavior parity.
        Assert.Equal("aB_.d", extracted);
    }

    [Fact]
    public void LongEntry_QueriesReuseIndexWithoutBodySizedAllocations()
    {
        string longText = string.Concat(Enumerable.Repeat(
            "warp terminal output alpha beta 中文内容 0123456789\n",
            6_500)) + " needle-at-tail";
        Assert.True(longText.Length >= 270_000);

        var buildTimer = Stopwatch.StartNew();
        var index = new ClipboardSearchIndex(longText, ["terminal"], [], "warp");
        buildTimer.Stop();

        ClipboardSearchQuery hit = ClipboardSearchQuery.Parse("warp needle-at-tail");
        ClipboardSearchQuery miss = ClipboardSearchQuery.Parse("definitely-not-present");
        Assert.True(index.IsMatch(hit));
        Assert.False(index.IsMatch(miss));

        // Warm JIT/static paths before measuring steady-state query allocations.
        _ = index.IsMatch(miss);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var queryTimer = Stopwatch.StartNew();
        for (int iteration = 0; iteration < 50; iteration++)
        {
            Assert.False(index.IsMatch(miss));
        }
        queryTimer.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        output.WriteLine(
            "Long entry length={0:N0}; index build={1:F1}ms; 50 warm misses={2:F1}ms; allocated={3:N0} bytes",
            longText.Length,
            buildTimer.Elapsed.TotalMilliseconds,
            queryTimer.Elapsed.TotalMilliseconds,
            allocated);

        // The old path allocated a body-sized pinyin buffer per field/query.
        // A generous 256 KiB ceiling allows assertion/test-runner overhead but
        // prevents regression to allocations proportional to the 270k+ body.
        Assert.True(allocated < 256 * 1024, $"Steady-state search allocated {allocated:N0} bytes.");
        Assert.True(queryTimer.Elapsed < TimeSpan.FromSeconds(2),
            $"50 indexed queries took {queryTimer.Elapsed.TotalMilliseconds:F1}ms.");
    }
}
