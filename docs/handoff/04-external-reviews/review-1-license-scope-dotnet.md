## 总体结论

这份方案作为“Windows 划词翻译 MVP”是可行的，但它并不是你最初要求的“可自定义能力、可配置模型接口的划词 AI 助手”。

最大问题不是技术细节，而是**需求已经被缩减错了**：文档明确把“多 Prompt 模板”列为非目标，并把后端简化为 DeepL 等翻译接口。 

我的评价是：

* 作为翻译演示版：**7/10**
* 作为你原始产品方案：**4/10**
* 优化后可继续沿用：Windows 优先、C#、双窗口、分阶段验证
* 必须重构：许可证策略、Prompt 系统、模型 Provider、取词降级链、打包目标和工期

---

## 一、最需要立即修正的问题

### 1. 产品定位发生了偏移

原方案的目标只是“选中文字后弹出翻译工具栏”，并明确不做多 Prompt 模板。

但你的原始需求至少包括：

* 翻译
* 单词或段落解释
* 总结
* 润色、改写等预设能力
* 用户自定义 Prompt
* 自定义 API 地址、端口、模型和密钥

因此产品核心不应该叫“翻译层”，而应该叫：

> **文本动作引擎 Text Action Engine**

翻译只是一个默认动作，不应成为架构中心。

建议内置以下动作：

| 动作   | 默认 Prompt                   |
| ---- | --------------------------- |
| 翻译   | 将选中文本翻译为 `{targetLanguage}` |
| 解释   | 用简单语言解释选中文本                 |
| 总结   | 总结选中文本的核心内容                 |
| 润色   | 改善表达，不改变原意                  |
| 改写   | 按 `{tone}` 风格改写             |
| 编程解释 | 解释代码用途、输入、输出和风险             |
| 自定义  | 用户自行编写 Prompt 模板            |

工具栏按钮直接对应“动作”，而不是写死“翻译、复制、搜索”。

---

### 2. 直接移植 Everywhere 代码存在严重许可证风险

这是现有方案最容易被忽略、但最可能阻塞商业发布的问题。

Everywhere 当前使用 **Business Source License 1.1**。其许可证明确把提供相同或实质相似功能的商业产品列为“Competing Use”，并限制这种使用；相关版本要到首次公开发布四年后才自动转为 Apache 2.0。([GitHub][1])

因此，“直接移植约 600 行 Everywhere 代码”的做法不能默认视为安全。

Cherry Studio 使用 AGPL-3.0。参考交互和窗口架构通常没有问题，但复制或改造其代码可能使整个分发产品承担 AGPL 的源代码开放义务。([GitHub][2])

建议采用以下策略：

1. **可以研究行为和架构，不直接复制实现代码。**
2. 根据 Win32 官方 API 做 clean-room 实现。
3. 自己编写鼠标钩子、剪贴板状态机和窗口定位模块。
4. 建立 `THIRD_PARTY_NOTICES.md` 和依赖许可证扫描。
5. 商业发布前对 BSL、AGPL 复用范围做一次正式法律评估。

Win32 官方已经完整定义了低级鼠标钩子、钩子链和资源释放要求，因此并不需要依赖 Everywhere 的具体源代码才能实现。([Microsoft Learn][3])

---

### 3. .NET 版本选择已经过时

文档选择了 .NET 8，并把“.NET 9 最新 LTS”作为备选。

截至 2026 年 7 月：

* .NET 10 是当前 LTS，支持到 2028 年 11 月。
* .NET 9 是 STS，不是 LTS。
* .NET 8 将于 2026 年 11 月 10 日结束支持。([Microsoft][4])

因此新项目应直接使用：

> **C# / .NET 10 LTS**

没有理由在现在启动的新产品中继续以 .NET 8 为基线。

---

### 4. “WPF + NativeAOT ≤20MB”不能作为已确认结论

当前方案一方面把 WPF NativeAOT 作为正式发布路径，另一方面又在风险部分承认其兼容性尚未验证。

NativeAOT 本身有这些重要限制：

* 不支持动态 Assembly 加载
* 不支持运行时代码生成
* Windows 下没有内置 COM
* 强制 trimming
* 部分库没有完整 AOT 标注
* 自包含运行库可能增加文件尺寸，而不是必然缩小([Microsoft Learn][5])

这会影响反射、动态插件、某些 XAML 行为和第三方库。

### 推荐选择

**方案 A：可靠性优先**

* .NET 10 + WPF
* Self-contained 或 framework-dependent 发布
* 暂不承诺 20MB
* Windows 原生行为和调试体验更稳定

