# BYH 当前交接快照

> 更新时间：2026-07-22 第四十六批增量
> 本文件是下一位 Agent 的首要入口，优先级高于目录内的历史快照。
> 项目根：`C:\dvr\gh-kb\selection-assistant`
> **模块文档**：`docs/architecture/00-architecture-overview.md`（改任何模块先看这个）
> 路线图待办：见 `handoff\BACKLOG-roadmap.md`（R44-R53 新增）

## 1. 一句话状态

`BYH`（By Your Hand）= Windows NativeAOT 选词翻译 + AI 动作工具。选中文字 → 不抢焦点的工具条 → 翻译/总结/解释/自定义功能；**R34 工具栏可见时按 F/J/Z/R（或任意配置的单字符）立即触发对应动作，按键被吞掉不传源程序，Esc 关闭，快捷键在设置→自定义功能里改**；**R37/R41 内建工具栏快捷键 R/C（提示词/复制）作为兜底入口，不隐藏工具栏；R37 修复批：后台线程调 Avalonia UI API 导致 C 崩/R 没反应，改用 `Dispatcher.UIThread.Post` 派发 + 吞键判断提前；R/C 全可在设置→常规→工具栏快捷键卡片里改（含禁用），新 settings 文件 `toolbar-shortcuts.json`；R39 把工具栏"已取词"状态区从 R37 的 Italic Georgia 文字版 "byh" 升级为用户提供的真实手写体透明底 PNG wordmark（"By Your Hand" 全文），裁剪到内容 bbox + 缩到 103×36（~5KB），打包为 `avares://SelectionAssistant.UI/Assets/Theme/byh-wordmark.png`；清理了 IvoryJade.axaml 里 R37 留下的 `TextBlock.WordmarkArt` 死样式**；**R40 大改版：Fast tool / QuickTools 改名 Ocean Eyes，围绕视觉模型为核心重做——`Ctrl+Alt+Q` 不再弹面板，直接进入全屏框选（UIA 辅助框选默认 ON，悬停桌面元素即贴合 bbox，用户一拖拽即停止辅助）；框完区域 → 共用工具栏出现在区域右上角（F=翻译 / J=解释 / Z=总结 / R=提示词 / C=复制，全部零改动复用划词 PromptTemplate 管线）→ 视觉模型 OCR 提取文本喂入工具栏 → 用户按 `Enter` 保存截图（默认 `%USERPROFILE%\Pictures\Ocean Eyes\ocean-eyes-yyyyMMdd-HHmmss.png` + 复制剪贴板）/ `Esc` 退出；QuickToolsWindow.axaml(.cs) 删除，迁移读旧 `quick-tools.json` 透明无感升级，新 settings 文件 `ocean-eyes.json` + `ocean-eyes-capture.json`**；**R41 交互重构：左键释放=确认框选（替代双击/Enter 主入口）；右键=区分语义——框选中右键取消（现有），工具栏在时右键重画（mouse hook `SwallowCheck` 吞掉右键 + `overlay.Reset()` 清空 rect + 重新辅助框选，OCR 缓存清空）；OCR 改惰性——工具栏确认后立即出现显示"未识别 · 按 F/J/Z/R/C 开始"（按钮全 disabled），首次按动作键才触发 OCR（`EnsureOceanEyesOcrAsync` 缓存 task 复用，同区域后续动作键几乎瞬间）；Enter 存图/Esc 退出均不 OCR；V 粘贴键删除（`ToolbarShortcutSettings.PasteKey` 移除，旧 toolbar-shortcuts.json 的 pasteKey 字段读时忽略）**；**R32 独立 Spotlight 搜索面板（Ctrl+Alt+Space，PowerToys-Run 风格 + Ivory Jade 配色）**。多厂商 Provider，DPAPI 密钥，全局可编辑提示词预设 + 用户自定义功能。左右键 chord 因与右键菜单冲突默认关闭，仅作可选兼容入口。**R24-R42 全部完成。第三十二批：R42 — Ocean Eyes overlay 锁定 + 单击确认 UIA + 白虚线 + 中间透明 + Move 删除 + R41 SwallowCheck 回收 + 截图竞态修复。** 第三十三批：Spotlight 搜索增强——三级匹配（子串 + 词首字母/camelCase 缩写 + 拼音首字母），内置 ~600 常用汉字拼音表，无外部依赖。启动器新增 7 个应用（A HUB / CC Switch / RK Keyboard / QQ / 微信 / 微信输入法 / KeySilk）+ ChatGPT 桌面端更名 Codex。**第三十四批：R43 — Spotlight 选中态可读性（两次尝试）。第一版以为是 `ItemsControl` 异步 realize 容器的时机问题，叠了 `LayoutUpdated` + `Dispatcher.Post` + generation 守底，结果无效——真因是 `ItemsControl.ContainerFromIndex` 返回内部 `ContentPresenter` 而非 DataTemplate 的 `Border`，给 container 加 class 永远命中不了 `Border.SpotlightRow.Active` 选择器。第二版改用**数据绑定驱动 class**：`LauncherEntryRow` 实现 `INotifyPropertyChanged` + 新增 `IsSelected`，DataTemplate 根 Border 写 `Classes.Active="{Binding IsSelected}"`（Avalonia 12 class-条件绑定），切换 `IsSelected` 时 Avalonia 自己把 Active 加到真实 Border 上。Active 视觉：`SurfaceSelected`（淡豆绿）填充 + 玉色边框 + 左侧 3px 玉色 inset 强调条 + 名称 Bold 玉色；`Active:pointerover` 守护选中色不被 hover 覆盖。顺手补 `TextBox.SpotlightSearch:focus` 抑制通用 `TextBox:focus` 2px 玉色边框（消除"搜索框变绿"错觉）。**第三十五批：R43 续——选中态 + 搜索框金花边视觉精修 + 全局 accent 改色。选中行 + 搜索框共享同一套"金花边"设计：外金边 `#FFD9C28A` + 香槟缝 `#FFFCF7EA` + 亮金内线 glint `#FFF4E7C8` + 香槟填充（via BoxShadow inset 多层叠加在 Border 上）。搜索框用 `Border.SpotlightGoldFrame` 包装 TextBox（只包 TextBox 不包闪电图标），内层 TextBox 通过 `Style.Resources` 覆盖 8 个 FluentTheme `TextControl*` resource key 全 Transparent 才能彻底消除自己的边框，避免"两层框"。全局 `SystemAccentColor` 整条色阶从玉色改成金色（影响所有 Fluent 控件：TextBox focus ring、CheckBox、Toggle、ProgressBar 等）——这是经过染色测试（临时改红色验证）后确认的唯一能让 FluentTheme TextBox focus visual 变色的层级。最大教训：FluentTheme TextBox 模板内部硬画 focus visual，不看 `BorderBrush`，只读 `SystemAccentColor`；遇到"颜色不对"的疑难第一时间做染色测试，不要反复改样式顺序/特异性/包装层。** **第三十六批：本批无代码改动——制定 R44-R53 Ocean Eyes 扩展 roadmap（来源：对照 `xiaowang.com` 小旺 AI 截图功能清单 + gh cli 搜索 Windows 长截图社区实现）。10 项功能写入 `handoff/BACKLOG-roadmap.md`：R44 取色器 / R45 二维码识别（ZXing.Net，纯托管）/ R46 贴图（钉图为浮动小窗，复用 NoActivateWindowHost）/ R47 数字序号标注 / R48 标注工具集（矩形/椭圆/箭头/画笔/高亮，依赖 R47 layer）/ R49 截图相册（缩略图网格浏览 `Pictures/Ocean Eyes/`）/ R50 带壳截图（Mac/iPhone/Browser 等外壳模板，Skia 合成）/ R51 截图美化（padding+阴影+圆角）/ R52 磁力吸（贴图自动吸附，依赖 R46）/ R53 长截图（移植 ShareX `ScrollingCaptureManager.CombineImages` 的 `memcmp` 像素匹配 + bestMatch 兜底算法，~600 行 0 新依赖）。明确排除：录屏 MP4 / GIF / 视频编辑 / AI 抠图（CPU/GPU 重，偏离 Ocean Eyes 轻量定位）。完成顺序建议：P0 先做 R44/R45/R47/R46（互不依赖，可并行）→ R52（依赖 R46）→ P1 R48-R51 → P1+ R53。每项统一验收：0 警告 + exe 增量 <100KB（除 ZXing +200KB / 外壳资源 +500KB-1MB）+ 双路径同步 + 机器侧验证 + handoff §3 新增章节。** **第三十七批：R44 取色器落地——Ocean Eyes 框选确认后按 P 弹出跟随鼠标的 6 倍放大镜（15×15 BGRA → 150×150 RGBA WriteableBitmap，中心古金色十字），HEX/RGB 实时显示，再次 P 取消、Esc 退出、左键任意位置确认（mouse hook 路由）；确认后 `#RRGGBB` 进剪贴板 + 工具栏状态槽显示"已复制 #RRGGBB"。`ScreenRegionCapture` 抽出共用 BitBlt→BGRA 管线（`CaptureRawBgra` + `SamplePixel`，跳过 PNG 编码避免 30Hz 采样爆 CPU）；新增 `ColorFormatter`（纯函数 hex/RGB 格式化，11 单测）+ `ColorPickerLoupe.axaml(.cs)`（`NoActivateWindowHost` 不抢焦，`DispatcherTimer` 33ms tick，`Screens.ScreenFromPoint` 工作区 clamp）；`SelectionRuntime` 新增 P 键分支（Enter 后、A-Z filter 前，Ocean Eyes 限定，跳过 OCR 路径）+ `StartColorPicker`/`HideColorPicker`/`SampleCursorRegion`/`OnColorPicked`/`GetCursorPos` P/Invoke；mouse hook `_colorPickerActive` 短路（左键 down → ConfirmPick + 吞，不触发 toolbar dismiss / 新 selection）；`DismissOceanEyes`/`ResetForRedraw`/`Dispose` 全部清理 loupe。0 新依赖。** **第三十八批：R46 贴图落地（v7 含六轮用户反馈调整 + Esc bug 修复）——Ocean Eyes 框选确认后按 T 把当前缓存 PNG 钉成 always-on-top 浮动小窗（干净裸图，圆角无金边，带出现/关闭凘入凘出 + 滚轮缩放平滑过渡动画）。v2 改动：(a) 修默认尺寸自动放大。(b) 滚轮缩放 ×1.1 clamp。(c) 去边框去标题栏。(d) 双击关闭（v2 用 Avalonia `DoubleTapped`——v3 弃用）。**v3 修复（用户反馈"依然没有双击关闭"+"缩放损失图像数据"）：(e) 双击改手动检测**（500ms / 8px）。**(f) 缩放改像素保真**：`BitmapInterpolationMode.None` nearest-neighbor。**v4 修复（用户反馈"默认只看到中间少部分"+"滚轮仅压缩信息"——根因终于定位）：(g) Avalonia `Stretch="None"` 是裁剪不是缩放**，v2/v3 设 `Width/Height` 只改裁剪框。v4 改用 `LayoutTransformControl` + `ScaleTransform` 真正重新 measure。**v5 调整（用户反馈"添加圆角边框 + T 后自动关闭选框"）：(h) 圆角古金边框**。**(i) T 变 terminal action**。**v6 调整（用户反馈"不需要边框保持干净 + 添加 esc 关闭 + 最小化有限度"）：(j) 去掉金边只留圆角 clip**。**(k) MinScale 0.1→0.25**。**(l) Esc 关闭贴图**——全局 keyboard hook 路由，`OnToolbarKeyPressed` 顶部加新 Esc 分支 LIFO 关闭最后一个贴图，三处协同保活 hook（`DismissOceanEyes` 改条件禁用 / T 分支末尾 `SetEnabled(true)` / `ClosePinned`/`CloseAllPinned` 列表空时禁用）。**v6 Esc bug 修复（用户反馈"ESC 没有用"——深度日志定位根因）**：T 分支的 `SetEnabled(true)` 之后，`DismissOceanEyes` 的 UI-thread Post `_windowHost.Hide()` 触发了 `ToolbarSessionView.onToolbarHidden` 回调，回调里**无条件** `SetEnabled(false)` 又把 hook 关了。修复：`onToolbarHidden` 加 `_pinnedWindows.Count == 0` 守护；同样守护加到 `ResetForRedraw` 和 `StopKeyboardHookQuiet`。**v7 调整（用户反馈"添加动画"）：(m) 出现动画**——Window 初始 `Opacity="0"`，AXAML 加 `<DoubleTransition Property="Opacity" Duration="0:0:0.15"/>`，`Opened` 事件设 `Opacity=1.0` 自动 150ms ease-out 凘入。**(n) 关闭动画**——`AnimateOutAsync()` 设 `Opacity=0` 凘出（150ms DoubleTransition）+ `Task.Delay(180)` 等过渡完成，`ClosePinned` 改 `async` 在 Hide+Dispose 前 `await window.AnimateOutAsync()`；窗口从 `_pinnedWindows` 列表移除在动画**开始前**（防快速二次 Esc 重入）。**(o) 滚轮缩放平滑过渡**——`ScaleTransform.Transitions` 加 `DoubleTransition` for `ScaleX`/`ScaleY`（120ms），滚轮改 `_userScale` 后 `ApplyScale` 设新 ScaleX/Y，Avalonia 自动 120ms 插值（不再瞬间跳变）。关键：Transitions 挂在 `ScaleTransform` 实例上（它是 `Animatable`），不是挂在 `LayoutTransformControl.LayoutTransform`（`LayoutTransform` 是 `Transform` 类型，换实例不能 DoubleTransition；只改 ScaleX/Y 属性可以）。**v8-v12 动画探索（5 个版本迭代，最终 v13 回滚搁置）**——v7 是纯 Opacity 凃入，用户反馈“弹出可以改为 mac 类似的放大弹出吗”。**v8**：scale 0.85→1.0 BackEaseOut（用户反馈“没变化，从侧面弹出”）。**v9 误解**：我误以为“从侧面弹出”是要侧面滑入，改成 TranslateTransform 侧边滑入（用户后续澄清是要 scale 弹簧，不是侧滑）。**v10**：scale 0.3→1.0 BackEaseOut 350ms（用户反馈“抖了好几下”+“从左上角弹不是正中间”）。**v11**：改 CubicEaseOut + 移到 Border.RenderTransform（用户反馈“没变化，依然从左上角”）。**v12**：Avalonia 关键帧 Animation 0.5→1.15→1.0（Mac 弹簧：小→过冲→稳定）+ 显式 `Frame.RenderTransformOrigin = RelativePoint(0.5,0.5,Relative)`（用户决定“算了先就这样吧”搁置）。**核心未解问题**：`ExtendClientAreaToDecorationsHint=True` 窗口上 RenderTransformOrigin 不可靠——AXAML 属性和 code-behind RelativePoint 都不能让 scale 从中心放大，实际从左上角放大。**v13 回滚**：回到 v9 的 TranslateTransform 侧边滑入（`Window.RenderTransform = TranslateTransform(400,100)→(0,0)` CubicEaseOut 300ms）——用户认可这个效果作为暂定方案。code-behind + AXAML 都恢复 v9 状态，移除所有 v10-v12 的 ScaleTransform / 关键帧 Animation / RenderTransformOrigin 代码。scale 弹簧动画**搁置，以后再说**（详见 §3x §22 完整探索日志 + 未来修复思路）。0 新依赖。**

**测试**：232/232 通过（Core 156 = R44 前 145 + ColorFormatter 11；Providers 35；Windows 41）。NativeAOT 发布成功，第四十二批金属质感双圆角框版 exe = 27,670,528 字节（`BYH.exe`）。

---

## 3j. 本会话（第二十二批增量）完成的工作：选区空弹窗修复（R24 Pass 2 + Pass 3 关闭）

### 改动：关闭 UIA Pass 2（祖先链）和 Pass 3（元素文本 fallback）

**用户报告的 bug**：没选中文字时，按住左键滑动一定距离也会触发划词弹窗。第一轮只删 Pass 3 后用户复测：症状仍在——"在空白区域划一下，它就读取到了左边工具栏的文字"。

**根因（完整版）**：`WindowsUiAutomationBackend.ReadSelection` 有三层尝试，**两层都是误触发源**：

1. **Pass 3**（已第一轮删除）：命中元素/焦点元素的可见文本 fallback（TextPattern `DocumentRange` / ValuePattern `Value`），以 `IsAmbiguous=false` 返回。
2. **Pass 2**（本轮删除）：**祖先链遍历**——从命中元素向上走 8 层，每层试 `TryReadSelectedText`。这是真正持续作恶的路径：选区常常挂在祖先层级的整个文档控件上，UIA `getSelection` 在未选中时可能返回**退化 range（光标点）或覆盖整个文档的 range**。结果：用户在空白区域划动 → `elementUnderMouse` 命中底层 UI 元素 → Pass 2 向上找到一个祖先 TextPattern → `getSelection` 返回非空（可能是工具栏/侧栏的整个文本）→ 工具栏弹出。

**修复（第二轮，彻底）**：
- `WindowsUiAutomationBackend.ReadSelection` 只保留 **Pass 1**：直接在命中元素、焦点元素、桌面根上读 selection。
- 删除 Pass 2 的循环和 `TryReadFromAncestors` 方法本体（避免 unused method 警告）。
- 删除 Pass 3 的循环（第一轮已删，本轮保留注释说明）。
- 清理：`_maxAncestorDepth` 字段、`DefaultMaxAncestorDepth` 常量、`WindowsUiAutomationBackend(int maxAncestorDepth)` 构造函数参数、`UIAutomationTextCapture(int maxAncestorDepth)` 构造函数参数（全删，因为只被 Pass 2 用）。
- 保留 `_controlViewWalker`（仍被 `GetTextsInRegion` BFS 用）、`TryReadElementText`（仍被框选 OCR BFS 用）。

**产品决策**：划词工具条应**只在真有选中文字时弹出**。Pass 1 的直接 selection 读不到 → 让剪贴板层（模拟 Ctrl+C）兜底——这是最可靠的"用户真选了什么"信号。

### 关键代码入口（第二十二批最终状态）
- `src/SelectionAssistant.Platform.Windows/Capture/WindowsUiAutomationBackend.cs:67` `ReadSelection` 入口
- `src/SelectionAssistant.Platform.Windows/Capture/WindowsUiAutomationBackend.cs:99-109` Pass 1 直接 selection 读（保留）
- `src/SelectionAssistant.Platform.Windows/Capture/WindowsUiAutomationBackend.cs:111-122` Pass 2 + Pass 3 删除后的合并注释 + return null
- `src/SelectionAssistant.Platform.Windows/Capture/WindowsUiAutomationBackend.cs:762` `TryReadSelectedText`（保留，只走 TextPattern.getSelection）
- `src/SelectionAssistant.Core/Selection/SelectionSessionManager.cs:173` 守卫 `_lastCapturedText is not null`

### 机器侧验证（2026-07-18 第二十二批）
- `dotnet build -c Release`：0 警告 0 错误。
- `dotnet test`：Core 137/137 + Windows 41/41 = 178/178 全过。
- 用户真机第一轮复测：Pass 3 删除后症状仍在 → 触发本轮 Pass 2 删除。
- 用户真机第二轮复测（诊断日志版）：用户在多个应用空白处划词 9 次，BYH.log 全部记录为 `source=ManualFallback len=0 preview=<null>`——UIA/clipboard 都没返回任何文本，session manager 正确不弹窗。用户确认"现在又没有这个问题了"。**bug 关闭。**

### 诊断日志（R33 永久保留）
为定位本次 bug，给 `SelectionSessionManager` 加了一个 `Action<string>? diagnosticSink` 可选参数，App composition root 把它接到 `RedactedLogger.Info("Capture", ...)`。每次划词捕获后打一行：
```
capture source={Source} len={Text?.Length ?? 0} preview="..." proc={SourceProcessId}
```
- Source 取值：`None` / `Accessibility`(UIA) / `SimulatedCopyCtrlInsert` / `SimulatedCopyCtrlC` / `Vision` / `ManualFallback`
- preview 截断到 40 字符（选中文本是敏感数据，但调试必须能识别"读到了什么元素"）
- 写到 `C:/Users/DeRant Vilmon Ram/AppData/Local/BYH/logs/BYH.log`
- **保留理由**：下次出现"误弹窗/不弹窗"问题，用户发个 log 末尾就能立刻定位是哪个 tier 出问题，不用再发版诊断。RedactedLogger 是 append-only 文件，无性能影响。
- 入口：`src/SelectionAssistant.Core/Selection/SelectionSessionManager.cs:175`，`src/SelectionAssistant.App/SelectionRuntime.cs:90`

### 主 Agent vs sub-agent 分工（第二十二批）
- Explore sub-agent：完整调用链调研（mouse hook → gesture classifier → session manager → UIA backend → toolbar 显示），定位根因，给出三个候选修复方案。
- 主 Agent：第一轮按推荐删 Pass 3；用户复测发现症状仍在；主 Agent 直接读 `WindowsUiAutomationBackend.cs` 源码定位 Pass 2 祖先链是真凶；做第二轮修改（删 Pass 2 + 清理字段/参数）；编译验证；更新文档。

### ⚠️ 关键教训（永久记录，第二轮追加）
- **"根因诊断"不能停在第一个可疑点**：第一轮调研锁定 Pass 3 并推荐删除，但用户真机复测显示症状仍在——说明 Pass 2 才是主因。**真机反馈 > 代码静态分析**。当用户说"还在"时，立刻往深挖，不要假设"用户没重启"或"缓存没清"。
- **症状在静态分析下"修了"但真机没好时，立刻停止猜测，加日志看真机数据**：第三轮我没再改 capture 代码，而是给 `SelectionSessionManager` 加 `diagnosticSink` 记录 `Source/len/preview/proc`，跑一遍发现 9/9 都是 `ManualFallback len=0`——证明第二轮的 Pass 2 删除**就是对的修复**，前一次"还在"很可能是用户测试时还在跑旧进程（Mutex 单实例，新 exe 启动会被旧的吃掉）。**先看数据再下结论**。
- **UIA 的 `getSelection` 在未选中时行为不可靠**：许多控件（Avalonia/Electron/Word/某些 Chromium 渲染的文档）在未选中时返回退化 range 或整个文档 range，无法用静态代码分析判断。**祖先链遍历会放大这个不可靠性**——遍历 8 层，每层都可能命中一个返回伪选区的控件。正确做法：只读鼠标直接命中的元素和焦点元素，不向上爬。
- **fallback 链每一段都应是"真有选中"的强证据**。Pass 2/3 都违反了这条——它们用"近似选中"当"真选中"。如果未来要恢复对只读控件/祖先文档的支持，必须引入二次确认（如 IsAmbiguous 标记 + 用户轻提示），不能直接弹工具栏。
- **Mutex 单实例 + 后台启动的坑**：用 `run_in_background:true` 启 BYH，父 shell 退出但子进程仍在运行（进程列表看得到 PID），但用户手动双击新 exe 时会被 Mutex 拒绝（exit 0 无输出）。所以"诊断版没生效"经常不是代码问题，是旧进程没杀干净。**发布新版前先 `taskkill //F //IM SelectionAssistant.App.exe`**。
---

## 3k. 本会话（第二十三批增量）完成的工作：R34 工具栏动作快捷键（F/J/Z/R + 可配置）

### 功能

划词弹出工具栏后，按 **F**（翻译）/ **J**（解释）/ **Z**（总结）/ 或任意用户配置的单字符，立即触发对应动作（无需鼠标点按钮）。按键被工具栏**吞掉**（不传给源程序）；Esc 关闭工具栏。快捷键可在设置→自定义功能→编辑每个动作时改。

### 关键设计决策（已和用户确认）

1. **润色是用户自定义动作**，不是内置。用户已在 `prompt-templates.json` 加了 custom-* 润色。快捷键机制对所有动作统一——内置三动作 + 任意 custom 动作都用同一个 `PromptTemplate.Shortcut` 字段。
2. **快捷键 = `PromptTemplate.Shortcut` 可选字段**，序列化到 `prompt-templates.json`。与模板共存最自然（一个动作 = 一个模板 = 一个快捷键），不需要新 store 文件。
3. **默认种子**：translate=F, explain=J, summarize=Z（取拼音首字母 Fānyì/Jiěshì/Zǒngjié）。custom 动作默认无快捷键，用户自己去配。
4. **吞键策略**：工具栏可见时，命中绑定的单字符 → 触发动作 + 吞键；未绑定的键全部 `CallNextHookEx` 透传给源程序；Esc 关闭 + 吞键。

### 实现要点

**1. `LowLevelKeyboardHook` 新建**（`src/SelectionAssistant.Platform.Windows/Hooks/LowLevelKeyboardHook.cs`）
- 镜像 `LowLevelMouseHook`：`WH_KEYBOARD_LL=13`，专用后台线程 + Win32 消息循环，rooted delegate，Start/Stop/Dispose 三段式生命周期。
- 关键差异：mouse hook 只观察不吞事件；keyboard hook 用 `event Func<int, bool>? KeyPressed`——订阅者返回 `true` = 吞键（`HookCallback` return 1），`false` = `CallNextHookEx` 透传。
- 多了 `Stop()` 方法（mouse hook 只有 Start/Dispose）——toolbar 频繁显示/隐藏，复用单例 + start/stop 比每次 dispose 重建更省。
- 只 hook `WM_KEYDOWN` 和 `WM_SYSKEYDOWN`。

**2. 为什么必须用低层键盘钩子，不能用 Avalonia KeyDown**
工具栏是 `WS_EX_NOACTIVATE`（`NoActivateWindowHost.cs:84` `WsExNoActivate = 0x08000000`）——永不取键盘焦点，Avalonia 的 `KeyDown` 事件永远不会在工具栏窗口上触发。全局 `RegisterHotKey` 也不行（单字符 F/J/Z/R 会和所有其他 app 的打字冲突）。只有低层钩子 + "仅工具栏可见时激活"可行。

**3. `PromptTemplate` 加 `Shortcut` 字段**（`src/SelectionAssistant.Core/Translation/PromptTemplates.cs`）
- record 从 `(Id, Name, Prompt, ThinkingEnabled)` 扩展为 `(..., ThinkingEnabled, Shortcut)`，`Shortcut` 默认 null。
- 三默认种子：translate=F, summarize=Z, explain=J。
- 新增 `FindByShortcut(string key)`——ordinal-ignore-case 匹配，供 hook handler 用。
- 新增 `TrySet(actionId, prompt, thinkingEnabled, shortcut)` 重载——用于编辑窗口保存所有字段。
- **`FromList` 合并策略**：loaded entry 覆盖 built-in，但如果 loaded entry 没 shortcut 且 built-in 有 → 保留 built-in 的默认 shortcut。这样老 `prompt-templates.json`（没 shortcut 字段）的用户也能拿到 F/J/Z，不需要重新配。

**4. `PromptTemplatesStore` 读写 shortcut**（`src/SelectionAssistant.Infrastructure/Configuration/PromptTemplatesStore.cs`）
- `ParseEntry`：`shortcut` 是可选字段，老文件没这个字段 → null（由 `FromList` 在合并时给 built-in 补默认）。
- `WriteEntry`：shortcut 仅在非默认（与 built-in 不同）且非空时写入——保持文件最小化。
- `WriteEntry` 的 skip 判断从 `prompt+thinking==default` 扩展为 `prompt+thinking+shortcut==default`，否则用户只改 shortcut 时整条 entry 会被跳过不写入。

**5. `PromptTemplateEditWindow` 加快捷键输入**（`src/SelectionAssistant.UI/Views/PromptTemplateEditWindow.axaml(.cs)`）
- AXAML 加一行：`MaxLength="1"` 的 `TextBox x:Name="ShortcutInput"`，带说明"划词弹出工具栏后按此键即触发该动作；留空则不绑定"。
- ⚠️ **Avalonia 12.x 的 TextBox 没有 `CharacterCasing` 属性**（WPF 才有）。第一版 AXAML 加了 `CharacterCasing="Upper"` 编译报 AVLN2000。改为不设这个属性，在 code-behind 用 `ToUpperInvariant()` 归一化。
- 事件签名从 `Action<string,string,bool>` 扩展为 `Action<string,string,bool,string?>`（加 shortcut 参数）——影响 `TemplateSaved`/`TemplateCreated` 两个事件。
- `ShowFor` 签名加 `string? currentShortcut` 参数。

**6. `SettingsWindow` + `App.axaml.cs` 透传 shortcut**
- `SettingsWindow.PromptTemplateSaved`/`PromptTemplateAdded` 事件签名同步扩展为 `Action<string,string,bool,string?>`。
- `OpenPromptEditor` 把 `current.Shortcut` 传给 `editor.ShowFor`。
- `App.axaml.cs` 三个 handler（`OnPromptTemplateSaved`/`OnPromptTemplateAdded`/`OnPromptTemplateReset`）签名同步扩展，转发给 runtime。
- 方法组订阅（`settingsWindow.PromptTemplateSaved += OnPromptTemplateSaved`）自动匹配新签名，无需改订阅行。

**7. `SelectionRuntime` 接线键盘钩子**（`src/SelectionAssistant.App/SelectionRuntime.cs`）
- 字段：`private readonly LowLevelKeyboardHook _keyboardHook`（非 null，ctor 里 new）。
- ctor：`_keyboardHook = new LowLevelKeyboardHook(msg => _logger.Info("KeyboardHook", msg));` + 订阅 `KeyPressed += OnToolbarKeyPressed`。
- **`ToolbarSessionView` 嵌套类扩展**：加 `Action? onToolbarShown/onToolbarHidden` 回调。`ShowToolbar` 末尾调 shown 回调（→ `_keyboardHook.Start()`），`HideToolbar` 末尾调 hidden 回调（→ `_keyboardHook.Stop()`）。
- **绕过 view 的直接 hide 点**也要 stop：`RunActionAsync` 和 `RunPromptAsync` 各有一处 `_windowHost.Hide()` 直调，后面跟一句 `StopKeyboardHookQuiet()`（新增辅助方法，吞异常 + 日志）。
- 不需要在 `Start()` 初始化时的防御性 hide 后 stop（那时工具栏没显示，hook 没启动，stop 是 no-op）。
- **handler `OnToolbarKeyPressed(int vkCode)`**：
  - Esc (0x1B) → 隐藏工具栏 + stop hook + 吞键
  - A-Z (0x41-0x5A) → `_promptTemplates.FindByShortcut((char)vkCode)` → 命中则 `_sessionManager.GetLastCapturedText()` → 有文本则 `RunActionAsync(template.Id, text)` + stop hook + 吞键；没文本则**透传**（不吞打字）
  - 其他键 → 透传
- `Dispose`：`_keyboardHook.KeyPressed -= OnToolbarKeyPressed;` + `_keyboardHook.Dispose();`（跟在 `_mouseHook.Dispose()` 后）。

**8. `SavePromptTemplateAsync` / `AddPromptTemplateAsync` 扩展**
- 老的 3 参数 `SavePromptTemplateAsync(actionId, prompt, thinking)` 改为委托新 4 参数版（用现有 shortcut 不变）。
- 新 4 参数 `SavePromptTemplateAsync(actionId, prompt, thinking, shortcut)` → `TrySet` 4 参数重载 → persist。
- `ResetPromptTemplateAsync` 改为 reset 时也恢复 built-in 默认 shortcut（F/J/Z），这样"恢复默认"真的回到出厂状态。
- 同理 `AddPromptTemplateAsync` 加 4 参数重载。

### 关键代码入口（第二十三批）
- `src/SelectionAssistant.Platform.Windows/Hooks/LowLevelKeyboardHook.cs` 整个文件（新建，~330 行）
- `src/SelectionAssistant.App/SelectionRuntime.cs:36` `_keyboardHook` 字段
- `src/SelectionAssistant.App/SelectionRuntime.cs:82` ctor 里 new + 订阅
- `src/SelectionAssistant.App/SelectionRuntime.cs:90` `ToolbarSessionView` 构造传 start/stop 回调
- `src/SelectionAssistant.App/SelectionRuntime.cs:1126` `OnToolbarKeyPressed` handler
- `src/SelectionAssistant.App/SelectionRuntime.cs:1107` `StopKeyboardHookQuiet` 辅助方法
- `src/SelectionAssistant.Core/Translation/PromptTemplates.cs:19` `PromptTemplate` record（加 Shortcut）
- `src/SelectionAssistant.Core/Translation/PromptTemplates.cs:58` 三默认 seed 带 F/Z/J
- `src/SelectionAssistant.Core/Translation/PromptTemplates.cs:115` `FindByShortcut`
- `src/SelectionAssistant.Core/Translation/PromptTemplates.cs:148` `TrySet` 4 参数重载
- `src/SelectionAssistant.UI/Views/PromptTemplateEditWindow.axaml:55-65` ShortcutInput TextBox
- `src/SelectionAssistant.UI/Views/PromptTemplateEditWindow.axaml.cs:24,30` 扩展后的事件签名

### 机器侧验证（2026-07-18 第二十三批）
- `dotnet build -c Debug`：0 警告 0 错误（先 debug 快速抓编译错误，再 AOT）。
- 首次编译两个错：(1) `uint` → `int` 隐式转换错（`nativeEvent.VkCode` 是 uint，`RaiseKeyPressedSafely` 收 int，显式 cast 修复）；(2) Avalonia 12 TextBox 无 `CharacterCasing` 属性（删掉该属性，改 code-behind `ToUpperInvariant` 修复）。
- 还有一个手滑错误：一次 Edit 的 `old_string` 末尾包含 `private void OnTranslateRequested(string sourceText)` 但 `new_string` 忘了把它放回去，导致方法签名丢失、`RunActionAsync(...)` 漂浮在 `{}` 里。补回签名修复。
- `dotnet publish -c Release -r win-x64`：**0 警告 0 错误**，NativeAOT 完整 `Generating native code` 无 IL2025/IL2050trim 警告，publish 目录 26MB exe 产出。
- 发布时旧进程 PID 46812 占着 exe 文件（Mutex 单实例坑，见 §3j 教训），`taskkill //F //PID 46812` 后重 publish 成功。

### ⚠️ R34 第一版真机 bug + 修复（start/stop race → 持久 hook + flag）

**症状**：用户报"按键没反应啊"。看 `BYH.log`：
```
23:05:27 第一次：installed ✓ → 按 F → 触发翻译 ✓ → stopped ✓
23:05:32 第二次：Start 5 秒 timeout → 触发 Dispose() → 对象废了
23:05:38 之后所有：ObjectDisposedException（永远起不来）
```

**根因（两个 bug 叠加）**：
1. 第一版 `LowLevelKeyboardHook` 镜像 `LowLevelMouseHook` 的 start/stop 模型——每次 toolbar show 都 `SetWindowsHookExW` + 起线程，hide 都 `UnhookWindowsHookEx` + 杀线程。但 toolbar 每分钟 show/hide 很多次，反复装/卸钩子有 race：第二次 start 时 `_startupCompleted.Wait(5s)` 超时。
2. `Start()` timeout 后我照抄了 mouse hook 的 `Dispose()` 调用——但 mouse hook 是"装一次跑到底"，timeout 后 Dispose 合理；keyboard hook 设计成"复用单例"，timeout 后 Dispose 把对象**永久废了**，后续所有 Start 都抛 ObjectDisposedException。

**修复**：改成**持久线程 + flag 切换**模型。
- `Start()` 只在 runtime ctor 调一次，hook 装一次跑到底（和 mouse hook 一致）。
- 新增 `SetEnabled(bool)` 方法——只写一个 `Volatile.Write(ref _enabled, ...)`。
- `HookCallback` 第一行就检查 `_enabled`：flag=0 直接 `CallNextHookEx` 透传（近零开销），flag=1 才查 `KeyPressed` 订阅。
- `Start()` timeout 时**不再 Dispose**——只抛异常让调用方决定（调用方 log 后继续，toolbar 仍能鼠标点击用）。
- `SelectionRuntime` 所有 `_keyboardHook.Stop()` 改成 `SetEnabled(false)`；ctor 里 `_keyboardHook.Start()` 一次。
- `StopKeyboardHookQuiet` 改名语义但保留，内部调 `SetEnabled(false)`。

**为什么 flag 模型优于 start/stop**：
- 零线程开销：show/hide 只写一个 volatile int，不起/杀线程。
- 零 race：flag 是原子写，hook 线程原子读，无 lifecycle 竞争。
- 零对象废弃风险：hook 对象生命周期 = app 生命周期，不会中途废。
- 性能：toolbar 隐藏时（常态）callback 只多一次 volatile read，对全局打字无感。

**机器侧验证（修复后）**：
- Debug 0 警告 + NativeAOT publish 0 警告。
- 启动日志干净：`Keyboard hook installed on native thread X` + `Persistent keyboard hook installed at runtime startup`，之后**无任何** Start/Stop/Error 日志。
- 启动坑：用 `run_in_background:true` (Git Bash) 启动会因 `Clipboard message window startup timed out` 崩溃（detached 进程失去消息泵环境？）。改用 `powershell.exe -Command "Start-Process ..."` 启动成功。这个剪贴板超时**和 R34 无关**，是 Bash 后台启动的环境问题，但记录在此避免下次踩坑。

### ⚠️ R34 真机调试踩的三个坑（按发现顺序）

**坑 1：start/stop race（已在上面"修复"段记录）** — 第一版频繁 start/stop，第二次 timeout + Dispose 把对象永久废了。修复为持久线程 + SetEnabled flag。

**坑 2：双 publish 路径**（⚠️ 最重要的环境坑）
- 我一直 `dotnet publish` 后测试 `src/SelectionAssistant.App/bin/Release/net10.0-windows/win-x64/publish/SelectionAssistant.App.exe`，并从这里启动验证。
- 但**用户实际运行的进程来自 `artifacts/publish/win-x64-nativeuia/SelectionAssistant.App.exe`**（Get-Process Path 看到的）。这个路径可能是某个启动脚本/托盘"重启 BYH"固定的。
- 结果：我修了 bug、在 bin/.../publish 验证日志干净，但用户跑的是 artifacts 旧版——bug 当然没修。日志停在 `23:22:16` 不增长是关键信号（用户进程没写这个 log）。
- **教训**：发布后必须 `Get-Process SelectionAssistant.App | Select Path` 确认用户实际跑的路径，把 exe 复制到那个路径。两个路径都要更新。命令：`cp src/SelectionAssistant.App/bin/Release/net10.0-windows/win-x64/publish/SelectionAssistant.App.exe artifacts/publish/win-x64-nativeuia/SelectionAssistant.App.exe`。

**坑 3：键盘钩子在 ctor 早期启动会导致 Clipboard 崩溃**
- 修复坑 1 后把 `_keyboardHook.Start()` 放在 ctor 最前面（紧跟 new + 订阅）。结果从 PowerShell 启动也崩：`Clipboard message window startup timed out`，异常码 `0xc0000409` (STATUS_STACK_BUFFER_OVERRUN/fail-fast)，Windows 事件日志 Id 1026。
- 根因推断：键盘钩子的后台线程在 ctor 早期就起跑消息循环，和主线程后续创建 `Win32Clipboard` 消息窗口有 Win32 时序竞争（窗口类注册/desktop attach？）。
- 修复：把 `_keyboardHook.Start()` 从 ctor 移到 `Start()` 方法末尾（`_mouseHook.Start()` 成功之后）。mouse hook 已证明能和剪贴板共存，键盘钩子跟在后面装就稳定。
- 正确启动序列（日志可见）：`MouseHook installed → Runtime started → KeyboardHook installed`。
- **教训**：任何启动后台 Win32 消息循环线程的钩子/资源，**不要在 ctor 早期启动**，挪到 `Start()` 里 mouse hook 之后（mouse hook 是已验证的基线）。

**坑 4：PowerShell Start-Process 启动 vs Git Bash run_in_background**
- `run_in_background:true` (Git Bash) 启动 NativeAOT GUI exe 会触发 `Clipboard message window startup timed out`（即使坑 3 修了也会，因为 detached 环境本身有问题）。
- `powershell.exe -Command "Start-Process -FilePath '...'"` 是可靠的启动方式。
- **永远用 PowerShell Start-Process 启动 BYH**，不要用 Git Bash 后台。

### 用户真机最终验证（2026-07-18 23:30，R34 关闭）
日志确认 5 次快捷键全部正确触发：
```
23:30:35 capture "试划词 + 按 F。" → 23:30:36 Shortcut 'F' → translate ✓
23:30:43 capture → Shortcut 'F' → translate ✓
23:30:48 capture "栏弹出 → 按 F。测完..." → Shortcut 'F' → translate ✓
23:30:50 capture "告诉我，或我可以主动看日志确" → 23:30:51 Shortcut 'J' → explain ✓
```
用户确认"OK,似乎正常了"。**R34 关闭。**

### 用户真机待验证（bash 无法触发键盘事件）
1. 选中文字 → 工具栏弹出 → 按 **F** → 翻译触发（不点按钮）
2. 同理按 **J**（解释）、**Z**（总结）
3. 如果用户已配了润色 custom 动作的快捷键（如 R）→ 按 **R** → 润色触发
4. **吞键验证**：工具栏可见时在记事本按 F → 记事本不应收到 'F' 字符（被吞）
5. **透传验证**：工具栏可见时按 'A'（未绑定）→ 源程序正常收到 'A'
6. **Esc 关闭**：工具栏可见时按 Esc → 工具栏隐藏
7. **隐藏后钩子卸载**：工具栏隐藏后按 F/J/Z/R → 源程序正常收到字符（钩子已 stop）
8. **Settings 配置**：编辑翻译动作 → 改快捷键为 T → 保存 → 重新划词 → 按 T 触发翻译，按 F 不触发
9. **向后兼容**：用户的 `prompt-templates.json` 里 built-in 动作没 shortcut 字段 → 默认应有 F/J/Z（由 `FromList` 合并补全）；custom 动作没配过 → 无快捷键（需手动编辑配）

### ⚠️ 关键教训（永久记录）
- **WS_EX_NOACTIVATE 窗口永远拿不到 Avalonia KeyDown**：这是设计上正确的（工具栏不能抢焦点，否则 Ctrl+V 粘贴流会断），但意味着任何"工具栏上的键盘交互"必须走低层钩子，不能指望 Avalonia 事件系统。设计工具栏键盘功能前，先确认窗口的 extended style。
- **低层键盘钩子必须限制激活范围**：`WH_KEYBOARD_LL` 是全局的，一旦激活，所有键盘事件都过你的 hook。**绝对不能**让它在 app 生命周期常驻——只在工具栏可见时 Start，隐藏即 Stop。否则用户的每次打字都经过你的 hook，性能和体验都会受影响，还可能被杀毒软件标记为键盘记录器。
- **吞键 vs 透传要谨慎**：`HookCallback` 返回 1 吞键、返回 `CallNextHookEx` 透传。**只在确认命中绑定且有 captured text 时才吞**——没文本时透传，避免"工具栏可见但用户其实在别处打字"被吃键。
- **事件签名扩展要走完整链路**：从最底层（`PromptTemplateEditWindow`）到最上层（`App.axaml.cs` handler）的事件签名全都要同步改，不能只改一处。方法组订阅（`+= OnPromptTemplateSaved`）能自动匹配新签名是 C# 的便利，但要确保 handler 方法签名真的改了，否则编译错。
- **Avalonia 12.x 和 WPF 的 API 差异**：`TextBox.CharacterCasing` 是 WPF 有、Avalonia 没有的属性之一。遇到 AVLN2000 "Unable to resolve suitable property" 先查 Avalonia API 文档而不是想当然。本例用 code-behind `ToUpperInvariant()` 归一化是等效方案。
- **`Edit` 工具的 `old_string` 末尾要小心**：如果 old_string 包含下一方法的签名行，new_string 必须也包含它（或者明确知道要在别处重建）。我这次忘了重建 `OnTranslateRequested` 签名，导致方法体漂浮。Edit 后立刻 `dotnet build` 能立刻抓到这类结构错误。
- **低层钩子不要频繁 start/stop，用 flag 模型**（第二十三批教训，⚠️ 永久）：第一版 `LowLevelKeyboardHook` 镜像 mouse hook 的 start/stop，每次 toolbar show/hide 都装/卸钩子 + 起/杀线程。结果第二次 start race timeout，timeout handler 调 Dispose 把对象永久废了，之后所有 start 都 ObjectDisposedException。**正确做法**：hook 在 app 启动装一次跑到底，show/hide 只翻一个 `Volatile.Write(ref _enabled)` flag，callback 第一行检查 flag=0 直接透传。零线程开销、零 race、零对象废弃风险。mouse hook 能用 start/stop 是因为它整个生命周期只 start 一次（runtime 启动时），不频繁切换。
- **`Start()` timeout 不要自动 Dispose**（第二十三批教训）：mouse hook 的 `Start()` 5 秒 timeout 后调 Dispose 合理（它一次性），但任何"可复用"的钩子/资源 timeout 后 Dispose 会让对象永久不可用。timeout 只抛异常，让调用方决定（log + 降级继续）。
- **双 publish 路径坑**（第二十三批教训，⚠️ 最重要的环境坑）：`dotnet publish` 默认输出到 `src/.../bin/Release/.../publish/`，但用户实际运行的 exe 在 `artifacts/publish/win-x64-nativeuia/`（可能是某个启动脚本/托盘菜单固定的）。**只发布到 bin 路径、只在 bin 路径验证，用户跑的却是 artifacts 旧版**——bug 当然没修。发布后必须：`Get-Process SelectionAssistant.App | Select Path` 确认用户实际跑的路径，把 exe `cp` 过去。日志 mtime 停止增长（用户进程不写 log）是这个坑的关键信号。
- **Win32 后台消息循环线程不要在 ctor 早期启动**（第二十三批教训）：键盘钩子的后台线程如果在 ctor 最前面就跑消息循环，会和主线程后续创建 `Win32Clipboard` 消息窗口竞争，导致 `Clipboard message window startup timed out` 崩溃（异常码 `0xc0000409`）。修复：挪到 `Start()` 方法里 `_mouseHook.Start()` 成功之后——mouse hook 是已验证的基线，能和剪贴板共存。任何"启动后台 Win32 消息循环"的组件都遵循这个时序：ctor 只 new + 订阅，`Start()` 里在 mouse hook 之后才真正启动。
- **BYH 启动必须用 PowerShell Start-Process，不能用 Git Bash run_in_background**（第二十三批教训）：`run_in_background:true` 启动 NativeAOT GUI exe 会触发 `Clipboard message window startup timed out`（detached 环境问题）。`powershell.exe -Command "Start-Process -FilePath '...'"` 可靠。

---

## 3l. 本会话（第二十四批增量）完成的工作：R35 结果窗口 Esc + 工具栏位置改"选区右下方"

### 改动 1：翻译/总结/解释结果窗口支持 Esc 关闭

**用户请求**：划词翻译等窗口不支持 Esc 关闭。之前 R34 只给**工具栏**加了 Esc（走低层键盘钩子），结果窗口（`ResultWindow`）漏了。

**为什么这次不用低层钩子，直接用 Avalonia KeyDown**：
- `ResultWindow`（`src/SelectionAssistant.UI/Views/ResultWindow.axaml`）是**正常激活窗口**——`WindowStartupLocation="CenterScreen"`，`ShowAndActivate()` 里调 `Activate()` 拿键盘焦点，**不是** `WS_EX_NOACTIVATE`（`ShowActivated="False"` 那种）。
- 所以它天然就能收 Avalonia `KeyDown`，不用走 `LowLevelKeyboardHook`。只有工具栏那种 `WS_EX_NOACTIVATE`（永不抢焦点）的窗口才必须用低层钩子。
- 走低层钩子反而会出问题：结果窗口显示时工具栏已隐藏 + 钩子已禁用（`SelectionRuntime.cs:1091-1095`），如果硬把钩子打开来吃 Esc，全局所有 Esc 都会被吞，影响其他 app。

**实现**（完全照搬 `SpotlightWindow.axaml(.cs)` 的模式）：
- `ResultWindow.axaml` 根 `<Window>` 加 `KeyDown="OnWindowKeyDown"`。
- `ResultWindow.axaml.cs` 加 `using Avalonia.Input;`（引 `Key` / `KeyEventArgs`）+ 一个 `OnWindowKeyDown` handler，命中 `Key.Escape` 时 `Hide()` + `CloseRequested?.Invoke()`（与"关闭"按钮行为完全一致——已经存在的 `Closing` handler 会保证只 Hide 不真关）。

### 改动 2：工具栏位置改为选区右下方

**用户请求**：划词弹窗能不能设置位置在选中文字的右下方？

**之前的行为**（R35 前）：`SelectionRuntime.ToolbarSessionView.ShowToolbar` 直接把 `gesture.MouseUpX/MouseUpY` 传给 `NoActivateWindowHost.ShowAtNoActivate`，后者加固定 16px 偏移显示。问题：
- 左→右、上→下正常选择（多数情况）没问题——`mouseUp` 就在选区右下。
- 但**右→左**或**下→上**选择（很多用户从句末往句首拖）会让 `mouseUp` 落在选区**左上**——工具栏就跑到选区左上方，挡住还没读过的上文。

**新行为**（R35 后）：`ShowToolbar` 算 `anchorX = max(MouseUpX, MouseDownX)`、`anchorY = max(MouseUpY, MouseDownY)`——取拖拽矩形 `min..max` 的右下角，无论拖拽方向，都是选区视觉右下角。再交给现有的 `ShowAtNoActivate`（加 16px 右下偏移）。

**为什么用 mouseDown/mouseUp 的 max 而不是真去读 UIA 选区 bounding rect**：
- `SelectionGesture`（`ISelectionTextCapture.cs:53`）只带鼠标 down/up 两对坐标，**没有** UIA 文本选区几何。`WindowsUiAutomationBackend.ReadSelection` 走 `IUIAutomationTextPattern::GetSelection` 但**不调** `IUIAutomationTextRange::GetBoundingRectangles`（vtable slot 10）——目前只取字符串。
- 加 UIA bounding rect 需要改：capture backend（读 rect）→ `CaptureResult`（带 rect）→ `SelectionGesture` 或新字段 → `ToolbarSessionView.ShowToolbar`（消费）。改动面 4 处文件、跨 3 个项目，且 UIA bounding rect 在多行选区返回多个 rect（每行一个），需要算并集 bounding box。**这次先不做**，等用户真机测过简单的 `max(mouseUp, mouseDown)` 方案效果，如果不够好再升级到 UIA bounding rect。
- mouseDown/mouseUp 的 max 对"鼠标拖拽选词"这个手势是 0 成本 0 风险的强近似——用户从选区一端拖到另一端，两端的 max 就是选区右下。

**未做（明确决定）**：
- 不做屏幕边缘 clamp（toolbar 超出右/下屏边界时回缩）。如果实际使用中遇到"工具栏被屏幕边缘切掉"再加。
- 不读 UIA `GetBoundingRectangles`（见上）。
- 不加 settings 让用户改"右下/左下/光标处"——除非用户提出来。

### 关键代码入口（第二十四批）

| 文件 | 改动 |
|---|---|
| `src/SelectionAssistant.UI/Views/ResultWindow.axaml:1-10` | 根 `<Window>` 加 `KeyDown="OnWindowKeyDown"` |
| `src/SelectionAssistant.UI/Views/ResultWindow.axaml.cs:1-6` | `using Avalonia.Input;` |
| `src/SelectionAssistant.UI/Views/ResultWindow.axaml.cs` `OnWindowKeyDown`（在 `OnCloseClick` 后） | Esc → Hide + CloseRequested |
| `src/SelectionAssistant.App/SelectionRuntime.cs:1462-1473` | `ShowToolbar` 算 max(mouseUp, mouseDown) 再传 `ShowAtNoActivate` |

### 机器侧验证（2026-07-18 第二十四批）
- `dotnet build -c Debug`：0 警告 0 错误。
- `dotnet test`：Core 137/137 + Providers 35/35 + Windows 41/41 = 213/213 全过。
- `dotnet publish -c Release -r win-x64`：0 警告，`Generating native code` 通过，publish 目录 26,926,592 bytes。
- 双路径同步：`cp bin/.../publish/SelectionAssistant.App.exe artifacts/publish/win-x64-nativeuia/SelectionAssistant.App.exe`。
- 启动验证（§3k 教训：用 PowerShell Start-Process，不用 Git Bash run_in_background）：PID 34360 启动成功，日志显示正确序列 `MouseHook installed → Runtime started → KeyboardHook installed → Persistent keyboard hook installed`，无 Clipboard timeout 崩溃。

### 用户真机待验证（bash 无法触发键盘事件 + 鼠标拖拽）
1. 选中文字 → 翻译结果窗口弹出 → 按 **Esc** → 窗口关闭
2. 同理 Esc 关闭"解释"/"总结"结果窗口
3. **右→左选择**（从句末拖到句首）：工具栏应出现在**选区右下角**（句末下方），不是左上
4. **下→上选择**（从段落末尾拖到开头）：同理工具栏在选区右下
5. **左→右选择**（默认方向）：行为不变，工具栏仍在鼠标释放位置附近
6. 工具栏 Esc（R34）功能仍正常——这次没动 `OnToolbarKeyPressed`

---

### R35 Pass 2：工具栏自动 clamp 到屏幕工作区（永不超出屏幕）

**用户请求**：能不能自动判断窗口不超出屏幕范围。

**之前（Pass 1，R35 第一轮）的行为**：`ToolbarSessionView.ShowToolbar` 算出 `max(mouseUp, mouseDown)` 作为右下锚点，host 加固定 +16px 偏移显示。**问题**：选区如果在屏幕右下角（最右一列字 / 最后一行字 / 任务栏上方），工具栏就被推出屏幕外（右半边或下半边看不见），尤其在多显示器拼接边缘很常见。

**Pass 2 修复**：在锚点和 host 落子之间加一层 clamp。

**clamp 算法**（镜像已验证的 `QuickToolsWindow.ClampToScreen`，见 `src/SelectionAssistant.UI/Views/QuickToolsWindow.axaml.cs:128`）：
1. 用 Avalonia `Screens.ScreenFromPoint(anchor)` 拿到锚点所在屏幕（多显示器自动正确）。
2. 拿 `screen.WorkingArea`（PixelRect，物理像素；已扣除任务栏）。
3. **默认 placement**：top-left = `(anchor + 16, anchor + 16)`（保留 Pass 1 的偏移观感）。
4. **翻转**：如果 `top-left.x + width > work.Right`（右溢出）→ 翻到锚点左侧：`top-left.x = anchor.x - 16 - width`；y 同理（下溢出翻到上方）。镜像上下文菜单的"右边放不下就放左边"行为，并保证锚点（选区右下角）本身不被工具栏覆盖。
5. **clamp 到工作区原点**：`top-left.x = max(top-left.x, work.X)`、`top-left.y = max(top-left.y, work.Y)`——防止翻过头或者高 DPI 多屏拼接缝隙里出现工具栏。
6. **再保险**：如果工具栏比工作区还宽/高（极少），`top-left.x = max(work.X, work.Right - width)` 保证 top-left 始终可见。

**调用链（完整）**：
```
SelectionSessionManager → ToolbarSessionView.ShowToolbar(gesture)
  ↓ max(MouseUp, MouseDown) 算锚点（Pass 1 不变）
  ↓ ToolbarWindow.ClampAnchor(anchorX, anchorY) ← Pass 2 新增
  │   ↓ Avalonia Screens.ScreenFromPoint + WorkingArea
  │   ↓ 翻转 + clamp → 返回 PixelPoint（窗口最终 top-left，含 +16）
  ↓ IWindowFocusController.ShowAtNoActivatePoint(left, top) ← Pass 2 新增（host 直接落子，不再 +16）
```

**为什么需要新增 `ShowAtNoActivatePoint` 而不是用 `ShowAtNoActivate`**：
- 老 `ShowAtNoActivate(x, y)` 的语义是"以 (x, y) 为锚点 + 加 16 偏移"——它假设调用方传的是**锚点**，不是最终 top-left。
- 现在 clamp 算法在 `ToolbarWindow.ClampAnchor` 里已经算出**最终 top-left**（含 +16、含翻转、含 clamp），如果再传给 `ShowAtNoActivate`，host 又会加一次 +16，结果错位 32px。
- 解法：加一个新方法 `ShowAtNoActivatePoint(left, top)`——参数就是窗口最终 top-left，host 不再加偏移，直接 `SetWindowPos(left, top)`。老 `ShowAtNoActivate` 改为内部委托新方法（`ShowAtNoActivate(x, y) => ShowAtNoActivatePoint(x+16, y+16)`），保留向后兼容。
- `IWindowFocusController` 是公共抽象接口，加方法是标准演进，无破坏。

**`ClampAnchor` 的 fallback 处理 `SizeToContent` 首次显示**：
- 工具栏是 `SizeToContent="WidthAndHeight"`——首次 show 前 Avalonia 还没 measure，`Bounds.Width/Height` 是 0。
- 直接用 0 算 clamp 会误判"永远不溢出"。
- `ClampAnchor` 加 fallback：`width = Bounds.Width > 0 ? Bounds.Width : 460`、`height = Bounds.Height > 0 ? Bounds.Height : 40`。460/40 是按当前按钮 + Padding 估算的保守值——首次显示可能略不精准，但后续 show 都读真实尺寸（窗口复用不重建）。
- 用户首次划词的位置如果在屏幕右下边缘，首次显示可能还有溢出（fallback 估小了），但**第二次起就准了**。

### 关键代码入口（R35 Pass 2）

| 文件 | 改动 |
|---|---|
| `src/SelectionAssistant.UI/Views/ToolbarWindow.axaml.cs:1` | `using Avalonia;`（PixelPoint / PixelRect） |
| `src/SelectionAssistant.UI/Views/ToolbarWindow.axaml.cs` `ClampAnchor(int x, int y)` | Pass 2 新增，~50 行 |
| `src/SelectionAssistant.Platform.Abstractions/IWindowFocusController.cs:16` | 加 `void ShowAtNoActivatePoint(int left, int top);` |
| `src/SelectionAssistant.Platform.Windows/Windowing/NoActivateWindowHost.cs:54-66` | `ShowAtNoActivate` 改委托；新增 `ShowAtNoActivatePoint` |
| `src/SelectionAssistant.App/SelectionRuntime.cs:1465-1480` | `ShowToolbar` 调 `ClampAnchor` + `ShowAtNoActivatePoint`（替代旧 `ShowAtNoActivate`） |

### 机器侧验证（2026-07-19 第二十五批）
- Debug build：0 警告 0 错误（首个错是 `using Avalonia;` 引入 `Rect` 歧义——`Avalonia.Rect` vs `SelectionAssistant.Platform.Windows.Capture.Rect`；解法：不在 `SelectionRuntime.cs` 加 `using Avalonia;`，单点用全名 `Avalonia.PixelPoint`）。
- 第二个错：`Screen` 类型找不到命名空间——`QuickToolsWindow` 用 `var screen`（隐式类型）绕过命名空间 import；改 `var` 后过。
- `dotnet test`：Core 137/137 + Providers 35/35 + Windows 41/41 = 213/213 全过。
- `dotnet publish -c Release -r win-x64`：0 警告，`Generating native code` 通过，publish 目录 26,928,128 bytes。
- 双路径同步：`cp` 到 `artifacts/publish/win-x64-nativeuia/`。
- 启动验证（PowerShell Start-Process）：PID 43312 启动成功，启动序列 `MouseHook installed → Runtime started → KeyboardHook installed → Persistent keyboard hook installed`，无 Clipboard timeout 崩溃。

### 用户真机待验证（Pass 2 补充，bash 无法触发鼠标拖拽）
7. **选区靠右边缘**（屏幕最右一列字）：工具栏应翻到选区**左侧**显示，不溢出右边缘
8. **选区靠下边缘**（最后一行 / 任务栏上方）：工具栏应翻到选区**上方**显示，不溢出下边缘
9. **选区在多显示器拼接缝**：工具栏应在锚点所在显示器的工作区内（不跨屏、不进缝）
10. **正常位置**（屏幕中间）：行为不变，工具栏仍在选区右下方（Pass 1 默认）

### ⚠️ 关键教训（永久记录）
- **正常激活窗口（ResultWindow/SpotlightWindow）用 Avalonia KeyDown，WS_EX_NOACTIVATE 窗口（ToolbarWindow）才需要低层钩子**——不要看到"加键盘快捷键"就反射性地用 `LowLevelKeyboardHook`。低层钩子是全局的，开了影响所有 app 的打字 + 杀软可能误报键盘记录器。判断标准：窗口 `ShowActivated="False"` 或 host 调了 `WsExNoActivate = 0x08000000` → 必须用钩子；否则用 Avalonia 原生 KeyDown。
- **`max(mouseDown, mouseUp)` 是拖拽矩形右下角的零成本近似**——比读 UIA `IUIAutomationTextRange::GetBoundingRectangles` 简单 4 个数量级，对"鼠标拖拽选词"这个唯一选词入口完全够用。不要过早工程化加 UIA 几何读取，除非用户真机反馈"位置不对"。
- **`Hide() + event?.Invoke()` 而不是 `Close()`**：所有 BYH 的二级窗口（ResultWindow/SpotlightWindow/ParameterInputDialog）都遵循"Hide 不 Close"——`Closing` handler 拦截真关，app 退出时 `PrepareForShutdown()` 把 `_allowClose` 置真才允许 Close。这是为了窗口能复用（下次显示不用重建）。新加任何关闭入口（Esc、按钮、外部信号）都要走 `Hide()` + `CloseRequested?.Invoke()` 这条路，**不要直接 `Close()`**。
- **`Avalonia.Screens.ScreenFromPoint` + `WorkingArea` 是 BYH 屏幕几何的标准入口**（第二十五批教训，⚠️ 永久）：不要自己 P/Invoke `MonitorFromWindow` / `GetMonitorInfo` / `SystemParametersInfo`——`QuickToolsWindow.ClampToScreen`（`QuickToolsWindow.axaml.cs:128`）和 R35 Pass 2 的 `ToolbarWindow.ClampAnchor`（`ToolbarWindow.axaml.cs`）都用 Avalonia 的 `Screens.ScreenFromPoint(point).WorkingArea`，返回 `PixelRect` 物理像素坐标，已扣除任务栏，多显示器自动正确。`Bounds.Width/Height` 也是物理像素，和 `WorkingArea` 同坐标系。**注意** `Screen` 类型的命名空间不直接 import——`QuickToolsWindow` 用 `var screen = Screens.ScreenFromPoint(...)` 绕开显式类型名，避免再加 `using`。新代码跟着用 `var`。
- **`SizeToContent` 窗口首次显示前 `Bounds` 是 0×0**（第二十五批教训）：clamp / 翻转算法需要窗口尺寸来算 right/bottom 边缘对齐。首次 show 前 Avalonia 还没 measure，`Bounds.Width/Height == 0`——直接用会误判"永远不溢出"。fallback：给一个保守的 width/height 估算（如工具栏的 `460 × 40`），首次可能略不精准，但窗口是复用的，第二次起就准。**不要为了"首次也准"去加 measure-pass-await，那是过度工程**——划词场景用户每次会划很多次，首次不完美完全可接受。
- **anchor 语义和 top-left 语义不能混**（第二十五批教训）：`ShowAtNoActivate(x, y)` 老接口的 (x, y) 是**锚点**（host 内部 +16 偏移成 top-left）。clamp 算法把"锚点 + 屏幕边缘判定"算完返回的是**最终 top-left**（已含 +16）。两个语义混在一起会让 top-left 错位 32px（host 再加一次 16）。解法：**新增一个明确语义的方法**（`ShowAtNoActivatePoint(left, top)`），不要试图把新语义塞进老方法——老方法有向后兼容负担，签名改了所有调用点都要审。新方法 + 老方法委托新方法是更安全的演进。
- **加 `using Avalonia;` 要警惕类型歧义**（第二十五批教训）：`Avalonia.Rect` 和 BYH 自己定义的 `SelectionAssistant.Platform.Windows.Capture.Rect` 重名。在 `SelectionRuntime.cs` 加 `using Avalonia;` 后，所有 `Rect` 引用变歧义 CS0104。解法优先级：(1) **只在单点需要 Avalonia 类型时用全名** `Avalonia.PixelPoint`（本次选这个）；(2) 如果多处用，加 `using AvaloniaRect = Avalonia.Rect;` 别名；(3) 极端情况才重构 BYH 自己的 Rect 改名。不要图省事加全局 `using Avalonia;` 然后改一堆 `Rect` 引用——副作用面太大。

---

## ✅ 2. 历史问题已解决（R20/R21，2026-07-17 用户确认）

以下两个问题曾长期反复（之前几轮"一点没修"），**现已彻底解决并经用户真机验证**。记录根因供后续参考：

### 问题 A（已解决）：划词工具条在"未选中文字"时误触发
- **真根因**（之前几轮没抓到）：不在 UIA 返回非空文本，而在 `WindowsSelectionTextCapture` 的降级分支 + `SessionCoreAsync` 守卫配合：
  - `WindowsSelectionTextCapture.cs:108-111`：UIA 和剪贴板都取不到文本时，因 `ManualFallbackEnabled=true`（默认）返回 `CaptureResult(null, CaptureSource.ManualFallback, false)`。
  - `SelectionSessionManager.cs`（旧）：守卫 `_lastCapturedText is null && result.Source != CaptureSource.ManualFallback` —— ManualFallback 开了后门，绕过守卫强制显示工具栏。
  - ManualFallback 在底层无法区分"用户选了词但 UIA 读不出"和"用户根本没选词（双击空白）"——两者都走同一分支，等价于"永远显示"。
- **修复**：`SelectionSessionManager.cs:175` 删除 `&& result.Source != CaptureSource.ManualFallback`，改为 `_lastCapturedText is null` 一律 return。chord 流程不受影响（走 `GetLastCapturedText()` + 剪贴板兜底独立路径）。
- **测试**：`ManualFallbackSourceWithNoText_DoesNotShowToolbar`（回归守卫）。

### 问题 B（已解决）：划词工具条布局不均衡
- **修复**：`ToolbarWindow.axaml` 主行 Grid 从 `Auto×8` 改为 `Auto×6,*,Auto`；StatusText 用 `*` 列 + `MinWidth=80` + `TextAlignment=Right` + `HorizontalAlignment=Stretch`；按钮统一 `Padding="8,3"`、`ColumnSpacing=4`。状态文字长度变化时按钮区不再被撑动。
- **注**：`NoActivateWindowHost.ShowAtNoActivate` 的 `SWP_NOSIZE` 不阻止 Avalonia `SizeToContent`（SWP_NOSIZE 只约束单次 SetWindowPos，SizeToContent 是 Avalonia 层自行调尺寸）——此前的怀疑排除。

---

## 3m. 本会话（第二十六批增量）完成的工作：R36 品牌名统一为 BYH

### 用户请求
"统一一下应用名叫BYH，不要叫select assistance"

### 摸底结论（关键：用户可见界面早已 100% BYH）
通过两个只读 sub-agent 扫描整个 `src/`（排除 bin/obj/artifacts/handoff/docs）确认：**所有用户可见的显示名——窗口标题、托盘菜单、tooltip、设置页文字、错误消息、Mutex 名 `Global\BYH_ByYourHand_SingleInstance`、配置目录 `%LOCALAPPDATA%\BYH`、日志 `BYH.log`、HTTP UA `BYH/0.1`——早就全部是 BYH 了**，零处 "Selection Assistant" 残留。

真正还没统一的只有**"技术表层可见名"**——任务管理器/文件资源管理器里的 `SelectionAssistant.App(.exe)`、6 个后台线程名、1 个 Win32 窗口类名（Spy++/调试器可见）、exe 右键属性对话框（详细信息字段全空）。

**附带修复一个真 bug**：`OpenAiCompatibleStreamingProvider.cs:172` 和 `OpenAiCompatibleVisionOcrClient.cs:280` 的错误消息叫用户"运行 `BYH.exe --set-secret`"，但实际 exe 叫 `SelectionAssistant.App.exe`——照着命令找不到文件。改完后消息和 exe 名才一致。

### 改动清单（15 处）

#### P0 — 运行时必改（3 处）
| 文件 | 改动 |
|---|---|
| `src/SelectionAssistant.App/SelectionAssistant.App.csproj` | 加 `<AssemblyName>BYH</AssemblyName>` + `<AssemblyTitle>BYH</AssemblyTitle>` + `<Product>BYH</Product>` + `<Company>By Your Hand</Company>` + `<Description>BYH — By Your Hand...</Description>` |
| `src/SelectionAssistant.App/App.axaml.cs:1057` | `avares://SelectionAssistant.App/Assets/app-icon.ico` → `avares://BYH/Assets/app-icon.ico` |
| `src/SelectionAssistant.UI/Views/SettingsWindow.axaml:874` | `avares://SelectionAssistant.App/Assets/app-icon.png` → `avares://BYH/Assets/app-icon.png` |

#### P1 — 启动器必改（3 处）
| 文件 | 改动 |
|---|---|
| `BYH.cmd:3` | `SelectionAssistant.App.exe` → `BYH.exe` |
| `create-launchers.ps1:8` | `$exe` 路径中 `SelectionAssistant.App.exe` → `BYH.exe` |
| `create-launchers.ps1:32` | cmd 内容字符串里同名替换 |

#### P2 — 后台线程名/窗口类名（7 处，深度工具可见）
| 文件 | 改动 |
|---|---|
| `Program.cs:834` | 线程名 `SelectionAssistant.UIAutomationProbe` → `BYH.UIAutomationProbe` |
| `Capture/UIAutomationTextCapture.cs:152` | `SelectionAssistant.UIAutomation` → `BYH.UIAutomation` |
| `Capture/WindowsUiAutomationBackend.cs:677` | `SelectionAssistant.RegionOcr.UIAutomation` → `BYH.RegionOcr.UIAutomation` |
| `Clipboard/Win32Clipboard.cs:39` | 窗口类名前缀 `SelectionAssistant.Clipboard.` → `BYH.Clipboard.` |
| `Clipboard/Win32Clipboard.cs:57` | 线程名 `SelectionAssistant.ClipboardMessages` → `BYH.ClipboardMessages` |
| `Hooks/LowLevelKeyboardHook.cs:88` | `SelectionAssistant.KeyboardHook` → `BYH.KeyboardHook` |
| `Hooks/LowLevelMouseHook.cs:63` | `SelectionAssistant.MouseHook` → `BYH.MouseHook` |

#### P3 — 文档同步（2 文件）
- `docs/architecture/07-build-publish-run.md`：`Get-Process -Name 'SelectionAssistant.App'` → `'BYH'`；产物描述 `SelectionAssistant.App.exe` → `BYH.exe`（删掉过时的 26,606,080 字节数）；启动入口表"直接 exe"行同步。
- 本文件（`handoff/00-CURRENT-HANDOFF.md`）：顶部状态行 + 新增本 §3m 小节。**历史教训里的旧 exe 名（如 §3l line 298 的 `cp` 命令）保留原文，不重写历史**。

### 不改的（确认正确保持）
- **所有 `namespace SelectionAssistant.*` 声明**（~50+ 处，技术标识符，改了破坏编译且用户看不到）
- **所有 `avares://SelectionAssistant.UI/...` 路径**（7 处，指向 UI 项目，UI 的 AssemblyName 没改所以不受影响）
- **所有 ProjectReference、x:Class、csproj 文件名、项目目录名**（改了破坏 MSBuild 还原）
- **`app.manifest` 的 `<assemblyIdentity name="SelectionAssistant.App">`**（UAC 用的程序集标识，独立于 `<AssemblyName>` 属性，改了反而触发 UAC 重新缓存）
- **Mutex 名、配置目录、日志名、HTTP UA、TrayIcon tooltip、所有窗口 Title**（早已是 BYH）

### 风险评估（sub-agent 确认的关键路径）
- ✅ **自重启安全**：`App.axaml.cs:1089` 的 `RequestRestart()` 用 `Environment.ProcessPath` 动态取路径，自动跟随 exe 名，无硬编码。
- ✅ **单实例锁不受影响**：Mutex 名 `Global\BYH_ByYourHand_SingleInstance` 是字面量常量，独立于 AssemblyName。机器验证：第二个 BYH.exe 立即 exit code=0 退出。
- ✅ **NativeAOT 不受影响**：AssemblyName 是 MSBuild 属性，编译前解析；`Generating native code` 正常通过。
- ✅ **测试项目不引用 App**：Core/Providers/Windows 测试都不依赖 App 的 AssemblyName，213/213 全过。
- ⚠️ **avares:// 解析依赖 AssemblyName**：这是唯一的连锁点。Avalonia 的 `avares://<name>/<path>` 用编译后程序集名（= AssemblyName）解析，不是 RootNamespace 也不是 csproj 文件名。所以 AssemblyName 改 BYH 后，2 处 `avares://SelectionAssistant.App/*` 必须同步改成 `avares://BYH/*`，否则托盘图标 + 设置页头像加载失败（但不崩，因 catch 静默）。
- ⚠️ **必须重跑 `create-launchers.ps1`**：旧桌面 `.lnk` 的 TargetPath 指向已删除的 `SelectionAssistant.App.exe`，不重跑快捷方式失效。本轮已重跑。

### 机器侧验证（全部通过）
- `dotnet build -c Debug`：0 警告 0 错误，App 输出 `BYH.dll`（不再是 SelectionAssistant.App.dll）。
- `dotnet test SelectionAssistant.slnx`：Core 137 + Providers 35 + Windows 41 = **213/213 全过**。
- `dotnet publish -c Release -r win-x64`：0 警告，`Generating native code` 通过，产物 **`BYH.exe` 26,927,616 bytes**（无 PDB）。
- 双路径同步：`cp bin/.../publish/BYH.exe artifacts/publish/win-x64-nativeuia/BYH.exe`，删除旧 `SelectionAssistant.App.exe`（避免两个 exe 共存）。
- `create-launchers.ps1` 重跑成功：桌面 `BYH.lnk` + 项目根 `BYH.cmd` 都指向新 `BYH.exe`。
- 启动验证（PID 49200）：
  - **ProcessName = `BYH`**（任务管理器看到的名字，不再是 SelectionAssistant.App）✅
  - **Path = `...\BYH.exe`**（文件资源管理器看到的名字）✅
  - 启动日志序列正确：`MouseHook installed → Runtime started → KeyboardHook installed → Persistent keyboard hook installed`，无 Clipboard timeout 崩溃，无 avares 加载失败。
- 单实例锁验证：再启一个 BYH.exe 立即 exit code=0 退出——Mutex 名没变所以单实例照旧生效。

### 用户真机待验证
1. 任务管理器（Ctrl+Shift+Esc → 详细信息 tab）里进程名应显示 `BYH.exe`（不是 SelectionAssistant.App.exe）
2. 文件资源管理器到 `artifacts\publish\win-x64-nativeuia\` 看到 `BYH.exe`
3. 桌面双击 `BYH.lnk` 能正常启动（旧快捷方式已重建）
4. 右键 `BYH.exe` → 属性 → 详细信息 → "产品名称" = `BYH`、"公司" = `By Your Hand`（之前这里是空的）
5. 托盘图标显示正常（avares://BYH/Assets/app-icon.ico 加载成功，图标不缺失）
6. 设置页右上人物欢迎卡头像显示正常（avares://BYH/Assets/app-icon.png 加载成功）
7. 托盘右键"重启 BYH"仍能自重启（RequestRestart 跟随 ProcessPath）
8.（深度工具，可选）Visual Studio 调试器/Spy++/Process Explorer 里线程名显示 `BYH.MouseHook` / `BYH.KeyboardHook` / `BYH.ClipboardMessages` / `BYH.UIAutomation` 等（不再是 SelectionAssistant.*）

### ⚠️ 关键教训（永久记录）
- **Avalonia `avares://` URI 用 `<AssemblyName>` 解析，不是 RootNamespace 也不是 csproj 文件名**。改 AssemblyName 必须同步改所有 `avares://<旧名>/*`。判断方法：搜 `avares://` 字面量，凡 URI 第一段 = 旧 AssemblyName 的都要改。本次只 2 处（都指向 App 项目的 Assets）；UI 项目的 7 处 avares 不受影响因为 UI 的 AssemblyName 没动。
- **改 AssemblyName 对以下东西无影响**：namespace、ProjectReference、x:Class、Mutex 名（字面量常量）、配置目录/日志名（字面量常量）、`Environment.ProcessPath`（动态读取）。所以这是"低风险高收益"的改名手段——只要同步改 avares + 启动器脚本。
- **exe 改名后必须重跑 `create-launchers.ps1`**：桌面 `.lnk` 的 TargetPath 是绝对路径硬编码，指向旧 exe 名；不重跑则快捷方式变成"目标不存在"的死链。
- **双 publish 路径要删旧 exe**：`bin/.../publish/` 和 `artifacts/publish/win-x64-nativeuia/` 是两份产物。改名后旧的 `SelectionAssistant.App.exe` 还留在 artifacts 目录，必须显式删除，否则两个 exe 共存（用户可能点到旧的）。
- **"品牌统一"任务先摸底再动手**：本次通过 sub-agent 扫描发现用户可见界面早已 100% BYH，真正要改的只有"技术表层"（exe 名/进程名/线程名/exe 属性）。不摸底直接全局替换 namespace 会破坏编译。判断"用户可见 vs 技术标识"的标准：**任务管理器 / 文件资源管理器 / 右键属性 / Spy++ / 调试器看得到 = 要改；只在编译器/运行时内部用 = 不改**。
- **错误消息里的 CLI 命令名要与实际 exe 名一致**：`OpenAiCompatibleStreamingProvider.cs:172` 的 `"BYH.exe --set-secret ..."` 在改名前是 bug（实际 exe 叫 SelectionAssistant.App.exe），改名后才自洽。以后错误消息里引用自身可执行文件，用 `Path.GetFileName(Environment.ProcessPath)` 动态取，不要硬编码。

---

## 3n. 本会话（第二十七批增量）完成的工作：R37 工具栏 R/C/V 快捷键 + byh 艺术字 wordmark

### 用户需求

> 给划词弹窗 prompt 和复制粘贴也添加快捷键 r，c，v，把已取词 accessibility 的文本改成艺术字版的 byh，参考图 [图片：手写笔触的 "By Your Hand" wordmark]

两件事：
1. 划词工具栏新增 R/C/V 三个快捷键（提示词/复制/粘贴），行为与点击对应按钮一致。
2. 工具栏状态区把 "已取词 · Accessibility" 这种诊断式英文混中文换成艺术字 "byh" wordmark。

### 摸底结论（sub-agent 扫描）

**工具栏键盘派发链**：工具栏本身（`ToolbarWindow.axaml.cs`）**零**键盘事件——因为窗口是 `WS_EX_NOACTIVATE` 永不获得焦点。所有按键拦截走 `LowLevelKeyboardHook`（WH_KEYBOARD_LL）→ `SelectionRuntime.OnToolbarKeyPressed(vkCode)`（`SelectionRuntime.cs:1160`）。现有逻辑只查 `_promptTemplates.FindByShortcut(key)`——而 PromptTemplate 只有翻译/总结/解释三个内建 + 用户自定义，**复制/提示/粘贴不在 PromptTemplate 体系内**（它们是工具栏自身的按钮 + 独立事件 `PromptRequested`/`PasteRequested` + OnCopyClick 直写剪贴板）。

**状态文本现状**：`StatusText`（`ToolbarWindow.axaml:72`）是工具栏唯一的文本显示元素，code-behind 里三处 set：`ShowPending`（取词中 · x,y）/`SetCaptureResult`（已取词 · {Source} 或 需要手动复制 / 暂未取到文本）/`SetDiagnosticStatus`（任意诊断）。`{Source}` 是 `CaptureSource.ToString()` 直接插值——产出 "已取词 · Accessibility" 这种英文混中文的奇怪文本。

### 改动清单

| # | 文件 | 改动 |
|---|------|------|
| 1 | `src/SelectionAssistant.UI/Themes/IvoryJade.axaml` | 新增 `TextBlock.WordmarkArt` 样式：`FontFamily=Georgia`（ByhDisplayFontFamily）+ `FontStyle=Italic` + `FontWeight=Bold` + `FontSize=13` + `LetterSpacing=-0.4`（负字距挤紧模拟手写连笔）+ `Foreground=ByhAccentBrush`（玉色 #899845）。小尺寸下逼近参考图的手写笔触品牌字。 |
| 2 | `src/SelectionAssistant.UI/Views/ToolbarWindow.axaml` | PromptButton/CopyButton/PasteButton 各加 `ToolTip.Tip="提示词（快捷键 R）"` 等；状态区 Grid（Column 6）从单 TextBlock 改成双层 Grid：`StatusText`（保留所有非成功态）+ 新增 `WordmarkText`（Text="byh"、Classes="WordmarkArt"、右对齐），IsVisible 由 code-behind 互斥切换。 |
| 3 | `src/SelectionAssistant.UI/Views/ToolbarWindow.axaml.cs` | `ShowPending`/`SetCaptureResult`/`SetDiagnosticStatus` 三处加 `WordmarkText.IsVisible` 与 `StatusText.IsVisible` 互斥切换：取词成功 → 显示 byh 隐藏诊断文字；其它态 → 显示诊断文字隐藏 byh。新增 3 个 public 方法 `InvokePromptShortcut()`/`InvokeCopyShortcut()`/`InvokePasteShortcut()`——各自调既有 OnPromptClick/OnCopyClick/OnPasteClick（行为与点击按钮完全一致），返回 bool 表示是否真的触发（R/C 需 captured text；V 恒触发）。 |
| 4 | `src/SelectionAssistant.App/SelectionRuntime.cs` | `OnToolbarKeyPressed`：用户模板查不到时不再直接 `return false`，改为调新方法 `TryInvokeBuiltinToolbarShortcut(key)`。新方法判断 R/C/V：V → InvokePasteShortcut（恒吞）；C → InvokeCopyShortcut（没取词则不吞，让 C 透传源程序）；R → InvokePromptShortcut（同 C）；其它字母 → 仍 `return false`。**关键差异**：与 F/J/Z 不同，这三个动作不隐藏工具栏（复制完可能继续翻译），所以不调 `_keyboardHook.SetEnabled(false)`。 |

### 设计决策

- **R/C/V 与 F/J/Z 的吞键/隐藏工具栏语义必须分开**：F/J/Z 是"终态动作"（翻译/总结/解释一旦触发就开结果窗口，工具栏隐藏，hook disable）；R/C/V 是"中间动作"（复制/提示/粘贴之后工具栏仍在，用户可能继续做别的）。所以 R/C/V 分支**不调** `SetEnabled(false)`，工具栏保持可见。这是关键正确性点。
- **V 恒触发，R/C 需 captured text**：粘贴作用在剪贴板 + 源程序，与 captured text 无关（按钮本身也永远 enabled）；复制/提示需要选中文本（按钮 disabled 直到取词成功）。所以 V 不管 captured text 直接吞键；R/C 在 captured text 为空时 `return false`（让按键透传源程序，不吞用户正常打字）。这与按钮 enabled 状态完全一致。
- **R/C/V 是"用户未绑定该键时的兜底"**：如果用户在设置里把某个 PromptTemplate 的 Shortcut 设成 C，则用户的 C 优先（`FindByShortcut` 命中），走翻译/总结/解释路径；用户没绑定 C 时才走复制。这避免了快捷键冲突——用户配置永远赢。
- **艺术字 wordmark 用 CSS 属性而非图片**：参考图是手写笔触的 "By Your Hand" 全词，工具栏只有 ~80px 宽，放全词放不下且字体不同会很丑。改成只显示 "byh" 三个小写字母，用斜体 Georgia Bold + 负字距 + 玉色，在小尺寸下视觉上像流畅的手写签名。可缩放、可主题化、零额外资源文件。如果以后想要更接近参考图的笔触，可以改成 SVG `<Path>`（XAML 几何路径），但那是 P2 美化任务，当前先达成"不再显示英文 Accessibility、显示品牌字"的核心目标。
- **状态区用 Grid 叠层而非交换 Text**：原方案是在同一个 TextBlock 里切 Text（"已取词 · X" ↔ "byh"），但 WordmarkArt 样式（斜体 Bold 玉色 13px）与 Secondary 样式（常规 棕色 10px）差别太大，同一个 TextBlock 切 Class 会闪烁/抖动。改成 Grid 里叠两个 TextBlock + IsVisible 互斥，Avalonia 只布局可见的那个，干净无抖动。

### 不改的（确认正确保持）

- `PromptTemplate.Shortcut` 体系（F/J/Z + 用户自定义）完全不动——R/C/V 是工具栏内建快捷键，独立于 PromptTemplate。
- `OnToolbarKeyPressed` 的 Esc 分支、A-Z 用户模板分支都不动——R/C/V 只在用户模板 `template is null` 之后才查。
- `OnCopyClick` 的 async void + clipboard 写入逻辑不动——`InvokeCopyShortcut` 直接调它。
- `OnPasteClick` → `PasteRequested` → `OnPasteRequested`（SendInputHelper.SendPasteChord）链路不动——`InvokePasteShortcut` 直接调 OnPasteClick。
- `ShowPending` 的初始 "取词中 · x,y" 文本不动（取词进行中还没"已取词"，不该显示 byh）。
- `SetDiagnosticStatus` 的诊断文本不动（错误状态不该显示 byh，会误导用户）。

### 验证

1. `dotnet build -c Debug`：0 警告 0 错误，App 输出 `BYH.dll`。
2. `dotnet test`：213/213 通过（Core 137 + Providers 35 + Windows 41）。
3. `dotnet publish -c Release -r win-x64`：0 警告，`Generating native code` 成功，`BYH.exe` 26,931,712 bytes（无 PDB）。
4. 同步双路径：`cp bin/.../publish/BYH.exe artifacts/publish/win-x64-nativeuia/BYH.exe`。
5. 杀旧进程 PID 45356，启动新 `BYH.exe`，PID 31996，ProcessName = `BYH`。
6. 启动日志序列正确：`MouseHook installed → Runtime started → KeyboardHook installed → Persistent keyboard hook installed`。

### 用户验证清单

1. 选中文字 → 工具栏弹出 → 状态区显示艺术字 "byh"（斜体玉色），不再是 "已取词 · Accessibility"。
2. 工具栏可见时按 **R** → 打开提示词窗口（与点 Prompt 按钮一致）。
3. 工具栏可见时按 **C** → 复制选中文本到剪贴板（与点复制按钮一致），工具栏不消失。
4. 工具栏可见时按 **V** → 在源程序粘贴剪贴板内容（与点粘贴按钮一致），工具栏不消失。
5. 没取词时按 C/R → 按键透传源程序（不吞键、不弹任何东西）。
6. 在设置里把某个自定义功能快捷键设成 C → 按时触发该自定义功能（用户配置优先于内建复制）。
7. 工具栏可见时按 Esc → 关闭工具栏（不变）。
8. 取词失败（"暂未取到文本" / "需要手动复制"）→ 状态区显示诊断文字（不显示 byh，因为没成功）。

### ⚠️ 关键教训（永久记录）

- **"工具栏内建快捷键"与"PromptTemplate 快捷键"是两套独立体系**：F/J/Z 走 PromptTemplate（终态动作：触发后隐藏工具栏 + disable hook）；R/C/V 走工具栏内建（中间动作：触发后工具栏保持可见，hook 保持 enabled）。混用会导致复制后工具栏消失（用户没机会继续翻译）。判断标准：**动作完成后用户还会继续操作工具栏吗？是 → 不隐藏；否 → 隐藏**。
- **快捷键吞键的"用户配置优先"原则**：内建快捷键（R/C/V）必须是 fallback，永远让位于用户在 PromptTemplate 里配置的同键。实现方式：先查 `FindByShortcut`，命中走用户配置；查不到才查内建 R/C/V。这样用户不会被内建快捷键绑架。
- **快捷键吞键的"按钮 enabled 一致性"原则**：快捷键触发条件必须与对应按钮的 IsEnabled 完全一致。复制按钮 `IsEnabled = captured text is not null`，所以 C 快捷键在没取词时也不吞键（让按键透传）。粘贴按钮永远 IsEnabled，所以 V 恒吞键。不一致会出现"按钮灰着但快捷键能触发"或"按钮亮着但快捷键无效"的诡异行为。
- **Avalonia 同区域不同样式文本切换用 Grid 叠层 + IsVisible，不要切同一个 TextBlock 的 Class/Text**：同一个 TextBlock 在 WordmarkArt（斜体 13px 玉色）和 Secondary（常规 10px 棕色）之间切会有可见的字体/字号/颜色抖动，而且 Class 切换是动画化的（即使没显式动画也有过渡）。两个 TextBlock 叠在 Grid 里 + IsVisible 互斥是最干净的方案——Avalonia 只布局可见的那个，另一个完全不参与渲染。
- **手写笔触字体的"小尺寸可用"判断**：参考图是装饰性大尺寸手写体，直接缩到 13px 会糊成一团。妥协方案：用斜体 Georgia Bold + 负字距（LetterSpacing=-0.4）+ 玉色，小尺寸下保留"手写签名感"而不追求"逐笔还原"。如果非要还原参考图，正解是 SVG `<Path>`（XAML 几何路径）——但那要设计师出矢量，工程实现成本高，且 NativeAOT 下 Path 渲染性能要测。当前方案是工程性价比最高的近似。

### R37 修复批：C 崩溃 + R 没反应 + R/C/V 不可配置

**用户报告**：R37 初版（上一节）发出去后，按 R 键没反应，按 C 键软件直接崩溃；另外要求 R/C/V 可在设置里改。

#### 根因（崩溃 + R 没反应，同一根因）

`OnToolbarKeyPressed` 跑在 keyboard hook 的**后台线程**（`WH_KEYBOARD_LL` 回调线程，不是 UI 线程）。R37 初版直接调 `_toolbarWindow.InvokeCopyShortcut()` / `InvokePromptShortcut()`：

- **C 崩**：`InvokeCopyShortcut` → `OnCopyClick` → `await clipboard.SetTextAsync(text)`，Avalonia clipboard API **必须在 UI 线程调**，后台线程调直接崩进程。
- **R 没反应**：`InvokePromptShortcut` → `OnPromptClick` → `PromptRequested?.Invoke` → `OnPromptRequested` → `_promptWindow?.ShowForSelection(...)`，显示窗口也要 UI 线程，后台线程调静默无效（Avalonia 的 thread-affinity 检查在某些路径上不抛异常但什么都不做）。
- **V 为什么不崩**：`OnPasteRequested` → `SendInputHelper.SendPasteChord()`，是 Win32 `SendInput` API，**线程安全**，所以 V 一直正常。

而 `RunActionAsync`（F/J/Z 走的路径）从后台线程调却不崩，是因为它只做 `_windowHost.Hide()`（Win32 `ShowWindow`，线程安全）+ `StopKeyboardHookQuiet()`（volatile 写）+ `TrackSessionTask(_translationManager.StartOrReplaceAsync(request))`（fire-and-forget），真正碰 UI 的翻译管线**内部自己用 `_dispatcher.InvokeAsync` marshaling**。

#### 修复（崩溃）

`TryInvokeBuiltinToolbarShortcut` 重写，遵循 codebase 标准模式（`App.axaml.cs:157` chord/hotkey → UI 派发都是这么做的）：

1. **吞键判断在 hook 线程同步完成**：读 `_sessionManager.GetLastCapturedText()`（lock 保护，线程安全）判断 R/C 是否有文本可操作；V 恒吞。判断完就决定 return true/false，不等 UI。
2. **实际 UI 操作用 `Dispatcher.UIThread.Post(...)` fire-and-forget**：把 `_toolbarWindow.InvokeXxxShortcut()` 包进闭包派发到 UI 线程。不阻塞 hook 线程，不用 `InvokeAsync(...).GetAwaiter().GetResult()`（死锁风险——UI 线程可能在等 hook 线程）。
3. 外层 try/catch 防 dispatcher shutdown race（Post 本身基本不抛，但加保险不让 hook 线程崩掉整个键盘钩子链）。

#### 修复（可配置）

新增 settings 体系，完全平行于 `QuickToolsTriggerSettings`：

| 新文件 | 作用 |
|--------|------|
| `src/SelectionAssistant.Core/Input/ToolbarShortcutSettings.cs` | sealed record，3 个 `string?`（PromptKey/CopyKey/PasteKey，默认 R/C/V，null=禁用）。`Normalize()` 全转大写。`Validate()` 校验 A-Z 单字符 + 三键互斥（不能重复）。 |
| `src/SelectionAssistant.Infrastructure/Configuration/ToolbarShortcutsStore.cs` | 静态类，`schemaVersion=1`，`LoadIfExists` + `Save`，镜像 `QuickToolsTriggerStore`（atomic write、8KB 上限、字段缺失走默认——老用户无 `toolbar-shortcuts.json` 透明升级到 R/C/V）。 |

编辑的文件：

| 文件 | 改动 |
|------|------|
| `ByhApplicationPaths.cs` | 新增 `ToolbarShortcutsFile` → `toolbar-shortcuts.json` |
| `SettingsWindow.axaml` | GeneralSection 的 QuickTools 卡片后插入第二张 PearlCard「工具栏快捷键」：3 行（提示词/复制/粘贴）×（label + `MaxLength=1` TextBox + hint）+ 保存按钮 + 状态 TextBlock |
| `SettingsWindow.axaml.cs` | 新增 `SetToolbarShortcuts`（push 到 UI）、`OnSaveToolbarShortcutsClick`（读 → 校验 → raise event）、`ToolbarShortcutsSaved` event、`NormalizeInput`/`DisplayKey` helper |
| `SelectionRuntime.cs` | 新增 `_toolbarShortcuts` 字段（默认 Default）+ `SetToolbarShortcuts` 方法；`TryInvokeBuiltinToolbarShortcut` 从硬编码 R/C/V 改为读配置（三键可独立改/禁用） |
| `App.axaml.cs` | 启动加载 `_toolbarShortcuts`（带 ProviderConfigurationException fallback）；`_runtime` 构造后 `SetToolbarShortcuts`；订阅 `ToolbarShortcutsSaved += OnToolbarShortcutsSaved`（handler: store + 推 runtime + 刷 UI）；`RefreshSettingsAsync` 加 `SetToolbarShortcuts` |

#### 关键设计决策

- **吞键判断必须同步、派发必须异步**：后台线程不能同步等 UI 线程（死锁），所以"吞不吞键"的判断在 hook 线程用线程安全的 `GetLastCapturedText()` 完成，决定后用 `Dispatcher.UIThread.Post` fire-and-forget 实际执行。这跟 `RunActionAsync` 的模式一致（同步判断 + 异步管线）。
- **settings 字段允许空值（null/空串=禁用）**：用户可以在设置里清空某键来关掉那个快捷键。Validate 跳过空的，只校验非空的是 A-Z。runtime 匹配时也跳过空键。这让 R/C/V 可以独立禁用。
- **三键互斥校验**：Validate 检查三键互不相同（非空时）。否则一个键同时绑 Prompt 和 Copy 行为歧义。**注意**：用户配置的 PromptTemplate 快捷键（F/J/Z + 自定义）可以和 R/C/V 重叠——`FindByShortcut` 优先，用户配置永远赢——但 R/C/V 之间不能重叠。
- **settings 放 General 页不放 Functions 页**：R/C/V 是工具栏内建动作（复制/提示/粘贴），不是 PromptTemplate（翻译/总结/解释）。Functions 页管 PromptTemplate。General 页的 QuickTools 卡片旁边是"输入方式"配置区，最自然。
- **不引入 schemaVersion=2**：新字段对老用户透明（缺字段走默认 R/C/V），保持 v1。这跟 `thinkingEnabled`/`shortcut` 加到 `prompt-templates.json` 的做法一致。
- **字段写 JSON null 而非省略**：`ToolbarShortcutsStore.WriteOptionalString` 在 key 为 null 时写 `WriteNull`，这样 round-trip 保留"用户清空了"的状态，不会 load 时又 snap 回默认。

#### 验证

1. `dotnet build -c Debug`：0 警告 0 错误。
2. `dotnet test`：213/213 通过（Core 137 + Providers 35 + Windows 41）。
3. `dotnet publish -c Release -r win-x64`：0 警告，`BYH.exe` 26,951,168 bytes（无 PDB）。
4. 同步双路径 + 杀旧进程 + 启动新 `BYH.exe` PID 52044。
5. 启动日志序列正确：`MouseHook installed → Runtime started → KeyboardHook installed → Persistent keyboard hook installed`。

#### 用户验证清单（需用户实测）

1. 选中文字 → 工具栏弹出 → 状态区显示艺术字 "byh"（斜体玉色）。
2. 按 **C** → 复制（**不崩**，剪贴板有选中文本），工具栏不消失。
3. 按 **R** → 打开提示词窗口（**有反应**），工具栏不消失。
4. 按 **V** → 源程序粘贴（不变，本来就正常）。
5. 没取词时按 C/R → 按键透传源程序（不吞键）。
6. 设置 → 常规 → 往下滚到「工具栏快捷键」卡片 → 三个输入框显示 R/C/V。
7. 把复制键改成 X → 保存 → 状态显示「已保存：提示词 R · 复制 X · 粘贴 V」→ 选中文字 → 按 X 复制、按 C 透传源程序。
8. 把某键清空 → 保存 → 该键不再触发工具栏动作（透传源程序），状态显示「未绑定」。
9. 填重复键（如三个都填 C）→ 点保存 → 显示「工具栏快捷键不能重复：'C' 被绑定到多个动作」、不保存。
10. 老用户（无 `toolbar-shortcuts.json`）→ 启动走默认 R/C/V，正常工作。

#### ⚠️ 关键教训（永久记录，R37 修复批新增）

- **从后台 hook 线程调 Avalonia UI API 会崩或静默无效**：`OnToolbarKeyPressed` 跑在 `WH_KEYBOARD_LL` hook 线程，不是 UI 线程。任何 UI API（clipboard、显示窗口、改控件属性）必须 `Dispatcher.UIThread.Post(...)` 派发。判断标准：**这个 API 有 thread affinity 吗？Avalonia 的 DependencyObject/visual 系、clipboard、窗口管理都有——一律 Post**。V 不崩是因为 `SendInput` 是纯 Win32 线程安全 API。判断新 API 能否后台调：看它是托管的 Avalonia API（不能）还是 P/Invoke 的 Win32（一般能）。
- **吞键判断必须同步、派发必须异步（fire-and-forget）**：hook 线程不能同步等 UI 线程——`Dispatcher.UIThread.InvokeAsync(...).GetAwaiter().GetResult()` 在 UI 线程可能在等 hook 线程时会死锁。正解：用线程安全的只读访问（`GetLastCapturedText` 有 lock 保护）在 hook 线程同步决定吞不吞键，然后用 `Post`（不等返回）派发实际 UI 动作。`RunActionAsync` 就是这个模式（同步判断 + fire-and-forget 异步管线）。
- **新 settings 字段用 schemaVersion=1 + 字段缺失走默认，不要 bump 版本**：加 3 个新字段到全新 settings 文件时，老用户没有这个文件 → `LoadIfExists` 返回 `Default`；有文件但缺字段 → `ReadOptionalString` 走默认。两种情况都透明升级，不丢配置。这跟 `prompt-templates.json` 加 `thinkingEnabled`/`shortcut` 的做法一致。只有**破坏性 schema 变更**才 bump 版本（且会让所有老用户配置失效，慎用）。
- **可选字段写 JSON null 而非省略，保留"用户主动清空"语义**：`WriteOptionalString` 在 null 时 `WriteNull` 而非跳过。否则 round-trip 会把"用户清空 = 禁用"错读成"字段缺失 = 走默认"，用户清空的设置重启后又 snap 回默认。判断标准：**默认值是 null/空 = 可以省略；默认值非空但用户可能清成空 = 必须写 null**。本例 PromptKey 默认 "R"，用户清空成 null——必须写 null 才能保留。
- **三键配置必须做互斥校验**：R/C/V（或任何"多键绑定多动作"的 settings）Validate 里必须检查键互不相同（只比非空键）。否则一个键同时绑两个动作，runtime dispatch 歧义（会命中第一个匹配分支，另一个永远触发不了，且用户不知道为什么）。**注意**：跨体系（R/C/V vs PromptTemplate F/J/Z）的重叠不用校验——`FindByShortcut` 优先级保证用户配置永远赢，重叠时内建那键被遮蔽是预期行为。

---

## 3o. 本会话（第二十八批增量）完成的工作：R38 全窗口 Esc 关闭统一

### 用户需求

> 给所有窗口都添加 ESC 退出功能。

### 摸底结论（sub-agent 扫描）

App 共 **10 个 Window 类**，全部直接继承 `Avalonia.Controls.Window`，**没有共享基类**。扫描 Esc 现状：

**已有 Esc 的 5 个（不改）**：
| 窗口 | 机制 | 动作 |
|------|------|------|
| ToolbarWindow | Win32 `WH_KEYBOARD_LL` hook（不是 Avalonia KeyDown，因为窗口 `WS_EX_NOACTIVATE` 无焦点） | `Hide()` + disable hook |
| ResultWindow | AXAML `<Window KeyDown="OnWindowKeyDown">` | `Hide()` + raise `CloseRequested` |
| SpotlightWindow | AXAML `<Window KeyDown="OnWindowKeyDown">` | `Hide()`（同 handler 还处理 ↑/↓/Enter 导航） |
| RegionSelectOverlay | AXAML `KeyDown="OnRootKeyDown"` | `Cancel()`（stop UIA tracking + `Hide()` + raise `RegionCancelled`） |
| ParameterInputDialog | AXAML `KeyDown` on input TextBox | `Close()`（唯一用 Close 的——每次 `new` 创建） |

**没 Esc 的 5 个（本轮要改）**：
| 窗口 | 生命周期 | 正确动作 |
|------|----------|----------|
| QuickToolsWindow | 一次创建 + Show/Hide 复用（`_allowClose`+`Closing` 守卫） | `Hide()` |
| SettingsWindow | 同上 | `Hide()` |
| PromptWindow | 同上 | `Hide()` |
| PromptTemplateEditWindow | 每次 `new` 创建的模态弹窗 | `Close()` |
| LauncherEntryEditWindow | 同上 | `Close()` |

### 改动清单

每个窗口两处改动：AXAML `<Window>` 标签加 `KeyDown="OnWindowKeyDown"`，code-behind 加 `OnWindowKeyDown` 方法 + 必要时加 `using Avalonia.Input;`（`Key`/`KeyEventArgs` 在这个命名空间）。

| # | 文件 | 改动 |
|---|------|------|
| 1 | `QuickToolsWindow.axaml(.cs)` | `<Window>` 加 `KeyDown="OnWindowKeyDown"`；cs 在 `PrepareForShutdown` 后加 `OnWindowKeyDown` → `Hide()`（已有 `using Avalonia.Input`，无需加） |
| 2 | `SettingsWindow.axaml(.cs)` | 同上；cs 在 `OnHideClick` 后加 `OnWindowKeyDown` → `Hide()`；加 `using Avalonia.Input;` |
| 3 | `PromptWindow.axaml(.cs)` | 同上；cs 在 `OnCancelClick` 后加 `OnWindowKeyDown` → `Hide()`；加 `using Avalonia.Input;` |
| 4 | `PromptTemplateEditWindow.axaml(.cs)` | 同上；cs 在 `OnCancelClick` 后加 `OnWindowKeyDown` → `Close()`；加 `using Avalonia.Input;` |
| 5 | `LauncherEntryEditWindow.axaml(.cs)` | 同上；cs 在 `OnCancelClick` 后加 `OnWindowKeyDown` → `Close()`；加 `using Avalonia.Input;` |

### 设计决策

- **完全镜像 ResultWindow/SpotlightWindow 的现有模式**：`<Window KeyDown="OnWindowKeyDown">` + code-behind `if (e.Key == Key.Escape) { e.Handled = true; Hide()/Close(); }`。不引入基类（10 个窗口里只有 5 个要改，且每个就 3 行，基类抽象不划算还会牵动另外 5 个已工作的窗口）。
- **复用窗口用 `Hide()`，模态弹窗用 `Close()`**：判断标准是窗口生命周期——一次创建 + Show/Hide 复用的（有 `_allowClose`+`Closing` 守卫，`Close` 被 cancel 转 `Hide`）用 `Hide()`；每次 `new` 的用 `Close()` 释放资源。用错了：复用窗口 `Close()` 会触发 Closing 守卫转 Hide（实际还是 Hide，但语义错），模态弹窗 `Hide()` 会留着不释放（下次又 `new` 一个，泄漏）。
- **模态弹窗的 Esc 直接 `Close()`，不调 `OnCancelClick(null, null)`**：虽然 OnCancelClick 也只是 `=> Close()`，但直接写 `Close()` 更直白、少一层间接。注释里说明了"reuses OnCancelClick's close path"的语义等价性。如果将来 OnCancelClick 加了副作用（比如清空状态），再改成调它。
- **Settings Esc 安全性**：每个 setting 都有独立的「保存」按钮（不是 on-change 自动保存），所以 Esc 隐藏只是丢弃未保存的编辑，下次显示从 runtime 重新 push——和点「隐藏」按钮完全等价，无风险。
- **不碰已有 Esc 的 5 个窗口**：ToolbarWindow 的 Esc 在 Win32 hook 里（Avalonia KeyDown 永远不触发，因为无焦点）；ResultWindow/SpotlightWindow/RegionSelectOverlay/ParameterInputDialog 都已正确工作。改它们 = 制造回归风险，零收益。

### 不改的（确认正确保持）

- `ToolbarWindow` 的 Esc——必须在 Win32 keyboard hook 里（Avalonia 收不到）
- 已有 Esc 的 4 个 Avalonia 窗口的现有 handler
- `_allowClose`/`Closing`/`PrepareForShutdown` 复用机制（不改）
- 所有窗口的其它键盘行为（SpotlightWindow 的 ↑/↓/Enter 导航、ResultWindow 的复制快捷键等）

### 验证

1. `dotnet build -c Debug`：0 警告 0 错误。
2. `dotnet test`：213/213 通过（Core 137 + Providers 35 + Windows 41）。
3. `dotnet publish -c Release -r win-x64`：0 警告，`BYH.exe` 26,953,216 bytes（无 PDB）。
4. 同步双路径 + 杀旧进程 + 启动新 `BYH.exe` PID 27828。
5. 启动日志序列正确：`MouseHook installed → Runtime started → KeyboardHook installed → Persistent keyboard hook installed`。

### 用户验证清单

10 个窗口全验证（已有 5 个 + 新增 5 个）：
1. 选中文字 → 工具栏 → **Esc** 关闭工具栏（已有，Win32 hook 路径）
2. 翻译结果窗口 → **Esc** 关闭（已有）
3. Spotlight（Ctrl+Alt+Space）→ **Esc** 关闭（已有）
4. 画框 OCR 覆盖层 → **Esc** 取消（已有）
5. 参数输入弹窗（`{prompt:...}` 触发）→ **Esc** 取消（已有）
6. **QuickTools（Ctrl+Alt+Q）→ Esc 关闭（新增）**
7. **设置窗口 → Esc 关闭（新增）**
8. **Prompt Now 窗口 → Esc 关闭（新增）**
9. **编辑提示词弹窗 → Esc 取消（新增）**
10. **编辑启动项弹窗 → Esc 取消（新增）**

### ⚠️ 关键教训（永久记录，R38 新增）

- **"给所有窗口加 X"任务先摸底再动手**：本轮通过 sub-agent 扫描发现 10 个窗口里 5 个**已经有 Esc**了。不摸底直接全改 = 制造 5 个回归（改坏已工作的 handler，比如 ToolbarWindow 的 Esc 在 Win32 hook 里，加 Avalonia KeyDown 不仅没用还会让人误以为修了）。判断标准：**全局类任务（"所有窗口"、"所有按钮"、"所有菜单"）必须先穷举 + 分类现状，再决定改哪些**。
- **Esc 关闭用 `Hide()` 还是 `Close()` 取决于窗口生命周期**：一次创建 + Show/Hide 复用的窗口（有 `_allowClose`+`Closing` 守卫）用 `Hide()`——`Close()` 会被守卫拦截转 `Hide()`，语义错；每次 `new` 的模态弹窗用 `Close()`——`Hide()` 不释放，下次又 `new`，内存泄漏。判断标准：**这个窗口在 App 生命周期里 `new` 几次？一次 = 复用 = Hide；每次显示都 new = Close**。
- **没有共享基类时，"镜像现有窗口的模式"优于"抽基类"**：当只有少数（5/10）窗口要改、每个改动只有 3 行、且现有窗口已有清晰范式时，复制范式比抽基类更省事且风险更低。基类抽象会牵动另外 5 个已工作窗口的继承链，引入回归风险。判断标准：**改动行数 × 受益窗口数 < 抽基类的改动行数 × 总窗口数 → 复制；否则抽基类**。
- **`Key`/`KeyEventArgs` 在 `Avalonia.Input` 命名空间**：加 KeyDown handler 时，code-behind 必须 `using Avalonia.Input;`。这个命名空间不是 Avalonia 控件项目默认就 import 的（`Avalonia.Controls` 才是默认）。漏了会编译错误"找不到 Key 类型"。
- **WS_EX_NOACTIVATE 窗口的键盘事件 Avalonia 收不到**：`ToolbarWindow` 因为 `ShowActivated="False"` + `WS_EX_NOACTIVATE`（不抢焦点），Avalonia 的 `KeyDown` 事件**永远不会触发**。它的 Esc 必须走 Win32 `WH_KEYBOARD_LL` 低级键盘 hook（在 SelectionRuntime 里）。给这类窗口加 Avalonia KeyDown = 完全无效。判断标准：**窗口会获得键盘焦点吗？会 → Avalonia KeyDown；不会（noactivate/transparent/overlay）→ 必须 Win32 hook**。

### R38 修复批：工具栏 Prompt 按钮不隐藏工具栏

**用户报告**：点翻译/总结/解释按钮后工具栏消失、进入对应窗口；但**点 Prompt 按钮后工具栏不消失，和 Prompt 窗口同时存在**。

**根因**：`App.OnPromptRequested`（工具栏 Prompt 按钮的 handler）只调 `_promptWindow?.ShowForSelection(selectedText)`，**没隐藏工具栏、没 disable 键盘 hook**。对比翻译/总结/解释走 `RunActionAsync`（内部 `_windowHost.Hide()` + `StopKeyboardHookQuiet()`），Prompt 窗口点「运行」走 `RunPromptAsync`（也 hide + disable hook）——唯独"点工具栏 Prompt 按钮"这条路径漏了这两步。是个从 R2（Prompt 功能初引入）就存在的老 bug，R38 加 R 快捷键（同样走 PromptRequested）让它更明显。

**修复**：
- `SelectionRuntime` 加 public 方法 `HideToolbarAndDisableHook()`——就是 `RunActionAsync`/`RunPromptAsync` 的前两步（`_windowHost.Hide()` + `StopKeyboardHookQuiet()`），不启动翻译管线（用户还要在 Prompt 窗口输提示词）。
- `OnPromptRequested` 开 Prompt 窗口前先调 `_runtime?.HideToolbarAndDisableHook()`。

### ⚠️ 关键教训（永久记录，R38 修复批新增）

- **"开新窗口"路径要显式隐藏来源窗口**：当一个动作的语义是"从工具栏切到另一个独立窗口"（如 Prompt 输入窗），必须显式隐藏工具栏 + disable hook。翻译/总结/解释因为走 `RunActionAsync` 自动隐藏，所以没问题；但 Prompt 走的是独立的 `OnPromptRequested` handler（不经过 RunActionAsync），就漏了。判断标准：**这条路径有没有经过会自动隐藏工具栏的 RunActionAsync/RunPromptAsync？没有 → 必须手动 hide + disable hook**。
- **同一功能的多个入口要保证副作用一致**：触发 Prompt 有两个入口——工具栏 Prompt 按钮（`OnPromptRequested`）和 R 快捷键（R37 加的，也走 `InvokePromptShortcut` → `PromptRequested`）。两个入口共享同一个 event，所以修一处 `OnPromptRequested` 同时修好两个入口。但如果 R 快捷键是独立 handler（不共享 event），就会只修好按钮、漏掉快捷键。判断标准：**新加入口优先复用现有 event/handler，而不是另起一条并行路径**——一处修复覆盖所有入口。

---

## 3p. 本会话（第二十九批增量）完成的工作：R39 工具栏 byh wordmark 升级为真实透明底 PNG

### 用户请求（逐字）

> "你再尝试一下把 C:\Users\DeRant Vilmon Ram\Pictures\2fb137b8f7_image_f8a92f2d_00001_.png 放到划词弹窗的右边, 这次我做了透明背景的"

R37 时用户就想要把"已取词 · Accessibility"诊断文本替换成艺术字 byh wordmark，但当时我（基于"少改 UI、可即时编译"思路）用 `Italic Georgia Bold + 负字距 + 玉色` 的 TextBlock 文字版做了近似，绕过了用户提供的第一版参考图。用户现在提供第二版参考图（这次有真正的 alpha 通道），明确要求直接放图。本轮把 R37 的文字近似替换成真实手写体透明底 PNG。

### 改动

| # | 文件 | 改动 |
|---|------|------|
| 1 | `src/SelectionAssistant.UI/Assets/Theme/byh-wordmark.png` **(新)** | 把用户的 1376×768 透明底原图（1.7MB）裁剪到内容 bbox（1311×450）+ 缩到高 36 px（103×36，4.8KB），保 32bpp ARGB。裁剪是因为原图四周透明留白多（bbox 偏右下），不裁剪就放在工具栏里会被透明留白推偏；缩小是因为工具栏行高 ~28px，原图太大运行时要缩放开销 + 体积爆炸（1.7MB 进 avares）。 |
| 2 | `src/SelectionAssistant.UI/SelectionAssistant.UI.csproj` | 原 `<AvaloniaResource Include="Assets\Theme\*.jpg" />` glob 只收 JPG，PNG 没被打包。新增一行 `<AvaloniaResource Include="Assets\Theme\byh-wordmark.png" />`（单独显式引用而不是改 glob 为 `*.jpg;*.png`，避免把同名 jpg+png 的 ivory-jade-emblem/ornament 两套都打进 avares——它们是同图的两种格式，重复打包浪费体积）。 |
| 3 | `src/SelectionAssistant.UI/Views/ToolbarWindow.axaml` | 状态区第二层从 `<TextBlock x:Name="WordmarkText" Classes="WordmarkArt" Text="byh">` 换成 `<Image x:Name="WordmarkImage" Source="avares://SelectionAssistant.UI/Assets/Theme/byh-wordmark.png" Height="28" Stretch="Uniform" HorizontalAlignment="Right" VerticalAlignment="Center">`。`Height=28` ≈ 工具栏行高，`Stretch=Uniform` 保持比例，右对齐贴合工具栏右沿。`MinWidth=80` 保留不变（图片只占 ~80px 宽，撑不满 MinWidth 的话仍是右对齐，多余空间留在 wordmark 左边作为视觉缓冲）。 |
| 4 | `src/SelectionAssistant.UI/Views/ToolbarWindow.axaml.cs` | 3 处 `WordmarkText.IsVisible = ...` 改为 `WordmarkImage.IsVisible = ...`（ShowPending / SetCaptureResult / SetDiagnosticStatus）。互斥切换逻辑不变：取词成功 → 显 Image 隐 StatusText；其它状态 → 显 StatusText 隐 Image。 |
| 5 | `src/SelectionAssistant.UI/Themes/IvoryJade.axaml` | 删除 `<Style Selector="TextBlock.WordmarkArt">`（R37 留下的死样式——R39 后无人引用，R36 起项目强制的 0 警告/无死代码目标要求清掉）。 |

### 关键设计决策

- **用 Image 不用 Path/Draw**：用户的图是手写体 calligraphy（变粗细、有"墨水飞溅"边缘、非规则笔画），SVG `<Path>` 几何上还原不了那个笔触。直接用 PNG 保真最高。
- **裁剪到 bbox**：System.Drawing 扫整张 1376×768 找 `A>16` 的非透明像素 bbox（61,157 → 1311×450），加 6px margin，再缩放。如果不裁剪，103×36 的小图里 wordmark 本体只占 ~60% 宽度，看起来偏小且不居中。
- **缩到 Height=36**：工具栏行高实测约 28px（翻译/复制按钮 `Padding="8,3"` + `FontSize="11"`），36px 的源图在 Image 控件里被 `Stretch=Uniform + Height=28` 二次缩到 28×~80，留 2px 上下余量。源图比目标稍大让二次缩放走"下采样"路线（高频细节被裁掉，边缘更干净），反之"上采样"会让小源图放大后糊。
- **PNG 文件名带前缀 `byh-`**：放 `Assets/Theme/byh-wordmark.png` 而不是 `Assets/wordmark.png`——`Theme/` 子目录是 ivory-jade 主题资源的既定位置，加 `byh-` 前缀让资源用途自解释。
- **删 R37 的 `TextBlock.WordmarkArt` 死样式**：R36 起项目硬性 0 警告，但 Avalonia XAML 资源里的未使用 `<Style>` **不会**触发编译器警告（Avalonia 不像 WPF 有 XAML 静态分析）。所以这是"原则性清理"而不是"为消除警告"。保留它会让下次维护者误以为还有第二处引用。

### 验证

1. `dotnet build -c Debug` — 0 警告 0 错误
2. `dotnet test` — 213/213 通过（Core 137 + Providers 35 + Windows 41）
3. `dotnet publish -c Release -r win-x64` — 0 警告，`BYH.exe` 26,957,824 bytes（比上批 26,953,216 增 4608 字节，≈ PNG 4924 字节，吻合）
4. 检查 `obj/Debug/net10.0/Avalonia/resources` manifest 确认 `/Assets/Theme/byh-wordmark.png` 已打包
5. System.Drawing 实测 PNG 透明度：3708 像素中 64.5% a=0、27.6% 半透明（反锯齿边）、8.0% 不透明（黑墨水），确认透明底无白底
6. 同步 `artifacts/publish/win-x64-nativeuia/BYH.exe`（这是真实运行路径，3 个 native DLL 不变）
7. PowerShell `Start-Process` 启动新 BYH.exe（PID 55408）

### ⚠️ 关键教训（永久记录，R39 新增）

- **Vision 模型判断"背景是否透明"不可靠**：Gemini Vision 看着 32bpp ARGB 真透明图说"白底"。原因是模型把"看不见的背景"按默认渲染（多数 viewer 用白色作透明 fallback）描述了。判断 PNG 透明度**必须用程序读 alpha 通道**（PowerShell + System.Drawing 全像素扫描，统计 `a=0` / `0<a<255` / `a=255` 占比），不能信视觉模型的文字描述。
- **PS 写 bash heredoc 时 `$` 必须双重转义**：bash heredoc + PS 内联脚本里写 `$bmp0 = New-Object System.Drawing.Bitmap $src` 会被 bash 当变量展开成空。要么写成独立 `.ps1` 文件再 `powershell -File`，要么每个 `$` 写 `\$`。第一轮我尝试 heredoc 内联失败两次（语法错），第三轮直接 `Write` 出 `.ps1` 文件一次通过。**判断标准：PS 脚本 >5 行就别 heredoc，直接 Write 文件**。
- **Avalonia `<AvaloniaResource Include="Assets\Theme\*.jpg" />` 不自动收 PNG**：项目 csproj 里已有的 glob 只针对 `.jpg`。新增 PNG 资源必须显式加 `<AvaloniaResource Include="..." />`，否则运行时 `avares://` URI 加载会抛 `ResourceNotFoundException`，编译期却完全不报错（资源 glob 不命中不算错误）。验证资源是否真打进 manifest：看 `obj/<Config>/<TFM>/Avalonia/resources` 文件。
- **大尺寸 PNG 要先裁剪 bbox 再缩放**：直接把 1376×768 缩到 36 高，原图四周大量透明留白会让最终 wordmark 在小尺寸里偏小且位置错乱。流程：①全像素扫 alpha 找 `A>阈值` 的 bbox；②加小 margin（~6px）；③从 bbox crop；④bicubic 缩到目标尺寸。`System.Drawing.Bitmap.Clone(Rectangle, PixelFormat)` 一步完成 crop+保格式。
- **GDI+ Save 同一文件被锁会抛"一般性错误"**：`Image.FromFile(path)` 会在内部维持文件句柄直到 `Dispose`，期间 `Save` 同路径会失败。正确流程：①`File.ReadAllBytes` 读到内存；②`new MemoryStream(bytes)`；③`Image.FromStream(ms)`——这样源文件句柄立即释放，可以任意覆盖。判断标准：**任何"读-改-写同一路径"的 GDI+ 流程都要 FromStream**。
- **死 XAML Style 不会触发编译警告**：Avalonia 不像 WPF 有 XAML 静态分析器报告未使用 `<Style Selector>`。所以删死样式是"原则清理"而非"消警告"。改 AXAML 后 `grep` 全项目确认引用清零（`grep -rn "WordmarkArt"` → 0 命中）才安全删除。
- **替换控件类型时全项目 grep 旧名字**：把 `WordmarkText` TextBlock 换成 `WordmarkImage` Image，必须 grep `WordmarkText` 找全所有引用（code-behind 3 处 + AXAML 1 处），漏一处就编译失败。判断标准：**任何控件改名/换类型 → 改完先 `grep 旧名` 确认 0 命中再 build**。

---

## 3q. 本会话（第三十批增量）完成的工作：R40 Fast tool → Ocean Eyes 改版

### 设计核心（来自用户答案）

1. **视觉模型这批只做 OCR**（提取文本）。翻译/解释/总结/prompt 全部**复用划词现有 PromptTemplate + provider 管线**——Ocean Eyes 比划词只多一步"框选→OCR→文本"前置。未来再整合为一个模型一步到位。
2. **"划词同款快捷键"= 复用 ToolbarWindow**。OCR 完成后调 `SetCaptureResult(ocrText)` → F/J/Z/R/C/V 全部零改动走现有 `OnToolbarKeyPressed` → `RunActionAsync` / `TryInvokeBuiltinToolbarShortcut`。唯一新键是 **Enter**（存图），由 `_oceanEyesActive` flag 门控，选词模式下 Enter 仍透传。
3. **Ctrl+Alt+Q 直进框选**；QuickToolsWindow 面板退役。
4. **截图**：默认 `%USERPROFILE%\Pictures\Ocean Eyes\` 存文件 + 复制剪贴板，路径可配，"自动保存"开关控制是否落盘。

### 改动清单

| # | 文件 | 改动 |
|---|------|------|
| 1 | `Core/Input/OceanEyesTriggerSettings.cs` **(新，替代 QuickToolsTriggerSettings.cs)** | 重命名 record + `GlobalHotKeyModifiers` enum 唯一来源。字段不变（KeyboardShortcutEnabled / Modifiers / Key / MouseChordEnabled），默认 Ctrl+Alt+Q。 |
| 2 | `Infrastructure/Configuration/OceanEyesTriggerStore.cs` **(新，替代 QuickToolsTriggerStore.cs)** | LoadIfExists 内迁移：新 `ocean-eyes.json` 不存在 → 读 legacy `quick-tools.json`（`SetLegacyMigrationPath` 注入路径）；Save 只写新名。schemaVersion 保持 1（字段没变，只是文件改名 + 类改名）。 |
| 3 | `Core/Capture/OceanEyesCaptureSettings.cs` **(新)** | sealed record：`SavePath`（默认 `Pictures/Ocean Eyes`）、`AutoSaveEnabled`（true）、`CopyToClipboardEnabled`（true）、`UiaAssistEnabled`（true）。`Normalize()` 展开 `%VAR%`、去 trailing slash、空回默认。`Validate()` 拒绝控制字符、`Path.GetFullPath` 不抛。 |
| 4 | `Infrastructure/Configuration/OceanEyesCaptureStore.cs` **(新)** | 镜像 `ToolbarShortcutsStore`：schema v1、8KB 上限、atomic `.tmp + Move`、字段缺失走默认。`ReadString` 非 string kind 抛 schema 异常（不能默默回默认）。 |
| 5 | `Infrastructure/Configuration/ByhApplicationPaths.cs` | 加 `OceanEyesTriggerFile`（`ocean-eyes.json`）、`OceanEyesCaptureFile`（`ocean-eyes-capture.json`）、`QuickToolsTriggerFileLegacy`（`quick-tools.json`，仅供迁移读）。删旧 `QuickToolsTriggerFile`。 |
| 6 | `Platform.Windows/Capture/ScreenRegionCapture.cs` | 抽出 `CaptureAsPng(x, y, w, h) -> byte[]?` 为 public 主方法；`CaptureAsDataUri` 改为薄包装（`Convert.ToBase64String(CaptureAsPng(...))`）。零行为变化。 |
| 7 | `Platform.Windows/Clipboard/Win32Clipboard.cs` | 新增 `SetPng(byte[])`：`RegisterClipboardFormatW("PNG")` → `AllocateGlobal` → `EmptyClipboard` + `SetClipboardData`。PNG 原样放（不做 PNG→DIB 转换，避免 NativeAOT 下 alpha 预乘 BGRA 脆弱）。补 `RegisterClipboardFormatW` P/Invoke。 |
| 8 | `Core/Selection/SelectionSessionManager.cs` | 新增 `SeedLastCapturedText(CaptureResult)`：Ocean Eyes 路径下 OCR 产文本不走完整 selection session，直接 seed `_lastCapturedText` 让 F/J/Z 看到。镜像 `SessionCoreAsync` 的赋值语义（null/空 → null）。 |
| 9 | `App/SelectionRuntime.cs` | 新增字段 `_oceanEyesActive`（Volatile int）、`_oceanEyesPng`（byte[]?）、`_oceanEyesCapture`（OceanEyesCaptureSettings）。新增 public：`SetOceanEyesCaptureSettings` / `GetOceanEyesCaptureSettings` / `ShowToolbarForOceanEyes(rectRight, rectTop, png)` / `FeedOceanEyesCapture(ocrText)`。新增 private：`SaveOceanEyesScreenshot` / `DismissOceanEyes`。`OnToolbarKeyPressed` 新增 Enter 分支（`vkReturn && _oceanEyesActive==1 → save`），Esc 分支调 `DismissOceanEyes`。`StopKeyboardHookQuiet` 统一清理 Ocean Eyes 状态（所有隐藏工具栏的路径自动清理）。 |
| 10 | `App/App.axaml.cs` | 字段重命名：`_quickToolsWindow` 删除、`_quickToolsHotKey`→`_oceanEyesHotKey`、`_triggerSettings`→`_oceanEyesTrigger`、新增 `_oceanEyesCapture`。装载 OceanEyesTrigger（含迁移）+ OceanEyesCapture。`OnChordTriggered` 拆为 `OnOceanEyesTriggered`（toggle）+ `EnterOceanEyesAt`（共享入口）。`OnRegionOcrRequested` 删除（不再有面板按钮）。`OnRegionSelected` 重写为 Ocean Eyes 流：`WaitForCompositorSettle` → `CaptureAsPng`（缓存）→ `ShowToolbarForOceanEyes` → 后台 `CaptureAndRecognizeRegionAsync` → `FeedOceanEyesCapture`。删 `RunRegionOcrAsync` / `OnQuickAction` / `OnManagePrompts`。`CreateStartedHotKey` 接受 `OceanEyesTriggerSettings`。`ToQuickToolsShape`→`ToOceanEyesShape`。所有 QuickTools handler/event 重命名。 |
| 11 | `UI/Views/QuickToolsWindow.axaml(.cs)` **(删除)** | 面板退役。 |
| 12 | `UI/Views/RegionSelectOverlay.axaml.cs` | `Cancel()` 从 private 改 public（Ocean Eyes toggle 二次按键时直接调）。 |
| 13 | `UI/Views/SettingsWindow.axaml(.cs)` | 重命名 `SetQuickToolsTriggerSettings`→`SetOceanEyesTriggerSettings`、`QuickToolsTriggerSettingsSaved`→`OceanEyesTriggerSettingsSaved`、`OnSaveQuickToolsTriggerClick`→`OnSaveOceanEyesTriggerClick`、`ShowQuickToolsTriggerStatus`→`ShowOceanEyesTriggerStatus`。新增 `SetOceanEyesCaptureSettings` + `OceanEyesCaptureSettingsSaved` event + `OnSaveOceanEyesCaptureClick` + `OnBrowseOceanEyesSavePathClick`（用 `TopLevel.StorageProvider.OpenFolderPickerAsync`）。AXAML：原"QuickTools 快捷键"卡片改"Ocean Eyes 触发"（文案说明 F/J/Z/R/C/V/Enter/Esc），新增第六张 PearlCard「Ocean Eyes 截图」（路径 TextBox + 浏览按钮 + 3 个 ToggleSwitch：自动保存/复制剪贴板/辅助框选 + 保存按钮）。所有文案"快捷工具"→"Ocean Eyes"或"Spotlight"。 |
| 14 | `UI/Views/LauncherEntryEditWindow.axaml(.cs)` | "快捷工具面板"文案改"Spotlight 搜索面板"。 |
| 15 | `Core/Input/SpotlightTriggerSettings.cs` + `Infrastructure/Configuration/SpotlightTriggerStore.cs` + `Infrastructure/Configuration/ToolbarShortcutsStore.cs` | `QuickToolsTriggerSettings/Store` 引用→`OceanEyesTriggerSettings/Store`（含 XML doc cref）。 |
| 16 | `Platform.Windows/Input/WindowsGlobalHotKey.cs` | 构造参数 + `Settings` 属性类型改 `OceanEyesTriggerSettings`。 |
| 17 | 测试：`tests/Core.Tests/Input/OceanEyesTriggerSettingsTests.cs`（重命名） + `tests/Core.Tests/Configuration/OceanEyesTriggerStoreTests.cs`（重写，加迁移测试 + ctor 清理 static legacy path） + `tests/Core.Tests/Configuration/OceanEyesCaptureStoreTests.cs` **(新)** + `tests/Windows.IntegrationTests/Input/WindowsGlobalHotKeyTests.cs`（sed 替换） | 共 +8 个新测试（OceanEyesCapture 全套 + OceanEyes 迁移测试），旧 QuickTools 测试全部重写为 OceanEyes。 |

### 关键设计决策

#### 为什么复用 ToolbarWindow 而非新建 OceanEyesToolbar

用户明确"划词同款快捷键触发"。ToolbarWindow 已实现 F/J/Z/R/C/V + 按钮启用/禁用 + 事件管线。OCR 文本经 `SetCaptureResult` 注入后，**F/J/Z/R/C/V 全部零改动**复用。唯一真正新增的是 Enter 键，由 `_oceanEyesActive` flag 门控——选词模式下 Enter 仍透传（0x0D → 非 A-Z → return false），零行为变化。

#### PNG 预缓存解决两个问题

**问题 1 — 采集竞态**：ToolbarWindow 在区域附近可见时，BitBlt 会把它拍进截图。**解决**：框选确认后、工具栏显示前，先 `CaptureAsPng`（此时只有 RegionSelectOverlay 刚 Hide，工具栏还没显示）。PNG bytes 缓存在 `_oceanEyesPng`。

**问题 2 — OCR 延迟**：~1s。**解决**：工具栏先以"识别中…"显示（ShowPending 全按钮 disabled），OCR 在后台跑；完成后 `FeedOceanEyesCapture` 启用按钮。用户按 Enter 存图时**无需等 OCR**（PNG 已缓存）。

#### Enter 键的模式门控

`OnToolbarKeyPressed` 现识别 Esc + Enter（仅 Ocean Eyes 模式）+ A-Z。新增分支：`vkReturn(0x0D) && _oceanEyesActive==1 → Save + return true`。选词模式下 `_oceanEyesActive==0`，Enter 落到现有非 A-Z 透传。零行为变化。

#### `StopKeyboardHookQuiet` 统一清理 Ocean Eyes 状态

所有隐藏工具栏的路径（RunActionAsync / RunPromptAsync / HideToolbarAndDisableHook / Esc）都调 `StopKeyboardHookQuiet`。在它开头加 `if (_oceanEyesActive != 0) DismissOceanEyes()`——一处修复覆盖所有路径，避免 cached PNG 漏到下一次选词会话。

#### 迁移：quick-tools.json → ocean-eyes.json

`OceanEyesTriggerStore.LoadIfExists`：新文件存在读新；否则若 legacy `quick-tools.json` 存在 → 读旧作迁移源（一次性）→ 返回 settings（不立即写新文件，下次 Save 时才落新名）。`SetLegacyMigrationPath` 在 App 启动时注入 legacy 路径（避免改 LoadIfExists 签名破坏与其它 store 的对称性）。schemaVersion 保持 1（字段没变）。老用户无感升级。

#### CF_PNG 而非 CF_DIB

`Win32Clipboard.SetPng` 用 `RegisterClipboardFormatW("PNG")` 直接放原始 PNG bytes——不做 PNG→DIB 转换（NativeAOT 下 alpha 预乘 BGRA 脆弱）。Windows 10 1809+ 及所有现代图像编辑器/聊天客户端都识别 CF_PNG。落盘的文件是权威 artifact，剪贴板是便利。

### 验证

- `dotnet build -c Debug` — **0 警告 0 错误**（R36+ 硬要求）
- `dotnet test` — **221/221 通过**（Core 145 + Providers 35 + Windows 41；+8 个新 OceanEyes 测试，0 个回归）
- `dotnet publish -c Release -r win-x64` — **0 警告 0 错误**，NativeAOT 完整 `Generating native code`，exe `BYH.exe` = **26,945,536 bytes**（无 PDB，比 R39 的 26,957,824 小 ~12KB，因 QuickToolsWindow 代码删除）
- 双路径同步：`cp .../publish/BYH.exe artifacts/publish/win-x64-nativeuia/BYH.exe`
- PowerShell `Start-Process` 重启，PID 30288 在跑（`Get-Process BYH | Select Path` 确认是 artifacts 路径）
- 启动日志序列正确（Provider switched / Vision OCR enabled / MouseHook + KeyboardHook installed / Runtime started）
- grep 全项目确认无活性 QuickTools 代码引用（仅注释里的历史说明 + `QuickToolsTriggerFileLegacy` 迁移属性）

### 新增的 7 条永久教训（R40）

- **复用而非新建 UI 是用户"同款"诉求的正解**：用户说"划词同款的快捷键触发"，正解不是新建一个 OceanEyesToolbar 复刻一遍 F/J/Z/R/C/V，而是把 OCR 文本喂进现有 ToolbarWindow 的 `SetCaptureResult`——零改动复用全部快捷键 + 按钮启用逻辑。唯一真正新增的是 Enter 键（存图），用模式 flag 门控不破坏选词模式。判断标准：**"同款"诉求 → 找现有 UI 的注入点，而非新建并行 UI**。
- **模式 flag + 现有吞键守卫的组合 > 新键分发器**：新增 Enter 不需要在 `OnToolbarKeyPressed` 写一大段 Ocean Eyes 专属逻辑——一个 `vkReturn && _oceanEyesActive==1` 分支够了。`_oceanEyesActive==0` 时 Enter 自动落到现有"非 A-Z 透传"路径，选词模式行为零变化。判断标准：**新键优先用模式 flag 复用现有路径，而非复制分发逻辑**。
- **预缓存 PNG 解决采集竞态 + 延迟两个问题**：截图必须在工具栏显示前完成（否则工具栏被拍进截图）；同一份 bytes 还能让 Enter 立即存图不等 OCR。一次 capture 两用。判断标准：**任何"先采集再显示 UI 再异步处理"的流程，采集产物要缓存到 UI 显示之前**。
- **统一清理枢纽 > 每个退出点手动清理**：Ocean Eyes 状态（`_oceanEyesActive` + `_oceanEyesPng`）必须在所有"隐藏工具栏"的路径清理（Esc / F/J/Z 触发 / R/C 提示 / 粘贴后用户切走…）。与其在每个路径加清理，不如在 `StopKeyboardHookQuiet`（所有路径都调）开头加一次清理。判断标准：**跨多路径的状态清理 → 找最底层的公共枢纽方法**。
- **static 字段做迁移路径注入要配 ctor 清理**：`OceanEyesTriggerStore.SetLegacyMigrationPath` 是 static，测试间会泄漏。每个测试类的 ctor 调 `SetLegacyMigrationPath(null)` 重置。判断标准：**任何 static 可变状态 + 单测 → 测试 ctor 清理**。
- **Win32 clipboard 放 PNG 用 CF_PNG 不用 CF_DIB**：PNG→DIB 转换需要解 PNG + 写 BITMAPINFOHEADER + alpha 预乘 BGRA，NativeAOT 下脆弱。`RegisterClipboardFormatW("PNG")` 直接放原始 bytes，Win10 1809+ 全支持。判断标准：**剪贴板放图片 → 优先 CF_PNG 原始 bytes，落盘文件做权威 artifact**。
- **`ReadString` 非 string kind 必须抛 schema 异常**：settings store 的 `ReadString` 如果对 `value.ValueKind != String` 默默返回 default，会让 `"savePath": 42` 这种 schema 错误静默通过。修正：非 string kind 抛 `ProviderConfigurationException`。空字符串仍走 default（用户清空字段的合法语义）。判断标准：**JSON 字段类型不匹配 ≠ 字段缺失，前者是错误必须抛**。

---

## 3r. 本会话（第三十一批增量）完成的工作：R41 Ocean Eyes 交互重构

### 用户需求（确认版）

| 输入 | 框选中（drag in progress） | 工具栏已弹出（确认后） |
|------|---------------------------|----------------------|
| **左键** | 拖动画框（现有） | 无操作（透传） |
| **右键** | **取消**（现有，保留） | **清空 rect 重画**（overlay 不退、工具栏隐藏、OCR 缓存清空） |
| **Esc** | 退出 | 退出（overlay + 工具栏） |
| **Enter** | 确认（overlay 阶段保留） | **保存截图**（不 OCR） |
| **F/J/Z/R/C** | n/a | **首次：触发 OCR → 走动作。后续：复用 OCR 文本直接走动作。** |
| **V（粘贴）** | — | **删除** |

工具栏时机：**左键确认后立即出现**（状态"未识别"，按钮 disabled）。OCR 推迟到动作键触发，同区域多动作键复用 OCR 文本。

### 改动清单

| # | 文件 | 改动 |
|---|------|------|
| 1 | `UI/Views/RegionSelectOverlay.axaml.cs` | **`OnCanvasPointerReleased`**：左键释放 → `Confirm()`（替代 R24 的 DoubleTapped 确认）。**移除 `SelectionRect.DoubleTapped += ...` handler**。**新增 `public event Action? RegionReset`** + **`public void Reset()`**：清空 rect、`_userTouchedRect=false`、`_cancelling=false`、重新 EnableLiveTracking（辅助框选恢复）、raise RegionReset。`Cancel()` 改 public（R40 已改）。 |
| 2 | `App/SelectionRuntime.cs` | **新增字段**：`_oceanEyesRect`、`_oceanEyesOcrTask`（Task<string?>?，null=未启动）、`_oceanEyesOcrText`、`_oceanEyesOcrDone`（Volatile int）。**`ShowToolbarForOceanEyes` 加 4 个 rect 参数**，初始化 OCR 字段为 null，工具栏显示"未识别 · 按 F/J/Z/R/C 开始"（`SetDiagnosticStatus`）。**新增 `EnsureOceanEyesOcrAsync`**：首次调用启动 `CaptureAndRecognizeRegionAsync` task 存 `_oceanEyesOcrTask`；await + 缓存 `_oceanEyesOcrText` + 置 `_oceanEyesOcrDone=1` + 调 `FeedOceanEyesCapture`；后续调用 await 同一 task（OCR 只跑一次）。**`OnToolbarKeyPressed` A-Z 分支重构**：OCR 未完成时（`_oceanEyesActive==1 && _oceanEyesOcrDone==0`）→ 吞键 + 后台 `EnsureOceanEyesOcrAsync` + UI 线程 redispatch 原键；OCR 已完成 → 抽出的 `DispatchToolbarActionKey(key)` 走 PromptTemplate/builtin 路径。**`DismissOceanEyes` 清 OCR 字段**。**新增 `OnMouseSwallowCheck`**（hook 线程）：右键 down + Ocean Eyes 活跃 → `DismissOceanEyes` + raise `RegionResetRequested`，返回 true 吞右键。**新增 `public event Action? RegionResetRequested`**。**Start/Dispose 订阅/取消 `_mouseHook.SwallowCheck`**。 |
| 3 | `Platform.Abstractions/IMouseHook.cs` | **新增 `event Func<MouseEventData, bool>? SwallowCheck`**：订阅者返回 true 则 hook 吞掉该鼠标事件（不传源应用）。 |
| 4 | `Platform.Windows/Hooks/LowLevelMouseHook.cs` | 实现 `SwallowCheck` event。`HookCallback` 在派发 MouseEvent 前调 `ShouldSwallow(eventData)`——任一订阅者返回 true 则 `return 1`（吞）。新增 `ShouldSwallow` 辅助方法（隔离异常，类似 `RaiseMouseEventSafely`）。 |
| 5 | `UI/Views/ToolbarWindow.axaml(.cs)` | **移除 `PasteButton`**（AXAML）+ Grid 列定义 8→7 列 + Status Grid.Column 6→5 + MoreButton Grid.Column 7→6。**移除 `PasteRequested` event + `OnPasteClick` + `InvokePasteShortcut`** + 孤立 XML doc summary。 |
| 6 | `Core/Input/ToolbarShortcutSettings.cs` | **移除 `PasteKey` 字段**（只剩 PromptKey + CopyKey）。`Normalize`/`Validate` 改两键互斥。 |
| 7 | `Infrastructure/Configuration/ToolbarShortcutsStore.cs` | 不写 `pasteKey`；读时忽略（向前兼容，老文件正常加载）。 |
| 8 | `App/SelectionRuntime.cs` P2 连锁 | 移除 `_toolbarWindow.PasteRequested += OnPasteRequested`、`OnPasteRequested` 方法、`TryInvokeBuiltinToolbarShortcut` 的 Paste 分支（只剩 isCopy/isPrompt）。**`SendInputHelper.SendPasteChord` 保留**——ResultWindow 的 Replace 流程仍用它。 |
| 9 | `App/App.axaml.cs` | **`OnRegionSelected` 不再跑 OCR**：只 capture PNG + `ShowToolbarForOceanEyes(rectRight, rectTop, png, x, y, w, h)`（加 rect 参数）。**新增订阅 `_runtime.RegionResetRequested`** → `Dispatcher.UIThread.Post(() => _regionOverlay?.Reset())`。移除 OnRegionReset 方法（冗余——Runtime 已在 swallow handler 里 DismissOceanEyes）。移除 `requested.PasteKey` 的状态行。 |
| 10 | `UI/Views/SettingsWindow.axaml(.cs)` | 工具栏快捷键卡片：移除"粘贴"行 + Grid RowDefinitions 3→2 行。`SetToolbarShortcuts`/`OnSaveToolbarShortcutsClick` 移除 PasteKey。 |

### 关键设计决策

#### 惰性 OCR 状态机

```
画框/辅助框选 → 左键释放 → Confirm() → RegionSelected 事件
                                              │
                                              ▼
              ShowToolbarForOceanEyes(png, rect)
              _oceanEyesActive=1, _oceanEyesPng=png
              _oceanEyesOcrTask=null, _oceanEyesOcrText=null, _oceanEyesOcrDone=0
              工具栏: "未识别 · 按 F/J/Z/R/C 开始", 按钮 disabled
                                              │
              ┌───────────────────────────────┼──────────────────────────┐
              ▼                               ▼                          ▼
           按 F/J/Z/R                       按 C                       按 Enter
              │                               │                          │
              ▼                               ▼                          ▼
       _oceanEyesOcrDone==0:            _oceanEyesOcrDone==0:      SaveOceanEyesScreenshot
       吞键 + 后台启 OCR task           吞键 + 后台启 OCR task    (不 OCR, 直接存 PNG)
       完成后 UI 线程 redispatch key    完成后 UI 线程 redispatch
              │                               │
              ▼                               ▼
       DispatchToolbarActionKey(key)    DispatchToolbarActionKey(key)
       → PromptTemplate/builtin 分发    → PromptTemplate/builtin 分发
                                        (C=复制 OCR 文本到剪贴板)

       [第二次按动作键] _oceanEyesOcrDone==1 → 直接 DispatchToolbarActionKey
       (零延迟，OCR 缓存命中)
```

#### OCR Task 复用（缓存）

`EnsureOceanEyesOcrAsync` 第一次调用创建 `Task<string?>` 存 `_oceanEyesOcrTask`。后续调用 `await _oceanEyesOcrTask`——同一 Task 多次 await 合法，OCR 只跑一次。`_oceanEyesOcrDone` Volatile flag 让 hook 线程的 A-Z 分支快速判断"已有缓存走快路径"vs"必须异步等 OCR"。

#### 右键拦截：mouse hook `SwallowCheck` 方案

工具栏是 `WS_EX_NOACTIVATE`，不接收 Avalonia pointer。overlay 在 Confirm 后 `Hide()` 了，也收不到右键。所以右键拦截必须在 `LowLevelMouseHook` 的 hook callback 层。新增 `SwallowCheck: Func<MouseEventData, bool>` 事件——订阅者在事件派发**前**同步决定吞/不吞（hook 线程）。SelectionRuntime 订阅：右键 down + `_oceanEyesActive==1` → `DismissOceanEyes` + raise `RegionResetRequested` + 返回 true 吞右键。Ocean Eyes 活跃期间所有右键被吞（用户本来就在与 BYH 交互，代价可接受）；Esc/动作键退出后右键立即恢复。划词模式（`_oceanEyesActive==0`）不吞右键——用户可在源程序右键。

#### V 删除的向前兼容

`ToolbarShortcutSettings.PasteKey` 字段删除，但 `ToolbarShortcutsStore.LoadIfExists` 不读 `pasteKey` 字段——老文件（R37-R40 写的）仍正常加载，pasteKey 被静默忽略。Save 时不再写 pasteKey，下次保存后文件"自愈"。`SendInputHelper.SendPasteChord` 保留（ResultWindow Replace 流程用）。

#### 抽取 `DispatchToolbarActionKey`

`OnToolbarKeyPressed` 的 A-Z 分支（PromptTemplate 查询 + RunActionAsync / TryInvokeBuiltinToolbarShortcut）抽成独立方法 `DispatchToolbarActionKey(key)`。两个调用点：(1) OCR 完成的快路径，(2) 惰性 OCR 完成后的 UI 线程 redispatch。避免逻辑重复。

### 验证

- `dotnet build -c Debug` — **0 警告 0 错误**
- `dotnet test` — **221/221 通过**（Core 145 + Providers 35 + Windows 41；ToolbarShortcutSettings 从 3 键到 2 键，无专门测试文件所以 0 回归）
- `dotnet publish -c Release -r win-x64` — **0 警告 0 错误**，exe `BYH.exe` = **26,966,016 bytes**（无 PDB，比 R40 的 26,945,536 大 ~20KB，因 P0-P4 新逻辑：EnsureOceanEyesOcrAsync / Reset / SwallowCheck / DispatchToolbarActionKey 抽取等）
- 双路径同步 + PowerShell 重启，PID 55108 在跑
- 启动日志序列正确

### 新增的 5 条永久教训（R41）

- **惰性 OCR + task 缓存 > 即时 OCR**：用户按动作键前 OCR 没用——他可能只想 Enter 存图或 Esc 退出。即时 OCR 浪费 ~1s + API 调用。`Task<string?>` 缓存让首次动作键启动 OCR、后续动作键零延迟复用，且 `await task` 多次合法（Task 完成后 await 立即返回）。判断标准：**昂贵操作推迟到真正需要时，且用 Task 缓存让"首次触发"和"复用"统一**。
- **hook 线程的吞键决策必须同步、派发必须异步**：`OnToolbarKeyPressed` 跑在 keyboard hook 后台线程，吞键决定（return true/false）必须同步（不能 await），否则用户的下一个键事件会堆积。但 RunActionAsync 等 UI 操作必须 `Dispatcher.UIThread.Post` 派发（不能同步调 UI API，会崩）。惰性 OCR 的解法：hook 线程吞键 + `Task.Run(EnsureOceanEyesOcrAsync)` + `.ContinueWith → Dispatcher.UIThread.Post(redispatch)`。判断标准：**hook 线程 = 同步决策 + 异步派发，绝不阻塞**。
- **鼠标 hook 加 SwallowCheck 不能破坏现有"只观察"契约**：`LowLevelMouseHook` 原本只观察事件（永不吞，源应用右键菜单正常工作）。加吞键能力要用独立 `SwallowCheck` event（`Func<MouseEventData, bool>`），而不是改 `MouseEvent`（`Action`）的签名——后者会破坏所有现有订阅者。SwallowCheck 在 MouseEvent 派发**前**调用，返回 true 则跳过 MouseEvent 派发 + `return 1` 吞。判断标准：**扩展 hook 能力用新 event，不动现有 event 契约**。
- **字段移除要向前兼容读旧文件**：`PasteKey` 删除后，`ToolbarShortcutsStore.LoadIfExists` 不读 `pasteKey` 字段——老文件正常加载（字段被静默忽略），下次 Save 时新 schema 落盘"自愈"。不要在 Load 时检测到 pasteKey 就抛 schema 错误（用户没做错任何事）。判断标准：**字段移除 = Load 忽略 + Save 不写，schemaVersion 不变**。
- **同一交互的不同阶段用不同事件区分语义**：右键在框选中 = Cancel（现有），右键在工具栏已弹出 = Reset（重画）。Overlay 的 Cancel（退出）vs Reset（重画）是两个独立 event + 方法。判断当前阶段的职责在 mouse hook 层（看 `_oceanEyesActive`）而非 overlay 层——因为 overlay 在工具栏弹出后已 Hide，收不到右键。判断标准：**交互语义随阶段变化时，用状态 flag 在 hook 层区分，而非让单一控件处理多种语义**。

---

## 3s. 本会话（第三十二批增量）完成的工作：R42 Ocean Eyes overlay 锁定 + 截图竞态修复

### 用户需求（确认版）

R41 的 overlay 在 `Confirm()` 后 `Hide()` 导致"左键以后直接退出框选"。用户要求：
1. **左键确认后 overlay 不退出**——锁定窗口，停在框选画面等下一步按键（F/J/Z/R/C/Enter/Esc）。
2. **框内外统一**：单击（< 5px）= 确认当前 UIA 框，拖动（≥ 5px）= 重画新 rect。Move 模式删除。
3. **白虚线 + 古金手柄**：选区边框白色虚线 `#FFFFFFFF` + `StrokeDashArray="4,3"`，8 个 resize 手柄保持 Ivory Jade 古金色。
4. **中间透明**：EvenOdd PathGeometry——选区内完全透明（看到桌面），选区外 dim `#B33A2417`。

### 改动清单

| # | 文件 | 改动 |
|---|------|------|
| 1 | `UI/Views/RegionSelectOverlay.axaml` | Background → Transparent。新增 DimMask Path（EvenOdd 几何）。SelectionRect → 白虚线 Fill=Transparent。Handles 保持古金色。 |
| 2 | `UI/Views/RegionSelectOverlay.axaml.cs` | **新增 `_confirmed` flag**。**新增 `DrawPending` DragMode** + 5px 阈值。**`UpdateDimMask()`**：PathGeometry FillRule.EvenOdd 外圈屏幕 + 内圈 selection hole。**`Confirm()`**：移除 `Hide()`，设 `_confirmed=true`，overlay 保持可见。**新增 `ShowConfirmed()`**：截图后恢复 overlay。**`Reset()`/`Cancel()`**：清 `_confirmed`。**`OnCanvasPointerPressed`**：右键+confirmed→Reset，左键+confirmed→return，左键+not confirmed→DrawPending。**`OnCanvasPointerMoved`**：DrawPending 超 5px→Draw。**`OnCanvasPointerReleased`**：任何释放→Confirm。**`OnRectPointerPressed` DELETED**（SelectionRect IsHitTestVisible=False）。**`SelectionRect.DoubleTapped` handler removed**。 |
| 3 | `Platform.Abstractions/IMouseHook.cs` | **移除 `SwallowCheck` event**（R41 加的，R42 回收）。 |
| 4 | `Platform.Windows/Hooks/LowLevelMouseHook.cs` | **移除 `SwallowCheck` event + `ShouldSwallow` 方法 + HookCallback swallow check**。恢复"只观察不吞"契约。 |
| 5 | `App/SelectionRuntime.cs` | **移除 `RegionResetRequested` event**。**移除 `OnMouseSwallowCheck` 方法**。**移除 Start/Dispose 的 SwallowCheck 订阅**。**新增 `public Action? DismissOverlay`**：App 设置的回调，在 `DismissOceanEyes()` 末尾调用。**新增 `public void ResetForRedraw()`**：右键重画时清 toolbar + OCR 状态但不关 overlay。 |
| 6 | `App/App.axaml.cs` | **`RegionCancelled` handler**：调 `_runtime?.ResetForRedraw()`。**新增 `RegionReset` subscription**：调 `_runtime?.ResetForRedraw()`。**`DismissOverlay` callback**：`_regionOverlay?.Cancel()`。**移除 `RegionResetRequested` subscription**。**重写 `RunOceanEyesCaptureAsync`**：Hide overlay → WaitForCompositorSettle → CaptureAsPng → null 则 Cancel → ShowConfirmed → ShowToolbarForOceanEyes。 |

### 关键设计决策

#### overlay 锁定 + DismissOverlay 回调

`Confirm()` 不再 `Hide()`——overlay 保持可见，`_confirmed=true` 标记锁定状态。`DismissOceanEyes()` 末尾调 `DismissOverlay?.Invoke()` → App 关闭 overlay。覆盖所有终端路径：Esc、Enter、action keys、StopKeyboardHookQuiet。

#### 右键重画：ResetForRedraw vs DismissOceanEyes

右键重画需要清 toolbar + OCR 状态但**不关 overlay**。`DismissOceanEyes` 会调 `DismissOverlay`（关 overlay），所以新增 `ResetForRedraw()` 只做状态清理。App 订阅 `RegionReset` → `_runtime.ResetForRedraw()`。

#### 截图竞态：Hide → Capture → ShowConfirmed

overlay 在 Confirm 后保持可见，BitBlt 会拍到 dim mask + 白虚线 + handles。`RunOceanEyesCaptureAsync` 先 `_regionOverlay.Hide()` → settle → capture → `_regionOverlay.ShowConfirmed()` 恢复。

#### DrawPending 5px 阈值

`OnCanvasPointerPressed` 不再清零 rect。记录 `_dragStart`，设 `DrawPending`。moved 超 5px 升级为 Draw（此时才开始画新 rect）。released 时若仍 DrawPending = 单击 → 确认当前 UIA 框。

#### SelectionRect IsHitTestVisible=False

所有点击（无论在 rect 内外）都落到 Canvas，统一走 Draw 逻辑。Move 模式删除——用户要移动就重画。Resize 仍通过 8 个手柄。

### 验证

- `dotnet build -c Debug` — **0 警告 0 错误**
- `dotnet test` — **221/221 通过**（Core 145 + Providers 35 + Windows 41）
- `dotnet publish -c Release -r win-x64` — **0 警告 0 错误**，exe `BYH.exe` = **27,008,512 bytes**
- 双路径同步 + PowerShell 重启

---

## 3t. 本会话（第三十三批增量）完成的工作：Spotlight 搜索增强 + 启动器新增应用

### 用户需求

1. 启动器新增 7 个应用：A HUB、CC Switch、RK Keyboard、QQ、微信、微信输入法、KeySilk。
2. ChatGPT 桌面端快捷启动更名为 Codex（网页版保留 ChatGPT）。
3. Spotlight 搜索面板输入 "bb" 无法匹配 "bilibili"——需要拼音首字母 + 词首字母匹配。

### 改动清单

| # | 文件 | 改动 |
|---|------|------|
| 1 | `%LocalAppData%\BYH\launcher-entries.json` | 新增 7 个 launcher entry（A HUB / CC Switch / RK Keyboard / QQ / 微信 / 微信输入法 / KeySilk）。ChatGPT localApp 更名 Codex。 |
| 2 | `UI/Views/SpotlightWindow.axaml.cs` | **新增 `MatchesQuery(name, query)` 三级匹配**：① 子串匹配（现有）→ ② `MatchInitials` 贪心扫描（每个 query 字符匹配一个词段首字母，词段边界=分隔符/camelCase/CJK 边界，"bb" 匹配 "Bilibili"）→ ③ `ExtractPinyinInitials`（内置 ~600 常用汉字拼音首字母字典，"微信"→"wx"）。**`ReapplyFilter()` 的 Where 条件改为 `MatchesQuery`**。 |
| 3 | `handoff/00-CURRENT-HANDOFF.md` | 第三十二批 → 第三十三批；§1 状态行更新；新增 §3t。 |

### 关键设计决策

#### 三级匹配策略

1. **子串匹配**（保留）：`Name.Contains(query, OrdinalIgnoreCase)` — "se" 匹配 "DeepSeek"。
2. **贪心首字母扫描**：逐字符遍历 name，识别词段边界（分隔符/camelCase 小写→大写/CJK 边界），query 每个字符匹配一个词段首字母（大小写不敏感），匹配后继续贪心扫描。"bb" 匹配 "Bilibili"（B@词首 → b@camelCase 边界）。"cb" 匹配 "CodeBuddy CN"（C@0 → B@4）。"ah" 匹配 "A HUB"（A@0 → H@2）。
3. **拼音首字母**：内置静态字典覆盖 ~600 常用汉字，遍历中文字符查表。"wx" 匹配 "微信"、"微信输入法"。"xwjt" 匹配 "小旺AI截图"。NativeAOT 友好，无外部 NuGet 依赖。

#### 无外部拼音库

.NET 生态的拼音库（如 `PinYinConverterCore`）依赖反射和大字典文件，与 NativeAOT TrimMode=full 不兼容。内置 ~600 字静态字典足够覆盖应用名称和日常 UI 文本场景。

### 验证

- `dotnet build -c Debug` — **0 警告 0 错误**
- `dotnet test` — **221/221 通过**（Core 145 + Providers 35 + Windows 41）
- `dotnet publish -c Release -r win-x64` — **0 警告**
- 双路径同步 + PowerShell 重启
- 机器侧验证：Spotlight（Ctrl+Alt+Space）输入 "bb" → 匹配 bilibili，"wx" → 匹配 微信/微信输入法，"cb" → 匹配 CodeBuddy CN

---

## 3u. 本会话（第三十四批增量）完成的工作：R43 Spotlight 选中态可读性（两次尝试，第二次才找到真因）

### 用户需求

启动器当前选中的应用没有明显的选中状态，难以判断焦点在哪一行。要求：键盘 ↑↓ 切换的当前行必须一眼可辨。用户反馈第一版修复后："下面的应用列表依然没有显示当前选中的应用是谁。搜索框倒是变成了绿豆背景。"

### 根因（第一版诊断错了，第二版才找到）

**第一版的错误诊断**：以为是 `ItemsControl` 异步 realize 容器的"竞态"——加了 `LayoutUpdated` 钩子 + `Dispatcher.UIThread.Post(Loaded)` + generation 守卫。**这套方案完全无效**，因为根本不是时机问题。

**真正的根因**：`ItemsControl.ContainerFromIndex(i)` 返回的是 **Avalonia 内部自动生成的 `ContentPresenter` 包装**，**不是** `DataTemplate` 里我们写的那个 `<Border Classes="SpotlightRow">`。给 `ContentPresenter` 加 `Active` 类，永远不会命中我们的 `Border.SpotlightRow.Active` 样式选择器。无论同步、异步、LayoutUpdated 触发多少次，都改不到真正带样式的那个元素。所以选中态**从来没显示过**，不管时机怎么调。

视觉截图（用户提供）也证实：列表区**完全没有**任何高亮（背景/边框/强调条都没有），搜索框的"绿豆背景"其实是**既有**的 `TextBox:focus` 2px 玉色边框（不是 R43 引入的回归）。

### 真正的修复方案

**数据绑定驱动 class 成员**：在行模型上加 `IsSelected`（带 `INotifyPropertyChanged`），`DataTemplate` 里用 Avalonia 的 `Classes.<name>="{Binding bool}"` 语法把 `Active` 类的成员资格**直接绑定**到 `IsSelected`。这样切换 `IsSelected` 时，Avalonia 自己负责把 `Active` 类加到/移除自**真实的 Border**（因为绑定是挂在 Border 上的），不依赖容器查询，没有任何时机问题。

### 改动清单

| # | 文件 | 改动 |
|---|------|------|
| 1 | `UI/Views/LauncherEntryRow.cs` | **实现 `INotifyPropertyChanged`**。新增 `bool IsSelected` 属性（带 setter 触发 PropertyChanged）。`Icon` 也改成带通知的属性（之前是 plain auto-property，icon 异步加载后 UI 不更新——顺手修）。 |
| 2 | `UI/Views/SpotlightWindow.axaml` | DataTemplate 的根 `<Border>` 上加 `Classes.Active="{Binding IsSelected}"`（Avalonia 12 的 class-条件绑定语法，编译绑定兼容，NativeAOT 友好）。Name 加 `Classes="SpotlightRowName"`、Target 加 `Classes="Muted SpotlightRowTarget"`（让样式选择器命中 Active 行的内部文字）。Footer badge 链首加 "↑↓ 选择"。 |
| 3 | `UI/Views/SpotlightWindow.axaml.cs` | **删除**整套容器查询机制：`ApplySelectionVisual` / `ApplySelectionVisualCore` / `EnsureLayoutHook` / `OnResultsLayoutUpdated` / `_selectionGeneration` / `_layoutHooked` / `ResultsList.ItemsView.CollectionChanged` 订阅。**替换为 `SyncRowSelection()`**：遍历 `_filteredRows`，设 `row.IsSelected = (i == _selectedIndex)`。`ReapplyFilter` / `MoveSelection` / `OnRowPointerPressed` 三处调用点全部改成 `SyncRowSelection()`。数据绑定负责其余的——`IsSelected` 一变，对应 Border 的 `Active` 类自动加/删，样式立即生效。 |
| 4 | `UI/Themes/IvoryJade.axaml` | **`Border.SpotlightRow.Active`** 重写：`Background=ByhSurfaceSelectedBrush`（`#FFE7EDCF` 淡豆绿）+ `BorderBrush=ByhPrimaryBrush`（玉色）+ `BorderThickness=1` + `BoxShadow="inset 3 0 0 0 #FF667731"`（左侧 3px 玉色强调条）。**`Active:pointerover`** 保持 `SurfaceSelected` 填充（hover 不覆盖选中色）。**`Active TextBlock.SpotlightRowName`**：Bold + 玉色。**`Active TextBlock.SpotlightRowTarget`**：TextSecondary 色。`Border.SpotlightRow` 加 `BrushTransition`（80ms 渐变）。**`TextBox.SpotlightSearch:focus`** 补 `BorderThickness=0 + BorderBrush=Transparent`，抑制通用 `TextBox:focus` 2px 玉色边框（消除"搜索框变绿"错觉）。 |

### 关键设计决策

#### 为什么必须数据绑定，不能容器查询

`ItemsControl` 的容器是内部的 `ContentPresenter`，不是 DataTemplate 的根元素。从 code-behind 拿到的 container 给它加 class 永远改不到真正带样式的元素。要改 DataTemplate 内部元素的 class，**只能**用数据绑定（`Classes.<name>={Binding bool}`），让 Avalonia 在绑定目标（我们的 Border）上自己加/删 class。这是 Avalonia 11+ 处理 `ItemsControl` 条件样式的**唯一**可靠方案。Settings 页的 `Button.SettingsNav.Active` 能用 code-behind 直接加 class，是因为那里的 Button 是**直接**放在面板里的（不是 ItemsControl 的 item），没有 ContentPresenter 包装层。

#### `Classes.Active="{Binding IsSelected}"` 语法验证

这是 Avalonia 11/12 的"class 条件绑定"语法，等价于 WPF 的 DataTrigger。编译绑定（`x:DataType`）接受它，NativeAOT 0 警告通过。语义：绑定值为 true → `Active` 加入 Border 的 Classes 集合；false → 移除。配合 `INotifyPropertyChanged`，`IsSelected` 变化时立即触发。

#### 为什么顺手把 `Icon` 也改成通知属性

旧代码 `Icon` 是 plain `{ get; set; }`，App 后台线程加载完图标后调 `row.Icon = bitmap`，但 UI 不通知不刷新——依赖 `UpdateLauncherIcon` 走 `Dispatcher.UIThread.Post` 重新赋值碰巧触发刷新。改成 INPC 后语义干净，不依赖副作用。

#### 为什么搜素框 focus 边框要抑制

通用 `TextBox:focus` 样式（2px 玉色边框）在 `SpotlightSearch` 之后定义，且 `:focus` 伪类比无伪类更 specific，**会覆盖** `SpotlightSearch` 的 `BorderThickness=0`。所以必须显式写一个 `TextBox.SpotlightSearch:focus` 把 `BorderThickness` 和 `BorderBrush` 都重置。这不是 R43 引入的问题（既有行为），但用户截图里把它误读成"绿豆背景"，顺手修掉消除歧义。

### 教训

1. **不要在没验证假设的情况下叠加兜底机制**。第一版没真正确认 `ContainerFromIndex` 返回的是什么类型，就假设是"时机问题"叠了 3 层兜底（同步 + LayoutUpdated + Dispatcher.Post），结果全无效。正确做法是先打个日志或断点确认 `container.GetType()` 是 `ContentPresenter` 而不是 `Border`，再决定方案。
2. **Avalonia `ItemsControl` 的容器 ≠ DataTemplate 根元素**。这是和 WPF `ListBox`（容器是 `ListBoxItem`，可直接 styling）的最大区别之一。条件样式优先用数据绑定（`Classes.<name>={Binding}`），不要从 code-behind 查容器。

### 验证

- `dotnet build -c Debug` — **0 警告 0 错误**
- `dotnet test` — **221/221 通过**（Core 145 + Providers 35 + Windows 41）
- `dotnet publish -c Release -r win-x64` — **0 警告 0 错误**，exe `BYH.exe` = **27,606,528 bytes**（较第一版的 27,596,800 +10KB，因 INPC + IsSelected 属性 + class 绑定）
- 双路径同步（robocopy /MIR）+ PowerShell `Start-Process` 重启，PID 3168 运行中
- 机器侧验证（待用户确认）：
  - `Ctrl+Alt+Space` → Spotlight 打开，**第一行立即显示淡豆绿背景 + 玉色边框 + 左侧 3px 玉色强调条 + 名称 Bold 玉色**（数据绑定直接生效，不再依赖容器查询）
  - ↑↓ 键 → 选中行高亮实时跟随，无延迟
  - 输入 "wx" 过滤后 → 微信行**立即**带选中态（filter 重排后 SyncRowSelection 重设 IsSelected，绑定自动刷新）
  - 鼠标飘过其他行 → 选中行**保持**淡豆绿（Active:pointerover 守护）
  - 搜索框**不再有玉色边框**（TextBox.SpotlightSearch:focus 抑制）

---

## 3v. 本会话（第三十四批续 + 第三十五批）完成的工作：R43 视觉精修——选中态金花边 + 搜索框金框 + 全局 accent 改色

接 §3u。选中态机制修好（INPC + Classes.Active 绑定）后，用户继续要求视觉精修，跨度多轮迭代（约 15+ 次 publish 循环）。这一节记最终状态 + 关键教训。

### 最终视觉方案（用户认可）

**选中行**（`Border.SpotlightRow.Active`）和**搜索框**（`Border.SpotlightGoldFrame`）共享同一套"金花边"设计：

| 层 | 颜色 | 实现 |
|----|------|------|
| 外金边 1px | `#FFD9C28A` 中调香槟金 | `BorderBrush` |
| 香槟缝 1px | `#FFFCF7EA`（同填充色，读作"沟"） | `BoxShadow inset 0 0 0 2` |
| 亮金内线 glint 1px | `#FFF4E7C8` 亮香槟 | `BoxShadow inset 0 0 0 3` |
| 填充 | `#FFFCF7EA` 极淡香槟 wash | `Background` |
| 名称文字 | Bold + `#FFB89A5C` 深青铜 | Active 行专用 |

选中行额外：`Active:pointerover` 守护（hover 不覆盖选中色）。

搜索框：外层 `Border Classes="SpotlightGoldFrame" CornerRadius="12" Padding="8,3"`，内层 TextBox 全透明无边框（`BorderThickness=0` + `Background=Transparent` + 8 个 FluentTheme 内部 resource key 全设 Transparent——见下）。`PlaceholderText="Search…"`（英文短文案），`FontSize=15`，`VerticalAlignment=Center` + `VerticalContentAlignment=Center`（解决文字偏上）。

### 全局 `SystemAccentColor` 改色（关键决策）

把 `SystemAccentColor` 整条色阶从**玉色**改成**金色**：

| Key | 旧（玉） | 新（金） |
|-----|---------|---------|
| `SystemAccentColor` | `#FF667731` | `#FFD9C28A` |
| `Light1` | `#FF899845` | `#FFF4E7C8` |
| `Light2` | `#FFCFDE96` | `#FFFCF7EA` |
| `Light3` | `#FFE7EDCF` | `#FFFCF7EA` |
| `Dark1` | `#FF4C5721` | `#FFB89A5C` |
| `Dark2` | `#FF4C5721` | `#FF8B6E3A` |
| `Dark3` | `#FF4C5721` | `#FF8B6E3A` |

影响范围：**所有 Fluent 控件**（TextBox focus ring、CheckBox、ToggleSwitch、ProgressBar、Button focus visual 等）。这是不可逆的全局决策，后续若要把某控件单独改回玉色，需对该控件单独覆盖。

### 最大教训：找到真正根因前不要继续叠方案

**搜索框"绿色"问题排查的弯路**：用户一直反馈"搜索框有绿色框"，我先后尝试了（按时间顺序）：
1. 改 `BorderBrush` + `BorderThickness` 在 TextBox 上 → **无效**
2. 调样式定义顺序（移到全局 `TextBox:focus` 之后）→ **无效**
3. 加外层 Border 包装 + BoxShadow 金花边 → **无效**（用户说两层框）
4. 撤销外层、覆盖 `CaretBrush`/`SelectionBrush` → **无效**
5. 删 `SpotlightSearch` 类做诊断构建（裸 TextBox）→ **无效**，用户说"还是绿色圆角矩形，一直都在"
6. 把全局 `SystemAccentColor` 改成**红色**做染色测试 → **用户反馈"变红了"** → 终于定位

**根因**：Avalonia 12 FluentTheme 的 `TextBox` 模板**内部硬画** focus visual，颜色取自 `SystemAccentColor`，**完全不看** TextBox 的 `BorderBrush` 属性。我前面所有改 `BorderBrush` 的努力都白费——FluentTheme TextBox 模板根本不读它。

**正确诊断方法**：染色测试。把可疑的 color resource 临时改成**鲜艳的红色**（不是金色、不是玉色这种容易和别的颜色混淆的色），如果效果变红，证明假设成立。这比读源码、推理特异性、调样式顺序都快得多。**下次遇到"为什么这个 UI 的颜色不对"的疑难，第一时间做染色测试。**

### 第二个教训：TextBox 要彻底无边框，得覆盖 FluentTheme 内部 resource key

光设 `BorderThickness=0` + `BorderBrush=Transparent` 不够——FluentTheme TextBox 模板内部读 `TextControlBorderBrush` / `TextControlBorderBrushPointerOver` / `TextControlBorderBrushFocused` / `TextControlBorderBrushDisabled` + 对应的 `TextControlBackground*` 共 8 个 resource key。必须用 `Style.Resources` 把这 8 个全设 Transparent，TextBox 自己的边框才彻底消失，让外层 Border 的金花边唯一可见。这是避免"两层框"的唯一办法。

### 第三个教训：publish 命令要单独执行，不要和 restart 串联

中间发现一次"改了代码但没生效"——原因是 PowerShell 把 `dotnet publish` + `Stop-Process` + `robocopy` + `Start-Process` 串在一起执行，publish 还没完成 robocopy 就跑了，复制的是旧 exe。后续诊断也发现有时 publish 因为增量缓存跳过实际编译（exe 时间戳没变）。**正确做法**：publish 命令单独执行，确认 exe 时间戳更新了，再单独跑 sync + restart。每次都要核对 `artifacts BYH.exe` 的 LastWriteTime 和运行进程的 StartTime（StartTime 必须晚于 LastWriteTime）。

### 最终改动清单

| # | 文件 | 改动 |
|---|------|------|
| 1 | `UI/Themes/IvoryJade.axaml` | **全局 `SystemAccentColor` 色阶玉→金**（7 个 Color key）。**`Border.SpotlightRow.Active`**：金花边（外金边 + 香槟缝 + 亮金内线 via BoxShadow）+ 香槟填充。**`Border.SpotlightRow.Active:pointerover`**：守护填充色。**`Border.SpotlightRow.Active TextBlock.SpotlightRowName`**：Bold + 深青铜。**新增 `Border.SpotlightGoldFrame`**：搜索框包装层的金花边（同 Active 行参数）。**`TextBox.SpotlightSearch`**：`Style.Resources` 覆盖 8 个 TextControl* key 全 Transparent + BorderThickness=0 + Background=Transparent + 深青铜 caret/selection + 中性灰 placeholder。**`TextBox.SpotlightSearch:focus` + `/template/ ContentPresenter`**：focus 状态保持透明。 |
| 2 | `UI/Views/SpotlightWindow.axaml` | 搜索框 TextBox 套进 `Border Classes="SpotlightGoldFrame" CornerRadius="12" Padding="8,3"`（只包 TextBox，不包闪电图标）。TextBox：`PlaceholderText="Search…"` + `FontSize=15` + `Padding="0,1"` + `VerticalAlignment=Center` + `VerticalContentAlignment=Center`。闪电 ⚡ `FontSize=20 → 16`。 |

### 验证

- `dotnet publish -c Release -r win-x64` — **0 警告 0 错误**，exe `BYH.exe` ≈ **27.6 MB**
- 双路径同步（robocopy /MIR）+ PowerShell `Start-Process` 重启
- 机器侧验证（用户已确认 "ok 了"）：
  - `Ctrl+Alt+Space` → Spotlight 搜索框显示**精致金花边**（外金边 + 香槟缝 + 亮金内线 + 香槟填充），单层无叠加
  - ↑↓ 切换列表选中行 → 同款金花边出现
  - 全局 Fluent 控件（设置页 CheckBox/Toggle/ProgressBar 等）统一金色 accent
  - placeholder "Search…" 英文短文案，文字垂直居中

---

## 3w. 本会话（第三十七批增量）完成的工作：R44 Ocean Eyes 取色器（P 键）

### 功能

Ocean Eyes 框选确认、工具栏出现后，按 **P**（Picker）弹出一个跟随鼠标的放大镜：
- 放大镜显示鼠标位置周围 15×15 像素，每像素放大 10 倍（150×150 box），中心有古金色 `#FFD9C28A` 虚线十字 + 10×10 矩形框，标记"将被取样的像素"
- 十字下方实时显示 `#RRGGBB`（大写 HEX，16pt bold monospace）和 `rgb(r, g, b)`（CSS 形式，11pt secondary）
- **左键任意位置点击 → 确认**：把中心像素（= 光标下像素）的 `#RRGGBB` 复制到剪贴板，工具栏状态槽显示"已复制 #RRGGBB"
- 再次按 **P** 取消（不复制）
- 按 **Esc** 关闭取色器并连同 Ocean Eyes 一起退出
- 工具栏右键重画（`ResetForRedraw`）也会顺手关掉取色器

放大镜窗口是 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`（复用 `NoActivateWindowHost`），不抢焦点，所以全局键盘 hook 继续工作。

### 关键设计决策

1. **BitBlt 管线抽出，跳过 PNG 编码**：原 `ScreenRegionCapture.CaptureAsPng` 把 BitBlt + GetDIBits + PNG 编码耦合在一起。R44 把 BitBlt→BGRA 抽成 `CaptureRawBgra`，PNG 编码只在外层 `CaptureAsPng` 包一层。新增 `SamplePixel(x,y)` 是 1×1 便捷封装。原因：放大镜每秒采样 ~30 次（DispatcherTimer 33ms tick），如果每次都做 PNG 编码（DeflateStream + Adler-32 + CRC-32）会显著吃 CPU；纯 BitBlt+GetDIBits 15×15 ≈ 微秒级。

2. **sampler 用委托注入，UI 项目不依赖 Platform.Windows**：`ColorPickerLoupe.Show(Func<(int,int,byte[]?)> sample, Action<byte,byte,byte> onPicked)` 接受一个采样闭包。闭包在 SelectionRuntime（App 项目）里实现：`GetCursorPos` P/Invoke + `ScreenRegionCapture.CaptureRawBgra(x-7, y-7, 15, 15)`。这样 UI 项目保持纯 Avalonia，不引入 Platform.Windows 依赖。

3. **ColorFormatter 抽出纯函数**：hex/RGB 格式化逻辑放在 `SelectionAssistant.Core.Input.ColorFormatter`，11 个单测覆盖（边界值 0x00/0xFF/0x0A、uppercase 校验、CSS 形式）。loupe 自己不做 hex 转换，调 `ToHexRgb(r,g,b)` / `ToRgbDecimal(r,g,b)`。这条让"放大镜逻辑"和"格式化逻辑"解耦，前者要跑 Avalonia 才能测，后者随时可测。

4. **P 键分支放在 Enter 之后、A-Z filter 之前**：避开 OCR 懒触发门（`_oceanEyesOcrDone==0` 时所有 A-Z 都被吞等 OCR）。取色器跟 OCR 完全无关——它读像素不读文本。P 也跳过 `DispatchToolbarActionKey`（PromptTemplate 查询），所以即使 P 是用户自定义功能快捷键，在 Ocean Eyes 里也优先用作 Picker（这是个 trade-off，未来可加 `ToolbarShortcutSettings.ColorPickerKey` 让 P 可配）。

5. **左键确认走 mouse hook，不走 loupe 自己的 PointerPressed**：loupe 窗口位置是 `cursor + (20, 20)`，**不在光标下**，所以用户"点击源像素确认"时点不到 loupe 本身。必须靠全局 `LowLevelMouseHook` 捕获左键 down。`OnMouseEvent` 在 `_colorPickerActive` 时短路：左键 → `Dispatcher.UIThread.Post(() => loupe.ConfirmPick())` + 吞（不触发 toolbar dismiss / 新 selection session）。loupe 自己的 PointerPressed 作为"点到 loupe 本身"的兜底，逻辑复用 ConfirmPick。

6. **GetCursorPos 放在 sampler 闭包里，不进 hook**：原 mouse hook 不投影 `WM_MOUSEMOVE`（性能考虑，60+Hz 事件会爆）。放大镜采样自己用 `DispatcherTimer` 33Hz + `GetCursorPos` P/Invoke 读光标位置——不依赖 hook 投影 move 事件，零额外 hook 开销。

### 改动清单

| # | 文件 | 改动 |
|---|------|------|
| 1 | `Platform.Windows/Capture/ScreenRegionCapture.cs` | **抽出 `CaptureRawBgra(x,y,w,h)`**（原 CaptureAsPng 的 BitBlt 管线 + 新 public 入口），CaptureAsPng 改为薄包装。**新增 `SamplePixel(x,y)`** 1×1 便捷封装。 |
| 2 | `Core/Input/ColorFormatter.cs`（新） | `ToHexRgb(r,g,b) → "#RRGGBB"` + `ToRgbDecimal(r,g,b) → "rgb(r, g, b)"`，纯函数，无状态。 |
| 3 | `UI/Views/ColorPickerLoupe.axaml(.cs)`（新） | Avalonia Window + `NoActivateWindowHost` 包装。150×150 `WriteableBitmap`（PixelFormat.Rgba8888）放大显示，每源像素 = 10×10 目标块。`DispatcherTimer` 33ms tick 调 sampler。`Screens.ScreenFromPoint` + `WorkingArea` clamp 防出屏。`Show(sample, onPicked)` / `HideLoupe()` / `ConfirmPick()`。AXAML：`Line StartPoint/EndPoint` + `Rectangle Canvas.Left/Top`（Avalonia 12 语法，不是 WPF X1/Y1）。 |
| 4 | `App/SelectionRuntime.cs` | 新字段 `_colorPickerLoupe` / `_loupeHost` / `_colorPickerActive`（Volatile）。**OnToolbarKeyPressed 加 P 分支**（Enter 之后、A-Z filter 之前；Ocean Eyes 限定；不走 OCR 路径）。**OnMouseEvent 加 loupe-active 短路**（左键 down → ConfirmPick + 吞）。**新增 `StartColorPicker` / `HideColorPicker` / `SampleCursorRegion` / `OnColorPicked` 私有方法** + `GetCursorPos` P/Invoke + `NativePoint` struct。`DismissOceanEyes` / `ResetForRedraw` / `Dispose` 全部调 `HideColorPicker`。 |
| 5 | `Core.Tests/Input/ColorFormatterTests.cs`（新） | 11 个 [Theory]/[Fact] 测试：0x00/0xFF/混合/含 0x0A/大写校验/CSS 形式。 |

### 验证

- `dotnet build -c Debug` — **0 警告 0 错误**
- `dotnet test` — **232/232 通过**（Core 156 = R44 前 145 + ColorFormatter 11；Providers 35；Windows 41）
- `dotnet publish -c Release -r win-x64` — **0 AOT/裁剪警告**
- exe 大小：**27,634,688 字节**（前 27,610,112，**增量 +24,576 / 24KB**，远低于 R44 验收预算 100KB）
- 双路径同步：`cp src/.../publish/BYH.exe artifacts/publish/win-x64-nativeuia/BYH.exe`
- PowerShell `Start-Process` 启动 → PID 43764 运行在 artifacts 路径

### 待用户机器侧验证

- `Ctrl+Alt+Q` → 框选区域 → 释放鼠标确认 → 工具栏出现
- 按 **P** → 放大镜在光标右下方弹出，跟随鼠标，HEX/RGB 实时更新
- 在桌面/不同应用元素上移动鼠标 → 放大镜正确反映像素颜色
- **左键任意位置点击** → `#RRGGBB` 进剪贴板（粘贴到记事本验证），工具栏状态槽显示"已复制 #RRGGBB"
- 再次按 **P** → 取色器关闭（不复制）
- 取色器开着时按 **Esc** → 取色器 + Ocean Eyes 一起退出
- 取色器开着时右键 → 工具栏重画（取色器跟着关）

### 注意事项 / 已知 trade-off

- **P 键不可配置**：本批次 P 硬编码在 `OnToolbarKeyPressed`。若与用户自定义功能快捷键冲突，P 优先用作 Picker。未来可加 `ToolbarShortcutSettings.ColorPickerKey`（默认 "P"，可禁用）。
- **多显示器**：放大镜位置 clamp 用 `Screens.ScreenFromPoint`，但 15×15 BitBlt 在跨显示器边界时可能取到部分黑屏（Windows 限制）——和其它截图工具一样，已知行为。
- **DRM 内容**：BitBlt 对 DRM 保护的视频流返回黑屏，取色器取到 `#000000`——和所有取色工具一样，无解。

---

## 3x. 本会话（第三十八批增量）完成的工作：R46 Ocean Eyes 贴图（T 键）

接 §3w。R44 取色器（P 键）落地后，按 P0 优先级做下一个互不依赖功能：R46 贴图。三次任务推进：UI worker 起窗体 + main agent 接 runtime + 发布同步。

### 功能（用户视角，v13 终态——scale 弹簧探索搁置，回滚 v9 侧面滑入）

1. `Ctrl+Alt+Q` → 框选区域 → 释放确认 → 工具栏弹出
2. 按 **T**（Pin）→ 当前缓存 PNG 钉成 always-on-top 浮动小窗（**从屏幕外（右下方）滑入到 pin 位置**：TranslateTransform 从 (400,100) 滑到 (0,0)，CubicEaseOut 300ms + Opacity 0→1 凘入；干净裸图，圆角无金边，贴在区域左上角 +16,16 偏移；尺寸与原截图区域物理像素 1:1）。**T 是 terminal action——贴图后 Ocean Eyes overlay（选框）+ 工具栏自动关闭**，只留贴图在桌面上。
3. 贴图窗（圆角 clip，无边框，整窗都是图像）：
   - **拖动**：左键按住任意位置拖动（3px 阈值后才开始动，避免和双击冲突）
   - **滚轮缩放**：上=×1.1 放大，下=÷1.1 缩小，**clamp 25%-500%**，左上角固定。**像素保真缩放（nearest-neighbor）+ 120ms 平滑过渡**（独立于滑入/滑出动画）
   - **双击关闭**：双击任意位置 → 贴图窗 **反向滑出到屏幕外**（TranslateTransform 0→(400,100) CubicEaseOut + 凘出，300ms）
   - **Esc 关闭**：按 Esc → 最近一次 pin 的贴图窗 **反向滑出**（LIFO 顺序，多次 Esc 逐个关）
   - **复制**：右键 → "复制图像" → PNG 进剪贴板
   - **关闭单个**：双击 / Esc / 右键 → "关闭"（都带滑出动画）
   - **关闭所有**：右键 → "关闭所有"
4. **贴图窗口与 Ocean Eyes 会话完全解耦**——T/Esc/Enter/F/J/Z/R 关掉工具栏和 overlay 后，**贴图都不动**（除非用户主动 Esc 关闭贴图）。只有以下情况销毁贴图：双击 / Esc / 右键菜单 / app 退出（runtime Dispose）
5. **多张共存**：每次 Ocean Eyes 会话按 T 都生成一个新实例（T 是 terminal action，所以同一会话只能 pin 一张；要 pin 多张需要重新 `Ctrl+Alt+Q` 触发新会话）。runtime `List<PinnedScreenshotWindow>` 追踪

### 关键设计决策

#### 1. 贴图生命周期独立于 Ocean Eyes 会话

最大设计点：**贴图不能随工具栏 Esc/Enter/F/J/Z/R 一起消失**。用户典型场景：截图一个表单 → 钉在边上 → 回到浏览器填写 → 对照贴图录入。如果 Esc 一按贴图就没了，整个功能没意义。

实现：`DismissOceanEyes` / `ResetForRedraw` **都不调用** `CloseAllPinned`。只有 `Dispose()` 关闭贴图（app 退出时）。

#### 2. PNG 直接从 `_oceanEyesPng` 缓存读取，不重新截图

`PinOceanEyesScreenshot` 直接 `_oceanEyesPng`（`ShowToolbarForOceanEyes` 时缓存的同一个 byte[]）。优点：
- 0 延迟（不需要再 BitBlt + PNG 编码）
- 0 风险（缓存 PNG 已经是"干净"版本——overlay 在 capture 时已 Hide）
- 0 OCR 依赖（T 跳过 OCR-lazy gate，和 P 一样）

#### 3. T 键的位置：P 分支后、A-Z filter 前

`OnToolbarKeyPressed` 顺序：Esc → Enter（Ocean Eyes 限定）→ P（取色器）→ **T（贴图）** → A-Z filter → F/J/Z/R/C 等。T 用 `vkPin = 0x54`，`Ocean Eyes 限定`（`_oceanEyesActive != 0`）。

#### 4. 多窗口管理用两个并行 `List<>`

```csharp
private readonly List<PinnedScreenshotWindow> _pinnedWindows = new();
private readonly List<NoActivateWindowHost> _pinnedHosts = new();
```

为什么并行不配对（`Dictionary<window,host>`）：Avalonia Window 的 `HashCode` 可能不稳（HWND 不参与默认 hash），字典可能在 GC 后错位。List + 同步索引更直接——`ClosePinned` 用 `IndexOf(window)` 拿到 idx，分别 `RemoveAt(idx)`。代价：O(n) 查找，但 n 通常 < 10（用户不会一次钉几十张），无优化必要。

#### 5. ContextMenu 自包含在 PinnedScreenshotWindow.axaml.cs

不把 ContextMenu 逻辑下沉到 runtime——窗口自己 `BuildContextMenu()` 在 ctor 里挂三个 `MenuItem`（"复制图像" / "关闭" / "关闭所有"），点击 raise `RequestCopy` / `RequestClose` / `RequestCloseAll` 事件。runtime 订阅这三个事件，调 `CopyPinnedToClipboard` / `ClosePinned` / `CloseAllPinned`。这样窗口可复用、可单测，runtime 只关心事件路由。

#### 6. 不用 Avalonia `Window.Close()`，用 `Hide()` + `Dispose()`

AXAML.cs 的 `Closing` handler 强制 `e.Cancel = true; Hide();`——防止 Avalonia 自己关掉 native window 导致 HWND 失效。runtime 的 `ClosePinned` 显式 `window.Hide()` + `window.Dispose()`（释放 `Bitmap`）。`NoActivateWindowHost` 无 `IDisposable`（它只是个 Win32 style 应用器，无句柄资源），不需要 Dispose。

#### 7. 首次 Show 必须在 `ShowPng` 之后

`SizeToContent="WidthAndHeight"` 需要图片先 decode 才能算出窗口大小。所以 `window.ShowPng(png)` 先 decode + `Show()`，再 `host.ShowAtNoActivatePoint(x, y)` 定位。如果反过来先 ShowAt 再 decode，窗口会闪一下空尺寸。

#### 8. （v2）默认尺寸自动放大的根因 + ApplyScale DPI 修正

**用户反馈："贴图默认大小和我实际截取的区域大小不一样，它会自己放大"。** 根因：Avalonia `Bitmap` 默认 96 DPI，原生 `Stretch="None"` 不设 Image 的 Width/Height 时，Avalonia 会把图像渲染成 `pixelSize × (屏幕 DPI / 96)` 物理像素。在 150% 缩放的显示器下，贴图会放大 1.5 倍，占用的屏幕空间比原截图大很多。

**修复**：`ApplyScale` 方法显式设 `ScreenshotImage.Width/Height = basePixelSize × _scale / RenderScaling`。`RenderScaling` 是 `Window` 的属性（`TopLevel.RenderScaling`），返回屏幕 DPR（1.0 / 1.25 / 1.5 / 2.0 …）。除以它抵消 Avalonia 的自动 DPI 缩放——`_scale==1.0` 时贴图占用的物理像素 = 原截图时的物理像素（用户期望的"等大"）。

**`Opened` 事件兜底**：`RenderScaling` 在窗口 HWND 完全创建后才 finalized。`ShowPng` 在 `Show()` 前调 `ApplyScale` 可能拿到默认值 1.0（即使屏幕是 1.5）。所以 ctor 里挂 `Opened += ApplyScale` 在窗口首次打开后再调一次，确保 DPR 正确。

#### 9. （v2）双击关闭 + 滚轮缩放 + 拖动 3px 阈值协同

**用户反馈："双击关闭" + "添加滚轮缩放"。** v2 实现：

- **双击关闭**：`ScreenshotImage.DoubleTapped += (_, _) => RequestClose?.Invoke()`。Avalonia 内置 `DoubleTapped` 是 `InputElement` 的 RoutedEvent，自动处理双击时间窗口（默认 ~500ms）+ 距离阈值。不用自己 track 时间戳。**⚠️ v2 此方案在用户机器上不工作——见 §11 v3 修复。**
- **滚轮缩放**：`PointerWheelChanged` 检查 `e.Delta.Y > 0`（上滚）→ `×ScaleStep`；下滚 → `÷ScaleStep`；clamp [0.1, 5.0]；左上角固定锚点（窗口 `Position` 不动，`SizeToContent` 自动重排）。`e.Handled = true` 防止事件冒泡。**⚠️ v2 用默认 bilinear 插值，缩小时平均相邻像素导致模糊+丢像素——见 §10 v3 修复。**
- **拖动 3px 阈值**：`OnPointerPressed` 设 `_isDragging=true` 但 `_dragCommitted=false`；`OnPointerMoved` 检查位移 ≥ 3px 才真正开始更新 `Position`。3px 阈值让"按下不动+抬起"算作点击，"按下+移动≥3px"才算拖动。

#### 10. （v3）像素保真缩放：`BitmapInterpolationMode.None`

**用户反馈："你这个缩放是损失图像数据的缩放，而不是原图像缩放"。** 根因：Avalonia `Image` 默认 `RenderOptions.BitmapInterpolationMode` 是 `LowQuality` 或 `Default`（取决于后端，Skia 是 bilinear），缩小时会平均相邻源像素——视觉上模糊，像素数据被混合丢失。用户期望"原图像缩放"（保留源像素，不混合）。

**修复**：ctor 里加一行 `RenderOptions.SetBitmapInterpolationMode(ScreenshotImage, BitmapInterpolationMode.None)`。Avalonia 12.1 的 `BitmapInterpolationMode` 枚举：`None | LowQuality | MediumQuality | HighQuality`。Skia 后端的 `None` = nearest-neighbor：
- **放大**：每个源像素被复制成 N×N 块（看到方块像素，像复古游戏放大）
- **缩小**：每 N 个源像素只保留一个，其余丢弃（不平均、不模糊）
- **原始 PNG byte[] 不变**：贴图存的还是完整原 PNG，缩放只影响显示，复制到剪贴板的还是原图。

这正是用户要的"原图像缩放"——源像素数据完整保留，显示层只是 nearest-neighbor 重采样。

#### 11. （v3）双击检测改手动时间戳 + 距离，弃用 Avalonia `DoubleTapped`

**用户反馈："依然没有双击关闭功能"。** 根因分析：v2 用的 `ScreenshotImage.DoubleTapped` 是 Avalonia `InputElement` 的内置 RoutedEvent，理论上应该自动识别双击。但实际不工作，可能原因：
1. `OnPointerPressed` 里 `e.Pointer.Capture(this)` 抢占了 pointer，干扰 Avalonia 的 gesture recognizer（它需要看到完整的 pressed/released 序列）
2. no-activate 窗口（`WS_EX_NOACTIVATE`）的 input pipeline 和普通窗口不同，gesture 系统可能不响应
3. `Image` 控件的 hit-test 行为 + RoutedEvent 冒泡可能被外层 window 截断

**修复**：完全弃用 `DoubleTapped`，在 `OnPointerReleased` 手动检测：
```csharp
// 字段：_lastClickTicks (long) + _lastClickScreen (PixelPoint)
// 常量：DoubleClickMs = 500, DoubleClickPx = 8.0
if (_lastClickTicks != 0 &&
    (now - _lastClickTicks) <= DoubleClickMs &&
    Math.Abs(pos.X - _lastClickScreen.X) <= DoubleClickPx &&
    Math.Abs(pos.Y - _lastClickScreen.Y) <= DoubleClickPx)
{
    _lastClickTicks = 0;
    RequestClose?.Invoke();
}
else
{
    _lastClickTicks = now;
    _lastClickScreen = pos;
}
```

参数匹配 Windows 默认：`GetDoubleClickTime()` ≈ 500ms，`GetSystemMetrics(SM_CXDOUBLECLK)` ≈ 4px（用 8px 稍宽容）。drags ≥ 3px 不算 click 不参与双击判定（`_dragCommitted` flag 检查），避免"拖一下再点"误触发。

**教训**：Avalonia 的 `DoubleTapped` gesture 在 no-activate 窗口 + PointerCapture 激活时不可靠。下次在 no-activate 窗口上做手势识别，**优先手动时间戳 + 距离检测**，不要相信内置 gesture。

#### 12. （v4）根因终于定位：Avalonia `Stretch="None"` 是裁剪不是缩放

**用户反馈**（第三轮）："默认大小下，它只展示了中间少部分的内容，只有将它放大以后，才能展示完我所截的全部内容。也就是说，它整张图片比原生截的图片要大了很多。虽然默认窗口和截的窗口一样，但到时候是直接裁剪的。"——用户显示器 **175% 缩放**。

**v2/v3 错在哪里**：v2 加 `ApplyScale` 设 `ScreenshotImage.Width/Height = (pixelSize × _scale) / RenderScaling`，意图是"用 Image 的 Width/Height 控制图像显示尺寸"。但 Avalonia `Image.Stretch="None"` 的语义**不是**"按指定 Width/Height 缩放图像"，而是"**按图像自然尺寸渲染，超出控件 Width/Height 边界的部分裁剪掉**"。我设的 Width/Height 只是改了裁剪框大小，没动图像本身。所以：
- 175% DPI 下，bitmap 自然 Size = `PixelSize` 逻辑 DIP（PNG 无 pHYs 块，Bitmap 默认 96 DPI）
- `Stretch="None"` 让图像渲染成 `PixelSize × 1.75` 物理像素
- 但窗口尺寸只有 `PixelSize` 物理像素（裁剪框 = (PixelSize × 1.0) / 1.75 × 1.75 = PixelSize）
- 用户看到的是图像左上角 1/1.75 ≈ 57%

**v4 真正的修复**：放弃手动设 Width/Height，改用 Avalonia 的 `LayoutTransformControl` 包 `<Image>`。LayoutTransformControl 接受一个 `Transform`（这里用 `ScaleTransform`），**真正重新 measure 子控件**（不是裁剪）。`ApplyScale` 设：

```csharp
_scaleTransform.ScaleX = _userScale / RenderScaling;  // 抵消 DPI 缩放
_scaleTransform.ScaleY = _userScale / RenderScaling;
```

- `_userScale=1.0` 时，`ScaleX = 1/RenderScaling = 1/1.75 ≈ 0.571`
- LayoutTransformControl 让 Image 在 `PixelSize × 0.571` 逻辑 DIP 上 measure
- 渲染到 175% 物理屏幕：`PixelSize × 0.571 × 1.75 = PixelSize` 物理像素 ✓ = 原截图区域
- SizeToContent=WidthAndHeight 自动跟 LayoutTransformControl 的实际 measure 收紧窗口

**核心教训（永久记录）**：Avalonia `Image` 控件的 `Stretch="None"` **不是缩放控制**。要缩放 Avalonia Image：
- ❌ 错：设 `Image.Width/Height` + `Stretch="None"`（只裁剪不缩放）
- ✅ 对：用 `LayoutTransformControl` 包 Image + `ScaleTransform`（重新 measure + SizeToContent 跟随）
- ✅ 对：用 `Image.Stretch="Fill"` 或 `"Uniform"` + 显式 `Width/Height`（Stretch 才是缩放控制）
- ✅ 对：用 `Image.RenderTransform = ScaleTransform`（不重新 measure，SizeToContent 不会跟，窗口要手动改尺寸）

下次做"图片显示尺寸可控 + 窗口 SizeToContent 跟随"的功能，**默认 LayoutTransformControl**。

#### 13. （v5）圆角古金边框：Border.CornerRadius 自动 clip 子内容

**用户反馈："添加圆角边框"。** 实现：在 LayoutTransformControl 外包一层 `<Border>`：

```xml
<Border x:Name="Frame"
        Background="Transparent"
        BorderBrush="#FFD9C28A"
        BorderThickness="1"
        CornerRadius="8"
        Padding="0"
        ClipToBounds="True"
        BoxShadow="0 4 12 0 #66000000">
  <LayoutTransformControl x:Name="Scaler">
    <Image x:Name="ScreenshotImage" Stretch="None" />
  </LayoutTransformControl>
</Border>
```

关键点：
- **`CornerRadius="8"`**：Border 自动把子内容（Image）也按 8px 圆角 clip。Avalonia Border 的圆角不仅作用于边框线，还作用于整个内容区域的 clip 几何——所以图像四个角也是圆的。
- **`ClipToBounds="True"`**：保险——确保缩放后图像超出 Border 边界的部分（虽然 LayoutTransformControl 会重新 measure，但浮点误差可能让图像略大于 Border）被 clip 掉。
- **`BoxShadow="0 4 12 0 #66000000"`**：40% 黑色阴影，offset (0,4) blur 12——给贴图一点漂浮感（跟项目其他 FloatingSurface 一致）。阴影是 Border 外侧画的，不受 CornerRadius 内 clip 影响。
- **Window `Background="Transparent"` + `ExtendClientAreaToDecorationsHint=True`**：窗口本身是矩形，但透明背景让 Border 圆角外的方角区域透到桌面。窗口外的部分用户看到的是桌面，不是窗口方角。
- **`BorderThickness="1"`**：1px 古金色边框线，跟 ColorPickerLoupe 的十字线 / ToolbarWindow 的金花边一致（Ivory Jade 主题）。

注意 `SizeToContent=WidthAndHeight` 仍然有效——窗口跟随 Border 的尺寸，Border 跟随 LayoutTransformControl 的 measure（LayoutTransformControl 跟随 ScaleTransform + Image 自然尺寸）。整条链 DPI 正确 + 圆角正确。

#### 14. （v5）T 变 terminal action：贴图后 DismissOceanEyes

**用户反馈："按 T 贴图以后自动关闭...选框。我已经触发了动作，选框就可以关闭了"。** 用户语义：贴图是确认动作，框选使命完成，可以退出 Ocean Eyes 会话。

实现：T 键分支加 `DismissOceanEyes()` 调用：

```csharp
if (vkCode == vkPin && Volatile.Read(ref _oceanEyesActive) != 0)
{
    try
    {
        PinOceanEyesScreenshot();
        DismissOceanEyes();   // v5: 关工具栏 + overlay + 清状态
    }
    catch (Exception exception) { ... DismissOceanEyes(); }
    return true;
}
```

**关键：贴图窗不会被 DismissOceanEyes 关掉**——`DismissOceanEyes` 的代码里**没有**任何 `_pinnedWindows` 操作（之前的 §3x trade-off 明确设计：贴图独立于 Ocean Eyes 会话）。所以 T 之后：overlay 关、工具栏关、Ocean Eyes 状态清零，**贴图窗保留在桌面上**。

**竞态安全**：`PinOceanEyesScreenshot` 在 hook 线程同步读取 `_oceanEyesPng` 到局部变量 `png` + `_oceanEyesRect` 到 `anchorX/Y`，然后 `Dispatcher.UIThread.Post` 闭包捕获这些局部变量。`DismissOceanEyes()` 在 `PinOceanEyesScreenshot` 返回后立即在 hook 线程执行，把 `_oceanEyesPng = null` + 清状态 + UI 线程 Post 关工具栏/overlay。两个 UI-thread Post 的执行顺序由 Avalonia 调度，但即使 dismiss 的 Post 先跑，pin 的 Post 后跑——pin 闭包用的是局部 `png` 变量，不依赖 `_oceanEyesPng`，所以 decode 正常。

**v5 行为变化**：
- 之前（v1-v4）：T 后工具栏仍在，可以继续 F/J/Z/R/Enter/Esc
- 现在（v5）：T 后 Ocean Eyes 完全退出，只留贴图窗。要 pin 多张需要重新 `Ctrl+Alt+Q` 触发新 Ocean Eyes 会话。

#### 15. （v6）Esc 关闭贴图：全局 keyboard hook 路由 + hook 保活

**用户反馈："添加 esc 关闭"。** 挑战：贴图窗是 `WS_EX_NOACTIVATE`（永不抢焦点），所以窗口自己的 `KeyDown` 永远收不到 Esc——Esc 只能通过**全局低层 keyboard hook**（`WH_KEYBOARD_LL`）捕获。

**实现**：`OnToolbarKeyPressed` 顶部加新 Esc 分支（在原 Ocean Eyes Esc 分支之前）：

```csharp
const int vkEscape = 0x1B;
if (vkCode == vkEscape &&
    Volatile.Read(ref _oceanEyesActive) == 0 &&  // 非 Ocean Eyes
    _pinnedWindows.Count > 0)                     // 有贴图
{
    Dispatcher.UIThread.Post(() =>
    {
        if (_pinnedWindows.Count > 0)
        {
            // LIFO：关最后一个（最近的 = 最上层）
            var top = _pinnedWindows[_pinnedWindows.Count - 1];
            ClosePinned(top);
        }
    });
    return true;
}
```

**关键：hook 必须保活**。问题在于 `DismissOceanEyes`（T 之后调用）会 `_keyboardHook.SetEnabled(false)`，hook 关了之后 Esc 也收不到。三个改动协同：

1. **`DismissOceanEyes` 不再无条件禁用 hook**：检查 `_pinnedWindows.Count > 0`，若有贴图则保持 hook 启用。Ocean Eyes 状态（`_oceanEyesActive`）已经被清 0，所以 `OnToolbarKeyPressed` 顶部的新 Esc 分支会命中（非 Ocean Eyes + 有贴图）。
2. **T 分支末尾显式 `SetEnabled(true)`**：双保险——即使 `DismissOceanEyes` 的保活判断因为时机问题失败（pin 的 UI-thread Post 还没把 window 加进 `_pinnedWindows`），T 分支自己再启用一次。
3. **`ClosePinned` / `CloseAllPinned` 在列表空 + 非 Ocean Eyes 时禁用 hook**：贴图全关了就没必要继续监听全局按键，省 CPU + 避免干扰其他应用。

**Esc 的语义层级**（LIFO 关闭栈）：
- 多张贴图 + Ocean Eyes 都在：第一次 Esc 关 Ocean Eyes（旧分支命中，`_oceanEyesActive != 0`），第二次 Esc 关最后一个贴图（新分支命中），第三次关倒数第二个……
- 只有贴图：Esc 直接关最后一个，多次 Esc 逐个关。

**为什么不 FIFO**：用户最后 pin 的通常是最关心的（屏幕上最显眼位置），先关它符合直觉。LIFO 也是其他应用（Photoshop 浮窗、Snipping Tool pin）的通用行为。

#### 16. （v6）MinScale 0.1 → 0.25：避免缩到看不见

**用户反馈："最小化有限度"。** 原 `MinScale=0.1`（10%）让滚轮可以缩到极小——1920×1080 截图能缩到 192×108，基本看不见也拖不准。改成 **`MinScale=0.25`**（25%）：1920×1080 → 480×270，仍清晰可读可拖。

数值考量：太小（<20%）窗口接近光标大小，点击精度要求极高；太大（>40%）缩放体验受限，大截图没法真正"变小"。25% 是 Photoshop / Snagit 等工具的常见下限。MaxScale 保持 5.0（500%）不变——放大看细节是合理用例。

#### 17. （v6）去金边只留圆角：Border 还在但视觉无边框

**用户反馈："不需要边框保持干净"。** v5 加了 `<Border BorderBrush="#FFD9C28A" BorderThickness="1">` 古金边框。v6 用户要更干净。但**完全删掉 Border** 会让图像四角变回方角（生硬），所以保留 Border 但视觉无边框：

```xml
<Border x:Name="Frame"
        Background="Transparent"
        BorderBrush="Transparent"     <!-- v6: 透明，不可见 -->
        BorderThickness="0"           <!-- v6: 0，无边框线 -->
        CornerRadius="8"              <!-- 保留圆角 clip 几何 -->
        Padding="0"
        ClipToBounds="True">
  <LayoutTransformControl x:Name="Scaler">
    <Image x:Name="ScreenshotImage" Stretch="None" />
  </LayoutTransformControl>
</Border>
```

Border 的存在只为提供 `CornerRadius` 的 clip 几何（让 Image 四角圆滑）。`BorderBrush=Transparent` + `BorderThickness=0` 让边框线不可见。`BoxShadow` 也去掉（v6 不再要阴影）。Window `Background=Transparent` 让圆角外的方角区域透到桌面。

#### 18. （v6 Esc bug 修复）`onToolbarHidden` 回调是隐藏的 hook 杀手

**用户反馈："ESC 没有用"。** v6 加了 Esc 路由 + 三处 hook 保活，但用户测试 Esc 仍然无效。**深度日志定位根因**（在 `LowLevelKeyboardHook.SetEnabled` 加 DIAG 日志 + 在 hook callback 加 DIAG 日志）：

```
07:22:55.089  DIAG hook cb vk=0x54  (T 键被 hook 看到 ✓)
07:22:55.090  Pin screenshot: T → spawn pinned window + dismiss Ocean Eyes.
07:22:55.091  DIAG SetEnabled 1->0  (DismissOceanEyes 禁用 hook)
07:22:55.094  DIAG SetEnabled 0->1  (T 分支 SetEnabled(true) ✓)
07:22:55.095  Pin T done: hook re-armed, pinned count = 0
07:22:55.119  Pinned screenshot (23560 bytes)  ← UI-thread Post 跑完，窗口创建
07:22:55.144  DIAG SetEnabled 1->0  ← ❌ 又被禁用了！
```

**罪魁祸首**：`DismissOceanEyes` 的 UI-thread Post 里有 `_windowHost.Hide()`。`_windowHost` 是 `ToolbarSessionView`，它的 ctor 接 `onToolbarHidden` 回调（`SelectionRuntime.cs:166`），回调里**无条件** `_keyboardHook.SetEnabled(false)`。这个回调在 `_windowHost.Hide()` 触发时执行——而 `Hide()` 发生在 UI-thread Post 里，**晚于** T 分支的 `SetEnabled(true)`（在 hook 线程同步执行）。所以 hook 被 T 分支启用后又被 onToolbarHidden 关闭，Esc 收不到。

**修复**：`onToolbarHidden` 加 `_pinnedWindows.Count == 0` 守护：
```csharp
onToolbarHidden: () =>
{
    if (_pinnedWindows.Count == 0)
    {
        _keyboardHook.SetEnabled(false);
    }
}
```
同样的守护加到 `ResetForRedraw` 和 `StopKeyboardHookQuiet`（另外两个禁用 hook 的路径）。

**教训（永久）**：低层 hook 的启用/禁用有**多个调用点**，加新功能（Esc 路由依赖 hook 保活）时必须审计**所有**禁用点加守护。光看主路径（DismissOceanEyes + T 分支）不够——还有回调路径（onToolbarHidden）。诊断这种"启用后又被神秘禁用"的问题，**在 SetEnabled 内部加状态变化日志**是最快定位方法。

#### 19. （v7）动画：凘入凘出 + 滚轮缩放平滑过渡

**用户反馈："添加一下动画"。** 三种动画：

**(m) 出现动画（凘入）**：AXAML 给 Window 加初始 `Opacity="0"` + `<DoubleTransition Property="Opacity" Duration="0:0:0.15"/>`。ctor 里挂 `Opened += (_, _) => Opacity = 1.0;`——窗口打开后设 `Opacity=1`，DoubleTransition 自动从 0 插值到 1（150ms ease-out）。用户看到贴图窗平滑凘入而不是突然出现。

**(n) 关闭动画（凘出）**：新增 `PinnedScreenshotWindow.AnimateOutAsync()` 方法：
```csharp
public async Task AnimateOutAsync()
{
    _animatingOut = true;       // 防 Closing handler double-Hide
    Opacity = 0.0;              // DoubleTransition 自动 150ms 凘出
    await Task.Delay(180);      // 等过渡完成（180ms > 150ms 留余量）
}
```
`ClosePinned` 改 `async`，在 `Hide+Dispose` 前 `await window.AnimateOutAsync()`：
```csharp
await window.AnimateOutAsync();
window.Hide();
window.Dispose();
```
**重入保护**：窗口从 `_pinnedWindows` 列表移除在 `await` **之前**——快速二次 Esc / 双击不会重复关闭同一个窗口（第二次 Esc 时列表已经空了或只剩别的窗口）。

**(o) 滚轮缩放平滑过渡**：`ScaleTransform` 实例的 `Transitions` 加 DoubleTransition：
```csharp
_scaleTransform.Transitions = new Transitions
{
    new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(120) },
    new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(120) },
};
```
滚轮改 `_userScale` 后 `ApplyScale` 设新 `ScaleX/Y`，Avalonia 自动 120ms 插值。**关键**：Transitions 挂在 `ScaleTransform` 实例上（它是 `Animatable` 的子类），**不是**挂在 `LayoutTransformControl.LayoutTransform`——`LayoutTransform` 属性是 `Transform` 类型，换整个 Transform 对象不能 DoubleTransition；只改 `ScaleTransform` 的 `ScaleX`/`ScaleY` 属性可以。

**为什么不用 `RenderTransform`**：`RenderTransform` 不重新 measure，SizeToContent 不会跟随，窗口尺寸不变。我们 v4 的核心修复就是用 `LayoutTransformControl` + `LayoutTransform`——动画也得挂在这条链上。

**Avalonia Transitions vs Animation API**：Transitions 是属性变化自动插值（声明式，简单）；Animation 是关键帧序列（命令式，复杂）。这里都是简单的 0→1 / 1→0 / scale A→B，Transitions 足够。

#### 20. （v8）Mac 风格弹性放大弹出：两层 ScaleTransform 互不干扰

**用户反馈："弹出可以改为 mac 类似的放大弹出吗"。** v7 是纯 Opacity 凘入（透明渐显），用户要 Mac 那种从中心弹性放大的效果。

**v7 §19 里"为什么不用 RenderTransform"的结论需要修正**：v7 说 RenderTransform 不重新 measure、SizeToContent 不跟随——这对**功能缩放**（滚轮改图像大小）仍然成立，必须用 LayoutTransformControl。但**动画缩放**（出现/关闭时的弹性效果）不需要重新 measure——窗口尺寸不变，只是渲染时缩放。所以 v8 用 `Window.RenderTransform` 做动画，**独立于** LayoutTransformControl 的功能缩放。

**两层 ScaleTransform 架构**：

| 层 | 位置 | 用途 | 缓动 | 时长 |
|----|------|------|------|------|
| `_popScale` | `Window.RenderTransform` | 出现/关闭弹性动画（scale 0.85↔1.0） | `BackEaseOut`（overshooting cubic，Mac 回弹感） | 250ms |
| `_scaleTransform` | `LayoutTransformControl.LayoutTransform` | 滚轮功能缩放（`_userScale / RenderScaling`） | 默认 ease | 120ms |

两层完全独立——弹出动画跑的时候滚轮也能改尺寸（虽然实际场景用户不会同时做）。`_popScale` 跑在 Window 渲染层（GPU 合成，cheap），不影响 layout；`_scaleTransform` 跑在 layout 层（CPU measure，触发 SizeToContent 重排）。

**实现**：

AXAML（Window 加 RenderTransform + RenderTransformOrigin）：
```xml
<Window ... Opacity="0" RenderTransformOrigin="0.5,0.5">
  <Window.RenderTransform>
    <ScaleTransform ScaleX="0.85" ScaleY="0.85" />
  </Window.RenderTransform>
  ...
</Window>
```

code-behind ctor（拿 RenderTransform + 加 Transitions）：
```csharp
if (RenderTransform is ScaleTransform popScale)
{
    _popScale = popScale;
}
_popScale.Transitions = new Transitions
{
    new DoubleTransition
    {
        Property = ScaleTransform.ScaleXProperty,
        Duration = TimeSpan.FromMilliseconds(250),
        Easing = new BackEaseOut(),  // macOS dock 那种轻微回弹
    },
    // ScaleY 同上
};
```

`Opened` 触发弹出：
```csharp
Opened += (_, _) =>
{
    ApplyScale();
    _popScale.ScaleX = 1.0;  // 从 0.85 BackEaseOut 弹到 1.0
    _popScale.ScaleY = 1.0;
    Opacity = 1.0;           // 同时 Opacity 凘入
};
```

`AnimateOutAsync` 反向收缩：
```csharp
_popScale.ScaleX = 0.85;  // 从 1.0 BackEaseOut 收到 0.85
_popScale.ScaleY = 0.85;
Opacity = 0.0;
await Task.Delay(280);     // 等 250ms 弹性动画 + 余量
```

**BackEaseOut vs 其他缓动**：Avalonia 提供 `CubicEaseOut`（无回弹，快速减速）/ `BackEaseOut`（轻微 overshoot，弹性感）/ `ElasticEase`（明显多次回弹，太夸张）/ `BounceEaseOut`（落地反弹，不合适）。macOS 窗口放大的感觉最贴近 `BackEaseOut`——一次轻微 overshoot 后稳定，不像 spring 那样反复振荡。

**踩坑**：AXAML 里 `<ScaleTransform x:Name="PopScale">` 的 `x:Name` **不会**生成 code-behind 字段——Avalonia codegen 只为 Window **content children** 生成字段，不为 Window 属性级别的元素（如 `Window.RenderTransform`）生成。所以 code-behind 里用 `if (RenderTransform is ScaleTransform popScale) _popScale = popScale;` 手动拿引用。

#### 21. （v9）改成"从屏幕外滑入"：TranslateTransform 位置动画

**用户反馈："怎么没有变化?还是从侧面弹出"**——v8 的 BackEaseOut scale 0.85→1.0 用户感觉不对（scale 动画太微妙，且不是用户期望的"从屏幕外滑入"语义）。经询问确认用户要的是 **macOS 截图 pin 的真实行为：贴图从屏幕外滑入到 pin 位置**。

**v8 的教训**：用户说"Mac 类似的放大弹出"时，我猜是 scale 弹性放大（dock 图标放大那种）。但用户实际指的是**位置滑入**（窗口从屏幕外飞入）。下次遇到模糊的动画描述，**先问清是位置动画还是缩放动画**，不要猜。

**v9 实现**：把 `Window.RenderTransform` 从 ScaleTransform 换成 TranslateTransform：

```xml
<Window.RenderTransform>
  <TranslateTransform X="400" Y="100" />
</Window.RenderTransform>
```

AXAML 初始 `X=400 Y=100`——窗口渲染时偏移到右下方"屏幕外"（实际 Position 不变，只是渲染偏移）。`Opened` 设 `X=0 Y=0` 滑入。

ctor 里挂 Transitions（CubicEaseOut 300ms）：
```csharp
_slide.Transitions = new Transitions
{
    new DoubleTransition
    {
        Property = TranslateTransform.XProperty,
        Duration = TimeSpan.FromMilliseconds(300),
        Easing = new CubicEaseOut(),
    },
    // Y 同上
};
```

`AnimateOutAsync` 反向滑出：
```csharp
_slide.X = 400;
_slide.Y = 100;
Opacity = 0.0;
await Task.Delay(330);  // 300ms 滑出 + 余量
```

**CubicEaseOut vs BackEaseOut**：v8 用 BackEaseOut（scale 弹性，overshooting cubic）适合"放大弹出"；v9 改 CubicEaseOut（快速减速，无 overshoot）适合"滑入"——窗口快速从屏幕外滑入然后平稳停下，像 macOS 的 sheet/dialog 滑入。

**字段重命名**：`_popScale`（ScaleTransform）→ `_slide`（TranslateTransform）。语义跟随动画类型变化。

**为什么固定偏移 (400, 100) 而不是真的"屏幕外"**：计算真正的屏幕边缘需要窗口尺寸 + 屏幕尺寸 + 目标位置，复杂且容易出 bug。固定 400px 右 + 100px 下的偏移对常见屏幕尺寸（1920×1080 / 2560×1440）都足够让窗口起始位置在视觉上的"屏幕外"或"屏幕边缘"，效果接近。如果未来用户反馈"能看到起始位置不在屏幕外"，可以改成根据 `Screens.ScreenFromWindow` 的 `Bounds` 动态计算。

#### 22. （v8-v13）动画探索完整日志：Mac 弹簧效果搁置 + 未来修复思路

**用户需求**：v7 是纯 Opacity 凇入，用户反馈"弹出可以改为 mac 类似的放大弹出吗"。Mac 的真实效果是：**从正中间放大到 ~120%（过冲），再缩回 100%**——典型的单次过冲弹簧曲线。

**尝试 1：v8 — ScaleTransform 0.85→1.0 BackEaseOut**（250ms）
- 效果：太微妙（只有 15% 大小变化），用户感觉"没变化"。
- 用户反馈："怎么没有变化?还是从侧面弹出"。
- 我**误解**为"要侧面滑入"，改为 TranslateTransform → v9。

**尝试 2：v9 — TranslateTransform (400,100)→(0,0) CubicEaseOut**（300ms）
- 效果：从右下方屏幕外滑入到目标位置（侧边滑入）。
- 用户反馈：这不是想要的 Mac 效果（但用户先用着这个，后续再改）。
- **v13 回滚到这个版本作为暂定方案。**

**尝试 3：v10 — ScaleTransform 0.3→1.0 BackEaseOut**（350ms，起始值更小 0.3 vs v8 的 0.85）
- 效果：从 30% 放大到 100%，BackEaseOut 过冲。
- 用户反馈："抖了好几下"（BackEaseOut 过冲在渲染中看起来像多次振荡）+"从左上角弹，不是从正中间"。
- **"从左上角"是核心问题**——`Window.RenderTransformOrigin="0.5,0.5"` 在 AXAML 上设的，在 `ExtendClientAreaToDecorationsHint=True` 窗口上不生效。

**尝试 4：v11 — ScaleTransform 改 CubicEaseOut + 移到 Border.RenderTransform**（250ms）
- 修改：缓动从 BackEaseOut 改 CubicEaseOut（消除"抖"）；ScaleTransform 从 Window.RenderTransform 移到内部 Frame Border（Border 是普通 Control，理论上 RenderTransformOrigin 在普通 Control 上行为正确）。
- 用户反馈："没有变化，依然是从左上角开始的动画抖动"。
- **Border 上的 RenderTransformOrigin 也不生效**——说明问题不是 Window vs Control，而是 Avalonia 12 的 RenderTransformOrigin 在 AXAML 属性上整体不可靠。

**尝试 5：v12 — Avalonia 关键帧 Animation + 显式 RelativePoint**
- 修改：用 Animation 关键帧 API 做 Mac 弹簧曲线（0.5→1.15→1.0，3 个关键帧 400ms）；code-behind 显式 `Frame.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)`。
- 用户反馈："算了先就这样吧，你先记录"。
- **ScaleTransform RenderTransformOrigin 问题始终未解。**

**核心未解问题**：Avalonia 12 的 `RenderTransformOrigin` 在代码里和 AXAML 里都设了 center (0.5, 0.5)，但 ScaleTransform 始终从左上角放大。可能的原因：
1. `ExtendClientAreaToDecorationsHint=True` 改变了客户区坐标系，导致 RenderTransformOrigin 计算错误。
2. Avalonia 12 的 ScaleTransform RenderTransformOrigin 在 `TopLevel`/`Window` 子树中存在 bug。
3. `ClipToBounds=True` 的 Border 可能影响了 transform origin 的计算。

**未来修复思路**（搁置，以后再说）：
1. **自定义 Easing**：不用 `RenderTransformOrigin`，改为写一个自定义 `Easing` 类，在 `Ease(t)` 里自己算 translate-scale-translate 组合。Easing 可以访问 t（0-1），返回的值直接就是 ScaleTransform.ScaleX/Y 的目标值。配合显式在关键帧里设中间 TranslateTransform 偏移（先平移到中心、缩放、再平移回去）。复杂但可靠。
2. **用 `MatrixTransform` 显式构造缩放矩阵**：`MatrixTransform(matrix)` 里直接写 `translate(-w/2, -h/2) × scale × translate(w/2, h/2)`。但窗口尺寸是动态的（SizeToContent），不能硬编码。可以在 Opened 时读取 `Frame.Bounds` 算 w/h。
3. **用两层 LayoutTransformControl**：外层做弹簧动画（LayoutTransform 总是相对自身 measure 结果缩放，天然从中心），内层做滚轮功能缩放。嵌套两个 LayoutTransformControl 的风险：外层尺寸变化触发内层重新 measure，可能导致抖动。需要实测。
4. **等 Avalonia 更新**：如果这是 Avalonia 12 的 RenderTransformOrigin bug，后续版本可能修复。关注 Avalonia GitHub issues。

---

### 改动清单（v13 终态）

| # | 文件 | 改动 |
|---|------|------|
| 1 | `UI/Views/PinnedScreenshotWindow.axaml`（新，~32 行） | Avalonia Window + **v9：`Opacity="0"` + `<Window.RenderTransform><TranslateTransform X="400" Y="100"/></Window.RenderTransform>`**（从屏幕外右下方滑入，初始渲染偏移）。`<Window.Transitions>` 里 `DoubleTransition Property="Opacity" Duration="0:0:0.25"`（凘入凘出配合）。`<Border CornerRadius="8" BorderBrush="Transparent" BorderThickness="0" ClipToBounds="True">` 包 `<LayoutTransformControl x:Name="Scaler">` 包 `<Image x:Name="ScreenshotImage" Stretch="None"/>`（v6 干净无边框只圆角；v4 LayoutTransformControl 真正重新 measure）。 |
| 2 | `UI/Views/PinnedScreenshotWindow.axaml.cs`（v9 改 ctor + AnimateOutAsync） | `public partial class PinnedScreenshotWindow : Window, IDisposable`。**v9 改动**：(a) 字段 `_popScale`（ScaleTransform）→ **`_slide`（TranslateTransform）**；(b) ctor 里 `if (RenderTransform is TranslateTransform slide) _slide = slide;` 拿引用；(c) ctor 里给 `_slide.Transitions` 加 `DoubleTransition` 300ms + **`CubicEaseOut`** 缓动（快速减速，适合滑入语义）；(d) `Opened` handler 改成 `ApplyScale(); _slide.X=0; _slide.Y=0; Opacity=1.0;`（从 (400,100) 滑到 (0,0)）；(e) `AnimateOutAsync` 改成 `_slide.X=400; _slide.Y=100; Opacity=0; await Task.Delay(330);`（反向滑出）。v7 保留：`_scaleTransform.Transitions`（滚轮缩放 120ms 平滑）。v6 保留：`MinScale=0.25`。v4 保留：`ApplyScale`。v3 保留：手动双击检测、`BitmapInterpolationMode.None`。v1 保留：`ShowPng` / `Dispose` / `BuildContextMenu` / 3 个事件 / `NativeHandle` / `PngBytes`。 |
| 3 | `App/SelectionRuntime.cs`（v7 落地，v8/v9 0 行改动） | `ClosePinned(window)` 仍 `await window.AnimateOutAsync()`——v9 AnimateOutAsync 内部改成滑出，runtime 不感知（公共 API 签名不变）。v6 Esc bug 修复保留：`onToolbarHidden` / `ResetForRedraw` / `StopKeyboardHookQuiet` 都加 `_pinnedWindows.Count == 0` 守护。v6 保留：`OnToolbarKeyPressed` 顶部 Esc 分支、`DismissOceanEyes` 条件禁用、T 分支 `SetEnabled(true)`、`CloseAllPinned` 列表空禁用。v5 保留：T terminal action。v1 保留：`PinOceanEyesScreenshot` / `CopyPinnedToClipboard` / `CloseAllPinned` / `Dispose` 贴图清理。 |

### 验证（v13 终态——回滚 v9 侧面滑入，scale 弹簧搁置）

- `dotnet build -c Debug` — **0 警告 0 错误**
- `dotnet test` — **232/232 通过**
- `dotnet publish -c Release -r win-x64` — **0 警告**（NativeAOT 通过）
- exe 大小：**27,669,504 字节**（v13 = v9 代码，移除 v10-v12 的 ScaleTransform/关键帧 Animation/RenderTransformOrigin 代码；R44+R46 累计 +59,392 字节 / 58KB，远低于 100KB×2 预算）
- 双路径同步：`cp` 到 `artifacts/publish/win-x64-nativeuia/BYH.exe`
- 机器侧验证（BYH 已启动 PID 36916，**用户 175% DPI 显示器**）：
  - `Ctrl+Alt+Q` → 框选 → 工具栏
  - 按 T → Ocean Eyes 关闭，**贴图窗从屏幕外（右下方）滑入到 pin 位置**（TranslateTransform (400,100)→(0,0) CubicEaseOut 300ms）+ Opacity 凘入
  - Esc/双击关闭时反向滑出到屏幕外
  - Esc 关闭、干净无边框只圆角、缩小最低 25%、默认尺寸 = 原截图 1:1、拖动/右键菜单/滚轮缩放 均正常
  - **v8-v12 的 scale 弹性动画搁置**（详见 §22），待未来解决 RenderTransformOrigin 问题

### 注意事项 / 已知 trade-off

- **T 键不可配置**：本批次 T 硬编码在 `OnToolbarKeyPressed`。若与用户自定义功能快捷键冲突，T 优先用作 Pin。未来可加 `ToolbarShortcutSettings.PinKey`（默认 "T"，可禁用）。同 R44 的 P。
- **关闭手势**：双击贴图窗任意位置 / 右键 → "关闭" / 右键 → "关闭所有"。Esc 仍然只退出 Ocean Eyes 会话（贴图是 `WS_EX_NOACTIVATE` 永远不能获得焦点，没有"当前焦点贴图"概念）。拖动用左键 + 3px 阈值（避免单击/拖动与 DoubleTapped 双击识别冲突）。
- **滚轮缩放**：滚轮上 = ×1.1 放大，下 = ÷1.1 缩小，clamp [0.1, 5.0]。窗口左上角固定（top-left anchor），`SizeToContent` 自动重排到新尺寸。如需"鼠标位置不变"的中心锚点缩放，下批加。
- **DPI 正确性**：Avalonia `Bitmap` 默认 96 DPI。在非 100% 缩放显示器（如 150%）下，原生 `Stretch="None"` 不设 Width/Height 会把图像渲染成 `pixelSize × (dpi/96)` 物理像素——贴图比原截图大 1.5 倍。`ApplyScale` 在 `_scale == 1.0` 时把 Image 逻辑尺寸设为 `pixelSize / RenderScaling`，抵消 DPI 缩放，让贴图占用的物理像素 = 原截图时的物理像素。`Opened` 事件会再调一次 `ApplyScale`（兜底 Show() 早于 HWND 完全实现的边界情况）。
- **跨屏拖动未 clamp**：贴图窗拖到屏幕外不会自动 clamp 回工作区。和 ColorPickerLoupe 不同（loupe 是程序控制位置，需要 clamp；贴图是用户手动拖，用户自己负责不拖丢）。已知 trade-off。但跨屏拖动到不同 DPI 的显示器时不会自动 rescale（`RenderScaling` 在窗口打开时定一次），可能视觉跳变。已知 trade-off。
- **多显示器 BitBlt**：贴图窗内容是已经 capture 好的 PNG（不是实时取屏），所以没有跨显示器 BitBlt 黑屏问题。已规避 R44 的多显示器限制。
- **内存占用**：每个贴图窗 ~PNG 大小 + Bitmap decode 后的 RGBA buffer（width × height × 4）。1920×1080 截图约 8MB/张。用户一次钉 10 张 = 80MB——可接受。如需优化，可改成共享 PNG byte[]（窗口只持 byte[] 引用），但 `Bitmap` 必须每窗一个 decode（Avalonia 不支持跨窗口共享 Image source）。

---

## 3y. 本会话（第三十九批增量）完成的工作：R30 设置页上下窗格纠正

用户明确指出上一版仍把导航栏做成贯穿全高的独立列，与参考图不符。参考图的真实空间关系是：导航塔属于上排；下方大窗格从导航塔下方开始，并与中央工作区共同形成横向底排。本批已按这一关系完成纠正。

### 最终布局

- 第 1 列产品概览继续跨上下两排。
- 第 2 列导航删除 `Grid.RowSpan="2"`，只占 `Grid.Row="0"`；五个真实导航入口完整保留。
- 第 3 列中央设置继续只占上排并独立滚动；右上 `Current setup` 保持真实 Provider / QuickTools / OCR 摘要。
- 下方 `SYSTEM OVERVIEW` 改为 `Grid.Column="1" Grid.ColumnSpan="2" Grid.Row="1"`，横跨导航和中央设置区；右下 `Window controls` 保持独立。
- `SYSTEM OVERVIEW` 内部改成一块统一表面：上方安静标题带，下方运行模式 / 主题预览 / 配置与诊断三组内容，用 hairline 分隔，不再嵌套三张显眼卡片。

### 兼容性与保留项

- 为让导航在 1240×680 logical 最小高度完整显示，适度收紧品牌徽记、分区间距和按钮 padding；没有删除任何导航入口。
- 删除导航底部与 `SYSTEM OVERVIEW` 重复的“取词服务运行中”卡片，运行状态只在下方共享窗格表达一次。
- `PolicyPathText`、`OnOpenConfigDirectoryClick`、`OnOpenLogDirectoryClick` 以及全部导航 Click 事件保持不变；本批无 code-behind 改动。
- 永久约束已写入 `docs/architecture/08-theme-system.md`：禁止把导航恢复为全高 `Grid.RowSpan="2"` 独立列。

### 验证证据

- Release build：0 warning / 0 error。
- 全量测试：**232/232**（Core 156、Providers 35、Windows 41）。
- NativeAOT：0 AOT/裁剪警告、0 PDB；`BYH.exe` = **27,674,112 bytes**。
- 默认尺寸发布版：`artifacts/qa/ivory-jade-settings-v7-corrected-nativeaot.png`。
- 175% DPI、1240×680 logical 最小尺寸发布版：`artifacts/qa/ivory-jade-settings-v7-corrected-minimum-nativeaot.png`（2194×1254 physical），五个导航入口和底部三组内容均无裁切。
- 独立验证报告：`output/TASK-009-settings-layout-correction.md`。

### Agent / reqbase 工具记录

- `omp-worker` 已用精确 selector `xiaomi-mimo/mimo-v2.5-pro` 成功完成一次只读 XAML 审阅（约 43 秒），确认底部重排只需 AXAML 并应保留路径文字和两个目录按钮事件；此前的 MiMo 无响应问题当前不可复现。
- reqbase `quick` 在已有任务项目中误用了 `TASK-001`，一度覆盖旧任务；已立即从 REQ/交接证据恢复旧 `TASK-001`，并将本需求正确迁移为 `REQ-007` / `TASK-009`。该工具缺陷已登记为 `ISSUE-014`；后续调用 `quick` 后务必先检查任务 ID 是否递增。

--- 

## 3z. 本会话（第四十批增量）完成的工作：R30 设置页英文与立体边缘精修

用户要求停止额外设计方向流程，直接以已提供的 Ivory Jade 参考图作为视觉事实源。本批保持第三十九批正确的上下窗格结构不变，只处理语言、信息密度、图标和材质层级。

### 完成内容

- 设置窗口静态文案、动态页标题、运行状态、Provider/Actions/Vision/Launcher 表单及相关保存/错误反馈统一为英文；编辑文件中的中文匹配仅剩代码注释。
- 删除重复说明和重复状态行，保留快捷键语义、当前值、兼容性/安全提示与错误反馈。
- General / Translation / Actions / Vision / Launcher 五个导航入口加入统一的焦糖金轮廓 `Path` 图标；活动项仍使用暖金底和白色图标。
- `PearlCard`、`DecorativeFrame`、`SettingsFrame`、`PorcelainCard` 改为四层低对比材质：半透明暖金发丝线、内侧象牙高光、极淡香槟 inner glint、暖棕柔影；移除活动导航上突兀的 Fluent 黑色焦点矩形。
- 右侧 Current setup 在最小高度删除重复的 `Services ready` 行，避免与固定底部窗格发生局部遮挡。

### 验证证据

- 全量测试：**232/232**（Core 156、Providers 35、Windows 41）。
- NativeAOT：0 AOT/裁剪警告、0 PDB；`BYH.exe` = **27,671,040 bytes**。
- 默认尺寸：`artifacts/qa/ivory-jade-settings-v8-english-depth-nativeaot.png`。
- 175% DPI、1240×680 logical 最小尺寸：`artifacts/qa/ivory-jade-settings-v8-english-depth-minimum-nativeaot.png`，导航、Current setup、System Overview 与 Window controls 均无裁切/重叠。
- 独立报告：`output/TASK-010-settings-english-depth-verification.md`。

### 永久约束

- 立体感来自多层、低对比的光影关系，不来自粗金边或深色描边。
- 设置面板保持简洁英文；非必要说明不再补回。
- 导航使用一致的轮廓线图标，不使用 emoji 或混杂图标风格。
- 后续设置页修改仍需同时检查默认尺寸与 175% DPI 最小尺寸。

---

## 3aa. 本会话（第四十一批增量）完成的工作：R30 设置页标注图框架精修

用户在当前设置页截图上用红色箭头明确标注边框间距、背景层级、上下比例和最左分栏问题。本批严格以该标注图及用户的补充确认作为事实源，没有调用 huashu-design。

### 完成内容

- BYH 导航塔改用 `NavTowerFrame`：外框和内侧双线间距收紧到约 5px，采用低对比古金线、象牙高光、浅香槟内线与暖棕柔影；不再保留原来约 10–20px 的空洞套框感。
- `ivory-jade-ornament.jpg` 从不透明内框后方移入导航内容层，保持图片原本重心在下的构图；装饰现在完整位于导航内容后面，而不是只在右侧露出窄边。
- 设置窗口行定义由 `*,204` 调为 `*,260`：上方设置工作区降低，下方 `SYSTEM OVERVIEW` 增高；标题带不再绘制横向底边，标题与三个真实状态模块成为一体。
- 最左侧 `PRODUCT CONCEPT / COLOR LANGUAGE / WORKFLOW` 改为无圆角 `FlatRail`，只以 1.5px 古金色右竖线与导航区分隔；三个内部等高分区及细分隔线保留。
- 大型框架继续使用低对比多层光影，小型输入框和控件没有被扩散成双层边框，避免视觉碎裂。

### 验证证据

- 全量测试：**232/232**（Core 156、Providers 35、Windows 41），0 失败、0 跳过。
- NativeAOT 发布成功；`BYH.exe` = **27,671,552 bytes**。
- 175% DPI 默认尺寸：`artifacts/qa/ivory-jade-settings-v9-annotated-default-nativeaot.png`。
- 175% DPI、1240×680 logical 最小尺寸：`artifacts/qa/ivory-jade-settings-v9-annotated-minimum-nativeaot.png`；导航完整，中央/右栏正常滚动，System Overview 与 Window controls 无裁切或重叠。
- 独立报告：`output/TASK-011-settings-frame-verification.md`。

### OMP 与 reqbase 记录

- OMP 使用精确 selector `xiaomi-mimo/mimo-v2.5-pro`，本轮实现 + 基础编译耗时 360.5 秒，成功返回但最终摘要仍过于简略；主 Agent 完成了文件审查、视觉 QA、完整测试和发布。
- reqbase `quick` 再次错误覆盖历史 `TASK-001`；已当场恢复原任务并把本轮迁移为 `TASK-011`。这是已登记的 `ISSUE-010/011/014` 的复现，修复该 skill 时必须通过 `skill-hub` 处理。

### 永久约束

- 导航塔的装饰背景必须属于内框内容层，不能再放回不透明内框后方。
- 导航塔使用紧邻双层圆角框；最左产品概念栏使用直角平栏 + 古金竖线，两者结构不可互换。
- `SYSTEM OVERVIEW` 是连续面板，不要恢复标题与正文之间的横向分割线。
- 设置页根布局下排基准高度为 260 logical px；后续增减内容仍须复核默认与 175% DPI 最小尺寸。

---

## 3ab. 本会话（第四十二批增量）完成的工作：R54 金属质感双圆角结构框

本批直接研究用户提供的四张边框局部参考图，不调用 huashu-design；所有修改位于 Git 分支 `task/REQ-012-metallic-frames` 的独立 worktree。

### 完成内容

- 新增 `ByhMetallicEdgeBrush`：1 DIP 外缘按铜金 → 香槟 → 深金 → 亮金变化，避免普通恒色描边。
- 新增单一 `MetallicFrame` 结构样式：2 DIP 象牙光学缝、3 DIP 处 1 DIP 浅金内曲线，以及只在底侧显现的低透明度暖棕投影；圆角和直边始终同心。
- `MetallicFrame.Compact` 只覆盖圆角半径，用于窄导航塔；主设置、右侧概览、System Overview、Window controls 使用标准 24 DIP 半径。
- 移除设置页 `DecorativeFrame SettingsFrame` 的 class 叠加。两者原先设置完全相同的一组属性，后声明者会覆盖前者，并没有形成真实双层结构。
- `FlatRail`、`PearlCard`、`PorcelainCard` 保持原样：最左产品栏仍为平直分栏，内部信息卡不镶结构金框。

### 验证证据

- Release build：0 警告、0 错误；完整测试 **232/232**。
- win-x64 NativeAOT：0 警告、0 PDB；`BYH.exe` = **27,670,528 bytes**。
- 175% DPI 默认尺寸：`artifacts/qa/ivory-jade-settings-v10-metallic-default-nativeaot.png`。
- 175% DPI、1240×680 logical 最小尺寸：`artifacts/qa/ivory-jade-settings-v10-metallic-minimum-nativeaot.png`。
- 圆角局部：`artifacts/qa/ivory-jade-settings-v10-metallic-corner-detail.png`。
- 独立报告：`output/TASK-014-metallic-frame-verification.md`。

### OMP 分工

- `xiaomi-mimo/mimo-v2.5-pro` 第一次只读盘点 frame class、应用位置和 Avalonia BoxShadow 风险；第二次只做架构文档与验证报告草稿。
- 主 Agent 负责参考图光学拆解、主题实现、diff 审核、默认/最小尺寸视觉判断、测试、NativeAOT 发布与 Git 收口。

---

## 3ac. 本会话（第四十三批增量）完成的工作：REQ-012 设置页 Foamie 参考图精修 + main 合并

本批按用户提供的 Foamie 设计系统参考图精修设置页 UI（不调用 huashu-design），并先把 main 合入 `task/REQ-012-metallic-frames`（merge commit `fddb9a6`：冲突仅 BYH.exe 二进制与 index.yaml，后者保留 main 全文 + 补 REQ-012 done 条目；main 带入 R49 预览缩放修复、R52 磁力吸附、R48 标注工具集、R53 长截图）。

### 完成内容

- **截图管线修复（关键坑）**：`artifacts/qa/capture-settings.py` 现在为 `SetWindowPos`/`SetForegroundWindow` 显式声明 `argtypes`——64 位下裸传 `-1` 会被 ctypes 截成 `0xFFFFFFFF`，`HWND_TOPMOST` 静默失效，导致抓到的全是前台浏览器/Obsidian 画面（此前 v11 两张截图即因此作废）。抓图前 topmost、抓后还原。
- **主题 `IvoryJade.axaml`**：`ByhGoldNavBrush` 改垂直三段焦糖渐变（`#F0D5A1→#DCA85E→#C08337`，更饱满）；`SettingsNav.Active` 圆角 12→14（Avalonia 的 Button **没有 BoxShadow 属性**，AVLN2000，投影方案放弃）；新增 `TextBlock.CardTitle`（衬线 SemiBold，统一全页标题字族）与 `Path.MiniIcon`（13px 线图标，Stroke 内联给定）。
- **视图 `SettingsWindow.axaml`**：8 个卡片分区标题全部加 `Classes="CardTitle"`（原无衬线混排）；导航卡 Grid 改 `Auto,*,Auto` 加底部小宝石徽标（26px 圆形，最小高度下余量仅 ~37px，故从 30 缩到 26，再大会挤掉 Launcher 按钮）；Current setup 三行加圆形 soft-tint 图标徽章（provider=地球/AccentSoft、hotkey=键盘/WarningSoft、OCR=眼睛/SuccessSoft，对齐参考图 Today's Summary）；底部 Runtime 绿点外套 26px SuccessSoft 圆徽章；左栏与底部色块改方形（20x20 / 16x16，参考图 COLOR PALETTE 样式）；Runtime 文本加 `TextWrapping="Wrap"` 修最小宽度裁切。
- 未动：MetallicFrame 集中样式、五结构窗格、平直 FlatRail、Pearl/Porcelain 内卡克制度、jade Primary 按钮（参考图虽有焦糖主按钮，但 jade 主按钮是 R43 既定品牌决定）。

### 验证证据

- Release build：0 警告、0 错误；完整测试 **334/334**（main 合并后 232→334）。
- win-x64 NativeAOT：0 警告、0 PDB；`BYH.exe` = **27,959,808 bytes**。
- 175% DPI 默认尺寸：`artifacts/qa/ivory-jade-settings-v12-foamie-refined-default-nativeaot.png`。
- 175% DPI、1240×680 logical 最小尺寸：`artifacts/qa/ivory-jade-settings-v12-foamie-refined-minimum-nativeaot.png`。
- 可信基线对比：`artifacts/qa/before-default.png` / `before-minimum.png`。
- `docs/architecture/08-theme-system.md` 组件语义类表已同步（SettingsNav.Active / CardTitle / MiniIcon）。
- **待用户验收**：v12 两张截图已提交（commit `e728f1f`），等用户确认后 REQ-012 可视同收尾。

---

## 3ad. 本会话（第四十四批增量）完成的工作：REQ-012 设置页 Foamie 氛围/立体感/精致感深化

用户反馈 v12「几乎没改」，本批在保留五窗格结构、FlatRail、MetallicFrame 集中样式、克制内卡的前提下，从主题资源层大幅强化立体感（浮影+受光面）、精致感（金属边+宝石光晕+徽章细边）和氛围感（暖光径向光晕+云纹显影）。

### 完成内容

- **主题 `IvoryJade.axaml`**：
  * 新增 `ByhAtmosphereBrush`（暖色径向光晕）和 `ByhGemGlowBrush`（翡翠径向光晕）。
  * `ByhMetallicEdgeBrush` 加亮香槟高光，金属边对比度更高。
  * `ByhGoldNavBrush` 从三段升为四段，顶部增加香槟高光，并新增 `ByhGoldNavBorderBrush` 凸面边框渐变，让 active 药丸在 Avalonia Button（无 BoxShadow）上也能读出 3D 体积。
  * `MetallicFrame` 阴影加深为三层环境光+投影，并加顶部微光。
  * `PearlCard` / `PorcelainCard` 加顶部强光高光边和更明显的浮动阴影。
  * `StatusPill` / `Badge` 加内高光和微阴影。
  * `GemPortrait` 增加翡翠色外发光环。
- **视图 `SettingsWindow.axaml`**：
  * 全布局底层加 `ByhAtmosphereBrush` 暖光光晕。
  * 各面板 ornament 云纹透明度大幅提升（nav 0.30 / main 0.10 / rail 0.22 / right 0.28）。
  * 导航卡顶部 emblem 与底部 foot gem 套 `ByhGemGlowBrush`。
  * 欢迎标题放大（24→26），右上角 IVORY JADE 徽章也套 gem glow。
  * 右侧人物卡背景圆改 `ByhGemGlowBrush`。
  * Current setup / Runtime 图标徽章放大到 28px、加 1px 金边和软阴影。
  * 左栏底部 emblem 与色板小方块加 lift 阴影。
- 未动：五结构窗格、FlatRail、内卡克制度、jade Primary 按钮、SelectionRuntime / LongScreenshot / 翻译 / OCR / 快捷键逻辑。

### 验证证据

- Release build：0 警告、0 错误；完整测试 **334/334**。
- win-x64 NativeAOT：0 警告、0 PDB；`BYH.exe` = **27,959,296 bytes**。
- 175% DPI 默认尺寸：`artifacts/qa/ivory-jade-settings-v13-atmosphere-default-nativeaot.png`。
- 175% DPI、1240×680 logical 最小尺寸：`artifacts/qa/ivory-jade-settings-v13-atmosphere-minimum-nativeaot.png`。
- 可信基线对比：`artifacts/qa/before-default.png` / `before-minimum.png`。
- `docs/architecture/08-theme-system.md` 已同步新 token 与组件类描述。
- **待用户验收**：v13 两张截图已提交（commit `aaeb31a`），等用户确认。

### 踩坑

1. Avalonia `RadialGradientBrush` 用 `RadiusX`/`RadiusY` 而非 `Radius`，`Center`/`GradientOrigin` 用百分比字符串；直接用 WPF 语法会 AVLN2000。
2. Avalonia `BoxShadow` 颜色必须是 6 位或 8 位 HEX，写错成 9 位会在运行时 `Color.Parse` 抛 `FormatException`；主题 AXAML 编译不报错（颜色串在运行时才解析）。

---

## 3ae. 本会话（第四十五批增量）完成的工作：REQ-012 设置页 LiftedPanel 双层大圆角重构

用户再发参考图 `@image#1:Clipboard_Screenshot.png`，指出主内容区应改为上下两层大圆角框搭在一起，每个大边框添加外围阴影以增强纵深感。本批在保留五窗格结构、FlatRail、MetallicFrame 集中样式的前提下，把原来扁平并列的 `PearlCard` 重构成「大 LiftedPanel → 内部 InnerCard → 内容」的参考图层级。

### 完成内容

- **主题 `IvoryJade.axaml`**：
  * 新增 `ByhShadowLifted`（两层柔和 lift 影）与 `ByhShadowDeep`（三层环境+投影，用于大面板）。
  * 新增 `Border.LiftedPanel`：圆角 18px、1px 金褐边、顶部强光高光边、1px 内象牙隙、多层弥散阴影，让面板从背景真正浮起。
  * 新增 `Border.InnerCard`：圆角 14px、奶油渐变、顶部微光 + 1px 隙，用于包裹大面板内的输入块，避免三层嵌套厚重。
- **视图 `SettingsWindow.axaml`**：
  * `GeneralSection` 改为上下两个 `LiftedPanel`：上层合并 Ocean Eyes Trigger + Toolbar Shortcuts（中间用 Hairline 分隔，类似参考图堆叠卡片）；下层是 Ocean Eyes Capture，并把 Auto-save / Copy to Clipboard 并排到同一行以节省纵向空间。
  * `ProviderSection` 与 `FunctionsSection` 各用一个 `LiftedPanel` 包裹；内部 API Key 与 Prompt Templates 输入区改用 `InnerCard`。
  * `VisionSection` 改为上下两个 `LiftedPanel`：上层 Vision Recognition，下层 OCR Model + Recognition Strategy。
  * `LauncherSection` 改为上下两个 `LiftedPanel`：上层 Launcher list，下层 Spotlight Hotkey。
- 未动：五结构窗格、FlatRail、jade Primary 按钮、SelectionRuntime / LongScreenshot / 翻译 / OCR / 快捷键逻辑。

### 验证证据

- Release build：0 警告、0 错误；完整测试 **334/334**。
- win-x64 NativeAOT：0 警告、0 PDB；`BYH.exe` = **27,956,224 bytes**。
- 175% DPI 默认尺寸：`artifacts/qa/ivory-jade-settings-v14-lifted-default-nativeaot.png`。
- 175% DPI、1240×680 logical 最小尺寸：`artifacts/qa/ivory-jade-settings-v14-lifted-minimum-nativeaot.png`。
- 可信基线对比：`artifacts/qa/before-default.png` / `before-minimum.png`。
- **待用户验收**：v14 两张截图已提交（commit `0b8d6a4`），等用户确认。

---

## 3c. 本会话（第十四批增量）完成的工作：R26 Ivory Jade 主题

- 新增 `src/SelectionAssistant.UI/Themes/IvoryJade.axaml`：完整语义色、反馈色、alpha 派生色、圆角、阴影与全局组件类。
- `App.axaml` 固定 Light variant，并在 `FluentTheme` 后跨程序集加载主题。
- 七个窗口完成迁移：Settings / Toolbar / QuickTools / Prompt / PromptTemplateEdit / Result / RegionSelectOverlay。
- View AXAML 不再含旧品牌十六进制颜色；C# 状态色改为 `FeedbackSuccess` / `FeedbackError` class。
- 视觉原则：象牙为主体；玉色只用于保存/运行/OCR/活动状态；古金只用于细边、短分隔线和 OCR 小手柄；无渐变、无装饰图。
- 175% DPI 首轮截图发现 QuickTools 底部重叠，修复为 480px 高 + 72px 指令区 + Ghost 功能列表；第二轮截图通过。
- 主题规范详见 `docs/architecture/08-theme-system.md`；验证证据见 `output/TASK-002-theme-verification.md` 和 `artifacts/qa/ivory-jade-*.png`。

### R26 主题踩坑

1. Avalonia `TextBox` 没有可直接设置的 `BoxShadow` 属性，焦点环用 2px primary 边框；不要重新加无效 setter。
2. 主题迁移必须做高 DPI 真实截图；编译无法发现固定高度浮层的内容重叠。
3. `omp-worker` 大批量改七窗 + C# + build 在 420s 超时，且留下两个未闭合 AXAML 标签。以后把 MIMO 任务拆成 1-3 个文件/批，并由主 Agent 编译审查。

## 3d. 本会话（第十五批增量）完成的工作：R27 设置页高保真重构

- `SettingsWindow` 从 560×640 单列长页改为 1000×720（最小 860×600）固定侧栏；四页分别为常规、翻译服务、自定义功能、视觉识别。
- 右侧只滚动当前分区，标题与底栏固定；`ShowAndScrollToPromptTemplates` / `SelectProviderForEditing` 会先切换正确分区。
- 第一轮仅借鉴参考图结构，用户指出视觉差距明显；REQ-003 演化新增 AC-5，第二轮高保真补入奶油渐变、细金框、暖金活动导航、珠光花丝和玉石徽记。
- 新增 `UI/Assets/Theme/ivory-jade-{emblem,ornament}.jpg`；由内置 imagegen 参考用户效果图生成，不含人物与文字，仅设置页使用。
- 175% DPI 验证默认/最小尺寸和 Provider 表单；截图见 `artifacts/qa/ivory-jade-settings-v3*.png`。
- 验证报告：`output/TASK-005-settings-v3-verification.md`。

## 3e. 本会话（第十七批增量）完成的工作：R29 设置页视觉精修

- 将 `src/SelectionAssistant.App/Assets/app-icon.png` 通过跨程序集 `avares://SelectionAssistant.App/Assets/app-icon.png` 用作右上角人物欢迎卡；NativeAOT 发布版已实际显示。
- 中央标题区增加 `WELCOME BACK` 欢迎带、真实能力标签（local-first / instant capture / NativeAOT）和更清晰的标题层级。
- 右侧从纯配置列表升级为人物欢迎区 + `Current setup`；Provider / 快捷键 / OCR 仍全部来自运行时真实设置。
- 底部增加“主题预览”辅助模块，展示真实 Ivory Jade 语义色规则；未伪造项目、消息、任务或统计数据。
- 新增 `SettingsFrame` / `PorcelainCard` / `GemPortrait` / `StatusPill` 等设置页专用材料层；四列调整为 `190,170,*,270`。
- 175% DPI 默认、Provider、1240×680 最小尺寸截图通过；162/162 测试通过；NativeAOT 无警告、无 PDB。
- 截图：`artifacts/qa/ivory-jade-settings-v5-nativeaot.png`、`ivory-jade-settings-v5-provider.png`、`ivory-jade-settings-v5-minimum.png`。
- OMP 状态：`xiaomi-mimo/mimo-v2.5-pro` 本轮 3 次只读任务分别在 45s/75s/120s 超时且无文本，未影响主线；selector 与认证链无报错，后续需进一步压缩任务或排查 MiMo 会话响应。

## 3f. 本会话（第十八批增量）完成的工作：R30 设置页局部精修

- 宝石 JPG 不再按完整正方形缩进圆形容器；导航头像、中央主题徽记和左下品牌徽记都改为放大居中裁切，源图四周边缘不再可见。
- 新增 `ByhHairlineBrush`，并降低 `ByhFloatingBorderBrush` / `ByhPorcelainBorderBrush` 的 alpha；SettingsFrame、PearlInset、GemPortrait 与阴影改为更低对比的瓷器边界。
- 右上欢迎区改成参考图式单一人物构图：134px 真实 APP icon 与问候文字共享同一视觉区，三个真实运行摘要合并成一张轻量信息板。
- 左侧 PRODUCT CONCEPT / COLOR LANGUAGE / WORKFLOW 改为等高网格，底部品牌徽记仍独立。
- 175% DPI 默认与 1240×680 logical 最小尺寸通过；162/162 测试通过；NativeAOT 无警告、无 PDB。
- 截图：`artifacts/qa/ivory-jade-settings-v6-nativeaot.png`、`ivory-jade-settings-v6-minimum.png`。
- OMP 状态：精确 selector `xiaomi-mimo/mimo-v2.5-pro` 的 90s 只读盘点再次以 exit 124 超时；没有模型选择或鉴权报错。

## 3g. 本会话（第十九批增量）完成的工作：翻译默认 Provider 切回 DeepSeek + R31 重启 race 修复

### 改动 1：翻译默认 Provider 从 SiliconFlow 切回 DeepSeek（用户配置层）

- 用户原 `providers.json` 已含 `deepseek` + `siliconflow` 两个条目（都绑了 DPAPI 密钥），只是 `defaultProviderId=siliconflow`。
- 只改了一处：`AppData\Local\BYH\providers.json` 的 `defaultProviderId: "siliconflow"` → `"deepseek"`。deepseek 条目里 `defaultModel` 已是 `deepseek-v4-flash`（来自 `ProviderPresets.BuiltIn` 默认），无需改代码。
- **OCR 不动**：`vision.json` 仍 `providerId: siliconflow` + `model: Qwen/Qwen3.5-4B` + `disableThinking: true`。OCR Provider 与翻译 Provider 完全解耦（详见 §3b 关键代码入口表）。
- 探针验证：`--probe-translate-speed "The quick brown fox..."` → DeepSeek (deepseek-v4-flash)，TTFB 1660ms，译文「黎明时分，一只敏捷的棕色狐狸在河岸附近跳过那条懒狗。」。OCR 探针 `--probe-vision 0 0 400 200` 仍走 SiliconFlow + Qwen3.5-4B，728ms。

### 改动 2：R31 修复 — 重启后进程消失（Mutex race）

**症状**：用户点托盘「重启 BYH」，托盘消失且新进程没起来。

**根因**：`App.axaml.cs:RequestRestart()` 的旧实现是「先 `Process.Start(exePath)` 再 `RequestExit()`」。但单实例锁（`Program.Main` 里 `using var singleInstance = new Mutex(...)`）在旧进程 Main 返回前不会释放——新进程一启动就抢不到 Mutex，被 `if (!acquired) return 0;` 静默挡掉。结果：旧进程走完 shutdown 死了、Mutex 释放，但新进程早就退出了，**两个都没了**。

**修复**（两层保护）：
1. **Program.cs**：Mutex 从局部 `using` 提升为 static 字段 `s_singleInstance`；新增 `public static void ReleaseForRestart()` 显式 `ReleaseMutex()` + `Dispose()`（catch ApplicationException/ObjectDisposedException，安全 no-op）。
2. **Program.cs Main**：识别 `--restart` 参数，新进程在 Mutex 没拿到时重试 30 次（每次 100ms，总上限 3 秒），并 catch `AbandonedMutexException` 视为拿到（旧进程被 kill 时 OS 会 abandoned Mutex）。
3. **App.axaml.cs `RequestRestart`**：在 `Process.Start` **之前**调用 `Program.ReleaseForRestart()`；`ProcessStartInfo.ArgumentList` 加 `--restart` 让新进程知道走重试路径。

**Mutex 线程亲和性**：`new Mutex(initiallyOwned: true, ...)` 时 acquire 在 UI 线程（`[STAThread]`），`ReleaseMutex()` 必须由同一线程调；`RequestRestart` 跑在 TrayIcon.Click 回调（UI 线程），一致。catch 兜底防止跨线程调用异常。

**机器侧验证**（2026-07-18）：
- 编译 0 警告，NativeAOT exe 26,607,616 bytes。
- 正常启动 OK；第二个普通实例（无 `--restart`）被 Mutex 正确挡住（PID 只有第一个）。
- 旧实例 kill 后立刻用 `--restart` 启新实例：新进程拿到 abandoned Mutex，正常启动（日志 18:06:10 `Switched to provider 'deepseek'`）。
- Avalonia `StartWithClassicDesktopLifetime(args)` 容忍未知 `--restart` 参数，不报错不弹窗。
- ⚠️ **待用户真机点托盘「重启 BYH」做最终验证**（bash 无法点托盘菜单，但代码逻辑已审查 + 路径已模拟）。

### 关键代码入口（第十九批）

| 文件 | 改动 |
|---|---|
| `src/SelectionAssistant.App/Program.cs:147-204` | Mutex 提升为 `s_singleInstance` static 字段；`--restart` 重试 30×100ms；`public static void ReleaseForRestart()` |
| `src/SelectionAssistant.App/App.axaml.cs:683-716` | `RequestRestart`：先 `Program.ReleaseForRestart()` 再 `Process.Start(ArgumentList: "--restart")` |
| `AppData\Local\BYH\providers.json` | `defaultProviderId`: `siliconflow` → `deepseek` |

---

## 3h. 本会话（第二十批增量）完成的工作：R23 快捷启动器

> **完整架构文档**：`docs/architecture/09-launcher.md`（改这个模块先看）。下面只放摘要和踩坑。

### 设计决定（用户拍板）

- **入口**：QuickTools 面板新增第 5 行启动器区（默认 Ctrl+Alt+Q 唤出），窗口高度 480→560。设置页第 5 个分区"启动器"管理。
- **类型**：仅本地软件 + 网页（不动 CLI/UWP，避免参数解析 + UWP 启动 API 复杂度）。
- **图标**：自动从 exe 提取（HICON → PNG → Avalonia Bitmap）；网页用 Google S2 favicon。失败 fallback 到无图标，**不阻塞 UI**。
- **参数**：全部支持 — `{clip}`/`{sel}` 即时替换 + `{prompt:提示语}` 运行时弹 ParameterInputDialog。

### 架构（完全复用 R15 自定义功能系统模式）

8 层一一对应 `04-prompt-templates.md`：

| 层 | R15 | R23 |
|---|---|---|
| Core 数据 | `PromptTemplate` record + `PromptTemplateSet` | `LauncherEntry` record + `LauncherEntrySet` |
| Core 工具 | — | `ParameterReplace`（占位符两阶段展开）|
| Core 结果 | — | `LauncherLaunchResult` record |
| Infrastructure | `PromptTemplatesStore` | `LauncherEntryStore` |
| Platform.Windows | — | `WindowsIconExtractor`（P/Invoke + PNG）+ `LauncherRunner`（Process.Start）|
| UI ViewModel | `PromptFunctionRow` | `LauncherEntryRow`（+ Icon Bitmap 字段）|
| UI 编辑窗 | `PromptTemplateEditWindow` | `LauncherEntryEditWindow` + `ParameterInputDialog` |
| App 接线 | `OnPromptTemplateAdded/Saved/Deleted` | `OnLauncherEntryAdded/Saved/Deleted/Moved` + `OnLauncherRunRequested` |

### 关键代码入口

| 文件 | 角色 |
|---|---|
| `src/SelectionAssistant.Core/Launcher/LauncherEntries.cs` | 数据模型 + Set CRUD |
| `src/SelectionAssistant.Core/Launcher/ParameterReplace.cs` | 占位符展开（两阶段）|
| `src/SelectionAssistant.Core/Launcher/LauncherLaunchResult.cs` | 启动结果 |
| `src/SelectionAssistant.Infrastructure/Configuration/LauncherEntryStore.cs` | JSON 持久化 + 原子写 |
| `src/SelectionAssistant.Infrastructure/Configuration/ByhApplicationPaths.cs` | +`LauncherEntriesFile`/`LauncherIconsDirectory` |
| `src/SelectionAssistant.Platform.Windows/Launcher/WindowsIconExtractor.cs` | HICON → PNG（手写 P/Invoke，不用 System.Drawing）|
| `src/SelectionAssistant.Platform.Windows/Launcher/LauncherRunner.cs` | Process.Start 封装 |
| `src/SelectionAssistant.UI/Views/LauncherEntryRow.cs` | public top-level ViewModel |
| `src/SelectionAssistant.UI/Views/LauncherEntryEditWindow.axaml(.cs)` | 编辑/新建弹窗 |
| `src/SelectionAssistant.UI/Views/ParameterInputDialog.axaml(.cs)` | {prompt:...} 运行时输入 |
| `src/SelectionAssistant.UI/Views/SettingsWindow.axaml(.cs)` | +启动器分区（SettingsPage.Launcher）|
| `src/SelectionAssistant.UI/Views/QuickToolsWindow.axaml(.cs)` | +启动器区 + `UpdateLauncherIcon` 异步推图标 |
| `src/SelectionAssistant.App/SelectionRuntime.cs` | +`_launcherEntries` + 5 个 CRUD 方法 + Launch/Complete/Cancel |
| `src/SelectionAssistant.App/App.axaml.cs` | 订阅 5 个 launcher 事件 + `LoadLauncherIconsAsync` + favicon 抓取 + 弹框编排 |
| `src/SelectionAssistant.App/Program.cs` | +`--probe-icon-extract` / `--probe-launcher-list` / `--probe-launcher-run` |

### 机器侧验证（2026-07-18）

- `dotnet test`：**194/194**（原 162 + 新增 32：18 LauncherEntryStore + 14 ParameterReplace）
- NativeAOT publish：**0 警告 0 错误**，exe 26,841,088 bytes（~25.6MB，相比上版多 233KB）
- `--probe-icon-extract notepad.exe` → 28×28 RGBA PNG 2195B（Gemini Vision 确认是清晰 Notepad 图标）
- `--probe-icon-extract chrome.exe / cmd.exe / msedge.exe` 全部通过
- `--probe-launcher-list` 在 launcher-entries.json 不存在时返 Count=0，不崩
- NativeAOT 后图标提取仍工作（最关键风险点验证通过）

### ⚠️ 踩坑（永久记录）

1. **`GetObject` 对 SHGetFileInfo 返回的 HBITMAP 返 0（err=203）**：SHGetFileInfo 的 color bitmap 是 DIB 不是 DDB。**解法**：用两遍 `GetDIBits`（第一遍 lpvBits=NULL 拿尺寸，第二遍才拷像素），不要依赖 GetObject。详见 `09-launcher.md` 踩坑 1。
2. **Win32Bitmap 结构必须严格按 Win32 `BITMAP` 布局**：bmType/bmWidth/bmHeight/bmWidthBytes 是 LONG(4字节)，bmPlanes/bmBitsPixel 是 WORD(2字节)。布局错 GetObject 返 0。
3. **`SHGetFileInfo` 必须显式 `SetLastError=true`**：否则 `GetLastPInvokeError` 不可靠。
4. **ValueTuple 不可空**：`_pendingLaunch` 用 `(string, string)?` nullable，不要 `?? default`，改用 `is { } pending` 模式匹配。
5. **EntrySaved 签名要带 name**：编辑模式下 NameInput 可编辑，但初版 EntrySaved 漏传 name 导致改名不生效。修复：EntrySaved = `Action<string id, string name, kind, target, args, workDir>`。
6. **图标缓存目前不落盘**（每次启动重提）：如果未来要落盘，键要用 `{entryId}_{targetHash}.png` 避免 target 改了缓存还命中（用户改 target 后 SetLauncherEntries 会清空 rows 重提，所以当前 in-memory 也对）。

### 主 Agent vs mimo-agent 分工（本会话验证了 §3b 教训 6）

| 工作 | 谁做 | 备注 |
|---|---|---|
| HICON → PNG 链路 + P/Invoke 实现 + 探针验证 | **主 Agent** | 涉及 Win32 互操作，需要逐步诊断 + 调试 |
| ParameterReplace 两阶段算法 + Core 数据模型 | **主 Agent** | 核心业务逻辑 |
| App.axaml.cs 事件接线 + 图标异步加载编排 | **主 Agent** | UI 控制流 |
| 5 个 UI 文件（Row + 编辑窗 + 输入框 + 2 个窗口改动）| **mimo-agent** | 机械执行，0 警告 0 错误，162→162 测试保持 |
| 32 个测试 + NativeAOT 验证 | **mimo-agent** | 测试照搬模板，验证全通过（独立复核 exe + 测试文件存在）|
| 文档 | **主 Agent** | 设计本人最清楚，写得最快 |

教训再次确认：**mimo-agent 适合 1-N 个文件的机械实现/迁移/测试照搬，不适合 Win32 互操作 + 业务算法 + UI 控制流**。

---

## 3i. 本会话（第二十一批增量）完成的工作：R32 SpotlightWindow + QuickTools toggle 修复

### 改动 1：QuickTools 快捷键 toggle 修复（用户报告的 bug）

**症状**：按 Ctrl+Alt+Q 打开 QuickTools 后，再按一次不关闭，只是"再 Show 一次"（视觉上无反应）。

**根因**：`App.OnChordTriggered` 之前是无脑 `_quickToolsWindow?.ShowAt(x, y, selected)`，没有 toggle 逻辑。同一个 handler 被 chord 和全局快捷键两个路径调用。

**修复**（`App.axaml.cs:OnChordTriggered`）：
```csharp
if (_quickToolsWindow?.IsVisible == true) { _quickToolsWindow.Hide(); return; }
_quickToolsWindow?.ShowAt(x, y, selected);
```
一处改动同时修两个路径（chord + 快捷键）。

### 改动 2：R32 SpotlightWindow（独立启动器搜索面板）

**用户设计决策**（4 项 AskUserQuestion 答案）：
- 入口：独立窗口 + 独立快捷键（默认 Ctrl+Alt+Space）
- 主题：复用 Ivory Jade（不做暗色 acrylic）
- toggle：只 toggle 自己（不管 QuickTools）
- 导航：↑↓ / Enter / Esc / Ctrl+Enter

**布局**（参考用户贴的 PowerToys Run 截图，但 Ivory Jade 配色）：
- 顶部搜索框（⚡ 图标 + "搜索启动项…" 占位符）
- 中间列表区（图标 24×24 + 主名称 + 次路径/url，Active 态玉色高亮）
- 底部 keycap 提示条（"↵ 启动" / "Ctrl+↵ 设置" / "Esc 关闭"）
- 窗口 560×480，CenterScreen，Topmost，AcrylicBlur，无装饰

**架构关键点**：
1. **完全独立于 QuickTools** — 独立窗口、独立快捷键、独立持久化（`spotlight-trigger.json`）。两窗口可同时存在。
2. **共用同一份启动项数据** — `SelectionRuntime.GetLauncherEntries()` 是单一数据源，App 在 `RefreshSettingsAsync` 里同时推给 QuickTools + Settings + Spotlight 三个消费者。
3. **`WindowsGlobalHotKey` 接受 `QuickToolsTriggerSettings`** — Spotlight 在 App 层用 `ToQuickToolsShape` 适配器转换，**不修改 Platform.Windows 层**。

### 关键代码入口（第二十一批）

| 文件 | 角色 |
|---|---|
| `src/SelectionAssistant.Core/Input/SpotlightTriggerSettings.cs` | record + Validate/Normalize/ToDisplayText，默认 Ctrl+Alt+Space |
| `src/SelectionAssistant.Infrastructure/Configuration/SpotlightTriggerStore.cs` | JSON 持久化 + 原子写，`spotlight-trigger.json` |
| `src/SelectionAssistant.UI/Views/SpotlightWindow.axaml(.cs)` | 独立搜索窗口 + ↑↓/Enter/Ctrl+Enter/Esc 导航 + 过滤 |
| `src/SelectionAssistant.UI/Themes/IvoryJade.axaml` | +`Border.SpotlightRow`/`.Active`/`.SpotlightSearch` 样式 |
| `src/SelectionAssistant.UI/Views/SettingsWindow.axaml(.cs)` | +"启动器"分区 Spotlight 快捷键卡片（从"常规"移过来，避免两张卡片挤在同一个分区导致底部被裁掉） + `ShowAndScrollToLauncher` + `RequestLauncherEdit` |
| `src/SelectionAssistant.App/App.axaml.cs` | +`OnChordTriggered` toggle + `_spotlightWindow`/`_spotlightHotKey` 字段 + `ToQuickToolsShape` 适配器 + `RegisterInitialSpotlightHotKey` + `OnSpotlightTriggered`（toggle）+ `OnSpotlightTriggerSettingsSaved` + `OnSpotlightLauncherEditRequested` + `OnSpotlightSettingsRequested` + `RefreshSettingsAsync`/`LoadLauncherIconsAsync` 推 spotlight |

### 机器侧验证（2026-07-18）

- `dotnet test`：**213/213**（原 194 + 新增 19：9 SpotlightTriggerSettings + 10 SpotlightTriggerStore）
- NativeAOT publish：**0 警告 0 错误**，exe 26,899,968 bytes（~25.6MB，相比上版 +58KB — 新增 SpotlightWindow + 主题样式合理增长）
- 编译验证：0 警告 0 错误（Debug + Release）

### 主 Agent vs mimo-agent 分工（第二十一批）

| 工作 | 谁做 | 备注 |
|---|---|---|
| QuickTools toggle 修复 | **主 Agent** | 1 处改动，简单 |
| SpotlightTriggerSettings + Store 设计 + 实现 | **主 Agent** | 照搬 QuickTools 模式但要去 MouseChordEnabled |
| SpotlightWindow.axaml(.cs) 核心 UI + 键盘导航 | **主 Agent** | 涉及焦点时序 + ItemsControl.ContainerFromIndex + Active class 管理 |
| Ivory Jade 加 SpotlightRow/Active 样式 | **主 Agent** | 主题样式细节 |
| App.axaml.cs 全套接线 | **主 Agent** | 控制流 + 适配器 + 事件订阅 |
| SettingsWindow Spotlight 卡片（AXAML + 后端）| **主 Agent** | 紧耦合 AXAML 字段 + 后端 handler，自己做更稳 |
| 19 个测试 + NativeAOT 验证 | **mimo-agent** | 测试照搬模板，验证全通过（独立复核：测试文件 + exe 大小都对得上）|
| 文档（09-launcher.md + handoff §3i）| **主 Agent** | 设计本人最清楚 |

**和第二十批对比**：本批 mimo-agent 只做测试 + 验证（最机械），核心代码全部主 Agent 做。原因是 SpotlightWindow 的键盘导航 + 焦点管理 + Active class 状态机有大量细节决策，不适合委托。

### 用户真机待验证（R32，bash 无法触发）

1. **QuickTools toggle**：按 Ctrl+Alt+Q 弹出 → 再按一次应关闭（不再"无反应"）
2. **Spotlight 唤出**：按 Ctrl+Alt+Space → 应在屏幕中央弹出搜索面板
3. **搜索过滤**：输入字符 → 列表应实时过滤
4. **↑↓ 选中**：箭头键移动玉色高亮，到底/到顶不循环
5. **Enter 启动**：回车启动当前选中项（需先在设置页加几个启动项）
6. **Ctrl+Enter 编辑**：跳到设置页启动器分区 + 打开该 entry 编辑窗
7. **Esc 关闭**：ESC 应关闭面板
8. **Spotlight toggle**：再按 Ctrl+Alt+Space 应关闭（同 QuickTools）
9. **设置页启动器分区 Spotlight 快捷键卡片**：启动器分区应有"搜索面板快捷键"卡片，可改键

---

> 这是一个**反复迭代**的过程，记录所有尝试过的方案（包括失败的）非常重要——避免下一位 Agent 重蹈覆辙。读这一节时重点看"为什么某些方案被放弃"。

### 迭代时间线（按发生顺序）

| # | 方案 | 结果 | 放弃/保留原因 |
|---|---|---|---|
| 1 | 预填框 RunHidden（SW_HIDE→查 UIA→SW_SHOW） | ✅ 跟随工作，但 ❌ 闪烁 | 每次查询 hide/show 整个 overlay，<30ms 但人眼可见 |
| 2 | WS_EX_TRANSPARENT + WS_EX_LAYERED | ❌ UI 完全卡死 | 点击穿透到桌面，Avalonia 收不到事件（见教训 1） |
| 3 | UIA_WindowVisibilityOverridden=2 prop | ✅ 跟随 + 不闪 + 不卡，但 ❌ 只返回大框 | UIA 只返顶层元素，不深入细节（见教训 2） |
| 4 | UIA 优先 + OCR 兑底（`GetTextsInRegion` BFS） | ❌ 用户报"框外内容混入" | UIA 祖先容器远大于画框（见教训 3） |
| 5 | **默认走 OCR，UIA 整体改为可选开关** | ✅ **当前方案** | "框内即所得"比 UIA 的"可能准"重要 |
| 6 | 换 OCR 模型：DeepSeek-OCR → Qwen3.5-4B + 关思考 | ✅ **当前方案** | DeepSeek-OCR 在桌面截图上严重幻觉 |

### 当前最终方案（第十二批收尾）

**画框 OCR 默认走云端 OCR**，UIA 路径整体改为可选（`VisionCaptureSettings.UiaPrefillEnabled`，默认 false）：

1. 全局快捷键（默认 Ctrl+Alt+Q；chord 可选）→ QuickTools → 📐 画框识别文字 → 弹全屏遮罩
2. **用户手动画框**（默认无预填框，精确控制）
3. 确认 → `CaptureAndRecognizeRegionAsync`：
   - **默认（UiaPrefillEnabled=false）**：直接走 OCR，框内即所得
   - **可选（UiaPrefillEnabled=true）**：先 UIA `GetTextsInRegion` 扫框内文字 → 空才 OCR
4. OCR 结果进剪贴板 + QuickTools 弹回显示

**OCR 模型**：从 `deepseek-ai/DeepSeek-OCR` 换成 `Qwen/Qwen3.5-4B`（用户 vision.json 已改）。Qwen3.5-4B 是混合推理模型，必须 `enable_thinking:false`（否则 9-14s），关思考后 **<1s**。新增 `VisionCaptureSettings.DisableThinking` 字段控制。

### 关键代码入口（第十二批最终状态）

| 文件 | 角色 |
|---|---|
| `src/SelectionAssistant.App/SelectionRuntime.cs:CaptureAndRecognizeRegionAsync` | UIA tier（opt-in）→ OCR tier（默认）两阶段 |
| `src/SelectionAssistant.Platform.Windows/Capture/WindowsUiAutomationBackend.cs:GetTextsInRegion` | UIA 框内文字扫描（BFS + 走祖先找最小容器）|
| `src/SelectionAssistant.Platform.Windows/Capture/WindowsUiAutomationBackend.cs:FindSmallestContainingAncestor` | 走祖先链找包含 region 的最小容器（`MaxAncestorDepthForRegionRoot=4`）|
| `src/SelectionAssistant.UI/Views/RegionSelectOverlay.axaml.cs:EnableLiveTracking/TryLiveTrack` | UIA 预填框跟随（opt-in，40ms 节流）|
| `src/SelectionAssistant.UI/Views/RegionSelectOverlay.axaml.cs:MarkInvisibleToUia` | 设 `UIA_WindowVisibilityOverridden=2` prop 让 UIA 跳过 overlay |
| `src/SelectionAssistant.Providers/OpenAiCompatibleVisionOcrClient.cs:BuildRequestBody` | 按 `disableThinking` 决定是否发 `enable_thinking:false` |
| `src/SelectionAssistant.Providers/OpenAiCompatibleVisionOcrClient.cs:CleanOcrText` | 去 `<think>` 块（public static，+8 测试）|
| `src/SelectionAssistant.Providers/OpenAiCompatibleVisionOcrClient.cs:RecognizeRawAsync` | 诊断用，返回原始 HTTP body（`OcrRawResult` record）|
| `src/SelectionAssistant.Core/Capture/VisionCaptureSettings.cs` | +`UiaPrefillEnabled`（默认 false）+`DisableThinking`（默认 false）|
| `src/SelectionAssistant.App/App.axaml.cs:OnRegionOcrRequested` | 按 `UiaPrefillEnabled` 决定是否接 live tracker |
| `src/SelectionAssistant.App/Program.cs` | `--probe-uia-region`（UIA 框内扫描诊断）+ `--probe-ocr-raw`（OCR 原始 body 诊断）|

### 机器侧验证（2026-07-18 第十二批）

- `--probe-uia` EXIT=0
- `--probe-bounds 960 540` → `(768,144) 1794x1415` EXIT=0
- `--probe-uia-region 0 0 500 300` → 9 元素 / 41ms（UIA 框内扫描可用）
- `--probe-ocr-raw`（Qwen3.5-4B + 关思考）→ 872ms / 571ms，文字干净（对比 DeepSeek-OCR 862ms 幻觉、Qwen3.5-4B 开思考 9264ms）

### ⚠️ 关键教训（永久记录，避免重蹈覆辙）

**教训 1：`WS_EX_TRANSPARENT` + `WS_EX_LAYERED` 会破坏 Avalonia 事件路由**
- 调查 Everywhere 代码库（`C:/dvr/gh-kb/sources/Everywhere`）发现：Everywhere 的 `ScreenSelectionSession` 只设 `WS_EX_TRANSPARENT`，**不设 `WS_EX_LAYERED`**。
- 原因：Avalonia 12.x 用 `WS_EX_NOREDIRECTIONBITMAP`（DirectComposition），不用 `WS_EX_LAYERED`。单独的 `WS_EX_TRANSPARENT`（无 LAYERED）对 hit-test 是 no-op。
- 如果两个都设（我第一次试的）→ 点击穿透 → Avalonia 收不到 PointerMoved → UI 卡死。
- **正确做法**：用 `UIA_WindowVisibilityOverridden=2` prop（让 UIA 跳过 overlay），不要动窗口 style。

**教训 2：`UIA_WindowVisibilityOverridden=2` 让 UIA 只返大框，不深入细节**
- 设了这个 prop 后，UIA 的 `ElementFromPoint` 能穿透 overlay 查到下层元素，但只返回**顶层窗口的 bounds**，不返回嵌套的子控件。
- 用户报"只有大框，没有细节小框"。原因：prop 改变了 UIA 的查询深度行为。
- 如果只要 UIA 预填框跟随，这个 prop 够用；但要细节，得用 `GetTextsInRegion` 的 BFS 遍历。

**教训 3：UIA 取词的"框内即所得"不可靠**
- `GetTextsInRegion` 用"走祖先找最小包含容器"策略，但在很多软件里 UIA 树结构和视觉框不一致——祖先容器远大于用户画的框，结果扫到了框外内容。
- 用户报"UIA 的识别完全不遵循我画的框，把软件其他部分放到剪贴板"。
- **结论**：UIA 适合"取焦点元素文字"（划词场景），不适合"取框内文字"（画框场景）。画框默认必须走 OCR。

**教训 4：DeepSeek-OCR 在桌面截图上严重幻觉**
- 实测 `deepseek-ai/DeepSeek-OCR` 会输出完全不相关的内容（百度贴吧、健康数据、菜谱...），不是 prompt 问题，是模型在桌面场景不可靠。
- `--probe-ocr-raw` 诊断确认：原始 body 里就是幻觉内容，不是客户端拼接错误。
- 换 `Qwen/Qwen3.5-4B` 后干净准确。

**教训 5：Qwen3.x 必须关思考**
- Qwen3.5-4B 开思考 9-14s（reasoning_content 占大量 token，虽然 SSE parser 不读它，但模型还是要生成完才能结束）。
- `enable_thinking:false` 后 <1s，提升 10-25x。
- 但纯 OCR 模型（DeepSeek-OCR/PaddleOCR-VL）不认这个参数，会报 HTTP 400。所以做成 per-model 可配开关。

**教训 6：mimo-agent 适合执行类任务，不适合架构决策**
- 本会话大量使用 `mimo-agent` sub-agent 跑构建/测试/探针/实验，省主对话 token。
- 但 mimo-agent 修 bug 时容易引入新问题（比如修 COM 内存 bug 时漏改探针的 OCR client 构造，导致 `disableThinking` 没传进去）。架构决策和核心逻辑必须主 Agent 做。

---

## 3a. 本会话（第七批增量）完成的工作：R24 视觉取词

### 轨道 A：UIA 选区强化（`WindowsUiAutomationBackend.cs`）
- 候选根 2 → 3：命中测试元素 + 焦点元素 + **桌面根 `GetRootElement`**（vtable slot 5）
- 祖先链 **5 → 8 层**（`DefaultMaxAncestorDepth`）
- 选区读不到时**读元素全文**（Pass 3）：TextPattern `get_DocumentRange`（slot 7）→ `GetText`（slot 12），再退 ValuePattern `get_CurrentValue`（slot 4），上限 4000 字
- 新增 `GetElementBoundsAt`（读 `get_CurrentBoundingRectangle` slot 43）供 phase 2 截图取包围盒
- **所有 vtable 槽位从 `UIAutomationClient.h` 逐个数出**，不靠猜

### 轨道 B①：云端 OCR 两阶段兜底（默认开；当时用 DeepSeek-OCR，后换 Qwen3.5-4B，见 §3b）
  - phase 1 `CaptureAsync`：UIA → 剪贴板，有文本立即出工具条（<100ms，无 OCR 延迟）
  - phase 2 `CaptureVisionAsync`：phase 1 空 + `VisionTierAvailable` 才触发，显示"识别中…" → 截图 → OCR，**独立 5s 超时**，失败/空→隐藏工具条
- 新增文件：`ScreenRegionCapture`（Win32 BitBlt + 手写 PNG，AOT 安全无 System.Drawing）、`OpenAiCompatibleVisionOcrClient`（多模态 chat，复用 Provider/SSE/DPAPI）、`VisionTextCapture`、`VisionCaptureSettings` + `VisionCaptureStore`（`vision.json`）、`IVisionOcrClient`（Core 契约）
- `CaptureSource` 新增 `Vision`；`ISelectionTextCapture` 新增默认方法 `CaptureVisionAsync` + 属性 `VisionTierAvailable`（不破坏现有实现者）
- `SelectionSessionManager.SessionCoreAsync` 两阶段编排 + generation 守卫；`ISelectionSessionView.ShowVisionPending()`
- 设置页"视觉识别"卡片（ToggleSwitch + Provider/模型下拉 + 提示词），`VisionModelPresets` 预置 DeepSeek-OCR/PaddleOCR-VL 等
- 测试 +7：2 两阶段流程 + 5 VisionCaptureStore；**133/133 通过，NativeAOT 0 警告，exe 24.4MB**
- 新增 `--probe-vision [x y w h]` 诊断探针（`Program.cs`）：不经过选词会话，直接截图→OCR→打印文字+耗时，是验证轨道 B① 新代码的唯一 CLI 手段
- **新增 SiliconFlow 到 `ProviderPresets.BuiltIn`**：设置页"+ 添加 Provider"里可选 SiliconFlow（自动填 baseUrl + 翻译默认模型 + `secret://provider/siliconflow`），与 `vision.json` 默认 `providerId` 完美匹配。用户无需手编 JSON，全在 UI 配 Provider + 密钥。
- 新增 `docs/vision.example.json`；更新 `docs/providers.example.json`（加 SiliconFlow 条目）

### 发布 + 机器侧验证（2026-07-18）
- 已发布到 `artifacts\publish\win-x64-nativeuia\`（覆盖旧版，24.4MB）
- `--probe-uia` = 0（轨道 A UIA 后端强化后仍正常初始化）
- **`--probe-vision` 端到端通了**：SiliconFlow + DeepSeek-OCR，529ms 识别出屏幕文字（exit 0）。OCR 截图编码 + 多模态请求 + SSE 解析全部验证通过。
- 托盘启动 4s 存活（`ConfigureVisionCapture` 接线不崩）；单实例锁第二实例静默退出 0

### 真机验证中修的 3 个 bug（2026-07-18，用户反馈）
1. **设置页"新增 Provider 后跳回默认 + 保存后恢复默认 + 不显示已输密钥"**：根因是 `SetProviders` 每次刷新都把 combo 跳回 `DefaultProviderId`，丢掉用户正在编辑的选择。修复：新增 `_editingProviderId` 跟踪用户当前编辑的 Provider，刷新时优先选中它；新增 `SelectProviderForEditing`，添加 Provider 后选中刚加的。
2. **OCR 报 HTTP 400**（两个根因，诊断时发现的）：
   - **PNG chunk 顺序写反**：`WriteChunk` 写成 `[length][type][CRC][data]`，spec 是 `[length][type][data][CRC]`。产出无效 PNG → SiliconFlow 报 `not a valid image`。已修。
   - **OCR client 发 `enable_thinking` 被拒**：DeepSeek-OCR 是纯 OCR 模型，不认 thinking 参数 → SiliconFlow 报 `code 20015: does not support parameter enable_thinking`。已从 `OpenAiCompatibleVisionOcrClient.BuildRequestBody` 移除 thinking 相关字段（OCR 不该发，与翻译 provider 不同）。
3. **OCR 错误响应体之前被丢弃**：`OpenAiCompatibleVisionOcrClient` 非 2xx 时现在读出响应体并拼进异常消息（否则 400 是黑箱，没法诊断）。
- **教训**：手写的二进制格式（PNG）必须有真实端到端验证——单元测试没法验证"图片服务能解开"。这次是靠 `--probe-vision` + curl 隔离测试（假密钥：参数对→401，参数错→400）才定位到。
- 清理：用户 `providers.json` 有 2 个垃圾 `custom-*` 条目（添加流程 bug 的产物），已清掉只留 deepseek + siliconflow（均有密钥）；`vision.json` providerId 改回 siliconflow。

---

## 3. 本会话（第五批增量）完成的工作

### R14 划词工具条（ToolbarWindow）功能完善
- 删掉死的"自定义"按钮
- 加**复制**按钮（选中文本 → 剪贴板）
- 加**粘贴**按钮（注入 Ctrl+V 到源应用，替换选中的可编辑文本）——`SendInputHelper.SendPasteChord()`
- 解释/总结按钮接通（复用 `RunActionAsync`）——之前是硬编码禁用的死按钮
- 按钮缩小（Padding 6,2 / FontSize 11），加**折叠展开区**（自定义功能通过"▼"展开第二行显示）
- `ToolbarWindow.SetActions()` 接收动态自定义功能列表

### R15 自定义功能系统重构（"提示词模板"→"自定义功能"）
- **数据模型**：`PromptTemplateSet` 从 3 固定属性改为 `List<PromptTemplate>`（翻译/总结/解释内置 + 任意 custom- 动作）
- **CRUD**：`Add(template)` / `Remove(actionId)` / `IsBuiltIn(id)` / `IsCustom(id)`；自定义 id 用 `custom-{guid}` 前缀
- **设置页**：3 行硬编码 Grid → `ItemsControl` 动态行 + "＋ 新增功能"按钮 + 编辑/删除按钮
- **编辑窗口**：支持新建模式（`ShowForNew`，名称可编辑）+ 编辑模式（`ShowFor`）
- **QuickTools**：3 个硬编码按钮 → `ItemsControl` 动态按钮（`SetActions`）
- **运行时**：`AddPromptTemplateAsync` / `DeletePromptTemplateAsync`
- **持久化**：`PromptTemplatesStore` 遍历列表保存/加载；内置 3 个等于默认时省略，自定义始终写
- 测试：+6（Add/Remove/RoundTrip/Load/IsBuiltIn/IsCustom）

### R16 单实例锁
- `Program.Main` 命名 Mutex `Global\BYH_ByYourHand_SingleInstance`
- 第二个实例静默退出；探针分支（--probe-*/--set-secret）跳过锁

### R17 启动入口
- `create-launchers.ps1`：生成桌面快捷方式 `BYH.lnk` + 项目根 `BYH.cmd`
- 托盘"重启 BYH"

### R18 chord 相关修复（历史，记录踩坑）
- **chord 定位双重缩放 bug**：坐标乘了 RenderScaling → 面板跑到屏幕外。修复：不乘缩放 + `ClampToScreen`。
- **chord "只能触发一次"**：根因是 grace window 内 `Activate()` 重入循环冻结 UI 线弦。修复：grace window 内绝不 Activate，只忽略 Deactivated。
- **chord 时间窗口**：400ms → 600ms。
- **chord 面板可拖动**：`BeginMoveDrag`。

### R19 项目文档体系（`docs/architecture/`）
新建 8 份架构文档（621 行）：
- `00-architecture-overview.md` — **入口**：架构图 + 模块速查表（改 X 先看 Y）+ 数据流 + 不变量
- `01-selection-capture.md` — 选词链路
- `02-windowing.md` — 五窗口 + 定位/clamp/chord grace window 踩坑
- `03-translation-provider.md` — Provider/SSE/热切换
- `04-prompt-templates.md` — 自定义功能系统
- `05-configuration-persistence.md` — JSON 配置/密钥/原子写
- `06-security-invariants.md` — 11 条安全硬规则
- `07-build-publish-run.md` — 构建/发布/启动/探针

### R20 误触发修复尝试（第一版，⚠️ 未生效）
- `SessionCoreAsync` 改为先取词后显示（有文本才弹工具条）
- 测试 +2：`NoSelectedText_DoesNotShowToolbar` / `ManualFallbackSourceWithNoText_DoesNotShowToolbar`
- ⚠️ 此版本用户反馈"一点没修"——根因见 §2 问题 A（ManualFallback 后门）

### R21 布局修复尝试（第一版，⚠️ 未生效）
- 窗口 `SizeToContent="WidthAndHeight"` + StatusText 列 `Auto`
- ⚠️ 此版本用户反馈"一点没修"——根因见 §2 问题 B（StatusText 在 Auto 列里文字长度撑动按钮区）

### R20-FIX / R21-FIX / R22 真正修复 + 验证（本会话）
- **R20-FIX**：`SelectionSessionManager.cs:175` 删除 ManualFallback 后门特例 → `_lastCapturedText is null` 一律不显示。测试改为 `ManualFallbackSourceWithNoText_DoesNotShowToolbar`。
- **R21-FIX**：`ToolbarWindow.axaml` 主行 Grid `Auto×6,*,Auto`；StatusText `*` 列 + `MinWidth=80` + 右对齐；按钮统一 `Padding="8,3"`、`ColumnSpacing=4`。
- **R22**：用户真机确认——"新增功能没问题，误触也已经修复，chord 连续触发也已经解决了"。R20/R21/R18(chord) 三项全部关闭。

---

## 4. 用户已经确认的产品决定

- 品牌固定为大写 `BYH`，全称 `By Your Hand`。
- Windows 优先；长期目标含 macOS，当前无 macOS 实现。
- 工具条不抢焦点，必须保住选中文字高亮。
- 翻译**默认关闭模型思考模式**；仅自定义功能可经 ThinkingEnabled 开启。
- **多厂商 Provider 管理**：内置预设 + 自定义；设置页增删改 + 热切换。
- **自定义功能**（R15）：翻译/总结/解释 3 个内置 + 任意用户自定义，所有 Provider 共用一套，存 `prompt-templates.json`。内置 3 个不可删。
- **键盘快捷键（默认 Ctrl+Alt+Q）**触发 QuickTools 浮层面板（R25）。左右键同按（chord）默认关闭，可在设置页兼容性开启。
- **划词工具条**（ToolbarWindow）：翻译/解释/总结/Prompt/复制/粘贴 + 自定义功能折叠展开 + 画框 OCR。
- **粘贴**（划词弹窗）= 用剪贴板文字替换源应用选中的可编辑文本（注入 Ctrl+V）。
- 弹窗置顶：PromptWindow + QuickToolsWindow 置顶；Settings/Result/Toolbar 不置顶（Toolbar 用 Topmost 但 NOACTIVATE）。
- app 图标 = 用户提供的人物头像透明 PNG。

---

## 5. 当前验证证据

最后验证：2026-07-20（第四十二批：R54 金属质感双圆角结构框）。

- **第四十二批当前发布物**：`artifacts/publish/win-x64-nativeuia/BYH.exe`，27,670,528 bytes；当前 PID 43192 已从该分支 worktree 路径启动并恢复默认窗口尺寸。
- **第四十二批自动测试**：**232/232**（Core 156 + Providers 35 + Windows 41），0 失败、0 跳过。
- **第四十二批视觉复核**：NativeAOT 默认尺寸与 175% DPI、1240×680 logical 最小尺寸均通过；证据为 `ivory-jade-settings-v10-metallic-*-nativeaot.png`。
- **第四十二批结构审计**：结构窗格统一使用 `MetallicFrame`；外金属渐变、象牙缝与浅金内曲线同心；FlatRail/内部卡片未被过度装饰。

- `dotnet test`：**213/213**（Core 137 + Providers 35 + Windows 41），0 失败、0 跳过。
- NativeAOT 发布成功，**0 AOT/裁剪警告**；exe 26,899,968 bytes（~25.6MB）。
- **机器侧探针全通**：
  - `--probe-uia` EXIT=0
  - `--probe-bounds 960 540` → `(768,144) 1794x1415` EXIT=0
  - `--probe-uia-region 0 0 500 300` → 9 元素 / 41ms（UIA 框内扫描可用）
  - `--probe-ocr-raw`（Qwen3.5-4B + 关思考）→ 872ms / 571ms，文字干净 EXIT=0
- **用户 providers.json 当前配置**：`defaultProviderId=deepseek`（翻译走 `deepseek-v4-flash`）；两个 Provider（deepseek + siliconflow）均绑了 DPAPI 密钥。
- **用户 vision.json 当前配置**：`providerId=siliconflow`, `model=Qwen/Qwen3.5-4B`, `disableThinking=true`, `uiaPrefillEnabled=false`（OCR 与翻译解耦）。
- **第十九批翻译探针（2026-07-18）**：`--probe-translate-speed "The quick brown fox..."` → DeepSeek (deepseek-v4-flash)，TTFB 1660ms，总耗时 1999ms，译文「黎明时分，一只敏捷的棕色狐狸在河岸附近跳过那条懒狗。」。
- **第十九批 OCR 探针（2026-07-18）**：`--probe-vision 0 0 400 200` → SiliconFlow + Qwen3.5-4B，728ms，干净识别（OCR 与翻译切换互不影响）。
- **第十九批重启 race 验证（2026-07-18）**：单实例锁正常挡住第二个普通实例；旧实例 kill 后用 `--restart` 启动的新进程成功拿到 Mutex（abandoned 路径）；Avalonia 容忍 `--restart` 未知参数；新进程日志显示正常加载配置。**待用户真机点托盘「重启 BYH」做最后一步行为验证**。
- **R24 真机确认（2026-07-18）**：用户确认 Qwen3.5-4B 画框 OCR 效果很好。R24 完成。
- **R25 桌面实测（2026-07-18）**：设置页显示快捷键区；默认 Ctrl+Alt+Q 打开 QuickTools；改成 Ctrl+Alt+Shift+Q 后旧组合失效、新组合生效；恢复 Ctrl+Alt+Q；左右键 chord 默认不打开面板。
- **R26 视觉实测（2026-07-18）**：175% DPI 截图检查 Settings/QuickTools；QuickTools 首轮重叠已修复并二次通过；实际打开 RegionSelectOverlay；发布版 Settings + QuickTools 同时可见。
- **第二十批 R23 启动器验证（2026-07-18）**：`dotnet test` 194/194（+32 新测试）；NativeAOT 0 警告，exe 26.8MB；`--probe-icon-extract notepad.exe/chrome.exe/cmd.exe/msedge.exe` 全通（28×28 RGBA PNG，Gemini Vision 确认是清晰的应用图标）；NativeAOT 发布版仍可正常提取图标（关键风险点消除）；`--probe-launcher-list` 在配置文件不存在时不崩。**待用户真机从设置页添加启动项 + 从 QuickTools 点启动做最后行为验证**。
- **第二十一批 R32 Spotlight 验证（2026-07-18）**：`dotnet test` 213/213（+19 新测试：9 SpotlightTriggerSettings + 10 SpotlightTriggerStore）；NativeAOT 0 警告，exe 26.9MB（+58KB，SpotlightWindow + 主题样式合理增长）；编译 0 警告 0 错误（Debug + Release 都验）。**待用户真机按 Ctrl+Alt+Space 弹出搜索面板 + ↑↓/Enter/Ctrl+Enter/Esc 导航做最后行为验证**。
---

## 6. 构建和运行

```powershell
Set-Location 'C:\dvr\gh-kb\selection-assistant'
dotnet test SelectionAssistant.slnx -c Debug --nologo
dotnet publish src\SelectionAssistant.App\SelectionAssistant.App.csproj -c Release -r win-x64 --nologo
Copy-Item src\SelectionAssistant.App\bin\Release\net10.0-windows\win-x64\publish\* artifacts\publish\win-x64-nativeuia\ -Force
.\artifacts\publish\win-x64-nativeuia\SelectionAssistant.App.exe
```

启动入口：桌面 `BYH.lnk` / 项目根 `BYH.cmd` / 托盘"重启 BYH"。

---

## 7. 关键代码入口

### 应用生命周期
- `src\SelectionAssistant.App\Program.cs` — 入口；探针分支（含 **R24 `--probe-vision`**：截图→OCR 端到端）；**单实例 Mutex**（R16）；启动 Avalonia。
- `src\SelectionAssistant.App\App.axaml.cs` — 六窗口（+ **R24 `RegionSelectOverlay`**）+ TrayIcon；事件接线（Provider/自定义功能 CRUD / VisionSettingsSaved / **R24 画框 OCR：`OnRegionOcrRequested`/`OnRegionSelected`**）；全局热键→QuickTools，chord 为可选兼容入口；重启。
- `src\SelectionAssistant.App\SelectionRuntime.cs` — 组合根；钩子/会话/Provider/自定义功能生命周期；**R24 `ConfigureVisionCapture` + `UpdateVisionSettings` + `GetInitialRegionAt` + `CaptureAndRecognizeRegionAsync`**（画框 OCR 入口）；`OnPasteRequested`（Ctrl+V 注入）。

### 自定义功能系统（R15）
- `src\SelectionAssistant.Core\Translation\PromptTemplates.cs` — `PromptTemplate`/`PromptTemplateSet`（List 模型）/`PromptActionIds`。
- `src\SelectionAssistant.Infrastructure\Configuration\PromptTemplatesStore.cs` — 持久化。
- `src\SelectionAssistant.UI\Views\SettingsWindow.axaml(.cs)` — "自定义功能"卡片（ItemsControl）。
- `src\SelectionAssistant.UI\Views\PromptTemplateEditWindow.axaml(.cs)` — 编辑/新建弹窗。
- `src\SelectionAssistant.UI\Views\QuickToolsWindow.axaml(.cs)` — 动态功能按钮。
- `src\SelectionAssistant.UI\Views\PromptFunctionRow.cs` — 行 ViewModel（public top-level）。
- `src\SelectionAssistant.UI\Views\RelayCommand.cs` — NativeAOT 安全 ICommand。

### 选词 + 取词（R20 修误触发；R24 视觉取词已实现）
- `src\SelectionAssistant.Core\Selection\SelectionSessionManager.cs` — 会话管理；**R20 已删除 ManualFallback 后门**；**R24 SessionCoreAsync 改两阶段**（phase1 快路径 + phase2 视觉）。
- `src\SelectionAssistant.Core\Selection\SystemMetricGestureClassifier.cs` — 手势判定（拖拽/双击）。
- `src\SelectionAssistant.Platform.Windows\Capture\WindowsUiAutomationBackend.cs` — UIA 取词核心。**R24 轨道 A 已强化**：3 候选根（命中测试+焦点+桌面根）、祖先链 8 层、选区空时读 DocumentRange/ValuePattern 全文；`GetElementBoundsAt` 供截图。
- `src\SelectionAssistant.Platform.Windows\Capture\WindowsSelectionTextCapture.cs` — UIA→剪贴板降级链（phase 1）；**R24 phase 2** `CaptureVisionAsync`（5s 超时）+ `VisionTierAvailable`。
- `src\SelectionAssistant.Platform.Windows\Capture\ScreenRegionCapture.cs` — **R24 新增**：Win32 BitBlt + 手写 PNG 编码 → base64。
- `src\SelectionAssistant.Platform.Windows\Capture\VisionTextCapture.cs` — **R24 新增**：截图取区域 + 调 OCR。
- `src\SelectionAssistant.Providers\OpenAiCompatibleVisionOcrClient.cs` — **R24 新增**：多模态 OCR（复用 Provider/SSE/密钥）。
- `src\SelectionAssistant.Infrastructure\Configuration\VisionCaptureStore.cs` + `src\SelectionAssistant.Core\Capture\VisionCaptureSettings.cs` — **R24 新增**：`vision.json` 持久化（默认 Qwen3.5-4B + disableThinking=true）。

### 窗口系统（R21 已修复布局）
- `src\SelectionAssistant.UI\Views\ToolbarWindow.axaml(.cs)` — 划词工具条；**R21 改 Auto×6,*,Auto 布局**。
- `src\SelectionAssistant.Platform.Windows\Windowing\NoActivateWindowHost.cs` — `ShowAtNoActivate` 用 `SWP_NOSIZE`（已确认不阻止 SizeToContent）。

### Provider + 翻译
- `src\SelectionAssistant.Providers\OpenAiCompatibleStreamingProvider.cs` — 流式 + 条件 thinking-disable。
- `src\SelectionAssistant.Core\Translation\TranslationSessionManager.cs` — 流式 + generation 守卫。

### R25 QuickTools 全局键盘快捷键
- `src\SelectionAssistant.UI\Views\SettingsWindow.axaml(.cs)` — 快捷键设置卡片（ToggleSwitch 启停 + 修饰键 Ctrl/Alt/Shift/Win 复选框 + 主键 A-Z/0-9/F1-F12/Space 下拉）。
- `src\SelectionAssistant.App\App.axaml.cs` — 注册/注销全局热键；热键回调 → QuickTools.ShowAt。
- `src\SelectionAssistant.Core\Input\QuickToolsTriggerSettings.cs` + `src\SelectionAssistant.Infrastructure\Configuration\QuickToolsTriggerStore.cs` — `quick-tools.json` 持久化（默认 Ctrl+Alt+Q，chord 默认关闭）。
- `src\SelectionAssistant.Platform.Windows\Input\WindowsGlobalHotKey.cs` — 专用消息线程 + RegisterHotKey / MOD_NOREPEAT。

### R26 Ivory Jade 主题
- `src\SelectionAssistant.UI\Themes\IvoryJade.axaml` — 唯一主题事实源；颜色/Brush/圆角/阴影/组件状态类。
- `src\SelectionAssistant.App\App.axaml` — Light variant + StyleInclude。
- `docs\architecture\08-theme-system.md` — 主题使用规则、组件映射、验证方法和永久不变量。

---

## 8. 关键不变量和已踩坑（⚠️ 永久记录）

详见 `docs/architecture/06-security-invariants.md`（11 条）。核心：
- 钩子始终 CallNextHookEx 放行；回调不碰 UI。
- WS_EX_NOACTIVATE 永不 SetForegroundWindow。
- 密钥 DPAPI，不进明文 JSON。
- 0 警告（TrimMode=full）；DataTemplate 绑定类型 public top-level。
- 配置原子写入；Utf8JsonWriter 手写。
- **chord grace window 绝不 Activate()**（重入冻结 UI 线程）。
- **chord 定位不乘 RenderScaling**（双重缩放把面板推到屏幕外）。
- **SelectionSessionManager 守卫不给 ManualFallback 开后门**（R20 根因）：`WindowsSelectionTextCapture` 在 UIA+剪贴板都失败时返回 `ManualFallback`，无法区分"选了词读不出"和"没选词"，所以会话层必须无视它——无文本一律不显示工具栏（phase 2 视觉空结果也走这条，隐藏工具条）。
- **R24 视觉 OCR 必须两阶段，不能塞进主链路串行 await**（本会话教训）：初版把 `CaptureVisionAsync` 放进 `CaptureAsync` 串行链，导致"选不中"内容松鼠标后干等 1-3s 无反馈。修正：Vision 拆成 phase 2，phase 1 空且 `VisionTierAvailable` 才触发；`ShowVisionPending()`（"识别中…"）必须在 `await CaptureVisionAsync` **之前**调用；Vision 独立 5s 超时，失败/空静默隐藏工具条。详见 `docs/architecture/01-selection-capture.md`。
- **R24 region overlay 实时跟踪必须"用户一碰就停"**（第十一批教训）：UIA 跟踪如果不停，用户开始画框时会出现"我画，模型也在挪"的拉锯。修法：`PointerPressed` 在画/移/调三处都 `_userTouchedRect = true`；`TryLiveTrack` 在 `_userTouchedRect` 时直接 return。下次 `ShowWithInitialRect` 才复位。永远让手动编辑赢。
- **R24 OCR 多余文字优先怀疑模型，不是客户端**（第十一批教训，第十二批更新）：DeepSeek-OCR 在桌面截图上**严重幻觉**（输出完全不相关内容如百度贴吧、菜谱）。这不是 prompt 问题，是模型在桌面场景不可靠。诊断必须先跑 `--probe-ocr-raw` 看原始 body。**最终解法**：换 `Qwen/Qwen3.5-4B`（关思考，<1s，干净准确）。不要在没看到原始 body 的情况下盲改客户端。
- **R24 WS_EX_TRANSPARENT + WS_EX_LAYERED 会破坏 Avalonia 事件路由**（第十二批教训，⚠️ 永远不要这样组合）：Avalonia 12.x 用 `WS_EX_NOREDIRECTIONBITMAP`（DirectComposition），单独的 `WS_EX_TRANSPARENT`（无 LAYERED）对 hit-test 是 no-op。如果两个都设 → 点击穿透 → Avalonia 收不到事件 → UI 卡死。正确做法：让 UIA 跳过 overlay 用 `UIA_WindowVisibilityOverridden=2` prop（`MarkInvisibleToUia`），不要动窗口 style。详见 §3b 教训 1。
- **R24 画框场景 UIA 不可靠，必须默认走 OCR**（第十二批教训）：UIA 的"框内即所得"在很多软件里不成立——UIA 树结构和视觉框不一致，祖先容器远大于画框，扫到框外内容。用户报"UIA 把软件其他部分放到剪贴板"。**结论**：UIA 适合"取焦点元素文字"（划词），不适合"取框内文字"（画框）。画框默认必须走 OCR，UIA 改为可选开关（`UiaPrefillEnabled`，默认 false）。详见 §3b 教训 3。
- **R24 Qwen3.x 必须关思考（enable_thinking:false）**（第十二批教训）：混合推理模型开思考 9-14s（reasoning_content 占大量 token），关思考后 <1s。但纯 OCR 模型（DeepSeek-OCR/PaddleOCR-VL）不认这个参数会报 HTTP 400。所以做成 per-model 可配开关（`VisionCaptureSettings.DisableThinking`）。详见 §3b 教训 5。
- **R26 主题必须保持语义资源单一事实源**：View 用 `DynamicResource` + Classes，C# 通过反馈 class 切换；不允许重新散落十六进制品牌色或构造 Brush。
- **R26 QuickTools 改内容后必须测 175% DPI**：固定高度浮层在编译/普通缩放下可能正常，但高 DPI 会发生底部重叠。
- **R28/R30 Settings 多窗格不变量**：默认 1320×800、最小 1240×680；品牌概览跨两排，导航只占上排，右侧真实配置摘要位于右上，底部 `SYSTEM OVERVIEW` 横跨导航与中央区，窗口操作位于右下；仅中央当前分区滚动。禁止把导航恢复为 `Grid.RowSpan="2"` 的全高独立列。摘要列必须显示运行时 Provider/快捷键/OCR，禁止虚构统计数据。
- **R29 人物欢迎卡不变量**：右上角人物必须复用真实 `app-icon.png`；辅助模块只能展示真实配置或静态产品说明，禁止为了接近参考图编造任务、消息、百分比或活动历史。
- **R30 设置页柔和边界不变量**：宝石 JPG 必须在圆形容器中放大裁切，不能重新露出源图四周；大窗格用 hairline/低 alpha 边界，避免恢复多层实金框；左侧三个说明区保持等高。
- **R31 重启 Mutex race 不变量**（第十九批教训）：单实例 Mutex 不能用 `using var singleInstance = new Mutex(...)` 局部变量——`RequestRestart` 在 spawn 新进程时旧进程还没退出 Main，Mutex 仍在 `using` 持有中，新进程会被 `if (!acquired) return 0;` 静默挡掉，**结果是旧进程也死了、新进程也没起来，托盘消失**。必须：(1) Mutex 提升为 static 字段；(2) `RequestRestart` 在 `Process.Start` **之前**调用 `Program.ReleaseForRestart()` 显式释放；(3) 给新进程传 `--restart` 参数，让它重试 Mutex（30×100ms=3s 上限）；(4) catch `AbandonedMutexException` 视为拿到。Mutex 有线程亲和性，`ReleaseMutex()` 必须由 acquire 它的同一线程（UI 线程）调用——catch ApplicationException 兜底。OCR Provider 与翻译 Provider 完全解耦（前者 `vision.json`，后者 `providers.json`），切换互不影响。
- **OCR 与翻译 Provider 解耦不变量**：OCR 走 `vision.json`（当前 SiliconFlow + Qwen3.5-4B），翻译走 `providers.json` 的 `defaultProviderId`（当前 DeepSeek + deepseek-v4-flash）。两者独立配置，禁止未来把它们合并成"单一 Provider"。
- **R23 启动器模块化不变量**（第二十批教训）：完整架构详见 `docs/architecture/09-launcher.md`。核心约束：(1) 完全复用 R15 模式（不要重发明 CRUD/Store/Row）；(2) 图标提取 best-effort + 永不在 UI 线程跑；(3) 参数替换在 Launch 时做，保存原始模板；(4) LocalApp 用 `UseShellExecute=false` 支持工作目录，WebUrl 用 `UseShellExecute=true` 走默认浏览器；(5) **不要用 System.Drawing.Common**（NativeAOT TrimMode=full 会裁剪），HICON → PNG 必须手写 SHGetFileInfo + GetIconInfo + 两遍 GetDIBits + 复用 `PngEncoder`；(6) **GetObject 对 DIB 不可靠**（err=203），必须用两遍 GetDIBits；(7) SHGetFileInfo 必须 `SetLastError=true`；(8) ValueTuple 不可空，用 `(string, string)?` + `is { } pending` 模式匹配；(9) EntrySaved 签名必须带 name（编辑模式允许改名）。
- **R23 mimo-agent 分工不变量**（第二十批再次验证）：mimo-agent 适合 1-N 个文件的机械实现/迁移/测试照搬（本次 5 UI 文件 + 32 测试全 0 错）；**不适合** Win32 互操作（HICON/P/Invoke 需要逐步诊断）+ 核心业务算法（ParameterReplace 两阶段）+ UI 控制流编排（事件订阅 + 异步图标加载）。主 Agent 做核心代码，mimo-agent 做执行和验证。

---

## 3l. 本会话（第四十六批增量）完成的工作：REQ-012 设置页头部卡片圆角阴影

### 改动

- `src/SelectionAssistant.UI/Views/SettingsWindow.axaml`
  - 顶部标题区（`WELCOME BACK / General / 状态 pills / IVORY JADE`）由平底边框改为 `Classes="LiftedPanel"`，获得 20px 大圆角、多层弥散阴影与顶部高光边，与参考图的欢迎卡片质感一致。
  - `ScrollViewer` 顶部内边距由 24 降至 14，使头部卡片与下方第一个 `LiftedPanel`（Ocean Eyes Trigger）视觉紧密相连。
- `artifacts/publish/win-x64-nativeuia/BYH.exe`：重新 NativeAOT publish。

### 验证

- `dotnet build -c Release`：0 警告 0 错误。
- `dotnet test -c Release`：334/334 通过。
- `dotnet publish -c Release -r win-x64`：0 警告。
- QA 截图：`artifacts/qa/ivory-jade-settings-v15-header-card-*-nativeaot.png`（默认 1320×800 + 最小 1240×680，175% DPI）。
- 用户日常 BYH 实例已恢复（PID 60348）。

---

## 9. 下一位 Agent 的明确执行顺序

### 当前状态：R24-R34 全部完成（R23 + R31 + R32 + R34 待真机行为验证）

**R24 画框 OCR**（2026-07-18 用户真机确认）：经过 6 轮迭代（见 §3b 时间线），最终方案是**默认走云端 OCR + UIA 可选开关**。默认配置：
- 手动画框 → OCR（框内即所得）→ Qwen3.5-4B + 关思考 → <1s 出干净文字
- UIA 预填默认关闭（`UiaPrefillEnabled=false`）

**R25 QuickTools 全局键盘快捷键**（2026-07-18 完成）：默认 Ctrl+Alt+Q 打开 QuickTools；设置页可启停、选择 Ctrl/Alt/Shift/Win + A-Z/0-9/F1-F12/Space；快捷键冲突或保存错误时旧快捷键继续有效并显示错误；左右键 chord 因与右键菜单冲突默认关闭，可在设置页兼容性开启。

**R26 Ivory Jade**（2026-07-18 完成）：统一主题资源覆盖七个窗口；测试 162/162；NativeAOT 成功；Settings/QuickTools/Region overlay 完成桌面视觉烟测。

**R27 设置页高保真重构**（2026-07-18 完成）：四分区侧栏 + 独立滚动 + 固定底栏；按用户参考图加入受控的玉石/珠光/暖金材料；175% DPI 默认和最小尺寸通过。

**R28 设置页多窗格工作台**（2026-07-18 完成）：按参考图空间骨架重排为四列两行——产品概念、独立导航、中央设置、右侧 Current setup、底部运行/诊断/窗口控制同时可见；摘要接真实 Provider、快捷键和 OCR 配置。默认 1320×800，最小 1240×680；175% DPI 默认/Provider/最小 Provider 截图通过。MiMo 审阅无 P1 阻断；162/162 测试与 NativeAOT 发布通过。

**R29 设置页视觉精修**（2026-07-18 完成）：右上角以真实 APP icon 构成人物欢迎卡；中央增加欢迎带与产品能力标签；底部增加诚实的 Ivory Jade 主题预览；边框和卡片改为轻量瓷器层级。175% DPI 默认/Provider/最小尺寸与 NativeAOT 资源显示均通过。

**R30 设置页局部精修**（2026-07-18 完成；2026-07-20 follow-up）：宝石改为放大裁切消除源图边缘；窗格和卡片使用更浅 hairline；右上人物区扩大并合并真实摘要；左侧三个说明区等高。第三十九批根据用户纠正，将导航限制在上排，并让下方 `SYSTEM OVERVIEW` 横跨导航与中央设置区；不要再恢复成全高独立导航列。175% DPI 默认/最小尺寸与 NativeAOT 均通过，最新证据见 `ivory-jade-settings-v7-corrected-*.png`。

**R30 第二次 follow-up**（2026-07-20 第四十批完成）：直接以用户参考图为准，设置面板统一为简洁英文，五个导航入口加入统一轮廓线图标；主窗格与卡片使用低对比金线、象牙内高光、香槟 glint 与暖色柔影形成层叠立体感。默认与 175% DPI 最小尺寸均通过，最新证据见 `ivory-jade-settings-v8-english-depth-*.png`。后续不要恢复冗长说明、混合语言、emoji 图标或粗深边框。

**R30 第三次 follow-up**（2026-07-20 第四十一批完成）：按用户红色标注图收紧导航塔双层边框到约 5px，将底重心装饰图移入导航内框；设置页下排由 204 增高到 260 logical px，System Overview 去标题分隔线；最左产品栏取消圆角外框，改用 1.5px 古金竖线分栏。默认与 175% DPI 最小尺寸均通过，证据见 `ivory-jade-settings-v9-annotated-*-nativeaot.png`。

**R54 金属质感 follow-up**（2026-07-20 第四十二批完成，分支 `task/REQ-012-metallic-frames`）：结构框改为单一 `MetallicFrame`，用渐变古金真实外缘 + 2 DIP 象牙缝 + 3 DIP 浅金内曲线 + 暖色底影复现参考图；移除无效的 `DecorativeFrame SettingsFrame` 属性覆盖组合。默认与最小 NativeAOT 视觉证据见 `ivory-jade-settings-v10-metallic-*-nativeaot.png`。

**R31 重启 Mutex race 修复**（2026-07-18，机器侧验证通过，**待用户真机点托盘「重启 BYH」最终确认**）：详见 §3g。修复"点重启 → 托盘消失且新进程没起"的 race。代码层已验证：Mutex static 字段、`ReleaseForRestart()` 在 spawn 前、`--restart` 重试 30×100ms、AbandonedMutex 兜底、Avalonia 容忍未知参数。

**翻译默认 Provider 切换（2026-07-18）**：`providers.json` 的 `defaultProviderId` 从 `siliconflow` 改为 `deepseek`（deepseek-v4-flash）。OCR 仍走 SiliconFlow + Qwen3.5-4B（`vision.json` 不变）。

**R23 快捷启动器**（2026-07-18 完成，机器侧全通过，**待真机行为验证**）：QuickTools 第 5 区 + 设置页第 5 分区 + 自动提取 exe 图标 + `{clip}/{sel}/{prompt:}` 参数。详见 §3h 和 `docs/architecture/09-launcher.md`。机器侧验证：32 个新测试全通过，NativeAOT 0 警告，4 个应用（notepad/chrome/cmd/msedge）的图标提取均产出有效 PNG。**用户真机要测**：(1) 从设置页"＋ 新增启动项"加 Chrome/网页；(2) 看图标是否自动出现；(3) 从 QuickTools 点启动项验证启动；(4) 试 `{sel}` 参数（选中文字后启动带这个 token 的项）。

**R32 SpotlightWindow + QuickTools toggle 修复**（2026-07-18 完成，机器侧全通过，**待真机行为验证**）：详见 §3i。两件事：(1) QuickTools 快捷键 toggle（再按一次关闭）；(2) 独立 Spotlight 搜索面板（Ctrl+Alt+Space，↑↓/Enter/Ctrl+Enter/Esc 导航，Ivory Jade 配色）。机器侧验证：213/213 测试（+19 新），NativeAOT 0 警告。**用户真机要测**：(1) QuickTools toggle；(2) Ctrl+Alt+Space 弹出搜索面板；(3) 搜索过滤 + ↑↓ + Enter + Ctrl+Enter + Esc；(4) 设置页启动器分区 Spotlight 快捷键卡片改键；(5) Spotlight 再按一次关闭（toggle）。

**R33 选区空弹窗修复**（2026-07-18 完成，用户真机已确认关闭）：详见 §3j。删 UIA Pass 2（祖先链）和 Pass 3（元素文本 fallback），只保留 Pass 1（直接 selection 读）。加 `[Capture]` 诊断日志作为永久诊断手段（每次划词打 `source/len/preview/proc`）。

**R34 工具栏动作快捷键**（2026-07-18 完成，机器侧全通过，**待真机行为验证**）：详见 §3k。划词弹出工具栏后按 F/J/Z（翻译/解释/总结）或任意配置的单字符（含润色等 custom）立即触发；按键被吞掉不传源程序；Esc 关闭。新增 `LowLevelKeyboardHook`（WH_KEYBOARD_LL，仅工具栏可见时激活），`PromptTemplate` 加 `Shortcut` 字段，编辑窗口加单字符输入框，默认 F/J/Z 由拼音首字母决定。机器侧验证：Debug 0 警告 + NativeAOT 0 警告，publish 26MB exe 产出，新版 PID 14024 已启动。**用户真机要测**：(1) 划词后按 F→翻译触发；(2) 按 J/Z 同理；(3) 吞键验证（记事本按 F 不应收到字符）；(4) 透传验证（按未绑定的 A 正常输入）；(5) Esc 关闭；(6) 隐藏后按 F 不触发（钩子已卸载）；(7) Settings→编辑翻译→改快捷键为 T→保存→按 T 触发、F 不触发。

### 用户真机待验证清单（R23 + R31，bash 无法触发）

1. **托盘"重启 BYH"** → 应在 1-3 秒内托盘图标重新出现（R31 修复验证）
2. **设置页添加启动项** → 图标自动加载、字段保存往返正确
3. **QuickTools 点启动项** → 真的启动对应软件/网页
4. **`{sel}` 参数**：选中文字 → Ctrl+Alt+Q → 点带 `{sel}` 的启动项 → 应把选中文字作为参数
5. **`{prompt:提示语}`**：点带 prompt token 的启动项 → 应弹 ParameterInputDialog → 输入后启动

### 用户真机待验证（仅一项，bash 无法触发）

1. 启动 BYH（新版 exe 已发布到 `artifacts/publish/win-x64-nativeuia/`，托盘菜单「重启 BYH」会切到新版本）。
2. 点托盘「重启 BYH」→ 应在 1-3 秒内看到托盘图标重新出现（旧进程退出、新进程起来）。
3. 翻译：选中文字 → 工具条 → 翻译 → 应走 DeepSeek (deepseek-v4-flash)（可在 `AppData\Local\BYH\logs\BYH.log` 看到 `Switched to provider 'deepseek'`）。
4. 画框 OCR：Ctrl+Alt+Q → 📐 画框识别文字 → 应仍走 SiliconFlow + Qwen3.5-4B（与翻译 Provider 切换互不影响）。

如果重启后托盘仍未出现，根因排查：(a) Mutex 没释放（看新进程是否进了 `if (!acquired) return 0`）；(b) `Process.Start` 失败被 catch 吞（看旧进程是否能正常退出）；(c) Mutex 跨线程释放抛 ApplicationException（已在 catch 内吞掉）。

### 下一步工作（按优先级）

1. **真机验证 R23 + R31**（见上节"用户真机待验证清单"）
2. **安装包 / 代码签名 / 开机启动**（v0.1 收尾）
3. **P1.7 DPI/多显示器定位**（单屏 mouse+16px）
4. **P1.8 应用语料库 95% 验收**
5. **R23 启动器增强（如果真机验证有反馈）**：图标缓存落盘（目前每次启动重提）、UWP/CLI 命令支持、多 prompt UX 优化
6. **R24 轨道 B② PaddleOCR-VL 本地 OCR**：0.9B SOTA 离线兜底，AOT 验证 + 模型分发
6. **R24 轨道 B③ WinRT OCR**：`Windows.Media.Ocr`，长期 backlog
7. **macOS 未开始**

### 可能的优化（非紧急）

1. **OCR prompt 优化**：当前默认 `"Free OCR."`（DeepSeek-OCR 遗留）。Qwen3.5-4B 是通用视觉模型，可能需要更明确指令。mimo-agent 之前实验推荐 `"Recognize text. No markdown. No formatting. Text only."`。可改 `VisionCaptureSettings.Default.OcrPrompt` 并测对比。
2. **UIA `GetTextsInRegion` 性能**：当前走祖先链找最小容器（`MaxAncestorDepthForRegionRoot=4`）。如果用户开启 UIA 路径后报慢，可降到 2-3 层。
3. **多 OCR 模型 fallback**：主模型失败/明显幻觉时自动换备用。需要新字段 + 逻辑。

### 关于 omp-worker / MIMO 的使用

通过 `omp-worker` 调用 Xiaomi MIMO，模型必须使用精确 selector `xiaomi-mimo/mimo-v2.5-pro`：
- ✅ 适合：构建/测试/静态扫描/读文件/1-3 个文件的机械迁移/独立复核。
- ❌ 不适合：架构决策、跨七窗的大批量修改、复杂 bug 修复。R26 的 420s 大任务超时并留下未闭合 XAML，说明必须拆小任务。
- ⚠️ wrapper 的 `--max-time` 只控制 Python 子进程；宿主 shell/exec 的外层 timeout 必须至少多留 10–30 秒，否则健康 worker 会被外层提前杀掉。2026-07-18 已通过 `skill-hub` 把此条补入 `omp-worker` 并校验三端链接。

主 Agent 做设计和核心代码，mimo-agent 做执行和验证，这样最省 token。参考 §3b 教训 6。
---

## 10. 需求管理说明
项目用 `handoff\` + `docs\Phase{1,3}-Tasks.md` + **`docs/architecture/`**（R19 新增）作 living requirements + 模块文档。路线图待办统一放 `handoff\BACKLOG-roadmap.md`。
