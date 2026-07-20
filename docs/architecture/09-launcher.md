# 09. R23 快捷启动器（Quick Launcher）+ R32 Spotlight 搜索面板

> **作用**：
> - **R23 快捷启动器**：用户在 QuickTools 面板（默认 Ctrl+Alt+Q 唤出）里一键启动常用软件或打开网页，可在设置页增删改、调顺序、配参数模板。每条启动项支持从 exe 自动提取图标，参数中可用 `{clip}`/`{sel}`/`{prompt:提示语}` 占位符做动态替换。
> - **R32 SpotlightWindow**：独立的搜索面板（默认 Ctrl+Alt+Space），Spotlight/PowerToys-Run 风格——顶部搜索框过滤启动项，↑↓ 选中、Enter 启动、Ctrl+Enter 编辑、Esc 关闭。和 QuickTools 完全独立但共用同一份启动项数据。

架构参照 R15 自定义功能系统（`04-prompt-templates.md`）—— Core record → Set → Store → Row → 编辑窗 → Runtime → App 接线 → 测试，每层都一一对应。读这一节前可先扫一眼 `04-prompt-templates.md` 摸整体模式。

## 数据流总览

```
用户在设置页"启动器"分区操作
  ├─ ＋ 新增启动项 → LauncherEntryEditWindow.ShowForNew() → EntryCreated 事件
  ├─ 编辑某条 → LauncherEntryEditWindow.ShowFor(id)       → EntrySaved 事件
  ├─ 删除某条 → DeleteCommand                            → LauncherEntryDeleted 事件
  └─ ↑/↓ 调序 → MoveUp/DownCommand                       → LauncherEntryMoved(id, delta)
       │
       ▼ (SettingsWindow raises these events)
App.axaml.cs (OnLauncherEntryAdded/Saved/Deleted/Moved)
  └─ await SelectionRuntime.Add/Save/Delete/MoveLauncherEntryAsync
       ├─ 改内存 _launcherEntries
       └─ LauncherEntryStore.Save → launcher-entries.json (原子写)
       │
       ▼ (await RefreshSettingsAsync)
       ├─ SettingsWindow.SetLauncherEntries (刷新设置页列表)
       ├─ QuickToolsWindow.SetLauncherEntries (刷新 QuickTools 启动器区)
       └─ App.LoadLauncherIconsAsync (异步加载图标，推回 UpdateLauncherIcon)

用户在 QuickTools 点启动项
  └─ LauncherEntryRow.RunCommand → LauncherRunRequested(entryId, sel, clip)
       │ (QuickTools 已读剪贴板 + 选中)
       ▼
App.OnLauncherRunRequested
  └─ SelectionRuntime.StartLauncherLaunchAsync(id, clip, sel)
       ├─ ParameterReplace.Expand(args, clip, sel)
       │    ├─ {clip} → 剪贴板文本
       │    ├─ {sel}  → 选中文本
       │    └─ {prompt:...} → 保留 token + 加进 Prompts list
       ├─ if NeedsPrompt → 暂存 _pendingLaunch, 返回 Prompts
       │    └─ App.CollectPromptAnswersAndCompleteAsync
       │         └─ 逐个弹 ParameterInputDialog → 收集 answers
       │              └─ Runtime.CompleteLauncherLaunchAsync(answers)
       │                   └─ ParameterReplace.ApplyPromptValues(expandedArgs, answers)
       │                        └─ LauncherRunner.Start(entry, finalArgs)
       └─ else → 立即 LauncherRunner.Start(entry, expandedArgs)
            ├─ LocalApp → Process.Start(FileName=exe, Arguments=args, WorkDir=...)
            └─ WebUrl   → Process.Start(FileName=url, UseShellExecute=true)
```

## 关键文件

### Core 层（业务实体 + 无 UI/Win32 依赖）

