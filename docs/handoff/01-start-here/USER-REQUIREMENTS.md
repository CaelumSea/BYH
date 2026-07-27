# 用户原始需求与约束

> 本文记录用户在整个对话中说出的需求和约束，按主题整理。接手 Agent 必须遵守这些。

---

## 产品定义（用户原话）

> "我现在需要做一个桌面软件，最好是轻量便捷一点。可以参考 cherryStudio 的划词翻译实现方式，他的弹窗是怎么实现的。everywhere的屏幕读取功能对划词有帮助吗"

核心诉求：**轻量便捷的划词助手**。选中文字 → 弹工具栏 → 点按钮发 LLM → 看结果。

---

## 硬约束（用户明确说过）

### 1. 跨平台 —— Windows + macOS

> "我不仅用win,还用Mac"

这条约束**直接否决了 WPF 方案**，强制选 Avalonia（跨平台）。任何只支持 Windows 的技术选型都不接受。

### 2. 个人使用 —— 无商业顾虑

> "我是个人用的，不用担心商业逻辑"

但许可证仍需谨慎（见下，因为参考项目是 BSL/AGPL）。

### 3. Clean-room 实现 —— 学思路不抄代码

> "确实可以不直接抄代码块，而是学习核心思路"

不直接复制 Everywhere（BSL）或 Cherry Studio（AGPL）的源码。参考架构和行为，自己写实现。

### 4. 动作范围

最初说："只要翻译 + 自定义"
后被外部评审纠正：MVP 应内置 **翻译 + 解释 + 总结 + 自定义**（用同一引擎，工程成本可忽略，更符合原始产品体验）。

用户接受了这个修正。所以内置动作 = **翻译 / 解释 / 总结 / 自定义 Prompt**。

---

## 用户工作偏好（元约束）

> "为什么一直问我,你应该把所有信息落地为任务文件,而不是干一下问一下"

**用户明确不喜欢被频繁打断。** 接手 Agent 应该：
- 把信息、决策、任务都落地成文件
- 自己能推进的不要问（如：选 .NET 10 而非 8，用 Avalonia 而非 WPF——这些都是已定决策）
- 只在**真正的岔路**（如：要不要装 3GB 的 VS 工具链）问用户
- 优先用 `TodoWrite` 跟踪进度，而不是反复确认

---

## 非目标（用户或评审明确排除的）

- ❌ **不做 Agent / MCP / 工具调用**：MVP 是文本助手，Prompt 不获得文件系统/命令执行能力
- ❌ **不做动态 DLL 插件**：与 NativeAOT 冲突，首期用数据驱动动作（ActionProfile JSON）
- ❌ **不承诺首字 <500ms**：外部网络和模型延迟不可控，改为分项指标（工具栏出现 P95<150ms）
- ❌ **DeepL 不是架构中心**：作为可选 Provider，不是核心
- ❌ **首期不用数据库**：用 JSON 配置文件（settings.json / actions.json / providers.json），只有加历史/搜索/同步时才引入 SQLite

---

## 参考项目（在 gh-kb 知识库里，可查阅架构思路）

| 项目 | 位置 | 参考价值 | 许可证 |
|---|---|---|---|
| **Everywhere** | `C:\dvr\gh-kb\sources\Everywhere\` | 选词检测（WH_MOUSE_LL + UIA）、WS_EX_NOACTIVATE、Accessibility Tree 取词 | **BSL 1.1**（禁商业复制） |
| **Cherry Studio** | `C:\dvr\gh-kb\sources\cherry-studio\` | 双窗口弹窗设计（toolbar 350×43 + result 500×400）、selection-hook | **AGPL-3.0**（禁闭源复制） |

**只看架构，不复制代码。** 详见 `04-external-reviews/review-1-license-and-scope.md` 的许可证分析。
