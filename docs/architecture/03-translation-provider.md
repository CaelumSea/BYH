# 03 · Provider 系统与翻译流式

> **改翻译/Provider 配置/SSE/热切换/联网前先读本文件。**

---

## 职责一句话

OpenAI 兼容的流式翻译 Provider，支持多厂商增删改 + 热切换；SSE 流式解析；每 chunk generation 守卫防旧 provider 迟到 chunk 污染。

## 关键文件

| 文件 | 职责 |
|---|---|
| `Core/Translation/ITranslationProvider.cs` | 翻译契约；`TranslationRequest`（含 SystemPrompt + ThinkingEnabled）；`TranslationDelta`/`TranslationResult` |
| `Core/Translation/TranslationSessionManager.cs` | 流式会话；`StartOrReplaceAsync`；`ReplaceProvider` 热切换；每 chunk generation 守卫 |
| `Core/Translation/TranslationLanguageSelector.cs` | 自动检测方向（中文→英，非中文→简中） |
| `Core/Translation/ProviderPresets.cs` | 内置 9 预设（DeepSeek/SiliconFlow/OpenAI/Zhipu/Moonshot/MiniMax/MiMo/OpenRouter/OpenCode Go）+ 自定义；模型 id 是快照，靠 /models 拉取覆盖 |
| `Providers/OpenAiCompatibleStreamingProvider.cs` | OpenAI 兼容流式；URI-aware URL 拼接；Bearer；禁重定向；条件 thinking-disable |
| `Providers/OpenAiCompatibleModelsClient.cs` | `GET {BaseUrl}/models` 模型目录拉取（设置页 "Refresh Models"）；`JsonDocument` 解析兼容 `{data:[]}` 与裸数组；AOT 安全 |
| `Providers/OpenAiCompatibleProviderOptions.cs` | Provider 配置项（BaseUrl/Model/ChatPath/Timeout/MaxChars） |
| `Providers/Sse/` | SSE 解析器（7 种 case，25 单测） |
| `Providers/MyMemoryTranslationProvider.cs` | 无密钥测试回退 |
| `Infrastructure/Configuration/ProviderConfiguration.cs` | providers.json 数据模型 + 加载/保存 |
| `Infrastructure/Configuration/ModelsCacheStore.cs` | models-cache.json（按 provider 缓存 `/models` 拉取结果 + UTC 时间戳）；原子写；AOT 安全手写 Utf8JsonWriter |

## 数据流

```
RunActionAsync(actionId, text)（App）
  → PromptTemplateSet.Find(actionId) 拿提示词（Core，详见 04）
  → TranslationLanguageSelector.CreateRequest(text) 定方向
  → request.SystemPrompt = template.Prompt（空→null→provider 内置模板）
  → request.ThinkingEnabled = template.ThinkingEnabled（单一真相源）
  → _windowHost.Hide()（隐藏工具条）
  → TranslationSessionManager.StartOrReplaceAsync(request)
    → OpenAiCompatibleStreamingProvider.StreamAsync
      → BuildRequestBody（解析 prompt：request→options→内置；条件 thinking-disable）
      → HTTP POST（禁重定向；Bearer；URI-aware URL）
      → SSE 流式解析
      → 每个 chunk → generation 守卫 → ResultWindow 追加
```

## thinking-disable（跨厂商）

翻译**默认关思考**，跨厂商：
- DeepSeek：`thinking:{type:disabled}`
- Qwen：`enable_thinking:false`
- OpenAI/GLM：忽略两者
- 仅当 `request.ThinkingEnabled == true`（自定义功能开启思考）才不 disable

**单一真相源**：`TranslationRequest.ThinkingEnabled` 由提示词模板注入，Provider 不再自带 thinking 设置。

## 热切换

`SwitchToProvider(entry)`：Dispose 旧 provider → 新 provider 注入 session manager → 更新 label/reference。热切换后旧 provider 的迟到 chunk 被 generation 守卫丢弃。

## 不变量 / 踩坑

- **HTTP 默认禁重定向**；无 TLS 禁用选项。
- **URL 拼接 URI-aware**（`ProviderUriBuilder`），防路径注入。
- **密钥走 DPAPI**，providers.json 只存 `secret://` 引用。
- **每 chunk generation 守卫**——热切换/新会话后旧 chunk 不污染。
- 空翻译 prompt → null SystemPrompt → provider 用内置翻译模板。

## 改动检查清单

- [ ] 改 Provider：保持 OpenAI 兼容；禁重定向；URI-aware。
- [ ] 改流式：每 chunk 过 generation 守卫。
- [ ] 改 thinking：只读 request.ThinkingEnabled，不在 provider 加配置。
- [ ] 改热切换：Dispose 旧的；新 provider 注入后 generation 自增。
