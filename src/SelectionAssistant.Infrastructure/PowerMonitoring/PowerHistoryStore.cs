using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SelectionAssistant.Infrastructure.PowerMonitoring;

/// <summary>
/// power-history.jsonl 的追加/裁剪/清除/元信息。每分钟由
/// <c>SelectionRuntime.AppendHistorySampleIfDue</c> 调 <see cref="Append"/> 写一行；
/// 启动时调 <see cref="TrimByAge"/> 按保留天数删除旧行；设置页"清除"按钮调 <see cref="Clear"/>。
/// <para>
/// <b>线程安全</b>：<see cref="Append"/> 与 <see cref="Clear"/> 用 <see cref="_gate"/>
/// 互斥；只有轮询线程写、UI 线程读元信息，竞争极轻。镜像 <c>RedactedLogger</c> 的
/// <c>File.AppendAllText + lock</c> 模式。
/// </para>
/// <para>
/// <b>AOT 安全</b>：读取历史行解析 ts 用 <see cref="JsonDocument"/>（已是 BYH 全代码库
/// 的惯例，AOT 兼容）；写出用 <see cref="PowerHistorySample.ToJsonLine"/> 的手写
/// <see cref="Utf8JsonWriter"/>。无反射。
/// </para>
/// <para>
/// <b>失败静默</b>：Append/Trim/Clear 的 IO 异常一律吞掉（best-effort），因为历史存储
/// 是辅助功能，决不能让磁盘问题崩掉监控主路径。GetInfo 同理，失败返回零值。
/// </para>
/// </summary>
public static class PowerHistoryStore
{
    private static readonly object _gate = new();

    /// <summary>
    /// 追加一行历史采样。自动创建文件/目录。失败静默（吞掉 IOException）。
    /// </summary>
    public static void Append(string path, in PowerHistorySample sample)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            lock (_gate)
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                // 每行 = 紧凑 JSON + 换行。AppendAllText 是 OS 缓冲的原子追加。
                File.AppendAllText(path, sample.ToJsonLine() + "\n", Encoding.UTF8);
            }
        }
        catch
        {
            // 磁盘满/权限/占用 —— 历史是辅助功能，吞掉不崩监控。
        }
    }

    /// <summary>
    /// 删除所有早于 <c>now - retentionDays</c> 的采样行。逐行解析 ts 字段判定保留与否，
    /// 保留的行写回临时文件再原子替换。启动时调用一次，避免追加热路径做 IO。
    /// <b>不</b>在每次 Append 后调用。失败静默。
    /// </summary>
    /// <returns>删除的行数（若失败则为 0）。</returns>
    public static int TrimByAge(string path, int retentionDays, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (retentionDays <= 0 || !File.Exists(path))
        {
            return 0;
        }
        DateTimeOffset cutoff = now.AddDays(-retentionDays);
        int removed = 0;
        try
        {
            lock (_gate)
            {
                if (!File.Exists(path)) return 0;

                var keep = new List<string>(capacity: 4096);
                int totalLines = 0;
                foreach (string rawLine in File.ReadLines(path))
                {
                    totalLines++;
                    string line = rawLine.Trim();
                    if (line.Length == 0)
                    {
                        continue; // 丢弃已有空行
                    }
                    DateTimeOffset? ts = TryParseTimestamp(line);
                    if (ts.HasValue && ts.Value < cutoff)
                    {
                        removed++;
                        continue; // 早于 cutoff → 删除
                    }
                    keep.Add(rawLine);
                }

                if (removed == 0)
                {
                    return 0; // 没有要删的，不重写文件
                }

                // 原子写回：临时文件 + Move 覆盖。保留的行原样写回（含原始换行）。
                string tempPath = path + ".trim.tmp";
                try
                {
                    File.WriteAllLines(tempPath, keep, Encoding.UTF8);
                    File.Move(tempPath, path, overwrite: true);
                }
                catch
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    // 写回失败：把 removed 归零，不谎报成功。
                    return 0;
                }
                return removed;
            }
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 清空历史文件（删除）。设置页"立即清除历史"按钮调用。失败静默。
    /// </summary>
    public static void Clear(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            lock (_gate)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// 返回历史文件的元信息（路径、是否存在、字节数、采样行数）。设置页打开时调用一次
    /// 显示"当前大小 / 采样点数"。失败返回零值（不抛）。
    /// </summary>
    public static (bool Exists, long Bytes, int Samples) GetInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (!File.Exists(path))
            {
                return (false, 0, 0);
            }
            FileInfo info = new(path);
            long bytes = info.Length;
            int samples = 0;
            // 只数非空行（一行 = 一个采样点）。不解析 JSON，纯行计数，快。
            foreach (string line in File.ReadLines(path))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    samples++;
                }
            }
            return (true, bytes, samples);
        }
        catch
        {
            return (false, 0, 0);
        }
    }

    /// <summary>
    /// 从一行 JSONL 中提取 <c>"ts"</c> 字段并解析为 <see cref="DateTimeOffset"/>。
    /// 解析失败（格式错/无 ts 字段）返回 null —— 调用方据此决定保留（无法判定时间的行
    /// 保守保留，避免误删）。
    /// </summary>
    private static DateTimeOffset? TryParseTimestamp(string jsonLine)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonLine);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("ts", out JsonElement tsElem) &&
                tsElem.ValueKind == JsonValueKind.String)
            {
                string? ts = tsElem.GetString();
                if (!string.IsNullOrEmpty(ts) &&
                    DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset result))
                {
                    return result;
                }
            }
        }
        catch
        {
            // JsonException / ArgumentException —— 视为无法解析，返回 null
        }
        return null;
    }
}
