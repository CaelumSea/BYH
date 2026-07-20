# BYH 架构总览

> **这是改任何模块前的第一站。** 本文件给出全局架构图、模块速查表、"改 X 先看 Y"的导航。
> 各模块详细文档在同目录的 `01-` ~ `08-` 文件里。

---

## 一句话定义

`BYH`（By Your Hand）= Windows NativeAOT 选词翻译 + AI 动作工具。选中文字 → 不抢焦点的工具条/面板 → 翻译/总结/解释/自定义功能。多厂商 Provider（DeepSeek/OpenAI 兼容），DPAPI 密钥加密，全局可编辑提示词预设。

---

## 项目分层（7 个项目）

```
SelectionAssistant.App              ← 组合根 + 生命周期 + 五窗口接线
  ├── SelectionAssistant.UI          ← Avalonia 窗口 + ViewModel（无业务逻辑）
  ├── SelectionAssistant.Core         ← 领域模型 + 选词会话 + 翻译契约（纯逻辑，无 Win32）
  ├── SelectionAssistant.Infrastructure ← 持久化（JSON 配置/密钥/原子写）
  ├── SelectionAssistant.Providers    ← OpenAI 兼容流式 Provider + SSE 解析
  ├── SelectionAssistant.Platform.Windows ← Win32 互操作（钩子/UIA/剪贴板/窗口宿主）
  └── SelectionAssistant.Platform.Abstractions ← 平台无关接口（鼠标事件/取词/密钥）
```

**依赖方向**：App → 所有；UI → Core + Abstractions；Core → Abstractions；Infrastructure → Core；Providers → Core + Abstractions；Platform.Windows → Abstractions + Core。**Core 永远不依赖 UI/Platform.Windows**（保持可测试）。

---

## 模块速查表（改 X 先看 Y）

| 你要改的功能 | 先读这份文档 |
|---|---|
| 选词不灵 / 鼠标钩子 / 手势判定 / 取词失败 | `01-selection-capture.md` |
| 工具条/面板/弹窗的显示、定位、失焦、拖拽、chord 触发 | `02-windowing.md` |
| Provider 配置 / 翻译流式 / SSE / 热切换 / 联网失败 | `03-translation-provider.md` |
| 提示词预设 / 自定义功能（增删改）/ 翻译总结解释 | `04-prompt-templates.md` |
| providers.json / prompt-templates.json / 密钥存储 / 原子写 | `05-configuration-persistence.md` |
| 安全规则（密钥/重定向/钩子不吞事件/NativeAOT） | `06-security-invariants.md` |
| 构建命令 / 发布 / 启动 / 探针 / 单实例锁 | `07-build-publish-run.md` |
| 主题色 / 控件状态 / 卡片材料 / 窗口视觉 | `08-theme-system.md` |

---

## 七窗口职责（详见 `02-windowing.md`）

| 窗口 | 触发方式 | 职责 | 抢焦点？ |
|---|---|---|---|
| `ToolbarWindow` | 划词（拖选松开） | 窄条：翻译/解释/总结/Prompt/复制/粘贴 | 否（WS_EX_NOACTIVATE） |
| `QuickToolsWindow` | 键盘快捷键（默认 Ctrl+Alt+Q，可配）或左右键同按（chord，默认关，可在设置页开启） | 浮层面板：动态功能按钮 + 自定义指令 + 复制/粘贴/管理功能 + 画框 OCR + OCR 结果 | Topmost，失焦隐藏 |
| `PromptWindow` | 工具条"Prompt" | 输入任意指令对选中文本执行 | Topmost |
| `PromptTemplateEditWindow` | 设置页新增/编辑功能 | 编辑功能名称、提示词和思考开关 | Topmost |
| `ResultWindow` | 翻译/动作完成 | 流式显示结果 + 重试/关闭 | 否 |
| `SettingsWindow` | 托盘菜单/管理功能 | Provider CRUD + 自定义功能 CRUD + 进程策略 | 否 |
| `RegionSelectOverlay` | QuickTools 的画框 OCR | 全屏遮罩、绘制/移动/缩放选区、确认/取消 | Topmost |

---

## 主链路数据流

```
用户选中文字（拖选）
  → WH_MOUSE_LL 钩子捕获坐标（Platform.Windows，原生线程）
  → 手势分类器判定为"选择"（Core）
  → SelectionSessionManager 启动取词会话（Core）
    ├── UIA TextPattern 取词（Platform.Windows）+ 剪贴板回退
    └── 75ms 防闪烁后显示 ToolbarWindow（UI，Dispatcher 切线程）
  → 取词完成 → 工具条按钮可用
  → 用户点"翻译" → RunActionAsync(translate)（App）
    → 读 PromptTemplateSet 找提示词（Core）
    → 构造 TranslationRequest（Core）
    → OpenAiCompatibleStreamingProvider 流式请求（Providers）
    → SSE 解析 → 每个 chunk generation 守卫 → ResultWindow（UI）
```

**QuickTools 链路**：键盘快捷键（默认 Ctrl+Alt+Q）→ 注册热键回调 → Dispatcher → QuickToolsWindow.ShowAt → 动态功能按钮。**chord 链路**（默认关）：左右键同按 → ChordDetector → ChordTriggered → Dispatcher → QuickToolsWindow.ShowAt。

**主题链路**：`App.axaml` → `FluentTheme` → `UI/Themes/IvoryJade.axaml` → Views 通过 `DynamicResource` + 语义 Classes 消费。主题规则和不变量见 `08-theme-system.md`。

---

## 关键不变量（违反必出 bug，详见 `06-security-invariants.md`）

1. 钩子回调**绝不直接访问 UI**——必须 `Dispatcher.UIThread.Post` 切线程。
2. 钩子**始终 CallNextHookEx 放行**——绝不吞事件，否则破坏源应用右键菜单。
3. ToolbarWindow 用 `WS_EX_NOACTIVATE`——**永不 SetForegroundWindow**（会抢焦点，选中高亮消失）。
4. API 密钥**绝不进明文 JSON**——DPAPI 加密，providers.json 只存 `secret://` 引用。
5. **0 警告**（TrimMode=full）——不用反射绑定；DataTemplate 绑定类型必须 public top-level。
6. 配置文件**原子写入**（temp + File.Move）。
7. JSON 用 **Utf8JsonWriter 手写**（AOT 安全，不用反射序列化）。
