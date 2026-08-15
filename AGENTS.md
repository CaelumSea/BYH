# AGENTS.md — BYH

> 给任何被启动来改 BYH 的 coding agent（ZCode / omp / Codex / Cursor / Aider 等）。
> 人看 [`CONTRIBUTING.md`](CONTRIBUTING.md)，机器先看这里。

## 这是什么

**BYH (By Your Hand)** — Windows 选词助手。后台常驻，全局快捷键直达屏幕选中或框选内容：OCR、翻译、剪贴板历史、启动器、提示词模板。NativeAOT 单文件 exe，所有配置和密钥落 `%LOCALAPPDATA%\BYH\`。

现役版本 / 能力快照 / 配置清单见 [`README.md`](README.md)；版本历史见 [`CHANGELOG.md`](CHANGELOG.md)。

## 怎么跑起来

```bash
dotnet build SelectionAssistant.slnx -c Release      # 0 警告（CI /warnaserror）
dotnet test SelectionAssistant.slnx                   # 当前 956 项
dotnet publish src/SelectionAssistant.App/SelectionAssistant.App.csproj -c Release -r win-x64
```

发布产物同步到 `artifacts/publish/win-x64-nativeuia/BYH.exe`（仓库约定基线）。需 .NET 10 SDK + VS Build Tools 2022 C++ 工作负载（NativeAOT）。

## 技术栈与目录

.NET 10 + Avalonia 12.1 + Win32 P/Invoke。`PublishAot=true` + `TrimMode=full`。

`src/`：`App`（组合根）/ `Core`（领域、i18n）/ `Infrastructure`（配置、日志）/ `Platform.Abstractions` / `Platform.Windows`（Win32）/ `Providers`（OpenAI 兼容）/ `UI`（Avalonia、Ivory Jade 主题）。
`tests/`：`Core.Tests` / `Providers.Tests` / `Windows.IntegrationTests`。
`docs/architecture/`：模块级架构文档（改某模块前先看对应的）。`docs/BACKLOG-roadmap.md`：R1–R54 路线图待办（含 120 批次演进历史）。`docs/AUDIT-findings.md`：P0–P3 审查清单与修复进度。

## 不看到就会犯错的铁律

1. **i18n 三向同步**。`src/SelectionAssistant.Core/I18n/Strings.cs`（属性）+ `Strings_en.cs` + `Strings_zh_CN.cs` 三处 key 必须 1:1。`StringsTests` 三个不变量测试是守卫——typo / 漏 entry / en-zh 不一致 = CI 红。AXAML 用 `{x:Static i18n:Strings.X}`，code-behind 用 `Strings.X`。`x:Static` 是编译期解析，trim-safe。
2. **无反射**。NativeAOT + `TrimMode=full`。JSON 全部手写 `Utf8JsonReader/Writer`，禁用 `System.Text.Json` 反射模式、`Activator.CreateInstance`、运行时 `ResourceInclude`。
3. **密钥走 DPAPI**。`secret://provider/{id}` URI + `ISecretStore`（`%LOCALAPPDATA%\BYH\secrets\{sha256}.bin`，CurrentUser scope）。**永不**进 JSON、永不进日志（`RedactedLogger` 脱敏 `api_key=`/`bearer`）。CLI 写入：`BYH.exe --set-secret secret://provider/{id} <value>`。
4. **P/Invoke 迁移进行中**（69/122 完成，全 src）。新增 P/Invoke 用 `[LibraryImport]` + 显式 `EntryPoint="...W"`。陷阱见 `docs/AUDIT-findings.md` M4：`StringMarshalling.Utf16` ≠ `CharSet.Unicode`、bool 参数要 `[MarshalAs(Bool)]`、out 句柄要显式 release。例外：App/UI 项目 csproj 未开 unsafe（SYSLIB1062 要求），这两处的纯 blittable 签名保持 `[DllImport]`。
5. **单实例 mutex**。`Global\BYH_ByYourHand_SingleInstance`。**替换 exe 前先停止运行中的 BYH.exe**——运行中的进程锁文件，部署/发布前必做。
6. **品牌名约定**。`AssemblyName=BYH`（exe 名、进程名、mutex、配置目录、托盘 tooltip 统一），但 namespace 仍是 `SelectionAssistant.*`（技术标识符不改）。`avares://` URI 用 `avares://BYH/*`（App 项目）和 `avares://SelectionAssistant.UI/*`（UI 项目），两者并存。
7. **样式约定：InnerCard 即唯一框 + Fluent 资源键双层陷阱**。设置页所有输入控件（TextBox / ComboBox / NumericUpDown）都包在 `Border.InnerCard` 里，**InnerCard 是唯一可见的框**——控件自己的 Fluent 边框必须透明，否则会"套两层"（白瓷砖 + 金边，很丑）。具体：
   - **TextBox**：`IvoryJade.axaml` 里 `Style Selector="TextBox"` 默认 `Background=Transparent` + `BorderBrush=Transparent` + `BorderThickness=0`；focus 用极淡背景 tint（TextBox **没有** `BoxShadow` 属性，别试 inset ring）。不在 InnerCard 里的 TextBox（11 个对话框/搜索框）加 `Classes="Bordered"` 恢复传统边框。
   - **Fluent 模板内部资源键必须一并覆盖**：光改外层 `BorderBrush`/`Background` 属性**不够**——Fluent 控件模板内部用 `TextControlBorderBrush`/`ComboBoxBackground`/`ComboBoxDropDownBackground` 等资源键画自己的框。必须在 `Style Selector="控件"` 里用 `<Style.Resources>` 把这些键全部 shadow 掉（TextBox 完整 28 键、ComboBox ~50 键含下拉抽屉 chrome）。参照现有 `TextBox`/`ComboBox` 的 `Style.Resources` 块（IvoryJade.axaml，在 SpotlightSearch 样式之前）。
   - **下拉抽屉配色**：`ComboBoxDropDownBackground` = ivory cream（`ByhColorSurface`），`ComboBoxDropDownBorderBrush` = 香槟金 `#58E8C89A`；项目 hover 淡象牙、**selected 用金色 `ByhColorGold` (`#D5A86A`) + 白字**（不是橄榄绿）；chevron 橄榄 `ByhColorPrimary`。
   - **focus 残留框陷阱（自己挖的坑）**：选完下拉项后焦点回到 ComboBox，如果 `ComboBox:focus` 样式设了 `BorderBrush=ByhPrimaryBrush`，会画出 1px 橄榄绿残留框。`ComboBox:focus` 和 `NumericUpDown:focus` 的 BorderBrush **必须 Transparent**——任何 focus 指示都不该在 inline selector 上画第二层框。NumericUpDown 的 spinner 箭头用 scoped `NumericUpDown /template/ RepeatButton` 选择器（**不要**全局改 `RepeatButton*`，会污染 ScrollBar）。
   - **设置页结构**：每个 section 包一层 `Border Classes="DashboardPanel"`（奶白渐变 + 香槟金细边 + 暖调柔光，与 Dashboard 页同款），section 间靠 Spacing 间隔、**不用 hairline 分隔线**。子卡片用 `Classes="InnerCard"`。`EditFormBorder`/`PromptTemplatesCard` 这两个 x:Name 在 Border 上（code-behind 调 `BringIntoView()`，`Control` 基类方法，StackPanel→Border 类型变更安全）。

