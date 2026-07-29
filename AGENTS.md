# AGENTS.md — BYH

> 给任何被启动来改 BYH 的 coding agent（ZCode / omp / Codex / Cursor / Aider 等）。
> 人看 [`CONTRIBUTING.md`](CONTRIBUTING.md)，机器先看这里。

## 这是什么

**BYH (By Your Hand)** — Windows 选词助手。后台常驻，全局快捷键直达屏幕选中或框选内容：OCR、翻译、剪贴板历史、启动器、提示词模板。NativeAOT 单文件 exe，所有配置和密钥落 `%LOCALAPPDATA%\BYH\`。

现役版本 / 能力快照 / 配置清单见 [`README.md`](README.md)；版本历史见 [`CHANGELOG.md`](CHANGELOG.md)。

## 怎么跑起来

```bash
dotnet build SelectionAssistant.slnx -c Release      # 0 警告（CI /warnaserror）
dotnet test SelectionAssistant.slnx                   # ~660 项
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
4. **P/Invoke 迁移进行中**（66/112 完成）。新增 P/Invoke 用 `[LibraryImport]` + 显式 `EntryPoint="...W"`。陷阱见 `docs/AUDIT-findings.md` M4：`StringMarshalling.Utf16` ≠ `CharSet.Unicode`、bool 参数要 `[MarshalAs(Bool)]`、out 句柄要显式 release。
5. **单实例 mutex**。`Global\BYH_ByYourHand_SingleInstance`。**替换 exe 前先 `taskkill /F /IM BYH.exe`**——运行中的进程锁文件，部署/发布前必做。
6. **品牌名约定**。`AssemblyName=BYH`（exe 名、进程名、mutex、配置目录、托盘 tooltip 统一），但 namespace 仍是 `SelectionAssistant.*`（技术标识符不改）。`avares://` URI 用 `avares://BYH/*`（App 项目）和 `avares://SelectionAssistant.UI/*`（UI 项目），两者并存。
7. **样式约定：InnerCard 即唯一框 + Fluent 资源键双层陷阱**。设置页所有输入控件（TextBox / ComboBox / NumericUpDown）都包在 `Border.InnerCard` 里，**InnerCard 是唯一可见的框**——控件自己的 Fluent 边框必须透明，否则会"套两层"（白瓷砖 + 金边，很丑）。具体：
   - **TextBox**：`IvoryJade.axaml` 里 `Style Selector="TextBox"` 默认 `Background=Transparent` + `BorderBrush=Transparent` + `BorderThickness=0`；focus 用极淡背景 tint（TextBox **没有** `BoxShadow` 属性，别试 inset ring）。不在 InnerCard 里的 TextBox（11 个对话框/搜索框）加 `Classes="Bordered"` 恢复传统边框。
   - **Fluent 模板内部资源键必须一并覆盖**：光改外层 `BorderBrush`/`Background` 属性**不够**——Fluent 控件模板内部用 `TextControlBorderBrush`/`ComboBoxBackground`/`ComboBoxDropDownBackground` 等资源键画自己的框。必须在 `Style Selector="控件"` 里用 `<Style.Resources>` 把这些键全部 shadow 掉（TextBox 完整 28 键、ComboBox ~50 键含下拉抽屉 chrome）。参照现有 `TextBox`/`ComboBox` 的 `Style.Resources` 块（IvoryJade.axaml，在 SpotlightSearch 样式之前）。
   - **下拉抽屉配色**：`ComboBoxDropDownBackground` = ivory cream（`ByhColorSurface`），`ComboBoxDropDownBorderBrush` = 香槟金 `#58E8C89A`；项目 hover 淡象牙、**selected 用金色 `ByhColorGold` (`#D5A86A`) + 白字**（不是橄榄绿）；chevron 橄榄 `ByhColorPrimary`。
   - **focus 残留框陷阱（自己挖的坑）**：选完下拉项后焦点回到 ComboBox，如果 `ComboBox:focus` 样式设了 `BorderBrush=ByhPrimaryBrush`，会画出 1px 橄榄绿残留框。`ComboBox:focus` 和 `NumericUpDown:focus` 的 BorderBrush **必须 Transparent**——任何 focus 指示都不该在 inline selector 上画第二层框。NumericUpDown 的 spinner 箭头用 scoped `NumericUpDown /template/ RepeatButton` 选择器（**不要**全局改 `RepeatButton*`，会污染 ScrollBar）。
   - **设置页结构**：每个 section 包一层 `Border Classes="DashboardPanel"`（奶白渐变 + 香槟金细边 + 暖调柔光，与 Dashboard 页同款），section 间靠 Spacing 间隔、**不用 hairline 分隔线**。子卡片用 `Classes="InnerCard"`。`EditFormBorder`/`PromptTemplatesCard` 这两个 x:Name 在 Border 上（code-behind 调 `BringIntoView()`，`Control` 基类方法，StackPanel→Border 类型变更安全）。

## 当前状态与下一步

**v0.1.0 已发布**（2026-07，git tag `v0.1.0`）。DEFER 队列（详见 CHANGELOG）：

- **M1/M2**：god-class 拆分（`ClipboardHistoryWindow.axaml.cs` ~2940 行 / `App.axaml.cs` ~2240 行）
- **L3**：`AutomationProperties.Name` 无障碍全层补齐（需屏幕阅读器验证）
- **M4**：剩余 46 处 `[DllImport]` → `[LibraryImport]`（高风险核心路径）
- **macOS**：已取消移植计划。BYH 是 Windows 专属工具，Mac 同类需求由独立项目专项开发。
