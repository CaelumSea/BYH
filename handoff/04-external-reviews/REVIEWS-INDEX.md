# 外部评审索引

> 三轮外部评审逐次纠正了方案的技术错误和架构偏差。v4 主方案是吸收了这三轮所有意见后的版本。理解这些评审能知道"为什么这么设计"。

---

## 评审 1：`review-1-license-scope-dotnet.md`（方案 v1 → v2）

**核心贡献**（否决性的）：

1. **产品定位纠偏**：v1 把产品降级成了"DeepL 翻译演示版"，丢失了"可自定义能力、可配置模型接口"的核心需求。应升级为**文本动作引擎（Text Action Engine）**，翻译只是默认动作。
2. **许可证红线**：Everywhere 是 BSL 1.1，Cherry Studio 是 AGPL-3.0，**不能直接复制代码**。必须 clean-room 实现。
3. **.NET 版本过时**：v1 选 .NET 8，但它在 2026-11 EOL。必须用 **.NET 10 LTS**。
4. **NativeAOT 不能前置承诺**："WPF + NativeAOT ≤20MB"是未验证结论，AOT 有诸多限制（不支持动态加载、反射、COM）。应作为 Phase 0 实验项。
5. **推荐方案 B**：.NET 10 + Avalonia + Windows 专用 Interop 层（而非 WPF）。
6. **工期重估**：7-11 天只够翻译演示版，完整 MVP 需 15-24 工作日。

**落地结果**：方案改成 Avalonia + .NET 10，引入 Text Action Engine 概念，NativeAOT 列为 Phase 0 实验。

---

## 评审 2：`review-2-debounce-hook-clipboard.md`（方案 v2 → v3）

**核心贡献**（4 个实现阻塞项）：

1. **500ms 防抖与 150ms 目标矛盾**：不能既等 500ms 又要 P95 150ms。改用**选词会话（selection session）**——立即开始取词，防抖并发，新点击取代旧会话。用系统指标而非硬编码。
2. **Windows 钩子要求纠正**：钩子线程不需要 STA，不需要 Highest 优先级（会负面影响桌面），超时不是固定 300ms（注册表配置，上限 1000ms）。
3. **"完整剪贴板备份"是假的**：Windows 剪贴板有私有格式、延迟渲染格式，无法全保。改为**最佳努力保留**。用 `AddClipboardFormatListener` 而非 5ms 轮询。
4. **不抢焦点要用原生行为**：用 `WS_EX_NOACTIVATE` + `SW_SHOWNOACTIVATE` + `SWP_NOACTIVATE`，不是 `SetForegroundWindow` hack（那是抢焦点的反义词）。列为 **Phase 0 硬关卡**。

**产品补充**：内置 Translate + Explain + Summarize + Custom（不只是 Translate）。ActionProfile 加 schemaVersion/inputLimit/confirmBeforeSend。

**落地结果**：v3 引入选词会话、最佳努力剪贴板、不抢焦点硬关卡、四动作内置。

---

## 评审 3：`review-3-concurrency-uia-policy.md`（方案 v3 → v4，实施基线）

**核心贡献**（7 个必改项，2 个编码阻塞项）：

1. **取词仍未立即启动**（编码阻塞）：v3 伪代码还是先延迟再取词。必须取词第一行启动，防抖并发。且 UI 调用不能在 `Task.Run` 里（Avalonia 单线程）。
2. **注入事件解释技术错误**（编码阻塞）：模拟 Ctrl+C 是键盘输入，**不会**重进入 WH_MOUSE_LL（鼠标钩子）。常量是 `LLMHF_INJECTED` 不是 `LLMH_INJECTED`。不要全局丢弃注入事件。
3. **几何判定轴式**：矩形指标不能用欧氏距离。双击判定要含同窗口/同进程/同按键。删 DRAG_MAX_MS。线程优先级 Normal。
4. **进程策略改为可组合 record**：enum 互斥，PDF 阅读器需要 CopyAllowed + DelayedClipboardRead 两者。
5. **剪贴板状态机时序修正**：OpenClipboard 重试放在所有访问周围；恢复前取消订阅/标记 Restoring；完整和弦一个 SendInput 数组；注入前检查修饰键；macOS KVO 不承诺。
6. **UIA 超时诚实表述**：CancellationToken 不真正取消 COM 调用。专用单线程 worker，调用方超时，worker 隔离。
7. **macOS 运行时策略**：事件 tap 恢复（检测禁用/重启用/健康看门狗）；分发模型（App Store 外公证）；稳定身份（bundle id + Developer ID 签名 + 升级测试）；Retina 用 points 非 px。

**额外**：Provider 加 secret 引用头、URL 组合用 URI-aware、默认禁重定向、AOT 用 source-gen JSON、所有配置加 schemaVersion；验收标准量化（取词≥95%、零错误返回、零剪贴板覆盖）。

**落地结果**：v4 = 实施基线。所有伪代码已按此修订。

---

## 评审与 v4 章节对应

| 评审意见 | v4 落地位置 |
|---|---|
| Text Action Engine | §3 产品定义 |
| .NET 10 + Avalonia | §2 技术栈 |
| clean-room | §11 许可证 |
| 选词会话(并发) | §5.1 |
| 轴式几何 | §5.2 |
| 钩子要求 | §5.3 |
| 注入事件纠正 | §5.4 |
| 四级取词链 | §6.1 |
| UIA 超时模型 | §6.2 |
| 最佳努力剪贴板 | §6.4 |
| 可组合进程策略 | §6.6 |
| 不抢焦点双窗口 | §7 |
| Provider 系统 | §9 |
| 量化验收 | §12 |
| 分阶段时间线 | §13.3 |
