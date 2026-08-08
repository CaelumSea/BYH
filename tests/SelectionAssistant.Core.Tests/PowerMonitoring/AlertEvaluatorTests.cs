using SelectionAssistant.Core.PowerMonitoring;
using SelectionAssistant.Infrastructure.PowerMonitoring;
using Xunit;

namespace SelectionAssistant.Core.Tests.PowerMonitoring;

/// <summary>
/// AlertEvaluator 的滞后回差逻辑测试。覆盖：首次触发、不重复响、回差内不解除、
/// 回差以下才解除、SSD 取两盘最大值、null 传感器跳过、多指标同时触发、总开关关闭。
/// </summary>
public sealed class AlertEvaluatorTests
{
    // 便捷工厂：默认启用的告警配置，阈值 CPU=90 / GPU=85 / SSD=70。
    private static PowerMonitorSettings Settings(bool alertEnabled = true) => new()
    {
        AlertEnabled = alertEnabled,
        CpuTempThresholdC = 90,
        GpuTempThresholdC = 85,
        SsdTempThresholdC = 70,
    };

    // 便捷工厂：造一个 snapshot，只填关心的温度字段，其余默认 null。
    private static PowerSnapshot Snap(
        double? cpuT = null, double? gpuT = null,
        double? ssd1T = null, double? ssd2T = null, bool connected = true)
    {
        PowerSnapshot s = default;
        s.CpuTempC = cpuT;
        s.GpuTempC = gpuT;
        s.Ssd1TempC = ssd1T;
        s.Ssd2TempC = ssd2T;
        s.Connected = connected;
        return s;
    }

    [Fact]
    public void Cpu_FirstCrossing_Triggers()
    {
        AlertEvaluator.AlertResult r = AlertEvaluator.Evaluate(
            Snap(cpuT: 92), Settings(), AlertEvaluator.AlertState.Default);

        Assert.True(r.NewCpuTriggered, "首次超阈值应 NewCpuTriggered=true");
        Assert.True(r.State.CpuTriggered, "状态应进入触发态");
        Assert.True(r.AnyNewlyTriggered);
    }

    [Fact]
    public void Cpu_AlreadyTriggered_DoesNotRetrigger()
    {
        // 上次已触发，本次仍超阈值 → 保持触发但不重复响。
        var prev = new AlertEvaluator.AlertState(CpuTriggered: true, false, false);

        AlertEvaluator.AlertResult r = AlertEvaluator.Evaluate(Snap(cpuT: 92), Settings(), prev);

        Assert.False(r.NewCpuTriggered, "已触发态不应重复 NewCpuTriggered");
        Assert.True(r.State.CpuTriggered, "仍超阈值应保持触发");
    }

    [Fact]
    public void Cpu_WithinHysteresis_DoesNotClear()
    {
        // 触发后降到 阈值-3（在滞后区间 [阈值-5, 阈值] 内）→ 保持触发，不解除。
        // 阈值 90，回差 5，滞后区间 [85, 90]。当前 87 在区间内。
        var prev = new AlertEvaluator.AlertState(CpuTriggered: true, false, false);

        AlertEvaluator.AlertResult r = AlertEvaluator.Evaluate(Snap(cpuT: 87), Settings(), prev);

        Assert.True(r.State.CpuTriggered, "滞后区间内应保持触发，不解除");
        Assert.False(r.NewCpuTriggered);
    }

    [Fact]
    public void Cpu_BelowHysteresis_Clears()
    {
        // 触发后降到 阈值-6（低于回差下界 阈值-5）→ 解除。
        // 阈值 90，回差 5，解除点 <85。当前 84 解除。
        var prev = new AlertEvaluator.AlertState(CpuTriggered: true, false, false);

        AlertEvaluator.AlertResult r = AlertEvaluator.Evaluate(Snap(cpuT: 84), Settings(), prev);

        Assert.False(r.State.CpuTriggered, "降到回差以下应解除");
    }

    [Fact]
    public void Cpu_Cleared_CanRetrigger()
    {
        // 解除后再次超阈值应再次新触发。
        var prev = new AlertEvaluator.AlertState(CpuTriggered: false, false, false);

        AlertEvaluator.AlertResult r = AlertEvaluator.Evaluate(Snap(cpuT: 92), Settings(), prev);

        Assert.True(r.NewCpuTriggered, "解除后再次超阈值应视为新触发");
    }

