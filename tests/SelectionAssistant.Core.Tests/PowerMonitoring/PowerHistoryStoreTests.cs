using System.Text.Json;
using SelectionAssistant.Infrastructure.PowerMonitoring;
using Xunit;

namespace SelectionAssistant.Core.Tests.PowerMonitoring;

/// <summary>
/// PowerHistoryStore 测试：Append 写行、多次 Append 多行、TrimByAge 删旧、Clear 清空、
/// GetInfo 返回大小/行数。每个测试用独立临时文件，测完清理。
/// </summary>
public sealed class PowerHistoryStoreTests : IDisposable
{
    private readonly string _path;

    public PowerHistoryStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"byh-history-test-{Guid.NewGuid():N}.jsonl");
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    private static PowerHistorySample Sample(
        DateTimeOffset ts, double totalW,
        double? cpuT = null, double? ssd1T = null, double? ssd2T = null) => new()
    {
        CapturedAt = ts,
        TotalWatts = totalW,
        CpuTempC = cpuT,
        Ssd1TempC = ssd1T,
        Ssd2TempC = ssd2T,
    };

    [Fact]
    public void Append_CreatesFile_WithOneLine()
    {
        PowerHistoryStore.Append(_path, Sample(DateTimeOffset.UtcNow, 50.0, cpuT: 70));

        Assert.True(File.Exists(_path));
        string[] lines = File.ReadAllLines(_path);
        Assert.Single(lines);
        Assert.Contains("\"totalW\":50", lines[0]);
        Assert.Contains("\"cpuT\":70", lines[0]);
    }

    [Fact]
    public void Append_MultipleTimes_MultipleLines()
    {
        var baseTime = DateTimeOffset.Parse("2026-08-07T10:00:00+08:00", System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < 5; i++)
        {
            PowerHistoryStore.Append(_path, Sample(baseTime.AddMinutes(i), 50 + i));
        }

        string[] lines = File.ReadAllLines(_path);
        Assert.Equal(5, lines.Length);
        Assert.Contains("\"totalW\":50", lines[0]);
        Assert.Contains("\"totalW\":54", lines[4]);
    }

    [Fact]
    public void Append_NullFields_OmittedFromJson()
    {
        // cpuT/ssd2T 不给值 → 不应出现 cpuT/ssd2T key。
        PowerHistoryStore.Append(_path, Sample(DateTimeOffset.UtcNow, 42.0, ssd1T: 55));

        string line = File.ReadAllText(_path).Trim();
        Assert.DoesNotContain("cpuT", line);
        Assert.DoesNotContain("ssd2T", line);
        Assert.Contains("ssd1T", line);
        Assert.Contains("totalW", line);
    }

    [Fact]
    public void TrimByAge_DeletesOldSamples()
    {
        var now = DateTimeOffset.Parse("2026-08-07T12:00:00+08:00", System.Globalization.CultureInfo.InvariantCulture);
        // 5 行：3 分钟前、1 天前、2 天前、35 天前、36 天前
        PowerHistoryStore.Append(_path, Sample(now.AddMinutes(-3), 50));
        PowerHistoryStore.Append(_path, Sample(now.AddDays(-1), 50));
        PowerHistoryStore.Append(_path, Sample(now.AddDays(-2), 50));
        PowerHistoryStore.Append(_path, Sample(now.AddDays(-35), 50));
        PowerHistoryStore.Append(_path, Sample(now.AddDays(-36), 50));

        int removed = PowerHistoryStore.TrimByAge(_path, retentionDays: 30, now: now);

        Assert.Equal(2, removed); // 35 天、36 天两行被删
        var info = PowerHistoryStore.GetInfo(_path);
        Assert.Equal(3, info.Samples); // 剩 3 分钟、1 天、2 天三行
    }

    [Fact]
    public void TrimByAge_KeepAll_WhenNothingOld()
    {
        var now = DateTimeOffset.Parse("2026-08-07T12:00:00+08:00", System.Globalization.CultureInfo.InvariantCulture);
        PowerHistoryStore.Append(_path, Sample(now.AddMinutes(-5), 50));
        PowerHistoryStore.Append(_path, Sample(now.AddMinutes(-10), 50));

        int removed = PowerHistoryStore.TrimByAge(_path, retentionDays: 30, now: now);

        Assert.Equal(0, removed);
        Assert.Equal(2, PowerHistoryStore.GetInfo(_path).Samples);
    }

    [Fact]
    public void TrimByAge_DoesNotRewrite_WhenNothingRemoved()
    {
        // 没有要删的行时，不应触碰文件（mtime 不变）。
        var now = DateTimeOffset.Parse("2026-08-07T12:00:00+08:00", System.Globalization.CultureInfo.InvariantCulture);
        PowerHistoryStore.Append(_path, Sample(now.AddMinutes(-5), 50));
        DateTime beforeWrite = File.GetLastWriteTime(_path);
        Thread.Sleep(50); // 确保 mtime 能区分

        int removed = PowerHistoryStore.TrimByAge(_path, retentionDays: 30, now: now);

        Assert.Equal(0, removed);
        DateTime afterWrite = File.GetLastWriteTime(_path);
        Assert.Equal(beforeWrite, afterWrite);
    }

    [Fact]
    public void TrimByAge_MissingFile_ReturnsZero()
    {
        int removed = PowerHistoryStore.TrimByAge(_path + ".nonexistent", 30, DateTimeOffset.UtcNow);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void Clear_DeletesFile()
    {
        PowerHistoryStore.Append(_path, Sample(DateTimeOffset.UtcNow, 50));
        Assert.True(File.Exists(_path));

        PowerHistoryStore.Clear(_path);

        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Clear_MissingFile_NoThrow()
    {
        // 删除不存在的文件不应抛异常。
        string missing = _path + ".missing";
        PowerHistoryStore.Clear(missing); // 不抛即通过
    }

    [Fact]
    public void GetInfo_ReturnsSizeAndSampleCount()
    {
        PowerHistoryStore.Append(_path, Sample(DateTimeOffset.UtcNow, 50, cpuT: 70));
        PowerHistoryStore.Append(_path, Sample(DateTimeOffset.UtcNow, 51, cpuT: 71));

        var (exists, bytes, samples) = PowerHistoryStore.GetInfo(_path);

        Assert.True(exists);
        Assert.True(bytes > 0, "字节数应 > 0");
        Assert.Equal(2, samples);
    }

    [Fact]
    public void GetInfo_MissingFile_ReturnsFalse()
    {
        var (exists, bytes, samples) = PowerHistoryStore.GetInfo(_path + ".missing");
        Assert.False(exists);
        Assert.Equal(0, bytes);
        Assert.Equal(0, samples);
    }

    [Fact]
    public void ToJsonLine_RoundTrips_Timestamp()
    {
        // 验证 ts 字段格式能被 JsonDocument 解析回正确时间（端到端一致性）。
        // 注意：Utf8JsonWriter 会把 ISO 时间里的 "+" 转义成 \u002B（JSON 安全行为），
        // 这是合法的 —— 任何 JSON parser 都会还原，TrimByAge 的 JsonDocument.Parse 也能读。
        var ts = DateTimeOffset.Parse("2026-08-07T22:15:03+08:00", System.Globalization.CultureInfo.InvariantCulture);
        var sample = Sample(ts, 54.2);

        string line = sample.ToJsonLine();

        // 用 JsonDocument 解析回来，确认 ts round-trip 正确。
        using JsonDocument doc = JsonDocument.Parse(line);
        string? tsStr = doc.RootElement.GetProperty("ts").GetString();
        DateTimeOffset parsed = DateTimeOffset.Parse(tsStr, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(ts, parsed);
        // totalW 也在
        Assert.Equal(54.2, doc.RootElement.GetProperty("totalW").GetDouble());
    }
}
