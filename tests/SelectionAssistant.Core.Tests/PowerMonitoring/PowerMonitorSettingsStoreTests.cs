using SelectionAssistant.Core.PowerMonitoring;
using SelectionAssistant.Infrastructure.Configuration;
using SelectionAssistant.Infrastructure.PowerMonitoring;
using Xunit;

namespace SelectionAssistant.Core.Tests.PowerMonitoring;

/// <summary>
/// PowerMonitorSettingsStore 测试：schemaVersion 2 完整 round-trip、schemaVersion 1 旧文件
/// 迁移（缺告警/历史字段 → 默认值）、告警状态持久化、文件缺失 → 默认。每个测试独立临时文件。
/// </summary>
public sealed class PowerMonitorSettingsStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-pm-{Guid.NewGuid():N}.json");

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [Fact]
    public void MissingFile_ReturnsDefault_AndZeroEnergy()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");
        var (settings, whTotal, whToday, today, lastAt, alert) =
            PowerMonitorSettingsStore.LoadIfExists(missing);

        Assert.Equal(PowerMonitorSettings.Default, settings);
        Assert.Equal(0, whTotal);
        Assert.Equal(0, whToday);
        Assert.Equal(AlertEvaluator.AlertState.Default, alert);
        Assert.Equal(DateTimeOffset.MinValue, lastAt);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields_v2()
    {
        string path = TempPath();
        try
        {
            var original = new PowerMonitorSettings
            {
                Enabled = true,
                Endpoint = "http://192.168.1.10:8085/data.json",
                PollIntervalMs = 5000,
                ShowInTray = false,
                TrackEnergy = false,
                AlertEnabled = true,
                CpuTempThresholdC = 95,
                GpuTempThresholdC = 80,
                SsdTempThresholdC = 65,
                HistoryRetentionDays = 60,
            };
            var alertState = new AlertEvaluator.AlertState(true, false, true);

            PowerMonitorSettingsStore.Save(original, 123.4, 45.6, new DateOnly(2026, 8, 7),
                new DateTimeOffset(2026, 8, 7, 22, 15, 3, TimeSpan.FromHours(8)), alertState, path);

            var (loaded, whTotal, whToday, today, lastAt, loadedAlert) =
                PowerMonitorSettingsStore.LoadIfExists(path);

            Assert.Equal(original, loaded);
            Assert.Equal(123.4, whTotal);
            Assert.Equal(45.6, whToday);
            Assert.Equal(new DateOnly(2026, 8, 7), today);
            Assert.Equal(alertState, loadedAlert);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Load_v1_LegacyFile_AlarmHistoryFieldsDefault()
    {
        // 模拟一个 schemaVersion=1 的旧文件：没有告警/历史字段。
        // 预期：告警默认关、阈值默认值（90/85/70）、保留天数默认 30、告警状态全未触发。
        string path = TempPath();
        try
        {
            const string v1Content = """"
                {
                  "schemaVersion": 1,
                  "enabled": true,
                  "endpoint": "http://localhost:8085/data.json",
                  "pollIntervalMs": 3000,
                  "showInTray": true,
                  "trackEnergy": true,
                  "wattHoursTotal": 500.5,
                  "wattHoursToday": 100.2,
                  "today": "2026-08-06",
                  "lastSampleEpochMs": 1786000000000
                }
                """";
            File.WriteAllText(path, v1Content);

            var (settings, whTotal, whToday, today, lastAt, alert) =
                PowerMonitorSettingsStore.LoadIfExists(path);

            // 旧字段正确读取
            Assert.True(settings.Enabled);
            Assert.Equal(3000, settings.PollIntervalMs);
            Assert.Equal(500.5, whTotal);
            Assert.Equal(100.2, whToday);

            // 新字段走默认值（schemaVersion 1 旧文件缺这些）。默认阈值对齐
            // 当前硬件实测：CPU 93 / GPU 85 / SSD 73。
            Assert.False(settings.AlertEnabled, "告警默认关");
            Assert.Equal(93, settings.CpuTempThresholdC);
            Assert.Equal(85, settings.GpuTempThresholdC);
            Assert.Equal(73, settings.SsdTempThresholdC);
            Assert.Equal(30, settings.HistoryRetentionDays);
            Assert.Equal(AlertEvaluator.AlertState.Default, alert);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Save_AlwaysWritesSchemaVersion2()
    {
        string path = TempPath();
        try
        {
            PowerMonitorSettingsStore.Save(
                PowerMonitorSettings.Default, 0, 0, DateOnly.FromDateTime(DateTime.Now),
                DateTimeOffset.UtcNow, AlertEvaluator.AlertState.Default, path);

            string content = File.ReadAllText(path);
            Assert.Contains("\"schemaVersion\": 2", content);
            // 告警/历史字段都在
            Assert.Contains("\"alertEnabled\"", content);
            Assert.Contains("\"cpuTempThresholdC\"", content);
            Assert.Contains("\"historyRetentionDays\"", content);
            Assert.Contains("\"alertCpuTriggered\"", content);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Load_RejectsUnknownSchemaVersion()
    {
        // schemaVersion=99 不支持，应抛 ProviderConfigurationException。
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\": 99}");
            Assert.Throws<ProviderConfigurationException>(() =>
                PowerMonitorSettingsStore.LoadIfExists(path));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AlertState_PersistsAcrossRoundTrip()
    {
        // 告警状态各组合 round-trip：全触发、部分触发、全未触发。
        string path = TempPath();
        try
        {
            foreach (var state in new[]
            {
                new AlertEvaluator.AlertState(true, true, true),
                new AlertEvaluator.AlertState(true, false, false),
                new AlertEvaluator.AlertState(false, true, false),
                new AlertEvaluator.AlertState(false, false, false),
            })
            {
                PowerMonitorSettingsStore.Save(PowerMonitorSettings.Default, 0, 0,
                    DateOnly.FromDateTime(DateTime.Now), DateTimeOffset.UtcNow, state, path);
                var (_, _, _, _, _, loaded) = PowerMonitorSettingsStore.LoadIfExists(path);
                Assert.Equal(state, loaded);
            }
        }
        finally { Cleanup(path); }
    }
}