    [Fact]
    public void Cpu_NullSensor_PreservesState()
    {
        // 传感器掉线（CpuTempC=null）→ 既不触发也不解除，保持原状态。
        var prevTriggered = new AlertEvaluator.AlertState(CpuTriggered: true, false, false);
        var prevIdle = new AlertEvaluator.AlertState(CpuTriggered: false, false, false);

        AlertEvaluator.AlertResult r1 = AlertEvaluator.Evaluate(Snap(cpuT: null), Settings(), prevTriggered);
        AlertEvaluator.AlertResult r2 = AlertEvaluator.Evaluate(Snap(cpuT: null), Settings(), prevIdle);

        Assert.True(r1.State.CpuTriggered, "掉线时已触发应保持触发");
        Assert.False(r2.State.CpuTriggered, "掉线时未触发应保持未触发");
        Assert.False(r1.NewCpuTriggered, "掉线不应产生新触发");
    }

    [Fact]
    public void Cpu_ExactlyAtThreshold_DoesNotTrigger()
    {
        // 边界：c > thresholdC 严格大于。当前=阈值=90 不应触发（90 不大于 90）。
        AlertEvaluator.AlertResult r = AlertEvaluator.Evaluate(
            Snap(cpuT: 90), Settings(), AlertEvaluator.AlertState.Default);

        Assert.False(r.NewCpuTriggered, "恰好等于阈值不触发（严格大于）");
    }

    [Fact]
    public void Ssd_TakesMaxOfTwoDrives()
    {
        // SSD1=65 SSD2=72，阈值 70 → 最高 72 超阈值，应触发。
        AlertEvaluator.AlertResult r = AlertEvaluator.Evaluate(
            Snap(ssd1T: 65, ssd2T: 72), Settings(), AlertEvaluator.AlertState.Default);

        Assert.True(r.NewSsdTriggered, "SSD2=72 超阈值应触发（取两盘最大值）");
        Assert.True(r.State.SsdTriggered);
    }

    [Fact]
    public void Ssd_OnlyOneDrive_UsesThat()
    {
        // 只有一块盘有值（另一块掉线），用那块判定。
        AlertEvaluator.AlertResult r = AlertEvaluator.Evaluate(
            Snap(ssd1T: null, ssd2T: 73), Settings(), AlertEvaluator.AlertState.Default);

        Assert.True(r.NewSsdTriggered, "只有 SSD2=73 超阈值应触发");
    }

    [Fact]
    public void Ssd_BothNull_NoTrigger()
    {
        AlertEvaluator.AlertResult r = AlertEvaluator.Evaluate(
            Snap(ssd1T: null, ssd2T: null), Settings(), AlertEvaluator.AlertState.Default);

        Assert.False(r.NewSsdTriggered, "两盘都掉线不应触发");
        Assert.False(r.State.SsdTriggered);
    }

    [Fact]
    public void AllThree_TriggerSimultaneously()
    {
        // CPU/GPU/SSD 同时超阈值，三个应同时新触发。
        AlertEvaluator.AlertResult r = AlertEvaluator.Evaluate(
            Snap(cpuT: 95, gpuT: 90, ssd1T: 75, ssd2T: 78),
            Settings(),
            AlertEvaluator.AlertState.Default);

        Assert.True(r.NewCpuTriggered);
        Assert.True(r.NewGpuTriggered);
        Assert.True(r.NewSsdTriggered);
        Assert.True(r.AnyNewlyTriggered);
        Assert.True(r.AnyActive);
        Assert.Equal(
            new AlertEvaluator.AlertState(true, true, true),
            r.State);
    }

    [Fact]
    public void AlertDisabled_NoNewTrigger_PreservesState()
    {
        // 总开关关闭：即便严重超温，也不产生新触发。状态保持 previous 原样。
        var prev = new AlertEvaluator.AlertState(false, false, false);

        AlertEvaluator.AlertResult r = AlertEvaluator.Evaluate(
            Snap(cpuT: 99, gpuT: 99, ssd1T: 99),
            Settings(alertEnabled: false),
            prev);

        Assert.False(r.AnyNewlyTriggered, "告警关闭时不应有任何新触发");
        Assert.False(r.NewCpuTriggered);
        Assert.Equal(prev, r.State);
    }

    [Fact]
    public void AlertDisabled_PreservesExistingTriggeredState()
    {
        // 关告警时如果之前正触发，状态应保留（重开时若仍超温会再次新触发）。
        var prev = new AlertEvaluator.AlertState(true, false, false);

        AlertEvaluator.AlertResult r = AlertEvaluator.Evaluate(
            Snap(cpuT: 99), Settings(alertEnabled: false), prev);

        Assert.True(r.State.CpuTriggered, "关告警不应清除既有触发状态");
        Assert.False(r.AnyNewlyTriggered);
    }

    [Fact]
    public void Hysteresis_Constant_IsFive()
    {
        // 守卫回差常量。改动它意味着调整所有阈值判定的滞后区间，必须是有意为之。
        Assert.Equal(5, AlertEvaluator.HysteresisC);
    }
}
