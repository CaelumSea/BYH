# BYH 全代码库审查清单（Audit Findings）

> 创建：2026-07-26。来源：5 个并行只读 sub-agent 审查 + 主 agent 亲验。
> 范围：7 src 项目（44k 行 C# + 15k 行 AXAML）+ 3 测试项目（650+ 测试）。
> 修复方式：每条单独 commit，按 P0→P3 顺序，每修完在条目末尾标 `[DONE commit:<sha>]`，并把"修复要点"摘要追加到 `BACKLOG-roadmap.md` 对应 batch 段。
> 审查做对的部分（**不要重复修**）见文末「✅ 已确认正确」段。

## 状态约定
- `[ ]` 待修
- `[~]` 修复中
- `[DONE commit:<sha>]` 已修，等真机/回归
- `[SKIP reason:...]` 经评估放弃，写明原因
- `[WONTFIX reason:...]` 用户/架构决定不做

---

## P0 — 必须先修（影响发布或运行时崩溃）

### [DONE commit:1f5a8be] N1 · NativeAOT 下的 `RegexOptions.Compiled` 会崩溃
**严重度**：🔴 发布版崩溃（首次命中即 `PlatformNotSupportedException`）
**证据**（亲验 grep）：
- `src/SelectionAssistant.Core/Capture/ScreenshotGalleryLoader.cs:25`
- `src/SelectionAssistant.Infrastructure/Configuration/UserIconLibraryStore.cs:45,47,49,51,53,55,57`
- `src/SelectionAssistant.Platform.Windows/Launcher/WindowsStartMenuDetector.cs:110,114,118`
- `src/SelectionAssistant.Providers/OpenAiCompatibleVisionOcrClient.cs:46` ← **生产路径（OCR 输出清洗）**
**修复**：全部改 `[GeneratedRegex(@"...", RegexOptions.X)] partial` 源生成器；若正则含动态构造无法源生成，则去掉 `| RegexOptions.Compiled`（解释执行也 AOT 安全）。
**验证**：build 0 警告 + 全测试过 + NativeAOT publish 0 警告 + 真机触发 OCR + 扫描已安装应用 + 截图画廊。

### [DONE commit:bc56662] N2 · 4 个 Store 的 Delete+Move 非原子
**严重度**：🔴 配置文件可能丢失（崩溃/AV 锁/掉电窗口）
**证据**（亲验）：
- `src/SelectionAssistant.Infrastructure/Configuration/LauncherEntryStore.cs:122,124`
- `src/SelectionAssistant.Infrastructure/Configuration/PromptTemplatesStore.cs:134,136`
- `src/SelectionAssistant.Infrastructure/Configuration/ProviderConfiguration.cs:199,201` ← 注释甚至写了 "Atomic move" 但不是
- `src/SelectionAssistant.Infrastructure/Configuration/VisionCaptureStore.cs:106,109`
**修复**：4 处删掉 `if (File.Exists(path)) File.Delete(path);`，把 `File.Move(tempPath, path)` 改成 `File.Move(tempPath, path, overwrite: true)`（与其余 11 个 Store 一致）。
**验证**：4 处对应 Store round-trip 单测仍过；新增"目标已存在时覆盖"用例。

### [DONE commit:bc56662] N3 · Provider 保存未调 `Validate()`
**严重度**：🔴 脏配置落盘（空 Name/BaseUrl、越界 TimeoutSeconds）
**证据**：`src/SelectionAssistant.UI/Views/SettingsWindow.axaml.cs:1229-1251` `OnSaveProviderClick`，对比同文件其他 6 个保存按钮（830/855/929/964/1020/1071）都有 Validate。
**修复**：照搬 `OnSaveOceanEyesTriggerClick` 的 try/`Validate()`/catch → `SetFeedbackTone(isError:true)` 模式。
**验证**：ProviderTests + 新增"空 Name 保存被拒"用例。

### [DONE commit:1a4800f] N4 · 截图画廊删除无回收站/无确认
**严重度**：🔴 误按即不可恢复
**证据**：`src/SelectionAssistant.UI/Views/GalleryWindow.axaml.cs:686` `DeleteEntry` 直接 `File.Delete`，配合 `Delete` 键 + 悬停选中（line 329）。view 既发 `RequestDelete` 事件又自删文件，service 无法否决。
**修复**：① `File.Delete` 移到 service（事件已存在）；② view 只在 service 确认后从 `_items` 移除行；③ `Delete` 键加确认对话框（"Delete this screenshot? This cannot be undone."）。
**验证**：删一张测试截图 → 文件确实删 + 弹了确认；按 Esc 取消 → 不删。

