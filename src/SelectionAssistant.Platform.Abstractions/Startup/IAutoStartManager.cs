namespace SelectionAssistant.Platform.Abstractions.Startup;

/// <summary>
/// 平台无关的「开机自启」抽象。
/// Windows 实现 (WindowsRunAutoStartManager): 写 HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
/// </summary>
public interface IAutoStartManager
{
    /// <summary>
    /// 自启是否已启用。应反映系统真实状态(注册表 / 启动文件夹),而非缓存值,
    /// 因为用户可能在任务管理器 / Windows 设置里手动改过。
    /// </summary>
    bool IsEnabled();

    /// <summary>
    /// 启用开机自启。成功返回 true;若系统策略禁止或写入失败,返回 false(不抛异常)。
    /// </summary>
    bool TryEnable();

    /// <summary>
    /// 关闭开机自启。成功返回 true;若值本就不存在或删除失败,返回 false(不抛异常)。
    /// </summary>
    bool TryDisable();
}
