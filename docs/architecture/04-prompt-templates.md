# 04 · 自定义功能系统（Prompt Templates）

> **改提示词预设/自定义功能（增删改）/翻译总结解释/QuickTools 动态按钮前先读本文件。**

---

## 职责一句话

全局提示词预设：3 个内置功能（翻译/总结/解释）+ 任意用户自定义功能（润色/改写/续写…），所有 Provider 共用一套，存 `prompt-templates.json`。QuickTools 面板动态显示所有功能为按钮。

## 关键文件

| 文件 | 职责 |
|---|---|
| `Core/Translation/PromptTemplates.cs` | `PromptTemplate` record；`PromptTemplateSet`（List 模型）；`PromptActionIds`（3 内置 + custom- 前缀） |
| `Infrastructure/Configuration/PromptTemplatesStore.cs` | 加载/保存 prompt-templates.json；原子写；Utf8JsonWriter 手写 |
| `UI/Views/SettingsWindow.axaml(.cs)` | "自定义功能"卡片；ItemsControl 动态行；新增/编辑/删除 |
| `UI/Views/PromptTemplateEditWindow.axaml(.cs)` | 编辑弹窗；编辑模式 + 新建模式（名称可编辑）；思考开关 |
| `UI/Views/QuickToolsWindow.axaml(.cs)` | 动态功能按钮（ItemsControl）；`SetActions` 推送 |
| `UI/Views/PromptFunctionRow.cs` | 行 ViewModel（public top-level，编译绑定） |
| `UI/Views/RelayCommand.cs` | NativeAOT 安全的 ICommand（无反射） |
| `App/SelectionRuntime.cs` | `_promptTemplates` 字段；Get/Save/Reset/Add/Delete；`RunActionAsync` |
| `App/App.axaml.cs` | 事件接线；RefreshSettingsAsync 推送模板到设置页 + QuickTools |

## 数据模型

```
PromptTemplateSet
  └── List<PromptTemplate> Templates（有序：3 内置在前，custom 按添加顺序在后）
        ├── PromptTemplate(translate, "翻译", "...", thinking)
        ├── PromptTemplate(summarize, "总结", "...", thinking)
        ├── PromptTemplate(explain, "解释", "...", thinking)
        └── PromptTemplate(custom-a1b2c3d4, "润色", "...", thinking)  ← 用户自定义

PromptActionIds
  ├── Translate/Summarize/Explain = 固定常量（永不删除）
  ├── CustomPrefix = "custom-"
  ├── IsBuiltIn(id) / IsCustom(id) → 分类判断
  └── 自定义 id = custom- + Guid 前 8 位
```

## CRUD API（PromptTemplateSet）

| 方法 | 行为 |
|---|---|
| `Find(actionId)` | 列表查找，找不到 null |
| `TrySet(actionId, prompt)` | 改 prompt，保留 thinking；找不到 false |
| `TrySet(actionId, prompt, thinking)` | 改 prompt + thinking；找不到 false |
| `Add(template)` | 加自定义（必须 custom- 前缀，不重复）；内置/重复 false |
| `Remove(actionId)` | 删自定义（内置不可删）；找不到 false |
| `AsList()` | 返回 Templates 快照 |
| `IsBuiltIn(actionId)` / `IsCustom(actionId)` | 分类（UI 决定是否显示删除按钮） |

## 数据流

```
设置页编辑 → PromptTemplateSaved(actionId,prompt,thinking)
  → App.OnPromptTemplateSaved → runtime.SavePromptTemplateAsync
  → PromptTemplatesStore.Save（原子写）→ RefreshSettingsAsync 回显 + 推 QuickTools

设置页新增 → PromptTemplateAdded(name,prompt,thinking)
  → App.OnPromptTemplateAdded → runtime.AddPromptTemplateAsync
  → 生成 custom-{guid} id → set.Add → Save → Refresh

设置页删除 → PromptTemplateDeleted(actionId)
  → App.OnPromptTemplateDeleted → runtime.DeletePromptTemplateAsync
  → set.Remove（内置守卫）→ Save → Refresh

QuickTools 按钮点击 → ActionRequested(actionId, text)
  → App.OnQuickAction → runtime.RunActionAsync
  → Find(actionId) 拿提示词 → TranslationRequest → 流式
```

## 持久化（prompt-templates.json）

- schemaVersion: 1
- 内置 3 个：prompt 等于默认且 thinking=false 时**省略不写**（未来默认改进可传播）；translate 始终写（即使空）。
- 自定义动作：**始终写**（无默认值可比）。
- thinkingEnabled 仅 true 时写（legacy 无此 key → false）。
- `FromList` 合并：内置 id 覆盖默认值，custom id 追加。

## 不变量 / 踩坑

- **内置 3 个不可删**，id 不变（translate/summarize/explain）。
- **自定义 id 用 custom- 前缀**。
- DataTemplate 绑定类型 **public top-level**（`PromptFunctionRow`）；Command 不用反射。
- 原子写入（temp + Move）；Utf8JsonWriter 手写（AOT 安全）。
- `RunActionAsync` 对所有动作统一处理：空 prompt → null SystemPrompt → provider 内置（仅翻译有意义）。

## 改动检查清单

- [ ] 加新功能：用 Add（custom- id）；Save 持久化；Refresh 推 UI。
- [ ] 改提示词：用 TrySet；保留 thinking；Save。
- [ ] 改 QuickTools 按钮：SetActions 推送；ItemsControl + RunCommand。
- [ ] 改设置页行：ItemsControl + PromptFunctionRow（public top-level）。
- [ ] 改编辑窗口：ShowFor（编辑）/ ShowForNew（新建）；TemplateSaved / TemplateCreated 事件。
