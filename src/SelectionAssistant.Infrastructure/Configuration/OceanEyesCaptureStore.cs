using System.Text.Json;
using SelectionAssistant.Core.Capture;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// R40: AOT-safe, atomic persistence for <see cref="OceanEyesCaptureSettings"/>.
/// Mirrors <see cref="ToolbarShortcutsStore"/>: schema-versioned JSON, 8 KB cap,
/// atomic write via .tmp + <see cref="File.Move"/>, all fields optional with
/// fallback to defaults (so missing fields / first launch = defaults, no
/// schema bump).
/// </summary>
public static class OceanEyesCaptureStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 8 * 1024;

    public static OceanEyesCaptureSettings LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return OceanEyesCaptureSettings.Default;
        }

        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
            {
                throw new ProviderConfigurationException("Ocean Eyes 截图配置文件超过 8 KB 上限。");
            }
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                throw new ProviderConfigurationException("不支持的 Ocean Eyes 截图配置 schemaVersion。");
            }

            OceanEyesCaptureSettings defaults = OceanEyesCaptureSettings.Default;
            var settings = new OceanEyesCaptureSettings
            {
                SavePath = ReadString(root, "savePath", defaults.SavePath),
                AutoSaveEnabled = ReadBoolean(root, "autoSaveEnabled", defaults.AutoSaveEnabled),
                CopyToClipboardEnabled = ReadBoolean(root, "copyToClipboardEnabled", defaults.CopyToClipboardEnabled),
                UiaAssistEnabled = ReadBoolean(root, "uiaAssistEnabled", defaults.UiaAssistEnabled),

                // R51 beautify (optional — older v1 files fall back to defaults).
                BeautifyPadding = ReadInt32(root, "beautifyPadding", defaults.BeautifyPadding),
                BeautifyCornerRadius = ReadInt32(root, "beautifyCornerRadius", defaults.BeautifyCornerRadius),
                BeautifyBackgroundHex = ReadString(root, "beautifyBackgroundHex", defaults.BeautifyBackgroundHex),
                BeautifyShadowOffsetX = ReadInt32(root, "beautifyShadowOffsetX", defaults.BeautifyShadowOffsetX),
                BeautifyShadowOffsetY = ReadInt32(root, "beautifyShadowOffsetY", defaults.BeautifyShadowOffsetY),
                BeautifyShadowBlurRadius = ReadInt32(root, "beautifyShadowBlurRadius", defaults.BeautifyShadowBlurRadius),
                BeautifyShadowOpacity = ReadDouble(root, "beautifyShadowOpacity", defaults.BeautifyShadowOpacity),
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
            throw new ProviderConfigurationException("无法读取 Ocean Eyes 截图配置文件。", exception);
        }
    }

    public static void Save(OceanEyesCaptureSettings settings, string path)
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
                writer.WriteString("savePath", settings.SavePath);
                writer.WriteBoolean("autoSaveEnabled", settings.AutoSaveEnabled);
                writer.WriteBoolean("copyToClipboardEnabled", settings.CopyToClipboardEnabled);
                writer.WriteBoolean("uiaAssistEnabled", settings.UiaAssistEnabled);
                // R51 beautify (always written; readers tolerate their absence).
                writer.WriteNumber("beautifyPadding", settings.BeautifyPadding);
                writer.WriteNumber("beautifyCornerRadius", settings.BeautifyCornerRadius);
                writer.WriteString("beautifyBackgroundHex", settings.BeautifyBackgroundHex);
                writer.WriteNumber("beautifyShadowOffsetX", settings.BeautifyShadowOffsetX);
                writer.WriteNumber("beautifyShadowOffsetY", settings.BeautifyShadowOffsetY);
                writer.WriteNumber("beautifyShadowBlurRadius", settings.BeautifyShadowBlurRadius);
                writer.WriteNumber("beautifyShadowOpacity", settings.BeautifyShadowOpacity);
                writer.WriteEndObject();
                writer.Flush();
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入 Ocean Eyes 截图配置文件。", exception);
        }
    }

    private static bool ReadBoolean(JsonElement root, string name, bool defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return defaultValue;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ProviderConfigurationException($"{name} 必须是布尔值。"),
        };
    }

    private static string ReadString(JsonElement root, string name, string defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return defaultValue;
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ProviderConfigurationException($"{name} 必须是字符串。");
        }
        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? defaultValue : text;
    }

    private static int ReadInt32(JsonElement root, string name, int defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return defaultValue;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int i))
        {
            return i;
        }
        throw new ProviderConfigurationException($"{name} 必须是整数。");
    }

    private static double ReadDouble(JsonElement root, string name, double defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return defaultValue;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d))
        {
            return d;
        }
        throw new ProviderConfigurationException($"{name} 必须是数字。");
    }
}