---

## P1 — 高优先级（数据完整性 / 句柄泄漏 / 并发竞态）

### [DONE commit:6dbe940] H1 · 启动外部 App 泄漏进程句柄
**证据**：`src/SelectionAssistant.Platform.Windows/Launcher/LauncherRunner.cs:206,308,328`（`SEE_MASK_NOCLOSEPROCESS` 设了但 `info.hProcess` 从不 `CloseHandle`）。
**修复**：`StartViaShellExecuteEx` 成功返回前，`if (info.hProcess != IntPtr.Zero) CloseHandle(info.hProcess);` 放 `finally`。需先确认 P/Invoke `CloseHandle` 是否已声明（grep）。
**验证**：Windows.IntegrationTests 里 LauncherRunner 的测试仍过；任务管理器看 BYH 句柄数稳定。

### [DONE commit:6dbe940] H2 · `TranslationSessionManager._provider` 锁外读写
**证据**：`src/SelectionAssistant.Core/Translation/TranslationSessionManager.cs:147,174,184`（`RunAsync` 锁外解引用 `_provider`），`:95` `ReplaceProvider` 可并发换。
**修复**：`RunAsync` 入口在 `_gate` 内把 `_provider` 快照到局部变量，之后全用局部。
**验证**：Core.Tests 加并发 ReplaceProvider + RunAsync 测试。

### [DONE commit:6dbe940] H3 · 低层 Hook 用 `Marshal.PtrToStructure<T>`（AOT 慢路径）
**证据**：`src/SelectionAssistant.Platform.Windows/Hooks/LowLevelKeyboardHook.cs:196`、`Hooks/LowLevelMouseHook.cs:168`。
**修复**：换成 `Unsafe.AsRef<KbdllHookStruct>((void*)lParam)`（结构已是 `[StructLayout(Sequential)]`）。需加 `unsafe` 或用 ref。
**验证**：Windows.IntegrationTests hook 测试仍过；钩子 callback 性能不退化。

### [DONE commit:pending] H4 · UIA 后端跨线程 COM + 初始化竞态
**评估后降级为最小修复**：审计原判"跨线程 COM 使用"经线程模型核实属**误判**——所有 COM 访问已序列化到专属 MTA worker（`_boundsThread` 处理 `GetElementBoundsAt`，`BYH.UIAutomation` worker 处理 `ReadSelection`），见 `WindowsUiAutomationBackend.cs:46-54` 注释 + `UIAutomationWorker` 自持 `_thread`（`UIAutomationTextCapture.cs:149-156`）。`VisionTextCapture` 共享 backend 但通过 `_boundsQueue.Add` dispatch（line 261），仍单线程访问。
**实际修复**：只给 `EnsureInitialized()` 加 `lock(_initGate)` 防御未来新调用路径（check-then-act 竞态），无死锁风险（body 仅 COM init + vtable 读，无回调）。`_automation != 0` 快路径无锁（nint 读原子）。

### [DONE commit:6dbe940] H5 · UIA `CoUninitialize` 引用计数失衡
**证据**：`WindowsUiAutomationBackend.cs:154-157,1028-1032`（`S_FALSE` 时也置 `_comInitialized=true`，Dispose 仍 `CoUninitialize`）。
**修复**：只在 `== 0 (S_OK)` 时记 `_comInitialized=true`。
**验证**：UIA 工作线程多次复用后 COM 仍正常。

