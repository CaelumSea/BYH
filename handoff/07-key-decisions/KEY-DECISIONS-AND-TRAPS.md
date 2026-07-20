# 关键决策与必须避开的坑

> **本文是整个项目经验的浓缩。** 每一条都是实际踩过或评审纠正过的。接手 Agent 务必逐条遵守，违反任何一条都会导致可重现的 bug 或返工。

---

## 一、技术选型决策（已定，不要再改）

| 决策 | 结论 | 依据 | 为什么不是别的 |
|---|---|---|---|
| 运行时 | **.NET 10 LTS** | 评审 1 指出 .NET 8 在 2026-11 EOL，.NET 9 是 STS 非 LTS | .NET 10 支持到 2028-11 |
| UI 框架 | **Avalonia 12.1** | 用户需要 macOS，WPF 是 Windows 专用 | WPF 被否；Electron 太重 |
| 取词检测 | **自研 Win32 WH_MOUSE_LL** | 评审 1 指出 Everywhere 是 BSL，不能复制 | clean-room 实现 |
| 发布方式 | **NativeAOT 首选** | Phase 0 已验证可行 | AOT 本体 18.46MB；但保留非 AOT 备选 |
| 窗口设计 | **双窗口**：工具栏(不抢焦点) + 结果窗口(可获焦点) | 参考 Cherry Studio 架构 | 单窗口无法兼顾不抢焦点+可交互 |

---

## 二、已踩的坑（必须规避）

### 坑 1：钩子线程碰 UI → 崩溃 ★★★

**症状**：程序运行，鼠标拖拽时立即崩溃，报：
```
Unhandled exception. System.InvalidOperationException: The calling thread cannot access this object because a different thread owns it.
   at Avalonia.Threading.Dispatcher.VerifyAccess
```

**根因**：`WH_MOUSE_LL` 回调在**专用钩子线程**触发，直接调了 Avalonia 的 UI 属性（如 `IsVisible`）。Avalonia 是单线程 UI 模型。

**修复**：所有 UI 操作包进 `Dispatcher.UIThread.Post`：
```csharp
// ❌ 错误 —— 钩子线程直接碰 UI
_mainWindow.ShowAtNoActivate(x, y);

// ✅ 正确 —— 切到 UI 线程
int cx = x, cy = y;  // 捕获到局部变量
Dispatcher.UIThread.Post(() => {
    _mainWindow?.ShowAtNoActivate(cx, cy);
});
```

**注意**：`UpdateCoordFromHook` 用了 Dispatcher（所以没崩），但 `ShowAtNoActivate` 忘了用——这种"部分切线程"的错误最隐蔽。

### 坑 2：HookProc 委托被 GC 回收 → 崩溃

**症状**：程序运行一段时间后，钩子失效或崩溃（访问违规）。

**根因**：`SetWindowsHookEx` 的第二个参数是委托，如果用局部变量或临时构造，GC 会回收它，底层的函数指针变成野指针。

**修复**：委托必须用**实例字段**保活：
```csharp
private HookProc? _hookProc;  // 字段,保活

private void ThreadProc() {
    _hookProc = HookCallback;  // ✅ 赋值给字段
    _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, hModule, 0);
}
```

### 坑 3：Debug.WriteLine 在独立 exe 里不可见 → "没弹窗"误判

**症状**：程序没崩，但"没有弹窗"。无法判断是钩子没装上、回调没触发、还是 UI 没更新。

**根因**：`System.Diagnostics.Debug.WriteLine` 只在**附加调试器**时输出，独立运行的 Release/Debug exe 完全看不到。

**修复**：写文件日志。spike 里有 `SpikeLog.cs`（写 `%TEMP%\SelectionSpike.log`），正式版用 `Infrastructure/Logging/RedactedLogger.cs`。

### 坑 4：IntPtr 不能当 const

**症状**：编译错误 `CS0133: 表达式 "new IntPtr(-1)" 不是常量`。

**根因**：`HWND_TOPMOST = new IntPtr(-1)` 不是编译时常量。

**修复**：`private static readonly IntPtr HWND_TOPMOST = new(-1);`（不是 `const`）。

### 坑 5：Avalonia 12.x 拿 HWND 的 API 变了

**症状**：编译错误 `CS0122: 'ITopLevelImpl.Handle' 不可访问` 或 `CS0103: 'PlatformImpl' 不存在`。

