using System.Diagnostics;
using System.Runtime.InteropServices;
using SelectionAssistant.Core.Capture;

namespace SelectionAssistant.Platform.Windows.Capture;

public sealed class WindowsProcessIdentityResolver : IProcessIdentityResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    public WindowsProcessIdentityResolver()
    {
        IsCurrentProcessElevated = ReadElevation((uint)Environment.ProcessId);
    }

    public bool IsCurrentProcessElevated { get; }

    public ProcessIdentity Resolve(uint processId)
    {
        if (processId == 0)
        {
            return new ProcessIdentity(0, null, null);
        }

        string? processName = null;
        string? executablePath = null;
        try
        {
            using Process process = Process.GetProcessById(checked((int)processId));
            processName = process.ProcessName;
            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch
            {
                // Protected/cross-integrity processes may deny module access.
            }
        }
        catch
        {
            // The process may exit between the gesture and policy resolution.
        }

        return new ProcessIdentity(
            processId,
            processName,
            executablePath,
            IsElevated: ReadElevation(processId));
    }

    private static bool ReadElevation(uint processId)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return false;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out nint token))
            {
                return false;
            }

            try
            {
                return GetTokenInformation(
                    token,
                    TokenElevation,
                    out TokenElevationInfo elevation,
                    Marshal.SizeOf<TokenElevationInfo>(),
                    out _) && elevation.TokenIsElevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevationInfo
    {
        public int TokenIsElevated;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        out TokenElevationInfo tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
