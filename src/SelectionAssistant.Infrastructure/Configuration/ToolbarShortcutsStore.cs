using System.Text.Json;
using SelectionAssistant.Core.Input;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// AOT-safe, atomic persistence for the three user-configurable toolbar
/// shortcut keys (Prompt/Copy/Paste). Mirrors <see cref="OceanEyesTriggerStore"/>:
/// schema-versioned JSON, 8 KB cap, atomic write via .tmp + File.Move, all
/// fields optional with fallback to <see cref="ToolbarShortcutSettings.Default"/>
/// (so legacy/v1 readers with absent fields get the R/C/V defaults).
/// </summary>
public static class ToolbarShortcutsStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 8 * 1024;

    public static ToolbarShortcutSettings LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return ToolbarShortcutSettings.Default;
        }

        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
            {
                throw new ProviderConfigurationException("工具栏快捷键配置文件超过 8 KB 上限。");
            }
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                throw new ProviderConfigurationException("不支持的工具栏快捷键配置 schemaVersion。");
            }

            ToolbarShortcutSettings defaults = ToolbarShortcutSettings.Default;
            // R41: pasteKey is no longer a field — it's ignored on read so old
            // files (R37-R40 era) still load cleanly. Never written back.
            var settings = new ToolbarShortcutSettings
            {
                PromptKey = ReadOptionalString(root, "promptKey", defaults.PromptKey),
                CopyKey = ReadOptionalString(root, "copyKey", defaults.CopyKey),
            }.Normalize();
            settings.Validate();
            return settings;
        }
        catch (ProviderConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ProviderConfigurationException("无法读取工具栏快捷键配置文件。", exception);
        }
    }

    public static void Save(ToolbarShortcutSettings settings, string path)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        settings = settings.Normalize();
        settings.Validate();

        string tempPath = path + ".tmp";
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
                // Null keys (= disabled) are written as JSON null so a round-trip
                // preserves "user cleared this field" rather than snapping back to default.
                WriteOptionalString(writer, "promptKey", settings.PromptKey);
                WriteOptionalString(writer, "copyKey", settings.CopyKey);
                writer.WriteEndObject();
                writer.Flush();
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入工具栏快捷键配置文件。", exception);
        }
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>
    /// Reads an optional string field. Accepts JSON null (returns null), missing
    /// field (returns <paramref name="defaultValue"/>), or a string value. A
    /// non-string, non-null value is a schema violation.
    /// </summary>
    private static string? ReadOptionalString(JsonElement root, string name, string? defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return defaultValue;
        }
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }
}
