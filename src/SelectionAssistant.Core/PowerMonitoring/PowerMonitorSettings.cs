namespace SelectionAssistant.Core.PowerMonitoring;

/// <summary>
/// 功耗监控 (power monitoring) 功能配置。BYH 作为 HTTP 客户端，定时轮询用户已安装的
/// Libre Hardware Monitor 的 Web Server（默认 <c>http://localhost:8085/data.json</c>），
/// 解析 CPU/GPU/主板/电池功率与温度传感器，在托盘 tooltip 和设置页手机框的 4 个 view
/// (CPU/GPU/系统/能耗) 中展示，并梯形积分累计 Wh/kWh。BYH 不引用 LibreHardwareMonitorLib、
/// 不提权、不动 NativeAOT —— 纯 HTTP。镜像 <see cref="Speech.TtsSettings"/> 的 record 惯例：
/// <see cref="Default"/> / <see cref="Normalize"/> / <see cref="Validate"/>。
/// </summary>
public sealed record PowerMonitorSettings
{
    /// <summary>
    /// Master switch. When false, the polling loop never starts and the tray
    /// tooltip / phone views show no power data. Default false (opt-in) —— 用户
    /// 必须先装好 LHM 并开 Web Server，再在此显式启用。
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Libre Hardware Monitor 的 <c>data.json</c> 端点。LHM GUI: Options →
    /// (Remote) Web Server，默认端口 8085。必须是绝对 URL（http:// 或 https://）。
    /// </summary>
    public string Endpoint { get; init; } = "http://localhost:8085/data.json";

    /// <summary>
    /// 轮询间隔毫秒数。下限 1000（再快会被轮询自身的 CPU 功耗读数干扰，且加重 LHM
    /// 内核驱动负载），上限 60000。默认 3000（3 秒）—— 实测 LHM 在 2s 硬件采样档下
    /// CPU 约 0.24%，BYH 3s 轮询自身约 0.02%，合计对人眼实时性足够且不浪费。即便略
    /// 慢于 LHM 的 2s 采样也无所谓：BYH 拉的是 LHM 缓存的快照，偶尔重复值不影响梯形
    /// 积分精度（功率在秒级内不会狂跳）。配 LHM 的 2s 采样档（config key
    /// <c>updateIntervalMenuItem</c> = index 3）使用最经济。
    /// </summary>
    public int PollIntervalMs { get; init; } = 3000;

    /// <summary>
    /// 是否把 CPU/GPU 瓦数 + 累计 kWh 拼进托盘 tooltip（<c>BYH · CPU 45W · GPU 120W ·
    /// 1.234kWh</c>）。关掉则 tooltip 回到原始 <c>BYH · By Your Hand</c>。
    /// </summary>
    public bool ShowInTray { get; init; } = true;

    /// <summary>
    /// 是否累计能耗（梯形积分 Wh/kWh + 今日切片）。关闭时不积分、不落盘累计字段，
    /// 设置页的能耗 view 显示 —。CPU Package Power 等瞬时读数照常显示。
    /// </summary>
    public bool TrackEnergy { get; init; } = true;

    // ───────── 温度告警 ─────────

    /// <summary>
    /// 温度告警总开关。关闭时 <see cref="AlertEvaluator"/> 不评估任何指标，托盘
    /// tooltip 不出现 🔴 警告行，提示音不响。默认 false（opt-in）—— 告警是主动行为，
    /// 用户明确需要时再开。
    /// </summary>
    public bool AlertEnabled { get; init; } = false;

    /// <summary>
    /// CPU 温度告警阈值（°C）。超过即触发告警。默认 93（AMD Ryzen 7 6800H Tctl 强制
    /// 降频点 ~95，93 是"逼近降频红线"的预警点 —— 笔记本 6800H 散热激进，日常中载就
    /// 85-90°，但 93 仍属异常高负载，值得提醒）。Normalize 钳制到 50–110。
    /// </summary>
    public int CpuTempThresholdC { get; init; } = 93;

    /// <summary>
    /// GPU 温度告警阈值（°C）。默认 85（RTX 3060 Laptop GPU throttle ~87，85 是
    /// "逼近降频"的预警点）。Normalize 钳制到 50–110。iGPU 无温度传感器时此阈值不生效
    /// （自动跳过）。
    /// </summary>
    public int GpuTempThresholdC { get; init; } = 85;

    /// <summary>
    /// SSD 温度告警阈值（°C），同时管 SSD1 和 SSD2（取两盘最高温判定，任一超即触发）。
    /// 默认 73。依据用户机器实测：Micron 3400 512GB（系统盘）厂家 warning=79°，
    /// Geil P4A 2TB（数据盘）厂家 warning=99°（额定偏高，但闪存真实寿命 75°+ 已开始
    /// 下降）。73 对前者是 79°-6° 的合理预警，对后者是激进的早期保护。Normalize 钳制
    /// 到 40–90。
    /// </summary>
    public int SsdTempThresholdC { get; init; } = 73;

    // ───────── 历史时序存储 ─────────

    /// <summary>
    /// power-history.jsonl 历史保留天数。启动时按此裁剪早于 N 天的采样行。
    /// 默认 30 天。Normalize 钳制到 7–365（再短没查询价值，再长一年 42MB 也无妨）。
    /// </summary>
    public int HistoryRetentionDays { get; init; } = 30;

    public static PowerMonitorSettings Default { get; } = new();

    /// <summary>
    /// 返回一份副本，空字符串/越界数值恢复默认。镜像其它 settings record 的 Normalize 约定。
    /// </summary>
    public PowerMonitorSettings Normalize() => this with
    {
        Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? Default.Endpoint : Endpoint.Trim(),
        PollIntervalMs = PollIntervalMs <= 0
            ? Default.PollIntervalMs
            : Math.Min(Math.Max(PollIntervalMs, 1000), 60000),
        CpuTempThresholdC = Math.Min(Math.Max(CpuTempThresholdC, 50), 110),
        GpuTempThresholdC = Math.Min(Math.Max(GpuTempThresholdC, 50), 110),
        SsdTempThresholdC = Math.Min(Math.Max(SsdTempThresholdC, 40), 90),
        HistoryRetentionDays = Math.Min(Math.Max(HistoryRetentionDays, 7), 365),
    };

    /// <summary>断言 Normalize 后的不变量。</summary>
    public void Validate()
    {
        // 必须是 http/https 绝对 URL。注意 Uri.TryCreate 会把 "localhost:8085/..."
        // 当成合法绝对 URI（scheme="localhost"），所以必须再校验 scheme，否则用户漏写
        // http:// 时会得到一个看似合法但实际无法请求的端点。
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException(
                "PowerMonitor Endpoint 必须是 http/https 绝对 URL（如 http://localhost:8085/data.json）。",
                nameof(Endpoint));
        }
    }
}