**方案 B：体积和未来跨平台优先**

* .NET 10 + Avalonia
* NativeAOT
* Windows 首发，但保留 macOS/Linux 扩展可能

Avalonia 已有官方 NativeAOT 部署文档，但同样要求编译期 XAML、减少反射和动态资源，并明确存在第三方控件兼容性限制。([docs.avaloniaui.net][6])

我的建议是选择 **方案 B：.NET 10 + Avalonia + Windows 专用 Interop 层**。这也更接近 Everywhere 当前的 .NET 10/Avalonia 技术组合，但实现必须自行编写。([GitHub][1])

安装包体积应该是阶段 0 的实测结果，而不是前置承诺。

---

## 二、优化后的系统架构

```text
┌────────────────────────────────────────────────────┐
│                    Desktop UI                       │
│  Selection Toolbar │ Result Window │ Settings       │
└──────────────────────────┬─────────────────────────┘
                           │
┌──────────────────────────▼─────────────────────────┐
│                  Text Action Engine                 │
│  Action Registry │ Prompt Renderer │ Execution      │
│  Translate       │ Explain         │ Summarize      │
│  Rewrite         │ Custom Prompt   │ Cancel/Retry   │
└───────────────┬───────────────────────┬─────────────┘
                │                       │
┌───────────────▼────────────┐  ┌──────▼──────────────┐
│ Text Acquisition Pipeline │  │   Model Router       │
│ 1. UI Automation          │  │ Provider Profiles    │
│ 2. Clipboard Copy         │  │ Model Overrides      │
│ 3. Manual Clipboard       │  │ Capability Detection │
└───────────────┬────────────┘  └──────┬──────────────┘
                │                       │
┌───────────────▼────────────┐  ┌──────▼──────────────┐
│ Windows Integration       │  │ Provider Adapters    │
│ Mouse Hook / Hotkeys      │  │ OpenAI-compatible    │
│ DPI / Multi-monitor       │  │ Azure OpenAI         │
│ Foreground Process        │  │ Native Gemini        │
│ Tray / Startup            │  │ Optional DeepL       │
└────────────────────────────┘  └─────────────────────┘
```

建议代码结构：

```text
src/
  WordCrossing.App/
  WordCrossing.Core/
    Actions/
    Prompts/
    Models/
    Configuration/
  WordCrossing.Providers/
    OpenAICompatible/
    AzureOpenAI/
    Gemini/
    DeepL/
  WordCrossing.Platform.Windows/
    Hooks/
    Clipboard/
    UIAutomation/
    Windowing/
    Hotkeys/
  WordCrossing.Infrastructure/
    Secrets/
    Logging/
    Updates/
tests/
  WordCrossing.Core.Tests/
  WordCrossing.Provider.Tests/
  WordCrossing.Windows.IntegrationTests/
```

---

## 三、Prompt 与自定义动作设计

不要使用动态 DLL 插件作为首期扩展机制。NativeAOT 与动态 Assembly 加载天然冲突。首期采用**数据驱动动作**即可。

### ActionProfile

```json
{
  "id": "explain-simple",
  "name": "简单解释",
  "icon": "Lightbulb",
  "enabled": true,
  "promptTemplate": "请用简单、准确的语言解释以下内容：\n\n{{text}}",
  "systemPrompt": "你是一名善于解释复杂概念的助手。",
  "providerId": "local-ollama",
  "modelOverride": null,
  "temperature": 0.3,
  "maxOutputTokens": 800,
  "stream": true,
  "showInToolbar": true,
  "order": 20
}
```

支持的变量建议限制为：

```text
{{text}}
{{sourceLanguage}}
{{targetLanguage}}
{{applicationName}}
{{windowTitle}}
{{currentDate}}
```

不要让用户 Prompt 获得文件系统、命令执行或任意工具调用能力。你的软件是文本助手，不需要在 MVP 阶段引入 Agent 或 MCP。

---

## 四、自定义 API 地址与端口

建议不要分别保存“主机”和“端口”，而是保存完整 `BaseUrl`：

```text
https://api.openai.com/v1
http://127.0.0.1:11434/v1
https://company-gateway.example.com:8443/v1
```

官方 OpenAI .NET SDK已经支持自定义 Endpoint，可直接连接代理或自托管 OpenAI-compatible 模型，并支持流式返回。([GitHub][7])

Ollama 提供部分 OpenAI API 兼容接口，其默认示例地址是 `http://localhost:11434/v1/`。 ([Ollama][8])

