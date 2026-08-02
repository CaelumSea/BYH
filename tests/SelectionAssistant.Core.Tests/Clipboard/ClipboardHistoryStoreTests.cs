using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Infrastructure.Configuration;
using SelectionAssistant.Platform.Abstractions.Secrets;
using Xunit;

namespace SelectionAssistant.Core.Tests.Clipboard;

public sealed class ClipboardHistoryStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-clip-{Guid.NewGuid():N}.json");

    private static ClipboardEntry Entry(
        string text,
        int ageSeconds = 0,
        bool pinned = false,
        bool isSensitive = false) =>
        new()
        {
            Text = text,
            CapturedAt = DateTimeOffset.UtcNow.AddSeconds(-ageSeconds),
            IsPinned = pinned,
            Group = ClipboardClassifier.Classify(text),
            // Batch 124: Sensitive is no longer auto-derived from text (the
            // classifier's IsSensitive helper is retired). Tests that need a
            // sensitive entry pass isSensitive: true explicitly — mirroring
            // the production path where only SetGroupOverride sets it.
            IsSensitive = isSensitive,
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
                // Batch 124: isSensitive is explicit (no auto-detection). The
                // test still proves a sensitive+pinned entry round-trips and
                // sorts to the front; the entry text is incidental.
                Entry("password=secret", pinned: true, isSensitive: true),
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

    // R99 Bug B: re-copying the same text used to silently destroy the user's
    // Sensitive override / IsSensitive / entry tags because the prior (annotated)
    // entry was dropped and a fresh (unannotated) one took its place. AddAndEvict
    // now migrates those annotations onto the new head entry.
    [Fact]
    public void AddAndEvict_DuplicateText_CarriesSensitiveAndTagsOntoNewHead()
    {
        // Prior entry the user manually marked Sensitive + tagged "aws".
        var prior = new ClipboardEntry
        {
            Text = "AKIAIOSFODNN7EXAMPLE",
            CapturedAt = DateTimeOffset.UtcNow.AddSeconds(-60),
            Group = ClipboardGroup.Text,
            GroupOverride = ClipboardGroup.Sensitive,
            IsSensitive = true,
            EntryTags = new[] { "aws" },
        };
        var existing = new List<ClipboardEntry> { prior };

        // User re-copies the same text → fresh capture (new Id, classifier ran
        // but no user annotations yet: GroupOverride=null, EntryTags=[]).
        // Batch 124: the classifier no longer auto-tags AKIA as Sensitive, so
        // the fresh capture lands as Text/IsSensitive=false — exactly the
        // "unannotated re-copy" this test exercises. The dedup carry-forward
        // then restores the user's prior Sensitive override.
        ClipboardEntry reCopy = new()
        {
            Text = "AKIAIOSFODNN7EXAMPLE",
            CapturedAt = DateTimeOffset.UtcNow,
            Group = ClipboardGroup.Text,
            IsSensitive = false,
        };

        List<ClipboardEntry> result = ClipboardHistoryStore.AddAndEvict(existing, reCopy, 100);

        Assert.Single(result, e => e.Text == "AKIAIOSFODNN7EXAMPLE");
        ClipboardEntry head = result[0];
        // New Id (the re-capture), but the user's annotations survived.
        Assert.NotEqual(prior.Id, head.Id);
        Assert.Equal(ClipboardGroup.Sensitive, head.GroupOverride);
        Assert.True(head.IsSensitive);
        Assert.Contains("aws", head.EntryTags);
    }

    [Fact]
    public void AddAndEvict_DuplicateText_NoAnnotations_UnchangedBehavior()
    {
        // Regression guard: a plain re-copy with NO user annotations on the
        // prior entry must behave exactly as before (no phantom marks appear).
        var existing = new List<ClipboardEntry>
        {
            Entry("plain text", ageSeconds: 60),
        };
        ClipboardEntry reCopy = new()
        {
            Text = "plain text",
            CapturedAt = DateTimeOffset.UtcNow,
            Group = ClipboardGroup.Text,
            IsSensitive = false,
        };

        List<ClipboardEntry> result = ClipboardHistoryStore.AddAndEvict(existing, reCopy, 100);

        ClipboardEntry head = result[0];
        Assert.Null(head.GroupOverride);
        Assert.False(head.IsSensitive);
        Assert.Empty(head.EntryTags);
    }

    [Fact]
    public void AddAndEvict_DuplicateText_CarriesTagsEvenWhenNotSensitive()
    {
        // Tags are independent of the Sensitive override — a non-sensitive entry
        // the user tagged must also survive a re-copy.
        var prior = new ClipboardEntry
        {
            Text = "git checkout main",
            CapturedAt = DateTimeOffset.UtcNow.AddSeconds(-60),
            Group = ClipboardGroup.Shell,
            IsSensitive = false,
            EntryTags = new[] { "git", "daily" },
        };
        var existing = new List<ClipboardEntry> { prior };
        ClipboardEntry reCopy = new()
        {
            Text = "git checkout main",
            CapturedAt = DateTimeOffset.UtcNow,
            Group = ClipboardGroup.Shell,
            IsSensitive = false,
        };

        List<ClipboardEntry> result = ClipboardHistoryStore.AddAndEvict(existing, reCopy, 100);

        ClipboardEntry head = result[0];
        Assert.NotEqual(prior.Id, head.Id);
        Assert.False(head.IsSensitive);
        Assert.Contains("git", head.EntryTags);
        Assert.Contains("daily", head.EntryTags);
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

    // ── R102: out evicted parameter ──

    [Fact]
    public void AddAndEvict_OutParam_ReturnsEvictedEntries()
    {
        // 3 existing non-pinned + 1 new = 4 non-pinned; max=2 → evict the 2
        // oldest ("a" age 100, "b" age 50). The out list must contain exactly
        // those two; the kept list must exclude them.
        var existing = new List<ClipboardEntry>
        {
            Entry("a", ageSeconds: 100),
            Entry("b", ageSeconds: 50),
            Entry("c", ageSeconds: 10),
        };
        ClipboardEntry newEntry = Entry("new", ageSeconds: 0);

        List<ClipboardEntry> result = ClipboardHistoryStore.AddAndEvict(
            existing, newEntry, maxEntries: 2, out IReadOnlyList<ClipboardEntry> evicted);

        Assert.Equal("new", result[0].Text);
        Assert.DoesNotContain(result, e => e.Text == "a");
        Assert.DoesNotContain(result, e => e.Text == "b");
        Assert.Equal(2, evicted.Count);
        Assert.Contains(evicted, e => e.Text == "a");
        Assert.Contains(evicted, e => e.Text == "b");
    }

    [Fact]
    public void EvictToMax_OutParam_EmptyWhenNothingEvicted()
    {
        var entries = new List<ClipboardEntry>
        {
            Entry("a", ageSeconds: 10),
            Entry("b", ageSeconds: 5),
        };
        // max=5, only 2 non-pinned → nothing to evict, out must be empty.
        List<ClipboardEntry> result = ClipboardHistoryStore.EvictToMax(
            entries, maxEntries: 5, out IReadOnlyList<ClipboardEntry> evicted);

        Assert.Equal(2, result.Count);
        Assert.Empty(evicted);
    }

    [Fact]
    public void EvictToMax_OutParam_ReturnsMultipleEvicted()
    {
        var entries = new List<ClipboardEntry>
        {
            Entry("oldest", ageSeconds: 200),
            Entry("old", ageSeconds: 100),
            Entry("young", ageSeconds: 10),
            Entry("newest", ageSeconds: 1),
        };
        // max=2 non-pinned → 2 oldest ("oldest" + "old") evicted.
        List<ClipboardEntry> result = ClipboardHistoryStore.EvictToMax(
            entries, maxEntries: 2, out IReadOnlyList<ClipboardEntry> evicted);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, evicted.Count);
        Assert.Contains(evicted, e => e.Text == "oldest");
        Assert.Contains(evicted, e => e.Text == "old");
    }

    [Fact]
    public void EvictToMax_OutParam_PinnedNotEvicted()
    {
        var entries = new List<ClipboardEntry>
        {
            Entry("pinned-old", ageSeconds: 999, pinned: true),
            Entry("old", ageSeconds: 100),
            Entry("young", ageSeconds: 10),
        };
        // max=1 non-pinned → only "old" evicted; "pinned-old" stays despite age.
        List<ClipboardEntry> result = ClipboardHistoryStore.EvictToMax(
            entries, maxEntries: 1, out IReadOnlyList<ClipboardEntry> evicted);

        Assert.Contains(result, e => e.Text == "pinned-old");
        ClipboardEntry evictedEntry = Assert.Single(evicted);
        Assert.Equal("old", evictedEntry.Text);
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
    public void BuildPreview_FlattensSupportedLineEndingsAndTrimsWhitespace()
    {
        const string text = " \r\n\talpha\rbeta\ngamma\fdelta\u0085epsilon\u2028zeta\u2029eta  ";

        string preview = ClipboardHistoryStore.BuildPreview(text, false, true);

        Assert.Equal("alpha beta gamma delta epsilon zeta eta", preview);
    }

    [Fact]
    public void BuildPreview_LongMultilineTextDoesNotAllocateBodySizedBuffer()
    {
        string longText = string.Concat(Enumerable.Repeat("line\r\n", 45_000));
        Assert.True(longText.Length >= 270_000);

        _ = ClipboardHistoryStore.BuildPreview("warmup\nline", false, true);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        string preview = ClipboardHistoryStore.BuildPreview(longText, false, true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.EndsWith("…", preview);
        Assert.True(preview.Length <= 80);
        Assert.True(allocated < 64 * 1024,
            $"Preview creation allocated {allocated:N0} bytes for a long body.");
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

    // ── R54 v2: image entries (schema v1 → v2) ──

    private static ClipboardEntry ImageEntry(string fileName, int ageSeconds = 0, bool pinned = false) =>
        new()
        {
            Kind = ClipboardEntryKind.Image,
            Text = string.Empty,
            ImageFileName = fileName,
            CapturedAt = DateTimeOffset.UtcNow.AddSeconds(-ageSeconds),
            IsPinned = pinned,
            Group = ClipboardGroup.Text,
        };

    [Fact]
    public void SaveThenLoad_ImageEntry_RoundTrips()
    {
        string path = TempPath();
        try
        {
            var original = new List<ClipboardEntry>
            {
                ImageEntry("clip-abc123.png"),
                Entry("hello"),
            };

            Assert.True(ClipboardHistoryStore.Save(original, path));
            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path);

            Assert.Equal(2, loaded.Count);
            ClipboardEntry img = loaded.Single(e => e.Kind == ClipboardEntryKind.Image);
            Assert.Equal("clip-abc123.png", img.ImageFileName);
            Assert.Empty(img.Text);
            Assert.Equal(ClipboardGroup.Text, img.Group);
            // Text entry survived alongside the image entry.
            Assert.Contains(loaded, e => e.Kind == ClipboardEntryKind.Text && e.Text == "hello");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_SchemaV1File_UpgradesToV2()
    {
        // Hand-written schema-v1 file (no kind/imageFileName fields, schemaVersion=1).
        string path = TempPath();
        string v1Content = """
            {
              "schemaVersion": 1,
              "entries": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "text": "legacy text",
                  "capturedAt": "2026-07-22T10:00:00Z",
                  "isPinned": false,
                  "group": 7,
                  "isSensitive": false
                }
              ]
            }
            """;
        File.WriteAllText(path, v1Content);
        try
        {
            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path);
            // v1 must still load (schema 1 ≤ CurrentSchemaVersion=2).
            Assert.Single(loaded);
            Assert.Equal(ClipboardEntryKind.Text, loaded[0].Kind); // defaults to Text
            Assert.Equal("legacy text", loaded[0].Text);
            Assert.Null(loaded[0].ImageFileName); // no image field in v1
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_KindFieldMissing_DefaultsToTextSafely()
    {
        // A v2 file missing the kind field on one entry must degrade to Text
        // (corrupt/partial records never crash the loader).
        string path = TempPath();
        string content = """
            {
              "schemaVersion": 2,
              "entries": [
                {
                  "id": "22222222-2222-2222-2222-222222222222",
                  "text": "no kind field",
                  "capturedAt": "2026-07-22T10:00:00Z"
                }
              ]
            }
            """;
        File.WriteAllText(path, content);
        try
        {
            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path);
            Assert.Single(loaded);
            Assert.Equal(ClipboardEntryKind.Text, loaded[0].Kind);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_KindFieldInvalid_DefaultsToTextSafely()
    {
        string path = TempPath();
        string content = """
            {
              "schemaVersion": 2,
              "entries": [
                {
                  "id": "33333333-3333-3333-3333-333333333333",
                  "text": "bad kind",
                  "kind": 99,
                  "capturedAt": "2026-07-22T10:00:00Z"
                }
              ]
            }
            """;
        File.WriteAllText(path, content);
        try
        {
            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path);
            Assert.Single(loaded);
            // Out-of-range kind (99) is not a defined enum value → falls back to Text.
            Assert.Equal(ClipboardEntryKind.Text, loaded[0].Kind);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AddAndEvict_ImageEntry_DedupsByFileName()
    {
        // Two image entries with the same ImageFileName (identical content hash)
        // must dedup: re-adding moves the existing one to the front, no duplicate.
        var existing = new List<ClipboardEntry>
        {
            ImageEntry("clip-deadbeef.png", ageSeconds: 5),
            Entry("older text", ageSeconds: 10),
        };

        ClipboardEntry reAdd = ImageEntry("clip-deadbeef.png", ageSeconds: 0);
        List<ClipboardEntry> result = ClipboardHistoryStore.AddAndEvict(existing, reAdd, maxEntries: 100);

        // Only one image entry with that file name should remain.
        Assert.Single(result, e => e.Kind == ClipboardEntryKind.Image && e.ImageFileName == "clip-deadbeef.png");
        // And it's at the front (newest).
        Assert.Equal("clip-deadbeef.png", result[0].ImageFileName);
        // Text entry preserved.
        Assert.Contains(result, e => e.Text == "older text");
    }

    [Fact]
    public void SaveThenLoad_TextAndImageMixed_RoundTripsAll()
    {
        string path = TempPath();
        try
        {
            var original = new List<ClipboardEntry>
            {
                Entry("text one"),
                ImageEntry("img-a.png"),
                Entry("https://link.example", pinned: true),
                ImageEntry("img-b.png", pinned: true),
            };

            Assert.True(ClipboardHistoryStore.Save(original, path));
            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path);

            Assert.Equal(4, loaded.Count);
            // Two pinned first (one text link, one image), then newest.
            Assert.Equal(2, loaded.Count(e => e.IsPinned));
            Assert.Single(loaded, e => e.Kind == ClipboardEntryKind.Image && e.ImageFileName == "img-a.png");
            Assert.Single(loaded, e => e.Kind == ClipboardEntryKind.Image && e.ImageFileName == "img-b.png");
            Assert.Contains(loaded, e => e.Text == "text one");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── R54 v2 Phase 2: DPAPI encryption at the serialization boundary ──

    [Fact]
    public void SaveThenLoad_SensitiveEntry_WithCipher_RoundTripsEncrypted()
    {
        // A sensitive entry saved with a cipher must survive a save/load
        // round-trip with its plaintext intact — the cipher layer is invisible
        // to callers. The on-disk file must NOT contain the plaintext.
        string path = TempPath();
        try
        {
            var cipher = new FakeCipher();
            var original = new List<ClipboardEntry>
            {
                // Batch 124: isSensitive is explicit (no auto-detection).
                Entry("api_key=sk-secret-12345", isSensitive: true),
            };
            Assert.True(original[0].IsSensitive);

            Assert.True(ClipboardHistoryStore.Save(original, path, cipher));
            string onDisk = File.ReadAllText(path);
            // The plaintext secret must not appear verbatim in the file.
            Assert.DoesNotContain("api_key=sk-secret-12345", onDisk);
            Assert.Contains("\"isEncrypted\": true", onDisk);

            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path, cipher);
            Assert.Single(loaded);
            Assert.Equal("api_key=sk-secret-12345", loaded[0].Text);
            Assert.True(loaded[0].IsSensitive);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveThenLoad_NonSensitiveEntry_NotEncrypted()
    {
        // Non-sensitive entries are never encrypted, even when a cipher is
        // wired in — the on-disk text is plaintext and isEncrypted is false.
        string path = TempPath();
        try
        {
            var cipher = new FakeCipher();
            var original = new List<ClipboardEntry> { Entry("hello world") };
            Assert.False(original[0].IsSensitive);

            Assert.True(ClipboardHistoryStore.Save(original, path, cipher));
            string onDisk = File.ReadAllText(path);
            Assert.Contains("hello world", onDisk);
            Assert.Contains("\"isEncrypted\": false", onDisk);

            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path, cipher);
            Assert.Equal("hello world", loaded[0].Text);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_WithNullCipher_WritesPlaintextEvenForSensitive()
    {
        // No cipher (legacy/default) = backward-compatible plaintext writes,
        // even for sensitive entries. isEncrypted must be false so a future
        // cipher-enabled load doesn't try to decrypt plaintext.
        string path = TempPath();
        try
        {
            var original = new List<ClipboardEntry> { Entry("password=hunter2", isSensitive: true) };
            Assert.True(original[0].IsSensitive);

            Assert.True(ClipboardHistoryStore.Save(original, path, cipher: null));
            string onDisk = File.ReadAllText(path);
            Assert.Contains("password=hunter2", onDisk);
            Assert.Contains("\"isEncrypted\": false", onDisk);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_V2File_WithCipher_LoadsAsPlaintext()
    {
        // A hand-written schema-v2 file (no isEncrypted field, plaintext
        // sensitive text) must load correctly even when a cipher is present.
        // The missing isEncrypted field defaults to false → no decryption
        // attempted → the legacy plaintext is preserved.
        string path = TempPath();
        string v2Content = """
            {
              "schemaVersion": 2,
              "entries": [
                {
                  "id": "44444444-4444-4444-4444-444444444444",
                  "kind": 0,
                  "text": "api_key=legacy-plaintext",
                  "capturedAt": "2026-07-22T10:00:00Z",
                  "isPinned": false,
                  "group": 0,
                  "isSensitive": true
                }
              ]
            }
            """;
        File.WriteAllText(path, v2Content);
        try
        {
            var cipher = new FakeCipher();
            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path, cipher);
            Assert.Single(loaded);
            Assert.Equal("api_key=legacy-plaintext", loaded[0].Text);
            Assert.True(loaded[0].IsSensitive);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_V3Encrypted_WithWrongCipher_TextBecomesPlaceholder()
    {
        // An encrypted entry read with a cipher that fails to decrypt (wrong
        // account / corrupt / mismatched cipher) degrades to the placeholder.
        // The entry itself is retained so the user can still delete/pin it.
        string path = TempPath();
        try
        {
            // Save encrypted with FakeCipher, then load with WrongCipher.
            // Batch 124: isSensitive is explicit (no auto-detection).
            var original = new List<ClipboardEntry> { Entry("token=abc-123", isSensitive: true) };
            Assert.True(ClipboardHistoryStore.Save(original, path, new FakeCipher()));

            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path, new WrongCipher());
            Assert.Single(loaded);
            Assert.Equal(ClipboardHistoryStore.UndecryptablePlaceholder, loaded[0].Text);
            Assert.True(loaded[0].IsSensitive); // sensitivity preserved
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_V3Encrypted_WithNullCipher_TextBecomesPlaceholder()
    {
        // Degraded mode: a v3 encrypted file read with NO cipher (e.g. the app
        // downgraded) must not return the raw ciphertext as if it were text —
        // that would leak the (useless) base64 AND risk double-encryption on
        // the next save. Placeholder instead.
        string path = TempPath();
        try
        {
            var original = new List<ClipboardEntry> { Entry("secret=value", isSensitive: true) };
            Assert.True(ClipboardHistoryStore.Save(original, path, new FakeCipher()));

            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path, cipher: null);
            Assert.Single(loaded);
            Assert.Equal(ClipboardHistoryStore.UndecryptablePlaceholder, loaded[0].Text);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_IsEncryptedFieldMissing_DefaultsToFalse()
    {
        // Forward-compatibility: a v3 entry missing the isEncrypted field (e.g.
        // written by a future variant, or lightly corrupted) defaults to
        // plaintext. This mirrors how missing `kind` defaults to Text.
        string path = TempPath();
        string content = """
            {
              "schemaVersion": 3,
              "entries": [
                {
                  "id": "55555555-5555-5555-5555-555555555555",
                  "kind": 0,
                  "text": "no isEncrypted field",
                  "capturedAt": "2026-07-22T10:00:00Z",
                  "isSensitive": true
                }
              ]
            }
            """;
        File.WriteAllText(path, content);
        try
        {
            var cipher = new FakeCipher();
            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path, cipher);
            Assert.Single(loaded);
            Assert.Equal("no isEncrypted field", loaded[0].Text); // not treated as ciphertext
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_WritesCurrentSchemaVersion()
    {
        // The persisted file must carry the current schema version so future
        // loads route through the right reader path.
        string path = TempPath();
        try
        {
            var original = new List<ClipboardEntry> { Entry("hi") };
            Assert.True(ClipboardHistoryStore.Save(original, path, new FakeCipher()));
            string onDisk = File.ReadAllText(path);
            Assert.Contains($"\"schemaVersion\": {ClipboardHistoryStore.CurrentSchemaVersion}", onDisk);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReadSchemaVersion_ReturnsVersionWithoutLoadingEntries()
    {
        string path = TempPath();
        try
        {
            var original = new List<ClipboardEntry> { Entry("hi") };
            Assert.True(ClipboardHistoryStore.Save(original, path, new FakeCipher()));

            Assert.Equal(ClipboardHistoryStore.CurrentSchemaVersion, ClipboardHistoryStore.ReadSchemaVersion(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReadSchemaVersion_MissingOrCorrupt_ReturnsZero()
    {
        Assert.Equal(0, ClipboardHistoryStore.ReadSchemaVersion(
            Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.json")));

        string path = TempPath();
        File.WriteAllText(path, "not json at all");
        try
        {
            Assert.Equal(0, ClipboardHistoryStore.ReadSchemaVersion(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>A deterministic fake cipher for unit tests (no Windows DPAPI
    /// dependency). Prefixes ciphertext with "ENC:" + base64 so it's clearly
    /// distinguishable from plaintext, and round-trips exactly.</summary>
    private sealed class FakeCipher : IClipboardEntryCipher
    {
        public string Encrypt(string plaintext) =>
            "ENC:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

        public string? Decrypt(string ciphertext)
        {
            if (!ciphertext.StartsWith("ENC:", StringComparison.Ordinal))
            {
                return null;
            }
            try
            {
                return System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(ciphertext[4..]));
            }
            catch
            {
                return null;
            }
        }
    }

    // ── R54 v2: per-entry annotation tags (schema v4) ──

    [Fact]
    public void SaveThenLoad_EntryTags_RoundTrips()
    {
        // Entry tags survive a save/load round-trip in order. Multiple tags,
        // mixed with a no-tag entry, all preserved.
        string path = TempPath();
        try
        {
            var original = new List<ClipboardEntry>
            {
                Entry("aws-key-12345") with { EntryTags = ["AWS", "Prod"] },
                Entry("plain text, no tags"),
                Entry("stripe_secret_xyz") with { EntryTags = ["Stripe"] },
            };

            Assert.True(ClipboardHistoryStore.Save(original, path));
            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path);

            // Load applies display ordering (pinned first, then newest); don't
            // assume index order — look up by text instead.
            Assert.Equal(3, loaded.Count);
            ClipboardEntry awsEntry = Assert.Single(loaded, e => e.Text == "aws-key-12345");
            Assert.Equal(new[] { "AWS", "Prod" }, awsEntry.EntryTags);
            ClipboardEntry plainEntry = Assert.Single(loaded, e => e.Text == "plain text, no tags");
            Assert.Empty(plainEntry.EntryTags);
            ClipboardEntry stripeEntry = Assert.Single(loaded, e => e.Text == "stripe_secret_xyz");
            Assert.Equal(new[] { "Stripe" }, stripeEntry.EntryTags);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_V3File_MissingEntryTags_DefaultsToEmpty()
    {
        // A schema-v3 file (written before entryTags existed) must load with
        // empty tag lists, never null — backward compatibility.
        string path = TempPath();
        string content = """
        {
          "schemaVersion": 3,
          "entries": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "kind": 0,
              "text": "old entry, no tags field",
              "capturedAt": "2026-07-24T00:00:00Z",
              "isPinned": false,
              "group": 7,
              "isSensitive": false,
              "isEncrypted": false
            }
          ]
        }
        """;
        File.WriteAllText(path, content);
        try
        {
            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path);
            Assert.Single(loaded);
            Assert.NotNull(loaded[0].EntryTags);
            Assert.Empty(loaded[0].EntryTags);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WriteEntry_WithEmptyTags_OmitsEntryTagsField()
    {
        // Entries with no tags must not write the entryTags field at all —
        // keeps old-style records compact and avoids cluttering the JSON.
        string path = TempPath();
        try
        {
            var original = new List<ClipboardEntry>
            {
                Entry("no tags here"),
            };
            Assert.True(ClipboardHistoryStore.Save(original, path));

            string json = File.ReadAllText(path);
            Assert.DoesNotContain("entryTags", json);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── R54 v2: IsProtected (ClearOlderEntries keep-rule) ──

    [Fact]
    public void IsProtected_PlainUnassignedUnpinnedTextEntry_IsNotProtected()
    {
        // The baseline disposable case: plain text, no tags, not pinned, and
        // auto-classified into the Text fallback bucket. Only this shape is
        // considered clearable; everything else is protected.
        var entry = Entry("plain text") with { IsPinned = false };
        Assert.False(ClipboardHistoryStore.IsProtected(entry, assignedTagCount: 0));
    }

    [Fact]
    public void IsProtected_PinnedEntry_IsProtected()
    {
        var entry = Entry("pinned") with { IsPinned = true };
        Assert.True(ClipboardHistoryStore.IsProtected(entry, assignedTagCount: 0));
    }

    [Fact]
    public void IsProtected_EntryWithEntryTags_IsProtected()
    {
        // Per-entry annotation tags (batch 85) protect the entry.
        var entry = Entry("aws key") with { EntryTags = ["AWS"] };
        Assert.True(ClipboardHistoryStore.IsProtected(entry, assignedTagCount: 0));
    }

    [Fact]
    public void IsProtected_EntryWithCustomTagAssignment_IsProtected()
    {
        // A custom-tab tag assignment (the left-nav system) protects the entry.
        var entry = Entry("tagged via Move-to") with { IsPinned = false };
        Assert.True(ClipboardHistoryStore.IsProtected(entry, assignedTagCount: 1));
    }

    [Fact]
    public void IsProtected_FavoritedEntry_IsProtected()
    {
        // Favorite lives in the same assignments map as custom tags (it's just
        // the FavoriteTagName tag), so a non-zero assigned count covers it.
        var entry = Entry("favorited") with { IsPinned = false };
        Assert.True(ClipboardHistoryStore.IsProtected(entry, assignedTagCount: 1));
    }

    [Fact]
    public void IsProtected_ImageEntry_IsProtected()
    {
        // Screenshots are always worth keeping — never swept by ClearOlder.
        var entry = Entry(string.Empty) with
        {
            Kind = ClipboardEntryKind.Image,
            ImageFileName = "clip-abc.png",
            IsPinned = false,
        };
        Assert.True(ClipboardHistoryStore.IsProtected(entry, assignedTagCount: 0));
    }

    [Fact]
    public void IsProtected_LinkGroupEntry_IsProtected()
    {
        // Anything auto-classified into a non-Text group (Link/Code/Json/Shell/
        // Contact/Number/Sensitive) is protected — only plain Text is disposable.
        var entry = Entry("https://example.com") with { IsPinned = false };
        Assert.Equal(ClipboardGroup.Link, entry.Group); // sanity: classifier agrees
        Assert.True(ClipboardHistoryStore.IsProtected(entry, assignedTagCount: 0));
    }

    // ── R54 v2 (schema v5): groupOverride — manual correction of auto-group ──

    [Fact]
    public void SaveThenLoad_GroupOverride_RoundTrips()
    {
        // A user-set group override survives save/load, and a null override
        // (auto classification) stays null. Mixed: one overridden, one auto.
        string path = TempPath();
        try
        {
            var original = new List<ClipboardEntry>
            {
                Entry("looks plain but I marked it a secret") with { GroupOverride = ClipboardGroup.Sensitive },
                Entry("auto-classified, no override"),
                Entry("forced into Code") with { GroupOverride = ClipboardGroup.Code },
            };

            Assert.True(ClipboardHistoryStore.Save(original, path));
            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path);

            Assert.Equal(3, loaded.Count);
            ClipboardEntry secret = Assert.Single(loaded, e => e.Text == "looks plain but I marked it a secret");
            Assert.Equal(ClipboardGroup.Sensitive, secret.GroupOverride);
            ClipboardEntry auto = Assert.Single(loaded, e => e.Text == "auto-classified, no override");
            Assert.Null(auto.GroupOverride);
            ClipboardEntry code = Assert.Single(loaded, e => e.Text == "forced into Code");
            Assert.Equal(ClipboardGroup.Code, code.GroupOverride);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_V4File_MissingGroupOverride_DefaultsToNull()
    {
        // A schema-v4 file (written before groupOverride existed) must load with
        // null overrides — backward compatibility. The auto Group is preserved.
        string path = TempPath();
        string content = """
        {
          "schemaVersion": 4,
          "entries": [
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "kind": 0,
              "text": "old entry, no override field",
              "capturedAt": "2026-07-24T00:00:00Z",
              "isPinned": false,
              "group": 1,
              "isSensitive": false,
              "isEncrypted": false
            }
          ]
        }
        """;
        File.WriteAllText(path, content);
        try
        {
            List<ClipboardEntry> loaded = ClipboardHistoryStore.Load(path);
            ClipboardEntry entry = Assert.Single(loaded);
            Assert.Null(entry.GroupOverride);
            Assert.Equal(ClipboardGroup.Link, entry.Group); // group field still read
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WriteEntry_WithNullOverride_OmitsGroupOverrideField()
    {
        // Entries on auto classification must not write the groupOverride field
        // — keeps records compact and legacy files byte-minimal on rewrite.
        string path = TempPath();
        try
        {
            var original = new List<ClipboardEntry>
            {
                Entry("auto classified, no override"),
            };
            Assert.True(ClipboardHistoryStore.Save(original, path));

            string json = File.ReadAllText(path);
            Assert.DoesNotContain("groupOverride", json);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void IsProtected_EntryOverriddenToSensitive_IsProtected()
    {
        // A plain-Text entry that the user pulled into Sensitive (or any non-Text
        // group) via override is protected — the effective group wins over auto.
        // NOTE: the text must NOT contain classifier trigger words (secret, key,
        // token, password…) — otherwise the auto-group is already Sensitive and
        // the test would not be exercising the override path at all.
        var entry = Entry("just some plain notes") with
        {
            IsPinned = false,
            GroupOverride = ClipboardGroup.Sensitive,
        };
        // Auto-group is Text (no secret pattern), so without override this would
        // be clearable. The override must promote it to protected.
        Assert.Equal(ClipboardGroup.Text, entry.Group);
        Assert.True(ClipboardHistoryStore.IsProtected(entry, assignedTagCount: 0));
    }

    [Fact]
    public void IsProtected_EntryOverriddenToTextFromLink_IsClearable()
    {
        // The override wins in BOTH directions: an auto-Link entry the user
        // pushed back into the Text catch-all becomes clearable again. This is
        // the "false positive → move it out" path.
        var entry = Entry("https://example.com") with
        {
            IsPinned = false,
            GroupOverride = ClipboardGroup.Text,
        };
        Assert.Equal(ClipboardGroup.Link, entry.Group); // auto said Link
        Assert.False(ClipboardHistoryStore.IsProtected(entry, assignedTagCount: 0));
    }

    /// <summary>A cipher that always fails to decrypt — simulates the
    /// wrong-account / corrupt-cipher degradation path.</summary>
    private sealed class WrongCipher : IClipboardEntryCipher
    {
        public string Encrypt(string plaintext) => "WRONG:" + plaintext;
        public string? Decrypt(string ciphertext) => null;
    }
}
