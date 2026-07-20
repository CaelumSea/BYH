# BYH 路线图待办（Backlog）

> 来源：用户 2026-07-17 提出的功能清单 + 第 1-14 批增量；2026-07-19 加入 R44-R53（小旺 inspired Ocean Eyes 扩展）。
> 主交接快照见 `00-CURRENT-HANDOFF.md`。
> 模块文档见 `docs/architecture/00-architecture-overview.md`。
> **更新 2026-07-20 第四十三批（v2 终态）：撤销 R45 二维码（用户判定"不太用得上"，revert 0623e4c）；R52 磁力吸 + R48 标注工具集 落地。R48 走了两轮：v1（merge 9092d37）用户测试发现 3 个严重 bug（无拖拽实时预览 / pen-highlight 路径记录失效 / arrow 撤销计数错）→ revert 79a39a0 → v2（merge 702788c）重做，扩 IMouseHook 加 MouseMove 事件根本解决路径记录 + 加 live preview + 修 Arrow Tag(2)，reviewer 补 20 个回归测试覆盖 3 个 v1 失败模式。R52 也修了 GetWorkAreas 的 DPI 缩放 bug（WorkingArea 已是物理像素，不应再 ×RenderScaling，commit 633f066）。最终 main：316/316 测试通过，NativeAOT 0 警告，exe = 27,782,144 字节。**

---

## ✅ R26 Ivory Jade 主题（已完成，2026-07-18）

- 统一语义资源覆盖七个窗口；Light Fluent 控件统一使用 jade accent。
- View 层旧硬编码色为 0；运行时反馈改为 class 切换。
- 175% DPI Settings/QuickTools 视觉检查通过；QuickTools 重叠已修复。
- Release 测试 162/162；NativeAOT 0 警告；exe 26,460,160 bytes。
- 主题规范：`docs/architecture/08-theme-system.md`。

## ✅ R27 设置页信息架构与高保真 Ivory Jade（已完成，2026-07-18）

- 默认 560×640 单列长页改为 1000×720（最小 860×600）固定侧栏布局。
- 常规、翻译服务、自定义功能、视觉识别四个独立分区；右侧独立滚动，底栏固定。
- 参考用户效果图加入玉石徽记、珠光花丝、奶油渐变、暖金活动导航与细金框；不引入人物角色。
- 175% DPI 下验证默认尺寸和最小尺寸；Provider/OCR 表单无横向重叠。
- 视觉证据：`artifacts/qa/ivory-jade-settings-v3*.png`。

---

## ✅ R25 QuickTools 可配置全局键盘快捷键（已完成，2026-07-18）

### 功能描述
QuickTools 面板可通过全局键盘快捷键打开，不再依赖左右键同按（chord）作为唯一入口。

### 落地内容
- **默认快捷键**：`Ctrl+Alt+Q` 打开 QuickTools 面板。
- **设置页可配**：快捷键卡片含 ToggleSwitch 启停 + 修饰键（Ctrl/Alt/Shift/Win 复选框）+ 主键（A-Z/0-9/F1-F12/Space 下拉）。
- **冲突安全**：快捷键注册失败（被其他程序占用）或保存出错时，旧快捷键继续有效并显示错误提示。
- **左右键 chord**：因与源应用右键菜单冲突，默认关闭，可在设置页"兼容性"区域手动开启。

### 桌面实测证据（2026-07-18）
- 设置页显示快捷键配置区
- 默认 Ctrl+Alt+Q 打开 QuickTools
- 改成 Ctrl+Alt+Shift+Q 后旧组合失效、新组合生效
- 恢复 Ctrl+Alt+Q 正常
- 左右键 chord 默认不打开面板

### 测试
自动测试 162/162（Core 86 + Providers 35 + Windows 41），含 R25 新增测试。

---

## ✅ R24 视觉取词（已完成，2026-07-18 用户真机确认）

轨道 A（UIA 强化）+ 轨道 B①（云端 OCR 两阶段兜底）均已落地。**测试 162/162 通过，NativeAOT 0 警告，exe 26,409,984 bytes。**

### 落地内容
- **轨道 A**（`WindowsUiAutomationBackend.cs`）：候选根 2→3（命中测试 + 焦点 + 桌面根 `GetRootElement`），祖先链 5→8 层，选区读不到时读元素全文（TextPattern `DocumentRange` → ValuePattern `CurrentValue`，上限 4000 字）。
- **轨道 B① 两阶段**（不是串行挂死）：
  - phase 1（`CaptureAsync`）：UIA→剪贴板，有文本立即出工具条（<100ms，无 OCR 延迟）。
  - phase 2（`CaptureVisionAsync`）：phase 1 空 + `VisionTierAvailable` 才触发，显示"识别中…"工具条 → 截图 → 云端 OCR，**独立 5s 超时**，失败/空→隐藏工具条。
- **默认 OCR 模型**：`Qwen/Qwen3.5-4B`，`disableThinking=true`，UIA 预填默认 false。新安装即此配置。
- **画框模式**（第十二批最终版，R25 更新入口）：从划词自动 OCR 改为显式触发——全局快捷键（默认 Ctrl+Alt+Q；chord 可选）→ QuickTools → 📐 画框识别文字 → 全屏遮罩画框 → 确认 → OCR → 文字进剪贴板 + 弹回 QuickTools。
- 截图：Win32 `BitBlt` + 手写 PNG 编码（AOT 安全，无 System.Drawing）。截图区域用 UIA 元素包围盒（省延迟+隐私）。
- 所有 COM vtable 槽位从 `UIAutomationClient.h` 逐个数出（见 `docs/architecture/01-selection-capture.md`）。

