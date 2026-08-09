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
- **剪贴板 tag 徽章可点击过滤 + 搜索 tag 命中前置（REQ-043）。** 行内的粉色 EntryTag（如 `AWS`）和橄榄色 CustomTag（如 `#工作`）徽章现在可点击——点任意徽章即在搜索框上方出现一个过滤 chip（`🏷 <name> ✕` / `# <name> ✕`），列表立即只显示带该 tag 的条目；chip 单一互斥，点别的徽章替换，点 chip 或 ✕ 清除。搜索时凡是 token 命中 tag 字段的条目整组排在只命中正文的前，组内仍按时间倒序。新增 `ClipboardMatchScore` + `ClipboardMatchRanker`（Core 纯函数三键稳定排序），`ClipboardSearchIndex.ScoreMatch` 在原布尔 `IsMatch` 基础上附带 TagHit 信号（语义不变，14 例 parity 测试保护）。纯 UI/视图行为，App 层零改动，过滤 chip 会话级不持久化。
- **选词工具栏「朗读」快捷播报（TTS）。** 选词工具栏新增「朗读」按钮，默认 `S` 快捷键，把当前选区文本送 MiniMax T2A 合成语音并通过 Windows MCI 播放。语音按书写体系自动路由（CJK / 拉丁），缺 mmx key 时回退到全局密钥；同 R99 一样受 i18n + 焦点吞键哨位保护。
- **功耗监控（PowerMonitoring，默认关闭）。** BYH 周期性 HTTP 轮询用户配置的 Libre Hardware Monitor Web Server 拉取实时功率 / 温度，托盘 tooltip 显示 W / kWh，按梯形积分把瞬时功率累计成 Wh / kWh 落盘，温度超过阈值时 TTS 播报 mp3 警音（5°C 滞回防抖），每分钟写入 `power-history.jsonl`。纯 HTTP、无 LHM 客户端依赖、无提权，NativeAOT 安全。
- **手机卡片接入实时功耗数据 + 托盘两行 tooltip。** 设置中心右侧的「手机式状态卡」原先后 4 页是翻译/视觉/剪贴板/启动器的功能摘要，现在改为功耗实时数据页（CPU / GPU / System / Energy），与已上线的 PowerMonitoring 同源。底部 dock 4 个按钮换名但图标外观保持原样（五边形/树叶/信封/齿轮），Overview 第一页不动。字段映射直接取自 `PowerSnapshot` 的 dock 分组：CPU 页（封装功率/核心温度/总负载/最大核心负载/频率）、GPU 页（功耗/温度/负载/核心频率/显存频率）、System 页（12V/5V/3.3V/内存功耗与温度/CPU 与 GPU 风扇/电池功率与百分比/两块 SSD 温度）、Energy 页（仅瞬时合计功率）。传感器字段为 null（测不到）时整行隐藏，LHM 离线时 4 页统一显离线提示。同时把托盘 hover tooltip 从单行 W/kWh 改为两行——上排 CPU/GPU 功率 + SSD1 温度，下排 CPU/GPU 温度 + SSD2 温度（SSD 只有温度字段、无功率）。实现：`App.axaml.cs` 的 `PowerMonitorLoopAsync` 轮询回调在原 `UpdateTrayTooltip(snap)` 后增调 `_settingsWindow?.UpdatePhonePowerViews(snap)`；`SettingsWindow.axaml.cs` 新增公共方法按页/按行做 null-aware 显隐；i18n 三件套删 21 旧 phone key、新增 power key（最终 36/36/36 对齐）。

### 性能

- **剪贴板超长内容检索（REQ-030）。** 将搜索索引构建和查询移出 UI 线程，加入查询版本取消与按需正文匹配；27 万字符级条目不再随每次键入同步扫描全文。
- **剪贴板搜索二次提速（REQ-033）。** 普通历史取消固定 100ms 输入等待，超长正文预览改为常量级分配、展开正文按需生成，并消除打开窗口时的重复索引构建。搜索/默认列表分别只实例化首批 12/16 条，退格扩大结果集时复用现有控件，清空最后一个搜索字符不再同步重建约 60 个复杂条目。

### 兼容性

- **Warp 选区捕获（REQ-029）。** 对 `warp.exe` 增加专用 `Ctrl+Shift+C` 复制策略与 120ms 剪贴板稳定等待；Warp 的 GPU/WebView 剪贴板没有 Win32 owner 时，在明确的 Warp 策略中按序号变化 + 稳定文本受控接受，其他应用仍保留严格 owner 校验。新增 `--probe-process-policy <pid>` 诊断探针。

### 修复