### [WONTFIX reason:误判-随 popup GC] H6 · ClipboardHistoryWindow popup lambda handler 累积
**评估后降级为 WONTFIX**：审计原判"窗口 reuse 累积"经核实属**误判**。每次 `ShowImagePopup`/`ShowFullTextPopup` 等都 `new Popup()` + `new Border card` + `new Image()`——**popup 和子控件每次都是新实例**，6 个 lambda handler 是 popup 局部闭包，随 popup 一起 GC。`_fullTextPopup?.Close()` 后字段被新 popup 覆盖，老 popup 立即 unreachable。"反复开关 100 次内存增长"实际不会发生。
**真实情况**：唯一持有重对象的是 `row.FullBitmap`（存在 row 上，设计意图跨 popup 复用），popup 本身不持重对象。
**结论**：H6 实际收益极小，改动面大（6 lambda → named method + Closed 卸载），且真机内存验证本环境做不了。**移到 P3/M1 重构窗口**和 `ClipboardHistoryWindow` 拆分一起做（那时整个 popup 子系统会重写为 `ClipboardPopupFactory`）。
**证据**：`src/SelectionAssistant.UI/Views/ClipboardHistoryWindow.axaml.cs` 6 个 popup（ShowIconPickerPanel/ShowTagInputPanel/ShowFullTextPopup/ShowImagePopup/ConfirmClearOlder/ShowEntryTagInputPopup），图片 popup 5 个 zoom/pan handler（2487-2565）捕获整条 transform 链。窗口 reuse（Hide 不 Close）。
**修复**：图片 popup 的 5 个 handler 在 `popup.Closed` 显式 `-=`；其余 popup 评估。**优先只做图片 popup**（最大泄漏面），其余随 P3 重构一起做。
**验证**：反复开关图片 popup 100 次，内存不增长。

### [DONE commit:6dbe940] H7 · 流式翻译超时被响应头到达绕过
**证据**：`src/SelectionAssistant.Providers/OpenAiCompatibleStreamingProvider.cs:98-146`（`timeout` CTS 传给 `SendAsync` 但 SSE 消费循环用原 `cancellationToken`）；同模式 `OpenAiCompatibleVisionOcrClient.cs:155-157`。
**修复**：`EnumerateDeltasAsync`（line 144-146）token 改 `timeout.Token`；OCR 同。
**验证**：Providers.Tests 加"server 发 header 后卡住"超时测试（mock handler）。

### [DONE commit:6dbe940] H8 · `RequestExit` 不 Dispose `_runtime`
**证据**：`src/SelectionAssistant.App/App.axaml.cs:2064-2094`（释放 hotkeys/service/tray 但跳过 `_runtime`）。
**修复**：`RequestExit` 加 `_runtime?.Dispose()`；`DisposeApplicationResources` 改幂等。
**验证**：退出后任务管理器 BYH 进程消失 + 句柄归零。

### [DONE commit:6dbe940] H9 · `OceanEyesTriggerStore.LegacyMigrationPathField` 无 volatile
**证据**：`src/SelectionAssistant.Infrastructure/Configuration/OceanEyesTriggerStore.cs:46,49`（启动期 set，加载期 read，无屏障）。
**修复**：标 `volatile`。
**验证**：Core.Tests 现有 OceanEyes store 测试仍过。

### [DONE commit:6dbe940] H10 · 剪贴板 Restore 失败仍已清空
**证据**：`src/SelectionAssistant.Platform.Windows/Win32Clipboard.cs:178-193`。
**修复**：先 stage 所有 `SetClipboardData` 确认至少一个可成功后再 `EmptyClipboard`；或文档化"false 也可能已清空"并加日志。
**验证**：Windows.IntegrationTests clipboard 测试。

---

## P2 — 中优先级（健壮性 / 可维护性 / 性能）

### [DEFER reason:独立重构窗口] M1 · `ClipboardHistoryWindow.axaml.cs` 2937 行上帝类
**评估后 DEFER**：纯可维护性重构（拆 4 类：ClipboardTagDragController/IconPickerBuilder/ClipboardPopupFactory/ClipboardContextMenuBuilder），无 bug 无崩溃。风险：① 2937 行逻辑边界需逐块理解；② UI 行为需真机验证（无法在本环境验证）；③ H6（popup handler）评估为随 popup GC，不紧急。**移到独立重构窗口**，与 L8（IManagedWindow 接口）一起做。
### [DEFER reason:独立重构窗口] M2 · `App.axaml.cs` 2095 行接线 shell
**评估后 DEFER**：抽 3 个 controller（ClipboardHistoryController/SpotlightController/OceanEyesController）。与 M1 同因——纯重构、需真机验证生命周期/事件订阅迁移、无 bug。**与 M1 一起做**。
### [DONE commit:pending] M3 · Provider 每实例 `new HttpClient` + favicon 不缓存
**证据**：`OpenAiCompatibleStreamingProvider.cs:34-41`、`OpenAiCompatibleVisionOcrClient.cs:67-72`、`App.axaml.cs:1837`。
**修复**：注入单个共享 `HttpClient` 到 providers，`SelectionRuntime` 持有；favicon 加内存或磁盘缓存。

