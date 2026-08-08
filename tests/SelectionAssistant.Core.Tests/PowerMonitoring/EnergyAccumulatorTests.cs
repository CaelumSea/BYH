using SelectionAssistant.Infrastructure.PowerMonitoring;
using Xunit;

namespace SelectionAssistant.Core.Tests.PowerMonitoring;

public sealed class EnergyAccumulatorTests
{
    // ── 梯形积分核心正确性 ────────────────────────────────────────────────

    [Fact]
    public void OnSample_TrapezoidalRule_TwoSamplesYieldExpectedWh()
    {
        // 100W 持续 36 秒 → 100W × 36s / 3600 = 1.0 Wh（矩形法）。
        // 梯形法用首末功率平均：100W→100W 的平均就是 100，结果同。
        var acc = new EnergyAccumulator();
        var t0 = DateTimeOffset.Parse("2026-08-06T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var t1 = t0.AddSeconds(36);

        var s0 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t0 };
        acc.OnSample(ref s0);
        var s1 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t1 };
        acc.OnSample(ref s1);

        Assert.Equal(1.0, s1.WattHours, 3);
    }

    [Fact]
    public void OnSample_TrapezoidalRule_RampFromZeroHalvesTheResult()
    {
        // 0W → 100W，36 秒。梯形法：(0+100)/2 × 36/3600 = 0.5 Wh。
        // 矩形法（取后点）会是 1.0 Wh —— 这就是梯形法更准的体现。
        var acc = new EnergyAccumulator();
        var t0 = DateTimeOffset.Parse("2026-08-06T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var t1 = t0.AddSeconds(36);

        var s0 = new PowerSnapshot { Connected = true, CpuPackageWatts = 0, CapturedAt = t0 };
        acc.OnSample(ref s0);
        var s1 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t1 };
        acc.OnSample(ref s1);

        Assert.Equal(0.5, s1.WattHours, 3);
    }

    [Fact]
    public void OnSample_AccumulatesAcrossMultipleSamples()
    {
        // 三个采样：100W→100W（36s，1Wh）→ 100W（36s，1Wh）= 2Wh 总累计。
        var acc = new EnergyAccumulator();
        var t0 = DateTimeOffset.Parse("2026-08-06T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var s0 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t0 };
        acc.OnSample(ref s0);
        var s1 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t0.AddSeconds(36) };
        acc.OnSample(ref s1);
        var s2 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t0.AddSeconds(72) };
        acc.OnSample(ref s2);

        Assert.Equal(2.0, s2.WattHours, 3);
    }

    // ── 断连处理 ─────────────────────────────────────────────────────────

    [Fact]
    public void OnSample_DisconnectedSample_DoesNotIntegrate()
    {
        var acc = new EnergyAccumulator();
        var t0 = DateTimeOffset.Parse("2026-08-06T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var s0 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t0 };
        acc.OnSample(ref s0);
        var sDisc = new PowerSnapshot { Connected = false, CapturedAt = t0.AddSeconds(36) };
        acc.OnSample(ref sDisc);

        // 断连不积分，累计应仍为 0。
        Assert.Equal(0, sDisc.WattHours, 3);
        // 但累计值仍被回填给 UI（0 是合法的历史值）。
        Assert.Equal(0, sDisc.TodayWattHours, 3);
    }

    [Fact]
    public void OnSample_DisconnectedThenReconnected_ResumesFromSinglePoint()
    {
        // 连续 → 断连 → 重连。断连期间不积分；重连后从重连那一刻作为单点重新开始。
        var acc = new EnergyAccumulator();
        var t0 = DateTimeOffset.Parse("2026-08-06T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var s0 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t0 };
        acc.OnSample(ref s0); // 第一次单点，不积分
        var s1 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t0.AddSeconds(36) };
        acc.OnSample(ref s1); // +1 Wh
        var sDisc = new PowerSnapshot { Connected = false, CapturedAt = t0.AddSeconds(72) };
        acc.OnSample(ref sDisc); // 断连，0 增量
        // 重连 —— 这里不应把断连→重连这一段的 36s 当作有效积分（重连后变成新单点）。
        var sRecon = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t0.AddSeconds(108) };
        acc.OnSample(ref sRecon);

        Assert.Equal(1.0, sRecon.WattHours, 3);
    }

    // ── 时间异常防御 ─────────────────────────────────────────────────────

    [Fact]
    public void OnSample_HugeGapDoesNotIntegrate_AvoidsSleepResumeSpikes()
    {
        // 机器休眠 2 小时唤醒，Δt 超过 1 小时 → 不积分（防止一次跳几百 Wh）。
        var acc = new EnergyAccumulator();
        var t0 = DateTimeOffset.Parse("2026-08-06T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var s0 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t0 };
        acc.OnSample(ref s0);
        var s1 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t0.AddHours(2) };
        acc.OnSample(ref s1);

        Assert.Equal(0, s1.WattHours, 3);
    }

    [Fact]
    public void OnSample_NegativeDelta_DoesNotIntegrate()
    {
        // 时钟回退（NTP 调整、手动改时间）→ Δt ≤ 0，跳过。
        var acc = new EnergyAccumulator();
        var t0 = DateTimeOffset.Parse("2026-08-06T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var s0 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t0 };
        acc.OnSample(ref s0);
        var s1 = new PowerSnapshot { Connected = true, CpuPackageWatts = 100, CapturedAt = t0.AddSeconds(-10) };
        acc.OnSample(ref s1);

        Assert.Equal(0, s1.WattHours, 3);
    }

    // ── TotalWatts 汇总 ──────────────────────────────────────────────────

    [Fact]
    public void OnSample_TotalWatts_SumsAbsoluteValuesOfMajorComponents()
    {
        var acc = new EnergyAccumulator();
        var s = new PowerSnapshot
        {
            Connected = true,
            CpuPackageWatts = 45.5,
            GpuPowerWatts = 120.0,
            RamWatts = 3.0,
            // 电池放电为负，绝对值计入。
            BatteryWatts = -15.0,
            CapturedAt = DateTimeOffset.UtcNow,
        };
        acc.OnSample(ref s);

        Assert.Equal(45.5 + 120.0 + 3.0 + 15.0, s.TotalWatts, 3);
    }

    [Fact]
    public void OnSample_TotalWatts_IgnoresNullComponents()
    {
        var acc = new EnergyAccumulator();
        var s = new PowerSnapshot
        {
            Connected = true,
            CpuPackageWatts = 50,
            // 其余全 null
            CapturedAt = DateTimeOffset.UtcNow,
        };
        acc.OnSample(ref s);

        Assert.Equal(50, s.TotalWatts, 3);
    }

    // ── Load/恢复 ────────────────────────────────────────────────────────

    [Fact]
    public void Load_RestoresTotalAndResetsTodayWhenDateAdvanced()
    {
        var acc = new EnergyAccumulator();
        var yesterday = new DateOnly(2026, 8, 5);
        acc.Load(wattHoursTotal: 1000, wattHoursToday: 50, yesterday, DateTimeOffset.UtcNow);

        // today 是文件里的昨天 → 与今天不同 → 今日 Wh 归零，但总累计保留。
        var (whTotal, whToday, _) = acc.Snapshot();
        Assert.Equal(1000, whTotal, 3);
        Assert.Equal(0, whToday, 3);
    }

    [Fact]
    public void Load_KeepsTodayWhenSameDay()
    {
        var acc = new EnergyAccumulator();
        var today = DateOnly.FromDateTime(DateTime.Now);
        acc.Load(wattHoursTotal: 1000, wattHoursToday: 50, today, DateTimeOffset.UtcNow);

        var (whTotal, whToday, _) = acc.Snapshot();
        Assert.Equal(1000, whTotal, 3);
        Assert.Equal(50, whToday, 3);
    }

    [Fact]
    public void Load_NegativeValuesClampedToZero()
    {
        var acc = new EnergyAccumulator();
        acc.Load(wattHoursTotal: -5, wattHoursToday: -3, DateOnly.FromDateTime(DateTime.Now), DateTimeOffset.UtcNow);
        var (whTotal, whToday, _) = acc.Snapshot();
        Assert.Equal(0, whTotal, 3);
        Assert.Equal(0, whToday, 3);
    }
}
