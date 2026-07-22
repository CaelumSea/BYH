using System.Text.Json;
using SelectionAssistant.Core.Input;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// AOT-safe, atomic persistence for the R54 clipboard-history popup trigger
/// hotkey. Mirrors <see cref="SpotlightTriggerStore"/> exactly (keyboard-only,
/// no mouse chord). Default Ctrl+Alt+V.
/// </summary>
public static class ClipboardHistoryTriggerStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 8 * 1024;

    public static ClipboardHistoryTriggerSettings LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return ClipboardHistoryTriggerSettings.Default;
        }

        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
            {
                throw new ProviderConfigurationException("剪贴板历史快捷键配置文件超过 8 KB 上限。");
            }
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                throw new ProviderConfigurationException("不支持的剪贴板历史快捷键配置 schemaVersion。");
            }

            ClipboardHistoryTriggerSettings defaults = ClipboardHistoryTriggerSettings.Default;
            var settings = new ClipboardHistoryTriggerSettings
            {
                KeyboardShortcutEnabled = ReadBoolean(
                    root, "keyboardShortcutEnabled", defaults.KeyboardShortcutEnabled),
                Modifiers = ReadModifiers(root, defaults.Modifiers),
                Key = ReadString(root, "key", defaults.Key),
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
            throw new ProviderConfigurationException("无法读取剪贴板历史快捷键配置文件。", exception);
        }
    }

    public static void Save(ClipboardHistoryTriggerSettings settings, string path)
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
                writer.WriteBoolean("keyboardShortcutEnabled", settings.KeyboardShortcutEnabled);
                writer.WriteStartArray("modifiers");
                WriteModifier(writer, settings.Modifiers, GlobalHotKeyModifiers.Control, "Control");
                WriteModifier(writer, settings.Modifiers, GlobalHotKeyModifiers.Alt, "Alt");
                WriteModifier(writer, settings.Modifiers, GlobalHotKeyModifiers.Shift, "Shift");
                WriteModifier(writer, settings.Modifiers, GlobalHotKeyModifiers.Windows, "Windows");
                writer.WriteEndArray();
                writer.WriteString("key", settings.Key);
                writer.WriteEndObject();
                writer.Flush();
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入剪贴板历史快捷键配置文件。", exception);
        }
    }

    private static void WriteModifier(
        Utf8JsonWriter writer,
        GlobalHotKeyModifiers value,
        GlobalHotKeyModifiers candidate,
        string name)
    {
        if (value.HasFlag(candidate)) writer.WriteStringValue(name);
    }

    private static GlobalHotKeyModifiers ReadModifiers(
        JsonElement root,
        GlobalHotKeyModifiers defaultValue)
    {
        if (!root.TryGetProperty("modifiers", out JsonElement value))
        {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ProviderConfigurationException("modifiers 必须是数组。");
        }

        GlobalHotKeyModifiers result = GlobalHotKeyModifiers.None;
        foreach (JsonElement item in value.EnumerateArray())
        {
            string? name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            result |= name?.ToLowerInvariant() switch
            {
                "control" => GlobalHotKeyModifiers.Control,
                "alt" => GlobalHotKeyModifiers.Alt,
                "shift" => GlobalHotKeyModifiers.Shift,
                "windows" => GlobalHotKeyModifiers.Windows,
                _ => throw new ProviderConfigurationException("modifiers 包含未知值。"),
            };
        }

        return result;
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
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(text)
            ? throw new ProviderConfigurationException($"{name} 必须是非空字符串。")
            : text;
    }
}