### [DONE commit:5e0349c — 部分] M4 · P/Invoke 全用 `[DllImport]` 非 `[LibraryImport]`，多处缺 `SetLastError`
**评估后部分落地（66/112 处）**：`[LibraryImport]` source generator 迁移，NativeAOT 下比 `[DllImport]` 的运行时 IL stub 更友好。**实际全仓库 112 处 DllImport**（AUDIT 原估 ~105 偏低）。
**已迁 66 处**（Platform.Windows 项目的纯 blittable + 纯字符串 marshal）：批次 0-2，覆盖 `WindowsSystemMetrics`/`WindowsMouseContextProvider`/`WindowsProcessIdentityResolver`/`WindowsGlobalHotKey`/`WindowsUiAutomationBackend`/`SendInputHelper`/`NoActivateWindowHost`/`Win32Clipboard` 的大部分。迁移规则统一：`[DllImport]`→`[LibraryImport]` + `static extern`→`static partial` + 保留 `[return: MarshalAs(Bool)]` + 保留 `SetLastError` + 类加 `partial`。
**关键陷阱（实测）**：① `[LibraryImport]` 总是要求 `AllowUnsafeBlocks=true`（SYSLIB1062），即使纯 blittable 签名——所以 `App`/`UI` 项目的 4 处无法迁（csproj 没开 unsafe，不动 csproj）；② `bool` 参数（非返回值）需显式 `[MarshalAs(UnmanagedType.Bool)]`（SYSLIB1051）；③ **`StringMarshalling.Utf16` 不等价 `CharSet.Unicode`**——前者只管字符串 marshal，不影响入口点查找；凡是 Win32 只有 W/A 版本无裸名的（`GetMessage`/`DispatchMessage`/`PostMessage`/`PostThreadMessage`/`DefWindowProc`/`GetModuleHandle`/`CreateWindowEx`/`UnregisterClass` 等），必须显式 `EntryPoint = "...W"`。`[DllImport]` 靠 .NET runtime 的 fallback 机制找 W 版，`[LibraryImport]` 无此 fallback。
**剩余 46 处 DEFER（高风险，机会性迁移）**：批次 3 未做——`SetWindowsHookExW`（hook delegate callback，快捷键核心）、`ShellExecuteEx`/`SHGetFileInfo`（struct 含多 string/ByValTStr/BestFitMapping，启动器+图标核心）、`RegisterClassEx`（struct 含 delegate+string）、`DragQueryFile`（StringBuilder 需重构调用点）、`GetDIBits`（byte[] 数组 marshal）、ScreenRegionCapture 的 9 处 GDI、Hooks 两文件 20 处。这些回归会导致核心功能失效，本环境无法真机验证 delegate/复杂 struct marshal，留作机会性迁移。**SetLastError 现状健康**（10 处 `GetLastWin32Error` 调用全配对，无 bug），不借迁移修。
**验证**：build 0 警告，661/661 测试过，NativeAOT 0 警告，exe +2KB（28,412,928→28,414,976），真机启动 OK（MouseHook/KeyboardHook/ClipboardHistory 全正常，无 EntryPointNotFound）。
### [DONE commit:3b17337] M5 ·  (批次 C) `PromptTemplateSet` / `LauncherEntrySet` 共享可变集合无线程安全
**证据**：`PromptTemplates.cs:57-255`、`LauncherEntries.cs:72-198`。设置 UI 线程改、工具栏线程读，会抛 `Collection was modified`。
**修复**：lock 或 copy-on-write（`ImmutableArray<T>` + `Interlocked.Exchange`）。

### [DONE commit:e623616] M6 ·  (批次 A) `VisionCaptureSettings` 缺 `Normalize()` + `Validate()`
**证据**：`src/SelectionAssistant.Core/Capture/VisionCaptureSettings.cs:10-66`（其他 8 个 record 都有）。null ProviderId NPE 下游。
**修复**：照搬同层其他 record 模式。

### [DONE commit:e623616] M7 ·  (批次 A) `ProcessCapturePolicy.Validate` 只 throw 不 clamp
**证据**：`src/SelectionAssistant.Core/Capture/ProcessCapturePolicy.cs:23-33`（JSON 读 -1 直接抛，与全局 Normalize clamp 约定不一致）。
**修复**：加 `Normalize()` clamp 到 [0,5000]。

