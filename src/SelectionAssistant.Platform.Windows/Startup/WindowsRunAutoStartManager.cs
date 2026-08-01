using System.Runtime.Versioning;
using Microsoft.Win32;
using SelectionAssistant.Platform.Abstractions.Startup;

namespace SelectionAssistant.Platform.Windows.Startup;

/// <summary>
/// Windows 「开机自启」实现:在
/// <c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run</c> 写一个名为
/// <c>BYH</c> 的字符串值,数据为当前 exe 的完整路径(带引号,处理路径含空格)。
/// 用户级 hive,无需管理员权限。
/// <para>
/// <b>真相源是注册表,不是缓存。</b>用户可能在任务管理器「启动」页或 Windows
/// 设置里手动禁用过自启项(那会让 Run 键值被禁用标识覆盖,或被第三方启动管理器
/// 拦截)。本类只负责 Run 键值的读 / 写 / 删;系统级是否真的开机拉起取决于
/// Windows 与启动管理器,这一点和所有自启工具一致。
/// </para>
/// <para>
/// <b>路径校验。</b><see cref="IsEnabled"/> 不仅看值是否存在,还比对路径是否等于
/// 当前进程 exe——exe 被移动 / 重命名后,旧的自启项视为失效,避免开机时启动一个
/// 不存在的路径。
/// </para>
/// <para>
/// 所有写操作吞掉异常返回 bool——组策略、受控文件夹访问、杀毒软件都可能拒绝写
/// Run 键,此时 UI 需要提示「启用失败」而非崩溃。AOT 安全:
/// <see cref="Microsoft.Win32.Registry"/> 在 <c>net10.0-windows</c> TFM 下开箱可用。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRunAutoStartManager : IAutoStartManager
{
    /// <summary>用户级 Run 键。HKCU 无需管理员。</summary>
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Run 键里 BYH 条目的名字。</summary>
    public const string ValueName = "BYH";

    private readonly string _runKeyPath;
    private readonly string? _expectedExePath;

    /// <summary>
    /// 用默认 HKCU Run 键和当前进程 exe 路径构造。生产路径。
    /// </summary>
    public WindowsRunAutoStartManager()
        : this(RunKeyPath, Environment.ProcessPath)
    {
    }

    /// <summary>
    /// 内部 / 测试构造器:允许指定 Run 键路径(测试可指向临时子键,不动真 Run 键)
    /// 和期望 exe 路径(测试可传固定值)。<paramref name="runKeyPath" /> 是相对
    /// HKCU 的子树路径;<paramref name="expectedExePath" /> 为 null 时只判值是否存在。
    /// </summary>
    internal WindowsRunAutoStartManager(string runKeyPath, string? expectedExePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKeyPath);
        _runKeyPath = runKeyPath;
        _expectedExePath = expectedExePath;
    }

    /// <inheritdoc />
    /// <remarks>读 HKCU Run 键;若 OpenSubKey 失败(键不存在)视为未启用。</remarks>
    public bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: false);
            if (key is null)
            {
                return false;
            }

            object? raw = key.GetValue(ValueName);
            if (raw is not string stored || string.IsNullOrWhiteSpace(stored))
            {
                return false;
            }

            // 没指定期望路径(纯存在性检查,测试用):有值就算启用。
            if (_expectedExePath is null)
            {
                return true;
            }

            string expected = Quote(_expectedExePath);
            // 路径比对用序号比较(大小写敏感,Windows 路径虽不敏感但避免误判)。
            // 接受带引号和不带引号两种存储形式。
            return string.Equals(stored, expected, StringComparison.Ordinal) ||
                   string.Equals(stored, _expectedExePath, StringComparison.Ordinal);
        }
        catch
        {
            // 注册表读取异常(权限 / hive 未加载)——保守返回 false。
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryEnable()
    {
        if (_expectedExePath is null)
        {
            // 没有 exe 路径无法写值(AOT 单测里允许,但生产路径必然有 ProcessPath)。
            return false;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
            key.SetValue(ValueName, Quote(_expectedExePath), RegistryValueKind.String);
            return true;
        }
        catch
        {
            // 组策略 / 受控文件夹访问 / AV 拒绝写入——优雅降级。
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryDisable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
            if (key is null)
            {
                // 键都不存在,视为已禁用(幂等)。
                return true;
            }

            if (key.GetValue(ValueName) is null)
            {
                // 值本就不存在——幂等成功。
                return true;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            // 写入 / 删除失败——返回 false 让 UI 提示。
            return false;
        }
    }

    /// <summary>给路径两端加双引号,匹配 Run 键惯例(处理含空格的路径)。</summary>
    private static string Quote(string path) => "\"" + path + "\"";
}
