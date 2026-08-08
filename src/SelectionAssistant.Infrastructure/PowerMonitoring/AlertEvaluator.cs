using SelectionAssistant.Core.PowerMonitoring;

namespace SelectionAssistant.Infrastructure.PowerMonitoring;

/// <summary>
/// 温度告警评估器：输入本次采样 + 上次告警状态 → 输出新状态 + 是否有"新触发"。
/// 每个指标（CPU/GPU/SSD）独立判定，带 <b>滞后回差</b>（hysteresis）防止边界抖动
/// 反复响：触发后必须降温到 <c>阈值 - <see cref="HysteresisC"/></c> 才会解除，从而
/// 允许下次再次触发。回差固定 5°C（<see cref="HysteresisC"/>）。
/// <para>
/// 判定规则（每个指标）：
/// <list type="bullet">
///   <item>当前值 &gt; 阈值 且 未触发 → <b>新触发</b>（响铃 + 标记），状态置触发</item>
///   <item>当前值 &gt; 阈值 且 已触发 → 保持触发，<b>不重复响</b></item>
///   <item>当前值 &lt; 阈值 - 回差 → 解除触发（允许下次再触发）</item>
///   <item>阈值 - 回差 ≤ 当前值 ≤ 阈值 且 已触发 → 保持触发（滞后区间）</item>
///   <item>传感器掉线（null） → 不触发也不解除，保持原状态</item>
/// </list>
/// </para>
/// <para>
/// SSD 取 SSD1/SSD2 两盘最高温（<c>Math.Max</c>），任一超阈值即视为 SSD 超阈值。
/// 这是 <see cref="PowerMonitorSettings.SsdTempThresholdC"/> 一个阈值管两盘的语义。
/// </para>
/// <para>
/// 纯函数（除传入状态外无副作用），完全可单测。无 IO、无反射，AOT 安全。
/// </para>
/// </summary>
public static class AlertEvaluator
{
    /// <summary>
    /// 滞后回差（°C）。触发后需降温到「阈值 - 回差」以下才会解除。
    /// 5°C 是经验值：既能挡住传感器 ±2°C 抖动，又不会让告警解除太迟钝。
    /// </summary>
    public const int HysteresisC = 5;

    /// <summary>
    /// 每个温度指标的触发状态。三个 bool 各自独立。可序列化到 power-monitoring.json
    /// 以跨重启保持（防止"刚关时正超温，重启后又响一遍"）。
    /// </summary>
    public record struct AlertState(bool CpuTriggered, bool GpuTriggered, bool SsdTriggered)
    {
        /// <summary>全未触发的初始状态。</summary>
        public static AlertState Default { get; } = new(false, false, false);
    }

    /// <summary>
    /// 一次评估的结果：新状态 + 各指标是否有"新触发"（首次超过阈值的 tick）。
    /// 只有 NewCpuTriggered/NewGpuTriggered/NewSsdTriggered 为 true 时才应该响铃；
    /// 已触发态保持不重复响。
    /// </summary>
    public readonly record struct AlertResult(
        AlertState State,
        bool NewCpuTriggered,
        bool NewGpuTriggered,
        bool NewSsdTriggered)
    {
        /// <summary>本次是否有任意新触发（用于决定是否启动提示音播放）。</summary>
        public bool AnyNewlyTriggered => NewCpuTriggered || NewGpuTriggered || NewSsdTriggered;

        /// <summary>本次是否有任意指标处于触发态（用于决定 tooltip 是否加 🔴 警告行）。</summary>
        public bool AnyActive => State.CpuTriggered || State.GpuTriggered || State.SsdTriggered;
    }

    /// <summary>
    /// 评估本次采样的告警状态。不修改输入 snapshot；状态由调用方持有并回传。
    /// 当 <paramref name="settings"/>.<see cref="PowerMonitorSettings.AlertEnabled"/>
    /// 为 false 时，直接返回全未触发（既不响也不标记，但不清除既有状态——调用方决定语义）。
    /// </summary>
    /// <param name="snap">本次功率/温度采样。</param>
    /// <param name="settings">告警阈值配置（CpuTempThresholdC 等）。</param>
    /// <param name="previous">上次 tick 结束时的告警状态（用于滞后回差判定）。</param>
    public static AlertResult Evaluate(in PowerSnapshot snap, PowerMonitorSettings settings, AlertState previous)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 总开关关闭：既不评估也不改变状态。返回 previous 原样（调用方据此既不响也不标记）。
        // 注意：不强制 reset，因为"关告警"不等于"清状态"——重开时如果还超温，应视为新触发再响。
        // 但调用方在 UpdatePowerTrayTooltip 里若 AlertEnabled=false 则根本不看 AnyActive，
        // 所以即便状态里有残留 triggered=true，tooltip 也不显示。这是设计意图。
        if (!settings.AlertEnabled)
        {
            return new AlertResult(previous, false, false, false);
        }

        // CPU：单一温度字段 CpuTempC。null（传感器掉线）→ 不判定，保持原状态。
        bool cpuTriggered = EvaluateSingle(snap.CpuTempC, settings.CpuTempThresholdC, previous.CpuTriggered);
        bool newCpu = cpuTriggered && !previous.CpuTriggered;

        // GPU：单一温度字段 GpuTempC。
        bool gpuTriggered = EvaluateSingle(snap.GpuTempC, settings.GpuTempThresholdC, previous.GpuTriggered);
        bool newGpu = gpuTriggered && !previous.GpuTriggered;

        // SSD：取 SSD1/SSD2 两盘最高温作为判定值。两盘都 null → 视为传感器掉线，不判定。
        // 只有一块盘有值就用那块；两块都有取 max。
        double? ssdMax = MaxNullable(snap.Ssd1TempC, snap.Ssd2TempC);
        bool ssdTriggered = EvaluateSingle(ssdMax, settings.SsdTempThresholdC, previous.SsdTriggered);
        bool newSsd = ssdTriggered && !previous.SsdTriggered;

        return new AlertResult(
            new AlertState(cpuTriggered, gpuTriggered, ssdTriggered),
            newCpu,
            newGpu,
            newSsd);
    }

    /// <summary>
    /// 单指标的滞后回差判定。返回该指标在本 tick 结束时是否应处于触发态。
    /// <paramref name="current"/> 为 null 时不判定（返回 <paramref name="wasTriggered"/> 原样）。
    /// </summary>
    private static bool EvaluateSingle(double? current, int thresholdC, bool wasTriggered)
    {
        if (!current.HasValue)
        {
            return wasTriggered; // 传感器掉线：保持原状态，不触发也不解除
        }
        double c = current.Value;
        if (c > thresholdC)
        {
            return true; // 超阈值：触发（首次或保持）
        }
        if (c < thresholdC - HysteresisC)
        {
            return false; // 降到回差以下：解除
        }
        return wasTriggered; // 滞后区间：保持原状态
    }

    /// <summary>两个可空 double 的最大值。都 null 返回 null；任一非 null 取其大。</summary>
    private static double? MaxNullable(double? a, double? b)
    {
        if (!a.HasValue) return b;
        if (!b.HasValue) return a;
        return Math.Max(a.Value, b.Value);
    }
}
