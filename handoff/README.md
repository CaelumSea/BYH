# 选词助手项目 — 交接文件夹

> 本文件夹包含项目的**全部上下文**，供其他 Agent 接续开发。零基础读完这里就能继续工作。

> **2026-07-16 最新入口**：请先读 `00-CURRENT-HANDOFF.md`。它记录了 P1.0–P1.6、翻译切片、托盘/设置/退出、60/60 测试、NativeAOT 发布证据、已知限制和 P1.7 的精确起点；其内容优先于下方历史说明。

---

## 📂 文件夹结构

```
handoff/
├── README.md                          ← 本文件,全景索引
├── 01-start-here/                     ← 【先读这里】
│   ├── HANDOFF-GUIDE.md               ← 交接总指引(项目是什么/做到哪/下一步)
│   └── USER-REQUIREMENTS.md           ← 用户原话需求与约束
├── 02-phase0-results/                 ← Phase 0 验证结果(三道门+NativeAOT)
│   └── Phase0-Validation-Report.md
├── 03-master-plan/                    ← v4 实施基线方案(完整技术规格)
│   └── Selection-Assistant-Plan-v4.md
├── 04-external-reviews/               ← 三轮外部评审(理解为何这么设计)
│   ├── REVIEWS-INDEX.md              ← 评审索引与核心贡献
│   ├── review-1-license-scope-dotnet.md
│   ├── review-2-debounce-hook-clipboard.md
│   └── review-3-concurrency-uia-policy.md
├── 05-spike-reference/                ← Phase 0 已验证的 spike 代码
│   ├── SPIKE-README.md               ← 迁移说明
│   ├── LowLevelMouseHook.cs          ← 钩子(迁移到 Platform.Windows)
│   ├── MainWindow.axaml(.cs)         ← 窗口(迁移到 UI/Views)
│   ├── Program.cs / App.axaml(.cs)   ← 入口(迁移到 App)
│   └── SpikeLog.cs                   ← 日志(迁移到 Infrastructure)
├── 06-project-skeleton/               ← 正式项目骨架现状
│   ├── SKELETON-STATUS.md            ← 各项目当前状态
│   └── Phase1-Tasks.md               ← Phase 1 文件级任务拆解
└── 07-key-decisions/                  ← 关键决策与必须避开的坑
    └── KEY-DECISIONS-AND-TRAPS.md    ← ★浓缩了所有经验
```

---

## 🎯 快速上手(3 步)

**第 1 步**：读 `00-CURRENT-HANDOFF.md` —— 获取最新状态、验证证据和下一执行点。

**第 2 步**：读 `07-key-decisions/KEY-DECISIONS-AND-TRAPS.md` —— 避开所有已知的坑(违反必出 bug)。

**第 3 步**：看 `06-project-skeleton/Phase1-Tasks.md` 顶部 —— 以最新状态和“下一执行点”为准。

---

## 📊 项目一句话总结

做一个 **Windows + macOS 双平台轻量选词助手**：选中文字 → 不抢焦点弹工具栏 → 点翻译/解释/总结/自定义 → 发 LLM → 流式看结果。技术栈 **C#/.NET 10/Avalonia + Win32 互操作**。

## ✅ 已完成

- Phase 0 验证(三道硬关卡全通过 + NativeAOT 18.46MB 可行)
- P1.0–P1.6：正式骨架、钩子、手势/会话、UIA、剪贴板安全回退、完整进程策略链
- v0.1 翻译测试切片：无需密钥 Provider、结果窗口、复制/重试/关闭
- NativeAOT 发布与自动化测试 60/60
- 最小托盘、设置页、配置/日志目录入口和正常退出；最新自动化测试 60/60

## ⏳ 接下来

完成 P1.7 DPI/多显示器定位，再执行 P1.8 应用语料库验收。详见 `00-CURRENT-HANDOFF.md` 和 `06-project-skeleton/Phase1-Tasks.md`。

## 🔑 命脉规则

**WS_EX_NOACTIVATE 不抢焦点**(已验证) + **钩子线程不碰 UI**(踩过坑) + **clean-room 不抄 BSL/AGPL 代码**(许可证红线)。

---

## 环境就绪状态

| 组件 | 状态 |
|---|---|
| .NET 10 SDK (10.0.302) | ✅ 已装 |
| Avalonia.Templates | ✅ 已装 |
| VS Build Tools 2022 + C++ 工作负载 | ✅ 已装(NativeAOT 所需) |
| spike 项目 | ✅ 保留在 `C:\Users\DeRant Vilmon Ram\phase0\SelectionSpike\` |
| 参考项目(Everywhere/Cherry Studio) | ✅ 在 `C:\dvr\gh-kb\sources\` |

**开箱即用,无需再装任何东西即可开始 Phase 1 编码。**
