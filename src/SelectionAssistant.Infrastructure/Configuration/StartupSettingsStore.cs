using System.Text.Json;
using SelectionAssistant.Core.Startup;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// AOT-safe, atomic persistence for <see cref="StartupSettings"/> (开机自启开关)。
/// Mirrors <see cref="UiLanguageStore"/>: schema-versioned JSON, 8 KB cap,
/// atomic write via .tmp + File.Move, single field with fallback to default.
/// <para>
/// <b>文件是「意图」,注册表才是「真相」。</b>本文件存的是用户上一次在设置里
/// 选的状态;开机是否真拉起取决于 <c>HKCU\…\CurrentVersion\Run</c>。App 加载
/// 时会用 <c>IAutoStartManager.IsEnabled()</c> 校准,以注册表为准回写本文件。
/// </para>
/// </summary>
public static class StartupSettingsStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 8 * 1024;

    /// <summary>
    /// 加载已保存的开机自启偏好;文件缺失返回 <see cref="StartupSettings.Default"/>。
    /// 文件格式错误抛 <see cref="ProviderConfigurationException"/>,调用方可回退
    /// 默认值并记录警告。
    /// </summary>
    public static StartupSettings LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return StartupSettings.Default;
        }

        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
            {
                throw new ProviderConfigurationException("开机自启配置文件超过 8 KB 上限。");
            }
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                throw new ProviderConfigurationException("不支持的开机自启配置 schemaVersion。");
            }

            StartupSettings defaults = StartupSettings.Default;
            var settings = new StartupSettings
            {
                LaunchAtStartup = ReadBoolean(root, "launchAtStartup", defaults.LaunchAtStartup),
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
            throw new ProviderConfigurationException("无法读取开机自启配置文件。", exception);
        }
    }

    /// <summary>
    /// 持久化开机自启偏好。写 <c>.tmp</c> 再 <c>File.Move(overwrite:true)</c>,
    /// 写入过程中崩溃不会留下截断的 <c>startup-options.json</c>——旧版本(若有)
    /// 在新字节完全落盘前保持不变。
    /// </summary>
    public static void Save(StartupSettings settings, string path)
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
                writer.WriteBoolean("launchAtStartup", settings.LaunchAtStartup);
                writer.WriteEndObject();
                writer.Flush();
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入开机自启配置文件。", exception);
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
}
