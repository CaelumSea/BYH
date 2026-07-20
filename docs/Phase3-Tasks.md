# Phase 3 任务清单 — Provider System

**来源**: v4 方案 §9（Provider System）、§11.3（密钥存储）、§9.1（SSE 七种 case）
**工期**: 3–4 个工作日（v4 估算）
**硬验收门**: Ollama + 一个云端流式 Provider 跑通；密钥 DPAPI 加密；HTTP 默认禁重定向

---

## 当前实现状态（2026-07-17 接续）

| 任务 | 状态 | 已验证结果 |
|---|---|---|
| P3.0 | ✅ 完成 | `IStreamingTranslationProvider` + `TranslationDelta` + `ITranslationSessionView.AppendPartialResult`；Core 编译通过 |
| P3.1 | ✅ 完成 | 新建 `SelectionAssistant.Providers` 项目；`SseFrameReader` + `OpenAiSseEventParser` + `OpenAiChatStream`；7 种 case 全覆盖，25 个单测全过 |
| P3.2 | ✅ 完成 | `OpenAiCompatibleStreamingProvider`（双接口）；URI-aware URL 拼接；Bearer 认证；禁重定向；无 TLS 禁用选项；**2026-07-17 起固定带 thinking-disable 两参数** |
| P3.3 | ✅ 完成 | `ProviderConfigurationLoader`（镜像 capture-policies 模式）；6 个加载器单测全过；`providers.example.json` 示例 |
| P3.4 | ✅ 完成 | `ISecretStore` 接口 + `DpapiSecretStore`（DPAPI CurrentUser）；`--set-secret` 探针；5 个 DPAPI 单测全过（含"非明文"校验） |
| P3.5 | ✅ 完成 | `TranslationSessionManager` 流式分支（每个 chunk generation 守卫）；`ResultWindow.AppendPartialResult`；3 个流式会话单测全过 |
| P3.6 | ✅ 完成 | `SelectionRuntime` 组合根装配；无配置/无密钥/解析失败 → MyMemory 安全降级 |
| P3.7 | ✅ 完成 | 全测试 99/99 通过；NativeAOT 发布成功无警告；**DeepSeek V4 真实流式联调通过**（见下方测速） |

### P3.7 DeepSeek 真实联网联调（已完成，2026-07-17）

用 `--probe-translate-speed` 探针（本次新增）对流式翻译测速，**关闭思考模式**：

| 文本 | TTFB | 总耗时 | chunks | 输出字符 | 流速 |
|---|---|---|---|---|---|
| 短句（58 字符英文） | 707 ms | 1063 ms | 14 | 23 | 2.2 字符/100ms |
| 长句（209 字符英文） | 680 ms | 1144 ms | 31 | 66 | 5.8 字符/100ms |

译文质量正常，逐字流式显示。命令：
```powershell
.\SelectionAssistant.App.exe --probe-translate-speed "your text"
```

### 本次验证证据

- `dotnet build SelectionAssistant.slnx`：0 警告、0 错误。
- `dotnet test SelectionAssistant.slnx -c Release`：
  - Core.Tests：39/39（原 30 + 新增 9：6 配置加载器 + 3 流式会话）
  - Providers.Tests：25/25（新建：SSE 帧 7 + 解析器 11 + 流集成 3 + URI 4）
  - Windows.IntegrationTests：35/35（原 30 + 新增 5 DPAPI）
  - **合计 99/99，0 失败、0 跳过**（后续增量至 162/162，见 `00-CURRENT-HANDOFF.md`）
- `dotnet publish ... -c Release -r win-x64`（NativeAOT）：发布成功，无 AOT/裁剪警告。
- NativeAOT 主程序：22,856,704 字节（约 22.8 MB，较 P1 结束的 22.7 MB 增加约 140 KB）。
- 发布目录 PDB 数量：0。
- `--probe-policy`：退出码 0（App 仍正常启动）。
- `--set-secret secret://provider/test-probe test-value-12345`：退出码 0；密钥写入 `%LOCALAPPDATA%\BYH\secrets\{sha256}.bin`；blob 内容经 DPAPI 加密，非明文（已人工确认）。
- 无 `providers.json` 时组合根安全降级到 MyMemory（日志 + 不崩溃）。

