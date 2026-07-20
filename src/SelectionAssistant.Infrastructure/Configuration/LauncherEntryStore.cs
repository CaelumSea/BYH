using System.Text.Json;
using SelectionAssistant.Core.Launcher;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// Loads and saves the user's <c>launcher-entries.json</c> — the ordered list
/// of quick-launch entries (local apps + web URLs). Unlike
/// <see cref="PromptTemplatesStore"/>, there are no built-in entries; every
/// entry is user-added. A missing file yields an empty set. Writes are atomic
/// (temp file + Move).
/// </summary>
public static class LauncherEntryStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 256 * 1024;

    /// <summary>
    /// Loads entries from the given path. Returns an empty set if the file is
    /// missing. Re-throws parse/IO errors wrapped in
    /// <see cref="ProviderConfigurationException" /> so callers handle one type.
    /// </summary>
    public static LauncherEntrySet LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return LauncherEntryDefaults.CreateDefault();
        }

        var info = new FileInfo(path);
        if (info.Length > MaximumFileBytes)
        {
            throw new ProviderConfigurationException("启动项文件超过 256 KB 上限。");
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                throw new ProviderConfigurationException("不支持的 launcher-entries schemaVersion。");
            }

            var entries = new List<LauncherEntry>();
            if (root.TryGetProperty("entries", out JsonElement entriesElement) &&
                entriesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in entriesElement.EnumerateArray())
                {
                    LauncherEntry? parsed = ParseEntry(entry);
                    if (parsed is not null)
                    {
                        entries.Add(parsed);
                    }
                }
            }

            return LauncherEntrySet.FromList(entries);
        }
        catch (ProviderConfigurationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProviderConfigurationException("启动项文件不是有效 JSON。", exception);
        }
        catch (IOException exception)
        {
            throw new ProviderConfigurationException("无法读取启动项文件。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ProviderConfigurationException("没有权限读取启动项文件。", exception);
        }
    }

    /// <summary>
    /// Atomically writes the full entry set to disk. Every entry is written
    /// (no built-in/default omission — there are no built-ins).
    /// </summary>
    public static void Save(LauncherEntrySet set, string path)
    {
        ArgumentNullException.ThrowIfNull(set);
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
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
                writer.WriteStartArray("entries");

                foreach (LauncherEntry entry in set.AsList())
                {
                    WriteEntry(writer, entry);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tempPath, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入启动项文件。", exception);
        }
    }

    private static void WriteEntry(Utf8JsonWriter writer, LauncherEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("id", entry.Id);
        writer.WriteString("name", entry.Name);
        // Persist kind as a string ("localApp" / "webUrl") for readability and
        // forward compatibility — future kinds can be added without breaking
        // older readers (they'd land on the default LocalApp).
        writer.WriteString("kind", entry.Kind == LauncherKind.WebUrl ? "webUrl" : "localApp");
        writer.WriteString("target", entry.Target);
        if (!string.IsNullOrEmpty(entry.Arguments))
        {
            writer.WriteString("arguments", entry.Arguments);
        }
        if (!string.IsNullOrEmpty(entry.WorkingDirectory))
        {
            writer.WriteString("workingDirectory", entry.WorkingDirectory);
        }
        if (!string.IsNullOrEmpty(entry.IconOverride))
        {
            writer.WriteString("iconOverride", entry.IconOverride);
        }
        writer.WriteEndObject();
    }

    private static LauncherEntry? ParseEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty("id", out JsonElement idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        string id = idElement.GetString() ?? string.Empty;
        if (!LauncherEntryIds.IsLauncher(id))
        {
            // Forward-compat: ignore unknown id prefixes gracefully.
            return null;
        }

        string name = element.TryGetProperty("name", out JsonElement nameElement) &&
            nameElement.ValueKind == JsonValueKind.String
                ? (nameElement.GetString() ?? id) : id;

        string target = element.TryGetProperty("target", out JsonElement targetElement) &&
            targetElement.ValueKind == JsonValueKind.String
                ? (targetElement.GetString() ?? string.Empty) : string.Empty;

        LauncherKind kind = LauncherKind.LocalApp;
        if (element.TryGetProperty("kind", out JsonElement kindElement) &&
            kindElement.ValueKind == JsonValueKind.String)
        {
            string kindStr = kindElement.GetString() ?? "localApp";
            if (string.Equals(kindStr, "webUrl", StringComparison.OrdinalIgnoreCase))
            {
                kind = LauncherKind.WebUrl;
            }
        }

        string arguments = element.TryGetProperty("arguments", out JsonElement argsElement) &&
            argsElement.ValueKind == JsonValueKind.String
                ? (argsElement.GetString() ?? string.Empty) : string.Empty;

        string workingDir = element.TryGetProperty("workingDirectory", out JsonElement workElement) &&
            workElement.ValueKind == JsonValueKind.String
                ? (workElement.GetString() ?? string.Empty) : string.Empty;

        string iconOverride = element.TryGetProperty("iconOverride", out JsonElement iconElement) &&
            iconElement.ValueKind == JsonValueKind.String
                ? (iconElement.GetString() ?? string.Empty) : string.Empty;

        return new LauncherEntry(
            Id: id,
            Name: name,
            Kind: kind,
            Target: target,
            Arguments: arguments,
            WorkingDirectory: workingDir,
            IconOverride: iconOverride);
    }
}
