# Changelog

本文件记录 BYH（By Your Hand）的版本演进。格式参考 [Keep a Changelog](https://keepachangelog.com/)，版本号遵循 [Semantic Versioning](https://semver.org/)。

每个版本的完整开发细节见 `docs/BACKLOG-roadmap.md` 对应批次段落；审查与修复进度见 `docs/AUDIT-findings.md`。

---

## [Unreleased]

### 产品方向

- **macOS 移植已取消。** 经 Mac 端实际调研，BYH 的核心能力（全局快捷键、UIA 文本捕获、低级鼠标/键盘 hook、DPAPI 密钥加密）深度依赖 Win32，跨平台移植成本远超收益。BYH 定位为 **Windows 专属**工具；Mac 上的同类需求由独立项目按 Mac 原生范式专项开发，不再共用代码库。`Platform.Abstractions` 抽象层作为 Windows 内部的解耦设计保留，但不再以「Mac 移植」为目标。

### 新增

- **开机自启选项（设置 → 通用）。** 新增「开机自启」开关：开启后写入 `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`（用户级注册表，无需管理员），登录 Windows 时自动拉起 BYH。注册表为真相源——若用户在任务管理器 / Windows 设置里手动禁用，启动时以注册表为准回写 `startup-options.json`，开关显示与实际一致。组策略或杀毒软件拦截写入时优雅降级，提示「启用失败」而非崩溃。新增 `IAutoStartManager` 平台抽象 + `WindowsRunAutoStartManager` 实现，沿用了 Ocean Eyes / 剪贴板历史的设置流水线（record → store → App 接线 → 设置卡片 + i18n）。
- **剪贴板长按多选（REQ-031）。** 长按条目进入批量编辑状态，支持逐项选择、选择全部、取消全选和批量删除；普通单击、双击与右键菜单保持原行为。

### 性能

- **剪贴板超长内容检索（REQ-030）。** 将搜索索引构建和查询移出 UI 线程，加入查询版本取消与按需正文匹配；27 万字符级条目不再随每次键入同步扫描全文。

### 兼容性

- **Warp 选区捕获（REQ-029）。** 对 `warp.exe` 增加专用 `Ctrl+Shift+C` 复制策略与 120ms 剪贴板稳定等待；Warp 的 GPU/WebView 剪贴板没有 Win32 owner 时，在明确的 Warp 策略中按序号变化 + 稳定文本受控接受，其他应用仍保留严格 owner 校验。新增 `--probe-process-policy <pid>` 诊断探针。

### 修复

- **大尺寸 Ocean Eyes 截图稳定性（REQ-028）。** 为 BGRA/DIB/PNG 路径增加尺寸上限和 checked 算术，修正 `SetClipboardData` 句柄所有权释放竞态，并加入 `tools/monitor-byh-crash.ps1` 监控探针。受控 2880×1620、3840×2160、4000×3000 和 6000×4000 场景均未复现崩溃；6000×4000 会安全返回诊断失败。
- **选词工具栏打字/编辑时自动隐藏。** 此前选中文字弹出工具栏后，若用户直接打字、按 Backspace/Delete 删除、或按方向键移动光标，工具栏会一直浮在原处挡住输入。现在工具栏可见时，按下任意「非动作」的字符键 / 编辑键 / 导航键（且无 Ctrl/Alt/Shift/Win 修饰键按下）会立即隐藏工具栏，同时按键照常生效给源应用。动作键（翻译/总结/复制等）、修饰键组合（Ctrl+C 等）、Esc、Ocean Eyes 模式行为不变。

### 验证

- 2026-08-01 主线合并后的全量测试：**736/736 通过**（Core 581、Windows Integration 105、Providers 50）；Release build 0 warning / 0 error；REQ-029 NativeAOT QA 发布成功。Warp 真机日志在 20:34–21:08 多次确认 `source=SimulatedCopyCtrlShiftC`、`ownerless=True`，成功捕获 5–1636 字符。
- 2026-08-02 将当前 `main` 重新 NativeAOT 发布并同步到 `artifacts/publish/win-x64-nativeuia`；正式实例从该路径重启，Warp 进程策略探针确认 `CtrlShiftCOnly`、稳定等待 120ms，用户随后确认正式版本真机测试正常。
- 2026-08-02 合并 REQ-030 / REQ-031 后全量测试：**752/752 通过**（Core 597、Windows Integration 105、Providers 50）；Release build 0 warning / 0 error；NativeAOT 正式产物 SHA-256 `3787A04A0C91FE8A02FE335C6071DE545DCA6000678E38CEDED2A9F6DD30CE17`。

---

## [0.1.0] — 2026-07-27

首个标记版本（"事实上的 v0.1"）。在此之前的演进全部汇总在 `docs/BACKLOG-roadmap.md` 的 120 个批次里；本条目只描述 v0.1.0 标记时刻的能力快照与关键质量门槛。

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
