using System.Text.Json;
using SelectionAssistant.Core.Translation;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// Loads and saves the global <c>prompt-templates.json</c> — the ordered set of
/// user-editable system prompts for the built-in actions (translate/summarize/
/// explain) plus any user-added custom actions. All providers share this single
/// file. A missing or unreadable file yields the built-in defaults (no crash).
/// Writes are atomic (temp file + Move).
/// <para>
/// 架构与数据流详见 <c>docs/architecture/04-prompt-templates.md</c>。
/// </para>
/// </summary>
public static class PromptTemplatesStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 64 * 1024;

    /// <summary>
    /// Loads templates from the given path. Returns the built-in defaults if
    /// the file is missing. Re-throws parse/IO errors wrapped in
    /// <see cref="ProviderConfigurationException" /> so callers handle one type.
    /// </summary>
    public static PromptTemplateSet LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return PromptTemplateDefaults.CreateDefault();
        }

        var info = new FileInfo(path);
        if (info.Length > MaximumFileBytes)
        {
            throw new ProviderConfigurationException("提示词模板文件超过 64 KB 上限。");
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
                throw new ProviderConfigurationException("不支持的 prompt-templates schemaVersion。");
            }

            var entries = new List<PromptTemplate>();
            if (root.TryGetProperty("templates", out JsonElement templatesElement) &&
                templatesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in templatesElement.EnumerateArray())
                {
                    PromptTemplate? parsed = ParseEntry(entry);
                    if (parsed is not null)
                    {
                        entries.Add(parsed);
                    }
                }
            }

            // FromList merges the loaded entries over the built-in defaults:
            // built-in ids override the default values, custom ids are appended.
            return PromptTemplateSet.FromList(entries);
        }
        catch (ProviderConfigurationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProviderConfigurationException("提示词模板文件不是有效 JSON。", exception);
        }
        catch (IOException exception)
        {
            throw new ProviderConfigurationException("无法读取提示词模板文件。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ProviderConfigurationException("没有权限读取提示词模板文件。", exception);
        }
    }

    /// <summary>
    /// Atomically writes the template set to disk. Omits a built-in entry when
    /// its prompt equals the built-in default AND thinking is off (keeps the
    /// file minimal and lets future built-in improvements propagate). Always
    /// writes the translate entry, even when empty, so "use default translation"
    /// is explicit. Custom actions are always written (they have no default).
    /// </summary>
    public static void Save(PromptTemplateSet set, string path)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var defaults = PromptTemplateDefaults.CreateDefault();
        string tempPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
                writer.WriteStartArray("templates");

                foreach (PromptTemplate current in set.AsList())
                {
                    bool isBuiltIn = PromptActionIds.IsBuiltIn(current.Id);
                    bool alwaysWrite = current.Id == PromptActionIds.Translate;
                    PromptTemplate? @default = defaults.Find(current.Id);
                    WriteEntry(writer, current, @default, isBuiltIn, alwaysWrite);
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
            throw new ProviderConfigurationException("无法写入提示词模板文件。", exception);
        }
    }

    /// <summary>
    /// Writes one entry. For built-in actions, skip when prompt+thinking+shortcut
    /// all equal the default (unless alwaysWrite). Custom actions are always
    /// written. Shortcut is persisted only when non-default, so legacy files
    /// without the field stay minimal and inherit the built-in default on load.
    /// </summary>
    private static void WriteEntry(
        Utf8JsonWriter writer, PromptTemplate current, PromptTemplate? @default,
        bool isBuiltIn, bool alwaysWrite)
    {
        bool shortcutIsDefault = @default is not null &&
            string.Equals(current.Shortcut, @default.Shortcut, StringComparison.OrdinalIgnoreCase);
        if (isBuiltIn && !alwaysWrite && @default is not null &&
            current.Prompt == @default.Prompt && !current.ThinkingEnabled && shortcutIsDefault)
        {
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("id", current.Id);
        writer.WriteString("name", current.Name);
        writer.WriteString("prompt", current.Prompt);
        // Persist thinkingEnabled only when non-default (true), so legacy
        // files without the key load as false and the file stays minimal.
        if (current.ThinkingEnabled)
        {
            writer.WriteBoolean("thinkingEnabled", true);
        }
        // Persist shortcut only when set and not the built-in default (so the
        // common case keeps the file minimal). Null/empty means "no shortcut".
        if (!string.IsNullOrWhiteSpace(current.Shortcut))
        {
            writer.WriteString("shortcut", current.Shortcut);
        }
        writer.WriteEndObject();
    }

    private static PromptTemplate? ParseEntry(JsonElement element)
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

        string name = element.TryGetProperty("name", out JsonElement nameElement) &&
            nameElement.ValueKind == JsonValueKind.String
                ? (nameElement.GetString() ?? id) : id;

        string prompt = element.TryGetProperty("prompt", out JsonElement promptElement) &&
            promptElement.ValueKind == JsonValueKind.String
                ? (promptElement.GetString() ?? string.Empty) : string.Empty;

        // Optional thinking flag (absent in legacy files → false).
        bool thinkingEnabled = element.TryGetProperty("thinkingEnabled", out JsonElement thinkElement) &&
            thinkElement.ValueKind == JsonValueKind.True;

        // Optional single-character toolbar shortcut (absent in legacy files
        // → null; built-ins get F/J/Z merged back in by PromptTemplateSet.FromList).
        string? shortcut = element.TryGetProperty("shortcut", out JsonElement shortcutElement) &&
            shortcutElement.ValueKind == JsonValueKind.String
                ? shortcutElement.GetString() : null;

        return new PromptTemplate(id, name, prompt, thinkingEnabled, shortcut);
    }
}
