# 07 · 构建 / 发布 / 运行

> **改构建配置/发布/启动/探针前先读本文件。**

---

## 构建

```powershell
Set-Location 'C:\dvr\gh-kb\MyProjects\BYH'
dotnet test SelectionAssistant.slnx -c Release --nologo   # 当前 762/762 通过
```

## NativeAOT 发布

```powershell
# 杀掉运行中的实例（否则 exe 被锁）
Get-Process -Name 'BYH' -ErrorAction SilentlyContinue | Stop-Process -Force

# 直接发布到标准产物路径
dotnet publish src\SelectionAssistant.App\SelectionAssistant.App.csproj `
  -c Release -r win-x64 --nologo -o artifacts\publish\win-x64-nativeuia
```

**产物**：`artifacts\publish\win-x64-nativeuia\`
- `BYH.exe`（NativeAOT，无 PDB；R36 起 exe 名跟随 `<AssemblyName>BYH</AssemblyName>`，任务管理器进程名亦为 `BYH`）
- `av_libglesv2.dll`、`libHarfBuzzSharp.dll`、`libSkiaSharp.dll`

**要求**：0 AOT/裁剪警告（TrimMode=full）。

## 启动入口

| 方式 | 路径 |
|---|---|
| 桌面快捷方式 | `桌面\BYH.lnk`（`create-launchers.ps1` 生成） |
| 项目根脚本 | `BYH.cmd`（双击运行） |
| 直接 exe | `artifacts\publish\win-x64-nativeuia\BYH.exe` |
| 托盘重启 | 托盘右键 → "重启 BYH"（`RequestRestart`：spawn 新进程 + 退出旧的） |

**单实例锁**：`Program.Main` 命名 Mutex；第二个实例静默退出。

## 探针命令（CLI 工具，跳过单实例锁）

```
--open-settings                    启动后打开设置
--probe-uia                        UIA 可用性
--probe-bounds <x> <y>             UIA 元素边界
--probe-uia-region <x> <y> <w> <h> UIA 框内文本扫描
--probe-policy                     进程策略
--probe-process-policy <pid>       指定进程的捕获策略
--probe-translation                MyMemory 翻译
--probe-translate-speed [text]     真实联网测速（需配置 Provider + 密钥）
--probe-vision [x y w h]           R24 视觉 OCR 端到端（截图→OCR，需 vision.json + Provider + 密钥）
--probe-ocr-raw <x> <y> <w> <h>    输出 OCR 原始响应（模型诊断）
--probe-save-region <x> <y> <w> <h> 保存选区截图探针
--probe-ocean-eyes-save ...        Ocean Eyes 保存/剪贴板探针
--probe-ocean-eyes-history ...     Ocean Eyes 历史记录探针
--probe-clipboard <hwnd> <pid> <text>   剪贴板回退
--probe-capture <hwnd> <pid> <text>      取词
--probe-icon-extract ...           启动器图标提取探针
--probe-launcher-list              列出启动器条目
--probe-launcher-run ...           启动器执行探针
--set-secret <reference> <value>         写 DPAPI 密钥（不回显）
```

**R24 视觉 OCR 探针**（`--probe-vision`）：不经过选词会话，直接截屏幕区域 → PNG → 调 `vision.json` 配的 OCR 模型 → 打印识别文字 + 耗时。是验证轨道 B① 新代码（截图编码 + 多模态 OCR）的唯一 CLI 手段。退出码：0=有文字，2=空，3=配置/网络错。
- 用法：`--probe-vision`（默认截屏幕中心 300×150）或 `--probe-vision 100 100 50 50`
- 前提：`providers.json` 有对应 Provider 条目、`vision.json` enabled=true、密钥已 `--set-secret`
- 示例配置见 `docs/vision.example.json`、`docs/providers.example.json`

## create-launchers.ps1

项目根的 `create-launchers.ps1` 重新生成桌面快捷方式 + BYH.cmd（幂等，覆盖旧）。重新发布后路径不变则无需重跑。

## 启动流程（Program.Main → App）

```
Program.Main(args)
  ├── --probe-* / --set-secret 分支 → 执行后 return（不进单实例锁）
  ├── 命名 Mutex 单实例锁（第二实例退出）
  └── BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)
        → App.OnFrameworkInitializationCompleted
          ├── 创建七窗口 + TrayIcon
          ├── settingsWindow 事件接线（Provider/PromptTemplate/QuickTools 快捷键 CRUD）
          ├── quickToolsWindow 事件接线（Action/ManagePrompts）
          ├── toolbarWindow.Opened → new SelectionRuntime().Start()
          │     ├── 鼠标钩子 Start（原生线程）
          │     ├── 读取 ocean-eyes.json（兼容旧 quick-tools.json 迁移）；默认 Ctrl+Alt+Q + chord 关闭
          │     ├── RegisterHotKey 专用线程 → Dispatcher → QuickTools.ShowAt
          │     ├── 可选 _chordDetector.ChordDetected → ChordTriggered
          │     └── toolbarWindow.PasteRequested → OnPasteRequested（Ctrl+V）
          └── 快捷键/chord 共用 QuickTools.ShowAt 入口
```

## 关键文件

| 文件 | 职责 |
|---|---|
| `App/Program.cs` | 入口；探针分支；单实例 Mutex；启动 Avalonia |
| `App/App.axaml.cs` | 七窗口 + TrayIcon；事件接线；重启；退出 |
| `App/SelectionRuntime.cs` | 组合根；钩子/会话/Provider/PromptTemplate 生命周期 |
| `App/SelectionAssistant.App.csproj` | NativeAOT 配置；AvaloniaResource（图标） |
| `App/App.axaml` | FluentTheme + 跨程序集 Ivory Jade StyleInclude |
| `UI/Themes/IvoryJade.axaml` | 主题资源；必须由 NativeAOT 正确嵌入 |
| `create-launchers.ps1` | 生成桌面快捷方式 + BYH.cmd |

## 改动检查清单

- [ ] 改构建：保持 0 警告；TrimMode=full。
- [ ] 改发布：杀旧进程再 publish；复制到标准路径。
- [ ] 改启动：保持单实例锁；探针分支在锁之前 return。
- [ ] 改图标：替换 Assets/app-icon.png + .ico；csproj AvaloniaResource；重发布。
