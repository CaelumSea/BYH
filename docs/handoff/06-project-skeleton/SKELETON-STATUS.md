# 项目骨架现状

> 截至交接时（2026-07-16），正式项目骨架已按 v4 §4 搭建完毕，8 个项目组成多项目解决方案。本文件记录每个项目的当前状态，让接手 Agent 知道从哪里接着写。

> **2026-07-16 接续更新（优先于下方历史快照）**：P1.0–P1.6、v0.1 翻译测试切片及最小托盘/设置/退出已实现并验证。全解决方案构建 0 警告、0 错误，自动化测试 60/60 通过；NativeAOT 设置→退出桌面闭环退出码 0。下一执行点是 P1.7 DPI/多显示器定位。完整最新状态见 `..\00-CURRENT-HANDOFF.md`。

**项目根**：`C:\dvr\gh-kb\selection-assistant\`
**解决方案**：`SelectionAssistant.slnx`（注：.NET 10 默认生成 .slnx 格式）

---

## 项目依赖图

```
SelectionAssistant.App (net10.0-windows, WinExe, 组合根)
  ├─ SelectionAssistant.Core (net10.0)
  │    └─ SelectionAssistant.Platform.Abstractions (net10.0)
  ├─ SelectionAssistant.Platform.Windows (net10.0-windows)
  │    ├─ SelectionAssistant.Core
  │    └─ SelectionAssistant.Platform.Abstractions
  ├─ SelectionAssistant.UI (net10.0, Avalonia)
  │    ├─ SelectionAssistant.Core
  │    └─ SelectionAssistant.Infrastructure
  └─ SelectionAssistant.Infrastructure (net10.0)

tests/
  SelectionAssistant.Core.Tests (net10.0) → Core
  SelectionAssistant.Windows.IntegrationTests (net10.0-windows) → Platform.Windows + Core
```

**设计原则**（v4 §4）：平台分离为独立程序集，便于 NativeAOT 裁剪。Core 和 Abstractions 不依赖任何具体平台。

---

## 各项目当前状态（原始交接快照，仅供溯源）

### 1. `Platform.Abstractions/` ✅ 接口已定义

已写好的 5 个核心接口：

| 文件 | 接口 | 说明 |
|---|---|---|
| `IMouseHook.cs` | `IMouseHook` + `MouseEventData` + `MouseMessageType` | 鼠标钩子抽象 |
| `ISystemMetrics.cs` | `ISystemMetrics` | 系统指标（拖拽阈值/双击时间/矩形） |
| `IWindowFocusController.cs` | `IWindowFocusController` | 不抢焦点窗口控制 |
| `ISelectionTextCapture.cs` | `ISelectionTextCapture` + `CaptureResult` + `CaptureSource` + `SelectionGesture` | 取词降级链 + 手势数据 |
| `IClipboardAccess.cs` | `IClipboardAccess` + `ClipboardSnapshot` | 剪贴板访问（序列号/备份/恢复/订阅） |

**下一步**：无需改动，Windows 实现去 `Platform.Windows/`。

### 2. `Core/` 🚧 部分完成

| 文件 | 状态 | 说明 |
|---|---|---|
| `Capture/ProcessCapturePolicy.cs` | ✅ 完成 | 可组合 record + SimulatedCopyMode enum + Default |
| `Capture/ProcessPolicyResolver.cs` | ✅ 完成 | 优先级匹配（路径>bundleId>进程名>默认） |
| `Selection/SelectionSessionManager.cs` | ❌ 待写 | **P1.3 架构核心**，代码在 v4 §5.1 |
| `Selection/SystemMetricGestureClassifier.cs` | ❌ 待写 | P1.2 轴式几何判定 |
| `Selection/IGestureClassifier.cs` | ❌ 待写 | 手势判定抽象 |

**下一步**：先写 `SelectionSessionManager`（v4 §5.1 代码可直接用），再写几何判定。

### 3. `Platform.Windows/` ❌ 待迁移

空。需要从 spike 迁移：
- `Hooks/LowLevelMouseHook.cs` ← spike `LowLevelMouseHook.cs`（实现 `IMouseHook`）
- `Hooks/WindowsMouseHookAdapter.cs` ← 新写，把 spike 的 `Action<int,int,int>` 事件适配成 `MouseEventData`
- `Capture/UIAutomationTextCapture.cs` ← 新写（P1.4）
- `Capture/Win32ClipboardCapture.cs` ← 新写（P1.5）
- `Clipboard/Win32Clipboard.cs` ← 新写（P1.5，实现 `IClipboardAccess`）
- `Windowing/NoActivateWindowHost.cs` ← spike `MainWindow.axaml.cs` 的 `ShowAtNoActivate` 抽出
- `WindowsSystemMetrics.cs` ← 新写（实现 `ISystemMetrics`，调 `GetSystemMetrics`）

**下一步**：先迁移 `LowLevelMouseHook`（改动最小，spike 已验证），让骨架能跑起来。

### 4. `UI/` ❌ 待迁移

空。需要：
- `Views/ToolbarWindow.axaml(.cs)` ← spike `MainWindow.axaml(.cs)`（实现 `IWindowFocusController`）
- `Views/ResultWindow.axaml(.cs)` ← 新写占位
- `ViewModels/` ← 后续（P2-P4）

### 5. `Infrastructure/` ❌ 待迁移

空。需要：
- `Logging/RedactedLogger.cs` ← spike `SpikeLog.cs`（改名 + 脱敏）
- `Secrets/` ← 后续（P5，DPAPI / Keychain）

### 6. `App/` 🚧 占位

- `SelectionAssistant.App.csproj` ✅ 配置好（含 Avalonia + NativeAOT Release 配置 + 所有项目引用）
- `app.manifest` ✅ per-monitor DPI 感知
- `Program.cs` ❌ 待写（入口 + 依赖注入装配）
- `App.axaml(.cs)` ❌ 待写

### 7. 测试项目 ❌ 待写

- `Core.Tests/` ← 单元测试（手势判定、会话管理、策略解析）
- `Windows.IntegrationTests/` ← 集成测试（钩子、UIA、剪贴板压力测试）

---

## 验收检查点

搭完骨架后必须确认：
1. `dotnet build SelectionAssistant.slnx` 全解决方案 0 错误
2. 项目引用无循环
3. Windows 项目正确指向 `net10.0-windows`
4. NativeAOT 的 Release 配置只在 App 项目（其他项目不 AOT）

> **注**：交接时骨架的 App 项目还缺 `Program.cs`/`App.axaml`，所以**整体编译会失败**（App 项目引用了不存在的文件）。接手 Agent 先补上这两个文件让骨架编译通过，这是 P1.0 的收尾。

---

## NativeAOT 配置（已在 App.csproj 里）

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <PublishAot>true</PublishAot>
  <TrimMode>full</TrimMode>
  <TrimmerSingleWarn>false</TrimmerSingleWarn>
  <DynamicLoading>false</DynamicLoading>
</PropertyGroup>
```

发布命令：
```bash
cd src/SelectionAssistant.App
dotnet publish -c Release -r win-x64
# 产物在 bin/Release/net10.0-windows/win-x64/publish/
```

Phase 0 实测体积：exe 本体 18.46MB，总发布 ~36MB（含 SkiaSharp/GPU dll）。
