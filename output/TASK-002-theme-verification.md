# TASK-002 · Ivory Jade 验证记录

日期：2026-07-18

## 结果

- View 层旧十六进制颜色：0。
- C# `Brushes.LightCoral` / `Brushes.LightGreen`：0。
- Debug 编译：0 warnings / 0 errors。
- Release 自动测试：162/162（Core 86 + Providers 35 + Windows 41）。
- win-x64 NativeAOT：成功，0 AOT/裁剪警告。
- 最终 exe：`artifacts/publish/win-x64-nativeuia/SelectionAssistant.App.exe`，26,460,160 bytes。
- 发布版启动：Settings 可见；向热键线程发送等价 `WM_HOTKEY` 后 QuickTools 可见。

## 视觉检查

| 界面 | 结果 | 证据 |
|---|---|---|
| Settings | 象牙背景、卡片层级、玉色开关和输入焦点、焦糖文字均正确 | `artifacts/qa/ivory-jade-settings.png` |
| QuickTools | 第二轮截图无底部重叠；功能列表降为 Ghost，OCR/Prompt Run 保持玉色焦点 | `artifacts/qa/ivory-jade-quick-tools.png` |
| Region overlay | 暖棕遮罩和提示层可读，选区/手柄由语义资源控制 | `artifacts/qa/ivory-jade-region-overlay.png` |

## 执行中修复

1. `TextBox.BoxShadow` 在 Avalonia 12.1 不存在：焦点态改用 2px primary 边框。
2. MIMO 批量迁移超时且留下两个未闭合 AXAML 标签：主 Agent 审查后修复，最终编译通过。
3. 175% DPI 首轮 QuickTools 截图发现底部控件重叠：高度 400→480，指令区固定 72px，移除空行并降低功能按钮视觉重量。
4. MIMO 最终复核指出语义按钮可能覆盖通用 focus setter：已新增 `Primary/Danger/DangerQuiet/Ghost:focus` 高特异性样式，保证键盘焦点可见。
