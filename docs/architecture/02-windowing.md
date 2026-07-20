# 02 · 窗口系统（Windowing）

> **改工具条/面板/弹窗的显示、定位、失焦、拖拽、全局快捷键或 chord 触发前先读本文件。**

---

## 职责一句话

管理七个窗口的创建、显示、定位、焦点行为、生命周期；核心是 `WS_EX_NOACTIVATE` 契约（工具条不抢焦点保住选中高亮）+ 快捷键/chord 浮层面板的定位/clamp/失焦隐藏/拖拽。

## 关键文件

| 文件 | 职责 |
|---|---|
| `Platform.Windows/Windowing/NoActivateWindowHost.cs` | Win32 HWND 宿主；WS_EX_NOACTIVATE\|TOOLWINDOW\|TOPMOST；SetWindowPos(SWP_NOACTIVATE) |
| `UI/Views/ToolbarWindow.axaml(.cs)` | 划词窄条；翻译/解释/总结/Prompt/复制/粘贴；ShowPending/SetCaptureResult |
| `UI/Views/QuickToolsWindow.axaml(.cs)` | 全局快捷键浮层面板（chord 可选）；动态功能按钮（ItemsControl）+ 自定义指令 + 复制/粘贴/管理功能 |
| `UI/Views/PromptWindow.axaml(.cs)` | 任意指令输入；Topmost |
| `UI/Views/PromptTemplateEditWindow.axaml(.cs)` | 自定义功能编辑/新建；Topmost |
| `UI/Views/ResultWindow.axaml(.cs)` | 流式结果显示；重试/关闭 |
| `UI/Views/SettingsWindow.axaml(.cs)` | 1000×720 设置主界面；固定侧栏分为常规/翻译服务/自定义功能/视觉识别，右侧独立滚动 + 固定底栏 |
| `UI/Views/RegionSelectOverlay.axaml(.cs)` | 全屏画框 OCR；暖色遮罩 + 手动画框/调整/确认 |
| `UI/Themes/IvoryJade.axaml` | 七窗口共享主题；详见 `08-theme-system.md` |
| `UI/Assets/Theme/ivory-jade-*.jpg` | R27 设置页玉石徽记与珠光花丝资产 |
| `App/App.axaml.cs` | 七窗口实例化 + 事件接线 + TrayIcon + 重启 + 单实例 |

## 七窗口焦点行为

| 窗口 | 抢焦点？ | 显示方式 | 失焦行为 |
|---|---|---|---|
| ToolbarWindow | **否**（NOACTIVATE） | NoActivateWindowHost.SetWindowPos(SWP_NOACTIVATE\|SWP_SHOWWINDOW) | 点外部 → dismiss 会话 |
| QuickToolsWindow | Topmost | Show() + Activate()；grace window 内忽略 Deactivated | 失焦隐藏（grace window 外） |
| PromptWindow | Topmost | Show() | 手动关闭 |
| PromptTemplateEditWindow | Topmost | Show(owner) | 手动关闭 |
| ResultWindow | 否 | Show() | 手动关闭 |
| SettingsWindow | 否 | Show() + Activate() | 隐藏（不关闭） |
| RegionSelectOverlay | Topmost | 覆盖主屏幕 Show() | 确认/ESC 后隐藏 |

## chord 浮层定位（⚠️ 易踩坑）

`QuickToolsWindow.ShowAt(x, y, ...)` 定位逻辑：
```
坐标来源：LowLevelMouseHook 的 MSLLHOOKSTRUCT.Point = 物理屏幕像素
Avalonia Position(PixelPoint) = 物理像素
→ 直接用 x+16, y+16，【绝不乘 RenderScaling】（旧 bug：双重缩放把面板推到屏幕外）
→ ClampToScreen：超出右/下边缘则翻转到光标左/上方
```

## chord grace window（⚠️ 永久记录）

**问题**：chord 的右键会弹出源应用右键菜单并抢焦点 → 面板 Deactivated → 立即隐藏 = 闪一下消失。

**错误修复（已废弃，勿用）**：grace window 内调 `Activate()` 抢回焦点 → 右键菜单再抢回 → `Deactivated→Activate→Deactivated` **重入循环冻结 UI 线程** → 后续所有 chord 的 `Dispatcher.Post(ShowAt)` 堆积执行不了 = "只能触发一次"。

**正确修复**：grace window（400ms）内**只忽略 Deactivated，绝不调 Activate()**。面板靠 Topmost 保持可见，不需焦点也能被点击。

## 关键方法

- `NoActivateWindowHost.ShowAtNoActivate(x,y)` — SetWindowPos(HWND_TOPMOST, x+16, y+16, NOACTIVATE\|SHOWWINDOW\|NOSIZE)。
- `QuickToolsWindow.ClampToScreen(left,top)` — Screens.ScreenFromPoint → WorkingArea → 超边缘翻转。
- `QuickToolsWindow.OnRootPointerPressed` — BeginMoveDrag（面板可拖动，无边框无标题栏）。
- `ToolbarWindow` 的 `OnPasteClick` → `PasteRequested` 事件 → runtime `OnPasteRequested` → SendInputHelper.SendPasteChord（Ctrl+V 注入源应用）。

## 不变量 / 踩坑

- **ToolbarWindow 永不 SetForegroundWindow**——用 SWP_NOACTIVATE。
- **QuickTools 定位不乘 RenderScaling**——坐标已是物理像素。
- **grace window 绝不调 Activate()**——重入冻结 UI 线程。
- DataTemplate 绑定类型必须 **public top-level**（R7 踩坑；`PromptFunctionRow`/`ProviderOption` 都是 public top-level）。
- 窗口 `Closing` 事件 cancel + Hide()（除非 `_allowClose`）——窗口复用，不销毁重建。
- 所有视觉色值经 `IvoryJade.axaml` 的 `DynamicResource` 消费；窗口内不复制品牌色。
- QuickTools 内容或高度变化后必须在 175% DPI 截图，R26 曾发现 400px 高度导致底部控件重叠。
- Settings 默认 1000×720、最小 860×600 logical；侧栏和底栏固定，只有当前分区滚动。`ShowAndScrollToPromptTemplates()` 必须先切到自定义功能页，`SelectProviderForEditing()` 必须先切到翻译服务页。

## 改动检查清单

- [ ] 改定位：确认坐标是物理像素，不乘缩放；加 ClampToScreen。
- [ ] 改失焦：grace window 内绝不 Activate()。
- [ ] 改窗口按钮：DataTemplate 绑定类型 public top-level；用 Command 不用反射。
- [ ] 改工具条：保持 NOACTIVATE；复制用 Avalonia 剪贴板，粘贴用 SendInput Ctrl+V。
