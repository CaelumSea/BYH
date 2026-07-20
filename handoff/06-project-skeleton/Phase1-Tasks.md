# Phase 1 任务清单 — Selection + Capture

**来源**: v4 方案 §5、§6、§7.3、§13.3
**工期**: 5–6 个工作日
**硬验收门**: 取词成功率 ≥ 95%（测试应用语料库），零"错误文本返回为成功"故障，零剪贴板覆盖故障

---

## 当前实现状态（2026-07-16 接续）

| 任务 | 状态 | 已验证结果 |
|---|---|---|
| P1.0 | ✅ 完成 | 正式组合根、Avalonia 窗口、脱敏日志已落地；全解决方案构建 0 警告、0 错误 |
| P1.1 | ✅ 完成 | 独立原生线程 `WH_MOUSE_LL`、真实 Win32 线程 ID、`WS_EX_NOACTIVATE` 窗口宿主；启动/释放集成测试及隐藏启动冒烟通过 |
| P1.2 | ✅ 完成 | 系统指标驱动的轴式拖拽/双击判定；覆盖同窗口、同进程、同按键和无拖拽最长时限，共 6 个单元测试 |
| P1.3 | ✅ 完成 | 立即并发取词、75ms 防闪烁、每次 UI 写入前 stale-session 守卫、阻塞任务安全观察；3 个会话测试通过 |
| P1.4 | ✅ 实现完成 | Windows 原生 UIA `TextPattern2 → TextPattern`、400ms 可替换 MTA worker、超时隔离；JIT 与 NativeAOT 实机探针均通过 |
| P1.5 | ✅ 完成 | 原生剪贴板监听、受支持格式快照/恢复、完整复制和弦、稳定窗口、序列号与所有权守卫均已接入；真实 AOT 桌面链路通过 |
| P1.6 | ✅ 完成 | 进程身份解析、显式匹配优先级、终端/PDF 默认策略、用户 JSON 覆盖、UIPI 管理员边界与逐次复制参数均已接入完整取词链 |
| P1.7 | ⏳ 待开始 | DPI / 多显示器定位尚未实现 |
| P1.8 | 🚧 已有基础 | 当前自动化测试 60/60 通过；应用语料库 95% 成功率与剪贴板零覆盖验收需待 P1.7 后执行 |
| v0.1 翻译切片 | ✅ 测试闭环完成 | 工具条翻译按钮、可替换 Provider、并发会话守卫、结果窗口、复制/重试/关闭已接通；真实 AOT 桌面闭环通过 |
| v0.1 管理切片 | ✅ 完成 | 系统托盘、设置状态页、配置/日志目录入口、关闭即隐藏与“退出 BYH”已接通；NativeAOT UIA 设置→退出闭环退出码 0 |

### 本次验证证据

