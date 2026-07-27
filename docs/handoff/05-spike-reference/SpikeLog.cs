using System;
using System.IO;

namespace SelectionSpike;

/// <summary>
/// 轻量诊断日志:同时写到 %TEMP%\SelectionSpike.log 和 Debug 输出。
/// Phase 0 spike 专用 —— 独立运行 exe 时唯一能看到的诊断手段。
/// </summary>
internal static class SpikeLog
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "SelectionSpike.log");

    public static void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            // AppendAllText 是线程安全的(内部用锁),钩子线程和 UI 线程都能写
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // 日志失败不能影响主程序
        }
    }

    public static string GetLogPath() => LogPath;
}
