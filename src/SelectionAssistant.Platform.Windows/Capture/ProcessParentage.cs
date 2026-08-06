using System.Runtime.InteropServices;
using System.Diagnostics;

namespace SelectionAssistant.Platform.Windows.Capture;

/// <summary>
/// 判断一个 PID 是否是另一个 PID 的后代，用于剪贴板属主校验时认套壳应用的子进程
/// （如 Weixin.exe → WeChatAppEx.exe、Electron 主进程 → renderer/GPU 子进程）。
/// 实现走 ntdll!NtQueryInformationProcess 拿 PROCESS_BASIC_INFORMATION 的
/// InheritedFromUniqueProcessId，向上最多 <paramref name="maxDepth"/> 跳。
/// 任意一次 OpenProcess / NtQuery 失败（受保护进程、进程已退出）返回 false：
/// 保守判定——查不动就当不是后代，由调用方按现有外部改动逻辑拒绝，零回归。
/// </summary>
internal static partial class ProcessParentage
{
    private const int WeChatFamilyMaxDepth = 8;

    // PROCESSINFOCLASS.ProcessBasicInformation == 0
    private const uint ProcessBasicInformation = 0;

    // PROCESS_QUERY_LIMITED_INFORMATION — 足够查 PBI，不需要更高权限，普通用户态进程都打的开。
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // 父链最大跳数。Weixin→WeChatAppEx 一跳；Electron 主→renderer 一跳；留 4 跳余量
    // 覆盖更深的中间 broker（如 Edge/Electron 的 crashpad/gpu broker）。过深会误把无关
    // 进程判为同源——但父链必然收敛到系统进程（smss/services），不会无限深。
    private const int DefaultMaxDepth = 4;

    /// <summary>
    /// 判断 <paramref name="candidatePid"/> 是否是 <paramref name="rootPid"/> 的后代。
    /// 相等视为后代（同一进程）。0 或不可打开视为非后代。
    /// </summary>
    public static bool IsDescendantOf(uint candidatePid, uint rootPid, int maxDepth = DefaultMaxDepth)
    {
        if (candidatePid == 0 || rootPid == 0)
        {
            return candidatePid != 0 && candidatePid == rootPid;
        }
        if (candidatePid == rootPid)
        {
            return true;
        }
        if (maxDepth <= 0)
        {
            return false;
        }

        uint current = candidatePid;
        for (int i = 0; i < maxDepth; i++)
        {
            uint? parent = GetParentPid(current);
            if (parent is null || parent == 0)
            {
                return false;
            }
            if (parent == rootPid)
            {
                return true;
            }
            if (parent == current)
            {
                // 自环保护（理论上不会发生，防御损坏的 PBI）。
                return false;
            }
            current = parent.Value;
        }
        return false;
    }

    /// <summary>
    /// Accepts the two process branches used by the current WeChat client.
    /// Public-account pages may run in <c>Weixin.exe --type=wxpublic</c> while
    /// the actual clipboard write is performed by the sibling
    /// <c>WeChatAppEx.exe</c> process. A strict descendant check rejects that
    /// legitimate hand-off, so allow a common ancestor only when both process
    /// names are known WeChat hosts. This remains deliberately narrower than a
    /// generic sibling-process rule.
    /// </summary>
    public static bool IsSameWeChatFamily(uint candidatePid, uint rootPid)
    {
        if (candidatePid == 0 || rootPid == 0)
        {
            return false;
        }

        if (IsDescendantOf(candidatePid, rootPid))
        {
            return true;
        }

        string? candidateName = TryGetProcessName(candidatePid);
        string? rootName = TryGetProcessName(rootPid);
        if (!IsWeChatHost(candidateName) || !IsWeChatHost(rootName))
        {
            return false;
        }

        HashSet<uint> rootChain = BuildAncestorChain(rootPid, WeChatFamilyMaxDepth);
        if (rootChain.Count == 0)
        {
            return false;
        }

        uint current = candidatePid;
        for (int depth = 0; depth <= WeChatFamilyMaxDepth; depth++)
        {
            if (rootChain.Contains(current))
            {
                return true;
            }

            uint? parent = GetParentPid(current);
            if (parent is null || parent == 0 || parent == current)
            {
                return false;
            }

            current = parent.Value;
        }

        return false;
    }

    private static HashSet<uint> BuildAncestorChain(uint pid, int maxDepth)
    {
        var chain = new HashSet<uint>();
        uint current = pid;
        for (int depth = 0; depth <= maxDepth && current != 0; depth++)
        {
            if (!chain.Add(current))
            {
                break;
            }

            uint? parent = GetParentPid(current);
            if (parent is null || parent == 0 || parent == current)
            {
                break;
            }

            current = parent.Value;
        }

        return chain;
    }

    private static string? TryGetProcessName(uint pid)
    {
        try
        {
            using Process process = Process.GetProcessById(checked((int)pid));
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWeChatHost(string? processName) =>
        processName is not null &&
        (processName.Equals("Weixin", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("WeChatAppEx", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 拿 <paramref name="pid"/> 的父 PID。失败（OpenProcess 被拒、NtQuery 返回非成功
    /// NTSTATUS、PBI 字段为空）返回 null。NtQueryInformationProcess 在 NativeAOT 下走
    /// DllImport（ntdll 不在已验证 LibraryImport 列表，且本签名无 string/StringBuilder）。
    /// </summary>
    private static uint? GetParentPid(uint pid)
    {
        nint handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, bInheritHandle: false, pid);
        if (handle == 0)
        {
            return null;
        }
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(
                handle,
                ProcessBasicInformation,
                ref pbi,
                Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
                out _);

            // NTSTATUS == 0 (STATUS_SUCCESS) 才可用；任何非 0（包括 STATUS_ACCESS_DENIED、
            // STATUS_INVALID_PARAMETER 等）都当失败。
            if (status != 0)
            {
                return null;
            }

            // PBI.InheritedFromUniqueProcessId 是 PVOID（在 64 位下是 IntPtr，承载 ULONG PID）。
            // 转成 uint 时做范围检查；0 表示没有父进程（如 System Idle Process）。
            long inherited = pbi.InheritedFromUniqueProcessId.ToInt64();
            if (inherited <= 0 || inherited > uint.MaxValue)
            {
                return null;
            }
            return (uint)inherited;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public nint ExitStatus;                  // PVOID — 未使用
        public nint PebBaseAddress;              // PPEB
        public nint AffinityMask;                // ULONG_PTR
        public nint BasePriority;                // KPRIORITY (LONG)
        public nint UniqueProcessId;             // ULONG，承载本进程 PID
        public nint InheritedFromUniqueProcessId; // ULONG，承载父进程 PID — 我们要的字段
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        nint ProcessHandle,
        uint ProcessInformationClass,
        ref PROCESS_BASIC_INFORMATION ProcessInformation,
        int ProcessInformationLength,
        out int ReturnLength);
}