### 落地中修正的设计问题（教训，永久记录）
- 初版把 Vision 塞进 `CaptureAsync` 主链路串行 await → 干等 1-3s 无反馈。改成两阶段。
- 初版自动截图 → 用户控不了区域。改成显式画框。
- DeepSeek-OCR 严重幻觉 → 换 Qwen3.5-4B（关思考，<1s，干净准确）。
- 详见 `00-CURRENT-HANDOFF.md` §3b 和 `01-selection-capture.md`。

### 调研依据（2026-07 OCR 选型，留存备查）

**关键事实（用户 2026-07-17 在 SiliconFlow 平台实时确认）**：
- `deepseek-ai/DeepSeek-OCR` —— **当前限免**（落地时为默认，后因严重幻觉弃用）
- `PaddlePaddle/PaddleOCR-VL-1.5` —— **当前免费**（设置页备选）
- 两者都是 OCR 专项模型，对划词场景比通用视觉模型（Qwen/Kimi）更对口。
- 通用视觉模型（Qwen3.5-27B / Kimi K2.6）作为"高精度备选"保留——手写等场景可切，但按量付费。
- **GPT 系列明确排除**：2026 实测中文 OCR 集体翻车（gpt-5.4-nano 手写 0%），且贵 10~144 倍。
- **最终选用 `Qwen/Qwen3.5-4B`**（关思考，<1s，干净准确，免费）。

**轨道 B②/B③（未落地，留 backlog）**：PaddleOCR-VL 本地（OnnxRuntime，需 AOT 验证）/ WinRT OCR（CsWinRT 投影，性价比最低）。

---

## ⏸️ 暂缓：R23 快捷命令（一键打开软件/网页）

用户 2026-07-18 明确暂缓，不再作为当前下一步。

**待设计澄清（开工前先和用户确认）**：
- 触发方式：放在哪个窗口？QuickTools 面板？划词工具条？托盘菜单？独立命令面板？
- 命令类型：目前明确两类——打开软件（启动本机程序）/ 打开网页（URL，默认浏览器）
- 是否需要更多类型（运行 CLI 命令、打开文件、系统操作如锁屏/截屏）？
- 数据模型参考：类似 R15 自定义功能——`List<QuickCommand>` + 持久化 `quick-commands.json` + 设置页增删改。
- 复用 R15 的模式：public top-level 行 ViewModel + ItemsControl DataTemplate + RelayCommand（NativeAOT 安全）。

**这是 R3 "快捷触发脚本"子项的落地**——用户最初提到"快捷触发脚本"，现在明确为"快捷命令：打开软件/网页"。

---

## 已完成（R1–R30）

| # | 功能 | 状态 |
|---|---|---|
| R1 | 自定义提示词 + 思考开关 | ✅ |
| R2 | Prompt Now 弹窗 | ✅ |
| R3 | 左右键同按触发快捷工具 | ✅（"脚本"子项待明确） |
| R4 | app 图标（透明背景） | ✅ |
| R5 | 复制/粘贴/剪切右键菜单 | ✅ |
| R6 | 全局提示词预设系统 | ✅ |
| R7 | 设置页 Provider 下拉精简 | ✅ |
| R8 | 托盘图标透明背景修复 | ✅ |
| R9 | 思考模式迁移到提示词级 + 设置紧凑化 | ✅ |
| R10 | QuickTools 复制/粘贴 + 管理提示词跳转 | ✅ |
| R11 | chord 选词剪贴板兜底 | ✅ |
| R12 | 托盘重启 BYH | ✅ |
| R13 | 图标放大 96% 填充 | ✅ |
| R14 | 划词工具条复制/粘贴/解释总结接通/折叠展开 | ✅ |
| R15 | 自定义功能系统重构（增删任意功能） | ✅ |
| R16 | 单实例锁 | ✅ |
| R17 | 启动入口（快捷方式 + BYH.cmd） | ✅ |
| R18 | chord 定位/触发/时间窗口修复 | ✅ |
| R19 | 项目文档体系 docs/architecture/ | ✅ |
| R20 | 误触发修复 | ✅ 根因=ManualFallback 后门，已删除（用户确认） |
| R21 | 布局均衡修复 | ✅ Auto×6,*,Auto + StatusText MinWidth（用户确认） |
| R22 | R20/R21 真机验证 | ✅ 用户确认全部生效 |
| R24 | 视觉取词（画框 OCR，Qwen3.5-4B 关思考） | ✅ 2026-07-18 用户真机确认效果很好 |
| R25 | QuickTools 可配置全局键盘快捷键 | ✅ 默认 Ctrl+Alt+Q，设置页可配，冲突安全 |
| R26 | Ivory Jade 统一主题 | ✅ 七窗口覆盖，高 DPI 视觉检查 + NativeAOT 通过 |
| R27 | 设置页高保真重构 | ✅ 四分区侧栏、1000×720、珠光玉石视觉、175% DPI |
| R28 | 设置页参考图同构多窗格工作台 | ✅ 四列两行、真实 Current setup、1320×800、175% DPI 默认/最小尺寸 |
| R29 | 设置页视觉精修与 APP icon 人物卡 | ✅ 顶部欢迎带、右上真实人物 icon、主题预览、轻量瓷器层级、175% DPI |
| R30 | 设置页局部精修 | ✅ 宝石放大裁切、hairline 柔和边界、右上人物主视觉、左侧三分区等高；follow-up 将导航上移并让底部共享窗格横跨导航与中央区；第二次 follow-up 完成全英文精简、统一线性图标和层叠立体边缘 |

---

