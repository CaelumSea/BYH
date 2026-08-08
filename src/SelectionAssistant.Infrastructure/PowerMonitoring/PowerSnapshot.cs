namespace SelectionAssistant.Infrastructure.PowerMonitoring;

/// <summary>
/// 一次轮询 Libre Hardware Monitor 的 <c>data.json</c> 得到的全部传感器快照。所有
/// 字段可空 —— 缺失传感器（台式机无电池、某些主板不报各路功率等）= <c>null</c>，UI 显示 "—"。
/// <para>
/// 字段分组对应设置页手机框 dock 的 4 个 view：CPU / GPU / System / Energy。
/// <see cref="TotalWatts"/> / <see cref="WattHours"/> / <see cref="TodayWattHours"/> 由
/// <see cref="EnergyAccumulator"/> 在 <c>OnSample</c> 中回填（瞬时合计 + 积分累计）。
/// </para><para>
/// 作为 <c>record struct</c>（非 readonly）是值类型，零堆分配，适合 2s 一次的高频轮询路径。HTTP client 和 EnergyAccumulator 会就地 mutate 字段，所以不能用 readonly record struct。
/// </para>
/// </summary>
public record struct PowerSnapshot
{
    // ── CPU 维度（dock 按钮 2）────────────────────────────────────────────
    /// <summary>CPU 封装功率（Intel: "CPU Package Power"；AMD: "CPU Package Power" / "Package"）。瓦。</summary>
    public double? CpuPackageWatts;

    /// <summary>CPU 最忙核心的负载百分比（"CPU Core Max"，Type=Load）。比单核功率更有意义。</summary>
    public double? CpuCoreMaxLoadPct;

    /// <summary>CPU 核心温度（"CPU Package" 或 "Core Average"，Type=Temperature）。摄氏度。</summary>
    public double? CpuTempC;

    /// <summary>CPU 核心平均频率（Type=Clock，"Core Average" 或 "Bus Speed"×倍频）。MHz。</summary>
    public double? CpuClockMhz;

    /// <summary>CPU 总负载百分比（"CPU Total"，Type=Load）。</summary>
    public double? CpuLoadPct;

    // ── GPU 维度（dock 按钮 3）────────────────────────────────────────────
    /// <summary>GPU 总功率（"GPU Power" / "Power" / "Total Board Power"）。瓦。</summary>
    public double? GpuPowerWatts;

    /// <summary>GPU 温度（"GPU Core"，Type=Temperature）。摄氏度。</summary>
    public double? GpuTempC;

    /// <summary>GPU 核心频率（"GPU Core"，Type=Clock）。MHz。</summary>
    public double? GpuClockMhz;

    /// <summary>GPU 显存频率（"GPU Memory"，Type=Clock）。MHz。</summary>
    public double? GpuMemClockMhz;

    /// <summary>GPU 核心负载百分比（"GPU Core"，Type=Load）。</summary>
    public double? GpuLoadPct;

    // ── 系统/主板维度（dock 按钮 4）──────────────────────────────────────
    /// <summary>主板 +12V 路功率（"+12V"，Type=Power，来自 LPC/Super IO）。瓦。台式机常见，笔记本通常无。</summary>
    public double? Rail12vWatts;

    /// <summary>主板 +5V 路功率。瓦。</summary>
    public double? Rail5vWatts;

    /// <summary>主板 +3.3V 路功率。瓦。</summary>
    public double? Rail3v3Watts;

    /// <summary>内存功率（"Memory" / "DRAM"，Type=Power）。瓦。</summary>
    public double? RamWatts;

    /// <summary>CPU 风扇转速（"CPU Fan" / "Fan #1"，Type=Fan）。RPM。</summary>
    public int? CpuFanRpm;

    /// <summary>GPU 风扇转速。RPM。</summary>
    public int? GpuFanRpm;

    /// <summary>电池充放电功率（笔记本 "Battery" / "Battery Charge"/"Discharge"，Type=Power）。
    /// 台式机无电池 = <c>null</c>。正=充电，负=放电（LHM 语义）。</summary>
    public double? BatteryWatts;

    /// <summary>电池剩余电量百分比。0–100。</summary>
    public double? BatteryPct;

    /// <summary>主 SSD/NVMe 复合温度（第一块盘，"Composite Temperature"）。摄氏度。
    /// 无 SSD 传感器 = <c>null</c>。SSD 过热会降速掉速，是健康指标。</summary>
    public double? Ssd1TempC;

    /// <summary>第二块 SSD/NVMe 温度（双盘机器）。单盘 = <c>null</c>。</summary>
    public double? Ssd2TempC;

    /// <summary>内存条温度（"DIMM #1" / "Memory"，Type=Temperature）。摄氏度。无传感器 = <c>null</c>。</summary>
    public double? RamTempC;

    // ── 能耗汇总维度（dock 按钮 5）────────────────────────────────────────
    /// <summary>瞬时主要元件功率合计（CPU+GPU+各路+内存+电池 的绝对值之和）。这是"主要元件
    /// 估算瓦数"，<b>不是</b>插座处的整机真实输入功率 —— 后者需智能插座等硬件。瓦。</summary>
    public double TotalWatts;

    /// <summary>累计 Watt-hours（跨重启连续，来自 <see cref="EnergyAccumulator"/> 梯形积分）。</summary>
    public double WattHours;

    /// <summary>今日累计 Wh（按本地日期切片，跨午夜归零）。</summary>
    public double TodayWattHours;

    /// <summary>本次快照的采集时刻（UTC）。用于能耗积分的 Δt 和 UI 的"上次更新"显示。</summary>
    public DateTimeOffset CapturedAt;

    /// <summary>是否成功连接并解析了 LHM 响应。<c>false</c> = 连接失败/超时/JSON 解析异常，
    /// 此时所有传感器字段保持上一次的值或 <c>null</c>，UI 应显示离线状态而非过时数据。</summary>
    public bool Connected;
}