### DeepSeek 真实联调步骤（用户执行）

1. 设置密钥（在终端执行，key 不进入对话/日志）：
   ```powershell
   .\artifacts\publish\win-x64-nativeuia\SelectionAssistant.App.exe `
     --set-secret secret://provider/deepseek <你的 DeepSeek API key>
   ```
2. 创建 provider 配置文件 `%LOCALAPPDATA%\BYH\providers.json`（参考 `docs\providers.example.json`）：
   ```json
   {
     "schemaVersion": 1,
     "defaultProviderId": "deepseek",
     "providers": [{
       "id": "deepseek",
       "name": "DeepSeek",
       "baseUrl": "https://api.deepseek.com",
       "apiKeyReference": "secret://provider/deepseek",
       "defaultModel": "deepseek-v4-flash",
       "chatCompletionsPath": "chat/completions",
       "timeoutSeconds": 60,
       "maxSourceCharacters": 8000
     }]
   }
   ```
   > **注意**：DeepSeek 已发布 V4 系列。旧模型名 `deepseek-chat` / `deepseek-reasoner` 将于 **2026-07-24** 弃用。新模型名 `deepseek-v4-flash`（经济）或 `deepseek-v4-pro`（高性能），base_url 不再带 `/v1`。详见 https://api-docs.deepseek.com/zh-cn/quick_start/pricing
3. 启动 BYH，选中一段英文，点击翻译。
4. 预期：结果窗口逐字流式显示中文译文（而非一次性显示）。

### SSE 七种 case 覆盖映射

| case | 描述 | 处理层 | 测试 |
|---|---|---|---|
| 1 | 帧跨读拆分 | `SseFrameReader` 帧累加器 | `SseFrameReaderTests.Case1_FrameSplitAcrossReads` |
| 2 | 多行 `data:` | `SseFrameReader` 事件聚合 | `SseFrameReaderTests.Case2` + `OpenAiChatStreamTests.FullStream` |
| 3 | UTF-8 跨缓冲拆分 | `StreamReader(UTF-8)` 有状态解码 | `SseFrameReaderTests.Case3_Utf8SplitAcrossBuffers` |
| 4 | 空 delta | `OpenAiSseEventParser` 跳过 | `OpenAiSseEventParserTests.Case4_*`（3 个） |
| 5 | 流中错误对象 | `OpenAiSseEventParser` 抛异常 | `OpenAiSseEventParserTests.Case5_*` + `OpenAiChatStreamTests.MidStreamError` |
| 6 | `[DONE]` | `OpenAiSseEventParser` 结束枚举 | `OpenAiSseEventParserTests.Case6_*`（2 个） |
| 7 | 取消 mid-frame | `OpenAiChatStream` 枚举取消 | `OpenAiChatStreamTests.Case7_CancellationMidFrame` |

---

## 任务详情

### P3.0 流式契约扩展

在 Core/Translation 增加流式契约，不破坏现有 `ITranslationProvider`：
- `IStreamingTranslationProvider.StreamAsync` → `IAsyncEnumerable<TranslationDelta>`
- `TranslationDelta(string Content)` 增量记录
- `ITranslationSessionView.AppendPartialResult(string chunk)` 视图增量方法

### P3.1 SSE 解析器

新建 `SelectionAssistant.Providers` 项目（net10.0，零第三方依赖）。两层分离：
- `SseFrameReader`（低层：Stream → SSE 事件块，case 1/2/3）
- `OpenAiSseEventParser`（高层：事件块字符串 → TranslationDelta/error/done，case 4/5/6）
- `OpenAiChatStream`（组合：HTTP 响应流 → IAsyncEnumerable，case 7）

### P3.2 OpenAI 兼容流式 Provider

`OpenAiCompatibleStreamingProvider` 实现 `IStreamingTranslationProvider` + `ITranslationProvider`：
- `POST {baseUrl}/{chatCompletionsPath}`，body `{"model":...,"messages":[...],"stream":true}`
- URI-aware 拼接（§9.3）：`ProviderUriBuilder` 防止 `/v1` 丢失
- 安全（§9.4）：`SocketsHttpHandler.AllowAutoRedirect=false`；无 TLS 禁用选项
- Bearer 认证（key 由 `ISecretStore` 解析 `secret://` 引用）
- 内建翻译 prompt 模板 + 分隔符（§11.1 prompt injection 风险消减）
- 错误映射到 `TranslationProviderException(userMessage)`

