# R42 交接包 — 剩余任务（P3 + P4 + 验证）

## 项目根目录
`C:\dvr\gh-kb\selection-assistant`

## 上下文

Ocean Eyes 是 BYH 的视觉模型框选功能。R40 引入（QuickTools 面板退役 → Ctrl+Alt+Q 直进框选）。R41 加了惰性 OCR + 左键确认 + 右键重画（通过 mouse hook SwallowCheck）。R42 正在重做交互——overlay 不再确认后 Hide，改为锁定状态等待用户按键；右键重画由 overlay 自己处理（不再需要 mouse hook 吞键）。

**P0-P2 已完成并编译通过（UI 项目 0 警告 0 错误）。** 剩余 P3 + P4 + 验证。

---

## P3 — 删除 R41 的 mouse hook SwallowCheck 机制

overlay 现在不 Hide，右键由 overlay 的 OnCanvasPointerPressed 自己处理，SwallowCheck 不再需要。

### 文件 1: `src/SelectionAssistant.Platform.Abstractions/IMouseHook.cs`

删掉 `SwallowCheck` event 声明（含上面的注释块，共约 8 行）。其余不动。目标效果：

```csharp
public interface IMouseHook : IDisposable
{
    event Action<MouseEventData>? MouseEvent;
    // SwallowCheck 已删
    void Start();
}
```

### 文件 2: `src/SelectionAssistant.Platform.Windows/Hooks/LowLevelMouseHook.cs`

**A.** 删 event 声明（约 line 43-44）：
```
    /// <inheritdoc />
    public event Func<MouseEventData, bool>? SwallowCheck;
```

**B.** HookCallback 中删 swallow 检查（约 line 178-189）。当前：
```csharp
                    // R41: let subscribers veto (swallow)...
                    if (ShouldSwallow(eventData))
                    {
                        // Swallow: return non-zero...
                        return 1;
                    }

                    RaiseMouseEventSafely(eventData);
```
改为只保留：
```csharp
                    RaiseMouseEventSafely(eventData);
```

**C.** 删 `ShouldSwallow` 方法（约 line 220-238，整段删除）：
```csharp
    private bool ShouldSwallow(MouseEventData eventData)
    {
        Delegate[] handlers = SwallowCheck?.GetInvocationList() ?? [];
        foreach (Func<MouseEventData, bool> handler in handlers.Cast<Func<MouseEventData, bool>>())
        {
            try
            {
                if (handler(eventData))
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                Trace.TraceError($"Mouse swallow-check handler failed: {exception}");
            }
        }
        return false;
    }
```

### 文件 3: `src/SelectionAssistant.App/SelectionRuntime.cs`

**A.** 删 `RegionResetRequested` event 声明（约 line 281-288）：
```csharp
    public event Action? RegionResetRequested;
```
（含上面的 XML doc 注释，共约 8 行）

**B.** Start() 中删 SwallowCheck 订阅（约 line 1823-1824）：
```csharp
        _mouseHook.SwallowCheck += OnMouseSwallowCheck;
```
（含上面的注释，共约 3 行）

**C.** Dispose() 中删 SwallowCheck 取消订阅（约 line 1975）：
```csharp
        _mouseHook.SwallowCheck -= OnMouseSwallowCheck;
```

**D.** 删 `OnMouseSwallowCheck` 方法（约 line 1862-1898，整段删）：
```csharp
    private bool OnMouseSwallowCheck(MouseEventData mouseEvent)
    {
        if (mouseEvent.Message != MouseMessageType.RightButtonDown) return false;
        if (Volatile.Read(ref _oceanEyesActive) == 0) return false;
        try
        {
            DismissOceanEyes();
            RegionResetRequested?.Invoke();
            _logger.Info("OceanEyes", "Right-click swallowed → region reset requested.");
        }
        catch (Exception exception)
        {
            _logger.Error("OceanEyes", "Right-click swallow handler failed.", exception);
        }
        return true;
    }
```

---

## P4 — App.axaml.cs：截图竞态修复 + 删 RegionResetRequested 订阅

### 文件: `src/SelectionAssistant.App/App.axaml.cs`

**改动 A — 删 RegionResetRequested 订阅（约 line 215-216）**：