## 📋 R44-R53 待办：Ocean Eyes 扩展（小旺 inspired，2026-07-19 制定）

> 来源：对照 `xiaowang.com`（小旺 AI 截图）的功能清单，挑选**实现成本不高、运行开销低、契合 Ocean Eyes 轻量定位**的 10 项。
> 排除项（明确不做）：录屏 MP4 / GIF 动图 / 视频编辑 / AI 抠图（占用 CPU/GPU 重，偏离 Ocean Eyes 定位）。
> **重要原则**：保持 NativeAOT 单文件 ~27MB、0 警告、0 新原生依赖、常驻内存增量 ≈ 0。每项落地都必须符合这条。

### 通用架构原则（所有 10 项遵循）

- **入口位置**：默认挂在 Ocean Eyes 框选 overlay 上的浮动工具栏（已有），新增 toolbar 快捷键或图标按钮。
- **零新原生依赖**：所有图像处理走 Skia（已在依赖中）或 Avalonia 自带能力；二维码识别走 `ZXing.Net`（纯托管 ~200KB，AOT 友好）。
- **零常驻开销**：功能仅在 Ocean Eyes overlay 激活期间生效，关闭 overlay 即释放。
- **保存路径**：默认 `%USERPROFILE%\Pictures\Ocean Eyes\`，复用 R40 既有路径。
- **NativeAOT 安全**：所有新代码必须 0 反射、0 动态类型；优先 record / 静态泛型。

---

### 🎨 P0 高性价比（优先做，代码量小、零额外开销）

#### ✅ R44 — 取色器（color picker）（已完成，2026-07-19 第三十七批）

- **触发**：Ocean Eyes 框选确认后按 **P**（Picker）→ 放大镜弹出，跟随鼠标 ~30Hz 显示中心像素的 HEX/RGB；再次按 **P** 取消，按 **Esc** 关闭（连同 Ocean Eyes）；**左键任意位置确认**（由 mouse hook 路由），把 `#RRGGBB` 复制到剪贴板并在工具栏状态槽显示"已复制 #RRGGBB"。
- **架构**：
  - `ScreenRegionCapture.CaptureRawBgra(x,y,w,h)` + `SamplePixel(x,y)` 新增——抽出原 BitBlt 管线跳过 PNG 编码（30Hz 采样不爆 CPU）。
  - `ColorPickerLoupe.axaml(.cs)`（新）— Avalonia Window + `NoActivateWindowHost`（`WS_EX_NOACTIVATE` 不抢焦），`DispatcherTimer` 33ms tick，15×15 BGRA → 150×150 RGBA WriteableBitmap（每像素 10×10 块放大），中心十字 + 古金色 `#FFD9C28A`。
  - `ColorFormatter.ToHexRgb/ToRgbDecimal`（新）— 纯函数 hex/RGB 格式化（AOT 友好，11 个单测覆盖边界）。
  - `SelectionRuntime` 新增 P 键分支（Enter 后、A-Z filter 前；Ocean Eyes 限定；不走 OCR 路径），新增 `StartColorPicker`/`HideColorPicker`/`SampleCursorRegion`/`OnColorPicked` 私有方法，新增 `GetCursorPos` P/Invoke。
  - mouse hook `OnMouseEvent` 在 `_colorPickerActive` 时短路：左键 down → `ConfirmPick` + 吞（不触发 toolbar dismiss / 新 selection session）。
  - `DismissOceanEyes` / `ResetForRedraw` / `Dispose` 全部调用 `HideColorPicker` 清理。
- **依赖**：0 新增（复用 BitBlt + Avalonia WriteableBitmap + ColorFormatter 纯函数）。
- **资源开销**：常驻 0（loupe 懒构造，未用 P 键永不分配）；运行时 30Hz × 15×15 BitBlt ≈ 可忽略。
- **代码量**：~250 行（ScreenRegionCapture +60 / ColorFormatter +35 / loupe +200 / runtime +60 / 测试 +60）。
- **验收**（第三十七批）：
  - Debug build 0 警告 0 错误。
  - 232/232 测试通过（前 221 + 新增 ColorFormatter 11）。
  - NativeAOT Release publish 0 警告。
  - exe 27,634,688 字节（前 27,610,112，**增量 +24,576 字节 / 24KB**，远低于 100KB 预算）。
  - 双路径同步：`cp` 到 `artifacts/publish/win-x64-nativeuia/BYH.exe`。
  - 机器侧验证：BYH 已启动（PID 43764），等待用户人工复测 P 键交互。

#### ❌ R45 — 二维码识别（QR decode）（**已撤销 2026-07-20 第四十三批**）

> ⚠️ **状态：WITHDRAWN** — 用户 2026-07-20 反馈："二维码识别功能其实不太用得上，如果占用资源多可以不做。" 实测 ZXing.Net 在 NativeAOT 下占 +595KB exe 体积（超 AC-4 的 300KB 目标），用户判定性价比不足。代码已 revert（commit `0623e4c`），REQ-010 标记 withdrawn。下方技术细节作为历史保留，未来若 QR 需求回归可参考（注意"裁掉未用格式或换 QR-only 解码库"的瘦身建议）。