- `src/SelectionAssistant.Core/Launcher/LauncherEntries.cs`
  - `LauncherEntry` record（`Id`/`Name`/`Kind`/`Target`/`Arguments`/`WorkingDirectory`/`IconOverride`）
  - `LauncherKind` enum（`LocalApp=0`, `WebUrl=1`）
  - `LauncherEntryIds.IsLauncher(id)` — `launcher-` 前缀判断
  - `LauncherEntrySet` — 可变 List + CRUD：`Add`/`Update`/`Remove`/`Move`/`Find`/`AsList`/`FromList`
  - `LauncherEntryDefaults.CreateDefault()` — 返回空集（启动项无内置）

- `src/SelectionAssistant.Core/Launcher/ParameterReplace.cs`
  - `Expand(args, clipText, selectedText)` → `ParameterReplaceResult(ExpandedArguments, Prompts, NeedsPrompt)` 两阶段：先替换 `{clip}`/`{sel}`，**保留** `{prompt:...}` token 并收集提示语
  - `ApplyPromptValues(expandedArgs, answers)` — 把用户答案按顺序填进 `{prompt:...}` token
  - `StripPromptTokens(args)` — 删除所有 `{prompt:...}` token（取消弹框时用）

- `src/SelectionAssistant.Core/Launcher/LauncherLaunchResult.cs` — `record(Success, ErrorMessage, Prompts, NeedsPrompt)`，App 据此决定要不要弹输入框

### Infrastructure 层（持久化）

- `src/SelectionAssistant.Infrastructure/Configuration/LauncherEntryStore.cs`
  - `LoadIfExists(path)` / `Save(set, path)`
  - `CurrentSchemaVersion=1`、`MaximumFileBytes=256KB`
  - 原子写：temp + rename
  - 省略规则：`Arguments`/`WorkingDirectory`/`IconOverride` 为空时不写
  - 前向兼容：未知 id 前缀（非 `launcher-`）的条目忽略
  - 同 id 去重保留第一个

- `src/SelectionAssistant.Infrastructure/Configuration/ByhApplicationPaths.cs`
  - `LauncherEntriesFile` → `<BaseDirectory>/launcher-entries.json`
  - `LauncherIconsDirectory` → `<BaseDirectory>/launcher-icons/`（预留，目前图标不落盘，只 in-memory）

### Platform.Windows 层（Win32 互操作 + 启动执行）

- `src/SelectionAssistant.Platform.Windows/Launcher/WindowsIconExtractor.cs`
  - `ExtractSmallIconPng(exePath)` → `byte[]?`（PNG bytes）
  - 链路：`SHGetFileInfo` 拿 HICON → `GetIconInfo` 拿 color/mask HBITMAP → 两遍 `GetDIBits`（第一遍填 BITMAPINFOHEADER，第二遍拷 32 位 BGRA 像素）→ 24/32bpp 时按需 apply AND mask 做 alpha → 复用 `PngEncoder`（同 ScreenRegionCapture）输出 PNG
  - 失败诊断字段：`LastDiagnostic` / `LastShGetFileInfoResult` / `LastShGetFileInfoError`（探针用）
  - **不用 System.Drawing.Common**（NativeAOT 不友好），全靠手写 DIB 读取 + 复用项目内 PNG 编码器
  - **不抓网页图标** —— 网页 favicon 由 App 层用 HttpClient 抓 Google S2 服务

- `src/SelectionAssistant.Platform.Windows/Launcher/LauncherRunner.cs`
  - `Start(entry, expandedArgs)` → `string?`（null=成功，非空=错误消息）
  - `LocalApp`：`UseShellExecute=false` + `FileName=exe` + `Arguments=expandedArgs` + `WorkingDirectory`（要支持工作目录 + 字面参数传递）
  - `WebUrl`：`UseShellExecute=true` + `FileName=url`（让系统默认浏览器处理）
  - 捕获 `Win32Exception`/`FileNotFoundException` 等并返回友好错误消息，不抛

### UI 层（Avalonia）