- **大尺寸 Ocean Eyes 截图稳定性（REQ-028）。** 为 BGRA/DIB/PNG 路径增加尺寸上限和 checked 算术，修正 `SetClipboardData` 句柄所有权释放竞态，并加入 `tools/monitor-byh-crash.ps1` 监控探针。受控 2880×1620、3840×2160、4000×3000 和 6000×4000 场景均未复现崩溃；6000×4000 会安全返回诊断失败。
- **剪贴板 Delete 键删条目有时失效（REQ-044）。** 窗口打开焦点落在搜索框、方向键导航不移走焦点，导致焦点始终在 `TextBox` 内，裸 `Delete` 被 `TextBox` 当作「向前删一个字符」吃掉并标记 Handled，事件不冒泡到窗口级 `OnWindowKeyDown`——只有搜索框为空 / 光标在末尾时 Delete 才生效，这就是「有时」失效的来源。与此前 R99「加 tag 偶尔失效」是同一焦点吞键根因。复用 R99 的 Tunnel 修法：在 `SearchInput` 上以 `InputElement.KeyDownEvent` + `RoutingStrategies.Tunnel` + `handledEventsToo: true` 订阅，在 `TextBox` 冒泡处理之前先拿到键，只拦裸 `Delete`（`KeyModifiers.None`），调共享的 `HandleDeleteKey`。修饰键组合（Ctrl/Ctrl+Shift + Delete）仍归文本编辑；Backspace 故意不拦，永远是文本编辑键。
- **选词工具栏打字/编辑时自动隐藏。** 此前选中文字弹出工具栏后，若用户直接打字、按 Backspace/Delete 删除、或按方向键移动光标，工具栏会一直浮在原处挡住输入。现在工具栏可见时，按下任意「非动作」的字符键 / 编辑键 / 导航键（且无 Ctrl/Alt/Shift/Win 修饰键按下）会立即隐藏工具栏，同时按键照常生效给源应用。动作键（翻译/总结/复制等）、修饰键组合（Ctrl+C 等）、Esc、Ocean Eyes 模式行为不变。
- **Ocean Eyes 注释框 Enter 保存偶发失败（REQ-040）。** 注释对话框按 Enter 偶发「已保存但未落盘」——快速连按的 Enter 会触发两次保存路径，且对同一注释的两次写入按毫秒时间戳争抢。加固：先做幂等 gate（同 Id + 同 Content 的编辑直接去重），再以毫秒时间戳做稳定幂等键，强制单次写盘成功。
- **Speak 设置选项卡崩溃（REQ-041）。** 设置 → Speak 打开后偶发白屏，根因在 `ShowSettingsPage` 路由表缺少 `Tts` title 的分支，路由落入默认 catch → 触发对未初始化 view-model 的 null 解引用。补齐 Tts 分支，与 Vision / Translation 对齐。
- **Speak 密钥状态显示与实际不符（REQ-042）。** 之前 Speak 设置只显示「已配 / 未配」二值化状态，实际来源可能不同（`ByhSecret` BYH 私钥库 / `MmxConfig` MiniMax 配置文件）。改为 `TtsCredentialSource` 枚举（ByhSecret / MmxConfig / None），UI 同步显示来源，避免用户误以为未配。
- **剪贴板右下角快捷键提示跑位。** 剪贴板历史弹窗底部的快捷键提示行（↑↓ 选择 / 双击粘贴 / 右键菜单 / Esc 关闭）位置错乱，直接移除该 `NormalFooter`；同区的长按批量操作栏（`MultiSelectToolbar`）保留。进/出多选模式时切换该提示显隐的两行引用一并删除，4 个随之成为孤儿的 i18n key（`Clip_FooterSelect/Paste/Menu/Close`）从三件套同步移除（32/32/32）。

### 验证

- 2026-08-01 主线合并后的全量测试：**736/736 通过**（Core 581、Windows Integration 105、Providers 50）；Release build 0 warning / 0 error；REQ-029 NativeAOT QA 发布成功。Warp 真机日志在 20:34–21:08 多次确认 `source=SimulatedCopyCtrlShiftC`、`ownerless=True`，成功捕获 5–1636 字符。
- 2026-08-02 将当前 `main` 重新 NativeAOT 发布并同步到 `artifacts/publish/win-x64-nativeuia`；正式实例从该路径重启，Warp 进程策略探针确认 `CtrlShiftCOnly`、稳定等待 120ms，用户随后确认正式版本真机测试正常。
- 2026-08-02 合并 REQ-030 / REQ-031 后全量测试：**752/752 通过**（Core 597、Windows Integration 105、Providers 50）；Release build 0 warning / 0 error；NativeAOT 正式产物 SHA-256 `3787A04A0C91FE8A02FE335C6071DE545DCA6000678E38CEDED2A9F6DD30CE17`。
- 2026-08-02 REQ-033 支线验收：**754/754 通过**（Core 599、Windows Integration 105、Providers 50）；Release build 0 warning / 0 error；NativeAOT 发布成功，用户确认剪贴板连续检索及清空查询恢复流畅。
- 2026-08-09 TTS / PowerMonitoring 合并后全量测试：**954/954 通过**（Core 784、Windows Integration 119、Providers 51）；Release build 0 warning / 0 error；NativeAOT 发布成功（29.3 MB，0 IL 警告）。新增 TTS 朗读功能、功耗监控功能、Ocean Eyes Enter 保存修复、Speak 选项卡崩溃修复与密钥状态修复。
- 2026-08-09 手机卡片功耗数据 + 托盘两行 tooltip 合并后：i18n 三向 36/36/36 对齐，Release build 0 warning / 0 error，954 项测试全绿（i18n parity 守卫未动），NativeAOT 发布 0 IL 警告。已部署 `artifacts/publish/win-x64-nativeuia/BYH.exe`。

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
