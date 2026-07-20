# TASK-001 验证报告

日期：2026-07-18

## 交付结果

- QuickTools 默认全局快捷键为 `Ctrl+Alt+Q`。
- 设置页可启停键盘快捷键，并组合 `Ctrl`、`Alt`、`Shift`、`Win` 与 `A-Z`、`0-9`、`F1-F12`、`Space`。
- 配置持久化到 `%LOCALAPPDATA%\BYH\quick-tools.json`，保存后立即重注册。
- 新快捷键注册冲突或配置保存失败时，新注册会回滚，旧快捷键继续有效并显示错误。
- 左右键 chord 默认关闭，设置页仍可选择开启兼容方式。
- 全新配置的 OCR 默认值已与 R24 最终方案一致：`Qwen/Qwen3.5-4B`、`disableThinking=true`、UIA 预填关闭。

## 自动验证

- `dotnet test SelectionAssistant.slnx -c Release --nologo`
  - Core：86/86
  - Providers：35/35
  - Windows：41/41
  - 总计：162/162
- `dotnet publish ... -c Release -r win-x64 -o artifacts/publish/win-x64-nativeuia`
  - NativeAOT 发布成功，0 个 AOT/裁剪警告。
  - `SelectionAssistant.App.exe`：26,409,984 bytes。
- Windows 桌面实测：
  - 设置页可见 QuickTools 快捷键区与默认 `Ctrl+Alt+Q`。
  - `Ctrl+Alt+Q` 可从发布版打开 QuickTools。
  - 改为 `Ctrl+Alt+Shift+Q` 后，旧组合不再打开，新组合可打开；随后恢复默认。
  - 默认左右键 chord 不会打开 QuickTools。
- Windows 集成测试会真实注册同一组热键两次，确认第二次返回冲突；释放第一组后可重新注册。

## 关键文件

- `src/SelectionAssistant.Core/Input/QuickToolsTriggerSettings.cs`
- `src/SelectionAssistant.Infrastructure/Configuration/QuickToolsTriggerStore.cs`
- `src/SelectionAssistant.Platform.Windows/Input/WindowsGlobalHotKey.cs`
- `src/SelectionAssistant.App/App.axaml.cs`
- `src/SelectionAssistant.App/SelectionRuntime.cs`
- `src/SelectionAssistant.UI/Views/SettingsWindow.axaml`
- `tests/SelectionAssistant.Core.Tests/Input/QuickToolsTriggerSettingsTests.cs`
- `tests/SelectionAssistant.Core.Tests/Configuration/QuickToolsTriggerStoreTests.cs`
- `tests/SelectionAssistant.Windows.IntegrationTests/Input/WindowsGlobalHotKeyTests.cs`
