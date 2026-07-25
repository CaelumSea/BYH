using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Infrastructure.Configuration;
using SelectionAssistant.Platform.Abstractions.Secrets;
using Xunit;

namespace SelectionAssistant.Core.Tests.Clipboard;

public sealed class ClipboardArchiveStoreTests
{
    private static string TempArchiveDir() =>
        Path.Combine(Path.GetTempPath(), $"byh-archive-{Guid.NewGuid():N}");

    private static int Year = 2026;

    /// <summary>Builds a text entry with an explicit capture month so tests
    /// don't depend on the wall clock. Month is 1-12; day/second fixed.</summary>
    private static ClipboardEntry EntryAtMonth(string text, int month, bool sensitive = false) =>
        new()
        {
            Text = text,
            CapturedAt = new DateTimeOffset(Year, month, 15, 12, 0, 0, TimeSpan.Zero),
            Kind = ClipboardEntryKind.Text,
            IsSensitive = sensitive,
            Group = ClipboardClassifier.Classify(text),
        };

    private static ClipboardEntry ImageEntryAtMonth(int month) =>
        new()
        {
            Text = string.Empty,
            ImageFileName = $"clip-deadbeef.png",
            Kind = ClipboardEntryKind.Image,
            CapturedAt = new DateTimeOffset(Year, month, 15, 12, 0, 0, TimeSpan.Zero),
        };

    // ── FormatMonthKey ──

    [Fact]
    public void FormatMonthKey_Produces_YearMonth()
    {
        var dto = new DateTimeOffset(2026, 7, 31, 23, 59, 0, TimeSpan.Zero);
        Assert.Equal("2026-07", ClipboardArchiveStore.FormatMonthKey(dto));
    }

    [Fact]
    public void FormatMonthKey_RollsYearAtDecember()
    {
        var dto = new DateTimeOffset(2026, 12, 31, 23, 59, 0, TimeSpan.Zero);
        Assert.Equal("2026-12", ClipboardArchiveStore.FormatMonthKey(dto));
    }

    // ── AppendToArchive basic ──

