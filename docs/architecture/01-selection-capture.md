# 01 · 选词链路（Selection Capture）

> **改取词/钩子/手势/会话管理前先读本文件。**
> 架构总览见 `00-architecture-overview.md`。

---

## 职责一句话

从"用户拖选文字松开鼠标"到"拿到选中文本 + 显示工具条"的完整链路，含全局鼠标钩子、轴式手势判定、UIA 取词、剪贴板回退、并发会话管理。

## 关键文件

| 文件 | 职责 |
|---|---|
| `Platform.Windows/Hooks/LowLevelMouseHook.cs` | WH_MOUSE_LL 全局钩子；原生线程消息泵；始终放行（CallNextHookEx） |
| `Platform.Abstractions/MouseEventData.cs` | 平台无关鼠标事件（坐标/类型/时间戳/是否注入） |
| `Core/Selection/ChordDetector.cs` | 左右键同按（chord）判定；400→600ms 时间窗口；触发后等双键释放才重置 |
| `Core/Selection/SelectionGestureClassifier.cs` | 轴式拖拽/双击判定（SM_CXDRAG/SM_CYDRAG 矩形指标） |
| `Core/Selection/SelectionSessionManager.cs` | 取词会话生命周期；并发安全；75ms 防闪烁；过期会话 generation 守卫 |
| `Platform.Windows/Capture/WindowsSelectionTextCapture.cs` | UIA TextPattern2→TextPattern 取词 + 剪贴板回退 |
| `Platform.Windows/Capture/SendInputHelper.cs` | 注入 Ctrl+Insert（复制）/ Ctrl+V（粘贴）到源应用 |
| `Core/Capture/IProcessCapturePolicyProvider.cs` | 进程策略（终端只注入 Ctrl+Insert；高完整性降级） |
| `App/SelectionRuntime.cs` | 组合根；`OnMouseEvent` 分发（chord 优先 → dismiss → 手势 → 取词） |

## 数据流

```
WH_MOUSE_LL 钩子（原生线程 HookCallback）
  → RaiseMouseEventSafely(MouseEventData)  // 投射成平台无关事件
  → SelectionRuntime.OnMouseEvent（钩子线程）
    ├── 先喂 ChordDetector（chord 优先，触发则 return）
    ├── 工具条可见且点在外部 → DismissCurrentSessionAsync
    └── SelectionGestureClassifier.Process → 若是"选择"手势
        → 进程策略检查 DetectionEnabled
        → SelectionSessionManager.StartOrReplaceSessionAsync
            ├── 立即启动 UIA 取词任务（并发）
            ├── Task.Delay(75ms) 防闪烁
            ├── ShowToolbar（Dispatcher 切 UI 线程）
            ├── 等 UIA 取词完成 → SetCaptureResult（generation 守卫）
            └── _lastCapturedText = 取到的文本（供 QuickTools 快捷键/chord 复用）
```

## 关键方法/类

- `LowLevelMouseHook.HookCallback` — **绝不在回调里碰 UI**；只投射事件后立即 CallNextHookEx。
- `ChordDetector.OnMouseEvent` — down 事件 TryFire（双键在 600ms 内）；up 事件重置 `_bothDown`。
- `SelectionSessionManager.SessionCoreAsync` — 取词 + 防闪烁并发；每次 UI 写入前 `IsCurrent(session)` 守卫防旧会话复活。
- `SelectionRuntime.OnMouseEvent` — **chord 优先于 dismiss**（chord 触发时 return，不走 dismiss 逻辑）。

## 不变量 / 踩坑

- **钩子始终放行**：CallNextHookEx 永远调用，否则源应用右键菜单失效。
- **取词立即启动**，不等 75ms（防闪烁延迟与取词并发）。
- **generation 守卫**：每次 UI 写入检查 session id；热切换/新会话后旧 chunk 被丢弃。
- **chord up 事件必须到达检测器**才能重置——若 UI 线程被阻塞（如旧的 Activate 重入循环），up 事件虽到但面板不更新。详见 `02-windowing.md` 的 QuickTools grace window 踩坑。
- `_lastCapturedText` 供 QuickTools 触发流程复用；若 BYH 会话没取到词，`GetLastCapturedText` 回退到剪贴板读取。

## 改动检查清单

- [ ] 改钩子：确认仍 CallNextHookEx；确认不在回调碰 UI。
- [ ] 改手势判定：轴式（矩形），不用欧氏距离。
- [ ] 改会话管理：每次 UI 写入前 generation 守卫；过期会话不复活。
- [ ] 改取词：UIA 失败有剪贴板回退；高完整性进程安全降级。