Gemini 也提供 OpenAI compatibility 接口，但官方仍将其标为 beta，因此最好保留原生 Gemini Adapter 作为后续兼容方案。([Google AI for Developers][9])

### ProviderProfile

```json
{
  "id": "local-ollama",
  "name": "Local Ollama",
  "type": "OpenAICompatible",
  "baseUrl": "http://127.0.0.1:11434/v1",
  "apiKeyReference": "secret://provider/local-ollama",
  "defaultModel": "qwen3:8b",
  "timeoutSeconds": 60,
  "customHeaders": {},
  "supportsStreaming": true
}
```

设置界面至少提供：

* Provider 类型
* API Base URL，包括端口
* API Key
* 默认模型
* 自定义 Header
* 超时时间
* HTTP 代理
* “测试连接”按钮
* “获取模型列表”按钮
* 每个动作可覆盖 Provider 和模型

Azure OpenAI 最好使用单独 Adapter，因为其接口还涉及资源 Endpoint、Deployment ID 和 `api-version`。([Microsoft Learn][10])

---

## 五、取词模块应改为降级链

原方案把 Ctrl+C/Clipboard 作为唯一正式路径，把 UI Automation 放到后续。

建议改成：

```text
UI Automation TextPattern
        ↓ 失败
Ctrl+Insert
        ↓ 失败
Ctrl+C
        ↓ 失败
提示用户按快捷键，在当前剪贴板文本上执行
```

不需要移植完整 UI 树，只实现一个最小 `UIAutomationTextExtractor`。

### 必须处理的两个 Windows 边界

第一，`SendInput` 受 UIPI 限制，普通权限进程不能向更高完整性级别的管理员应用注入输入，而且 API 返回值不一定明确指出失败来自 UIPI。([Microsoft Learn][11])

因此应：

* 检测目标进程完整性级别
* 遇到管理员程序时不反复注入
* 显示“请手动复制后调用快捷键”
* 不建议让应用长期以管理员身份运行

第二，剪贴板恢复必须防止覆盖用户的新内容。

推荐状态机：

```text
保存原剪贴板及 sequence=A
注入复制
等待 sequence 变为 B
读取选中文本
准备恢复前再次读取 sequence
若仍为 B：恢复原剪贴板
若已经变为 C：说明用户或其他应用修改过剪贴板，不恢复
```

Windows 的剪贴板序列号会在内容改变或清空时增加，适合实现这种竞争检测。([Microsoft Learn][12])

另外，低级 Hook 回调里不要做剪贴板、HTTP 或 UI 操作。Hook 线程必须保持消息循环并快速调用下一钩子，否则会影响其他应用。([Microsoft Learn][3])

---

## 六、交互层可以保留，但要调整执行时机

双窗口设计是原方案中最正确的决定之一，可以保留。

进一步优化：

1. 鼠标抬起后立即显示工具栏，不等待取词完成。
2. 后台并行获取选中文本。
3. 用户点击动作时：

   * 文本已就绪则立即执行；
   * 尚未就绪则按钮显示短暂加载。
4. 新的选词操作自动取消旧请求。
5. 结果窗口支持：

   * 停止生成
   * 重试
   * 切换模型
   * 复制
   * 替换原文本，后续版本
   * 固定窗口
6. 工具栏最多直接展示 4–6 个动作，其余放入“更多”。

“首字 <500ms”不应该作为统一产品指标，因为外部网络和模型延迟不可控。建议改成：

* 工具栏出现：P95 < 150ms
* 本地处理开销：P95 < 80ms
* 模型首 Token：按 Provider 分别统计
* 请求可以在 100ms 内被取消

---

## 七、配置与安全

建议初期不使用数据库：

```text
settings.json        非敏感设置
actions.json         Prompt 和动作
providers.json       Provider 元数据，不含明文密钥
系统安全存储          API Key
```

只有加入历史记录、搜索和同步后再引入 SQLite。

安全要求：

* API Key 不以明文写入 JSON
* 日志默认不记录选中文本
* `Authorization` 和自定义认证 Header 必须脱敏
* 远程 API 默认要求 HTTPS
* HTTP 只允许 localhost、127.0.0.1 或用户显式确认
* 限制选中文本最大长度，例如 30,000 字符
* 支持请求超时、取消和重试退避
* 将选中文本视为“不可信数据”，用明确分隔符放入 Prompt，避免其冒充系统指令
* 崩溃报告默认不包含 Prompt、文本和密钥