- **触发**（历史）：Ocean Eyes overlay 框选确认后按 **Q**（QR），用 ZXing 解码当前缓存 PNG；成功把内容塞剪贴板并在工具栏状态槽显示"已复制 URL：..."或"已复制：..."；失败显示"未识别到二维码"。**不自动打开浏览器**（用户可能正在敏感应用中工作，自动开浏览器会抢焦点）。
- **代码量**：~280 行（QrDecoder 138 + SelectionRuntime Q 分支 + DecodeQrFromOceanEyes 106 + Win32Clipboard.SetText 45）。
- **依赖**：`ZXing.Net` 0.16.11（micjahn 维护，纯托管，MIT）。
- **架构关键决策（永久记录）**：
  - **不用 `BarcodeReader<T>`**（它会通过委托反射构造 LuminanceSource，AOT 风险高）。改用最静态的管线：`RGBLuminanceSource(BGRA32)` → `HybridBinarizer` → `BinaryBitmap` → `MultiFormatReader.decode(bitmap, hints)`，全链路无反射。
  - **POSSIBLE_FORMATS** 显式限定 QR_CODE / DATA_MATRIX / CODE_128 三种，缩小检测面。
  - `[UnconditionalSuppressMessage("IL2026"/"IL2057")]` 集中压在 `QrDecoder.Decode` 上，不外溢到其他文件。
  - PNG byte[] → Avalonia Bitmap → `Marshal.AllocHGlobal` + `CopyPixels(nint, ...)` + `Marshal.Copy` 取 BGRA。**Avalonia 12 的 `Bitmap.CopyPixels` 第二参数已从 byte[] 改为 nint**，必须走指针路径。
  - Q 键 vkCode 0x51，分支必须插在 R46 T 之后、OCR-lazy gate **之前**（Q 在 A-Z 范围内，gate 之后会被吞）。
  - 剪贴板复用 `Win32Clipboard.SetPng` 模式新增 `SetText(string)`（CF_UNICODETEXT）。
- **资源开销**：常驻 0；运行时单次解码 < 100ms（BitBlt 已在 R40 完成，ZXing 静态管线 < 50ms）。
- **验收（第四十二批）**：
  - Debug build 0 警告 0 错误。
  - **253/253 → 280/280 测试通过**（R45 新增 21：QrDecoder 边界 7 + UrlDetector 12 + record 2）。
  - NativeAOT Release publish **0 trim/AOT 警告**（IL2026+IL2057 suppress 生效）。
  - exe 27,669,504 → 28,264,960 字节（**+595,456 / 582KB**，超 AC-4 的 300KB 目标 282KB；原因：ZXing 在 NativeAOT 下 QR+DM+Code128 全解码链 AOT 化的代码体积；如需瘦身可裁掉未用格式或换 QR-only 解码库）。0 trim 风险的功能性可接受。
  - 双路径同步到 `artifacts/publish/win-x64-nativeuia/BYH.exe`。
- **风险实测结论**：spec 预警"ZXing 在 NativeAOT 下首次反射调用需验证" —— **实测通过**，因为绕开了 `BarcodeReader<T>` 反射路径，用纯静态管线。



#### ✅ R46 — 贴图（pin screenshot as floating note）（已完成，2026-07-19 第三十八批）

- **触发**：Ocean Eyes 工具栏按 **T**（Pin）→ 当前缓存 PNG 钉成 always-on-top 浮动小窗（区域左上角 +16,16 偏移），可拖动（标题栏）/ 可关闭（✕ 按钮或右键菜单）/ 可复制（右键菜单 → `Win32Clipboard.SetPng`）/ 可关闭所有（右键菜单）。
- **架构**：
  - `PinnedScreenshotWindow.axaml(.cs)`（新）— Avalonia Window + `NoActivateWindowHost`（`WS_EX_NOACTIVATE | WS_EX_TOPMOST`），`SizeToContent=WidthAndHeight` 自适应图片，`Stretch="None"` 1:1 显示。HeaderBar 标题栏（"📌 Pinned" + 古金色 ✕）+ 1px 古金边 Image。拖动通过 HeaderBar 的 `PointerPressed/Moved/Released` + `PointToScreen` delta。`ContextMenu` 自包含三 MenuItem（复制图像 / 关闭 / 关闭所有），各自 raise `RequestCopy` / `RequestClose` / `RequestCloseAll` 事件。
  - `SelectionRuntime` 新增字段 `_pinnedWindows` / `_pinnedHosts`（两并行 `List<>`）。`OnToolbarKeyPressed` 新增 T 键分支（vkPin=0x54，P 后、A-Z filter 前，Ocean Eyes 限定，吞键 + 不 dismiss）。新增 `PinOceanEyesScreenshot` / `CopyPinnedToClipboard` / `ClosePinned` / `CloseAllPinned` 私有方法。
  - **关键设计：贴图生命周期独立于 Ocean Eyes 会话**——`DismissOceanEyes` / `ResetForRedraw` 都不动贴图，只有 runtime `Dispose`（app 退出）或贴图自己的关闭按钮才销毁。这覆盖用户典型场景：截图 → 钉在边上 → 回去对照录入，中间允许 Esc/Enter/F/J/Z/R 操作。
  - PNG 来源：直接读 `_oceanEyesPng` 缓存（`ShowToolbarForOceanEyes` 时已缓存，0 延迟 / 0 重新 BitBlt）。
- **依赖**：无新增。
- **资源开销**：每个贴图 ~PNG 大小 + Bitmap decode buffer（1920×1080 ≈ 8MB/张）；用户常驻 < 10 张可接受。关闭即 Dispose 释放。
- **代码量**：~213 行（AXAML 51 + code-behind 162 + runtime +110）。
- **验收**（第三十八批 v13 终态）：
  - Debug build 0 警告 0 错误。
  - 232/232 测试通过（无新增测试——R46 是 UI 集成层，无新纯函数可测）。
  - NativeAOT Release publish 0 警告。
  - exe 27,669,504 字节（v13 = v9 代码，移除 v10-v12 的 ScaleTransform/关键帧 Animation/RenderTransformOrigin 代码；R44+R46 累计 +59,392 / 58KB，远低于 100KB×2 预算）。
  - 双路径同步：`cp` 到 `artifacts/publish/win-x64-nativeuia/BYH.exe`。
  - 机器侧验证：BYH 已启动（PID 36916），**用户 175% DPI 显示器**——贴图窗从屏幕外（右下方）滑入到 pin 位置（TranslateTransform (400,100)→(0,0) CubicEaseOut 300ms）。v8-v12 scale 弹性动画探索搁置，详见 §22。
