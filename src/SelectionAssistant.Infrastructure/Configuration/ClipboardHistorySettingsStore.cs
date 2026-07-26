using System.Text.Json;
using SelectionAssistant.Core.Clipboard;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// AOT-safe, atomic persistence for <see cref="ClipboardHistorySettings"/>
/// (the R54 feature toggles — separate from the popup trigger hotkey, which is
/// <see cref="ClipboardHistoryTriggerStore"/>). Missing file = defaults.
/// </summary>
public static class ClipboardHistorySettingsStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 8 * 1024;

    public static ClipboardHistorySettings LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return ClipboardHistorySettings.Default;
        }

        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
            {
                throw new ProviderConfigurationException("剪贴板历史配置文件超过 8 KB 上限。");
            }
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                throw new ProviderConfigurationException("不支持的剪贴板历史配置 schemaVersion。");
            }

            ClipboardHistorySettings defaults = ClipboardHistorySettings.Default;
            var settings = new ClipboardHistorySettings
            {
                Enabled = ReadBoolean(root, "enabled", defaults.Enabled),
                AutoPasteEnabled = ReadBoolean(root, "autoPasteEnabled", defaults.AutoPasteEnabled),
                MaxEntries = ReadInt(root, "maxEntries", defaults.MaxEntries),
                ExcludeProcessNames = ReadStringArray(root, "excludeProcessNames", defaults.ExcludeProcessNames),
                MaskSensitiveEnabled = ReadBoolean(root, "maskSensitiveEnabled", defaults.MaskSensitiveEnabled),
                WindowWidth = ReadInt(root, "windowWidth", defaults.WindowWidth),
                WindowHeight = ReadInt(root, "windowHeight", defaults.WindowHeight),
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
            throw new ProviderConfigurationException("无法读取剪贴板历史配置文件。", exception);
        }
    }

    public static void Save(ClipboardHistorySettings settings, string path)
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
                writer.WriteBoolean("enabled", settings.Enabled);
                writer.WriteBoolean("autoPasteEnabled", settings.AutoPasteEnabled);
                writer.WriteNumber("maxEntries", settings.MaxEntries);
                writer.WriteStartArray("excludeProcessNames");
                foreach (string name in settings.ExcludeProcessNames)
                {
                    writer.WriteStringValue(name);
                }
                writer.WriteEndArray();
                writer.WriteBoolean("maskSensitiveEnabled", settings.MaskSensitiveEnabled);
                writer.WriteNumber("windowWidth", settings.WindowWidth);
                writer.WriteNumber("windowHeight", settings.WindowHeight);
                writer.WriteEndObject();
                writer.Flush();
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入剪贴板历史配置文件。", exception);
        }
    }

    private static bool ReadBoolean(JsonElement root, string name, bool defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return defaultValue;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    private static int ReadInt(JsonElement root, string name, int defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return defaultValue;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : defaultValue;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name, IReadOnlyList<string> defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return defaultValue;
        }

        var result = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    result.Add(s.Trim());
                }
            }
        }
        return result;
    }
}