- `src/SelectionAssistant.UI/Views/LauncherEntryRow.cs` — public sealed top-level ViewModel（NativeAOT 编译绑定要求）：`Id`/`Name`/`Kind`/`Target`/`Arguments`/`Icon` + 5 个 `ICommand?`（Run/Edit/Delete/MoveUp/MoveDown）
- `src/SelectionAssistant.UI/Views/LauncherEntryEditWindow.axaml(.cs)` — 编辑/新建弹窗
  - `ShowForNew()` / `ShowFor(id, existing)` 双模式
  - 字段：类型 RadioButton / 名称 / 目标（+ 浏览 exe）/ 参数 / 工作目录 / 图标预览
  - 事件：`EntryCreated(name, kind, target, args, workDir)` / `EntrySaved(id, name, kind, target, args, workDir)`
  - 文件浏览用 `Avalonia.Platform.Storage.StorageProvider`（不调 Win32）
- `src/SelectionAssistant.UI/Views/ParameterInputDialog.axaml(.cs)` — `{prompt:...}` 运行时输入小弹窗（只有提示语 + 输入框 + 确定/取消）
- `src/SelectionAssistant.UI/Views/SettingsWindow.axaml(.cs)` — 新增"启动器"分区（第 5 个 SettingsNav）
  - `LauncherList` ItemsControl 绑 `_launcherRows`
  - 4 个事件：`LauncherEntryAdded`/`Saved`/`Deleted`/`Moved`
  - `SetLauncherEntries(entries)` + `UpdateLauncherIcon(id, bitmap)`（App 异步推图标用）
- `src/SelectionAssistant.UI/Views/QuickToolsWindow.axaml(.cs)` — 启动器区在 Row 5（OCR 按钮之后，自定义指令之前）
  - 窗口高度 480 → 560
  - `ScrollViewer MaxHeight=120` 包住 `ItemsControl` 防止启动项太多撑爆窗口
  - 每行：`[图标 20×20] [名称]`，点击 = 启动
  - `SetLauncherEntries(entries)` / `UpdateLauncherIcon(id, bitmap)` / `LauncherRunRequested(id, sel, clip)` 事件

### App 层（组合根）

- `src/SelectionAssistant.App/SelectionRuntime.cs`
  - `_launcherEntries` 字段，构造时 `LoadLauncherEntries(paths)`
  - 公共方法：
    - `GetLauncherEntries()` — 快照
    - `AddLauncherEntryAsync(name, kind, target, args, workDir)` → `Task<string?>`（返新 id）
    - `SaveLauncherEntryAsync(id, name, kind, target, args, workDir)` → `Task<bool>`
    - `DeleteLauncherEntryAsync(id)` → `Task<bool>`
    - `MoveLauncherEntryAsync(id, delta)` → `Task<bool>`
    - `StartLauncherLaunchAsync(id, clip, sel)` → `Task<LauncherLaunchResult>`（展开 `{clip}`/`{sel}`，若有 `{prompt:}` 则暂存 `_pendingLaunch` 返回 NeedsPrompt）
    - `CompleteLauncherLaunchAsync(answers)` → `Task<string?>`（用答案填 token 后启动）
    - `CancelPendingLaunch()` — 用户取消弹框时调
  - 持久化失败不回滚内存（和 PromptTemplates/Vision 一致）

- `src/SelectionAssistant.App/App.axaml.cs`
  - 订阅 SettingsWindow 4 个 launcher 事件 + QuickTools 1 个 LauncherRunRequested 事件
  - `RefreshSettingsAsync` 里推 launcher entries 给两个窗口 + fire-and-forget `LoadLauncherIconsAsync`
  - `LoadLauncherIconsAsync`：每条 entry 起异步任务 → LocalApp 调 `WindowsIconExtractor`、WebUrl 调 Google S2 favicon → 推回 `UpdateLauncherIcon`
  - `OnLauncherRunRequested`：调 `StartLauncherLaunchAsync`，若 `NeedsPrompt` 则 `CollectPromptAnswersAndCompleteAsync`（逐个弹 ParameterInputDialog，TaskCompletionSource 转 await）
  - 失败静默（不弹错误窗，只在 Debug 输出）