### [SKIP reason:单例架构无并发] M8 · 剪贴板归档 read-modify-write 无互斥锁
**评估后降级为 SKIP**：BYH 有 `Global\BYH_ByYourHand_SingleInstance` Mutex 保证单例，没有第二个 BYH 实例并发写归档。同进程内 `ClipboardHistoryService` 的所有 store 调用已序列化（service 内部 lock）。审计担心的"service + UI 双实例"在当前架构不成立。**若未来拆 service/UI 进程，需加 named Mutex（按月文件 `Global\BYH_ClipboardArchive_{month}`）**——届时再加。
### [DONE commit:8f00fa5] M9 ·  (批次 B) `ScreenshotGalleryLoader` 硬编码中文星期/格式
**证据**：`src/SelectionAssistant.Core/Capture/ScreenshotGalleryLoader.cs:27-30,114-124`（英文用户看到"今天 14:30"）。
**修复**：走 `Strings.Get`（加 `Gallery_Today`/`Gallery_Yesterday`/weekday keys）。

### [SKIP reason:best-effort 设计,非 bug] M10 · 静默 `catch { }` 15+ 处
**评估后降级为 SKIP**：审计原列 MED 基于"诊断可见性"，但逐处核实后这些 catch 都是合理的 best-effort / defense-in-depth：
① Store 的 `.tmp` cleanup catch —— cleanup 失败不影响主流程（主文件已 Move 成功）；
② `RedactedLogger.Write:53` catch —— 日志失败时再加日志有递归崩溃风险（设计正确）；
③ `WindowsGlobalHotKey.cs:101` catch —— 热键 callback 抛异常不能崩 hook 链（Win32 hook 规则）。
`Trace.WriteLine`/`Debug.WriteLine` 在 NativeAOT 发布版默认 no-op（无 listener），加它们收益≈0。
改动面广（15+ 处），收益极小，跳过。若需要诊断，开启 ETW 或加文件 listener 是更正确的方向。

### [DONE commit:8f00fa5] M11 ·  (批次 B) 翻译语言路由漏日韩
**证据**：`src/SelectionAssistant.Core/Translation/TranslationLanguageSelector.cs:20-33`（漏 U+3040-30FF 假名、U+AC00-D7AF 谚文）。
**修复**：加假名/谚文检测，或文档化"MVP 仅中英"。

### [DONE commit:3b17337] M12 ·  (批次 C) `InstalledAppsScanDialog.StartIconLoading` fire-and-forget 无取消
**证据**：`src/SelectionAssistant.UI/Views/InstalledAppsScanDialog.axaml.cs:151-176`。
**修复**：传 `CancellationToken`，`OnClosed` cancel。

### [DONE commit:e623616] M13 ·  (批次 A) `ResultWindow.ShowAndActivate` DispatcherTimer 不复用
**证据**：`src/SelectionAssistant.UI/Views/ResultWindow.axaml.cs:289-295`。
**修复**：timer 存字段，stop+start，Closing 路径 stop。

### [DONE commit:e623616] M14 ·  (批次 A) `MagneticSnapCalculator.CheckX/CheckY` 死参
**证据**：`src/SelectionAssistant.Core/Annotation/MagneticSnapCalculator.cs:151-179`。
**修复**：删 `snappedLeft`/`snappedTop` 入参 + call site（107,116,125,128,134,137）。

### [SKIP reason:相等性从未使用] M15 · `ClipboardEntry` record 用 `IReadOnlyList<string>` 破坏值相等
**评估后降级为 SKIP**：grep 核实 `ClipboardEntry` 的合成相等性**从未被使用**——所有比较都走 `.Id`（Guid），从未用作字典 key/HashSet/Distinct。record 的合成 Equals 对 EntryTags 走引用相等是理论问题，无实际 bug。不 override Equals（避免给 record 加隐藏行为）；加 doc 注释说明按 Id 身份比较即可。
### [DONE commit:58cdf30] L1 · 代码后置硬编码英文字符串绕过 i18n
多处：`ClipboardHistoryWindow.axaml.cs:603-607,855,2423,2785`（"just now"/"Xm ago"/"View full…"/图片 popup header/tooltip），`SettingsWindow.axaml.cs:497-498,802,1051,1089,519-523`，AXAML `PromptWindow.axaml:5`/`ResultWindow.axaml:5`/`SpotlightWindow.axaml:6` 的 Title。
**修复**：加 `Strings.*` keys + 三文件同步（`EveryProperty_HasEntryInBothDictionaries` 测试守卫）。
**续：第一百一十九/二十批全量接入 [DONE commit:d5b349a + a99130b]**：REQ-027 合并引入的硬编码英文（SettingsWindow 手机视图/Dashboard/Profile/主题概念块 ~70 处 AXAML + ~20 处 code-behind）全部接入 i18n。+~80 个 key（capacity 128→256），复用 16 个已有 key。code-behind 重构 `SetDashboardModuleActive` 为标识符+映射，修 `RefreshDashboardModelRouteCount` 的字面量比较陷阱。补漏 `Settings_PageTitle_Dashboard`/`Settings_Nav_Dashboard`（接入时漏了两处 Dashboard 硬编码）。保留品牌名/hex 色码/字体样本/技术占位符不翻译。整个 SettingsWindow 中英文下一致。**这是 i18n MVP 的收尾**。

