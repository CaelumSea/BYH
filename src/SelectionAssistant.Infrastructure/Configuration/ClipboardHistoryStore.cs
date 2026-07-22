using System.Globalization;
using System.Text.Json;
using SelectionAssistant.Core.Clipboard;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// AOT-safe, atomic JSON persistence for R54 clipboard-history entries. Mirrors
/// the pattern of <see cref="SpotlightTriggerStore"/> (manual
/// <see cref="JsonDocument"/> read + <see cref="Utf8JsonWriter"/> temp-file
/// move) but holds the entry list rather than a single settings record.
/// </summary>
/// <remarks>
/// <b>LRU policy:</b> <see cref="Add"/> inserts at the front (newest first).
/// When the non-pinned count exceeds <paramref name="maxEntries"/>, the oldest
/// non-pinned entries (highest <see cref="ClipboardEntry.CapturedAt"/> after the
/// newest) are dropped. Pinned entries are never evicted and always sort first.
/// The store is pure-ish (no Win32 calls) so it can be unit-tested with temp
/// files. <b>Thread safety:</b> the caller (<c>ClipboardHistoryService</c>) is
/// responsible for serialization — the store itself is not locked.
/// </remarks>
public static class ClipboardHistoryStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 8 * 1024 * 1024; // 8 MB safety cap.

    /// <summary>Loads the full history from <paramref name="path"/>. Missing or
    /// corrupt file returns an empty list (and logs nothing here — the caller
    /// decides). Never throws on read.</summary>
    public static List<ClipboardEntry> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
            {
                return [];
            }

            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                return [];
            }

            if (!root.TryGetProperty("entries", out JsonElement entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<ClipboardEntry>(entries.GetArrayLength());
            foreach (JsonElement item in entries.EnumerateArray())
            {
                ClipboardEntry? entry = TryReadEntry(item);
                if (entry is not null)
                {
                    result.Add(entry);
                }
            }

            return OrderForDisplay(result);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Persists <paramref name="entries"/> atomically (temp file +
    /// move). Validates the file stays under <see cref="MaximumFileBytes"/> and
    /// refuses (returns false) if the serialized size would exceed it. Throws
    /// <see cref="ProviderConfigurationException"/> only on I/O failure.</summary>
    public static bool Save(IReadOnlyList<ClipboardEntry> entries, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
                writer.WriteStartArray("entries");
                foreach (ClipboardEntry entry in entries)
                {
                    WriteEntry(writer, entry);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
            }

            if (new FileInfo(tempPath).Length > MaximumFileBytes)
            {
                try { File.Delete(tempPath); } catch { }
                return false;
            }

            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入剪贴板历史文件。", exception);
        }
    }

    /// <summary>
    /// Adds <paramref name="entry"/> at the front of <paramref name="entries"/>,
    /// then trims the non-pinned tail to <paramref name="maxEntries"/>. Returns
    /// the new ordered list (does NOT persist — caller calls <see cref="Save"/>).
    /// Dedup-by-exact-text: if an identical non-empty text already exists, it is
    /// moved to the front instead of adding a duplicate.
    /// </summary>
    public static List<ClipboardEntry> AddAndEvict(
        IReadOnlyList<ClipboardEntry> entries,
        ClipboardEntry entry,
        int maxEntries)
    {
        if (maxEntries < 1)
        {
            maxEntries = 1;
        }

        var result = new List<ClipboardEntry>(entries.Count + 1) { entry };

        // Dedup + re-add the rest, skipping a same-text existing entry.
        if (!string.IsNullOrEmpty(entry.Text))
        {
            foreach (ClipboardEntry existing in entries)
            {
                if (!existing.IsPinned &&
                    string.Equals(existing.Text, entry.Text, StringComparison.Ordinal))
                {
                    // Drop the duplicate (entry.Text already at front).
                    continue;
                }
                result.Add(existing);
            }
        }
        else
        {
            result.AddRange(entries);
        }

        return EvictToMax(result, maxEntries);
    }

    /// <summary>Trims non-pinned entries until the non-pinned count is ≤
    /// <paramref name="maxEntries"/>. The <b>oldest</b> non-pinned entries (by
    /// <see cref="ClipboardEntry.CapturedAt"/>) are evicted; pinned entries are
    /// never removed. Input order is otherwise preserved.</summary>
    public static List<ClipboardEntry> EvictToMax(IReadOnlyList<ClipboardEntry> entries, int maxEntries)
    {
        if (maxEntries < 0)
        {
            maxEntries = 0;
        }

        int nonPinned = entries.Count(e => !e.IsPinned);
        if (nonPinned <= maxEntries)
        {
            return entries.ToList();
        }

        // Identify the oldest (nonPinned - maxEntries) non-pinned entries by
        // CapturedAt ascending. Ties broken by input order (stable).
        int toDrop = nonPinned - maxEntries;
        var dropIndices = entries
            .Select((e, i) => (entry: e, index: i))
            .Where(t => !t.entry.IsPinned)
            .OrderBy(t => t.entry.CapturedAt)
            .Take(toDrop)
            .Select(t => t.index)
            .ToHashSet();

        var result = new List<ClipboardEntry>(entries.Count - dropIndices.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            if (!dropIndices.Contains(i))
            {
                result.Add(entries[i]);
            }
        }
        return result;
    }

    /// <summary>Pinned first (by pinned time = order), then non-pinned by
    /// <see cref="ClipboardEntry.CapturedAt"/> descending (newest first). This
    /// is the canonical display order.</summary>
    public static List<ClipboardEntry> OrderForDisplay(IReadOnlyList<ClipboardEntry> entries)
    {
        return entries
            .OrderByDescending(e => e.IsPinned)
            .ThenByDescending(e => e.CapturedAt)
            .ToList();
    }

    /// <summary>Returns a masked preview string for the history row: ●●●● for
    /// sensitive entries (when <paramref name="maskSensitive"/> is true),
    /// otherwise a single-line truncated preview.</summary>
    public static string BuildPreview(string text, bool isSensitive, bool maskSensitive)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (isSensitive && maskSensitive)
        {
            return new string('●', Math.Min(text.Length, 16));
        }

        string firstLine = text.ReplaceLineEndings(" ").Trim();
        return firstLine.Length <= 80 ? firstLine : firstLine[..77] + "…";
    }

    private static ClipboardEntry? TryReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!item.TryGetProperty("id", out JsonElement idElement) ||
            !Guid.TryParse(idElement.GetString(), out Guid id))
        {
            return null;
        }

        string text = item.TryGetProperty("text", out JsonElement textElement)
            ? textElement.GetString() ?? string.Empty
            : string.Empty;

        string? source = item.TryGetProperty("source", out JsonElement sourceElement)
            ? sourceElement.GetString()
            : null;

        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        if (item.TryGetProperty("capturedAt", out JsonElement capturedElement) &&
            DateTimeOffset.TryParse(capturedElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
        {
            capturedAt = parsed;
        }

        bool isPinned = item.TryGetProperty("isPinned", out JsonElement pinnedElement) &&
                        pinnedElement.ValueKind == JsonValueKind.True;

        ClipboardGroup group = ClipboardGroup.Text;
        if (item.TryGetProperty("group", out JsonElement groupElement) &&
            groupElement.ValueKind == JsonValueKind.Number &&
            groupElement.TryGetInt32(out int groupInt) &&
            Enum.IsDefined(typeof(ClipboardGroup), groupInt))
        {
            group = (ClipboardGroup)groupInt;
        }

        bool isSensitive = item.TryGetProperty("isSensitive", out JsonElement sensitiveElement) &&
                           sensitiveElement.ValueKind == JsonValueKind.True;

        return new ClipboardEntry
        {
            Id = id,
            Text = text,
            SourceProcessName = source,
            CapturedAt = capturedAt,
            IsPinned = isPinned,
            Group = group,
            IsSensitive = isSensitive,
        };
    }

    private static void WriteEntry(Utf8JsonWriter writer, ClipboardEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("id", entry.Id);
        writer.WriteString("text", entry.Text);
        if (entry.SourceProcessName is not null)
        {
            writer.WriteString("source", entry.SourceProcessName);
        }
        writer.WriteString("capturedAt", entry.CapturedAt.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteBoolean("isPinned", entry.IsPinned);
        writer.WriteNumber("group", (int)entry.Group);
        writer.WriteBoolean("isSensitive", entry.IsSensitive);
        writer.WriteEndObject();
    }
}