- `dotnet build SelectionAssistant.slnx`：0 警告、0 错误。
- Core/Infrastructure 测试 30/30，Windows 集成测试 30/30。
- `dotnet publish src/SelectionAssistant.App/SelectionAssistant.App.csproj -c Release -r win-x64`：NativeAOT 发布成功，无 AOT/裁剪警告。
- NativeAOT 主程序约 22.4 MB，发布目录无 PDB；`--probe-uia` 与 `--probe-translation` 退出码均为 0。
- NativeAOT 正常启动存活检查通过，日志确认鼠标钩子和 Phase 1 运行时启动。
- NativeAOT 真实桌面端到端：双击与拖拽均能选中文本，工具条可访问状态为“已取词 · Accessibility”，弹出前后焦点保持在源文本窗口，`WS_EX_NOACTIVATE` 生效。
- P1.6 策略链：路径/Bundle ID > 签名身份 > 进程名 > 默认策略；同优先级下用户后置规则覆盖内置规则。终端只注入 `Ctrl+Insert`，PDF 使用 150ms 稳定窗口，高完整性目标自动禁止 `SendInput` 并进入手动复制提示。
- 用户策略从 `%LOCALAPPDATA%\BYH\capture-policies.json` 读取；示例见 `docs/capture-policies.example.json`。无文件时使用安全默认值，文件无效时整体拒绝并继续使用默认值。
- 工具条生命周期：在工具条外按下鼠标会取消当前取词会话并自动隐藏；迟到的异步结果不能重新显示旧工具条。真实 AOT 桌面验收通过。
- P1.5 剪贴板回退：真实 WinForms 文本框经完整复制和弦成功取词，AOT `--probe-clipboard` 退出码 0；测试前后原剪贴板逐字符一致。14 个专项测试覆盖空剪贴板、文本/图片/文件恢复、延迟格式、来源退出、并发用户复制、序列号竞争、多次更新稳定、取消清理、超时和修饰键保护。
- 翻译测试闭环：真实 WinForms 选词 → `BYH` 工具条 → 翻译按钮 → `BYH · Translation` 结果窗口通过；UI Automation 精确确认原文与中文译文分别进入两个 ValuePattern 文本框。翻译会话支持取消/替换，迟到响应不能覆盖新结果。
- 测试 Provider：MyMemory 公共 REST API，无需密钥，传统翻译记忆 + 机器翻译；遵循官方单请求 500 UTF-8 字节限制。该 Provider 会把选中文字通过 HTTPS 发送到第三方，仅用于原型测试。
- 最小管理闭环：托盘菜单支持打开设置/配置目录/退出；设置页支持配置目录、日志目录、隐藏与退出。NativeAOT 下 UI Automation 找到设置窗和退出按钮，Invoke 后进程正常退出，退出码 0。

### 当前可用边界

- **可用**：Windows 上启动后台程序，在支持 UIA TextPattern 或安全剪贴板回退的应用中拖拽/双击选词，点击“翻译”得到英→中或中→英结果，并可复制、重试、关闭。
- **尚不可用**：“解释/总结/自定义”仍禁用；MyMemory 仅是联网测试 Provider，受 500 字节、匿名配额与翻译质量限制；设置页尚不能表单编辑策略；DPI/多显示器和 95% 应用语料库尚未验收。

### Windows 最小可用版本（v0.1）检查点

当前只是“可工作的选词原型”。第一个可供日常使用的版本定义为：

1. ✅ 完成 P1.5 + P1.6：UIA 失败时安全降级到剪贴板取词，按进程选择策略，绝不覆盖用户剪贴板。
2. ✅ 实现精简 Action Engine：v0.1 只启用“翻译”，其余动作保持禁用。
3. ⏳ OpenAI-compatible Provider 尚未实现；当前先接入无密钥 MyMemory 测试 Provider，接口可替换。
4. ✅ 接通结果窗口：传统机器翻译一次性返回，支持取消、复制、重试和关闭；流式输出留给模型 Provider。
5. ✅ 提供最小托盘、设置入口、配置/日志目录入口与退出方式。
6. 对记事本、主流浏览器和至少一个 Office/编辑器应用做端到端验收并重新发布 NativeAOT 包。

达到以上检查点后，用户闭环为“选中文字 → 点击翻译 → 查看/复制结果”，即可称为最小可用程序。P1.7 的完整多显示器适配、全部四个动作、开机启动、macOS 与 95% 广泛兼容可在 v0.1 后继续完善；最小托盘管理已经完成。

**下一执行点**：P1.7 DPI/多显示器定位；随后执行 P1.8 应用语料库验收。下一位 Agent 应先读 `..\00-CURRENT-HANDOFF.md`。

**品牌已确认**：`BYH`（By Your Hand）。用户可见品牌统一使用大写缩写。

---

## 任务总览