---

## 八、优化后的开发计划

原方案的 7–11 天，只适合“DeepL 翻译演示版”。

完整 MVP 更合理的单人开发量为 **15–24 个工作日**。

| 阶段 | 工作内容                                    |    估算 |
| -- | --------------------------------------- | ----: |
| 0  | 许可证确认、WPF/Avalonia AOT 实测、Hook/取词 Spike | 2–3 天 |
| 1  | 鼠标钩子、UIA、剪贴板降级链、DPI 和多屏                 | 4–5 天 |
| 2  | Action Engine、自定义 Prompt、配置模型           | 3–4 天 |
| 3  | OpenAI-compatible Provider、流式输出、取消重试    | 3–4 天 |
| 4  | 双窗口、设置页、托盘、快捷键、开机启动                     | 3–4 天 |
| 5  | 兼容性测试、日志、安装包、自动更新准备                     | 3–4 天 |

### MVP 验收标准

* 可在浏览器、记事本、Office、VS Code 和常见 PDF 阅读器中取词
* 支持拖动选取和双击选词
* 至少提供翻译、解释、总结和两个自定义动作
* 可设置任意 OpenAI-compatible Base URL 和端口
* 可连接 OpenAI-compatible 云端、Ollama 和代理网关
* API Key 不明文保存
* 支持流式输出、停止和重试
* 不覆盖用户在执行期间新复制的剪贴板内容
* 在管理员应用、终端、远程桌面等失败场景中安全降级
* 在 100%、125%、150%、200% DPI 和多显示器下正确定位

---

## 最终技术决策建议

| 决策     | 建议                                           |
| ------ | -------------------------------------------- |
| 操作系统   | Windows 10/11 首发                             |
| 语言     | C#                                           |
| 运行时    | **.NET 10 LTS**                              |
| UI     | 优先 Avalonia；若 AOT 实测不理想则使用 WPF               |
| 选词检测   | 自研 Win32 `WH_MOUSE_LL`，不复制 BSL 代码            |
| 取词     | UI Automation → Ctrl+Insert → Ctrl+C → 手动剪贴板 |
| 窗口     | 保留双窗口                                        |
| 核心后端   | OpenAI-compatible LLM                        |
| 翻译 API | DeepL 作为可选专用 Provider，不作为架构中心                |
| Prompt | 数据驱动 ActionProfile                           |
| API 端口 | 通过完整 Base URL 配置                             |
| 插件     | MVP 不加载动态 DLL                                |
| 发布     | 先实测 AOT；同时保留非 AOT 发布方案                       |
| 工期     | 15–24 个工作日，比原计划更可信                           |

核心方向可以概括为：

> **保留原方案的 Windows 取词与双窗口思路，删除对受限许可证代码的直接依赖，把“翻译层”升级成“动作与 Prompt 引擎”，把 DeepL 单接口升级成可配置的模型 Provider 系统。**

[1]: https://github.com/Sylinko/Everywhere "GitHub - Sylinko/Everywhere: On-screen aware AI assistant for your desktop. Uses current app context, multiple LLMs, and MCP tools to help you act across apps. · GitHub"
[2]: https://github.com/CherryHQ/cherry-studio "GitHub - CherryHQ/cherry-studio: AI productivity studio with smart chat, autonomous agents, and 300+ assistants. Unified access to frontier LLMs · GitHub"
[3]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexa "SetWindowsHookExA function (winuser.h) - Win32 apps | Microsoft Learn"
[4]: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core ".NET and .NET Core official support policy | .NET"
[5]: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ "Native AOT deployment overview - .NET | Microsoft Learn"
[6]: https://docs.avaloniaui.net/docs/deployment/native-aot "Native AOT | Avalonia Docs"
[7]: https://github.com/openai/openai-dotnet "GitHub - openai/openai-dotnet: The official .NET library for the OpenAI API · GitHub"
[8]: https://docs.ollama.com/openai "OpenAI compatibility - Ollama"
[9]: https://ai.google.dev/gemini-api/docs/openai "OpenAI compatibility  |  Gemini API  |  Google AI for Developers"
[10]: https://learn.microsoft.com/en-us/azure/ai-foundry/openai/reference "Azure OpenAI image and audio REST API reference (2024-10-21) - Microsoft Foundry | Microsoft Learn"
[11]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput "SendInput function (winuser.h) - Win32 apps | Microsoft Learn"
[12]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber "GetClipboardSequenceNumber function (winuser.h) - Win32 apps | Microsoft Learn"