- **v2 用户反馈调整（2026-07-19 第三十八批 v2）**：
  - 修默认尺寸自动放大（根因 Avalonia `Bitmap` 默认 96 DPI）。
  - 滚轮缩放 ×1.1 clamp [0.1, 5.0]。
  - 去边框去标题栏。
  - 双击关闭（v2 用 Avalonia `DoubleTapped`——v3 弃用）。
  - 拖动 3px 阈值。
- **v3 用户反馈修复（2026-07-19 第三十八批 v3）**：
  - 双击关闭依然不工作 → 弃用 Avalonia `DoubleTapped`（no-activate + PointerCapture 下不可靠），改 `OnPointerReleased` 手动检测 500ms / 8px。
  - 缩放损失图像数据 → 设 `BitmapInterpolationMode.None`（nearest-neighbor 像素保真）。
- **v4 用户反馈修复（2026-07-19 第三十八批 v4，根因终于定位）**：
  - 用户反馈："默认窗口只覆盖了很小一部分，整张图片比原生截的图片要大了很多，虽然默认窗口和截的窗口一样，但到时候是直接裁剪的"。
  - **根因**：Avalonia `Image.Stretch="None"` 的语义**不是**"按指定 Width/Height 缩放图像"，而是"按图像自然尺寸渲染，超出控件 Width/Height 边界就裁剪"。v2/v3 设 `Image.Width/Height = pixelSize × _scale / RenderScaling` 只改了裁剪框，没缩放图像本身。在 175% DPI 下，bitmap 自然 Size = `PixelSize` 逻辑 DIP，渲染成 `PixelSize × 1.75` 物理像素，但窗口只有 `PixelSize` 物理像素——所以用户只看到图像左上 57%。
  - **修复**：用 `LayoutTransformControl` 包 `<Image>`，缩放走 `ScaleTransform`（LayoutTransformControl 真正重新 measure + SizeToContent 跟随，不裁剪）。`ApplyScale` 设 `_scaleTransform.ScaleX/Y = _userScale / RenderScaling`。
  - **核心教训（永久）**：Avalonia `Image.Stretch="None"` 不是缩放控制。要缩放 Avalonia Image + SizeToContent 跟随，**默认用 LayoutTransformControl + ScaleTransform**，不要靠 Width/Height。
- **v5 用户反馈调整（2026-07-19 第三十八批 v5）**：
  - **添加圆角边框**：v4 的 LayoutTransformControl 外包一层 `<Border CornerRadius="8" BorderBrush="#FFD9C28A" BorderThickness="1" ClipToBounds="True" BoxShadow="0 4 12 0 #66000000">`。Border 的 CornerRadius 自动 clip 子内容（图像也圆角）；Window Background=Transparent 让窗口外方角透到桌面。
  - **T 后自动关闭选框**：用户语义"我已经触发了动作，选框就可以关闭了"——贴图是确认动作，Ocean Eyes 框选使命完成。T 键分支加 `DismissOceanEyes()` 调用（关工具栏 + overlay + 清状态）。**贴图窗保留**：`DismissOceanEyes` 不动 `_pinnedWindows`（关键设计点保留）。竞态安全：PNG 快照在 `PinOceanEyesScreenshot` 的 hook 线程同步读取，DismissOceanEyes 后续 nulling 不会 race UI 线程 decode。
  - **T 行为变化**：v1-v4 T 后工具栏保留可继续 F/J/Z/R/Enter；v5 T 是 terminal action，贴图后 Ocean Eyes 完全退出。要 pin 多张需要重新 `Ctrl+Alt+Q`。
- **v6 用户反馈调整（2026-07-20 第三十八批 v6）**：
  - **去金边只留圆角**：v5 加的 `<Border BorderBrush="#FFD9C28A" BorderThickness="1">` 改成 `BorderBrush="Transparent" BorderThickness="0"`，BoxShadow 删除。Border 元素还在只为提供 `CornerRadius` clip 几何让图像四角圆滑。
  - **添加 esc 关闭**：贴图是 `WS_EX_NOACTIVATE` 永不抢焦点，所以 Esc 不能用窗口 KeyDown——必须走全局 keyboard hook。`OnToolbarKeyPressed` 顶部加新 Esc 分支（`_oceanEyesActive==0 && _pinnedWindows.Count>0` 时 LIFO 关闭最后一个贴图）。三处协同保活 hook：`DismissOceanEyes` 改条件禁用（有贴图就保活）、T 分支末尾显式 `SetEnabled(true)`、`ClosePinned`/`CloseAllPinned` 列表空时禁用。
  - **最小化有限度**：`MinScale` 0.1 → 0.25。原 10% 让 1920×1080 缩到 192×108 看不见；25% 缩到 480×270 仍清晰可读可拖。
