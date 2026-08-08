using System.Globalization;
using System.IO;
using System.Net.Http.Headers;
using System.Text.Json;
using SelectionAssistant.Core.PowerMonitoring;

namespace SelectionAssistant.Infrastructure.PowerMonitoring;

/// <summary>
/// HTTP 客户端：定时轮询用户已安装的 Libre Hardware Monitor 的 Web Server
/// （<c>http://localhost:8085/data.json</c>），用 <see cref="Utf8JsonReader"/> 递归遍历
/// LHM 的 JSON 传感器树，按 <c>Type</c> + <c>Label/Identifier</c> 前缀白名单匹配填充
/// <see cref="PowerSnapshot"/>。镜像 <c>MiniMaxTtsProvider</c> 的 HttpClient 模式：
/// sealed、own（可选注入）的 <see cref="HttpClient"/>、<see cref="IDisposable"/>、
/// <see cref="SendAsync"/> + <see cref="HttpCompletionOption.ResponseHeadersRead"/> + 链式
/// CTS <see cref="CancellationTokenSource.CancelAfter"/> 超时。
/// <para>
/// <b>AOT 安全</b>：全程 <see cref="Utf8JsonReader"/>（值类型、零反射），符合 BYH 铁律 #2。
/// 不反序列化为对象 —— 边读边匹配。失败/超时返回 <see cref="PowerSnapshot.Connected"/>=false。
/// </para><para>
/// <b>无鉴权</b>：LHM 的 Web Server 明文无鉴权，仅限本机回环或用户内网。
/// </para>
/// </summary>
public sealed class HttpPowerMonitorClient : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly TimeSpan _timeout;
    private int _disposed;

    public HttpPowerMonitorClient(HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    /// <summary>
    /// 读取一次 LHM <c>data.json</c> 并解析。任何失败（网络、超时、HTTP 错误、JSON 异常、
    /// 传感器不匹配）都返回 <see cref="PowerSnapshot.Connected"/>=false，<b>不</b>抛异常 ——
    /// 轮询循环把它当瞬时故障静默吞掉，下一个 tick 重试。CaptureAt 始终设为当前 UTC 时间，
    /// 即便断连（让 EnergyAccumulator 知道时间在走）。
    /// </summary>
    public async Task<PowerSnapshot> ReadAsync(string endpoint, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var snapshot = new PowerSnapshot { CapturedAt = DateTimeOffset.UtcNow };

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd("BYH/0.1 (power monitor)");
        request.Headers.Accept.ParseAdd("application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 超时（LHM 没开 Web Server / 端口不通）—— Connected=false。
            return snapshot;
        }
        catch (HttpRequestException)
        {
            // 连接被拒 / DNS / 端口错误。
            return snapshot;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return snapshot;
            }

            Stream stream;
            try
            {
                stream = await response.Content
                    .ReadAsStreamAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return snapshot;
            }
            catch (IOException)
            {
                return snapshot;
            }

            await using (stream)
            {
                try
                {
                    ParseDataJson(stream, ref snapshot);
                }
                catch (JsonException)
                {
                    // 解析失败 → 视为未连接，保留 Connected=false。
                    return snapshot;
                }
            }

            // 只要解析走完，就标记连接成功（即便部分传感器缺失，也说明 LHM 在线）。
            snapshot.Connected = true;
            return snapshot;
        }
    }

    /// <summary>
    /// 解析 LHM <c>data.json</c>。结构是递归的：根对象有 <c>Children</c> 数组，每个 child
    /// 又有自己的 <c>Children</c>（硬件树：/intelcpu/0 → /intelcpu/0/power/0 等）和
    /// <c>Sensors</c> 数组。每个 sensor: <c>Type</c>(Power/Temperature/Clock/Load/Fan/Voltage/Current)、
    /// <c>Label</c> 或 <c>Name</c>(显示名)、<c>Value</c>(字符串数值如 "45.3")、<c>Identifier</c>
    /// 或 <c>NodeId</c>(稳定路径如 "/intelcpu/0/power/0")。
    /// <para>
    /// 用 <see cref="JsonDocument"/> 而非裸 <see cref="Utf8JsonReader"/> —— 树结构递归遍历用
    /// JsonDocument 的 DOM 更清晰且仍是 AOT 安全（DOM 是 POCO，非反射）。值类型 Utf8JsonReader
    /// 手写递归容易出错。JsonDocument 在 .NET 10 + NativeAOT 下验证可用（MiniMaxTtsProvider
    /// 已用 <c>JsonDocument.ParseAsync</c>）。
    /// </para>
    /// </summary>
    public static void ParseDataJson(Stream stream, ref PowerSnapshot snapshot)
    {
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        Walk(root, ref snapshot);
    }

    private static void Walk(JsonElement node, ref PowerSnapshot snapshot)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // 路径 A：LHM 老版/部分版本把 sensor 放在显式的 Sensors 数组里。
        if (node.TryGetProperty("Sensors", out JsonElement sensors) &&
            sensors.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement sensor in sensors.EnumerateArray())
            {
                ClassifySensor(sensor, ref snapshot);
            }
        }

        // 路径 B：LHM data.json 的主流形态 —— sensor 是叶节点，直接作为容器节点（如
        // "Powers"/"Temperatures" 分组）的 Children 元素，带 Type/Value 字段。遍历 Children
        // 时对每个 child 判断：若有 Type 字段就是 sensor → 分类；否则当作容器继续递归。
        if (node.TryGetProperty("Children", out JsonElement children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object &&
                    child.TryGetProperty("Type", out JsonElement typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String)
                {
                    ClassifySensor(child, ref snapshot);
                }
                else
                {
                    Walk(child, ref snapshot);
                }
            }
        }
    }

    /// <summary>
    /// 把一个 sensor 元素分类填入 snapshot。匹配规则：先读 Type，再读 Label/Name + Identifier/NodeId，
    /// 按字段目标选择对应的写入方法。第一个命中即写入（LHM 可能同时报多个 CPU 核心温度，取首个）。
    /// </summary>
    private static void ClassifySensor(JsonElement sensor, ref PowerSnapshot snapshot)
    {
        if (!sensor.TryGetProperty("Type", out JsonElement typeEl) ||
            typeEl.ValueKind != JsonValueKind.String)
        {
            return;
        }
        string type = typeEl.GetString() ?? string.Empty;

        // Label 优先于 Name（LHM 新版）；都回退到 Text（老版/兼容）。
        string label = ReadNameProperty(sensor, "Label") ??
                       ReadNameProperty(sensor, "Name") ??
                       ReadNameProperty(sensor, "Text") ?? string.Empty;
        // Identifier / NodeId / SensorId —— LHM 不同版本用不同字段名。SensorId 是
        // 实测最常见（如 "/amdcpu/0/power/0"），用于区分 CPU/GPU/电池等硬件归属。
        string identifier = ReadNameProperty(sensor, "Identifier") ??
                            ReadNameProperty(sensor, "NodeId") ??
                            ReadNameProperty(sensor, "SensorId") ?? string.Empty;
        string? rawValue = ReadNameProperty(sensor, "Value");

        switch (type)
        {
            case "Power":
                ClassifyPower(label, identifier, rawValue, ref snapshot);
                break;
            case "Temperature":
                ClassifyTemperature(label, identifier, rawValue, ref snapshot);
                break;
            case "Clock":
                ClassifyClock(label, identifier, rawValue, ref snapshot);
                break;
            case "Load":
                ClassifyLoad(label, identifier, rawValue, ref snapshot);
                break;
            case "Fan":
                ClassifyFan(label, identifier, rawValue, ref snapshot);
                break;
        }
    }

    private static void ClassifyPower(string label, string identifier, string? rawValue, ref PowerSnapshot snapshot)
    {
        if (!TryParseDouble(rawValue, out double v))
        {
            return;
        }
        string low = label.ToLowerInvariant();
        string idLow = identifier.ToLowerInvariant();

        // GPU 功率先判（避免 "GPU Package" 被 CPU 的 "package" 规则抢先匹配）。
        // 匹配：SensorId 含 /gpu 或 /gpu-nvidia /gpu-amd；或 label 含 "gpu"。
        bool isGpu = idLow.Contains("/gpu") || low.Contains("gpu");
        if (snapshot.GpuPowerWatts is null && isGpu &&
            (low.Contains("package") || low.Contains("power") || low.Contains("board") || low.Contains("total")))
        {
            snapshot.GpuPowerWatts = v;
            return;
        }

        // CPU 封装功率（Intel "CPU Package"、AMD "Package"，SensorId 含 /cpu）。
        // 注意排除 GPU（已在上面 return 掉）。
        bool isCpu = idLow.Contains("/cpu") || idLow.Contains("/intelcpu") || idLow.Contains("/amdcpu") || low.Contains("cpu");
        if (snapshot.CpuPackageWatts is null && isCpu &&
            (low.Contains("package") || low.Contains("cpu package")))
        {
            snapshot.CpuPackageWatts = v;
            return;
        }
        // AMD 平台 label 可能只是 "Package" 且 SensorId 明确是 /amdcpu —— 上面 isCpu 靠 SensorId 命中。
        // 兜底：label 仅 "package" 且未匹配 GPU 时归 CPU。
        if (snapshot.CpuPackageWatts is null && !isGpu && low == "package")
        {
            snapshot.CpuPackageWatts = v;
            return;
        }
        // 主板各路
        if (snapshot.Rail12vWatts is null && (Contains(low, "#12v") || Contains(low, "+12v")))
        {
            snapshot.Rail12vWatts = v;
            return;
        }
        if (snapshot.Rail5vWatts is null && (Contains(low, "#5v") || Contains(low, "+5v")))
        {
            snapshot.Rail5vWatts = v;
            return;
        }
        if (snapshot.Rail3v3Watts is null && (Contains(low, "#3.3v") || Contains(low, "+3.3v")))
        {
            snapshot.Rail3v3Watts = v;
            return;
        }
        // 内存
        if (snapshot.RamWatts is null && (Contains(low, "memory") || Contains(low, "dram")))
        {
            snapshot.RamWatts = v;
            return;
        }
        // 电池（充放电）：label 是 "Charge/Discharge Rate"，靠 SensorId 含 /battery 判。
        if (snapshot.BatteryWatts is null &&
            (low.Contains("battery") || low.Contains("charge") || low.Contains("discharge") ||
             idLow.Contains("/battery")))
        {
            snapshot.BatteryWatts = v;
            return;
        }
    }

    private static void ClassifyTemperature(string label, string identifier, string? rawValue, ref PowerSnapshot snapshot)
    {
        if (!TryParseDouble(rawValue, out double v))
        {
            return;
        }
        string low = label.ToLowerInvariant();
        string idLow = identifier.ToLowerInvariant();
        bool isGpu = idLow.Contains("/gpu") || low.Contains("gpu");
        bool isCpu = idLow.Contains("/cpu") || idLow.Contains("/intelcpu") ||
                     idLow.Contains("/amdcpu") || low.Contains("cpu");
        // CPU 温度：label 含 "cpu"/"package"/"tctl"/"core"，且归属 CPU（靠 SensorId）。
        if (snapshot.CpuTempC is null && isCpu &&
            (low.Contains("cpu") || low.Contains("package") || low.Contains("tctl") ||
             low.Contains("core") || low.Contains("average")))
        {
            snapshot.CpuTempC = v;
            return;
        }
        if (snapshot.GpuTempC is null && isGpu &&
            (low.Contains("gpu") || low.Contains("core") || low.Contains("hotspot")))
        {
            snapshot.GpuTempC = v;
            return;
        }
        // SSD/NVMe 温度：SensorId 含 /nvme 或 /ssd 或 /hdd。多块盘时按设备索引
        // （/nvme/0、/nvme/1）分别填 Ssd1TempC / Ssd2TempC。跳过 Warning/Critical
        // 阈值传感器（它们是固定阈值不是实时读数）。优先取 Composite Temperature。
        bool isStorage = idLow.Contains("/nvme") || idLow.Contains("/ssd") ||
                         idLow.Contains("/hdd") || idLow.Contains("/disk");
        if (isStorage &&
            !low.Contains("warning") && !low.Contains("critical") &&
            !low.Contains("low limit") && !low.Contains("high limit"))
        {
            // Composite / 第一个 Temperature 优先。已经填过的同索引盘跳过（取首个温度传感器）。
            int devIdx = ExtractDeviceIndex(idLow);
            if (devIdx == 0 && !snapshot.Ssd1TempC.HasValue)
            {
                snapshot.Ssd1TempC = v;
            }
            else if (devIdx == 1 && !snapshot.Ssd2TempC.HasValue)
            {
                snapshot.Ssd2TempC = v;
            }
            else if (devIdx < 0)
            {
                // 无设备索引（/ssd 无编号）—— 当作第一块。
                if (!snapshot.Ssd1TempC.HasValue) snapshot.Ssd1TempC = v;
            }
            return;
        }
        // 内存条温度（"DIMM #1"，SensorId 含 /memory/dimm）。无则 null。
        if (snapshot.RamTempC is null &&
            (idLow.Contains("/memory/dimm") || idLow.Contains("/memory/") &&
             (low.Contains("dimm") || low.Contains("memory"))))
        {
            snapshot.RamTempC = v;
        }
    }

    /// <summary>从 SensorId 提取设备索引（如 "/nvme/1/temperature/0" → 1）。无索引返回 -1。</summary>
    private static int ExtractDeviceIndex(string idLow)
    {
        // 匹配 /nvme/N 或 /ssd/N 或 /hdd/N
        foreach (string prefix in new[] { "/nvme/", "/ssd/", "/hdd/", "/disk/" })
        {
            int i = idLow.IndexOf(prefix, StringComparison.Ordinal);
            if (i >= 0)
            {
                int digitStart = i + prefix.Length;
                if (digitStart < idLow.Length && char.IsDigit(idLow[digitStart]))
                {
                    return idLow[digitStart] - '0';
                }
            }
        }
        return -1;
    }

    private static void ClassifyClock(string label, string identifier, string? rawValue, ref PowerSnapshot snapshot)
    {
        if (!TryParseDouble(rawValue, out double v))
        {
            return;
        }
        string low = label.ToLowerInvariant();
        string idLow = identifier.ToLowerInvariant();
        bool isGpu = idLow.Contains("/gpu") || low.Contains("gpu");
        bool isCpu = idLow.Contains("/cpu") || idLow.Contains("/intelcpu") ||
                     idLow.Contains("/amdcpu") || low.Contains("cpu");
        // GPU 先判（"GPU Core" / "GPU Memory"）。
        if (snapshot.GpuMemClockMhz is null && isGpu && low.Contains("memory"))
        {
            snapshot.GpuMemClockMhz = v;
            return;
        }
        if (snapshot.GpuClockMhz is null && isGpu && (low.Contains("core") || low.Contains("gpu")))
        {
            snapshot.GpuClockMhz = v;
            return;
        }
        // CPU 频率：bus speed / core / 任意 CPU clock（取第一个 CPU clock 传感器）。
        if (snapshot.CpuClockMhz is null && isCpu &&
            (low.Contains("core") || low.Contains("bus") || low.Contains("cpu")))
        {
            snapshot.CpuClockMhz = v;
        }
    }

    private static void ClassifyLoad(string label, string identifier, string? rawValue, ref PowerSnapshot snapshot)
    {
        if (!TryParseDouble(rawValue, out double v))
        {
            return;
        }
        string low = label.ToLowerInvariant();
        string idLow = identifier.ToLowerInvariant();
        bool isGpu = idLow.Contains("/gpu") || low.Contains("gpu");
        bool isCpu = idLow.Contains("/cpu") || idLow.Contains("/intelcpu") ||
                     idLow.Contains("/amdcpu") || low.Contains("cpu");
        // GPU 负载先判。
        if (snapshot.GpuLoadPct is null && isGpu && (low.Contains("core") || low.Contains("gpu")))
        {
            snapshot.GpuLoadPct = v;
            return;
        }
        // CPU Core Max（最忙核心负载）先于 CPU Total 抓 —— label "CPU Core Max" 也含 "cpu"，
        // 若不先判会被 CpuTotal 规则吃掉。
        if (snapshot.CpuCoreMaxLoadPct is null && isCpu && low.Contains("core max"))
        {
            snapshot.CpuCoreMaxLoadPct = v;
            return;
        }
        if (snapshot.CpuLoadPct is null && isCpu && (low.Contains("total") || low.Contains("cpu")))
        {
            snapshot.CpuLoadPct = v;
        }
    }

    private static void ClassifyFan(string label, string identifier, string? rawValue, ref PowerSnapshot snapshot)
    {
        if (!TryParseDouble(rawValue, out double v))
        {
            return;
        }
        string low = label.ToLowerInvariant();
        int rpm = (int)Math.Round(v);
        if (snapshot.CpuFanRpm is null && (Contains(low, "cpu fan") || Contains(low, "fan #1")))
        {
            snapshot.CpuFanRpm = rpm;
            return;
        }
        if (snapshot.GpuFanRpm is null && Contains(low, "gpu fan"))
        {
            snapshot.GpuFanRpm = rpm;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string? ReadNameProperty(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return value.GetString();
    }

    /// <summary>
    /// 把 LHM 的字符串数值解析成 double。LHM 的 Value 字段<b>带单位</b>（如 "29.3 W"、
    /// "62.5 °C"、"2520.0 MHz"、"1450 RPM"、"19.0 %"），这里取开头第一个数值 token，
    /// 忽略后面的单位/百分号。"N/A"/"—"/"-" 等非数值返回 false。强制
    /// <see cref="CultureInfo.InvariantCulture"/>（LHM 输出用点作小数分隔符）。
    /// </summary>
    private static bool TryParseDouble(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        // LHM 偶尔输出 "-" 或 "N/A"，跳过。
        string trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed == "-" || trimmed == "N/A" || trimmed == "—")
        {
            return false;
        }
        // 取开头连续的数值字符（含小数点和正负号、e/E 科学计数法），忽略尾部单位。
        // 例如 "29.3 W" → "29.3"，"-12.5 W" → "-12.5"，"1.2e3 RPM" → "1200"。
        Span<char> num = stackalloc char[trimmed.Length];
        int n = 0;
        bool seenDigit = false;
        foreach (char c in trimmed)
        {
            if (char.IsDigit(c) || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E')
            {
                num[n++] = c;
                if (char.IsDigit(c)) seenDigit = true;
            }
            else
            {
                break; // 遇到第一个非数值字符（空格、字母、°、% 等）即停止。
            }
        }
        if (!seenDigit || n == 0)
        {
            return false;
        }
        return double.TryParse(num.Slice(0, n), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value);
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.Ordinal);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
