using System.Globalization;
using System.Text.Json;

namespace SelectionAssistant.Infrastructure.PowerMonitoring;

/// <summary>
/// power-history.jsonl 中一行历史采样点的内存表示。是 <see cref="PowerSnapshot"/>
/// 的精简子集（9 个字段），只保留事后查询有用的：时间戳 + 总功率 + 各元件功率/温度。
/// <para>
/// 每分钟落盘一行（由 <c>SelectionRuntime.AppendHistorySampleIfDue</c> 在轮询循环里
/// 60s 节流触发）。文件格式 JSONL（每行一个紧凑 JSON 对象，无缩进）。
/// </para>
/// <para>
/// 缺字段的传感器（如 GPU 温度掉线）对应属性为 null，序列化时省略该 key（不写出），
/// 这样文件更紧凑、AI 全表扫时也能正确识别缺失。
/// </para>
/// </summary>
public readonly record struct PowerHistorySample
{
    /// <summary>采样时刻（UTC + 偏移），序列化为 ISO 8601 round-trip 字符串。</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>瞬时总功率（主要元件之和）。非空。</summary>
    public required double TotalWatts { get; init; }

    public double? CpuPackageWatts { get; init; }
    public double? GpuPowerWatts { get; init; }
    public double? CpuTempC { get; init; }
    public double? GpuTempC { get; init; }
    public double? RamTempC { get; init; }
    public double? Ssd1TempC { get; init; }
    public double? Ssd2TempC { get; init; }

    /// <summary>从完整 PowerSnapshot 投影出一个历史采样点（只取需要的 9 个字段）。</summary>
    public static PowerHistorySample FromSnapshot(in PowerSnapshot snap) => new()
    {
        CapturedAt = snap.CapturedAt,
        TotalWatts = snap.TotalWatts,
        CpuPackageWatts = snap.CpuPackageWatts,
        GpuPowerWatts = snap.GpuPowerWatts,
        CpuTempC = snap.CpuTempC,
        GpuTempC = snap.GpuTempC,
        RamTempC = snap.RamTempC,
        Ssd1TempC = snap.Ssd1TempC,
        Ssd2TempC = snap.Ssd2TempC,
    };

    /// <summary>
    /// 把本采样点序列化为一行紧凑 JSON（无换行尾，调用方追加 <c>\n</c>）。手写
    /// <see cref="Utf8JsonWriter"/> 而非反射式序列化，符合 NativeAOT 铁律。null 字段
    /// 省略 key。紧凑 key 名（<c>ts/cpuW/gpuW/cpuT/...</c>）省字节 —— 每行约 120B。
    /// <para>
    /// <b>编码器</b>：用 <see cref="System.Text.Encodings.Web.JavaScriptEncoder"/>'s
    /// <c>UnsafeRelaxedJsonEscaping</c> 放宽转义，让 ISO 时间里的 <c>+</c> 原样输出
    /// （而非 <c>\u002B</c>），提升 jsonl 文件人眼/AI 可读性。安全：这是本地内部文件，
    /// 不面向 HTML/web 注入场景。
    /// </para>
    /// </summary>
    public string ToJsonLine()
    {
        using var buffer = new MemoryStream(192);
        var options = new JsonWriterOptions
        {
            Indented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", CapturedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("totalW", TotalWatts);
            if (CpuPackageWatts.HasValue) writer.WriteNumber("cpuW", CpuPackageWatts.Value);
            if (GpuPowerWatts.HasValue) writer.WriteNumber("gpuW", GpuPowerWatts.Value);
            if (CpuTempC.HasValue) writer.WriteNumber("cpuT", CpuTempC.Value);
            if (GpuTempC.HasValue) writer.WriteNumber("gpuT", GpuTempC.Value);
            if (RamTempC.HasValue) writer.WriteNumber("ramT", RamTempC.Value);
            if (Ssd1TempC.HasValue) writer.WriteNumber("ssd1T", Ssd1TempC.Value);
            if (Ssd2TempC.HasValue) writer.WriteNumber("ssd2T", Ssd2TempC.Value);
            writer.WriteEndObject();
            writer.Flush();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
