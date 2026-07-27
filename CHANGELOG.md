# Changelog

本文件记录 BYH（By Your Hand）的版本演进。格式参考 [Keep a Changelog](https://keepachangelog.com/)，版本号遵循 [Semantic Versioning](https://semver.org/)。

每个版本的完整开发细节见 `docs/handoff/BACKLOG-roadmap.md` 对应批次段落；审查与修复进度见 `docs/handoff/AUDIT-findings.md`。

---

## [0.1.0] — 2026-07-27

首个标记版本（"事实上的 v0.1"）。在此之前的演进全部汇总在 `docs/handoff/BACKLOG-roadmap.md` 的 120 个批次里；本条目只描述 v0.1.0 标记时刻的能力快照与关键质量门槛。

### 能力快照

- **Ocean Eyes**（区域截图 OCR）：`Ctrl+Alt+Q`，拖框选 → OCR → 标注 / 翻译 / 识别 / 贴图，UIA 辅助预填，截图自动落盘。
- **Spotlight 启动器**：`Ctrl+Alt+Space`，全局快速搜索 + 启动配置的应用/网页/命令。支持 `.lnk` 解析与 UAC 提权 fallback（`ShellExecuteEx`）。
- **剪贴板历史**：`Ctrl+Alt+V`，文本 + 图片、自动分类、手动 `GroupOverride`（联动 Sensitive DPAPI 加密）、置顶、标签、自定义 tab、全文搜索、按月归档（`clipboard-archive/YYYY-MM.json`）、schema v5。
- **工具栏**：选词后浮出，按提示词模板（翻译 / 总结 / 解释 / 自定义）调用 provider，CJK 路由检测扩展至假名 / 谚文。
- **主题**：内置 Ivory Jade 主题系统（REQ-027），CSS 变量驱动，可恢复。
- **i18n**：中英双语全覆盖，代码内手写字典（NativeAOT 安全），`StringsTests` 三向同步守卫。
- **托盘**：设置 / 配置目录 / 截图画廊 / 重启 / 退出。

### 质量门槛（v0.1.0 验证状态）

- `dotnet build` 0 警告 0 错误
- `dotnet test` **661/661** 通过（Core 532 + Providers 35 + Windows Integration 94）
- NativeAOT publish **0 trim/AOT 警告**
- 单文件 exe **28,589,568 字节**（~27.2 MB）
- 全代码库审查（P0/P1/P2/P3 共 33 条）已修 19 条，SKIP 5 条，DEFER 9 条到后续版本

### 关键架构决策（已落地）

- **NativeAOT + TrimMode=full**：所有 JSON 序列化手写 `Utf8JsonReader/Writer`，零反射；`RegexOptions.Compiled` 全部改 `[GeneratedRegex]`。
- **配置全落 `%LOCALAPPDATA%\BYH\`**：12 个 JSON store，原子替换写入（`File.Move(overwrite:true)`），4 个 store 补齐 `Validate()`。
- **Provider 安全**：API key 走 `secret://provider/{Id}` + `ISecretStore` + DPAPI，从不出现在 URL / 日志 / 异常。
- **日志滚动**：`RedactedLogger` 按 1MB 滚动保留 5 份，API key / bearer token 脱敏，启动时归档超阈值老文件。
- **单实例**：mutex `Global\BYH_ByYourHand_SingleInstance`。
- **P/Invoke 现代化（部分）**：66/112 处 `[DllImport]` → `[LibraryImport]`（Platform.Windows 项目），剩余 46 处（hook/launcher/icon 核心高风险）留作机会性迁移。

### DEFER 到后续版本的项

- **M1/M2** god-class 拆分（`ClipboardHistoryWindow.axaml.cs` ~2900 行 / `App.axaml.cs` ~2095 行）
- **L3** 无障碍 `AutomationProperties.Name`（13 个 AXAML 0 标注，需屏幕阅读器验证）
- **L8** `IManagedWindow` 公共接口（与 M1/M2 耦合）
- **M4** 剩余 46 处 LibraryImport 迁移
- 开机自启、LICENSE 文本、安装包
