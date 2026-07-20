using System.Diagnostics;
using SelectionAssistant.Core.Launcher;

namespace SelectionAssistant.Platform.Windows.Launcher;

/// <summary>
/// Spawns local apps (via <see cref="Process.Start(ProcessStartInfo)"/>) and
/// opens URLs (via <c>UseShellExecute=true</c>, handing off to the system
/// default browser). Owns no state; safe to call from any thread.
/// </summary>
/// <remarks>
/// <para>
/// For local apps, <c>UseShellExecute=false</c> is used so that a
/// <see cref="LauncherEntry.WorkingDirectory"/> can be honored and arguments
/// are interpreted literally (no shell re-parsing). For URLs,
/// <c>UseShellExecute=true</c> is used because that's the only reliable way to
/// trigger the default browser on Windows without enumerating the registry.
/// </para>
/// <para>
/// This class never throws — failures return a string describing the error
/// (the caller surfaces it in the UI). Process startup errors (Win32
/// ERROR_FILE_NOT_FOUND etc.) are wrapped into the returned string.
/// </para>
/// </remarks>
public static class LauncherRunner
{
    /// <summary>
    /// Starts the entry with already-expanded arguments. Returns <c>null</c>
    /// on success, or an error message on failure.
    /// </summary>
    public static string? Start(LauncherEntry entry, string expandedArguments)
    {
        if (entry is null)
        {
            return "启动项为空。";
        }
        if (string.IsNullOrWhiteSpace(entry.Target))
        {
            return "启动项目标为空。";
        }

        try
        {
            bool isShellWrapper = entry.Kind == LauncherKind.LocalApp &&
                !string.IsNullOrEmpty(expandedArguments) &&
                (entry.Target.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
                 entry.Target.EndsWith("cmd", StringComparison.OrdinalIgnoreCase));

            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = entry.Kind == LauncherKind.WebUrl,
                WindowStyle = isShellWrapper ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                CreateNoWindow = isShellWrapper,
            };

            if (entry.Kind == LauncherKind.WebUrl)
            {
                // UseShellExecute=true → FileName is the URL, system opens it.
                startInfo.FileName = entry.Target;
            }
            else
            {
                // Local app: FileName=exe path, Arguments=expanded args.
                // UseShellExecute=false so we get literal arg passing.
                startInfo.FileName = entry.Target;
                if (!string.IsNullOrEmpty(expandedArguments))
                {
                    startInfo.Arguments = expandedArguments;
                }
                if (!string.IsNullOrWhiteSpace(entry.WorkingDirectory))
                {
                    startInfo.WorkingDirectory = entry.WorkingDirectory;
                }
            }

            Process.Start(startInfo);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or
                                       FileNotFoundException or
                                       System.IO.DirectoryNotFoundException or
                                       UnauthorizedAccessException or
                                       InvalidOperationException)
        {
            return $"启动失败：{ex.Message}";
        }
    }
}
