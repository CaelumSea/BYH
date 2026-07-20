using System.Text.Json;
using SelectionAssistant.Core.Capture;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// R24 track B: loads and saves <c>vision.json</c> — the screenshot-OCR tier
/// settings (enabled flag, provider id, model, OCR prompt). Missing or unreadable
/// file yields the built-in defaults (no crash). Writes are atomic (temp + Move),
/// hand-written via <see cref="Utf8JsonWriter"/> (NativeAOT-safe, no reflection).
/// </summary>
public static class VisionCaptureStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 16 * 1024;

    public static VisionCaptureSettings LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return VisionCaptureSettings.Default;
        }

        var info = new FileInfo(path);
        if (info.Length > MaximumFileBytes)
        {
            throw new ProviderConfigurationException("视觉识别配置文件超过 16 KB 上限。");
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
                throw new ProviderConfigurationException("不支持的 vision schemaVersion。");
            }

            VisionCaptureSettings defaults = VisionCaptureSettings.Default;
            return new VisionCaptureSettings
            {
                Enabled = ReadBoolean(root, "enabled", defaults.Enabled),
                ProviderId = ReadString(root, "providerId", defaults.ProviderId),
                Model = ReadString(root, "model", defaults.Model),
                OcrPrompt = ReadString(root, "ocrPrompt", defaults.OcrPrompt),
                UiaPrefillEnabled = ReadBoolean(root, "uiaPrefillEnabled", defaults.UiaPrefillEnabled),
                DisableThinking = ReadBoolean(root, "disableThinking", defaults.DisableThinking),
            };
        }
        catch (ProviderConfigurationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProviderConfigurationException("视觉识别配置文件不是有效 JSON。", exception);
        }
        catch (IOException exception)
        {
            throw new ProviderConfigurationException("无法读取视觉识别配置文件。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ProviderConfigurationException("没有权限读取视觉识别配置文件。", exception);
        }
    }

    /// <summary>Atomically writes the settings to disk.</summary>
    public static void Save(VisionCaptureSettings settings, string path)
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
                writer.WriteString("providerId", settings.ProviderId);
                writer.WriteString("model", settings.Model);
                writer.WriteString("ocrPrompt", settings.OcrPrompt);
                writer.WriteBoolean("uiaPrefillEnabled", settings.UiaPrefillEnabled);
                writer.WriteBoolean("disableThinking", settings.DisableThinking);
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
            throw new ProviderConfigurationException("无法写入视觉识别配置文件。", exception);
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
