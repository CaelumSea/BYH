using SelectionAssistant.Core.PowerMonitoring;
using Xunit;

namespace SelectionAssistant.Core.Tests.PowerMonitoring;

public sealed class PowerMonitorSettingsTests
{
    [Fact]
    public void Default_IsOptIn_DashboardOff()
    {
        // 用户必须显式装 LHM 并开 Web Server 后才启用 —— 默认关闭。
        PowerMonitorSettings d = PowerMonitorSettings.Default;
        Assert.False(d.Enabled);
        Assert.Equal("http://localhost:8085/data.json", d.Endpoint);
        Assert.Equal(3000, d.PollIntervalMs);
        Assert.True(d.ShowInTray);
        Assert.True(d.TrackEnergy);
        // 告警默认关闭，阈值 CPU=90/GPU=85/SSD=70，历史保留 30 天。
        Assert.False(d.AlertEnabled);
        Assert.Equal(93, d.CpuTempThresholdC);
        Assert.Equal(85, d.GpuTempThresholdC);
        Assert.Equal(73, d.SsdTempThresholdC);
        Assert.Equal(30, d.HistoryRetentionDays);
    }

    [Fact]
    public void Normalize_RestoresEmptyEndpointToDefault()
    {
        PowerMonitorSettings s = new() { Endpoint = "   " };
        PowerMonitorSettings n = s.Normalize();
        Assert.Equal(PowerMonitorSettings.Default.Endpoint, n.Endpoint);
    }

    [Fact]
    public void Normalize_TrimsEndpoint()
    {
        PowerMonitorSettings s = new() { Endpoint = "  http://192.168.1.5:8085/data.json  " };
        Assert.Equal("http://192.168.1.5:8085/data.json", s.Normalize().Endpoint);
    }

    [Theory]
    [InlineData(0)]      // 非法 → 默认
    [InlineData(-100)]
    public void Normalize_NonPositiveIntervalRestoresDefault(int bad)
    {
        PowerMonitorSettings s = new() { PollIntervalMs = bad };
        Assert.Equal(PowerMonitorSettings.Default.PollIntervalMs, s.Normalize().PollIntervalMs);
    }

    [Theory]
    [InlineData(500, 1000)]     // 低于下限 → 夹到下限
    [InlineData(1000, 1000)]    // 下限
    [InlineData(3000, 3000)]    // 正常
    [InlineData(60000, 60000)]  // 上限
    [InlineData(120000, 60000)] // 超上限 → 夹到上限
    public void Normalize_ClampsInterval(int input, int expected)
    {
        PowerMonitorSettings s = new() { PollIntervalMs = input };
        Assert.Equal(expected, s.Normalize().PollIntervalMs);
    }

    [Fact]
    public void Normalize_DoesNotMutateBooleans_WhenAlreadySet()
    {
        PowerMonitorSettings s = new() { Enabled = true, ShowInTray = false, TrackEnergy = false };
        PowerMonitorSettings n = s.Normalize();
        Assert.True(n.Enabled);
        Assert.False(n.ShowInTray);
        Assert.False(n.TrackEnergy);
    }

    [Fact]
    public void Validate_ValidAbsoluteUrl_Passes()
    {
        // Normalize 已经把字段收拾干净，Validate 不抛即通过。
        PowerMonitorSettings.Default.Normalize().Validate();
        new PowerMonitorSettings { Endpoint = "https://192.168.0.10:8085/data.json" }.Normalize().Validate();
    }

    [Fact]
    public void Validate_RelativeUrl_Throws()
    {
        PowerMonitorSettings s = new() { Endpoint = "localhost:8085/data.json" };
        Assert.Throws<ArgumentException>(() => s.Validate());
    }

    [Fact]
    public void Validate_Garbage_Throws()
    {
        PowerMonitorSettings s = new() { Endpoint = "not a url at all" };
        Assert.Throws<ArgumentException>(() => s.Validate());
    }

    [Fact]
    public void Normalize_IsPure_DoesNotMutateOriginal()
    {
        PowerMonitorSettings original = new() { PollIntervalMs = 500 };
        PowerMonitorSettings normalized = original.Normalize();
        // with { } 返回副本，原对象不变。
        Assert.Equal(500, original.PollIntervalMs);
        Assert.Equal(1000, normalized.PollIntervalMs);
    }

    [Theory]
    [InlineData(49, 50)]    // 低于下限 → 夹到下限
    [InlineData(50, 50)]    // 下限
    [InlineData(95, 95)]    // 正常
    [InlineData(110, 110)]  // 上限
    [InlineData(150, 110)]  // 超上限 → 夹到上限
    public void Normalize_ClampsCpuThreshold(int input, int expected)
    {
        PowerMonitorSettings s = new() { CpuTempThresholdC = input };
        Assert.Equal(expected, s.Normalize().CpuTempThresholdC);
    }

    [Theory]
    [InlineData(49, 50)]
    [InlineData(110, 110)]
    [InlineData(200, 110)]
    public void Normalize_ClampsGpuThreshold(int input, int expected)
    {
        PowerMonitorSettings s = new() { GpuTempThresholdC = input };
        Assert.Equal(expected, s.Normalize().GpuTempThresholdC);
    }

    [Theory]
    [InlineData(39, 40)]    // SSD 下限 40（不同于 CPU/GPU 的 50）
    [InlineData(40, 40)]
    [InlineData(70, 70)]
    [InlineData(90, 90)]
    [InlineData(99, 90)]
    public void Normalize_ClampsSsdThreshold(int input, int expected)
    {
        PowerMonitorSettings s = new() { SsdTempThresholdC = input };
        Assert.Equal(expected, s.Normalize().SsdTempThresholdC);
    }

    [Theory]
    [InlineData(6, 7)]      // 低于下限 → 夹到下限 7
    [InlineData(7, 7)]
    [InlineData(30, 30)]
    [InlineData(365, 365)]
    [InlineData(9999, 365)] // 超上限 → 夹到上限 365
    public void Normalize_ClampsHistoryRetentionDays(int input, int expected)
    {
        PowerMonitorSettings s = new() { HistoryRetentionDays = input };
        Assert.Equal(expected, s.Normalize().HistoryRetentionDays);
    }
}