**根因**：Avalonia 12.x 把底层句柄访问 API 改了，旧教程的 `PlatformImpl.Handle` 是 internal。

**修复**：用基类公开的 `TryGetPlatformHandle()`：
```csharp
private IntPtr? GetHwnd() {
    var handle = TryGetPlatformHandle();  // 基类 TopLevel 的公开方法
    return handle?.Handle;
}
```

### 坑 6：WS_EX_NOACTIVATE 使窗口收不到键盘事件

**症状**：想用 F8 热键做诊断，但窗口隐藏/不抢焦点时 F8 无反应。

**根因**：`WS_EX_NOACTIVATE` 设计就是不获焦点，不获焦点就收不到键盘事件。**这是正确行为，不是 bug**。

**教训**：工具栏的交互必须通过**鼠标点击**完成，不能依赖键盘。诊断用文件日志，不用热键。

---

## 三、评审纠正的硬规则（必须遵守）

### 规则 1：拖拽判定必须轴式，不能欧氏距离 ★★★

v3 用 `distance >= max(SM_CXDRAG, SM_CYDRAG)` 是**错的**。这些指标描述的是矩形。

```csharp
// ❌ 错误(v3)
double dist = Math.Sqrt(dx*dx + dy*dy);
bool isDrag = dist >= Math.Max(dragThresholdX, dragThresholdY);

// ✅ 正确(v4 §5.2) —— 轴式
bool isDrag = Math.Abs(up.X - down.X) >= dragThresholdX
           || Math.Abs(up.Y - down.Y) >= dragThresholdY;
```

### 规则 2：双击判定必须包含同窗口/同进程/同按键

两个相邻窗口里的快速点击不能误判为双击：
```csharp
bool isDoubleClick = elapsed <= doubleClickTime
    && Math.Abs(up.X - lastUp.X) <= doubleClickWidth / 2
    && Math.Abs(up.Y - lastUp.Y) <= doubleClickHeight / 2
    && currentRootHwnd == lastUpRootHwnd   // 同窗口
    && currentPid == lastUpPid             // 同进程
    && currentButton == lastUpButton;      // 同按键
```

### 规则 3：删除 DRAG_MAX_MS

慢速多段选择（选多段文字）是合法操作，不能设最大时长。

### 规则 4：取词必须立即启动，不能等防抖 ★★★

v3 的伪代码：先等 60-100ms → 显示工具栏 → 再启动取词。这会导致 P95 150ms 的目标无法达成。

v4 §5.1 的正确顺序：
```csharp
// 1. 取词立即启动(第一行!)
Task<CaptureResult> captureTask = _capture.CaptureAsync(gesture, token);
// 2. 防抖延迟与取词并发
await Task.Delay(AntiFlickerMs, token);
// 3. 显示工具栏(过期的会话不显示)
if (sessionId != Volatile.Read(ref _currentSessionId)) return;
await Dispatcher.UIThread.InvokeAsync(() => ShowToolbar(...));
// 4. 等取词结果
CaptureResult result = await captureTask;
```

### 规则 5：剪贴板是"最佳努力"，不承诺全格式恢复

不能说"完整备份恢复"。Windows 剪贴板有私有格式、owner-display 格式、延迟渲染格式，无法全保。

正确表述：**最佳努力备份可安全具象化的格式，保证已支持格式的竞态安全恢复，但不承诺私有/延迟渲染格式的逐位保真。**

### 规则 6：剪贴板序列号竞争检测

恢复前必须重检序列号：
```
备份时记 seqA → 注入复制 → 稳定后记 seqB → 读取 → 恢复前查 currentSeq
  if currentSeq == seqB: 恢复(没人动过)
  else: 不恢复(用户/其他应用改了剪贴板,不能覆盖)
```

### 规则 7：进程策略是可组合 record，不是 enum

enum 互斥，但 PDF 阅读器需要 `CopyAllowed` + `DelayedClipboardRead` 两者。用 record：
```csharp
public sealed record ProcessCapturePolicy(
    bool DetectionEnabled,
    bool AccessibilityEnabled,
    SimulatedCopyMode CopyMode,
    int ClipboardStabilizationMs,
    bool ManualFallbackEnabled);
```

### 规则 8：注入事件的事实纠正 ★★★

v3 说"模拟 Ctrl+C 会重进入 WH_MOUSE_LL 触发再次选词"——**这是错的**。Ctrl+C 是键盘输入，WH_MOUSE_LL 只收鼠标事件，**不可能重进入**。

