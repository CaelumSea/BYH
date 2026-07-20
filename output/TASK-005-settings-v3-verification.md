# TASK-005 · 设置页高保真重构验证

日期：2026-07-18

## 结果

- 默认窗口：1000×720 logical；最小窗口：860×600 logical。
- 固定侧栏：常规、翻译服务、自定义功能、视觉识别。
- 右侧仅当前分区滚动；页面标题和底部隐藏/退出操作固定可见。
- Provider 选择与编辑合并；OCR Provider/模型改为两列，不再使用易溢出的四列布局。
- 视觉材料：奶油渐变、细金框、暖金活动导航、珠光花丝、玉石徽记。
- 玉石与花丝资产由内置 imagegen 根据用户参考图生成，项目运行时仅打包压缩 JPG。

## 自动验证

- `dotnet test SelectionAssistant.slnx -c Release --nologo`
  - Core 86/86
  - Providers 35/35
  - Windows 41/41
  - 合计 162/162
- NativeAOT 发布成功，0 AOT/trim warnings。
- 最终 exe：`artifacts/publish/win-x64-nativeuia/SelectionAssistant.App.exe`，26,573,312 bytes。
- 最终发布版已启动：PID 54668，`BYH · Settings`，Responding=True。

## 视觉证据

- `artifacts/qa/ivory-jade-settings-v3.png`
- `artifacts/qa/ivory-jade-settings-v3-provider.png`
- `artifacts/qa/ivory-jade-settings-v3-minimum-provider.png`

三张图均在 175% DPI（168 DPI）环境取得；最小尺寸外框实测 1529×1114 physical px，与 860×600 logical + 非客户区一致。没有发现横向重叠，滚动区与固定底栏保持分离。