### CLI 探针

- `--probe-icon-extract <exePath> [out.png]` — 验证 HICON → PNG 链路
- `--probe-launcher-list` — 列出所有启动项
- `--probe-launcher-run <id> [--clip text] [--sel text]` — 直接跑指定启动项（不弹框，{prompt:} 会返回 exit 2）

## 测试

- `tests/SelectionAssistant.Core.Tests/Launcher/LauncherEntryStoreTests.cs` — 18 个 Fact（missing/roundtrip/省略/原子/前向兼容/去重/CRUD/Move/IsLauncher 分类）
- `tests/SelectionAssistant.Core.Tests/Launcher/ParameterReplaceTests.cs` — 14 个 Fact（Expand 各占位符 + ApplyPromptValues 边界 + StripPromptTokens）

## JSON Schema（launcher-entries.json）

```json
{
  "schemaVersion": 1,
  "entries": [
    {
      "id": "launcher-abc12345",
      "name": "Chrome 打开选中网址",
      "kind": "localApp",
      "target": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
      "arguments": "{sel}",
      "workingDirectory": "",
      "iconOverride": ""
    },
    {
      "id": "launcher-def67890",
      "name": "GitHub",
      "kind": "webUrl",
      "target": "https://github.com"
    }
  ]
}
```

`kind` 取值：`"localApp"` 或 `"webUrl"`（大小写不敏感，未知值当 LocalApp）。空可选字段（`arguments`/`workingDirectory`/`iconOverride`）省略不写。

## 参数占位符语法

| Token | 含义 | 何时替换 |
|---|---|---|
| `{clip}` | 当前剪贴板文本 | `Expand()` 即时替换 |
| `{sel}` | 当前选中文本 | `Expand()` 即时替换 |
| `{prompt:提示语}` | 运行时弹框，让用户输入 | 两阶段：`Expand()` 收集提示语 + 保留 token；用户填答案后 `ApplyPromptValues()` 替换 |

示例：
- `"https://dict.example.com/?q={sel}"` — 选中单词 → 打开词典查这个词
- `"{clip}"` — 复制了长 URL → 启动浏览器打开它
- `"--api-key {prompt:输入 API key}"` — 启动 CLI 工具时弹框让用户填密钥

`{prompt:}` 可多个：`"{prompt:first} - {prompt:second}"` 会弹两次框，按顺序替换。

## 关键不变量（⚠️ 改这个模块前必读）

1. **完全复用 PromptTemplates 模式** — 不要重新发明 CRUD/Store/Row 模式。改之前先看 `04-prompt-templates.md`。
2. **图标提取是 best-effort** — 失败 fallback 到无图标（bitmap=null），**绝不阻塞 UI 线程**。所有 `WindowsIconExtractor` 调用必须在 `Task.Run` 里。
3. **参数替换在 Launch 时做，不在保存时做** — 保证保存的就是用户原始模板。
4. **网页启动用 `UseShellExecute=true`** — 让系统默认浏览器处理，不假设 Chrome 路径。
5. **本地软件启动用 `UseShellExecute=false`** — 要支持工作目录 + 字面参数传递。
6. **图标缓存键用 entryId**（不是 target 路径）—— 用户改 target 后，row.Icon 会随 `RefreshSettingsAsync` 重置（因为 SetLauncherEntries 清空重建 rows）。**当前图标不落盘**（仅 in-memory，每次启动重提）—— 如果未来要落盘缓存，键要用 `{entryId}_{targetHash}.png` 避免 target 改了缓存还命中。
7. **NativeAOT 0 警告** — 所有 DllImport 显式 `CharSet.Unicode` + `SetLastError=true`；不用 `System.Drawing.Common`；Avalonia Bitmap 通过 `new Bitmap(stream)` 构造（AOT 安全）。
8. **`_pendingLaunch` 是单槽** —— 同一时间只能有一个待完成的启动操作。因为 ParameterInputDialog 是模态的，UI 上不会并发。如果未来改成非模态，需要改成按 entryId 索引的字典。
9. **`LauncherRunRequested` 签名带 `(id, sel, clip)` 三参数** —— QuickTools 在调事件前自己读剪贴板（UI 线程服务），App 不直接读剪贴板。
10. **HICON → Avalonia Bitmap 转换链** — `SHGetFileInfo` → `GetIconInfo` → 两遍 `GetDIBits`（第一遍填尺寸，第二遍拷像素）→ apply alpha mask（24bpp 时）或 force-opaque（32bpp alpha=0 时）→ `PngEncoder.Encode` → `new Bitmap(MemoryStream)`。**不要试图用 `System.Drawing.Icon.ExtractAssociatedIcon`** —— 它在 NativeAOT TrimMode=full 下会被裁剪。