### P3.3 providers.json 配置加载器

`ProviderConfigurationLoader`（镜像 `CapturePolicyConfigurationLoader`）：
- schemaVersion 校验、文件大小上限、provider 数量上限
- 缺字段 → 默认值；类型错 → 抛 `ProviderConfigurationException`
- 文件无效时整体拒绝 → 日志脱敏错误 → 安全降级
- `providers.example.json` 示例；`ByhApplicationPaths` 增加 `ProvidersFile` + `SecretsDirectory`

### P3.4 密钥存储（DPAPI + --set-secret）

- `ISecretStore` 接口（Platform.Abstractions/Secrets）
- `DpapiSecretStore`（Platform.Windows/Secrets）：DPAPI `CurrentUser` + SHA-256 文件名
- `--set-secret <reference> <value>` 探针（Program.cs，Avalonia 启动前短路）
- 密钥永不进入对话或日志（用户自行在终端执行）

### P3.5 会话管理器流式分支

`TranslationSessionManager.RunAsync` 分支：
- provider 实现 `IStreamingTranslationProvider` → 流式路径（每个 chunk generation 守卫 + `AppendPartialResult`）
- 否则 → 现有一次路径（不动）
- `ResultWindow.AppendPartialResult`：首次清占位符，后续追加；`ShowLoading` 重置流式状态

### P3.6 组合根装配

`SelectionRuntime` 组合根装配：
- 读 `providers.json` → 选 defaultProviderId → 构造 `OpenAiCompatibleStreamingProvider`
- 安全降级链：无配置/无密钥/解析失败 → MyMemory
- 2026-07-17 改为 `SwitchToProvider` + 可变 provider 字段（支持热切换）

### P3.7 联调 + 测试 + 发布

见上方"当前实现状态"和"P3.7 DeepSeek 真实联网联调"小节。**已完成**。

---

## 多厂商 Provider 管理（M0–M6，已完成）

借鉴 CC Switch 核心概念（多 provider 列表 + 增删改 + 当前激活切换 + 内置预设），适配 BYH 翻译场景。

| # | 任务 | 状态 | 产出 |
|---|---|---|---|
| M0 | 配置层读写 | ✅ | `ProviderConfigurationLoader.Save`（Utf8JsonWriter 手写 + 原子写：临时文件 + `File.Move`）；`MutableProviderConfiguration` |
| M1 | 内置预设模板 | ✅ | `ProviderPresets.BuiltIn` — DeepSeek / OpenAI / 智谱 GLM / Moonshot Kimi + `CustomPresetId` |
| M2 | 运行时热切换 | ✅ | `TranslationSessionManager.ReplaceProvider`（bump generation + 取消在飞 CTS；旧 provider 迟到 chunk 被 generation 守卫丢弃） |
| M3 | 运行时 CRUD | ✅ | `SelectionRuntime` 的 `GetProviders` / `GetCurrentProviderId` / `AddProviderAsync` / `UpdateProviderAsync` / `DeleteProviderAsync`（级联删 DPAPI 密钥）/ `SetDefaultProviderAsync`（热切换）/ `SaveApiKeyAsync(reference, key)` / `HasApiKeyAsync(reference)` |
| M4 | 设置页 UI | ✅ | `SettingsWindow` — 下拉选择 + 内联编辑表单（名称/Base URL/模型/Chat 路径/密钥/超时）+ 新增（预设菜单）/保存/设为当前/删除 |
| M5 | App 层接线 | ✅ | `App.axaml.cs` 五个 CRUD 事件 handler + `RefreshSettingsAsync` 推送列表 + 密钥状态 |
| M6 | 测试 + 发布 + 文档 | ✅ | 99/99 测试过；NativeAOT 0 警告；本文档 + `00-CURRENT-HANDOFF.md` |

