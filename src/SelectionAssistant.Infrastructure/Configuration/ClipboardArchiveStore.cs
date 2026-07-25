using System.Globalization;
using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Platform.Abstractions.Secrets;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// R102: Monthly sharded archive for clipboard entries that were evicted from
/// the live <c>clipboard-history.json</c> by LRU. The live file caps the most
/// recent <c>MaxEntries</c> items; anything older is moved here instead of
/// being silently dropped, so users keep a long-term history.
/// </summary>
/// <remarks>
/// <para><b>Sharding</b>: entries are grouped by capture month
/// (<c>YYYY-MM</c>) into separate JSON files under
/// <paramref name="archiveDirectory"/>. A month holds at most ~1000 entries
/// (one full LRU cycle) × ~1KB ≈ 1MB, well under the 8MB
/// <see cref="ClipboardHistoryStore.MaximumFileBytes"/> cap.</para>
/// <para><b>Format</b>: each archive file is structurally identical to
/// <c>clipboard-history.json</c> — same schema, same
/// <see cref="ClipboardHistoryStore.WriteEntry"/>/<see cref="ClipboardHistoryStore.TryReadEntry"/>
/// round-trip, same optional DPAPI encryption of sensitive entries. Reusing
/// <see cref="ClipboardHistoryStore.Load"/>/<see cref="ClipboardHistoryStore.Save"/>
/// means zero serialization code is duplicated.</para>
/// <para><b>What gets archived</b>: only <b>system-initiated LRU evictions</b>
/// (new capture pushes out oldest, or user lowered <c>MaxEntries</c>).
/// <b>User-initiated deletions</b> (<c>Delete</c>/<c>ClearNonPinned</c>/
/// <c>ClearOlderEntries</c>) bypass the archive entirely — when the user
/// deletes, they mean it. Image entries are not archived either: their paired
/// .png/.dib files are large and deleted by the service, so archiving only
/// the metadata would leave dangling references.</para>
/// <para><b>Failure mode</b>: archive I/O errors are caught by the caller and
/// logged — they never break the main clipboard flow. An archive miss just
/// means the entry is gone (same as the pre-R102 behavior).</para>
/// </remarks>
public static class ClipboardArchiveStore
{
    /// <summary>The archive schema version. Independent of the live file's
    /// schema (they happen to match today, but the archive can evolve
    /// separately if we ever add archive-only fields).</summary>
    public const int CurrentArchiveSchemaVersion = ClipboardHistoryStore.CurrentSchemaVersion;

    /// <summary>Appends <paramref name="entries"/> to the monthly archive file
    /// matching each entry's <see cref="ClipboardEntry.CapturedAt"/>. Entries
    /// are grouped by year-month before writing so a single call spanning a
    /// month boundary lands in two files. The file is read-modify-written
    /// (load existing → concat → save); archive frequency is low (one LRU
    /// eviction per clipboard change at most), so the read cost is acceptable.
    /// Returns the number of entries actually written.</summary>
    public static int AppendToArchive(
        IReadOnlyList<ClipboardEntry> entries,
        string archiveDirectory,
        IClipboardEntryCipher? cipher = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);
        if (entries.Count == 0) return 0;

        Directory.CreateDirectory(archiveDirectory);

        // Group by capture month so a call spanning 2026-07-31 23:59 → 2026-08-01
        // 00:00 lands in the right files.
        var byMonth = new Dictionary<string, List<ClipboardEntry>>(StringComparer.Ordinal);
        foreach (ClipboardEntry entry in entries)
        {
            // Defensive: only text entries should reach here, but filter again
            // so a future caller passing mixed kinds does the safe thing
            // (image entries would have dangling file references post-delete).
            if (entry.Kind != ClipboardEntryKind.Text) continue;
            string month = FormatMonthKey(entry.CapturedAt);
            if (!byMonth.TryGetValue(month, out List<ClipboardEntry>? bucket))
            {
                bucket = new List<ClipboardEntry>();
                byMonth[month] = bucket;
            }
            bucket.Add(entry);
        }

        int written = 0;
        foreach ((string month, List<ClipboardEntry> bucket) in byMonth)
        {
            string path = Path.Combine(archiveDirectory, month + ".json");

            // Load existing (empty if first write of the month), append, save.
            // Reuses the live file's Load/Save — same schema, same encryption,
            // same atomic temp-file write. OrderForDisplay inside Load sorts by
            // pinned-then-time-desc; we re-Save which writes in that order, so
            // successive appends keep a stable newest-first ordering.
            List<ClipboardEntry> existing = ClipboardHistoryStore.Load(path, cipher);
            existing.AddRange(bucket);
            ClipboardHistoryStore.Save(existing, path, cipher);
            written += bucket.Count;
        }
        return written;
    }

    /// <summary>Loads every archived entry across all monthly files, in
    /// arbitrary order (caller sorts as needed). Designed for future search
    /// across the full history. Returns empty if the directory is missing or
    /// every file is corrupt/unreadable.</summary>
    public static List<ClipboardEntry> LoadAll(string archiveDirectory, IClipboardEntryCipher? cipher = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);
        var result = new List<ClipboardEntry>();
        if (!Directory.Exists(archiveDirectory)) return result;

        foreach (string path in Directory.EnumerateFiles(archiveDirectory, "*.json"))
        {
            List<ClipboardEntry> monthEntries = ClipboardHistoryStore.Load(path, cipher);
            result.AddRange(monthEntries);
        }
        return result;
    }

    /// <summary>Formats a capture timestamp as the archive file key:
    /// <c>YYYY-MM</c> (e.g. <c>2026-07</c>). Invariant culture so the file
    /// name is stable across locales; the on-disk sort order then matches
    /// chronological order.</summary>
    public static string FormatMonthKey(DateTimeOffset capturedAt) =>
        capturedAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
