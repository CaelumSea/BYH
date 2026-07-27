# Phase 0 验证报告

**日期**: 2026-07-16
**状态**: ✅ 三道硬关卡全部通过
**Spike 项目**: `C:\Users\DeRant Vilmon Ram\phase0\SelectionSpike\`

---

## 验证目标

回答方案 v4 的前置风险问题：**Avalonia + .NET 10 + Win32 互操作能否实现"选中文字不抢焦点地弹出工具栏"？**

这是整个产品的前提——如果弹窗会抢走源应用的焦点，选中高亮消失，取词链就无法工作。

## 三道门

### 门 1：WH_MOUSE_LL 低级鼠标钩子 ✅

**验证内容**：在 Avalonia 宿主进程内，于专用线程安装全局低级鼠标钩子，捕获鼠标坐标，不阻塞 UI。

**结果**：通过。

**证据**（诊断日志 `%TEMP%\SelectionSpike.log`）：
```
[Hook] ✓ 钩子已安装 (handle=312085503, thread=7)
[Gesture] UP dx=614 dy=134 elapsed=594ms    ← 精确捕获拖拽
[Gesture] UP dx=431 dy=15 elapsed=328ms
[Gesture] UP dx=0 dy=0 elapsed=94ms          ← 精确识别单击
```

**关键结论**：
- `SetWindowsHookEx(WH_MOUSE_LL, ...)` 在专用后台线程（`Priority=Normal`，非 Highest）上稳定工作
- 回调线程与 Avalonia UI 线程分离，回调只做轻量工作（记录坐标 + `CallNextHookEx`）
- 跨线程传递通过 `Dispatcher.UIThread.Post` 切回 UI 线程（**必须**，否则 Avalonia 抛 `InvalidOperationException: 不同线程拥有此对象`）
- 钩子委托（`HookProc`）必须用实例字段保活，否则 GC 回收导致崩溃

### 门 2：WS_EX_NOACTIVATE 不抢焦点 ✅（命脉）

**验证内容**：Avalonia 创建的窗口，通过 Win32 注入 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST` 扩展样式后，显示时不抢焦点——**源应用的文字选中高亮必须保住**。

**结果**：通过。**用户实测确认：在记事本中选中文字（蓝色高亮）→ 松开鼠标 → 弹窗出现 → 蓝色高亮保持不消失。**

**关键注入方式**（`MainWindow.axaml.cs`）：
```csharp
// 1. 拿到 Avalonia 窗口的底层 HWND
var handle = TryGetPlatformHandle()?.Handle;

// 2. 注入扩展样式
int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

// 3. 定位 + 显示时带 SWP_NOACTIVATE / SW_SHOWNOACTIVATE
SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0,
    SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOSIZE | SWP_NOZORDER);
if (!IsVisible) ShowWindow(hwnd, SW_SHOWNOACTIVATE);
```

**关键结论**：
- Avalonia 12.1 的 `TryGetPlatformHandle()` 能拿到底层 HWND，Win32 互操作可行
- `WS_EX_NOACTIVATE` 在 Avalonia 宿主窗口上**确实生效**，焦点不被抢
- 这验证了 v4 §7.2 的"no-activation hard gate"——无需 fallback 到原生工具栏宿主
- 注意：`WS_EX_NOACTIVATE` 的副作用是窗口收不到键盘事件（F8 热键失效）——这是预期行为，工具栏的交互应通过鼠标点击完成

### 门 3：鼠标抬起时在坐标处弹出 + 基础手势判定 ✅

**验证内容**：钩子捕获 mouse-up → 判定拖拽 vs 单击 → 在鼠标坐标处调用 `ShowAtNoActivate` → UI 线程更新窗口文本。

**结果**：通过。

**基础手势判定**（spike 版本，非正式）：
```csharp
// 最简判定：移动 > 8px 或 时长 > 150ms 视为拖拽
bool isSelectionLike = dx >= 8 || dy >= 8 || elapsed > 150;
```

**关键结论**：
- 钩子线程 → UI 线程的完整链路打通：`HookCallback → MouseEvent → OnMouseEvent → Dispatcher.UIThread.Post → ShowAtNoActivate + UpdateCoordText`
- `SetWindowPos` 返回 True，Win32 错误码 0，定位精确
- **正式版**需替换为 v4 §5.2 的轴式系统指标判定（`SM_CXDRAG`/`SM_CYDRAG`/`GetDoubleClickTime`）