### 2026-07-17 增量

- **翻译关闭思考模式**：`BuildRequestBody` 固定带 `thinking:{type:disabled}`（DeepSeek）+ `enable_thinking:false`（Qwen）；OpenAI/GLM 忽略两者。跨厂商兼容，硬编码（翻译永远不需要思考）。
- **`--probe-translate-speed` 探针**：流式测速，报告 TTFB / 总耗时 / chunk 数 / 字符数 / 流速。
- **设置页修复**：下拉标签缩短为仅 provider 名称；切换 provider 立即刷新密钥状态（修复"加载不全"）；新增 Chat 路径编辑字段并持久化。

### 2026-07-17 第二批增量（用户路线图 R1/R2/R3/R5，全部完成）

详见 `handoff\BACKLOG-roadmap.md`。测试 99 → **109**（+6 ChordDetector + 4 R1 配置）。

- **R1 自定义提示词 + 思考开关**：`ProviderProfileEntry` + `TranslationRequest` + `OpenAiCompatibleProviderOptions` 加 `SystemPrompt`/`ThinkingEnabled`；`BuildRequestBody(request, options)` 解析 prompt（request→options→内置）+ 条件 thinking-disable；设置页加多行 SystemPrompt + 思考复选框；Loader/Save 读写且默认值省略（向后兼容）。
- **R2 Prompt Now 弹窗**：新建 `PromptWindow`；工具条加 "Prompt" 按钮；`SelectionRuntime.RunPromptAsync` 用 `TranslationRequest{SystemPrompt=userPrompt}` 复用流式 provider；`TranslationSessionManager` 加 `StartOrReplaceAsync(TranslationRequest)` 重载。
- **R3 左右键同按 chord**：`MouseMessageType` 加右键；`LowLevelMouseHook` 投射左右键且**始终放行**（安全关键）；新建 `ChordDetector`（400ms 窗口，latch，6 单测）；新建 `QuickToolsWindow`（光标浮层：翻译/总结/解释 + 自定义指令）；`SelectionRuntime` 暴露 `ChordTriggered` 事件，App.axaml 用 `Dispatcher.UIThread.Post` marshal；`SelectionSessionManager.GetLastCapturedText()`。
- **R5 复制/粘贴/剪切**：新建 `TextBoxContextMenu` 助手（Copy/Cut/Paste/全选，只读框隐藏 Cut/Paste，`ClipboardExtensions` API）；`ResultWindow` Attach 到两个 TextBox。

**R4 app 图标**（早些完成）：`Assets/app-icon.png` + `app-icon.ico`（多分辨率）；`.csproj` `<ApplicationIcon>` + `<AvaloniaResource>`；`App.axaml.cs` 用 `AssetLoader.Open` 加载，删 Base64 常量。exe 24.9 MB。

**待真机验证**：chord 与源应用右键菜单的交互、quick-tools 浮层失焦隐藏。
**待明确需求**：R3 的"快捷触发脚本"子项（脚本指什么？）。

### 2026-07-17 第三批增量（R6/R7/R8，全部完成）

详见 `handoff\BACKLOG-roadmap.md`。测试 109 → **116**（+7 PromptTemplatesStore）。