---

## R24 视觉取词（已实现，R25 后默认由全局快捷键进入画框模式）

> 轨道 A（UIA 强化）保留；轨道 B① 改为 **全局快捷键（默认 Ctrl+Alt+Q）→ QuickTools → 画框 → OCR**（用户控制区域），不再在划词里自动截图。左右键 chord 默认关闭，仅作可选兼容入口。

### 设计演进（重要）
- **初版**：划词 phase 1 空时自动截图一大块 → OCR。问题：用户控不了区域，经常截错。
- **现版**：视觉 OCR 从划词路径**完全移除**，改成显式触发：`全局快捷键（默认 Ctrl+Alt+Q；chord 可选）→ QuickTools 面板 → 点"📐 画框识别文字"→ 全屏遮罩画框/调整 → 回车确认 → OCR → 文字进剪贴板 + 弹回 QuickTools`。

### 划词路径（现在的全部）
```
UIA(TextPattern→选区→DocumentRange→Value) → 剪贴板(Ctrl+Insert/Ctrl+C)
有文本 → 出工具条（快路径）
无文本 → 不显示（R20 守卫，无自动 OCR）
```

### QuickTools 画框 OCR 流程（R25 当前版）
```
全局快捷键（默认 Ctrl+Alt+Q；chord 可选）→ QuickTools 面板
  └─ 点"📐 画框识别文字" → 记触发坐标 → 关 QuickTools → 开 RegionSelectOverlay
     ├─ 默认（UiaPrefillEnabled=false）：直接 free-draw，用户手动画框
     └─ 可选（UiaPrefillEnabled=true）：UIA 预填框跟随鼠标（MarkInvisibleToUia 让 UIA 跳过 overlay）
        一旦 PointerPressed（画/移/调）→ 锁存 _userTouchedRect，停止跟随
     空白拖拽=重画 / 拖矩形内=移动 / 拖手柄=调边角 / 双击或回车=确认 / ESC=取消
  确认 → overlay Hide() → WaitForCompositorSettle(3帧+150ms) → CaptureAndRecognizeRegionAsync(物理像素框, 10s超时)
       ├─ 默认：直接 OCR（框内即所得）
       └─ 可选（UiaPrefillEnabled=true）：先 UIA GetTextsInRegion 扫框内文字 → 空才 OCR
       → ScreenRegionCapture 截图 → OpenAiCompatibleVisionOcrClient OCR（CleanOcrText 去 <think>，按需 enable_thinking:false）
       → QuickTools.ShowOcrResult（文字进剪贴板 + 弹回面板，翻译/解释/复制直接可用）
```

**默认模型**：`Qwen/Qwen3.5-4B` + `disableThinking=true`（<1s，干净准确）。之前用 `deepseek-ai/DeepSeek-OCR` 严重幻觉已弃。

### 关键文件
- `WindowsUiAutomationBackend.cs` — 轨道 A 三趟 + `GetElementBoundsAt`（UIA 预填，opt-in）+ 🆕 `GetTextsInRegion`（框内文字 BFS 扫描，opt-in）+ `FindSmallestContainingAncestor`（走祖先找最小容器）
- `ScreenRegionCapture.cs` — Win32 `BitBlt` → 手写 PNG → base64（复用，不变）
- `OpenAiCompatibleVisionOcrClient.cs` — 多模态 OCR + `CleanOcrText`（去 `<think>`）+ `RecognizeRawAsync`（诊断用原始 body）+ 🆕 `disableThinking` 参数（按需发 `enable_thinking:false`）
- `RegionSelectOverlay.axaml(.cs)` — 全屏遮罩 + 8 手柄调整 + 拖拽画框 + `EnableLiveTracking`/`TryLiveTrack`（opt-in 节流 UIA 跟踪）+ `MarkInvisibleToUia`（设 `UIA_WindowVisibilityOverridden=2` prop）
- `SelectionRuntime.cs` — `GetInitialRegionAt`（UIA 预填）+ `CaptureAndRecognizeRegionAsync`（🆕 UIA tier opt-in → OCR tier 默认，两阶段）
- `QuickToolsWindow.axaml(.cs)` — "画框 OCR" 按钮 + `RegionOcrRequested` 事件 + `ShowOcrResult` + 记触发坐标
- `App.axaml.cs` — overlay 构造 + `OnRegionOcrRequested`（按 `UiaPrefillEnabled` 决定是否接 live tracker）/`OnRegionSelected`/`RunRegionOcrAsync` + `WaitForCompositorSettleAsync`（3 帧 + 150ms）
- `Program.cs` — `--probe-ocr-raw`（原始 body 诊断）+ 🆕 `--probe-uia-region`（UIA 框内扫描诊断）+ `--probe-bounds`/`--probe-save-region`/`--probe-vision`
- `SelectionSessionManager.SessionCoreAsync` — **删除** phase 2（划词不再 OCR）；`ISelectionSessionView.ShowVisionPending` 已清
- `vision.json` / `VisionCaptureStore.cs` / `VisionCaptureSettings.cs` — 视觉设置（enabled/providerId/model/ocrPrompt/uiaPrefillEnabled/disableThinking），默认 SiliconFlow + Qwen3.5-4B + 关思考
- 设置页"视觉识别"卡片（ToggleSwitch + Provider/模型下拉 + 提示词）