当前：
```csharp
                _runtime.RegionResetRequested += () =>
                    Dispatcher.UIThread.Post(() => _regionOverlay?.Reset());
```
**删掉这两行**。overlay 的右键重画现在由 overlay 自己处理（OnCanvasPointerPressed 右键分支 → Reset()），不需要 runtime 中转。

**改动 B — `RunOceanEyesCaptureAsync` 方法整体重写**：

当前代码（约 line 894-933）：
```csharp
    private async Task RunOceanEyesCaptureAsync(int x, int y, int w, int h)
    {
        if (_runtime is null) return;
        await WaitForCompositorSettleAsync().ConfigureAwait(true);
        byte[]? png = ScreenRegionCapture.CaptureAsPng(x, y, w, h);
        if (png is null) return;
        int anchorX = x + w;
        int anchorY = y;
        _runtime.ShowToolbarForOceanEyes(anchorX, anchorY, png, x, y, w, h);
    }
```

**改为**：
```csharp
    private async Task RunOceanEyesCaptureAsync(int x, int y, int w, int h)
    {
        if (_runtime is null || _regionOverlay is null)
        {
            return;
        }

        // R42: overlay is still visible after Confirm (locked frame). Temporarily
        // Hide it so BitBlt captures a clean screenshot (no dim mask, no white
        // dashed border, no resize handles).
        _regionOverlay.Hide();
        await WaitForCompositorSettleAsync().ConfigureAwait(true);

        byte[]? png = ScreenRegionCapture.CaptureAsPng(x, y, w, h);
        if (png is null)
        {
            _regionOverlay.Cancel();
            return;
        }

        // Restore overlay to the locked-confirmed state (DimMask hole visible,
        // rect white dashed border visible, handles visible).
        _regionOverlay.ShowConfirmed();

        int anchorX = x + w;
        int anchorY = y;

        // Show the toolbar in "未识别" state. OCR deferred to first action key (R41).
        _runtime.ShowToolbarForOceanEyes(anchorX, anchorY, png, x, y, w, h);
    }
```

---

## 验证清单

完成 P3 + P4 后：

1. `dotnet build src/SelectionAssistant.App/SelectionAssistant.App.csproj -c Debug` — 目标 0 警告 0 错误
2. `dotnet test tests/SelectionAssistant.Core.Tests/SelectionAssistant.Core.Tests.csproj -c Debug` — 目标 145/145
3. `dotnet test tests/SelectionAssistant.Providers.Tests/SelectionAssistant.Providers.Tests.csproj -c Debug --no-build` — 目标 35/35
4. `dotnet test tests/SelectionAssistant.Windows.IntegrationTests/SelectionAssistant.Windows.IntegrationTests.csproj -c Debug` — 目标 41/41
5. `dotnet publish src/SelectionAssistant.App/SelectionAssistant.App.csproj -c Release -r win-x64` — 目标 0 警告
6. 把 `src/SelectionAssistant.App/bin/Release/net10.0-windows/win-x64/publish/BYH.exe` 复制到 `artifacts/publish/win-x64-nativeuia/BYH.exe`，重启 BYH.exe
7. 机器侧验证：overlay 中间透明（白虚线框内看到桌面）+ 外部 dim；单击=确认 UIA 框；框内拖动=重画；右键=重画；截图干净（无 overlay 装饰）；F/J/Z/R/C 惰性 OCR；Enter 存图；Esc 退出

## 不要做的事

- 不要改 SelectionRuntime 的惰性 OCR（EnsureOceanEyesOcrAsync / DispatchToolbarActionKey）
- 不要改 ToolbarShortcutSettings（R41 已删 PasteKey）
- 不要改 IMouseHook.MouseEvent（只删 SwallowCheck）
- 不要改 RegionSelectOverlay.axaml(.cs) 的 P0-P2 改动（已完成）
- 不要改 OceanEyesTriggerSettings / OceanEyesCaptureSettings
- 不要引入新文件

## 测试结果基线（P0-P2 后）

- Core: 145/145 | Providers: 35/35 | Windows: 41/41 | Total: 221/221
- Publish exe (R41): 26,966,016 bytes
