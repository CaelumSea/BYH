using System.Text.Json;
using SelectionAssistant.Core.PowerMonitoring;
using SelectionAssistant.Infrastructure.PowerMonitoring;

namespace SelectionAssistant.Infrastructure.Configuration;

/// <summary>
/// 加载/保存 <c>power-monitoring.json</c> —— 功耗监控设置 + 能耗累计状态 + 告警状态。镜像
/// <see cref="TtsSettingsStore"/>：<c>static</c>、手写 <see cref="Utf8JsonWriter"/>
/// （NativeAOT 安全，无反射）、temp+<see cref="File.Move"/> 原子写、16 KB 上限。文件缺失 →
/// <see cref="PowerMonitorSettings.Default"/>（默认关闭）。
/// <para>
/// <b>schemaVersion 兼容性</b>：当前 schemaVersion = 2。读 schemaVersion = 1 的旧文件时，
/// 缺失的告警/历史字段（alertEnabled/cpuTempThresholdC/.../historyRetentionDays/alert*Triggered）
/// 一律走默认值分支（<c>ReadXxx(root, key, default)</c>），实现无缝迁移；写出永远写 2。
/// </para>
/// <para>
/// 累计 Wh / 今日 Wh / 上次采样时间戳也持久化在本文件里，让跨重启的能耗累计连续。告警状态
/// （<see cref="AlertEvaluator.AlertState"/>）同理持久化，防止"刚关时正超温，重启后又响一遍"。
/// </para>
/// </summary>
public static class PowerMonitorSettingsStore
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumFileBytes = 16 * 1024;

    /// <summary>
    /// 加载设置 + 能耗累计状态 + 告警状态。文件缺失返回默认设置 + 零累计 + 全未触发告警。
    /// 任何损坏抛 <see cref="ProviderConfigurationException"/>（与 TtsSettingsStore 一致），
    /// 调用方应 catch 后回退到 <c>Default with Enabled=false</c>。
    /// <para>
    /// schemaVersion 兼容：1 和 2 都接受。读 1 时告警/历史字段缺失 → 默认值。
    /// </para>
    /// </summary>
    public static (PowerMonitorSettings Settings, double WattHoursTotal, double WattHoursToday, DateOnly Today, DateTimeOffset LastSampleAt, AlertEvaluator.AlertState AlertState) LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return (PowerMonitorSettings.Default, 0, 0, DateOnly.FromDateTime(DateTime.Now), DateTimeOffset.MinValue, AlertEvaluator.AlertState.Default);
        }

        var info = new FileInfo(path);
        if (info.Length > MaximumFileBytes)
        {
            throw new ProviderConfigurationException("功耗监控配置文件超过 16 KB 上限。");
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                (schemaVersion != 1 && schemaVersion != CurrentSchemaVersion))
            {
                throw new ProviderConfigurationException("不支持的 power-monitoring schemaVersion。");
            }

            // schemaVersion == 1 的旧文件缺告警/历史字段，ReadXxx 的默认值分支天然兜底，
            // 无需显式分支。schemaVersion == 2 的文件所有字段都读出来。
            PowerMonitorSettings defaults = PowerMonitorSettings.Default;
            var settings = new PowerMonitorSettings
            {
                Enabled = ReadBoolean(root, "enabled", defaults.Enabled),
                Endpoint = ReadString(root, "endpoint", defaults.Endpoint),
                PollIntervalMs = ReadInt(root, "pollIntervalMs", defaults.PollIntervalMs),
                ShowInTray = ReadBoolean(root, "showInTray", defaults.ShowInTray),
                TrackEnergy = ReadBoolean(root, "trackEnergy", defaults.TrackEnergy),
                // 告警/历史字段（schemaVersion 1 旧文件缺这些 → 用默认值）。
                AlertEnabled = ReadBoolean(root, "alertEnabled", defaults.AlertEnabled),
                CpuTempThresholdC = ReadInt(root, "cpuTempThresholdC", defaults.CpuTempThresholdC),
                GpuTempThresholdC = ReadInt(root, "gpuTempThresholdC", defaults.GpuTempThresholdC),
                SsdTempThresholdC = ReadInt(root, "ssdTempThresholdC", defaults.SsdTempThresholdC),
                HistoryRetentionDays = ReadInt(root, "historyRetentionDays", defaults.HistoryRetentionDays),
            };

            double whTotal = ReadDouble(root, "wattHoursTotal", 0);
            double whToday = ReadDouble(root, "wattHoursToday", 0);
            DateOnly today = DateOnly.FromDateTime(DateTime.Now);
            if (root.TryGetProperty("today", out JsonElement todayEl) &&
                todayEl.ValueKind == JsonValueKind.String &&
                DateOnly.TryParse(todayEl.GetString(), out DateOnly parsed))
            {
                today = parsed;
            }
            DateTimeOffset lastSampleAt = DateTimeOffset.MinValue;
            if (root.TryGetProperty("lastSampleEpochMs", out JsonElement epochEl) &&
                epochEl.ValueKind == JsonValueKind.Number &&
                epochEl.TryGetInt64(out long epoch))
            {
                lastSampleAt = DateTimeOffset.FromUnixTimeMilliseconds(epoch);
            }

            // 告警状态（旧文件缺 → 全未触发 Default）。
            var alertState = new AlertEvaluator.AlertState(
                CpuTriggered: ReadBoolean(root, "alertCpuTriggered", false),
                GpuTriggered: ReadBoolean(root, "alertGpuTriggered", false),
                SsdTriggered: ReadBoolean(root, "alertSsdTriggered", false));

            return (settings, Math.Max(0, whTotal), Math.Max(0, whToday), today, lastSampleAt, alertState);
        }
        catch (ProviderConfigurationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProviderConfigurationException("功耗监控配置文件不是有效 JSON。", exception);
        }
        catch (IOException exception)
        {
            throw new ProviderConfigurationException("无法读取功耗监控配置文件。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ProviderConfigurationException("没有权限读取功耗监控配置文件。", exception);
        }
    }

    /// <summary>
    /// 原子写入设置 + 能耗累计状态 + 告警状态。调用方应在每次累计 Wh 变化后定期调用（约 60s 一次），
    /// 以及在应用退出时调用一次。永远写 schemaVersion = <see cref="CurrentSchemaVersion"/>（2）。
    /// </summary>
    public static void Save(
        PowerMonitorSettings settings,
        double wattHoursTotal,
        double wattHoursToday,
        DateOnly today,
        DateTimeOffset lastSampleAt,
        AlertEvaluator.AlertState alertState,
        string path)
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
                writer.WriteString("endpoint", settings.Endpoint);
                writer.WriteNumber("pollIntervalMs", settings.PollIntervalMs);
                writer.WriteBoolean("showInTray", settings.ShowInTray);
                writer.WriteBoolean("trackEnergy", settings.TrackEnergy);
                // 告警/历史配置（schemaVersion 2 新增）。
                writer.WriteBoolean("alertEnabled", settings.AlertEnabled);
                writer.WriteNumber("cpuTempThresholdC", settings.CpuTempThresholdC);
                writer.WriteNumber("gpuTempThresholdC", settings.GpuTempThresholdC);
                writer.WriteNumber("ssdTempThresholdC", settings.SsdTempThresholdC);
                writer.WriteNumber("historyRetentionDays", settings.HistoryRetentionDays);
                // 告警状态（跨重启保持，防重启后因残留超温状态重响）。
                writer.WriteBoolean("alertCpuTriggered", alertState.CpuTriggered);
                writer.WriteBoolean("alertGpuTriggered", alertState.GpuTriggered);
                writer.WriteBoolean("alertSsdTriggered", alertState.SsdTriggered);
                writer.WriteNumber("wattHoursTotal", wattHoursTotal);
                writer.WriteNumber("wattHoursToday", wattHoursToday);
                writer.WriteString("today", today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteNumber("lastSampleEpochMs", lastSampleAt.ToUnixTimeMilliseconds());
                writer.WriteEndObject();
                writer.Flush();
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw new ProviderConfigurationException("无法写入功耗监控配置文件。", exception);
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
