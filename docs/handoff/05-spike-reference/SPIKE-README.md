# Spike 参考代码说明

> 这里是 Phase 0 验证用的 spike 项目源码。**已通过三道门 + NativeAOT 验证**。正式开发时作为迁移参考——这些代码已经证明能跑，搬进正式骨架 + 抽接口即可，不需要重新发明。

**spike 原始位置**：`C:\Users\DeRant Vilmon Ram\phase0\SelectionSpike\`

---

## 文件清单与迁移目标

| spike 文件 | 作用 | 迁移到正式骨架 | 迁移时改动 |
|---|---|---|---|
| `LowLevelMouseHook.cs` | WH_MOUSE_LL 钩子，专用线程 + 消息循环 | `Platform.Windows/Hooks/LowLevelMouseHook.cs` | 实现 `IMouseHook` 接口；事件类型改为 `MouseEventData` |
| `MainWindow.axaml(.cs)` | 透明置顶无边框窗口 + WS_EX_NOACTIVATE 注入 | `UI/Views/ToolbarWindow.axaml(.cs)` | 把 `ShowAtNoActivate` 抽到 `IWindowFocusController`；去掉诊断用的 `SpikeLog` 调用 |
| `Program.cs` | 钩子→窗口接线 + 手势判定 | 拆分：判定逻辑 → `Core/Selection/BasicGestureClassifier.cs`；接线 → `App/Program.cs` | 手势判定换成 `IGestureClassifier`，用系统指标（P1.2） |
| `SpikeLog.cs` | 轻量诊断日志（写 `%TEMP%\SelectionSpike.log`） | `Infrastructure/Logging/RedactedLogger.cs` | 改名；加脱敏（不记录选中文本/密钥） |
| `App.axaml(.cs)` | Avalonia 应用入口 | `App/App.axaml(.cs)` | `OnMainWindowCreated` 改为正式的依赖注入装配 |
| `SelectionSpike.csproj` | 项目配置 | 参考，不直接迁移 | 正式项目已按多项目结构建好 |
| `app.manifest` | DPI 感知声明 | `App/app.manifest` | 已复制到正式骨架 |

---

## 关键代码片段（已验证有效）

### 1. 钩子安装（专用线程 + 消息循环）

```csharp
// LowLevelMouseHook.cs —— 核心结构
_hookThread = new Thread(ThreadProc) {
    IsBackground = true,
    Name = "MouseHookThread",
    Priority = ThreadPriority.Normal,  // 不是 Highest!
};
_hookThread.Start();

private void ThreadProc() {
    _hookProc = HookCallback;  // 字段保活,防 GC
    IntPtr hModule = GetModuleHandle(null);
    _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, hModule, 0);
    // ... GetMessage 循环驱动回调 ...
}
```

### 2. WS_EX_NOACTIVATE 注入（门2 命脉）

```csharp
// MainWindow.axaml.cs —— ApplyNoActivateFlags
var hwnd = TryGetPlatformHandle()?.Handle;  // Avalonia 12.x 的公开 API
int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

// 显示时带 NOACTIVATE 标志
SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0,
    SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOSIZE | SWP_NOZORDER);
if (!IsVisible) ShowWindow(hwnd, SW_SHOWNOACTIVATE);
```

### 3. 跨线程 UI 访问（坑 1 的修复）

```csharp
// Program.cs —— OnMouseEvent(钩子线程触发)
int cx = x, cy = y;  // 捕获到局部变量
Dispatcher.UIThread.Post(() => {
    if (_mainWindow == null) return;
    _mainWindow.ShowAtNoActivate(cx, cy);  // 切到 UI 线程!
});
```

---

## 验证日志样例（证明三道门通过）

`%TEMP%\SelectionSpike.log` 典型内容：
```
13:10:55 [Hook] 回调 #2 msg=514 (2099,1581)              ← 门1: 钩子捕获坐标
13:10:55 [Gesture] UP dx=431 dy=15 elapsed=328ms          ← 门3: 手势判定
13:10:55 [Gesture] → 判定为拖拽,准备弹窗
13:10:55 [Window] ShowAtNoActivate pos=(1398,1011)        ← 门3: 定位弹窗
13:10:55 [Window] SetWindowPos 返回 True,Win32 错误 = 0
13:10:55 [UI] 弹窗调用完成 (UI 线程)
```

门2（不抢焦点）由用户实测确认：在记事本选中文字 → 弹窗出现 → **蓝色高亮保住**。

---

## 诊断日志用法

spike 用 `SpikeLog.Log(msg)` 写 `%TEMP%\SelectionSpike.log`。调试时：
```bash
# 实时看日志
tail -f "/c/Users/DeRant Vilmon Ram/AppData/Local/Temp/SelectionSpike.log"
```

正式版用 `RedactedLogger`，路径改为应用数据目录，且**不记录选中文本和密钥**。