| # | 任务 | 依赖 | 优先级 |
|---|---|---|---|
| P1.0 | 搭正式项目骨架（多项目解决方案） | 无 | 高（地基） |
| P1.1 | 迁移 spike 钩子代码 + 抽象接口 | P1.0 | 高 |
| P1.2 | 系统指标几何判定（轴式） | P1.1 | 高 |
| P1.3 | 并发安全的 SelectionSessionManager | P1.1 | 高（架构核心） |
| P1.4 | Tier 1 UIA 取词（Windows） | P1.0 | 高 |
| P1.5 | Tier 2/3 最佳努力剪贴板状态机 | P1.0 | 高 |
| P1.6 | 可组合 ProcessCapturePolicy | P1.4, P1.5 | 中 |
| P1.7 | DPI / 多显示器定位 | P1.1 | 中 |
| P1.8 | 集成测试 + 95% 取词验收 | 全部 | 高 |

---

## P1.0 搭正式项目骨架

**目标**: 按 v4 §4 创建多项目解决方案，平台分离为独立程序集以便 AOT 裁剪。

**交付文件**:
```
selection-assistant/
  SelectionAssistant.sln
  src/
    SelectionAssistant.App/                      # net10.0  入口 + 组合根
      SelectionAssistant.App.csproj
      Program.cs
      App.axaml(.cs)
    SelectionAssistant.Core/                     # net10.0  跨平台核心
      SelectionAssistant.Core.csproj
      Selection/SelectionGesture.cs              # 手势数据记录
      Selection/SelectionSessionManager.cs       # P1.3
      Selection/IGestureClassifier.cs            # 几何判定抽象
      Capture/ICaptureService.cs                 # 四级链抽象
      Capture/CaptureResult.cs
      Capture/ProcessCapturePolicy.cs            # P1.6
      Capture/SimulatedCopyMode.cs
      Capture/ProcessPolicyResolver.cs           # P1.6
    SelectionAssistant.Platform.Abstractions/    # net10.0  平台接口
      SelectionAssistant.Platform.Abstractions.csproj
      IMouseHook.cs
      ISelectionTextCapture.cs
      IClipboardAccess.cs
      IWindowFocusController.cs                  # NOACTIVATE 抽象
      ISystemMetrics.cs                          # 几何指标抽象
    SelectionAssistant.Platform.Windows/         # net10.0-windows
      SelectionAssistant.Platform.Windows.csproj
      Hooks/LowLevelMouseHook.cs                 # ← 迁移自 spike
      Hooks/WindowsMouseHookAdapter.cs
      Capture/UIAutomationTextCapture.cs         # P1.4
      Capture/Win32ClipboardCapture.cs           # P1.5
      Capture/SendInputHelper.cs                 # 完整和弦注入
      Clipboard/Win32Clipboard.cs                # bounded retry + listener
      Windowing/NoActivateWindowHost.cs          # ← 迁移自 spike
      WindowsSystemMetrics.cs                    # SM_CXDRAG 等
    SelectionAssistant.UI/                       # net10.0  Avalonia 视图
      SelectionAssistant.UI.csproj
      Views/ToolbarWindow.axaml(.cs)             # ← 迁移自 spike MainWindow
      Views/ResultWindow.axaml(.cs)              # 占位
    SelectionAssistant.Infrastructure/           # net10.0
      SelectionAssistant.Infrastructure.csproj
      Logging/RedactedLogger.cs                  # ← 迁移自 spike SpikeLog
  tests/
    SelectionAssistant.Core.Tests/
    SelectionAssistant.Windows.IntegrationTests/
```

**验收**: `dotnet build` 全解决方案 0 错误；项目引用关系无循环；Windows 项目正确指向 `net10.0-windows`。

---

## P1.1 迁移 spike 钩子代码 + 抽象接口

**目标**: 把 Phase 0 验证过的代码搬进正式骨架，抽出平台无关接口。