## 踩坑记录（实现期）

1. **`GetObject` 对 SHGetFileInfo 返回的 HBITMAP 返 0（err=203）** —— SHGetFileInfo 的 color bitmap 可能是 DIB 而非 DDB，`GetObject(BITMAP)` 行为不一致。**解法**：用两遍 `GetDIBits`，第一遍 lpvBits=NULL + biWidth/biHeight=0 让 GDI 填 BITMAPINFOHEADER 拿尺寸，第二遍才拷像素。
2. **Win32Bitmap 结构布局要严格按 Win32 `BITMAP`** —— `bmType/bmWidth/bmHeight/bmWidthBytes` 是 LONG(4字节)，`bmPlanes/bmBitsPixel` 是 WORD(2字节)。布局错会导致 `GetObject` 返 0。
3. **`SHGetFileInfo` 不开 `SetLastError` 时 `GetLastPInvokeError` 不可靠** —— 显式 `[DllImport(..., SetLastError=true)]`。
4. **HICON 拿到但 `ConvertIconToPng` 失败（err=6 ERROR_INVALID_HANDLE）** —— 实际是 `GetObject` 返 0 让代码以为 colorBitmap 是 0 才报的；修了 GetObject 之后这一步也通了。
5. **ValueTuple 不可空** —— `_pendingLaunch = (string, string)?` 用 nullable，`?? default` 改成 `is { } pending` 模式匹配。

---

# R32 SpotlightWindow（独立启动器搜索面板）

## 设计要点

- **完全独立于 QuickTools**：独立窗口、独立全局快捷键（默认 Ctrl+Alt+Space）、独立持久化（`spotlight-trigger.json`）。两窗口可同时存在。
- **共用同一份启动项数据**：`SelectionRuntime.GetLauncherEntries()` 是单一数据源，App 在 `RefreshSettingsAsync` 里同时推给 QuickTools + Settings + Spotlight 三个消费者。CRUD 任一面操作都同步给所有消费者。
- **Ivory Jade 主题**：复用 FloatingSurface + PearlCard + Badge + Kicker + Muted classes，不构造新 Brush。布局参考 PowerToys Run / Spotlight（顶部搜索框 + 列表 + 底部 keycap 提示），但配色是亮色瓷器，不做暗色 acrylic。
- **键盘导航**：↑↓ 移动选中（钳制 0..count-1，不循环），Enter 启动选中项，Ctrl+Enter 跳设置页编辑该项，Esc 关闭。
- **搜索过滤**：`Contains(name, OrdinalIgnoreCase)`，足够快（启动项通常 <50 条）。空搜索框 = 显示全部。
- **鼠标也能用**：列表项 PointerPressed 直接启动（不强制键盘）。

## 数据流