    [Fact]
    public void AppendToArchive_CreatesMonthlyFile()
    {
        string dir = TempArchiveDir();
        try
        {
            int written = ClipboardArchiveStore.AppendToArchive(
                new[] { EntryAtMonth("hello", 7) }, dir);
            Assert.Equal(1, written);
            Assert.True(File.Exists(Path.Combine(dir, "2026-07.json")));
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void AppendToArchive_AppendsToExistingMonth()
    {
        string dir = TempArchiveDir();
        try
        {
            ClipboardArchiveStore.AppendToArchive(new[] { EntryAtMonth("first", 7) }, dir);
            ClipboardArchiveStore.AppendToArchive(new[] { EntryAtMonth("second", 7) }, dir);

            List<ClipboardEntry> loaded = ClipboardArchiveStore.LoadAll(dir);
            Assert.Equal(2, loaded.Count);
            // LoadAll returns them; texts should both be present regardless of order.
            Assert.Contains(loaded, e => e.Text == "first");
            Assert.Contains(loaded, e => e.Text == "second");
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void AppendToArchive_SplitsByMonth()
    {
        string dir = TempArchiveDir();
        try
        {
            // One entry from July, one from August, in the same call.
            ClipboardArchiveStore.AppendToArchive(
                new[] { EntryAtMonth("july", 7), EntryAtMonth("august", 8) }, dir);

            Assert.True(File.Exists(Path.Combine(dir, "2026-07.json")));
            Assert.True(File.Exists(Path.Combine(dir, "2026-08.json")));

            List<ClipboardEntry> loaded = ClipboardArchiveStore.LoadAll(dir);
            Assert.Equal(2, loaded.Count);
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void AppendToArchive_EmptyInput_WritesNothing()
    {
        string dir = TempArchiveDir();
        try
        {
            int written = ClipboardArchiveStore.AppendToArchive(
                Array.Empty<ClipboardEntry>(), dir);
            Assert.Equal(0, written);
            Assert.False(Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any());
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void AppendToArchive_SkipsImageEntries()
    {
        string dir = TempArchiveDir();
        try
        {
            // Defensive: even if a caller passes an image entry, the archive
            // silently drops it (image files are deleted elsewhere; archiving
            // metadata alone would leave dangling references).
            int written = ClipboardArchiveStore.AppendToArchive(
                new[] { ImageEntryAtMonth(7), EntryAtMonth("text-only", 7) }, dir);

            Assert.Equal(1, written); // only the text entry
            List<ClipboardEntry> loaded = ClipboardArchiveStore.LoadAll(dir);
            ClipboardEntry single = Assert.Single(loaded);
            Assert.Equal("text-only", single.Text);
        }
        finally { TryCleanup(dir); }
    }

    // ── Encryption ──

    [Fact]
    public void AppendToArchive_WithCipher_RoundTripsEncrypted()
    {
        string dir = TempArchiveDir();
        try
        {
            var cipher = new FakeCipher();
            var entry = EntryAtMonth("api_key=AKIAIOSFODNN7EXAMPLE", 7, sensitive: true);

            ClipboardArchiveStore.AppendToArchive(new[] { entry }, dir, cipher);

            // Re-load with the same cipher — text should decrypt back.
            List<ClipboardEntry> loaded = ClipboardArchiveStore.LoadAll(dir, cipher);
            ClipboardEntry single = Assert.Single(loaded);
            Assert.Equal("api_key=AKIAIOSFODNN7EXAMPLE", single.Text);
            Assert.True(single.IsSensitive);

            // The raw file on disk must NOT contain the plaintext.
            string raw = File.ReadAllText(Path.Combine(dir, "2026-07.json"));
            Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", raw);
            Assert.Contains("ENC:", raw); // FakeCipher prefix
        }
        finally { TryCleanup(dir); }
    }

    // ── LoadAll ──

    [Fact]
    public void LoadAll_MissingDirectory_ReturnsEmpty()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"byh-nonexistent-{Guid.NewGuid():N}");
        List<ClipboardEntry> loaded = ClipboardArchiveStore.LoadAll(dir);
        Assert.Empty(loaded);
    }

    [Fact]
    public void LoadAll_AggregatesAcrossMonths()
    {
        string dir = TempArchiveDir();
        try
        {
            ClipboardArchiveStore.AppendToArchive(new[] { EntryAtMonth("m1", 1) }, dir);
            ClipboardArchiveStore.AppendToArchive(new[] { EntryAtMonth("m2", 6) }, dir);
            ClipboardArchiveStore.AppendToArchive(new[] { EntryAtMonth("m3", 12) }, dir);

            List<ClipboardEntry> loaded = ClipboardArchiveStore.LoadAll(dir);
            Assert.Equal(3, loaded.Count);
        }
        finally { TryCleanup(dir); }
    }

    // ── Corrupt file resilience ──

    [Fact]
    public void LoadAll_SkipsCorruptFile()
    {
        string dir = TempArchiveDir();
        try
        {
            ClipboardArchiveStore.AppendToArchive(new[] { EntryAtMonth("good", 7) }, dir);
            // Write garbage into a different month file.
            File.WriteAllText(Path.Combine(dir, "2026-08.json"), "{ this is not valid json");

            List<ClipboardEntry> loaded = ClipboardArchiveStore.LoadAll(dir);
            // The good file's entry survives; the corrupt file is skipped.
            ClipboardEntry single = Assert.Single(loaded);
            Assert.Equal("good", single.Text);
        }
        finally { TryCleanup(dir); }
    }

    private static void TryCleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* test cleanup best-effort */ }
    }

    // Reuses the same deterministic cipher as ClipboardHistoryStoreTests so
    // the archive's encryption semantics are provably identical.
    private sealed class FakeCipher : IClipboardEntryCipher
    {
        public string Encrypt(string plaintext) =>
            "ENC:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

        public string? Decrypt(string ciphertext)
        {
            if (!ciphertext.StartsWith("ENC:", StringComparison.Ordinal)) return null;
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext[4..])); }
            catch { return null; }
        }
    }
}
