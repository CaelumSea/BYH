# BYH — By Your Hand

> Context-aware selection assistant. 选词即用，不离开当前上下文。
>
> Status: **v0.1.0 + post-release updates (2026-08-02)** · NativeAOT single-binary · Windows 10+ · .NET 10 · [MIT License](#license)

BYH 在后台常驻，通过全局快捷键直达屏幕上当前选中或框选的内容——**一个工具搞定截图 OCR、即时翻译、剪贴板历史、启动器、提示词模板**，全程不离开当前窗口。

![BYH Ivory Jade 设置中心 Dashboard](docs/screenshots/byh-settings-dashboard.png)

<p align="center"><sub>Ivory Jade 设置中心 · Dashboard、模块频率、模型路由与手机式状态卡（当前 main 实机截图）</sub></p>

**隐私优先**：所有配置和用户数据只落在本地系统配置目录（`%LOCALAPPDATA%\BYH\`），API 密钥走系统级加密（DPAPI），除你主动调用 LLM provider 外不上传任何第三方服务。

> **平台定位**：BYH 是 **Windows 专属**工具——核心能力（全局快捷键、UIA 文本捕获、低级 hook、DPAPI）深度依赖 Win32。`Platform.Abstractions` 作为 Windows 内部的解耦设计保留，Core/Providers 保持平台无关（由 macOS CI 守护）。

## 最近更新 · 2026-08-02

- **大区域截图稳定性**：补齐 BGRA / DIB / PNG 尺寸上限与 checked 算术，异常尺寸安全失败，不再带崩后台进程。
- **Warp 选区兼容**：为 `warp.exe` 增加受限的 `Ctrl+Shift+C` 捕获策略；仅在剪贴板序号变化、文本稳定且命中明确进程策略时接受 ownerless 结果。
- **超长剪贴板搜索**：索引构建和查询移出 UI 线程；27 万字符级条目也不会在每次键入时同步扫描全文。
- **长按多选**：长按剪贴板条目进入批量编辑，可选择全部、取消全选并批量删除；普通单击和右键菜单保持原行为。

---

## 五大核心能力

| 能力 | 触发 | 做什么 |
|---|---|---|
| 📸 **Ocean Eyes** 区域截图 OCR | `Ctrl+Alt+Q` | 拖框选屏幕区域 → 截图 → OCR 文字识别。截图自动保存到配置目录，可贴图钉在桌面、UIA 辅助自动框选元素、识别结果可翻译/编辑/复制。 |
| 🚀 **Spotlight 启动器** | `Ctrl+Alt+Space` | 全局快速搜索面板，一键启动配置的应用 / 网页 / 命令。支持自定义图标、URL、启动参数。 |
| 📋 **剪贴板历史** | `Ctrl+Alt+V` | 本地剪贴板管理弹窗：文本 + 图片、自动分类、手动归类、置顶、标签、自定义分组 tab、后台全文索引、长按多选与批量删除、按月归档。敏感条目（密码类）DPAPI 加密掩码，不会明文展示。 |
| 🔤 **选词工具栏** | 选中文字自动浮出 | 在任意应用选中文字即弹出工具栏，一键执行翻译 / 总结 / 解释 / 自定义提示词。结果窗口支持多模型同时对比、自定义动作、双语标题。 |
| 🎛️ **托盘与设置** | 托盘右键 | 统一设置中心（Dashboard / 通用 / 翻译 / 动作 / 视觉 / 启动器 / 剪贴板七页），所有改动即时保存。托盘菜单直达设置、配置目录、截图画廊、重启、退出。 |

**终端兼容性**：Warp（`warp.exe`）使用专用的 `Ctrl+Shift+C` 选区复制策略；其 GPU/WebView 剪贴板可能没有可查询的 Win32 owner，BYH 仅在该明确进程策略下按剪贴板序号变化和稳定文本读取，其他应用仍使用严格 owner 校验。

> 三组主快捷键均可在 **设置 → 各模块页** 自定义（支持 Ctrl/Alt/Shift/Win 修饰键 + A–Z / 0–9 / F1–F12 / Space）。修改后保存即时生效。

---

## 安装与首次启动

1. 把发布包解压到任意目录（例如 `C:\Tools\BYH\`）。
2. 双击 `BYH.exe`。首次启动会在 `%LOCALAPPDATA%\BYH\` 建好配置目录与子目录。
3. 托盘出现 BYH 图标 = 运行中。所有快捷键即刻可用，但 **OCR / 翻译需要先在设置里配 provider**（见下）。

**单实例**：进程级 mutex `Global\BYH_ByYourHand_SingleInstance` 保证同一时间只有一个 BYH 在跑。想替换 exe 时必须先关掉运行的实例（托盘 → 退出，或任务管理器结束 `BYH.exe`）。

**开机自启**：在 **设置 → General → Launch at startup** 开启。BYH 写入当前用户 `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`，无需管理员权限；注册表是运行时真相源，写入失败会在设置页提示而不会崩溃。

---

## 配置 LLM provider（翻译 / OCR / 提示词必需）

打开 **设置 → Translation**（翻译服务）：

- **Name**：任意标识，如 `siliconflow`
- **Base URL**：OpenAI 兼容端点，如 `https://api.siliconflow.cn/v1`
- **API Key**：你的密钥。保存时用 DPAPI 加密落盘到 `%LOCALAPPDATA%\BYH\secrets\`，从不出现在日志或 URL 里
- **Model**：默认模型 id，如 `gpt-4o-mini`（翻译）或 OCR 多模态模型如 `nex-agi/Nex-N2-Pro`

OCR（Ocean Eyes）用哪个 provider/model 在 **设置 → Vision** 单独配（可和翻译用不同的）。

---

## 配置文件清单

全部位于 `%LOCALAPPDATA%\BYH\`：

| 文件 | 作用 |
|---|---|
| `ui-language.json` | UI 语言（`en` / `zh-CN`）。缺失则按系统语言自动检测。切换需重启。 |
| `profile.json` | 显示名（用于问候语）。 |
| `startup-options.json` | 开机自启开关的本地镜像；启动时与当前用户 Run 注册表项同步。 |
| `providers.json` | 翻译/LLM provider 列表。 |
| `vision.json` | OCR provider / model / prompt 配置。 |
| `prompt-templates.json` | 工具栏提示词模板（翻译/总结/解释/自定义）。缺失用内置默认。 |
| `ocean-eyes.json` | Ocean Eyes 触发快捷键 + 鼠标和弦开关。 |
| `ocean-eyes-capture.json` | 截图保存路径 / 自动保存 / 写入剪贴板 / UIA 辅助开关。 |
| `spotlight-trigger.json` | 启动器面板快捷键 + 窗口尺寸。 |
| `clipboard-history-trigger.json` | 剪贴板历史弹窗快捷键。 |
| `clipboard-history-settings.json` | 剪贴板功能开关 / 自动粘贴 / 最大条数 / 排除应用 / 敏感掩码。 |
| `clipboard-history.json` | 剪贴板条目（文本）。schema v5。 |
| `clipboard-history-tags.json` | 标签 + 条目→标签分配。 |
| `clipboard-history-icons.json` | 用户导入的图标库（SVG path data）。 |
| `launcher-entries.json` | 启动器条目（用户添加的 app/URL）。 |
| `toolbar-shortcuts.json` | 工具栏内置快捷键（默认 R/C/V）。 |
| `capture-policies.json` | 进程级选词捕获策略；内置 Warp 规则使用 `Ctrl+Shift+C`，其他终端保持 `Ctrl+Insert`。 |
| `clipboard-images/` | 图片剪贴板条目的 PNG。 |
| `clipboard-archive/` | 按月分片（`YYYY-MM.json`）的剪贴板归档。 |
| `launcher-icons/` | 启动器图标缓存。 |
| `secrets/` | DPAPI 加密的 provider 密钥。 |
| `logs/BYH.log` | 运行日志（含 API key 脱敏），按 1MB 滚动保留 5 份。 |

> 配置 JSON 全部手写 reader/writer（NativeAOT 安全，无反射）。删除某个文件即恢复默认。

---

## 设置页结构（左侧导航）

```
Dashboard     ← 模块/事件/模型路由总览（本地脱敏日志驱动）
General       ← 语言、显示名（问候语）
Translation   ← provider 管理 + 测试连通
Actions       ← 工具栏提示词模板
Vision        ← OCR provider / model / UIA 辅助
Launcher      ← 启动器条目
Clipboard     ← 剪贴板历史所有开关与上限
```

设置改动即时保存到对应 JSON；语言切换需重启生效。

---

## 构建

需要 **.NET 10 SDK**。BYH 是 Windows 专属，完整构建在 Windows 上进行；Core/Providers 是平台无关代码，CI 在 macOS 上验证它们不引入 Win32 依赖。

```bash
# 编译检查（Windows）
dotnet build SelectionAssistant.slnx -c Release

# 测试（当前 752 项，含 i18n 三向同步守卫；Windows.IntegrationTests 仅 Windows 可跑）
dotnet test

# NativeAOT 单文件发布（Windows，生成 BYH.exe，约 28MB）
dotnet publish src/SelectionAssistant.App/SelectionAssistant.App.csproj \
  -c Release -r win-x64
```

**产物不进 git**——编译产物在 `bin/.../publish/BYH.exe`，直接运行即可。对外分发走 [GitHub Releases](https://github.com/CaelumSea/BYH/releases)，用户从 Releases 页下载现成 exe，不必自己编译。

---

## i18n（国际化）

代码内手写字典（NativeAOT / trim-safe，非 resx）：

- `src/SelectionAssistant.Core/I18n/Strings.cs` — 属性入口（`Strings.X`）
- `src/SelectionAssistant.Core/I18n/Strings_en.cs` — 英文值
- `src/SelectionAssistant.Core/I18n/Strings_zh_CN.cs` — 中文值

三处 key 必须 1:1 对齐，由 `StringsTests` 三个不变量测试守卫（CI 红 = key 缺失 / typo / en-zh 不一致）。AXAML 用 `{x:Static i18n:Strings.X}`，code-behind 用 `Strings.X`。

---

## 项目结构

```
src/
├── SelectionAssistant.App/             ← 组合根：Program / App / 托盘 / 接线
├── SelectionAssistant.Core/            ← 领域模型、设置 record、i18n、输入触发器
├── SelectionAssistant.Infrastructure/  ← 配置 store、日志、JSON 序列化
├── SelectionAssistant.Platform.Abstractions/  ← 平台抽象接口（Windows 内部解耦，Core/Providers 平台无关的契约）
├── SelectionAssistant.Platform.Windows/       ← Win32 P/Invoke、hook、UIA、GDI、托盘
├── SelectionAssistant.Providers/      ← OpenAI 兼容翻译 / OCR client
└── SelectionAssistant.UI/             ← Avalonia 窗口、主题（Ivory Jade）、设置页
```

> `Platform.Abstractions` 是 Windows 内部的解耦设计，不做 macOS 移植；详见 [CHANGELOG](CHANGELOG.md) 的平台定位说明。

测试：`tests/SelectionAssistant.Core.Tests` / `.Providers` / `.Windows.IntegrationTests`。

---

## 已知限制与下一步

详见 `docs/AUDIT-findings.md` 和 `docs/BACKLOG-roadmap.md`。v0.1.0 之后排期中的硬骨头：

- **M1/M2**：`ClipboardHistoryWindow.axaml.cs`（~3300 行）和 `App.axaml.cs`（~2200 行）的 god-class 拆分
- **L3**：无障碍 `AutomationProperties.Name` 全层补齐（需屏幕阅读器验证）
- **M4**：剩余 ~46 处 `[DllImport]` → `[LibraryImport]` 迁移（hook / launcher / icon 高风险核心路径）
- **L8**：`IManagedWindow` 公共接口（与 M1/M2 耦合）
- 安装包 / 代码签名

---

## 路线图与审查

`docs/BACKLOG-roadmap.md` 是 R1–R54 路线图待办（含 120 批次演进历史），`docs/AUDIT-findings.md` 是按 P0/P1/P2/P3 分级的全代码库审查清单与修复进度。机器 agent 接续工作时先读根目录 `AGENTS.md`。

---

## License

[MIT](LICENSE) © Caelum