### [DONE commit:97b5021] L2 · `Strings_zh_CN.cs:38,56` 未翻译
`Result_Title="Translation"`、`Spotlight_SearchPlaceholder="Search…"` 中文词典里仍是英文。
**修复**：改 `"翻译"`、`"搜索…"`。

### [DEFER reason:独立窗口+a11y专项] L3 · UI 全层 `AutomationProperties.Name` 用 0 次
**评估后 DEFER**：13 个 AXAML 文件零无障碍标注，屏幕阅读器用户对搜索框/行/翻译面板什么都拿不到。这是真实的 a11y 缺陷，但：① 工作量大（每个交互控件都要加 Name + TabIndex 审计）；② 需要屏幕阅读器（NVDA/Narrator）真机验证；③ 不是 bug，是功能扩展。**移到独立 a11y 专项**，与 L8（PrepareForShutdown 接口）一起做时顺便给主要交互控件加 Name。
### [DONE commit:97b5021] L4 · `NumberedAnnotationSession.cs` 死代码
注释自承被 `AnnotationSession` 替换，"existing tests" 才保留。
**修复**：删，或 `[Obsolete]`。

### [DONE commit:42d5033] L5 · `RedactedLogger` 无滚动/封顶
**评估后原 DEFER，现落地**：`BYH.log` 无限增长会写满磁盘。落地为 size-based 滚动：`MaximumFileBytes = 1MB`（与 store 惯例一致）+ `RetainedRotations = 5`，超阈值时当前文件 rename 成 `BYH-yyyyMMdd-HHmmss.log`（同秒冲突加 `-2/-3` 后缀），超出保留数的旧归档按文件名（时间戳序）删除。构造时若现有文件已超阈值，先归档再开始写（启动归档）。
**决策**：保持 `File.AppendAllText` 无状态写入（4 个持有者 Dispose 链分散，引入持久 FileStream 风险大于收益）；不做压缩（NativeAOT 无先例 + 文本压缩比有限）；时间戳命名而非 `.1/.2` 链式 rename（单步 `File.Move` 更原子，与项目截图/归档命名一致）。跨实例并发靠 `File.Move(overwrite:false)` + `File.Exists` re-check 防双滚，单实例 mutex 兜底。
**真机验证**：当前 2.4MB 老 `BYH.log` 启动时被归档成 `BYH-20260727-100111.log`（18740 行完整保留），新 `BYH.log` 从 869 bytes 重新开始。
**验证**：8 个新单测（redaction 不回归 / 滚动触发 / 保留数上限 / 启动归档 / 小文件不动 / 目录缺失创建 / 同秒命名消歧）；build 0 警告，661/661 测试过，NativeAOT 0 警告。
### [DONE commit:97b5021] L6 · `OceanEyesTriggerSettings.Keys` 数组暴露为 IReadOnlyList 可向下转型改共享静态
**修复**：`Array.AsReadOnly(Keys)` 或 `ImmutableArray<string>`。

### [DONE commit:97b5021] L7 · `ClipboardClassifier.SensitivePattern` 的 `bearer\s+\S` 写法歧义
**修复**：改 `bearer\b`。

### [DEFER reason:与 M1/M2 重构一起] L8 · 6 个 window 各写 `PrepareForShutdown()` 无公共接口
**评估后 DEFER**：runtime 按名字逐个调 `PrepareForShutdown()`，抽 `IManagedWindow` 接口是合理改进。但 6 个 window 的生命周期管理与 M1（ClipboardHistoryWindow 拆分）+ M2（App.axaml.cs 拆分）耦合——**与 M1/M2 重构一起做**，那时窗口管理会整体重写。

