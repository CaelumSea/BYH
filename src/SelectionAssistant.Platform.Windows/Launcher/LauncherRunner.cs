using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SelectionAssistant.Core.Launcher;

namespace SelectionAssistant.Platform.Windows.Launcher;

/// <summary>
/// Spawns local apps (via <see cref="Process.Start(ProcessStartInfo)"/> and
/// <c>ShellExecuteEx</c>) and opens URLs (via <c>UseShellExecute=true</c>,
/// handing off to the system default browser). Owns no state; safe to call
/// from any thread.
/// </summary>
/// <remarks>
/// <para>
/// For local apps the launcher uses up to three strategies, tried in order:
/// </para>
/// <list type="number">
/// <item><term>Primary</term><description>
/// <c>Process.Start</c> with <c>UseShellExecute=false</c>. This honors a
/// user-supplied <see cref="LauncherEntry.WorkingDirectory"/> and passes
/// arguments literally (no shell re-parsing). Works for ~99% of apps.
/// </description></item>
/// <item><term>.lnk fallback</term><description>
/// When the target is a <c>.lnk</c> shortcut, <c>Process.Start(ProcessStartInfo)</c>
/// returns <c>ERROR_FILE_NOT_FOUND</c> regardless of <c>UseShellExecute</c> —
/// only the Explorer shell resolves shortcuts. We retry via
/// <c>ShellExecuteEx</c> with <c>verb="open"</c>, which is exactly what the
/// desktop double-click path does. The shortcut's stored arguments / working
/// directory are honored by the shell, and the user's expanded arguments are
/// appended as additional parameters. (Without this, entries that point at a
/// desktop shortcut — e.g. a skinned Codex launcher that wraps PowerShell —
/// can never be started.)
/// </description></item>
/// <item><term>Elevation fallback</term><description>
/// When the target exe's manifest declares <c>requireAdministrator</c>,
/// <c>Process.Start</c> from a non-elevated host returns
/// <c>ERROR_ELEVATION_REQUIRED (740)</c>. We retry via
/// <c>ShellExecuteEx</c> with <c>verb="runas"</c>, which triggers the
/// standard UAC consent prompt. This matches what happens when the user
/// right-clicks → "Run as administrator". If the user dismisses UAC the
/// shell returns <c>ERROR_CANCELLED (1223)</c>, which we surface as a
/// clear "user cancelled" message rather than a generic failure.
/// </description></item>
/// </list>
/// <para>
/// For URLs, <c>UseShellExecute=true</c> is used because that's the only
/// reliable way to trigger the default browser on Windows without enumerating
/// the registry.
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
    /// Win32 ERROR_ELEVATION_REQUIRED — the target exe's manifest requests
    /// admin and the host process isn't elevated.
    /// </summary>
    private const int ErrorElevationRequired = 740;

    /// <summary>
    /// Win32 ERROR_CANCELLED — the user clicked "No" on the UAC consent dialog.
    /// </summary>
    private const int ErrorCancelled = 1223;

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

        if (entry.Kind == LauncherKind.WebUrl)
        {
            return StartWebUrl(entry);
        }

        // Primary path: literal arg passing + honored WorkingDirectory.
        string? primaryError = StartLocalAppPrimary(entry, expandedArguments);
        if (primaryError is null)
        {
            return null;
        }

        // Decide whether to fall back to ShellExecuteEx based on the failure
        // mode. We only retry for the two cases the shell actually handles
        // differently from CreateProcess:
        //   - target is a .lnk shortcut (Explorer-only resolution)
        //   - 740 ERROR_ELEVATION_REQUIRED (need UAC consent)
        // Everything else (file genuinely missing, bad path, access denied
        // for non-elevation reasons) returns the original error.
        if (DecideFallback(entry.Target, primaryError) is not { } verb)
        {
            return primaryError;
        }

        string? fallbackError = StartViaShellExecuteEx(entry, expandedArguments, verb);
        // Map the common "user dismissed UAC" case to a clearer message so
        // the UI doesn't show a confusing raw Win32 code.
        if (fallbackError is not null
            && TryGetWin32ErrorCode(fallbackError, out int fallbackCode)
            && fallbackCode == ErrorCancelled)
        {
            return "用户取消了授权。";
        }
        return fallbackError;
    }

    /// <summary>
    /// Primary launch path for LocalApp: <see cref="Process.Start(ProcessStartInfo)"/>
    /// with <c>UseShellExecute=false</c>. Honors WorkingDirectory and passes
    /// arguments literally. Returns <c>null</c> on success, an error string
    /// (prefixed with the Win32 error code when available) on failure.
    /// </summary>
    private static string? StartLocalAppPrimary(LauncherEntry entry, string expandedArguments)
    {
        try
        {
            bool isShellWrapper = !string.IsNullOrEmpty(expandedArguments)
                && (entry.Target.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase)
                    || entry.Target.EndsWith("cmd", StringComparison.OrdinalIgnoreCase));

            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                WindowStyle = isShellWrapper ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                CreateNoWindow = isShellWrapper,
                FileName = entry.Target,
            };
            if (!string.IsNullOrEmpty(expandedArguments))
            {
                startInfo.Arguments = expandedArguments;
            }
            if (!string.IsNullOrWhiteSpace(entry.WorkingDirectory))
            {
                startInfo.WorkingDirectory = entry.WorkingDirectory;
            }

            Process.Start(startInfo);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception
                                       or FileNotFoundException
                                       or System.IO.DirectoryNotFoundException
                                       or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            return FormatStartError(ex);
        }
    }

    /// <summary>
    /// Opens a URL via the system default browser (<c>UseShellExecute=true</c>).
    /// </summary>
    private static string? StartWebUrl(LauncherEntry entry)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = entry.Target,
            };
            Process.Start(startInfo);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception
                                       or FileNotFoundException
                                       or System.IO.DirectoryNotFoundException
                                       or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            return FormatStartError(ex);
        }
    }

    /// <summary>
    /// Fallback launch path that goes through the Explorer shell
    /// (<c>ShellExecuteEx</c>). Used for <c>.lnk</c> shortcuts (which only
    /// the shell resolves) and for elevation (via <c>verb="runas"</c>,
    /// which triggers the UAC consent prompt).
    /// </summary>
    /// <param name="verb"><c>"open"</c> for normal shell launch, <c>"runas"</c> for UAC elevation.</param>
    /// <returns><c>null</c> on success; an error string (prefixed with the Win32 code) on failure.</returns>
    private static string? StartViaShellExecuteEx(LauncherEntry entry, string expandedArguments, string verb)
    {
        // When the target is a .lnk, the user's expanded arguments must be
        // passed via lpParameters; the shell appends them after the shortcut's
        // own stored arguments. For a plain .exe the same field is the only
        // way to pass args under ShellExecuteEx.
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_NOCLOSEPROCESS,
            lpVerb = verb,
            lpFile = entry.Target,
            lpParameters = string.IsNullOrEmpty(expandedArguments) ? null : expandedArguments,
            lpDirectory = string.IsNullOrWhiteSpace(entry.WorkingDirectory) ? null : entry.WorkingDirectory,
            nShow = SW_SHOWNORMAL,
        };

        if (!ShellExecuteEx(ref info))
        {
            int err = Marshal.GetLastWin32Error();
            return $"[Win32 {err}] 启动失败：{GetWin32ErrorMessage(err)}";
        }

        // Audit H1: SEE_MASK_NOCLOSEPROCESS asks the shell to populate
        // hProcess with a handle to the newly launched process. The OS
        // grants us this handle (kernel object, reference count bumped);
        // we never wait on it, so we MUST release our reference or it
        // leaks on every launcher invocation. (We don't own the process
        // lifetime — the user does — we only own the handle.)
        if (info.hProcess != IntPtr.Zero)
        {
            CloseHandle(info.hProcess);
        }
        return null;
    }

    /// <summary>
    /// Formats an exception thrown by <see cref="Process.Start(ProcessStartInfo)"/>
    /// into an error string, prefixing the Win32 error code when the exception
    /// is a <see cref="Win32Exception"/>. The code prefix lets the caller
    /// distinguish failure modes (740 = needs elevation, 2 = file not found, …)
    /// without parsing the localized message.
    /// </summary>
    private static string FormatStartError(Exception ex)
    {
        if (ex is Win32Exception w32)
        {
            return $"[Win32 {w32.NativeErrorCode}] 启动失败：{w32.Message}";
        }
        return $"启动失败：{ex.Message}";
    }

    /// <summary>
    /// Parses the <c>[Win32 N]</c> prefix that <see cref="FormatStartError"/>
    /// writes, returning the numeric code. Returns <c>false</c> if the prefix
    /// is missing or malformed (e.g. for exceptions that aren't
    /// <see cref="Win32Exception"/>, or strings that don't follow the
    /// <c>[Win32 &lt;digits&gt;]</c> shape).
    /// </summary>
    /// <example>
    /// <c>"[Win32 740] 启动失败：..."</c> → returns <c>true</c> with <c>code=740</c>.<br/>
    /// <c>"[Win32 not-a-number] ..."</c> → returns <c>false</c>.<br/>
    /// <c>"启动失败：foo"</c> → returns <c>false</c>.
    /// </example>
    private static bool TryGetWin32ErrorCode(string error, out int code)
    {
        code = 0;
        const string prefix = "[Win32 ";
        const string suffix = "]";
        if (string.IsNullOrEmpty(error) || !error.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        // Find the closing "]" of the "[Win32 N]" tag.
        int close = error.IndexOf(suffix, prefix.Length, StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }
        // Slice the digits between "[Win32 " and "]".
        ReadOnlySpan<char> digits = error.AsSpan(prefix.Length, close - prefix.Length);
        return digits.Length > 0 && int.TryParse(digits, out code);
    }

    /// <summary>
    /// Decides whether to retry a failed primary launch via
    /// <see cref="ShellExecuteEx"/>, and with which verb. Returns:
    /// <list type="bullet">
    /// <item><c>"runas"</c> — primary failed with <c>ERROR_ELEVATION_REQUIRED (740)</c>;
    ///     the shell will show the UAC consent prompt.</item>
    /// <item><c>"open"</c> — target is a <c>.lnk</c> shortcut (which only the
    ///     shell resolves); do a normal shell open like Explorer.</item>
    /// <item><c>null</c> — failure isn't one the shell handles differently
    ///     (genuinely missing file, non-elevation access denied, etc.); return
    ///     the original error without retrying.</item>
    /// </list>
    /// Extracted as a pure function so the routing logic can be unit-tested
    /// without actually launching processes or triggering UAC.
    /// </summary>
    internal static string? DecideFallback(string target, string primaryError)
    {
        bool isShortcut = target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        bool needsElevation = TryGetWin32ErrorCode(primaryError, out int code)
            && code == ErrorElevationRequired;
        if (!isShortcut && !needsElevation)
        {
            return null;
        }
        return needsElevation ? "runas" : "open";
    }

    /// <summary>
    /// Looks up the human-readable message for a Win32 error code. Used so
    /// <see cref="StartViaShellExecuteEx"/> (which gets a bare int from
    /// <see cref="Marshal.GetLastWin32Error"/>) can produce a message of the
    /// same quality as <see cref="Win32Exception.Message"/>.
    /// </summary>
    private static string GetWin32ErrorMessage(int code) =>
        new Win32Exception(code).Message;

    // ===== ShellExecuteEx P/Invoke =====

    private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    private const int SW_SHOWNORMAL = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