---

## 过程中遇到的问题与修复

| 问题 | 原因 | 修复 |
|---|---|---|
| 跨线程崩溃 `InvalidOperationException` | 钩子线程直接访问 `IsVisible` 等 UI 属性 | 所有 UI 操作包进 `Dispatcher.UIThread.Post` |
| 程序运行但"没弹窗" | 初始版本 `Debug.WriteLine` 不可见，且无诊断 | 加 `SpikeLog` 写 `%TEMP%\SelectionSpike.log` |
| F8 热键无反应 | `WS_EX_NOACTIVATE` 使窗口收不到键盘事件 | 预期行为，改用诊断日志替代 |

---

## NativeAOT 体积测试 ✅

**状态**：通过。编译成功 + 运行正常 + 体积实测。

**前置条件**：NativeAOT 在 Windows 上需要 MSVC 链接器（C++ 桌面开发工作负载）。本机初始未安装，报错 `Platform linker not found`。已通过 `winget install Microsoft.VisualStudio.2022.BuildTools` + `VCTools` 工作负载解决。

**编译**：`dotnet publish -c Release -r win-x64` → `Generating native code` → 成功（exit 0）。

**体积实测**（`bin/Release/net10.0/win-x64/publish/`）：

| 文件 | 体积 | 说明 |
|---|---|---|
| `SelectionSpike.exe` | **18.46 MB** | NativeAOT 本体（含 .NET 运行时） |
| `libSkiaSharp.dll` | 11.09 MB | Skia 绘图引擎（Avalonia 渲染依赖） |
| `av_libglesv2.dll` | 5.14 MB | ANGLE GPU 转译层 |
| `libHarfBuzzSharp.dll` | 1.73 MB | 字体整形 |
| `.pdb` × 4 | ~170 MB | 调试符号（发布时用 `<StripSymbols>true</StripSymbols>` 剔除） |

**结论**：
- **AOT 本体 18.46 MB** — v1 评审质疑的"≤20MB"对 exe 本体**成立**
- **总发布体积 ≈ 36 MB**（exe + 三个 Skia/GPU 原生 dll）— SkiaSharp 才是大头，非 .NET/AOT 本身
- **运行内存 78 MB**（vs Debug 版 122 MB）— AOT 版更省内存
- **运行验证**：AOT 版的钩子、窗口、手势判定、日志全部正常，与 Debug 版行为一致
- Avalonia 12.1 + .NET 10 NativeAOT **兼容性良好**，无裁剪导致的运行时崩溃

**对 v1 评审质疑的正式回答**：
> "≤20MB"作为 AOT 本体体积成立（18.46MB）。但完整 Avalonia 应用的发布体积约 36MB，大头是 SkiaSharp/GPU 原生依赖而非 .NET 运行时。若要进一步压缩，方向是精简 Avalonia 渲染依赖（如禁用 GPU 用软件渲染），而非优化 AOT 本身。v4 §13.3 保留非 AOT 发布方案的决策依然合理——但 AOT 已被证明可行，可作为首选发布路径。

---

## Spike 代码索引

| 文件 | 职责 | 迁移目标（v4 §4） |
|---|---|---|
| `LowLevelMouseHook.cs` | WH_MOUSE_LL 钩子，专用线程 + 消息循环 | `SelectionAssistant.Platform.Windows/Hooks/` |
| `MainWindow.axaml(.cs)` | 透明置顶无边框窗口 + WS_EX_NOACTIVATE 注入 | `SelectionAssistant.UI/Views/ToolbarWindow.axaml(.cs)` |
| `Program.cs` | 钩子→窗口接线 + 手势判定 | 拆分：判定逻辑入 `Core`，接线入 `App` |
| `SpikeLog.cs` | 轻量诊断日志 | `SelectionAssistant.Infrastructure/Logging/` |
| `App.axaml(.cs)` | Avalonia 应用入口 + 创建主窗口 | `SelectionAssistant.App/` |

---

## 下一步

进入 **Phase 1: Selection + Capture**（v4 §13.3，5-6 天）。详见 `Phase1-Tasks.md`。
