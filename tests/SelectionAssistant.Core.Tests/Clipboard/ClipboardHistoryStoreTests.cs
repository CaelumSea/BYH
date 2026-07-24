using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Clipboard;

public sealed class ClipboardHistoryStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-clip-{Guid.NewGuid():N}.json");

    private static ClipboardEntry Entry(string text, int ageSeconds = 0, bool pinned = false) =>
        new()
        {
            Text = text,
            CapturedAt = DateTimeOffset.UtcNow.AddSeconds(-ageSeconds),
            IsPinned = pinned,
            Group = ClipboardClassifier.Classify(text),
            IsSensitive = ClipboardClassifier.IsSensitive(text),
        };

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var entries = ClipboardHistoryStore.Load(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));
        Assert.Empty(entries);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        string path = TempPath();
        try
        {
            var original = new List<ClipboardEntry>
            {
                Entry("hello"),
                Entry("https://x.com"),
                Entry("password=secret", pinned: true),
            };

            Assert.True(ClipboardHistoryStore.Save(original, path));
            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path);

            // Load re-orders via OrderForDisplay: pinned first, then newest.
            Assert.Equal(3, loaded.Count);
            Assert.True(loaded[0].IsPinned); // password=secret pinned → first
            Assert.Equal("password=secret", loaded[0].Text);
            Assert.True(loaded[0].IsSensitive);
            // Remaining two are non-pinned, newest first (all same instant, so
            // order between hello/x.com is input-stable).
            Assert.Contains(loaded, e => e.Text == "hello");
            Assert.Contains(loaded, e => e.Text == "https://x.com");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AddAndEvict_NewEntry_GoesToFront()
    {
        var existing = new List<ClipboardEntry> { Entry("old", ageSeconds: 60) };
        ClipboardEntry newEntry = Entry("new", ageSeconds: 0);

        List<ClipboardEntry> result = ClipboardHistoryStore.AddAndEvict(existing, newEntry, 100);

        Assert.Equal("new", result[0].Text);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void AddAndEvict_DuplicateText_MovesToFront_NoDupe()
    {
        var existing = new List<ClipboardEntry>
        {
            Entry("dupe", ageSeconds: 60),
            Entry("other", ageSeconds: 30),
        };
        ClipboardEntry newEntry = Entry("dupe", ageSeconds: 0);

        List<ClipboardEntry> result = ClipboardHistoryStore.AddAndEvict(existing, newEntry, 100);

        Assert.Equal("dupe", result[0].Text);
        Assert.Single(result, e => e.Text == "dupe");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void AddAndEvict_OverMax_EvictsOldestNonPinned()
    {
        // 3 existing non-pinned; max=2 → after adding one more we have 4, evict
        // down to 2 non-pinned. Oldest (age 100) should be dropped.
        var existing = new List<ClipboardEntry>
        {
            Entry("a", ageSeconds: 100),
            Entry("b", ageSeconds: 50),
            Entry("c", ageSeconds: 10),
        };
        ClipboardEntry newEntry = Entry("new", ageSeconds: 0);

        List<ClipboardEntry> result = ClipboardHistoryStore.AddAndEvict(existing, newEntry, maxEntries: 2);

        Assert.DoesNotContain(result, e => e.Text == "a"); // oldest evicted
        Assert.Equal("new", result[0].Text);
        int nonPinned = result.Count(e => !e.IsPinned);
        Assert.Equal(2, nonPinned);
    }

    [Fact]
    public void AddAndEvict_PinnedNeverEvicted()
    {
        var existing = new List<ClipboardEntry>
        {
            Entry("pinned", ageSeconds: 999, pinned: true),
            Entry("old", ageSeconds: 100),
            Entry("newer", ageSeconds: 10),
        };
        ClipboardEntry newEntry = Entry("fresh", ageSeconds: 0);

        // maxEntries=1 non-pinned: "old" and "newer" are non-pinned candidates;
        // the oldest non-pinned ("old") is evicted, "newer" stays, "pinned" stays.
        List<ClipboardEntry> result = ClipboardHistoryStore.AddAndEvict(existing, newEntry, maxEntries: 1);

        Assert.Contains(result, e => e.Text == "pinned");
        Assert.DoesNotContain(result, e => e.Text == "old");
    }

    [Fact]
    public void OrderForDisplay_PinnedFirst_ThenNewest()
    {
        var entries = new List<ClipboardEntry>
        {
            Entry("a", ageSeconds: 10),
            Entry("p1", ageSeconds: 999, pinned: true),
            Entry("b", ageSeconds: 5),
            Entry("p2", ageSeconds: 1000, pinned: true),
        };

        List<ClipboardEntry> ordered = ClipboardHistoryStore.OrderForDisplay(entries);

        // Pinned first (by capturedAt desc), then non-pinned by capturedAt desc.
        Assert.True(ordered[0].IsPinned);
        Assert.True(ordered[1].IsPinned);
        Assert.False(ordered[2].IsPinned);
        Assert.False(ordered[3].IsPinned);
        Assert.Equal("p1", ordered[0].Text); // p1 newer than p2
        Assert.Equal("b", ordered[2].Text);  // b newer than a
    }

    [Theory]
    [InlineData("short text", false, "short text")]
    [InlineData("password=secret", true, "●●●●●●●●●●●●●●●●")]
    [InlineData("hello", true, "hello")] // mask off
    public void BuildPreview_MasksSensitive(string text, bool sensitive, string expected)
    {
        // MaskSensitiveEnabled only when sensitive=true.
        string preview = ClipboardHistoryStore.BuildPreview(text, sensitive, maskSensitive: true);
        if (sensitive)
        {
            Assert.Equal(new string('●', Math.Min(text.Length, 16)), preview);
        }
        else
        {
            Assert.Equal(expected, preview);
        }
    }

    [Fact]
    public void BuildPreview_TruncatesLongText()
    {
        string longText = new string('x', 100);
        string preview = ClipboardHistoryStore.BuildPreview(longText, false, true);
        Assert.True(preview.Length <= 80);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not valid json }}}");
            List<ClipboardEntry> entries = ClipboardHistoryStore.Load(path);
            Assert.Empty(entries);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