- **v6 Esc bug 修复（2026-07-20 第三十八批 v6 后期）**：
  - 用户反馈"ESC 没有用"——v6 加了 Esc 路由但 Esc 仍然不工作。
  - **深度日志定位根因**：在 `LowLevelKeyboardHook.SetEnabled` 加 DIAG 日志，看到 T 分支 `SetEnabled(true)` 后 25ms 又被 `SetEnabled(1->0)` 禁用。罪魁祸首是 `DismissOceanEyes` 的 UI-thread Post 里 `_windowHost.Hide()` 触发的 `ToolbarSessionView.onToolbarHidden` 回调——回调无条件禁用 hook，吞掉了 T 分支的启用。
  - **修复**：`onToolbarHidden` 加 `_pinnedWindows.Count == 0` 守护；同样守护加到 `ResetForRedraw` 和 `StopKeyboardHookQuiet`。
  - **教训（永久）**：低层 hook 启用/禁用有多个调用点（主路径 + 回调路径），加新功能依赖 hook 保活时必须审计**所有**禁用点。诊断"启用后又被神秘禁用"最快方法是在 `SetEnabled` 内部加状态变化日志。
- **v7 用户反馈调整（2026-07-20 第三十八批 v7）**：
  - **添加动画**：三种——(m) 出现凘入（Window `Opacity 0→1`，DoubleTransition 150ms，`Opened` 事件触发）；(n) 关闭凘出（`AnimateOutAsync` 设 `Opacity=0` + `Task.Delay(180)`，`ClosePinned` 改 `async` 在 Hide+Dispose 前 await）；(o) 滚轮缩放平滑过渡（`ScaleTransform.Transitions` 加 DoubleTransition for ScaleX/Y 120ms）。
  - **关键技术点**：Transitions 挂在 `ScaleTransform` 实例上（它是 `Animatable`），不是挂在 `LayoutTransformControl.LayoutTransform`（换整个 Transform 对象不能 DoubleTransition，只改 ScaleX/Y 可以）。窗口从 `_pinnedWindows` 列表移除在 `await AnimateOutAsync()` 之前，防快速二次 Esc 重入。
- **v8-v13 动画探索（2026-07-20 第三十八批 v8-v13，5 轮迭代，最终搁置）**：
  - **v8**：ScaleTransform 0.85→1.0 BackEaseOut 250ms（用户反馈"没变化，从侧面弹出"——太微妙）。
  - **v9 误解**：我误以为"从侧面弹出"是要侧面滑入，改成 TranslateTransform (400,100)→(0,0) CubicEaseOut 300ms（用户后续澄清是要 scale 弹簧，不是侧滑）。
  - **v10**：ScaleTransform 0.3→1.0 BackEaseOut 350ms（用户反馈"抖了好几下"+"从左上角弹不是正中间"）。
  - **v11**：改 CubicEaseOut + 移到 Border.RenderTransform（用户反馈"没变化，依然从左上角"）。
  - **v12**：Avalonia 关键帧 Animation 0.5→1.15→1.0（Mac 弹簧：小→过冲→稳定）+ 显式 `Frame.RenderTransformOrigin = RelativePoint(0.5,0.5,Relative)`（用户决定搁置）。
  - **核心未解问题**：`ExtendClientAreaToDecorationsHint=True` 窗口上 RenderTransformOrigin 不可靠——AXAML 属性和 code-behind RelativePoint 都不能让 scale 从中心放大，实际从左上角放大。未来修复思路：自定义 Easing / MatrixTransform 显式构造缩放矩阵 / 两层 LayoutTransformControl / 等 Avalonia 更新。
  - **v13 回滚**：回到 v9 TranslateTransform 侧面滑入作为暂定方案。code-behind + AXAML 都恢复 v9 状态，移除所有 v10-v12 代码。
  - **教训**：(1) 遇到模糊的动画描述先问清位置动画 vs 缩放动画。(2) Avalonia `ExtendClientAreaToDecorationsHint=True` 窗口上 `RenderTransformOrigin` 不可靠，不能用于 center-origin scale。(3) Avalonia `Animation` 关键帧 API 可以做三值曲线（弹簧），但 `RenderTransformOrigin` 问题阻止了测试效果。
- **与原始 spec 的偏差**：原始 spec 写"Esc 仅关闭当前焦点贴图"。实际 v6+ 实现 Esc 通过全局 hook LIFO 关闭最近一个贴图（贴图是 `WS_EX_NOACTIVATE` 永远不能获得焦点，没有"当前焦点贴图"概念）。贴图关闭走 **Esc** / **双击**（v3 手动检测）/ 右键菜单，所有路径带 v7 凘出 + v13 侧面滑出动画。v8-v12 的 scale 弹性动画搁置。

#### ✅ R47 — 数字序号标注（numbered badges）（已完成，2026-07-20 第四十二批）

- **触发**：Ocean Eyes overlay 框选确认后按 **A**（Annotate）进入标注模式 → 鼠标左键点击放数字 badge（1, 2, 3... 自增）→ **Ctrl+Z** 撤销 → 再次 **A** 或 **Esc** 退出标注模式（badge 仍保留）→ Enter 保存时 badge 烧入 PNG。
- **代码量**：~1060 行（Core 抽象 3 文件 + UI AnnotationCanvas + SelectionRuntime 432 + tests 292 + 其他配置）。
- **依赖**：**无新增**。
- **架构关键决策（永久记录）**：
  - **三层抽象**（为 R48 标注工具集预留）：
    - `NumberedBadge`（record: Number/X/Y）
    - `NumberedAnnotationSession`（Push/Undo/Clear，undo stack 抽象，R48 可复用）
    - `NumberedBadgeGeometry`（纯函数 DPI 缩放几何计算，可测，R48 可加 RectangleGeometry 等）
  - **A 键 vkCode 0x41 必须插在 Q 之后、OCR-lazy gate 之前**（A 是 A-Z 范围首字母，gate 之后会被吞）。
  - **Ctrl+Z** 用 P/Invoke `GetKeyState(VK_CONTROL=0x11) & 0x8000` 检测，不能只判单 vkCode。
  - **mouse hook 守护**：标注模式激活时吞左键（不传源程序、不触发 R41 重画），把点击坐标派发到 UI 线程调 `session.Push + canvas.AddBadge`。仿 R44 `_colorPickerActive` 短路模式。
  - **badge 烧入 PNG**：**故意不用 SkiaSharp**（会增重 ~2MB+ AOT 体积），改用**手写 BGRA 像素操作 + 内建 5x7 bitmap font** + alpha 混合 + DPI 缩放（数据源自 `NumberedBadgeGeometry` 纯函数）。代价：数字无抗锯齿（2x 缩放后单数字可读，可接受）。未来要美化再换 SkiaSharp 或 Avalonia RenderTargetBitmap。
  - Badge 视觉：28 DIP 直径，gold accent `#FFD9C28A` 填充 + `#FFB8956A` 描边 + 白色 Bold 数字。
- **资源开销**：常驻 0；运行时每 badge < 1ms 渲染。
- **验收（第四十二批）**：
  - Debug build 0 警告 0 错误。
  - **280/280 测试通过**（R47 新增 27：NumberedAnnotationSession 11 + NumberedBadgeGeometry 16）。
  - NativeAOT Release publish **0 trim/AOT 警告**。
  - exe 28,264,960 → 28,283,392 字节（**+18,432 / 18KB**，远低于 100KB 预算 —— 因为避开了 Skia 依赖）。
  - 双路径同步到 `artifacts/publish/win-x64-nativeuia/BYH.exe`。
- **R48 就绪度**：`NumberedAnnotationSession` undo stack + `AnnotationCanvas` add/remove/clear API + `NumberedBadgeGeometry` 纯函数模式都已为 rectangle/ellipse/arrow/pen/highlight 工具留好扩展点。



---

### 🖌️ P1 中等性价比（本地渲染，无网络，按需打开）

#### R48 — 标注工具集（rectangle / ellipse / arrow / pen / highlight）

- **触发**：与 R47 同一个标注模式（按 A 进入），工具栏提供 5 种工具。
- **代码量**：~400 行（5 个工具的 hit-test + draw + undo/redo stack）。
- **依赖**：无新增（纯 Avalonia Canvas + Skia）。
- **资源开销**：常驻 0。
- **验收**：每种工具拖拽即画；Shift 修饰键约束（矩形→正方形、椭圆→圆、画笔→直线）；undo/redo 完整；保存 PNG 时烧入。
- **关键点**：与 R47 数字 badge 共用同一 Canvas + undo stack。

#### R49 — 截图相册（screenshot gallery）

- **触发**：托盘菜单 / 设置页加入"打开 Ocean Eyes 相册"入口，打开新 `GalleryWindow.axaml(.cs)`，缩略图网格浏览 `%USERPROFILE%\Pictures\Ocean Eyes\` 下所有 PNG。
- **代码量**：~300 行（GalleryWindow + 缩略图加载 + 双击打开 / 右键删除 / "打开所在目录"）。
- **依赖**：无新增（Avalonia 自带 `ItemsControl` + `WriteableBitmap` 缩略图）。
- **资源开销**：常驻 0；打开时一次性扫描目录（懒加载缩略图）。
- **验收**：1000+ 张图不卡（虚拟化列表）；删除走回收站（`SHFileOperation` with `FOF_ALLOWUNDO`）；右键"打开所在目录"用 `explorer.exe /select,"..."`。
- **可选增强**：搜索框（按文件名/OCR 文本过滤，OCR 文本需要 R24 已有的元数据 JSON 旁车文件）。

#### R50 — 带壳截图（device mockup）

- **触发**：Ocean Eyes 工具栏按 **M**（Mockup）→ 弹出外壳选择菜单（MacBook / iMac / iPhone / Android / Browser），选中后用 Skia 把截图合成到外壳模板里，复制到剪贴板。
- **代码量**：~250 行（外壳加载 + Skia 合成 + 菜单）。
- **依赖**：无新增（外壳是 PNG 资源 + Skia draw image）。
- **资源开销**：常驻 0；每个外壳模板 PNG ~50-200KB 打包进 `avares://`。
- **验收**：截图自动缩放到外壳"屏幕区域"；输出 PNG 透明背景；常用 5 个外壳（MacBook / iMac / iPhone 15 / Pixel / Edge browser）。
- **关键点**：外壳模板需自己画或下载免费授权的——不要从其他截图软件扒。

#### R51 — 截图美化（padding + shadow + rounded corners）

- **触发**：Ocean Eyes 工具栏按 **B**（Beautify）→ 一键给截图加 32px padding + 香槟色背景（`#FFFCF7EA` 与 Ivory Jade 一致）+ 8px 圆角 + 柔光阴影，复制到剪贴板。
- **代码量**：~120 行（Skia 合成 + 配置）。
- **依赖**：无新增。
- **资源开销**：常驻 0。
- **验收**：输出尺寸 = 原图 + 64px；阴影偏移 4,4 blur 16；圆角半径可在设置页调（默认 8）。
- **可选**：用户在设置页选背景色（默认香槟 / 白 / 黑 / 渐变）。

#### R52 — 磁力吸（magnetic snap for pinned notes）

