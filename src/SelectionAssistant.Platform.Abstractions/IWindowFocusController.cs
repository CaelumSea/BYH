namespace SelectionAssistant.Platform.Abstractions;

/// <summary>
/// 窗口焦点控制抽象(门2 的核心)。
/// 实现必须保证:显示窗口时不抢焦点(WS_EX_NOACTIVATE / NSPanel nonactivatingPanel)。
/// Phase 0 已验证 Windows 实现可行。
/// </summary>
public interface IWindowFocusController
{
    /// <summary>在指定坐标显示窗口,不激活(v4 §7.1)。</summary>
    void ShowAtNoActivate(int x, int y);

    /// <summary>
    /// 在指定坐标显示窗口,不激活 —— 调用方传入的是窗口最终 top-left(物理像素),
    /// 不会再加偏移。R35 工具栏定位用这个变体:由 <see cref="ToolbarWindow.ClampAnchor"/>
    /// 算好 top-left(已考虑屏幕边缘 clamp 和 flip),host 直接落子。
    /// </summary>
    void ShowAtNoActivatePoint(int left, int top);

    /// <summary>隐藏窗口。</summary>
    void Hide();

    /// <summary>窗口当前是否可见。</summary>
    bool IsVisible { get; }
}