正确规则：模拟复制不会重进入鼠标钩子。如果将来加低级**键盘**钩子，再用 `LLKHF_INJECTED` 识别注入的键盘事件。常量是 `LLMHF_INJECTED`（不是 v3 写的 `LLMH_INJECTED`，那是拼写错误）。

不要全局丢弃所有注入的鼠标事件——辅助软件、远程控制、自动化、笔/触屏都会注入合法事件。只忽略**本应用自己注入的**（用 `dwExtraInfo` marker 识别）。

### 规则 9：UIA 超时模型的诚实表述

`CancellationToken` 让**调用方**停止等待，但**不会**真正取消阻塞的原生 COM 调用。必须区分：
- **调用方超时**：会话停止等待，走剪贴板 fallback
- **Worker 恢复**：阻塞的 UIA worker 标记为不健康，忽略后续过期结果，必要时建替代 worker
- **实际调用取消**：一般不假设

架构：单线程专用 UIA worker，所有 UIA 操作在同一线程，不要跨线程传递 `AutomationElement`。

### 规则 10：Thread priority 保持 Normal

不要设 Highest。钩子超时是注册表配置（`LowLevelHooksTimeout`），上限 1000ms，回调设计在个位数毫秒返回即可。Highest 会影响桌面其他应用。

---

## 四、许可证红线（绝对不要碰）

| 项目 | 许可证 | 风险 | 规则 |
|---|---|---|---|
| **Everywhere** | BSL 1.1 | 商业竞品使用被禁，首次发布 4 年后才转 Apache 2.0 | **不复制代码**，只学架构 |
| **Cherry Studio** | AGPL-3.0 | 衍生作品须开源 | **不复制代码**，只学架构 |

可以做的：
- 研究 Everywhere 如何用 WH_MOUSE_LL（看它的 `TextSelectionDetector.cs` 逻辑）
- 学习 Cherry Studio 的双窗口配置（`windowRegistry.ts` 里 SelectionToolbar 的属性）
- 按 Win32 官方 API 自己实现

不能做的：
- 复制 Everywhere 的 600 行钩子代码
- 复制 Cherry Studio 的 selection-hook 原生模块
- 任何形式的"改造后复制"

**Win32 API 是公开标准**（Microsoft Learn 文档齐全），基于官方 API 自己实现完全没有许可证问题。

---

## 五、Win32 API 参考（自己实现的依据）

| 功能 | API | 文档 |
|---|---|---|
| 低级鼠标钩子 | `SetWindowsHookExW` + `WH_MOUSE_LL` + `LowLevelMouseProc` | learn.microsoft.com/win32/winmsg/lowlevelmouseproc |
| 鼠标结构体 | `MSLLHOOKSTRUCT`（含 `LLMHF_INJECTED` flag） | learn.microsoft.com/win32/api/winuser/ns-winuser-msllhookstruct |
| 系统指标 | `GetSystemMetrics`(`SM_CXDRAG`/`SM_CYDRAG`/`SM_CXDOUBLECLK`/`SM_CYDOUBLECLK`) | learn.microsoft.com/win32/api/winuser/nf-winuser-getsystemmetrics |
| 双击时间 | `GetDoubleClickTime` | learn.microsoft.com/win32/api/winuser/nf-winuser-getdoubleclicktime |
| 不抢焦点窗口 | `WS_EX_NOACTIVATE` + `SW_SHOWNOACTIVATE` + `SWP_NOACTIVATE` | learn.microsoft.com/win32/winmsg/extended-window-styles |
| 剪贴板序列号 | `GetClipboardSequenceNumber` | learn.microsoft.com/win32/api/winuser/nf-winuser-getclipboardsequencenumber |
| 剪贴板变化通知 | `AddClipboardFormatListener` → `WM_CLIPBOARDUPDATE` | learn.microsoft.com/win32/api/winuser/nf-winuser-addclipboardformatlistener |
| 模拟输入 | `SendInput`（注意 UIPI 限制） | learn.microsoft.com/win32/api/winuser/nf-winuser-sendinput |
| 根窗口 | `GetAncestor(GA_ROOT)` | learn.microsoft.com/win32/api/winuser/nf-winuser-getancestor |
| 全屏检测 | `SHQueryUserNotificationState`(`QUNS_RUNNING_D3D_FULL_SCREEN`) | learn.microsoft.com/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate |
