# 交接总指引 — 选词助手项目

> **接手的 Agent：请先完整读本文件，再按"推荐阅读顺序"看其余文件。本文件告诉你项目是什么、做到哪了、下一步做什么、哪些坑必须避开。**

---

## 一句话项目定义

做一个 **Windows + macOS 双平台的轻量选词助手**：用户在任何应用里选中文字，松开鼠标后弹出一个**不抢焦点的小工具栏**（保住选中高亮），点工具栏上的"翻译/解释/总结/自定义"按钮，把选中文本发给可配置的 LLM，结果流式显示在旁边的结果窗口。个人使用，非商业。

技术栈：**C# / .NET 10 LTS / Avalonia UI 12.1** + Win32 互操作（Windows）/ AppKit（macOS）。

---

## 当前状态（2026-07-16）

### ✅ Phase 0 验证已完成 —— 三道硬关卡全部通过

这是整个项目最大的风险，现在已经排除：

| 关卡 | 验证内容 | 结果 |
|---|---|---|
| **门1** | WH_MOUSE_LL 低级鼠标钩子能否在 Avalonia 宿主进程内捕获全局鼠标坐标 | ✅ 通过 |
| **门2**（命脉） | WS_EX_NOACTIVATE 注入 Avalonia 窗口后，弹窗是否**不抢焦点**（源应用选中高亮保住） | ✅ **用户实测确认高亮保住** |
| **门3** | 钩子→手势判定→坐标弹窗的完整链路 | ✅ 通过 |
| **NativeAOT** | Avalonia + .NET 10 的 NativeAOT 编译可行性 + 体积 | ✅ 通过（exe 18.46MB，运行正常） |

**结论：技术路线完全可行，可以放心进入正式开发。**

### 🚧 Phase 1 进行中 —— P1.0–P1.6 已完成

已完成：正式多项目骨架、全局鼠标钩子、系统指标手势判定、并发会话管理、UIA 取词、安全剪贴板回退、进程策略链、v0.1 无密钥翻译和结果窗口，以及最小托盘/设置/退出。当前自动化测试 60/60 通过，NativeAOT 可发布运行。

下一步：完成 P1.7 DPI/多显示器定位，并执行 P1.8 应用语料库 95% 取词验收。先读 `..\00-CURRENT-HANDOFF.md`，再看 `06-project-skeleton/Phase1-Tasks.md` 顶部。

---

## 推荐阅读顺序（按依赖关系）

| 顺序 | 文件 | 内容 | 必读 |
|---|---|---|---|
| 1 | `01-start-here/HANDOFF-GUIDE.md`（本文件） | 全局认知 | ★★★ |
| 2 | `01-start-here/USER-REQUIREMENTS.md` | 用户原话需求 + 约束 | ★★★ |
| 3 | `02-phase0-results/Phase0-Validation-Report.md` | 三道门验证证据 + NativeAOT 体积 | ★★★ |
| 4 | `07-key-decisions/KEY-DECISIONS-AND-TRAPS.md` | 踩过的坑 + 必须遵守的硬规则 | ★★★ |
| 5 | `03-master-plan/Selection-Assistant-Plan-v4.md` | 实施基线方案（完整技术规格） | ★★☆ |
| 6 | `06-project-skeleton/Phase1-Tasks.md` | Phase 1 文件级任务拆解 | ★★☆ |
| 7 | `05-spike-reference/` | 已验证的 spike 代码（迁移参考） | ★☆☆ |
| 8 | `04-external-reviews/` | 三轮外部评审（理解为何这么设计） | ★☆☆ |

---

## 最关键的几条硬规则（违反必出 bug）

1. **钩子回调里绝对不能碰 UI**。Avalonia 单线程 UI 模型，钩子线程直接访问 UI 属性会抛 `InvalidOperationException`。必须 `Dispatcher.UIThread.Post` 切线程。详见 `07-key-decisions/`。

2. **`WS_EX_NOACTIVATE` 是命脉**，永远不要用 `SetForegroundWindow`（它会激活窗口，抢焦点，选中高亮消失，整个产品失效）。显示窗口只用 `SW_SHOWNOACTIVATE` + `SetWindowPos(SWP_NOACTIVATE)`。

3. **拖拽判定必须轴式**，不能用欧氏距离。`SM_CXDRAG`/`SM_CYDRAG` 是矩形指标。双击判定必须包含同窗口/同进程/同按键检查。删除 `DRAG_MAX_MS`（慢速多段选择合法）。

4. **取词链必须立即启动**，不能先等防抖延迟。v3 评审明确指出这是前置必改项。capture 在会话第一行启动，防抖并发运行。

5. **剪贴板是"最佳努力"**，不能承诺全格式恢复。用 `GetClipboardSequenceNumber` 做竞争检测，恢复前必须重检序列号。

6. **Thread priority 保持 Normal**，不要设 Highest。钩子超时是注册表配置，上限 1000ms，回调设计在个位数毫秒返回。

7. **不要直接复制 Everywhere/Cherry Studio 代码**。许可证风险（BSL/AGPL）。clean-room 实现，只学架构思路。详见 `07-key-decisions/`。

8. **注入事件过滤的事实纠正**：模拟的 Ctrl+C 是键盘输入，**不会**重进入 WH_MOUSE_LL（那是鼠标钩子）。v3 方案里这块写错了，v4 已纠正。常量是 `LLMHF_INJECTED` 不是 `LLMH_INJECTED`。

---

## 环境信息

- **OS**: Windows 11 (win32 10.0.26200)
- **.NET SDK**: 10.0.302（.NET 10 LTS，支持到 2028-11）
- **IDE**: 通过 CLI / ZCode agent
- **VS Build Tools 2022**: 已装 C++ 桌面开发工作负载（NativeAOT 所需，MSVC 14.44.35207）
- **Avalonia 模板**: 已 `dotnet new install Avalonia.Templates`
- **Shell**: Git Bash（Windows 上用 `//FI` 而非 `/FI` 传 tasklist 参数；`cd` 在复合命令里会触发权限提示，用绝对路径）

---

## 项目位置

| 路径 | 说明 |
|---|---|
| `C:\dvr\gh-kb\selection-assistant\` | **正式项目根**（本交接文件夹在此） |
| `C:\dvr\gh-kb\selection-assistant\src\` | 多项目解决方案源码 |
| `C:\dvr\gh-kb\selection-assistant\docs\` | 文档（Phase0 报告 + Phase1 任务） |
| `C:\dvr\gh-kb\selection-assistant\handoff\` | **本交接文件夹** |
| `C:\Users\DeRant Vilmon Ram\phase0\SelectionSpike\` | Phase 0 spike 项目（验证用，保留参考） |
| `C:\dvr\gh-kb\Selection-Assistant-Plan-v4.md` | v4 主方案原件（副本在 `03-master-plan/`） |

---

## 给接手 Agent 的工作建议

1. **先读 `07-key-decisions/KEY-DECISIONS-AND-TRAPS.md`**——这里浓缩了所有"如果不知道就会犯的错"。
2. **从 P1.1 开始**（迁移 spike 钩子代码），这是让正式骨架跑起来的第一步。spike 代码已经过验证，直接迁移 + 抽接口即可。
3. **不要重新设计架构**——v4 方案经过 3 轮外部评审，所有架构决策都有依据。如果对某个决策有疑问，先查 `04-external-reviews/` 看评审怎么说。
4. **每完成一个任务，更新 `06-project-skeleton/Phase1-Tasks.md` 的状态**，保持任务文件可追溯。
5. **用户偏好不打断式工作**——不要每做一步就问，把信息落地成文件自己推进，有重大决策点再问用户。