### 不变量 / 踩坑
- **配置全在 UI**：设置页"+ 添加 Provider" → SiliconFlow（预设，自动生成 `secret://provider/siliconflow`）→ 填密钥。`vision.json` 默认 SiliconFlow + Qwen3.5-4B + 关思考，开箱即用。
- **OCR Provider 必须和模型匹配**：`Qwen/Qwen3.5-4B` 在 SiliconFlow 上可用。视觉卡片 OCR Provider 选 **SiliconFlow**。
- **DeepSeek-OCR 已弃**（第十二批）：`deepseek-ai/DeepSeek-OCR` 在桌面截图上严重幻觉（输出完全不相关内容如百度贴吧、菜谱）。换 `Qwen/Qwen3.5-4B`（关思考，<1s，干净准确）。如果用户 vision.json 还指向 DeepSeek-OCR，建议改。
- **OCR client 不发 thinking 参数**：OCR 专项模型拒绝未知参数（SiliconFlow code 20015）。`OpenAiCompatibleVisionOcrClient.BuildRequestBody` 不发 `thinking`/`enable_thinking`。
- **PNG chunk 顺序**：`WriteChunk` 按 spec `[length][type][data][CRC]`（曾写反导致全失败）。
- **overlay 逻辑坐标→物理像素必须乘 RenderScaling**（方向与 chord 定位相反——chord 定位那个值本来就是物理像素，乘了是双重缩放；overlay 是 UI 逻辑坐标转物理，必须乘）。确认时 `_rectLeft * RenderScaling` 等。
- **overlay 单屏**：先覆盖主屏 WorkingArea；多屏拖拽以后处理。
- **UIA 自动框查询时机**（踩坑，曾导致"没有预填框"）：`OnRegionOcrRequested` 里调 `GetInitialRegionAt(x,y)` → `ElementFromPoint`，**必须在 QuickTools 面板真正从合成器消失之后**查。Avalonia 的 `Window.Hide()` 只翻 `IsVisible=false`，下一帧才从合成图里拿掉；同 tick 查 UIA 会命中 QuickTools 面板自身（它就在 chord+16 处），返 null → overlay 进 free-draw。修法：`OnRegionOcrRequested` 用一个 zero-interval `DispatcherTimer` 延后一帧再查 UIA + 显示 overlay。
- **截图捕获竞态**（踩坑，曾导致"OCR 文字全乱"）：`OnRegionSelected` 确认后 overlay `Hide()`，但 `BitBlt` 读的是**当前合成后的屏幕**。遮罩（`#80000000` 半透明黑）+ 亮蓝选择框 + 8 手柄 + 尺寸徽章还没从合成图里消失，OCR 模型拿到的是带遮罩的脏屏 → 乱码。`--probe-vision` 不经过 overlay 所以一直正常，正是为什么机器侧全过但真机乱码。修法：`RunRegionOcrAsync` 先 `await WaitForCompositorSettleAsync()`（两次 `DispatcherPriority.Background` 回调 + 80ms 固定 delay）让遮罩真正从屏幕消失，再 `BitBlt`。**确认后不要先弹"识别中…" QuickTools**：面板落在 chord+16，落在识别区内，会在截图时把自己拍进去——拿到结果再弹。
- **COM apartment 冲突**（踩坑，最深层根因）：`WindowsUiAutomationBackend.EnsureInitialized()` 调 `CoInitializeEx(0, COINIT_MULTITHREADED)`，但 Avalonia UI 线程是 STA（`[STAThread]`），返回 `RPC_E_CHANGED_MODE` → `_automation` 从未创建 → UIA 后端整条路径静默失败。phase-1 能工作是因为它跑在专用 MTA worker 线程（`UiAutomationWorker._thread.SetApartmentState(MTA)`）。修法：`GetElementBoundsAt` 通过专用 MTA worker 线程（`_boundsThread` + `BlockingCollection<Action>`）分发，`EnsureBoundsThread()` lazy 启动，`Dispose()` 时 `CompleteAdding()` 退出。
- **vtable 槽位错误**（踩坑，曾导致"自动框位置全错"）：`get_CurrentBoundingRectangle` 原用 slot 89（远超 vtable 末尾 → 调垃圾内存返 S_OK 但数据乱）。按头文件数出 slot 42 也返垃圾（off-by-one）。**暴力扫描 slot 38-55** 确认正确槽位是 **43**。教训：不要信手数 vtable，用暴力扫描验证。
- COM vtable 槽位全部从 `UIAutomationClient.h` 逐个数出（GetRootElement=5/ElementFromPoint=7/GetFocusedElement=8/CreateTreeWalker=14/GetCurrentPatternAs=14/get_CurrentProcessId=20/**get_CurrentBoundingRectangle=43**（非 89/42——经暴力扫描确认）/GetSelection=5/get_DocumentRange=7/GetText=12/SetValue+get_CurrentValue=4）。
- **预填框实时跟随必须"用户一碰就停"**（第十一批踩坑）：UIA 自动框若只在触发时查一次，框就钉死；若改成 MouseMove 持续跟踪但不停止，用户开始画框时会出现"我画，模型也在挪"的拉锯。修法：`RegionSelectOverlay.EnableLiveTracking` 接 tracker；`OnCanvasPointerMoved` 在 `_mode == None` 时调 `TryLiveTrack`（40ms 节流），PointerPressed 在画/移/调三处都 latch `_userTouchedRect = true`。**永远让手动编辑赢**。⚠️ 第十二批：整个 UIA 预填框默认关闭（`UiaPrefillEnabled=false`），因为手动画框更精确。
- **UIA 让 overlay 不可见的正确方法**（第十二批踩坑，⚠️ 关键）：`ElementFromPoint` 默认返 overlay 自己（全屏 Topmost）。三种尝试：
  - ❌ `SW_HIDE/SW_SHOW`（RunHidden）：能用但闪烁
  - ❌ `WS_EX_TRANSPARENT + WS_EX_LAYERED`：点击穿透，UI 卡死（Avalonia 用 `WS_EX_NOREDIRECTIONBITMAP` 不是 LAYERED，单独 TRANSPARENT 是 no-op，两个一起才生效但破坏事件路由）
  - ✅ `UIA_WindowVisibilityOverridden=2` prop（`MarkInvisibleToUia`）：让 UIA 跳过 overlay，不闪不卡。但只返大框不深入细节（教训 2）。
- **画框场景 UIA 不可靠，默认走 OCR**（第十二批最重要教训）：UIA 的"框内即所得"在很多软件里不成立——UIA 树结构和视觉框不一致，祖先容器远大于画框，扫到框外内容。用户报"UIA 把软件其他部分放到剪贴板"。**结论**：画框默认必须走 OCR（框内即所得），UIA 改为可选开关。详见 handoff §3b 教训 3。
- **OCR 多余文字优先怀疑模型**（第十一批踩坑，第十二批更新）：DeepSeek-OCR 在桌面截图上**严重幻觉**（不只是 markdown 包装，是完全不相关的内容）。`OpenAiCompatibleVisionOcrClient.CleanOcrText` 移除 `<think>` 块；`--probe-ocr-raw <x y w h>` 探针打印原始 SSE body。**最终解法**：换 `Qwen/Qwen3.5-4B`（关思考）。诊断多余文字永远先跑 `--probe-ocr-raw` 看原始 body。
- **Qwen3.x 必须关思考**（第十二批踩坑）：混合推理模型开思考 9-14s，关思考 <1s。但纯 OCR 模型不认 `enable_thinking` 会报 HTTP 400。做成 per-model 可配开关（`VisionCaptureSettings.DisableThinking`）。
- **截图捕获延迟加大**（第十一批调整）：`WaitForCompositorSettleAsync` 从 2 帧 + 80ms 加到 **3 帧 + 150ms**，覆盖慢驱动 / 后台 tab throttling / BitBlt round-trip。遮罩没消失干净 → OCR 拿到脏屏 → 乱码。