**迁移映射**:
| spike 文件 | → 正式位置 | 改动 |
|---|---|---|
| `LowLevelMouseHook.cs` | `Platform.Windows/Hooks/` | 加 `IMouseHook` 实现 |
| `MainWindow.axaml(.cs)` | `UI/Views/ToolbarWindow.axaml(.cs)` | `ShowAtNoActivate` 抽到 `IWindowFocusController` |
| `Program.cs` 的判定逻辑 | `Core/Selection/BasicGestureClassifier.cs` | 实现 `IGestureClassifier` |
| `SpikeLog.cs` | `Infrastructure/Logging/` | 改名 `RedactedLogger` |

**新增抽象**（`Platform.Abstractions/`）:
- `IMouseHook`: `event Action<MouseEventData> MouseEvent; void Start(); void Dispose();`
- `ISystemMetrics`: `int DragThresholdX { get; }` / `int DragThresholdY { get; }` / `int DoubleClickTimeMs { get; }` 等
- `IWindowFocusController`: `void ShowAtNoActivate(int x, int y); void Hide();`

**验收**: 程序能在正式骨架里跑起来，拖拽弹窗行为与 spike 一致。

---

## P1.2 系统指标几何判定（轴式）

**来源**: v4 §5.2

**目标**: 替换 spike 的硬编码 `dx>=8 || dy>=8`，用 Windows 系统指标做轴式判定。

**实现**（`Core/Selection/SystemMetricGestureClassifier.cs`）:
```
dragThresholdX   = GetSystemMetrics(SM_CXDRAG)
dragThresholdY   = GetSystemMetrics(SM_CYDRAG)
doubleClickTime  = GetDoubleClickTime()
doubleClickWidth = GetSystemMetrics(SM_CXDOUBLECLK)
doubleClickHeight= GetSystemMetrics(SM_CYDOUBLECLK)

isDrag = abs(up.x - down.x) >= dragThresholdX
      OR abs(up.y - down.y) >= dragThresholdY

isDoubleClick = elapsed <= doubleClickTime
            AND abs(up.x - lastUp.x) <= doubleClickWidth  / 2
            AND abs(up.y - lastUp.y) <= doubleClickHeight / 2
            AND currentRootHwnd == lastUpRootHwnd
            AND currentPid    == lastUpPid
            AND currentButton == lastUpButton
```

**关键约束**（来自 v3 评审纠正）:
- **必须轴式**，不能用欧氏距离（矩形指标语义不符）
- 双击判定**必须**包含同窗口/同进程/同按键检查（相邻窗口的两次快击不能误判为双击）
- **删除 `DRAG_MAX_MS`**——慢速多段选择是合法操作
- 时钟用 `Environment.TickCount64`（单调），非墙上时钟

**验收**: 在记事本/浏览器中快击、慢拖、双击、跨窗口快击均判定正确。

---

## P1.3 并发安全的 SelectionSessionManager ★架构核心

**来源**: v4 §5.1（评审指定的两个前置必改项之一）

**目标**: 替换 spike 的 fire-and-forget 调用，实现并发安全的会话管理。

**完整实现**（v4 §5.1 代码，`Core/Selection/SelectionSessionManager.cs`）:
```csharp
public sealed class SelectionSessionManager
{
    private long _currentSessionId;          // 单调递增
    private CancellationTokenSource? _currentCts;
    private Task? _runningTask;              // 跟踪,不 fire-and-forget

    public async Task StartOrReplaceSessionAsync(SelectionGesture gesture)
    {
        _currentCts?.Cancel();
        _currentCts?.Dispose();

        var sessionId = Interlocked.Increment(ref _currentSessionId);
        var cts = new CancellationTokenSource();
        _currentCts = cts;
        var token = cts.Token;

        _runningTask = SessionCoreAsync(gesture, sessionId, token);
        try { await _runningTask; }
        catch (OperationCanceledException) { /* 被新会话取代 */ }
    }

    private async Task SessionCoreAsync(
        SelectionGesture gesture, long sessionId, CancellationToken token)
    {
        // ── 取词立即启动(第一行,无延迟)──
        Task<CaptureResult> captureTask = _capture.CaptureAsync(gesture, token);

        // ── 防抖延迟与取词并发 ──
        await Task.Delay(AntiFlickerMs, token);

        // ── 显示工具栏(UI 线程)── 每次写 UI 前做过期会话守卫
        if (sessionId != Volatile.Read(ref _currentSessionId)) return;
        await Dispatcher.UIThread.InvokeAsync(
            () => ShowToolbar(gesture.MouseUpPosition));

        // ── 等取词结果 ──
        CaptureResult result = await captureTask;

        if (token.IsCancellationRequested) return;
        // 最终守卫:取词实现可能在取消后仍返回过期结果
        if (sessionId != Volatile.Read(ref _currentSessionId)) return;

        // ── 更新工具栏(UI 线程)──
        await Dispatcher.UIThread.InvokeAsync(
            () => ToolbarSetCaptureResult(result));
    }
}
```