```
用户按 Ctrl+Alt+Space
  └─ WindowsGlobalHotKey 触发 → App.OnSpotlightTriggered
       └─ if (IsVisible) Hide() else Show()    ← toggle 行为
            └─ SpotlightWindow.Show()
                 ├─ Opened 事件 → SearchInput.Focus() + 清空搜索框
                 └─ ReapplyFilter() → _filteredRows = _allRows（空查询 = 全部）

用户输入查询
  └─ SearchInput.TextChanged → ReapplyFilter()
       └─ _filteredRows = _allRows.Where(name.Contains(query, OrdinalIgnoreCase))
            └─ _selectedIndex = 0 + ApplySelectionVisual()

用户按 ↑↓
  └─ OnWindowKeyDown → MoveSelection(delta)
       └─ _selectedIndex = Clamp(_selectedIndex + delta, 0, count-1)
            └─ ApplySelectionVisual() + ScrollSelectedIntoView()

用户按 Enter
  └─ OnWindowKeyDown → LaunchCurrentAsync()
       └─ 读剪贴板 → Hide() → LauncherRunRequested(id, null, clip)
            └─ App 复用 OnLauncherRunRequested（同一个 runtime 入口）

用户按 Ctrl+Enter
  └─ OnWindowKeyDown → LauncherEditRequested?.Invoke(id)
       └─ App.OnSpotlightLauncherEditRequested
            └─ Hide() + RefreshAndShowSettings() + ShowAndScrollToLauncher() + RequestLauncherEdit(id)

用户按 Esc 或点别处
  └─ OnWindowKeyDown / Deactivated → Hide()
```

## 关键文件

### Core + Infrastructure

- `src/SelectionAssistant.Core/Input/SpotlightTriggerSettings.cs` — record，字段同 QuickToolsTriggerSettings 但去掉了 MouseChordEnabled。默认 `Ctrl+Alt+Space`。
- `src/SelectionAssistant.Infrastructure/Configuration/SpotlightTriggerStore.cs` — `LoadIfExists`/`Save`，原子写，照搬 QuickToolsTriggerStore。文件 `spotlight-trigger.json`。
- `ByhApplicationPaths.SpotlightTriggerFile`

### UI

- `src/SelectionAssistant.UI/Views/SpotlightWindow.axaml(.cs)` — 独立窗口（560×480，CenterScreen，Topmost，AcrylicBlur，无装饰）
  - 字段：`_allRows`（全量）/`_filteredRows`（过滤后）/`_selectedIndex`
  - 公开方法：`SetLauncherEntries(entries)` / `UpdateLauncherIcon(id, bitmap)` / `PrepareForShutdown()`
  - 公开事件：`LauncherRunRequested(id, sel, clip)` / `LauncherEditRequested(id)` / `SettingsRequested`
  - 关键私有：`ReapplyFilter` / `MoveSelection` / `ApplySelectionVisual` / `LaunchCurrentAsync` / `LaunchRowAsync`
- `src/SelectionAssistant.UI/Views/SettingsWindow.axaml(.cs)` — "启动器"分区追加 Spotlight 快捷键卡片（从"常规"移过来，避免两张卡片挤在同一个分区导致底部被裁掉）
  - AXAML 字段：`SpotlightKeyboardShortcutToggle` / `SpotlightCtrlModifierCheckBox` / `SpotlightAltModifierCheckBox` / `SpotlightShiftModifierCheckBox` / `SpotlightWinModifierCheckBox` / `SpotlightShortcutKeyComboBox` / `SpotlightShortcutStatusText`
  - 后端：`SetSpotlightTriggerSettings(settings, status, isError)` + `OnSaveSpotlightTriggerClick` + 事件 `SpotlightTriggerSettingsSaved` + `ShowAndScrollToLauncher()` + `RequestLauncherEdit(id)`
- `src/SelectionAssistant.UI/Themes/IvoryJade.axaml` — 新增 `Border.SpotlightRow` / `Border.SpotlightRow:pointerover` / `Border.SpotlightRow.Active` / `TextBox.SpotlightSearch` 样式

### App 接线