- **触发**：R46 贴图窗口靠近屏幕边或其他贴图时自动吸附对齐（边缘 + 角 ± 8px 阈值）。
- **代码量**：~100 行（在 `PinnedScreenshotWindow` 的位置变更回调里做对齐计算）。
- **依赖**：无新增（仅依赖 R46 已存在）。
- **资源开销**：常驻 0；吸附计算 O(贴图数²) 但贴图通常 < 5 个。
- **验收**：拖动时半透明吸附辅助线；松手吸附；按住 Shift 临时禁用磁力吸。
- **依赖前置**：必须先做 R46。

---

### 🔥 P1+ 重功能（核心价值，复杂度可控）

#### R53 — 长截图（scrolling screenshot with auto-stitch）

- **触发**：Ocean Eyes overlay 框选后按 **L**（Long）→ 进入长截图模式：
  - **自动滚动**：用户按 ↓/Space，Ocean Eyes 自动 `SendMouseWheel`，每滚一档截一张 + 立即 stitch，到滚动底部（`GetScrollInfo` 检测或截图未变化）自动停。
  - **手动滚动**：用户自己滚，按 Space 截一张并 stitch；按 Enter 完成保存。
  - 右侧实时预览已拼接 PNG（高度增长可视化）。
  - 失败兜底：若某帧匹配失败，按 ShareX `bestMatch` 兜底，右下角显示 ⚠️ "部分拼接失败 N 帧"。
- **算法**：移植 ShareX 的 `ScrollingCaptureManager.CombineImages`（130 行核心），即 `LockBits` + P/Invoke `msvcrt.memcmp` 逐行比字节找连续匹配段：
  - 忽略左右 5%（避免侧栏漂移）；
  - 忽略底部 10%（避免页脚固定条）；
  - 失败时取历史 bestMatch 兜底（标 `PartiallySuccessful`）。
- **代码量**：~600 行（capture loop 150 + stitch 150 + UI/预览 200 + 配置 100）。
- **依赖**：**0 新增**（stitch 用 `memcmp` P/Invoke；截图用现有 BitBlt + Skia；滚动用 `SendInput`）。
- **资源开销**：常驻 0；运行时 stitch 每帧 ~50-200ms（不阻塞 UI，`Task.Run` 异步）。
- **验收**：
  - 长网页（5000px+ 高）正确拼接无明显错位；
  - 微信/QQ 聊天记录可拼；
  - 失败帧有 UI 提示，不静默吞错；
  - 保存 PNG 到 `Pictures/Ocean Eyes/ocean-eyes-long-yyyyMMdd-HHmmss.png`，可选导出 PDF（参考 Susskind2 用 `PdfSharp`，可选依赖）。
- **风险**（ShareX 自己也提到，永久记录）：
  - **懒加载页面**：滚太快没加载完，需可配 `ScrollDelay`（默认 300ms）。
  - **浮层/动画/视频/广告位**：破坏像素匹配，可能产生 `PartiallySuccessful`（兜底已覆盖）。
  - **DRM 内容**：黑屏（与所有截图工具一样，无解）。
  - **NativeAOT + P/Invoke `msvcrt.memcmp`**：Windows 内置 DLL，AOT 安全；但要在 csproj 显式 `<DllImport>` 标注或用 `LibraryImport` source generator。
- **参考实现**：
  - `ShareX/ShareX.ScreenCaptureLib/ScrollingCaptureManager.cs`（金标准，~25k stars，30+ 行核心 stitch 算法）。
  - `Susskind2/ScrollCapture`（C#/WinForms，更简单；零拼接只导 PDF；代码 663 行，单 `PdfSharp` 依赖）。
- **建议**：移植 ShareX 算法（更鲁棒），UI 参考 Susskind2 的浮动工具栏 + 实时计数。

---

### R44-R53 完成顺序建议

```
P0（高性价比，先做）
  ✅ R44 取色器 (2026-07-19 落地)
  ❌ R45 二维码 (2026-07-20 落地 → 同日撤销，ZXing +595KB 不划算)
  ✅ R47 数字标注 (2026-07-20 落地)
  ✅ R46 贴图 (2026-07-19 落地，v13 终态)
    ──→ R52 磁力吸（依赖 R46，进行中，第四十三批）

P1（中等，按需做）
  R48 标注工具集（依赖 R47 标注 layer —— 已就绪，进行中，第四十三批）
  R49 截图相册
  R50 带壳截图
  R51 截图美化

P1+（重功能，最后做）
  R53 长截图（独立，可任何时候做）
```

### 每项落地的统一验收清单

1. `dotnet build -c Debug` — 0 警告 0 错误
2. `dotnet test` — 全过（Core + Providers + Windows，含该功能新增测试）
3. `dotnet publish -c Release -r win-x64` — 0 AOT/trim 警告
4. exe 大小增量 < 100KB（除 R50 带壳外壳资源 +500KB-1MB）
5. 双路径同步 + PowerShell 重启
6. 机器侧验证清单（每项独立列）
7. handoff §3 新增章节 + BACKLOG-roadmap.md 该项打 ✅

---

## 待办（非紧急，与 R44-R53 平行）

- **安装包 / 代码签名 / 开机启动**（v0.1 收尾）。
- **P1.7 DPI/多显示器定位**（单屏 mouse+16px）。
- **P1.8 应用语料库 95% 验收**。
- **R24 轨道 B② PaddleOCR-VL-1.6 本地 OCR**：0.9B SOTA 离线兜底，AOT 验证 + 模型分发，v0.2 目标。
- **R24 轨道 B③ WinRT OCR**：`Windows.Media.Ocr`，长期 backlog，性价比最低。
- **R23 快捷命令**（用户暂缓，见上方 ⏸️ 章节）。
- **macOS 未开始**。