**关键不变量**（v4 §5.1 行 180）:
1. `CaptureAsync` 在 `SessionCoreAsync` 第一行启动（**立即**，无延迟）
2. 防抖延迟与取词**并发**运行
3. **所有** UI 访问走 `Dispatcher.UIThread`
4. **每次** UI 写入前检查 `sessionId` 守卫
5. CTS 用后 Dispose
6. 任务被跟踪（不 fire-and-forget）

**验收**: 快速连续选词 10 次，每次都只显示最新会话的工具栏，无过期结果残留。

---

## P1.4 Tier 1 UIA 取词（Windows）

**来源**: v4 §6.2

**目标**: 实现 UI Automation 取词——不碰剪贴板的首选路径。

**取词顺序**:
```
STEP 0: 显示 UI 前缓存上下文
  foregroundHwnd, foregroundPid, focusedElement, elementUnderMouse
  # 显示工具栏会改变焦点,必须先缓存

STEP 1: TextPattern2 on focusedElement AND elementUnderMouse
STEP 2: TextPattern  on both
STEP 3: 有界父级遍历(≤ N 层)找 text pattern
STEP 4: 回退到 Tier 2
```

**超时模型**（诚实表述，v4 §6.2）:
- 专用 UIA 工作线程（单线程），所有 UIA 操作在同一线程
- 调用方等待 ≤ 300–500ms，超时则走剪贴板 fallback
- **不能假设**原生 COM 调用被真正取消——`CancellationToken` 只让调用方停止等待
- 超时后标记 worker 不健康，忽略后续过期结果，必要时建替代 worker
- 不要跨线程传递 `AutomationElement`

**禁止**: 用 `LegacyIAccessible.Name+Value` 拼接当选中文字（可能返回整个控件或无关标签）。

**验收**: 记事本/Edge/Word/WPF 应用中能取到选中文字；故意挂起的 UIA provider 不冻结工具栏。

---

## P1.5 Tier 2/3 最佳努力剪贴板状态机

**来源**: v4 §6.4

**目标**: UIA 失败时通过模拟复制取词，**不破坏**用户剪贴板内容。

**8 状态机**（详见 v4 §6.4）:
```
STATE 1: 用户意图检查(用户自己按 Ctrl+C? → 不干扰)
STATE 2: 进程策略(见 P1.6)
STATE 3: 备份(最佳努力,有界重试 OpenClipboard)
STATE 4: 订阅剪贴板变化(AddClipboardFormatListener → WM_CLIPBOARDUPDATE,非轮询)
STATE 5: 模拟复制(前置检查修饰键;完整和弦一次 SendInput 注入)
STATE 6: 稳定化(每次更新重置短定时器,非首次变化)
STATE 7: 读取剪贴板文本(有界重试 OpenClipboard)
STATE 8: 恢复(finally 中执行;先取消订阅或标记 Restoring;重检 seqB)
```

