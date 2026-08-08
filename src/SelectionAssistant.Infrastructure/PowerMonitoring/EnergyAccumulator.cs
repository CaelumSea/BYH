using System.Globalization;

namespace SelectionAssistant.Infrastructure.PowerMonitoring;

/// <summary>
/// 梯形积分累计能耗（Wh）。每次 <see cref="OnSample"/> 用新旧两次瞬时功率的算术平均乘以
/// Δt/3600 得到 Wh 增量，比矩形法（取单点功率）更准 —— 尤其在 CPU 瞬时尖峰（PL2 真男人
/// 时间几十瓦瞬间飙到 100+W）时误差更小。<see cref="OnSample"/> 同时回填传入
/// <see cref="PowerSnapshot"/> 的 <see cref="PowerSnapshot.TotalWatts"/> /
/// <see cref="PowerSnapshot.WattHours"/> / <see cref="PowerSnapshot.TodayWattHours"/>。
/// <para>
/// 跨午夜自动把"今日 Wh"切片归零（基于本地日期）。累计 Wh 跨重启连续 ——
/// <see cref="SelectionRuntime"/> 在启动时从 <c>power-monitoring.json</c> 调用
/// <see cref="Load"/> 恢复，退出时落盘。
/// </para><para>
/// 线程安全：所有可变状态通过 <c>lock</c> 保护，因为后台轮询线程写、UI 线程可能读。
/// </para>
/// </summary>
public sealed class EnergyAccumulator
{
    private readonly object _lock = new();
    private double _wattHoursTotal;
    private double _wattHoursToday;
    private DateOnly _today;
    private DateTimeOffset _lastSampleAt;
    private double _lastWatts;
    private bool _hasLastSample;

    /// <summary>
    /// 启动时从持久化恢复累计值。传 0 / DateTimeOffset.MinValue 表示无历史数据。
    /// <paramref name="today"/> 与文件里的今日日期不同时（机器关了一夜），今日 Wh 归零。
    /// </summary>
    public void Load(double wattHoursTotal, double wattHoursToday, DateOnly today, DateTimeOffset lastSampleAt)
    {
        lock (_lock)
        {
            _wattHoursTotal = Math.Max(0, wattHoursTotal);
            // 若落盘的 today 与当前 today 不同，今日计数归零（跨夜）。
            _wattHoursToday = today == DateOnly.FromDateTime(DateTime.Now) ? Math.Max(0, wattHoursToday) : 0;
            _today = DateOnly.FromDateTime(DateTime.Now);
            _lastSampleAt = lastSampleAt;
            _lastWatts = 0;
            _hasLastSample = false;
        }
    }

    /// <summary>
    /// 处理一次新采样：计算瞬时合计功率、（可选）梯形积分累计 Wh、跨午夜归零今日 Wh，
    /// 并把结果回填到 <paramref name="snapshot"/> 的 Total/WattHours/TodayWattHours 字段。
    /// 调用方应在拿到 client 解析出的 snapshot（传感器字段已填、TotalWatts 等未填）后立即调用本方法。
    /// <para>
    /// 断连样本（<see cref="PowerSnapshot.Connected"/> == false）<b>不</b>积分 —— 但仍把累计
    /// 的 Wh 回填进去，让 UI 能持续显示历史累计值。
    /// </para>
    /// </summary>
    public void OnSample(ref PowerSnapshot snapshot)
    {
        // 瞬时合计：取所有主要元件功率的绝对值之和（电池放电时为负，取绝对值计消耗）。
        // 注意这只是"主要元件估算"，不含显示器/外设/转换损耗 —— 真实整机瓦数需智能插座。
        double total = 0;
        AddIfHasValue(ref total, snapshot.CpuPackageWatts);
        AddIfHasValue(ref total, snapshot.GpuPowerWatts);
        AddIfHasValue(ref total, snapshot.Rail12vWatts);
        AddIfHasValue(ref total, snapshot.Rail5vWatts);
        AddIfHasValue(ref total, snapshot.Rail3v3Watts);
        AddIfHasValue(ref total, snapshot.RamWatts);
        if (snapshot.BatteryWatts.HasValue)
        {
            // 电池放电（负值）计为消耗；充电（正值）也计入（毕竟在耗电）。
            total += Math.Abs(snapshot.BatteryWatts.Value);
        }

        snapshot.TotalWatts = total;

        lock (_lock)
        {
            // 跨午夜检测：当前日期变了，今日 Wh 归零。
            DateNow(out DateOnly now);
            if (_today != now)
            {
                _today = now;
                _wattHoursToday = 0;
            }

            // 回填累计值（无论是否连接，都把历史累计给 UI 显示）。
            snapshot.WattHours = _wattHoursTotal;
            snapshot.TodayWattHours = _wattHoursToday;

            if (!snapshot.Connected)
            {
                // 断连不积分，但记下"无有效上一次"，重连后从单点重新开始积分。
                _hasLastSample = false;
                return;
            }

            if (_hasLastSample)
            {
                // 梯形法：ΔWh = (P_prev + P_now)/2 × Δt(秒) / 3600。
                double deltaSeconds = (snapshot.CapturedAt - _lastSampleAt).TotalSeconds;
                if (deltaSeconds > 0 && deltaSeconds < 3600)
                {
                    // 防御：Δt 超过 1 小时（机器休眠唤醒等）跳过这次积分，避免一次跳一大段。
                    double avgWatts = (_lastWatts + total) / 2.0;
                    double deltaWh = avgWatts * deltaSeconds / 3600.0;
                    if (deltaWh > 0)
                    {
                        _wattHoursTotal += deltaWh;
                        _wattHoursToday += deltaWh;
                    }
                }
            }

            _lastWatts = total;
            _lastSampleAt = snapshot.CapturedAt;
            _hasLastSample = true;

            // 再次回填，让本次 snapshot 带上积分后的累计值。
            snapshot.WattHours = _wattHoursTotal;
            snapshot.TodayWattHours = _wattHoursToday;
        }
    }

    /// <summary>取当前累计快照（不积分），供 UI 在轮询间隙随时读最近一次累计值。</summary>
    public (double WattHoursTotal, double WattHoursToday, DateTimeOffset LastSampleAt) Snapshot()
    {
        lock (_lock)
        {
            return (_wattHoursTotal, _wattHoursToday, _lastSampleAt);
        }
    }

    private static void AddIfHasValue(ref double sum, double? value)
    {
        if (value.HasValue)
        {
            sum += Math.Abs(value.Value);
        }
    }

    private static void DateNow(out DateOnly today)
    {
        DateTime now = DateTime.Now;
        today = new DateOnly(now.Year, now.Month, now.Day);
    }
}
