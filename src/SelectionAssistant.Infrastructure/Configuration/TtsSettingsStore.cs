using System.Text.Json;
using SelectionAssistant.Core.Speech;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// Loads and saves <c>tts.json</c> — the 朗读 (text-to-speech) settings. Missing
/// or unreadable file yields <see cref="TtsSettings.Default"/> (no crash). Writes
/// are atomic (temp + Move), hand-written via <see cref="Utf8JsonWriter"/>
/// (NativeAOT-safe, no reflection). Mirrors <see cref="VisionCaptureStore"/>.
/// </summary>
public static class TtsSettingsStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 16 * 1024;

    public static TtsSettings LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return TtsSettings.Default;
        }

        var info = new FileInfo(path);
        if (info.Length > MaximumFileBytes)
        {
            throw new ProviderConfigurationException("朗读配置文件超过 16 KB 上限。");
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
                throw new ProviderConfigurationException("不支持的 tts schemaVersion。");
            }

            TtsSettings defaults = TtsSettings.Default;
            // ApiKeyReference has three states we must distinguish:
            //   • field present + non-empty → that reference (DPAPI secret lookup)
            //   • field present but null/empty → explicitly empty (forces the
            //     ~/.mmx/config.json fallback; the user opted out of a BYH key)
            //   • field absent → file was written by an older build; use default.
            string? apiKeyReference = root.TryGetProperty("apiKeyReference", out JsonElement apiKeyElem)
                ? (apiKeyElem.ValueKind == JsonValueKind.String
                    ? (string.IsNullOrWhiteSpace(apiKeyElem.GetString()) ? null : apiKeyElem.GetString())
                    : null) // explicit JSON null
                : defaults.ApiKeyReference;

            return new TtsSettings
            {
                Enabled = ReadBoolean(root, "enabled", defaults.Enabled),
                ApiKeyReference = apiKeyReference,
                Region = ReadString(root, "region", defaults.Region),
                Model = ReadString(root, "model", defaults.Model),
                EnglishVoice = ReadString(root, "englishVoice", defaults.EnglishVoice),
                ChineseVoice = ReadString(root, "chineseVoice", defaults.ChineseVoice),
                MixedVoice = ReadString(root, "mixedVoice", defaults.MixedVoice),
                Speed = ReadDouble(root, "speed", defaults.Speed),
                MaxCharacters = ReadInt(root, "maxCharacters", defaults.MaxCharacters),
            };
        }
        catch (ProviderConfigurationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProviderConfigurationException("朗读配置文件不是有效 JSON。", exception);
        }
        catch (IOException exception)
        {
            throw new ProviderConfigurationException("无法读取朗读配置文件。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ProviderConfigurationException("没有权限读取朗读配置文件。", exception);
        }
    }

    /// <summary>Atomically writes the settings to disk.</summary>
    public static void Save(TtsSettings settings, string path)
    {
        ArgumentNullException.ThrowIfNull(settings);
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
                writer.WriteBoolean("enabled", settings.Enabled);
                // Always write apiKeyReference, even when null: an explicit JSON
                // null means "force the mmx-config fallback" (distinct from the
                // field being absent, which means "use the default reference").
                writer.WritePropertyName("apiKeyReference");
                if (settings.ApiKeyReference is not null)
                {
                    writer.WriteStringValue(settings.ApiKeyReference);
                }
                else
                {
                    writer.WriteNullValue();
                }
                writer.WriteString("region", settings.Region);
                writer.WriteString("model", settings.Model);
                writer.WriteString("englishVoice", settings.EnglishVoice);
                writer.WriteString("chineseVoice", settings.ChineseVoice);
                writer.WriteString("mixedVoice", settings.MixedVoice);
                writer.WriteNumber("speed", settings.Speed);
                writer.WriteNumber("maxCharacters", settings.MaxCharacters);
                writer.WriteEndObject();
                writer.Flush();
            }

            // Atomic replace (single API on Windows). Avoids the gap left by
            // Delete-then-Move where a crash between the two leaves NO file.
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入朗读配置文件。", exception);
        }
    }

    private static bool ReadBoolean(JsonElement root, string name, bool defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    private static double ReadDouble(JsonElement root, string name, double defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double num))
        {
            return defaultValue;
        }
        return num;
    }

    private static int ReadInt(JsonElement root, string name, int defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int num))
        {
            return defaultValue;
        }
        return num;
    }

    private static string ReadString(JsonElement root, string name, string defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return defaultValue;
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? defaultValue : text;
    }
}
