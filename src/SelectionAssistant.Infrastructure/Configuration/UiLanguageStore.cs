using System.Text.Json;
using SelectionAssistant.Core.I18n;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// AOT-safe, atomic persistence for the UI language preference. Mirrors
/// <see cref="ToolbarShortcutsStore"/>: schema-versioned JSON, 8 KB cap,
/// atomic write via .tmp + File.Move, single optional field with fallback.
/// </summary>
/// <remarks>
/// <b>Difference from the other stores.</b> Most stores return a fixed
/// <c>Default</c> constant when the file is missing. The language store is
/// different: a missing file on first launch means "auto-detect from the
/// OS", so <see cref="LoadIfExists"/> returns
/// <see cref="AppLanguage.DetectFromOS"/> in that case rather than a fixed
/// language. A missing <c>language</c> field inside an existing file is
/// treated the same way (auto-detect), so a corrupt/partial file degrades
/// gracefully.
/// <para>
/// <b>Accepted values.</b> Only the two <see cref="AppLanguage.Supported"/>
/// codes are persisted: <c>"en"</c> and <c>"zh-CN"</c>. Any other value on
/// read (e.g. an old <c>"zh-Hans"</c> from a hand-edited file) is mapped
/// through <see cref="AppLanguage.FromCultureName"/> so it still resolves to
/// a supported language instead of throwing.
/// </para>
/// </remarks>
public static class UiLanguageStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 8 * 1024;

    /// <summary>
    /// Loads the saved language, or auto-detects from the OS if the file is
    /// missing, the <c>language</c> field is absent, or the field is blank.
    /// Throws <see cref="ProviderConfigurationException"/> on a malformed
    /// file (wrong schema version, bad JSON, oversize) so the caller can
    /// fall back to OS detection and log the issue.
    /// </summary>
    public static AppLanguage LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return AppLanguage.DetectFromOS();
        }

        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
            {
                throw new ProviderConfigurationException("UI 语言配置文件超过 8 KB 上限。");
            }
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                throw new ProviderConfigurationException("不支持的 UI 语言配置 schemaVersion。");
            }

            // Missing/null/blank field → auto-detect from OS (graceful
            // degradation for a partially-written or hand-edited file).
            if (!root.TryGetProperty("language", out JsonElement value) ||
                value.ValueKind == JsonValueKind.Null)
            {
                return AppLanguage.DetectFromOS();
            }
            string? raw = value.GetString();
            return string.IsNullOrWhiteSpace(raw)
                ? AppLanguage.DetectFromOS()
                : AppLanguage.FromCultureName(raw);
        }
        catch (ProviderConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ProviderConfigurationException("无法读取 UI 语言配置文件。", exception);
        }
    }

    /// <summary>
    /// Persists the given language. Writes a <c>.tmp</c> file then
    /// <c>File.Move(overwrite:true)</c> so a crash mid-write never leaves a
    /// truncated <c>ui-language.json</c> — the previous version (if any)
    /// stays intact until the new bytes are fully on disk.
    /// </summary>
    public static void Save(AppLanguage language, string path)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

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
                writer.WriteString("language", language.Code);
                writer.WriteEndObject();
                writer.Flush();
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入 UI 语言配置文件。", exception);
        }
    }
}