---

## ✅ 审查中确认正确（**不要重复修**）

- **NativeAOT 序列化干净**：所有 Store 手写 `Utf8JsonReader/Writer`，零反射 serialize、零 `[Serializable]`、零 `Activator.CreateInstance`。Core 里 `[GeneratedRegex]` 用得规范。
- **Provider 安全**：API key 走 `secret://provider/{Id}` + `ISecretStore`，从不在 URL/日志/异常；redirect 默认禁用；类型化 `TranslationProviderException`；UTF-8 SSE 边界正确；`[DONE]` 哨兵处理对。
- **HTTP dispose（生产路径）**：`OpenAiCompatibleVisionOcrClient.RecognizeAsync` try/finally 正确释放 response+stream（"use-after-dispose" 在诊断 `RecognizeRawAsync`，已降级不计）。
- **Dispatcher 卫生**：全 UI 层零同步 `Dispatcher.UIThread.Invoke`，全用 `Post`/`InvokeAsync`。
- **AXAML i18n 绑定**：14 个 axaml 几乎全部绑 `x:Static i18n:Strings.*`（除 2 处品牌名）。
- **Hook root**：delegate 正确 root、`CallNextHookEx` 所有分支都调、专属 pump 线程 + finally `UnhookWindowsHookEx`。
- **GDI 平衡**：`ScreenRegionCapture`/`WindowsIconExtractor` 的 SelectObject/DeleteObject/DeleteDC/ReleaseDC 在 finally 全平衡。

---

## 修复进度

- [DONE 1f5a8be] N1 — RegexOptions.Compiled → [GeneratedRegex]（4 文件 11 处）
- [DONE bc56662] N2 — 4 Store 改 File.Move(overwrite:true)（原子替换）
- [DONE bc56662] N3 — ProviderProfileEntry 加 Validate() + OnSaveProviderClick 调用
- [DONE 1a4800f] N4 — 截图删除加确认 popup + i18n（防误按不可恢复）
- [DONE 6dbe940] P1 batch — H1 hProcess / H2 provider 锁快照 / H3 hook Unsafe.Read / H5 CoUninit 平衡 / H7 流式 timeout / H8 runtime dispose / H9 volatile / H10 clipboard 预检
- [DONE e623616] P2-A — M6 VisionCapture Validate / M7 ProcessCapture Normalize / M13 ResultWindow Timer 复用 / M14 MagneticSnap 死参
- [DONE 8f00fa5] P2-B — M9 ScreenshotGallery i18n / M11 翻译路由日韩（+11 测试）
- [DONE 3b17337] P2-C — M5 PromptTemplateSet 加锁 / M12 InstalledApps CancellationToken
- [SKIP] P2-D — M10 静默 catch（best-effort 设计,非 bug）
- [DONE 60f3c03] P2-E — M3 favicon 共享 HttpClient + 内存缓存（M8/M15 评估 SKIP）
- [DONE 97b5021] P3-快修 — L2 zh_CN 未译 / L4 NumberedAnnotationSession 死代码（+11 死测试）/ L6 SupportedKeys AsReadOnly / L7 SensitivePattern bearer
- [DONE 58cdf30] P3-i18n — L1 21 处硬编码英文字符串 → i18n（+18 新 key,复用 2 个已有）
- [DEFER] M1/M2 god-class 拆分 / L3 无障碍标注 / L8 IManagedWindow 接口 — 移到独立重构/功能窗口
- [DONE 部分] M4 LibraryImport 迁移 — 66/112 处已迁（Platform.Windows 纯 blittable + 字符串），剩 46 处高风险（hook/launcher/icon 核心 delegate+复杂 struct）机会性迁移，commit 5e0349c
- [DONE] L5 日志滚动 — 已落地（size-based 1MB/保留 5，启动归档，commit 42d5033）
- [DONE d5b349a + a99130b] i18n 全量接入 — SettingsWindow 全覆盖（手机视图/Dashboard/Profile/主题概念块 ~70 AXAML + ~20 code-behind），+~80 key，第一百一十九/二十批。i18n MVP 收尾。
- [DONE 第一百二十批] feat/i18n-zh-en-mvp fast-forward 合并进 main（51 commit，零冲突）+ 清理 3 个已合并分支 + 2 个 worktree。仓库现仅剩 main 分支。
