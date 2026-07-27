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

## 当前状态与下一步

**v0.1.0 已发布**（2026-07，git tag `v0.1.0`）。DEFER 队列（详见 CHANGELOG）：

- **M1/M2**：god-class 拆分（`ClipboardHistoryWindow.axaml.cs` ~2940 行 / `App.axaml.cs` ~2240 行）
- **L3**：`AutomationProperties.Name` 无障碍全层补齐（需屏幕阅读器验证）
- **M4**：剩余 46 处 `[DllImport]` → `[LibraryImport]`（高风险核心路径）
- **macOS**：v0.1.0 之后 roadmap（现役仅 Windows）