- **R6 全局提示词预设系统**：翻译/总结/解释三动作各有**可独立编辑**的系统提示词，所有 Provider 共用一套，存 `prompt-templates.json`。翻译默认提示词从"隐藏内置"改为可见可编辑（和总结/解释对等）。新增 `PromptTemplateSet`/`PromptTemplatesStore`/`PromptTemplateEditWindow`；设置页加"提示词模板"卡片；`SelectionRuntime.RunActionAsync`；QuickTools 总结/解释走全局预设（不再硬编码）。
- **R7 设置页 Provider 下拉精简**：`ProviderOption` 提为 public top-level record；ComboBox 加 `ItemTemplate` 编译绑定 `DisplayLabel`；下拉只显示 Provider 名。（踩坑：private nested record 编译绑定失败；`x:CompileBindings="False"` 破坏 NativeAOT。）
- **R8 托盘图标透明修复**：根因是 `WindowIcon(Stream)` 把原始 PNG 字节传给 `CreateIconFromResource`，Win32 需 ICO 容器才保 alpha。修复：托盘改加载 `app-icon.ico`（非 .png）；csproj 加 ICO AvaloniaResource；图标源换 `touming.png`；`gen-icon.ps1` 重新生成。
  - **⚠️ 第四批纠正**：第三批以为 ICO 容器是唯一根因，但用户反复反馈仍不透明。第四批发现**真正根因是源 PNG 内嵌棋盘格背景**（A=255 全不透明）。写了 `tools\MakeTransparent\` 抠图工具（边缘 flood-fill + 羽化 + 裁剪到主体 96% 填充）。
- **弹窗置顶**：PromptWindow + QuickToolsWindow 加 `Topmost="True"`（Settings/Result 不置顶）。
- **设置页隔开 + 自动跳转**：编辑区标题改 `▼ 编辑当前 Provider（{Name}）`；选 Provider 后 `BringIntoView` + `BaseUrlInput.Focus`。

### 2026-07-17 第四批增量（R9–R13，全部完成）

详见 `handoff\BACKLOG-roadmap.md`。测试 116 → **118**（+2 thinking 持久化）。

- **R9 思考模式迁移**：用户决定"Provider 里不设置思考，仅在提示词区修改"。`PromptTemplate.ThinkingEnabled` + `TranslationRequest.ThinkingEnabled` 单一真相源；**删除** provider 级 ThinkingEnabled（record/options/loader/UI）；旧 JSON 静默忽略；编辑窗口加思考复选框。
- **R8-2 图标抠图+裁剪**：发现源 PNG 内嵌棋盘格不透明；写 `tools\MakeTransparent\`（flood-fill 抠图+羽化+裁剪到 96% 填充）。
- **R10 QuickTools 复制/粘贴/管理提示词**：浮层加复制选中文本 + 粘贴到指令 + 管理提示词（跳转设置页滚到提示词区）。剪贴板坑：`ClipboardExtensions` 静态扩展方法 + `TryGetTextAsync`（非 `GetTextAsync`）。
- **R11 chord 选词剪贴板兜底**：chord 触发 QuickTools 按钮全灰的根因是 `GetLastCapturedText()` 只返回 BYH 会话文本；加 `Win32Clipboard.GetText()` 兜底。
- **R12 托盘重启**：`RequestRestart()` — detached spawn + exit。
- **R13 设置页排版紧凑化**：560×640，卡片间距/边距收紧，说明文字精简。

---

## 不在 Phase 3 / 多厂商范围（推迟 → 见 BACKLOG-roadmap.md）

- **quick-tools 可编辑提示词模板列表**（接 providers.json 或 templates.json）→ 待做
- **"快捷触发脚本"**（R3 子项，需明确需求）→ 待问用户
- **Phase 2 动作引擎**（解释/总结作为正式动作）→ quick-tools 当前临时实现
- **Ollama 本地验证**：代码可复用，待用户本地有 Ollama
- **Azure OpenAI 适配器**（§9.5）
- **重定向同源白名单**：当前全禁重定向（更安全）
