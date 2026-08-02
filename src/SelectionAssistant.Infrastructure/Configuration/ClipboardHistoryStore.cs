using System.Globalization;
using System.Text;
using System.Text.Json;
using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Platform.Abstractions.Secrets;

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
    public const int CurrentSchemaVersion = 5;
    public const int MaximumFileBytes = 8 * 1024 * 1024; // 8 MB safety cap.

    /// <summary>Placeholder substituted for a sensitive entry whose ciphertext
    /// cannot be decrypted (wrong account, corrupt cipher, DPAPI unavailable).
    /// Keeping the entry but hiding its content is safer than dropping it — the
    /// user can still delete or pin it, and the next Save re-encrypts whatever
    /// this placeholder becomes.</summary>
    public const string UndecryptablePlaceholder = "[无法解密]";

    /// <summary>R54 v2: decides whether an entry is "protected" and must
    /// survive a <c>ClearOlderEntries</c> sweep. Pure function over the entry's
    /// own flags + the tag-assignment view, so it can be unit-tested in isolation
    /// (the service layer that calls it has no test project). An entry is kept
    /// when ANY of these hold:
    /// <list type="bullet">
    ///   <item>Pinned (★) or favorited (❤, lives in the assignments map).</item>
    ///   <item>Has a custom-tag assignment (the left-nav "Move to…" tab system).</item>
    ///   <item>Has per-entry annotation tags (EntryTags badges).</item>
    ///   <item>Is an image entry (Kind=Image — screenshots are always worth keeping).</item>
    ///   <item>Was auto-classified into a non-Text group (Link/Code/Json/Shell/
    ///         Contact/Number/Sensitive). Only plain Text-bucket entries are
    ///         considered disposable, so the sweep cleans ordinary text snippets
    ///         while sparing anything structured or recognisable.</item>
    /// </list></summary>
    /// <param name="entry">The candidate entry.</param>
    /// <param name="assignedTagCount">Number of tags assigned to this entry in
    /// the custom-tag map (0 when unassigned / not in the map).</param>
    public static bool IsProtected(ClipboardEntry entry, int assignedTagCount)
    {
        if (entry.IsPinned) return true;
        if (entry.EntryTags.Count > 0) return true;
        if (assignedTagCount > 0) return true;
        if (entry.Kind == ClipboardEntryKind.Image) return true;
        // R54 v2: respect the user's manual group correction. If they pulled an
        // entry out of the catch-all Text into any structured category (incl.
        // Sensitive), treat it as deliberately marked and protect it from a
        // ClearOlder sweep. The override wins over the auto Group.
        ClipboardGroup effective = entry.GroupOverride ?? entry.Group;
        if (effective != ClipboardGroup.Text) return true;
        return false;
    }

    /// <summary>Reads just the top-level <c>schemaVersion</c> from the file
    /// without parsing entries. Returns 0 when the file is missing, corrupt, or
    /// lacks the field. Used by <c>ClipboardHistoryService</c> to decide whether
    /// an eager one-time migration (v1/v2 plaintext → v3 encrypted) is needed.
    /// Never throws.</summary>
    public static int ReadSchemaVersion(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumFileBytes)
            {
                return 0;
            }

            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("schemaVersion", out JsonElement schema) &&
                schema.TryGetInt32(out int schemaVersion))
            {
                return schemaVersion;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return 0;
    }

    /// <summary>Loads the full history from <paramref name="path"/>. Missing or
    /// corrupt file returns an empty list (and logs nothing here — the caller
    /// decides). Never throws on read.
    /// <para>
    /// R54 v2 Phase 2: when <paramref name="cipher"/> is provided, entries
    /// marked <c>isEncrypted</c> (schema v3) are decrypted back to plaintext.
    /// A decryption failure (or a null cipher on an encrypted file) substitutes
    /// <see cref="UndecryptablePlaceholder"/> — the entry is retained but its
    /// secret is hidden. Legacy schema-v1/v2 entries are always plaintext and
    /// load unchanged.</para></summary>
    public static List<ClipboardEntry> Load(string path, IClipboardEntryCipher? cipher = null)
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
                schemaVersion is < 1 or > CurrentSchemaVersion)
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
                ClipboardEntry? entry = TryReadEntry(item, cipher);
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
    /// <see cref="ProviderConfigurationException"/> only on I/O failure.
    /// <para>
    /// R54 v2 Phase 2: when <paramref name="cipher"/> is provided, sensitive
    /// text entries are DPAPI-encrypted before being written and flagged
    /// <c>isEncrypted:true</c>. Non-sensitive, image, or empty entries stay
    /// plaintext. An <see cref="IClipboardEntryCipher.Encrypt"/> failure falls
    /// back to plaintext (so a transient DPAPI error never loses data).</para></summary>
    public static bool Save(IReadOnlyList<ClipboardEntry> entries, string path, IClipboardEntryCipher? cipher = null)
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
                    WriteEntry(writer, entry, cipher);
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
    /// <remarks>
    /// <b>R99 Bug B fix:</b> when dedup discards a prior entry that the user had
    /// annotated, those annotations are <b>migrated</b> onto the newly-inserted
    /// head entry. Without this, re-copying the same text (which happens often —
    /// re-copying a key after pasting it elsewhere, clipboard managers, etc.)
    /// would silently destroy the user's Sensitive override / IsSensitive flag /
    /// entry tags, presenting exactly as "moved to sensitive sometimes reverts".
    /// Only user-facing marks are carried over — capture metadata (Id, source,
    /// CapturedAt, auto Group) stays that of the fresh capture, which is the
    /// right semantics (the user re-captured the text, it should be "just now").
    /// </remarks>
    public static List<ClipboardEntry> AddAndEvict(
        IReadOnlyList<ClipboardEntry> entries,
        ClipboardEntry entry,
        int maxEntries,
        out IReadOnlyList<ClipboardEntry> evicted)
    {
        if (maxEntries < 1)
        {
            maxEntries = 1;
        }

        // R99 Bug B: track whether the dedup path below drops a prior entry that
        // carries user annotations. If so we rebuild `entry` (the head) to
        // inherit those annotations. Initialized to the passed-in entry so the
        // no-dedup path is unchanged.
        ClipboardEntry head = entry;
        bool headNeedsRebuild = false;
        ClipboardGroup? carryGroupOverride = null;
        bool carryIsSensitive = false;
        IReadOnlyList<string> carryEntryTags = [];

        var result = new List<ClipboardEntry>(entries.Count + 1);

        // Dedup + re-add the rest. Text entries dedup by exact text; image
        // entries dedup by ImageFileName (the service embeds a content-hash
        // suffix in the name, so identical pixels → identical name → dropped).
        bool dedupByText = !string.IsNullOrEmpty(entry.Text);
        bool dedupByImage = entry.Kind == ClipboardEntryKind.Image &&
                            !string.IsNullOrEmpty(entry.ImageFileName);

        foreach (ClipboardEntry existing in entries)
        {
            bool dropAsDupe = false;
            if (!existing.IsPinned)
            {
                if (dedupByText &&
                    existing.Kind == ClipboardEntryKind.Text &&
                    string.Equals(existing.Text, entry.Text, StringComparison.Ordinal))
                {
                    dropAsDupe = true;
                }
                else if (dedupByImage &&
                    existing.Kind == ClipboardEntryKind.Image &&
                    string.Equals(existing.ImageFileName, entry.ImageFileName, StringComparison.Ordinal))
                {
                    dropAsDupe = true;
                }
            }

            if (dropAsDupe)
            {
                // R99 Bug B: prior entry is being dropped — if the user had
                // annotated it, remember the annotations to carry onto head.
                // Multiple dupes are theoretically possible (shouldn't happen
                // normally, but AddAndEvict must stay defensive): later drops
                // win for GroupOverride/IsSensitive, entryTags are unioned.
                if (existing.GroupOverride is not null || existing.IsSensitive || existing.EntryTags.Count > 0)
                {
                    headNeedsRebuild = true;
                    if (existing.GroupOverride is not null) carryGroupOverride = existing.GroupOverride;
                    if (existing.IsSensitive) carryIsSensitive = true;
                    if (existing.EntryTags.Count > 0)
                    {
                        carryEntryTags = carryEntryTags.Count == 0
                            ? existing.EntryTags
                            : carryEntryTags.Union(existing.EntryTags, StringComparer.Ordinal).ToList();
                    }
                }
                continue; // identical text/image already at front
            }
            result.Add(existing);
        }

        if (headNeedsRebuild)
        {
            head = entry with
            {
                GroupOverride = carryGroupOverride,
                IsSensitive = carryIsSensitive,
                EntryTags = carryEntryTags,
            };
        }
        result.Insert(0, head);

        return EvictToMax(result, maxEntries, out evicted);
    }

    /// <summary>Legacy overload that discards evicted entries. Preserved for
    /// backward compatibility with existing call sites and tests; new callers
    /// that want to archive should use the <c>out evicted</c> overload.</summary>
    public static List<ClipboardEntry> AddAndEvict(
        IReadOnlyList<ClipboardEntry> entries,
        ClipboardEntry entry,
        int maxEntries) =>
        AddAndEvict(entries, entry, maxEntries, out _);

    /// <summary>Trims non-pinned entries until the non-pinned count is ≤
    /// <paramref name="maxEntries"/>. The <b>oldest</b> non-pinned entries (by
    /// <see cref="ClipboardEntry.CapturedAt"/>) are evicted; pinned entries are
    /// never removed. Input order is otherwise preserved.</summary>
    /// <param name="evicted">R102: receives the entries that were evicted (in
    /// ascending <see cref="ClipboardEntry.CapturedAt"/> order). Empty when
    /// nothing was dropped. Callers that archive evicted entries (instead of
    /// silently dropping them) read from this list.</param>
    public static List<ClipboardEntry> EvictToMax(
        IReadOnlyList<ClipboardEntry> entries,
        int maxEntries,
        out IReadOnlyList<ClipboardEntry> evicted)
    {
        if (maxEntries < 0)
        {
            maxEntries = 0;
        }

        int nonPinned = entries.Count(e => !e.IsPinned);
        if (nonPinned <= maxEntries)
        {
            evicted = Array.Empty<ClipboardEntry>();
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
        // R102: collect evicted entries (in input order; the caller does not
        // depend on order, but stable input order is least surprising).
        var evictedList = new List<ClipboardEntry>(dropIndices.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            if (dropIndices.Contains(i))
            {
                evictedList.Add(entries[i]);
            }
            else
            {
                result.Add(entries[i]);
            }
        }
        evicted = evictedList;
        return result;
    }

    /// <summary>Legacy overload that discards evicted entries. Preserved for
    /// backward compatibility with existing call sites and tests; new callers
    /// that want to archive should use the <c>out evicted</c> overload.</summary>
    public static List<ClipboardEntry> EvictToMax(IReadOnlyList<ClipboardEntry> entries, int maxEntries) =>
        EvictToMax(entries, maxEntries, out _);

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

        // Do not call ReplaceLineEndings over the complete clipboard body here.
        // A row only displays 80 characters, while a terminal capture can be
        // hundreds of thousands of characters. The old implementation created
        // a body-sized temporary string on the UI thread for every refresh.
        // Keep at most the 81 normalized characters needed to decide whether an
        // ellipsis is required. We may still scan leading/trailing whitespace to
        // preserve Trim semantics, but allocations remain O(preview length).
        const int maximumLength = 80;
        const int ellipsisPrefixLength = 77;
        var preview = new StringBuilder(maximumLength + 1);
        bool started = false;
        int normalizedLength = 0;
        int lastNonWhitespaceLength = 0;

        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            char normalized;
            if (current == '\r')
            {
                normalized = ' ';
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
            }
            else if (current is '\n' or '\f' or '\u0085' or '\u2028' or '\u2029')
            {
                normalized = ' ';
            }
            else
            {
                normalized = current;
            }

            if (!started)
            {
                if (char.IsWhiteSpace(normalized))
                {
                    continue;
                }

                started = true;
            }

            normalizedLength++;
            if (preview.Length <= maximumLength)
            {
                preview.Append(normalized);
            }

            if (!char.IsWhiteSpace(normalized))
            {
                lastNonWhitespaceLength = normalizedLength;
                if (lastNonWhitespaceLength > maximumLength)
                {
                    break;
                }
            }
        }

        if (!started || lastNonWhitespaceLength == 0)
        {
            return string.Empty;
        }

        return lastNonWhitespaceLength <= maximumLength
            ? preview.ToString(0, lastNonWhitespaceLength)
            : preview.ToString(0, ellipsisPrefixLength) + "…";
    }

    /// <summary>R54 v1.2: full multi-line text for the expanded row view.
    /// Keeps original line breaks (unlike <see cref="BuildPreview"/>, which
    /// flattens them), caps at <paramref name="maxChars"/> as a hard ceiling
    /// (the TextBlock's MaxLines does the visual truncation), and appends an
    /// ellipsis when the hard ceiling is hit. Sensitive entries still mask
    /// (●●●●) until explicitly revealed — expanding never bypasses the mask.</summary>
    public static string BuildExpanded(string text, bool isSensitive, bool maskSensitive, int maxChars = 20000)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (isSensitive && maskSensitive)
        {
            return new string('●', Math.Min(text.Length, 32));
        }

        // Preserve leading content; only trim trailing whitespace so the user
        // sees where the text actually starts. Don't trim the whole thing (that
        // would drop meaningful leading newlines/indentation in code snippets).
        string trimmed = text.TrimEnd();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars] + "…";
    }

    private static ClipboardEntry? TryReadEntry(JsonElement item, IClipboardEntryCipher? cipher)
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

        string rawText = item.TryGetProperty("text", out JsonElement textElement)
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

        // R54 v2 (schema 2): kind + imageFileName. Missing/invalid → Text (v1
        // entries and any corrupt record degrade safely to a text entry).
        ClipboardEntryKind kind = ClipboardEntryKind.Text;
        if (item.TryGetProperty("kind", out JsonElement kindElement) &&
            kindElement.ValueKind == JsonValueKind.Number &&
            kindElement.TryGetInt32(out int kindInt) &&
            Enum.IsDefined(typeof(ClipboardEntryKind), kindInt))
        {
            kind = (ClipboardEntryKind)kindInt;
        }

        string? imageFileName = item.TryGetProperty("imageFileName", out JsonElement imageElement)
            ? imageElement.GetString()
            : null;

        // R54 v2 Phase 2 (schema 3): isEncrypted marks DPAPI-protected text.
        // Defaults to false so schema v1/v2 entries (which never had the field)
        // load as plaintext. On read we only care about the stored flag, not
        // IsSensitive — a record may have been written encrypted and later had
        // its in-memory sensitivity reclassified; the on-disk flag is the
        // source of truth for "this text field needs decryption."
        bool isEncrypted = item.TryGetProperty("isEncrypted", out JsonElement encryptedElement) &&
                           encryptedElement.ValueKind == JsonValueKind.True;

        // R54 v2 (schema 4): per-entry annotation tags. Missing field on older
        // files = empty list (backward compatible). Dedup preserving first-seen
        // order so hand-edited files with repeats don't show duplicate badges.
        List<string> entryTags = [];
        if (item.TryGetProperty("entryTags", out JsonElement tagsElement) &&
            tagsElement.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement tagEl in tagsElement.EnumerateArray())
            {
                string? tag = tagEl.GetString();
                if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag))
                {
                    entryTags.Add(tag);
                }
            }
        }

        // R54 v2 (schema 5): user correction of the auto-classified group.
        // Missing/invalid on older files = null (revert to automatic Group).
        // Images ignore it on write, so any stale value on an image record is
        // dropped on the next save.
        ClipboardGroup? groupOverride = null;
        if (item.TryGetProperty("groupOverride", out JsonElement overrideElement) &&
            overrideElement.ValueKind == JsonValueKind.Number &&
            overrideElement.TryGetInt32(out int overrideInt) &&
            Enum.IsDefined(typeof(ClipboardGroup), overrideInt))
        {
            groupOverride = (ClipboardGroup)overrideInt;
        }

        // Resolve the text: decrypt if flagged, else use raw plaintext. A null
        // cipher on an encrypted entry, or any decryption failure, falls back
        // to the placeholder. The entry itself is kept (user can still
        // delete/pin it); only the secret content is hidden. We deliberately do
        // NOT re-store the placeholder as if it were the original secret —
        // IsSensitive is preserved so the next Save re-encrypts whatever the
        // text field holds at that point.
        string text = rawText;
        if (isEncrypted)
        {
            string? decrypted = cipher?.Decrypt(rawText);
            text = decrypted ?? UndecryptablePlaceholder;
        }

        return new ClipboardEntry
        {
            Id = id,
            Kind = kind,
            Text = text,
            ImageFileName = imageFileName,
            SourceProcessName = source,
            CapturedAt = capturedAt,
            IsPinned = isPinned,
            Group = group,
            GroupOverride = groupOverride,
            IsSensitive = isSensitive,
            EntryTags = entryTags,
        };
    }

    private static void WriteEntry(Utf8JsonWriter writer, ClipboardEntry entry, IClipboardEntryCipher? cipher)
    {
        writer.WriteStartObject();
        writer.WriteString("id", entry.Id);
        // R54 v2: always write kind so readers can distinguish image entries.
        writer.WriteNumber("kind", (int)entry.Kind);

        // R54 v2 Phase 2: encrypt the text of sensitive TEXT entries when a
        // cipher is available. Image entries (empty text), non-sensitive
        // entries, empty text, and the no-cipher (legacy) path all stay
        // plaintext with isEncrypted=false. An Encrypt failure is swallowed —
        // we fall through to plaintext rather than dropping the entry. This
        // means a transient DPAPI outage degrades to "secret temporarily on
        // disk in plaintext" instead of "secret lost," which is the safer
        // failure mode for a clipboard history.
        bool isEncrypted = false;
        string textToWrite = entry.Text;
        if (cipher is not null &&
            entry.IsSensitive &&
            entry.Kind == ClipboardEntryKind.Text &&
            !string.IsNullOrEmpty(entry.Text))
        {
            try
            {
                textToWrite = cipher.Encrypt(entry.Text);
                isEncrypted = true;
            }
            catch
            {
                // DPAPI failed — fall back to plaintext. Logged by caller.
                textToWrite = entry.Text;
                isEncrypted = false;
            }
        }

        // Text is empty for image entries; write it anyway (cheap, keeps the
        // reader simple and the field always present for text entries).
        writer.WriteString("text", textToWrite);
        writer.WriteBoolean("isEncrypted", isEncrypted);
        if (entry.ImageFileName is not null)
        {
            writer.WriteString("imageFileName", entry.ImageFileName);
        }
        if (entry.SourceProcessName is not null)
        {
            writer.WriteString("source", entry.SourceProcessName);
        }
        writer.WriteString("capturedAt", entry.CapturedAt.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteBoolean("isPinned", entry.IsPinned);
        writer.WriteNumber("group", (int)entry.Group);
        writer.WriteBoolean("isSensitive", entry.IsSensitive);
        // R54 v2 (schema v5): user correction of the auto-classified group.
        // Only written when non-null — keeps entries compact and means legacy
        // files rewritten without an override stay minimal. Images never carry
        // an override (guarded at SetGroupOverride time), so even a stale value
        // on an image record is dropped here.
        if (entry.GroupOverride is { } groupOverride)
        {
            writer.WriteNumber("groupOverride", (int)groupOverride);
        }
        // R54 v2 (schema v4): per-entry annotation tags. Only written when
        // non-empty — keeps old-style entries compact and means v1/v2/v3 files
        // rewritten without tags stay byte-identical apart from schemaVersion.
        if (entry.EntryTags.Count > 0)
        {
            writer.WriteStartArray("entryTags");
            foreach (string tag in entry.EntryTags)
            {
                writer.WriteStringValue(tag);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}