8. **Ownerless clipboard 仅限显式策略（Warp / Zed）。** Warp（`Ctrl+Shift+C`）和 Zed（GPUI 渲染面）都可能写入无 Win32 owner HWND 的剪贴板，`ClipboardCaptureInvocation.AllowOwnerlessResult` 只由两种条件开启：Warp 的 `CtrlShiftCOnly` 模式，或显式 `AllowOwnerlessClipboardResult` 策略开关（当前仅 `zed` 规则）。仍必须满足目标窗口、剪贴板序号变化、稳定文本和读取期间序号未变化；其他应用不能走 ownerless 路径。

## 当前状态与下一步

**v0.1.0 已发布**（2026-07，git tag `v0.1.0`）。本地 `main` 截至 2026-08-09 已完成 REQ-028–REQ-044，新增 TTS「朗读」与 PowerMonitoring「功耗监控」两大功能：大尺寸截图防崩溃、Warp 选区捕获兼容、开机自启、剪贴板超长检索 / 长按多选 / tag 过滤 / Delete 键修复、Custom Provider 草稿、Ocean Eyes Enter 保存、Speak 设置选项卡与密钥状态、工具栏说话与系统功耗轮询。PowerMonitoring 上线后，设置中心手机式状态卡的后 4 页（CPU/GPU/System/Energy）改为实时功率与温度数据（同源、null 字段整行隐藏），托盘 hover tooltip 改为两行（上=功率、下=温度），Overview 第一页不动。该批验证基线 **953/953**（Core 783 / Providers 51 / Windows 119）。