**关键修复**（v3 评审要求）:
- `OpenClipboard` 有界重试放在**所有**剪贴板访问周围（不只 SendInput 前）
- 恢复前**取消订阅监听器**或标记 `Restoring` 状态，避免自己的恢复触发通知重置定时器
- 完整和弦在**一个** `SendInput` 数组里：`[Ctrl down, key down, key up, Ctrl up]`
- 注入前检查当前修饰键状态（用户按住 Ctrl/Alt/Shift 会干扰）
- `seqB` 在稳定化定时器到期后才记录（非首次变化）
- 恢复前重检 `currentSeq == seqB`，不等则不恢复（避免覆盖用户新内容）
- 无备份时只在 `currentSeq == seqB` 时清空剪贴板

**用 `GetClipboardSequenceNumber` 做竞争检测**。

**验收**: v4 §6.5 的 11 个集成测试全部通过（空剪贴板、Text+HTML/RTF、大图、文件列表、延迟渲染、剪贴板属主进程退出、捕获中用户复制、恢复中用户复制、≥3 次更新、输入后取消、整超时不可用）。

---

## P1.6 可组合 ProcessCapturePolicy

**来源**: v4 §6.6

**目标**: 替换硬编码黑名单，用可组合 record 表达每个进程的取词策略。

**模型**:
```csharp
public sealed record ProcessCapturePolicy(
    bool DetectionEnabled,
    bool AccessibilityEnabled,
    SimulatedCopyMode CopyMode,        // None | CtrlInsertOnly | CtrlInsertThenCtrlC
    int  ClipboardStabilizationMs,     // 0 = 默认
    bool ManualFallbackEnabled);

public enum SimulatedCopyMode { None, CtrlInsertOnly, CtrlInsertThenCtrlC }
```

**匹配优先级**（显式，避免不可预测的覆盖）:
1. 精确可执行路径 / macOS bundle id
2. 签名应用身份（可用时）
3. 进程名
4. 默认策略

**默认策略语料库**（内置，用户可覆盖）:
- 终端（Windows Terminal/cmd/PowerShell）: `CtrlInsertOnly`（Ctrl+C 会中断进程）
- PDF 阅读器（Acrobat/Foxit）: `CtrlInsertThenCtrlC`, `ClipboardStabilizationMs=150`
- 管理员应用: `ManualFallbackEnabled=true`（UIPI 限制 SendInput）

**验收**: 不同应用按策略走不同取词路径；用户自定义策略能覆盖默认。

---

## P1.7 DPI / 多显示器定位

**来源**: v4 §7.3

**目标**: 工具栏在多显示器、不同 DPI 缩放下正确定位。

**实现**:
- Windows: 物理像素 → DIP 转换；`SetWindowPos` 用物理坐标
- 定位在 mouse-up 点，溢出时翻转（向左/向上），夹紧到工作区
- macOS: 原生用 points，无需转换

**验收**: 100%/125%/150%/200% 缩放 + 多显示器下，工具栏出现在鼠标附近且不超出屏幕。

---

## P1.8 集成测试 + 95% 取词验收

**目标**: 验证硬验收门。

**测试应用语料库**: 记事本、Edge、Chrome、Word、PowerPoint、Acrobat、Windows Terminal、VS Code、WPF 应用、Win32 应用。

**验收标准**（v4 §12.2）:
- 自动取词成功率 ≥ 95%
- 零"错误文本返回为成功"故障
- 所有失败用例手动 fallback 成功
- 并发压力测试零剪贴板覆盖故障

---

## 执行顺序

```
P1.0 骨架 ──┬─→ P1.1 迁移钩子 ──┬─→ P1.2 几何判定
            │                    ├─→ P1.3 会话管理器 ★
            │                    └─→ P1.7 DPI 定位
            ├─→ P1.4 UIA 取词 ──┐
            └─→ P1.5 剪贴板 ────┴─→ P1.6 进程策略 ──→ P1.8 集成验收
```

P1.0 是所有任务的地基，先做。P1.3（会话管理器）和 P1.5（剪贴板）是 Phase 1 最复杂的两块，优先级最高。
