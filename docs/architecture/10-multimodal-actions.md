# 10 · 统一多模态动作（计划）

> **状态：已派发，未实现（2026-08-02）。**
> 对应本机 reqbase `REQ-036` / `TASK-053–055`。本文是可跟踪、可交接的仓库内架构计划，不表示功能已可用。

---

## 目标

让同一个兼容文本与图像输入的 Provider/Model/Secret 同时服务于 Vision 和翻译，并为 Ocean Eyes 增加单次多模态的“Translate Screenshot”。

“统一”是配置和执行能力的复用，不是将所有请求都变成图像请求。

## 当前链路

```text
截图
  → OpenAiCompatibleVisionOcrClient
  → OCR 文本
  → OpenAiCompatibleStreamingProvider
  → 翻译 / 解释 / 总结
```

两个 client 可以指向同一 Provider 甚至同一 model id，但仍然是两次 HTTP 请求。OCR 丢失的排版、图标、图表上下文无法在第二次请求中恢复。

## 目标选路

| 用户操作 | 输入 | 执行路径 | 保留原因 |
|---|---|---|---|
| 普通划词翻译/动作 | 纯文本 | 文本模式，不传图 | 最低延迟、最小上传量 |
| Ocean Eyes 识别文字/复制 | 截图 | 纯 OCR 模式 | 结果可编辑、复制和复用 |
| Ocean Eyes Translate Screenshot | 截图 + 指令 | 单次直接视觉动作 | 利用排版、图标、表格和混合语言上下文 |
| 直接视觉动作失败 | 截图 | OCR → 文本动作 | 兼容不支持图像输入或结构化输出的模型 |

## 首版产品合同

- Ocean Eyes 增加 **Translate Screenshot**。一次请求返回可分别复制的原文与译文。
- 请求契约预留 Explain/Summarize 复用，首版不为每个动作复制一套传输层。
- 结构化输出不完整时降级显示原始答案，不得导致后台进程退出。
- Provider 需明确图像输入能力；不能因 `/models` 返回了某个 id 就默认视觉通道可用。
- API Key 仍只通过 `secret://provider/{id}` + DPAPI 存储，不新增多模态专用明文配置。
- 日志可记录 provider/model、耗时、输入字节数和错误类别，不记录密钥、图片 base64、OCR 正文或翻译正文。
- 沿用现有超时、取消、generation 守卫、禁止重定向和 URI-aware 路径拼接不变量。

## 任务拆分

1. **TASK-053 · 能力感知的统一多模态请求层**
   扩展 OpenAI 兼容请求契约，让同一 Provider/Model/Secret 可按文本或图像输入调用。
2. **TASK-054 · Ocean Eyes 截图直译与回退交互**
   增加入口、原文/译文结果展示和两段式回退，不改变现有纯 OCR 与划词行为。
3. **TASK-055 · 直达/两段式对比验收**
   用固定样本比较成功率、响应时间、原文完整性和翻译可用性，并通过 Release、全量测试与 win-x64 NativeAOT。

## 非目标

- 不删除纯 OCR。
- 不将普通划词强制转换为截图上传。
- 不假定“视觉模型”必然是优秀的文本翻译模型。
- 不在没有真机样本对比前删除独立文本 Provider 选择能力。

## 验收证据

- 固定截图样本覆盖：普通文段、混合中英文、表格/UI、小字与特殊排版。
- 同一环境记录直达与两段式的成功率、中位/尾部延迟和输出完整性。
- 验证取消、超时、不支持图像的模型、无效结构化返回和 Provider 热切换。
- Release build 0 warning，全量测试通过，win-x64 NativeAOT 发布通过，真机完成至少一轮新旧链路对比。

## 相关文档

- `01-selection-capture.md` — Ocean Eyes 截图、OCR 与回退不变量。
- `03-translation-provider.md` — 文本 Provider、SSE、密钥与热切换不变量。
- `06-security-invariants.md` — 密钥、日志、重定向和 NativeAOT 边界。
