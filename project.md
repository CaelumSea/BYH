# 项目：BYH（adopt）

## 愿景与目标
（adopt：见 AGENTS.md）

## 目标用户
（adopt：见 AGENTS.md / handoff）

## 硬约束
（adopt：见 AGENTS.md / 顶层文档）

## 术语表
（adopt：见项目内文档）

## 关键历史决策
（adopt：见 handoff / 关键历史决策段）
- 2026-07-18 采用 Ivory Jade 作为 BYH 首个正式主题：象牙白为主体，玉色只用于真实操作，古金仅作小面积细节。

## 当前焦点需求
REQ-012：依据用户提供的局部参考图复现金属质感双圆角框，并在独立 Git 任务分支完成实现与验证。

## 任务历史
- [[REQ-012]] 设置页金属质感双圆角框 — done (2026-07-20)
- [[REQ-009]] 设置页框架比例与双层边框精修 — done (2026-07-20)
- [[REQ-008]] Settings UI English and dimensional frame refinement — done (2026-07-20)
- [[REQ-007]] 设置页导航上移与底部共享窗格 — done (2026-07-20)
- [[REQ-006]] 设置页柔和边界、宝石裁切与侧栏比例修正 — done (2026-07-18)
- [[REQ-005]] 设置页 Ivory Jade 视觉精修与 APP icon 头像 — done (2026-07-18)
- [[REQ-004]] 设置页参考图同构多窗格布局 — done (2026-07-18)
- [[REQ-003]] 重构设置页信息架构与默认窗口尺寸 — done (2026-07-18)
- [[REQ-002]] Ivory Jade 主题系统与全界面落地 — done (2026-07-18)
- [[REQ-001]] 自定义全局快捷键触发 QuickTools — done (2026-07-18)
（adopt：见项目内原有任务/交接文档）

## 执行基调
启用模块：dispatch, execution, memory, requirement, review

执行引擎：主 agent 单干（execution）—— 自己执行每个 TASK，边干边记 Execution Log，走证据三件套 + done 门。最快最省。

增强模块：
- review：每个 TASK done 前走客观审美自检（self-check），客观自修、主观攒进 notes，done 后汇总给人定夺
- memory：跨项目记忆 + 审美偏好进化，evolve 时触发记忆钩子，避免重复工作

## 任务进展流程
项目按 phase 推进链开展（机器强制，index.yaml.phase 实时记录）：

```
initialized → drafting → confirmed → dispatched → routed → executing → done
  建骨架      挖需求      AC确认      拆任务      选模块      干活      完成
```

当前阶段与下一步见 `index.yaml` 的 `phase` / `next_step` 字段。 每条改状态的命令会先校验 phase 是否允许，跳步会被 exit 1 拦下。

## 文件使用导引
| 场景 | 读/写哪个文件 |
|---|---|
| 看项目整体（做什么/为谁/基调） | 本文件 project.md |
| 看当前状态/下一步/全局索引 | index.yaml |
| 看执行编排（启用了哪些模块） | enabled-modules.yaml |
| 看某需求的目标/验收/叙事 | requirements/REQ-###.md |
| 看某需求的 AC/任务镜像/状态 | requirements/REQ-###.yaml |
| 看某任务的执行细节/log | tasks/TASK-###.yaml |
| 看历史审美偏好（挖掘时提醒） | preferences.yaml |
| 放产出物（代码/文档/资源） | output/ |

**格式原则**：md 是人的领地（叙事、审美、说明书），yaml 是 agent 的领地（状态、追溯、开关）。

## 执行要点
- TASK 是执行的唯一合法载体——没有 `in_progress` 的 TASK，不许执行任何实现动作。
- set in_progress 前 phase 必须 ≥ routed；set done 前 execution_log 必须非空。
- ★ review 启用：每个 TASK done 前调 `self-check`，对照审美模板查客观维度，绝不暂停问人。
- ★ memory 启用：执行中发现可复用的经验/踩坑，走 evolve 触发记忆钩子，沉淀进全局记忆。
- 执行中发现需求/参数不对 → 走 evolve（flag-back），不硬干。
