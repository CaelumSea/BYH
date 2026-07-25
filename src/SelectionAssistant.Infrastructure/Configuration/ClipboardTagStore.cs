using System.Globalization;
using System.Text.Json;
using SelectionAssistant.Core.Clipboard;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// AOT-safe, atomic JSON persistence for R54 v1.1 clipboard-history tags.
/// Mirrors the pattern of <see cref="ClipboardHistoryStore"/> (manual
/// <see cref="JsonDocument"/> read + <see cref="Utf8JsonWriter"/> temp-file
/// move). Holds the user's custom tag names + entry→tag assignments; the main
/// <c>clipboard-history.json</c> schema is untouched (backward compatible).
/// </summary>
/// <remarks>
/// All mutating helpers (<see cref="AddCustomTag"/>,
/// <see cref="RenameCustomTag"/>, <see cref="DeleteCustomTag"/>,
/// <see cref="Assign"/>, <see cref="Unassign"/>) are <b>pure</b> — they return a
/// new <see cref="ClipboardTagData"/> and never touch disk. The caller
/// (<c>ClipboardHistoryService</c>) persists the result via
/// <see cref="Save"/>. This keeps the helpers trivially unit-testable.
/// </remarks>
public static class ClipboardTagStore
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumFileBytes = 1 * 1024 * 1024; // 1 MB cap.

    /// <summary>Loads tags from <paramref name="path"/>. Missing or corrupt file
    /// returns <see cref="ClipboardTagData.Empty"/> (never throws on read).</summary>
    public static ClipboardTagData Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return ClipboardTagData.Empty;
        }

        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
            {
                return ClipboardTagData.Empty;
            }

            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion < 1 || schemaVersion > CurrentSchemaVersion)
            {
                return ClipboardTagData.Empty;
            }

            var customTags = new List<string>();
            if (root.TryGetProperty("customTags", out JsonElement tagsElement) &&
                tagsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in tagsElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string? name = item.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            customTags.Add(name);
                        }
                    }
                }
            }

            // R54 v1.2 (schema v2): tag name → emoji icon. v1 files have no
            // tagIcons property; an empty map loads seamlessly.
            var tagIcons = new Dictionary<string, string>(StringComparer.Ordinal);
            if (schemaVersion >= 2 &&
                root.TryGetProperty("tagIcons", out JsonElement iconsElement) &&
                iconsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in iconsElement.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(prop.Name))
                    {
                        continue;
                    }
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string? icon = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(icon))
                        {
                            tagIcons[prop.Name] = icon;
                        }
                    }
                }
            }

            var assignments = new Dictionary<Guid, IReadOnlySet<string>>();
            if (root.TryGetProperty("assignments", out JsonElement assignElement) &&
                assignElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in assignElement.EnumerateObject())
                {
                    if (!Guid.TryParse(prop.Name, out Guid entryId))
                    {
                        continue;
                    }

                    if (prop.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (JsonElement tag in prop.Value.EnumerateArray())
                    {
                        if (tag.ValueKind == JsonValueKind.String)
                        {
                            string? name = tag.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                set.Add(name);
                            }
                        }
                    }

                    if (set.Count > 0)
                    {
                        assignments[entryId] = set;
                    }
                }
            }

            return new ClipboardTagData
            {
                CustomTags = customTags,
                Assignments = assignments,
                TagIcons = tagIcons,
            };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return ClipboardTagData.Empty;
        }
    }

    /// <summary>Persists <paramref name="data"/> atomically (temp file + move).
    /// Throws <see cref="ProviderConfigurationException"/> only on I/O failure.</summary>
    public static void Save(ClipboardTagData data, string path)
    {
        ArgumentNullException.ThrowIfNull(data);
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

                writer.WriteStartArray("customTags");
                foreach (string name in data.CustomTags)
                {
                    writer.WriteStringValue(name);
                }
                writer.WriteEndArray();

                writer.WriteStartObject("assignments");
                foreach (KeyValuePair<Guid, IReadOnlySet<string>> kv in data.Assignments)
                {
                    if (kv.Value.Count == 0)
                    {
                        continue;
                    }
                    writer.WritePropertyName(kv.Key.ToString());
                    writer.WriteStartArray();
                    foreach (string tag in kv.Value)
                    {
                        writer.WriteStringValue(tag);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();

                // R54 v1.2 (schema v2): tag name → emoji icon. Only written when
                // at least one tag has an icon, so v1-style installs (no icons)
                // stay byte-identical except for the bumped schemaVersion.
                if (data.TagIcons.Count > 0)
                {
                    writer.WriteStartObject("tagIcons");
                    foreach (KeyValuePair<string, string> kv in data.TagIcons)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        {
                            writer.WriteString(kv.Key, kv.Value);
                        }
                    }
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.Flush();
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入剪贴板标签文件。", exception);
        }
    }

    // ── Pure mutating helpers (return new data; caller persists) ──

    /// <summary>Adds a custom tag name. No-op (returns input) if the name
    /// already exists (case-sensitive) or is blank.</summary>
    public static ClipboardTagData AddCustomTag(ClipboardTagData data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(name))
        {
            return data;
        }
        string trimmed = name.Trim();
        if (data.CustomTags.Contains(trimmed, StringComparer.Ordinal))
        {
            return data;
        }
        return data with { CustomTags = [..data.CustomTags, trimmed] };
    }

    /// <summary>Renames a custom tag and updates all assignments that referenced
    /// the old name. No-op if <paramref name="oldName"/> isn't a custom tag.</summary>
    public static ClipboardTagData RenameCustomTag(ClipboardTagData data, string oldName, string newName)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
        {
            return data;
        }
        string oldTrim = oldName.Trim();
        string newTrim = newName.Trim();
        if (oldTrim == newTrim)
        {
            return data;
        }
        if (!data.CustomTags.Contains(oldTrim, StringComparer.Ordinal))
        {
            return data;
        }

        var tags = data.CustomTags.Select(t => t == oldTrim ? newTrim : t).ToList();
        var assignments = Reassign(data.Assignments, oldTrim, newTrim);
        var icons = RenameIconKey(data.TagIcons, oldTrim, newTrim);
        return data with { CustomTags = tags, Assignments = assignments, TagIcons = icons };
    }

    /// <summary>Deletes a custom tag and removes it from all assignments. Entry
    /// records themselves are untouched (they live in the history file). The
    /// favorite tag and built-in tabs are never deletable here.</summary>
    public static ClipboardTagData DeleteCustomTag(ClipboardTagData data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(name))
        {
            return data;
        }
        string trim = name.Trim();
        if (!data.CustomTags.Contains(trim, StringComparer.Ordinal))
        {
            return data;
        }

        var tags = data.CustomTags.Where(t => t != trim).ToList();
        var assignments = RemoveFromAll(data.Assignments, trim);
        var icons = data.TagIcons.Where(kv => kv.Key != trim)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return data with { CustomTags = tags, Assignments = assignments, TagIcons = icons };
    }

    // ── R54 v1.2: icon + reorder helpers (pure; caller persists) ──

    /// <summary>Sets (or clears, when <paramref name="emoji"/> is blank) the
    /// emoji icon for <paramref name="tagName"/>. No-op if the name isn't a
    /// custom tag.</summary>
    public static ClipboardTagData SetTagIcon(ClipboardTagData data, string tagName, string emoji)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return data;
        }
        string trim = tagName.Trim();
        if (!data.CustomTags.Contains(trim, StringComparer.Ordinal))
        {
            return data;
        }

        var icons = new Dictionary<string, string>(data.TagIcons, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(emoji))
        {
            icons.Remove(trim);
        }
        else
        {
            icons[trim] = emoji.Trim();
        }
        return data with { TagIcons = icons };
    }

    /// <summary>Moves <paramref name="tagName"/> from its current position to
    /// <paramref name="toIndex"/> in <see cref="ClipboardTagData.CustomTags"/>. No-op
    /// when the name isn't a custom tag or the index is out of range. Display
    /// order = list position, so this is the single source of truth for
    /// drag-to-reorder.</summary>
    public static ClipboardTagData ReorderTag(ClipboardTagData data, string tagName, int toIndex)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return data;
        }
        string trim = tagName.Trim();
        int fromIndex = -1;
        for (int i = 0; i < data.CustomTags.Count; i++)
        {
            if (string.Equals(data.CustomTags[i], trim, StringComparison.Ordinal))
            {
                fromIndex = i;
                break;
            }
        }
        if (fromIndex < 0)
        {
            return data;
        }
        int clamped = Math.Clamp(toIndex, 0, data.CustomTags.Count - 1);
        if (clamped == fromIndex)
        {
            return data;
        }

        var tags = data.CustomTags.ToList();
        tags.RemoveAt(fromIndex);
        tags.Insert(clamped, trim);
        return data with { CustomTags = tags };
    }

    private static Dictionary<string, string> RenameIconKey(
        IReadOnlyDictionary<string, string> icons, string oldName, string newName)
    {
        var result = new Dictionary<string, string>(icons.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in icons)
        {
            result[kv.Key == oldName ? newName : kv.Key] = kv.Value;
        }
        return result;
    }

    /// <summary>Assigns <paramref name="tagName"/> to <paramref name="entryId"/>.
    /// Idempotent. Used for both custom tags and the favorite tag.</summary>
    public static ClipboardTagData Assign(ClipboardTagData data, Guid entryId, string tagName)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return data;
        }
        string trim = tagName.Trim();

        var assignments = new Dictionary<Guid, IReadOnlySet<string>>(data.Assignments.Count);
        foreach (KeyValuePair<Guid, IReadOnlySet<string>> kv in data.Assignments)
        {
            assignments[kv.Key] = kv.Value;
        }

        if (assignments.TryGetValue(entryId, out IReadOnlySet<string>? existing))
        {
            if (existing.Contains(trim))
            {
                return data; // already assigned
            }
            var set = new HashSet<string>(existing, StringComparer.Ordinal) { trim };
            assignments[entryId] = set;
        }
        else
        {
            assignments[entryId] = new HashSet<string>(StringComparer.Ordinal) { trim };
        }

        return data with { Assignments = assignments };
    }

    /// <summary>Removes <paramref name="tagName"/> from <paramref name="entryId"/>.
    /// Idempotent. When the entry ends up with no tags, its key is dropped.</summary>
    public static ClipboardTagData Unassign(ClipboardTagData data, Guid entryId, string tagName)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return data;
        }
        string trim = tagName.Trim();
        if (!data.Assignments.TryGetValue(entryId, out IReadOnlySet<string>? existing) ||
            !existing.Contains(trim))
        {
            return data;
        }

        var assignments = new Dictionary<Guid, IReadOnlySet<string>>(data.Assignments.Count);
        foreach (KeyValuePair<Guid, IReadOnlySet<string>> kv in data.Assignments)
        {
            if (kv.Key == entryId)
            {
                var set = new HashSet<string>(existing, StringComparer.Ordinal);
                set.Remove(trim);
                if (set.Count > 0)
                {
                    assignments[entryId] = set;
                }
                // else: drop the key (entry has no tags left)
            }
            else
            {
                assignments[kv.Key] = kv.Value;
            }
        }

        return data with { Assignments = assignments };
    }

    private static Dictionary<Guid, IReadOnlySet<string>> Reassign(
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> assignments,
        string oldName, string newName)
    {
        var result = new Dictionary<Guid, IReadOnlySet<string>>(assignments.Count);
        foreach (KeyValuePair<Guid, IReadOnlySet<string>> kv in assignments)
        {
            if (kv.Value.Contains(oldName))
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (string t in kv.Value)
                {
                    set.Add(t == oldName ? newName : t);
                }
                result[kv.Key] = set;
            }
            else
            {
                result[kv.Key] = kv.Value;
            }
        }
        return result;
    }

    private static Dictionary<Guid, IReadOnlySet<string>> RemoveFromAll(
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> assignments,
        string tagName)
    {
        var result = new Dictionary<Guid, IReadOnlySet<string>>(assignments.Count);
        foreach (KeyValuePair<Guid, IReadOnlySet<string>> kv in assignments)
        {
            if (!kv.Value.Contains(tagName))
            {
                result[kv.Key] = kv.Value;
                continue;
            }
            var set = new HashSet<string>(kv.Value, StringComparer.Ordinal);
            set.Remove(tagName);
            if (set.Count > 0)
            {
                result[kv.Key] = set;
            }
        }
        return result;
    }
}
