namespace SelectionAssistant.Core.Startup;

/// <summary>
/// 开机自启的用户偏好。单布尔开关,默认关闭(用户主动选择是否开机拉起)。
/// 持久化为 <c>startup-options.json</c>,由 <c>StartupSettingsStore</c> 读写。
/// <para>
/// <b>与系统真实状态的关系:</b>本记录是「用户上次的意图」;开机是否真的拉起
/// 取决于 <c>HKCU\…\CurrentVersion\Run</c> 注册表项。加载时 App 会用
/// <c>IAutoStartManager.IsEnabled()</c> 校准——以注册表为真相源回写本文件,
/// 避免用户在任务管理器 / Windows 设置里手动改过之后 UI 显示与实际不符。
/// </para>
/// </summary>
public sealed record StartupSettings
{
    /// <summary>
    /// 是否在 Windows 登录时自动启动 BYH。默认 <b>false</b>——遵循「不打扰」原则,
    /// 用户主动到设置里开启。
    /// </summary>
    public bool LaunchAtStartup { get; init; } = false;

    public static StartupSettings Default { get; } = new();

    /// <summary>
    /// 规范化。本记录目前只有单布尔,无范围字段;保留方法以与其他 settings
    /// record 保持一致的可扩展形态(未来若加延迟启动 / 启动参数等也走这里)。
    /// </summary>
    public StartupSettings Normalize() => this;

    /// <summary>硬断言。当前无约束字段,留作扩展点。</summary>
    public void Validate()
    {
    }
}