**2026-08-10 剪贴板交互改进**：托盘点击恢复最小化/置顶已打开的设置窗口（`ShowAndActivate` 闪烁 Topmost）；合并「翻译」+「视觉」导航为单一「模型」页（Provider 增删改 + 视觉/OCR 子卡片，OCR 控件 x:Name 不变、数据模型零改动）；剪贴板行新增三个快捷键 —— `Ctrl+T` 添加标签、`Ctrl+M` 移动到（分类+自定义标签扁平列表）、`Ctrl+R` 移除标签，并修复 `ApplyFilterResults` 在 tag/group/pin/favorite 等非搜索刷新时把选中条目重置回顶部的问题（改按 Id 找回保持选中，真搜索仍跳到首个匹配）。剪贴板行右键菜单的 Move to / Remove tag 构建逻辑提取为 `PopulateMoveToItems` / `PopulateRemoveTagItems`（叶子构建）+ `BuildMoveToMenu` / `BuildRemoveTagMenu`（带 header 包装）+ `FindRowContainer`（可视树查找，键盘路径无 PointerPressed source），右键菜单行为不变。

**2026-08-15 修复与性能**：**Zed（GPUI）划词捕获**——Zed 发布复制不带 owner HWND（7 个剪贴板事务），默认 owner 校验在读文本前拒绝；新增 opt-in `AllowOwnerlessClipboardResult` 策略开关 + `zed` 规则（`HistorySuppressionCount=8`，复制键链保持默认），真机已验证划词弹工具栏。**剪贴板图片行修复**——图片条目 tag/徽章不再显示两排重复（通用 meta 行按 `!IsImage` 门控）。**功耗「启动 LHM」按钮**——功耗卡片新增按钮 + `LhmExePath` 路径字段，LHM 离线时复用 LauncherRunner 的 runas→UAC 回退按管理员拉起，仅在真正可达时置灰。**空闲工作集裁剪**——3 分钟 DispatcherTimer，仅在回托盘（无弹窗可见）时调 psapi `EmptyWorkingSet`，纯提示性。最新验证基线 **956/956**（Core 783 / Providers 51 / Windows 122）。

**下一项产品需求**：`REQ-036` “统一多模态模型与直接觉动作”，当前仅已派发、**尚未实现**。从 `TASK-053` 开始：先建立能力感知的多模态请求层，再接 Ocean Eyes 截图直译，最后对比直达与两段式链路。普通划词仍走纯文本，纯 OCR 与两段式回退必须保留。详见 [`docs/architecture/10-multimodal-actions.md`](docs/architecture/10-multimodal-actions.md)。`REQ-027` 的主题切换三个任务（TASK-030–032）仍在待办，不要误标已完成。

DEFER 队列（详见 CHANGELOG）：

- **M1/M2**：god-class 拆分（`ClipboardHistoryWindow.axaml.cs` ~4000 行 / `App.axaml.cs` ~2770 行）
- **L3**：`AutomationProperties.Name` 无障碍全层补齐（需屏幕阅读器验证）
- **M4**：剩余 53 处 `[DllImport]` → `[LibraryImport]`（高风险核心路径；App/UI 因 unsafe 未开保持 DllImport）
- **macOS**：已取消移植计划。BYH 是 Windows 专属工具，Mac 同类需求由独立项目专项开发。