- `src/SelectionAssistant.App/App.axaml.cs`：
  - 新字段 `_spotlightWindow` / `_spotlightHotKey` / `_spotlightTriggerSettings` / `_spotlightLoadWarning`
  - `ToQuickToolsShape(SpotlightTriggerSettings)` 适配器 — 把 Spotlight settings 转 QuickTools shape 给 `WindowsGlobalHotKey` 构造（避免改 Platform 层）
  - `RegisterInitialSpotlightHotKey` + `CreateStartedSpotlightHotKey` + `OnSpotlightTriggered`（toggle）
  - `OnSpotlightTriggerSettingsSaved`（事务性热切换，照搬 QuickTools 模式）
  - `OnSpotlightLauncherEditRequested` + `OnSpotlightSettingsRequested`
  - `RefreshSettingsAsync` / `LoadLauncherIconsAsync` 都把 spotlight 当第三个消费者推
  - `RequestExit` 里 dispose `_spotlightHotKey` + `_spotlightWindow.PrepareForShutdown()`

## QuickTools toggle 修复（同一批附带）

`App.OnChordTriggered` 加 `if (IsVisible) Hide() else ShowAt()`。这一处同时被 chord 和 QuickTools 全局快捷键调用，所以一次改动修两个路径的 toggle 行为。

## 关键不变量（R32 新增）

1. **Spotlight 和 QuickTools 完全独立** — 独立快捷键、独立窗口、独立生命周期、独立持久化。共用数据但配置解耦。
2. **快捷键 toggle 只管自己** — `OnChordTriggered` 和 `OnSpotlightTriggered` 都只 toggle 自己的面板，不管另一个。两面板可同时显示（罕见，允许）。
3. **Ctrl+Enter 在 Spotlight 里 = 编辑当前选中项** — 跳到 SettingsWindow 启动器分区 + 打开该 entry 的编辑窗。
4. **搜索过滤用 `Contains(..., OrdinalIgnoreCase)`** — 不做 fuzzy/拼音首字母匹配，保持简单。
5. **↑↓ 越界钳制** — 到顶/到底不循环（避免误操作）。
6. **`WindowsGlobalHotKey` 接受 QuickToolsTriggerSettings** — Spotlight 在 App 层用 `ToQuickToolsShape` 适配器转换，**不修改 Platform.Windows 层**（保持 Platform 层稳定）。
7. **`ApplySelectionVisual` 走 ItemsControl.ContainerFromIndex** — 因为用 ItemsControl 而不是 ListBox（参考图样式高度定制，ListBox 默认样式会冲突），需要手动给 container 加/移除 `Active` class。

## 踩坑记录（R32）

1. **`IClipboard.TryGetTextAsync` 需要 `using Avalonia.Input.Platform`** — 缺这个 using 会报 "IClipboard 不包含 TryGetTextAsync" 错误（看似该方法是 IClipboard 的，实际是扩展方法）。
2. **`SearchInput.Focus()` 不能在构造函数调** — 窗口未显示，焦点设不上。要在 `Opened` 事件里调（每次 re-show 都重新 focus）。
3. **`_suppressFilterOnce` 等未用字段会触发 CS0649 警告** — NativeAOT 要求 0 警告，删掉未用字段。
4. **`LauncherCard` 名字不存在** — 早期 plan 里假设有 LauncherCard 控件，实际 SettingsWindow 里启动器分区叫 `LauncherSection`（StackPanel），BringIntoView 调它即可。
5. **`EntrySaved` 签名必须带 name**（R23 已踩过）— R32 Spotlight 复用同一套，无新坑。

## 测试

- `tests/SelectionAssistant.Core.Tests/Input/SpotlightTriggerSettingsTests.cs` — 9 个 Fact（Default/Normalize/Validate/ToDisplayText）
- `tests/SelectionAssistant.Core.Tests/Configuration/SpotlightTriggerStoreTests.cs` — 10 个 Fact（missing/roundtrip/disabled/atomic/schemaVersion/not-object/corrupt/invalid-settings/partial/too-large）

## CLI 探针

无新探针。Spotlight 是纯 UI 消费者，所有底层路径已被 R23 探针覆盖（`--probe-launcher-list` / `--probe-icon-extract`）。
