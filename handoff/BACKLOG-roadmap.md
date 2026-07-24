# BYH 路线图待办（Backlog）

> 来源：用户 2026-07-17 提出的功能清单 + 第 1-14 批增量；2026-07-19 加入 R44-R53（小旺 inspired Ocean Eyes 扩展）；2026-07-20 加入 R54 剪贴板历史（独立于 Ocean Eyes 系列，常驻型功能）。
> 主交接快照见 `00-CURRENT-HANDOFF.md`。
> 模块文档见 `docs/architecture/00-architecture-overview.md`。
> **更新 2026-07-20 第四十三批（v2 终态）：撤销 R45 二维码（用户判定"不太用得上"，revert 0623e4c）；R52 磁力吸 + R48 标注工具集 落地。R48 走了两轮：v1（merge 9092d37）用户测试发现 3 个严重 bug（无拖拽实时预览 / pen-highlight 路径记录失效 / arrow 撤销计数错）→ revert 79a39a0 → v2（merge 702788c）重做，扩 IMouseHook 加 MouseMove 事件根本解决路径记录 + 加 live preview + 修 Arrow Tag(2)，reviewer 补 20 个回归测试覆盖 3 个 v1 失败模式。R52 也修了 GetWorkAreas 的 DPI 缩放 bug（WorkingArea 已是物理像素，不应再 ×RenderScaling，commit 633f066）。最终 main：316/316 测试通过，NativeAOT 0 警告，exe = 27,782,144 字节。**
> **更新 2026-07-20 第四十四批（调研）：R54 剪贴板历史调研完成，规格定稿（v1 纯文本 + Smart auto-group + 50 图上限 + JSON 持久化）。基线内存实测 123MB（含完整 Ocean Eyes 功能），加 R54 预估 +3MB（+2.4%），用户感知不到。详见 §R54。**
> **更新 2026-07-20 第四十六批：R49 截图相册落地（含托盘入口 + 双击预览 + 右键菜单）。Ocean Eyes 工具栏按 G **或** 托盘右键 → "Open Screenshot Gallery" 弹出标准窗口，瀑布流浏览 `%USERPROFILE%\Pictures\Ocean Eyes\` 历史截图（newest-first）。**双击 = 大图预览**（半透明遮罩 lightbox，底部按钮：复制/删除/打开目录），**右键 = 上下文菜单**（复制/查看/删除/资源管理器中显示），Delete 键删除，Enter 键预览，Esc 两级关闭（先预览后窗口）。不退出 Ocean Eyes（同 P 模式）。代码 +660 行（含 9 个 loader 单测），326/326 测试通过，NativeAOT 0 警告，exe +134KB（超 100KB 预算 34KB，已记录例外——Avalonia ItemsControl/WrapPanel/DataTemplate/ContextMenu/Separator 的 AOT 元数据是固有成本）。详见 §R49。**
> **更新 2026-07-21 第四十七批：R49 预览缩放/平移 8 轮调试终态。双击预览图支持：滚轮缩放（fit/4 ~ fit×8，光标锚定）+ 左键拖动 1:1 跟手平移 + Esc 关闭。最终架构：`Border ClipToBounds > Canvas > Image Stretch=None` + `Image.RenderTransform = TransformOperations.Builder.AppendMatrix(_matrix)` + 单一 `_matrix` 自管 scale+translate（PanAndZoom `ZoomBorder` 模式）。踩了 6 个 Avalonia 12.1 NativeAOT 独立坑（详见 §R49 教训）：ScrollViewer 接管滚轮 / TransformGroup children 顺序 / LayoutTransformControl 不支持 Translate / Image Stretch=None 仍被父约束 / MatrixTransform 在 AOT 静默失效 / `Matrix *` 运算符语义反转。最终 exe 27,925,504 字节（+139KB vs R48 基线）。诊断方法：临时 `_logger.Info` + 读 `%LOCALAPPDATA%\BYH\logs\BYH.log`，停止猜测让数据说话。**
> **更新 2026-07-22 第五十一批：R54 剪贴板历史 v1 落地（文本 only + Smart auto-group + mask 敏感 + 粘贴可选）。**用户选"最小可用"档：Ctrl+Alt+V 弹出 Maccy 风格搜索窗（克隆 SpotlightWindow 的拼音三段搜索），后台监听 WM_CLIPBOARDUPDATE 自动归类（Ortu 风格规则引擎：链接/代码/JSON/命令/数字/联系人/敏感/文本），敏感条目 ●●●● 防肩窥（点击展开），Pinned 永不淘汰，排除 app 挡 1Password/KeePass。**调研最大发现**：`Win32Clipboard.cs`（815 行）早已有消息窗口 + SubscribeChanges + 序列号去重 + Backup/Restore + GetOwnerProcessId——spec 里"要写"的好几块其实早就有，**唯一要加的是 SetText（照 SetPng 抄）+ GetForegroundProcessName**。**用户拍板 4 档**：最小可用 v1（文本 only）/ 图片不做（留 v2）/ 敏感只 mask 不 DPAPI（留 v2）/ 粘贴都做（默认只写剪贴板，设置可开自动 Ctrl+V）。**架构**：4 阶段实施——① 底层纯函数（ClipboardEntry/ClipboardClassifier/ClipboardHistoryStore + 2 个 Store + ByhApplicationPaths path 属性）② ClipboardHistoryService（App 层，长存 Win32Clipboard + 序号去重 + 排除 app + LRU）③ UI（PinyinSearchHelper 提取共享 + ClipboardHistoryWindow 克隆 Spotlight + SettingsWindow 新 section）④ App.axaml.cs 接线（照 Spotlight 三件套对位加 hotkey+window+service+事件+dispose）。**踩坑**：① ClipboardHistorySettings 静态字段初始化顺序（DefaultExcludeProcessNames 必须在 Default 前，否则 null）；② EvictToMax 初版按列表位置淘汰是错的，改按 CapturedAt 时间戳淘汰最旧非 pinned；③ ClipboardHistoryService 不能放 Platform.Windows（不依赖 Infrastructure），改放 App 层（composition root，三项目都能见）。**安全性**：Win32Clipboard 唯一 window class name（pid+guid），长存实例与现有临时 using 块互不冲突；排除 app 是第一道防线（挡密码管理器），mask 是第二道（防肩窥），剪贴板内容不写日志。0 警告 0 错误，**390/390 测试通过**（+27 剪贴板单测：Classifier 8 组正反例 + Store LRU/Pin/dedup/round-trip + Settings + Trigger），NativeAOT 0 警告，exe **27,928,064 → 28,167,680（+234KB**，超 150KB 预算——R54 是独立模块不归 Ocean Eyes 管辖，且含完整新窗口+服务+设置页+拼音表共享，合理）。⚠️ **需真机验证**：复制各类文本→Ctrl+Alt+V→分组/mask/搜索/粘贴/Pin/删除/排除 app 全通。详见 §R54。**
> **更新 2026-07-22 第五十批：R56 贴图浮窗缩放弹出/缩走动画落地（v4 终态，用户确认"好了"）。**用户要求把 T 贴图的动画从 R46 v13 侧面滑入改成"简单弹动效果"，选了"缩放弹出"，后又要求关闭也用同款。**开了 4 轮迭代**（v1 ScaleTransform+RenderTransformOrigin → v2 timer+矩阵但 Ease 当绝对值 → v2.1 Ease 插值 → v3 LayoutUpdated 启动 → v4 ApplyScale 瞬间到位），每轮都有用户真机反馈。**踩坑教训**：① RenderTransformOrigin 在 frameless `ExtendClientAreaToDecorationsHint` 窗口整体失效（§R46-22 v11 早证伪，我没读日志重蹈）；② 缓动 Ease 返回值是进度不是绝对值；③ **真根因**：`ApplyScale` 的 DPI 基准 ScaleTransform 挂着 R46 v7 的 120ms DoubleTransition，窗口打开时 DPI 校正慢慢缩，Bounds 实时变化（日志显示 822→470，比例=RenderScaling），pop 每帧拿错基准 = "侧面滑入"。**v4 解**：`ApplyScale(animate:false)` 打开时瞬间到位 + pop 从 LayoutUpdated 启动。诊断靠 §R47 "停止猜测读日志"——临时加 RedactedLogger 写 BYH.log，确认后已删。**架构**：单 DispatcherTimer（16ms Render 优先级）按 PopDirection 枚举驱动 In/Out，每帧烘中心缩放矩阵 `Matrix(scale,0,0,scale,(1-scale)·w/2,(1-scale)·h/2)` 用 TransformOperations 推（NativeAOT 可靠，不用 MatrixTransform）。In=BackEaseOut 350ms 过冲弹，Out=CubicEaseIn 200ms 缩走+淡出。**安全性**：RenderTransform 纯视觉，拖拽和 R52 磁吸（Position+ClientSize）不受影响；ApplyScale 默认仍 animate 平滑滚轮缩放。0 警告 0 错误，255/255 测试，NativeAOT 0 警告，exe +1.5KB（27,926,528→27,928,064）。详见 §R56。**
> **更新 2026-07-22 第四十九批：R55 置顶截图四周立体阴影落地。Ocean Eyes 截图后按 **T** 贴图，浮动置顶窗口四周加大范围柔和投影（iShot/CleanShot X 风格的悬浮卡片感）。AXAML：把承载图片的 `Frame` Border 包进一个外层 Border，`BoxShadow="0 12 32 0 #66000000"`（offset y=12 / blur=32 / 黑 40%）+ `Margin="24"`（给透明 frameless 窗口留阴影渲染空间——Avalonia BoxShadow 只在元素边界内绘制且受 ClipToBounds 裁剪，窗口精确等于图片尺寸时阴影会被裁掉）。**吸附 gap 修正**（初版误判"吸附边界落在阴影边缘是预期行为"，用户实测发现图片离屏幕/彼此边缘永远隔 24px）：窗口变大后吸附改用 **IMAGE rect**（窗口 rect 四边内缩 24×DPI）运算，吸附后 image 左上角 `-margin` 转回 window 坐标；runtime `GetOtherPinnedBounds` 改报 peer image rect（新公开 `PinnedScreenshotWindow.ImagePhysicalRect`）；引导线画在 image 边缘。inset 纯函数 `MagneticSnapCalculator.InsetRect` 可单测。与 R51（已撤销）的区别：R51 的阴影"看不出"是因 CleanShot X 模型 padding 透明色被不透明原图遮盖；本次投影落在桌面背景上（图像四周全是桌面），一定可见。0 警告 0 错误（NU1900 漏洞库 EOF 是网络瞬时），255/255 测试通过（+5 个吸附 gap 回归），NativeAOT 0 警告，exe +1KB（27,925,504 → 27,926,528）。详见 §R55。**
> **更新 2026-07-22 第四十八批（R53 落地 → 同日撤销）：R53 长截图（L 键）实施完成（ShareX 风格逐行字节重叠匹配 stitcher + 纯函数单测），经历了 4 轮 UX 模型迭代（标准窗口 → no-activate 浮动工具条 → 纯状态机自动截帧），踩了 3 个架构陷阱（① Ocean Eyes 全屏 RegionSelectOverlay 吞掉鼠标滚轮事件导致无法滚动 ② DismissOverlay→Cancel()→RegionCancelled→ResetForRedraw() 会清零 _oceanEyesActive + 禁用 hook 导致按键全失效 ③ WS_EX_NOACTIVATE 窗口无法接收键盘焦点）。用户最终判定"弹窗有啥用 / 不做了"。本批撤销。撤销的代码：LongScreenshotStitcher.cs / LongScreenshotWindow.axaml(.cs) / L 键分支 / mouse hook 的 WM_MOUSEWHEEL 捕获 / MouseMessageType.MouseWheel 枚举 / MouseEventData.WheelDelta 字段 / 8 个 stitcher 单测。**教训**：长截图这类"需要边操作目标边截屏"的功能，核心矛盾是 BYH 的全局 hook + 全屏 overlay 与"让用户自由操作目标窗口"根本冲突——overlay 不隐藏则吞事件，隐藏则走 Cancel 事件链杀掉 Ocean Eyes 会话。若未来重做，需先重构 overlay 使其支持"鼠标事件穿透"模式（Win32 WS_EX_TRANSPARENT 或 Avalonia IsHitTestVisible=False 的全屏透明层），而非 Hide/Show 切换。详见 §R53。**
> **更新 2026-07-21 第四十五批（R51 落地 → 同日撤销）：R51 截图美化（B 键）实施完成（纯软件 BGRA 合成，CleanShot X 风格的浮动截图 + 圆角 + 投射阴影），+24.5KB / 0 新依赖 / 340 测试全过。用户真机测试后判定"美化了啥 / 不搞了"——根因是 CleanShot X 模型对深色内容截图美化效果不明显（背景色被不透明原图完全遮盖，阴影 RGB 与深色内容相近不可辨）。本批同日 revert（commit 跟进）。撤销的代码：ScreenshotBeautifier.cs / BeautifyOceanEyesScreenshot / B 键分支 / BurnAnnotationsOntoBgra 拆分 / OceanEyesCaptureSettings +7 字段 / OceanEyesCaptureStore 读写扩展 / 22 个测试。教训：美化模型要默认走 iShot 风格（padding 也是香槟底色 + 图像居中 + 卡片整体阴影），而非 CleanShot X 浮动模型（padding 透明）。如未来重做 R51，参考 iShot 模型，并默认半径/padding 更大（≥16px / ≥48px）让效果在任何内容上都明显。**

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

## ✅ R23 快捷启动器（已完成，2026-07-18 第二十批落地）

> 用户 2026-07-20 澄清："R23 是什么任务，应该做了吧"——R23 早已作为"快捷启动器"落地（第二十批），不再是"快捷命令"。
> 历史这里标"⏸️ 暂缓"是 R3 时代遗留的错误表述：最早 R3 提到"快捷触发脚本"时暂缓过，后来作为 R23 落地实现，但本节标题没更新。

### 已落地内容
- **QuickTools 第 5 区** + **设置页第 5 分区**两个入口。
- 自动提取 exe 图标（32 个测试通过，4 个应用实测有效 PNG）。
- 支持 `{clip}` `{sel}` `{prompt:提示语}` 三个参数 token。
- 第二十批落地，第三十三批加 7 个应用（A HUB / CC Switch / RK Keyboard / QQ / 微信 / 微信输入法 / KeySilk）+ ChatGPT 桌面端更名 Codex。
- 详见 `docs/architecture/09-launcher.md`。

### 剩余可选增强（非紧急，2026-07-20 backlog）
- 图标缓存落盘（目前每次启动重提，零功能影响）。
- UWP / CLI 命令支持。
- 多 prompt UX 优化。

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

#### ✅ R48 — 标注工具集（rectangle / ellipse / arrow / pen / highlight）（已完成 v2，2026-07-20 第四十三批）

- **触发**：R47 标注模式（按 A 进入）扩展 6 种工具（按 0-5 切换）。0=NumberedBadge（默认，R47 序号），1=矩形 / 2=椭圆 / 3=箭头 / 4=画笔 / 5=高亮。
- **代码量**：v2 ~1700 行（5 工具 sealed records + AnnotationSession 统一 undo stack + IMouseHook.MouseMove 扩展 + 实时拖拽预览 + BGRA 烧入 Bresenham 算法 + 25+20 测试）。
- **依赖**：**无新增**（避开 SkiaSharp，节省 ~2MB AOT 体积）。
- **v1 → v2 重做教训（永久记录）**：v1 worker 三个失败模式都源自"想当然"——
  1. **没有拖拽实时预览**（v1 假设只在 LeftButtonUp 才画，看不到拖拽过程）→ v2 用 `_livePreviewShape` 字段跟踪 + 每次 MouseMove 更新
  2. **画笔/高亮路径记录失效**（v1 用 DispatcherTimer 在 hook 线程创建跨线程失效）→ v2 扩 `IMouseHook.MouseMove` 事件（修改 platform abstraction，根本解决）
  3. **Arrow 撤销孤儿**（v1 给 line+head 两个 children 都标 AnnotationTag(1)）→ v2 改 Tag(2) + 6 个回归测试覆盖
- **v2 落地后 4 轮调试修复（永久记录）**：
  - 第 1 轮：吸附距离太近 → 阈值 8→24 物理像素
  - 第 2 轮：磁吸距离还太近 + 箭头/画笔/高亮看不到 → 24→48；发现 Line/Polyline 用 `Canvas.SetLeft(dipX)` 会**双偏移**（Points 本身就是绝对坐标），改 Line/Polyline 设 0
  - 第 3 轮：磁吸太远 + 画笔/高亮变封闭图形 + 序号偏左上 → 48→32；`Polyline.Fill = Brushes.Transparent` 在 Avalonia 仍渲染闭合多边形 → 改 `Fill = null`；TextBlock 加 HorizontalAlignment/VerticalAlignment/TextAlignment = Center
  - 第 4 轮：磁吸还远 + 保存 PNG 没烧入 → 32→20；**烧入真因 4 轮才定位**：`Avalonia 12.1 Bitmap.CopyPixels` 对 capture 出来的 PNG 100% 抛 `ArgumentOutOfRangeException('stride')`，worker 的 catch 块吞了异常返回 null → BurnAnnotationsIntoPng 直接 return 原 PNG。**修复**：capture 时同时保留原始 BGRA buffer（`CaptureAsPngAndBgra`），`BurnAnnotationsIntoPng` 直接用 buffer 跳过 Avalonia decode，buffer 克隆避免重复保存叠加。
- **验收（v2 终态，2026-07-20）**：
  - Debug build 0 警告 0 错误。
  - **316/316 测试通过**（worker 25 + reviewer 20 个 v1 回归测试 + 271 基线）。
  - NativeAOT Release publish **0 trim/AOT 警告**。
  - exe 27,711,488 → **27,786,240 字节**（+75KB，远低于 +80KB 预算）。
- **关键架构教训（永久）**：(1) Avalonia 12 的 `Bitmap.CopyPixels` 对某些 PNG 不可靠，**永远优先用 capture 时保留的原始 buffer**，不要 round-trip PNG。(2) `Polyline.Fill = Brushes.Transparent` ≠ 不填充——必须 `null`。(3) Line/Polyline 的 `Points/StartPoint` 是绝对坐标，`Canvas.SetLeft` 会双偏移。(4) DispatcherTimer 在非 UI 线程创建会跨线程失效，必须 UI 线程构造或直接用 hook 路由。
- **代码量**：~400 行（5 个工具的 hit-test + draw + undo/redo stack）。
- **依赖**：无新增（纯 Avalonia Canvas + Skia）。
- **资源开销**：常驻 0。
- **验收**：每种工具拖拽即画；Shift 修饰键约束（矩形→正方形、椭圆→圆、画笔→直线）；undo/redo 完整；保存 PNG 时烧入。
- **关键点**：与 R47 数字 badge 共用同一 Canvas + undo stack。

#### R49 — 截图相册（screenshot gallery）✅ 已完成

- **触发**：Ocean Eyes 工具栏按 **G**（Gallery）→ 弹出 `GalleryWindow.axaml(.cs)` 标准应用窗口，缩略图瀑布流浏览 `%USERPROFILE%\Pictures\Ocean Eyes\` 下所有 `ocean-eyes-*.png`。**不退出 Ocean Eyes**（同 P 贴图模式）——用户可关掉相册继续操作当前会话。
- **代码量**：~440 行（`ScreenshotGalleryLoader.cs` 100 + `GalleryWindow.axaml(.cs)` 230 + `SelectionRuntime.cs` 编辑 60 + 9 个单元测试 80）。
- **依赖**：无新增（Avalonia 12.1 `Bitmap.DecodeToWidth` + `ItemsControl` + `WrapPanel`）。
- **资源开销**：常驻 0；打开时一次性扫描目录（后台线程），缩略图并行加载（4 路并发，`Parallel.ForEach`）。
- **验收（v1 终态，2026-07-20）**：
  - Debug build 0 警告 0 错误。
  - **326/326 测试通过**（Core 250 + Providers 35 + Windows.Integration 41，含 R49 新 9 个）。
  - NativeAOT Release publish **0 trim/AOT 警告**。
  - exe 27,786,240 → **27,920,896 字节**（+134KB，超 100KB 预算 34KB，**已记录例外**——见下文）。
  - **双击缩略图 → 全屏预览 overlay**（半透明遮罩 + 大图居中，可滚动；点击空白处/Esc 关闭；底部按钮：复制/删除/打开目录）。
  - **右键缩略图 → 上下文菜单**：复制到剪贴板 / 查看大图 / 删除文件 / 在资源管理器中显示。
  - **托盘菜单 → "Open Screenshot Gallery"**：冷启动入口，不需进 Ocean Eyes 也能开。
  - Delete 键 → 直接 `File.Delete`（v1 不进回收站，v2 可加 `SHFileOperation` + `FOF_ALLOWUNDO`）。
  - Enter 键 → 打开预览（同双击）。
  - Esc 有两级：先关预览，再关窗口。关掉相册后 Ocean Eyes 工具栏仍可用。
- **关键设计决策**：
  - **G 不退出 Ocean Eyes**（与 P 一致），区别于 T 贴图/B 美化（terminal action）。理由：相册是"看历史"，与"当前会话"解耦；用户关掉相册可能想继续标注/翻译/贴图当前刚截的那张。
  - **双入口并存**：托盘右键 "Open Screenshot Gallery"（主入口，冷启动可用，不依赖 Ocean Eyes 会话）+ 工具栏 G（截图时快捷查看）。两个入口共用同一个 `ShowGallery()` 方法（公开 public 让 App 层能调），单例窗口（再开就聚焦旧窗口）。
  - **双击 = 查看大图**（不是复制）——遵循图片网格通用约定。复制走右键菜单 / 预览底部按钮。
  - **预览用同窗遮罩层**（lightbox）而非独立窗口——`Panel` 套住主 `Grid`，加一个 `PreviewOverlay` Border，半透明黑底（#E6000000）+ `ZIndex=100`。打开时 `IsVisible=true`，关闭时 `IsVisible=false` + Dispose bitmap。Esc 两级：先关预览，再关窗口。
  - **右键菜单用标准 `<ContextMenu>` + `<Separator/>`**——不用手写 PointerPressed 右键判定。菜单项通过 `Tag="{Binding}"` 把 ViewModel 传给 click handler。
  - **缩略图加载用 `Bitmap.DecodeToWidth(stream, 172, HighQuality)`**（不是 `CreateScaledBitmap`）——文档明确说前者更高效（解码时直接降到目标分辨率，跳过全分辨率像素缓冲）。
  - **大图预览用 `new Bitmap(stream)` 全分辨率加载**——4K 图也只在打开预览时解码，不在网格加载阶段。
  - **三个事件回调**：`RequestCopy` / `RequestDelete` / `RequestReveal`。`GalleryWindow` 在 UI 层（`SelectionAssistant.UI`），不能引用 `Platform.Windows`（架构分层）。所有 OS 调用（剪贴板 / Explorer /select / 日志）由 `SelectionRuntime` 订阅处理。
  - **DisplayName 本地化**：今天/昨天/周X/yyyy-MM-dd HH:mm 四级相对时间，从文件名 `ocean-eyes-yyyyMMdd-HHmmss` 解析时间戳（解析失败回退 `File.GetLastWriteTime`）。
- **超 100KB 预算的原因**：GalleryWindow 引入了 Avalonia 的 `ItemsControl` + `WrapPanel` + `DataTemplate` + `ContextMenu` + `MenuItem` + `Separator` + `INotifyPropertyChanged` + `Parallel.ForEach` + `Bitmap.DecodeToWidth` 这些新的 AOT 分析路径和反射元数据。这些是 Avalonia 数据绑定/菜单系统的固有成本，与功能本质绑定，无法压缩。+134KB 中约 +110KB 是 Avalonia 路径的 trim 元数据，~24KB 是新代码本身。
- **预览缩放/平移的 8 轮调试教训（永久记录，2026-07-21）**：
  - **需求**：双击缩略图 → 大图预览，滚轮缩放（光标锚定）、左键拖动平移。看似简单的功能，在 Avalonia 12.1 NativeAOT 上踩了 5 个独立坑，迭代 8 轮才修好。
  - **坑 1：ScrollViewer 接管滚轮**。`ScrollViewer > Image Stretch="Uniform"` 看似天然 fit，但 Extent > Viewport 时 ScrollViewer 的 ScrollContentPresenter class handler 强制把滚轮变成垂直滚动，`e.Handled = true` 在 bubbling 阶段挡不住。**结论：滚轮 = zoom 的需求下，永远不用 ScrollViewer**。
  - **坑 2：TransformGroup children 顺序**。两个 children `[Scale, Translate]`，组合矩阵是 `Scale · Translate`，按矩阵结合律 = 先 translate 再 scale（在 image space），但所有数学假设的是先 scale 再 translate（在 viewport space）。**结论：要么 swap children 顺序，要么用单一 `MatrixTransform`/`TransformOperations` 完全自控**。
  - **坑 3：LayoutTransformControl 不支持 Translate**。它的 `ArrangeOverride` 公式 `-transformedRect.X + (finalSize - transformedRect)/2` 把 matrix 的 M31/M32 显式抵消（**设计行为，不是 bug**）。Scale 能用，Translate 永远不生效——拖拽时 matrix 数字在变但视觉不动 + 闪烁。**结论：LayoutTransformControl 只用于 Scale/Rotation/Skew，pan 必须用 RenderTransform**。
  - **坑 4：Avalonia 12 的 Image 即使 `Stretch="None"`，layout box 也被父容器约束**。诊断日志显示 `Image.Bounds=900×546`（viewport 大小）而不是 bitmap 的 `2312×1563`——bitmap 内部被静默 fit 进 layout box，导致 RenderTransform 的 Scale 变成**第二次缩放**。**结论：Image 必须包在 `Canvas`（无限 available space）里，Bounds 才会等于 bitmap 真实 DIP 大小**。
  - **坑 5：Avalonia 12 NativeAOT 上 `MatrixTransform` 静默失效**。`Image.RenderTransform = new MatrixTransform(matrix)` 在 Debug 跑得动，NativeAOT publish 后不生效（图片按原始像素 1:1 渲染）。**结论：必须用 `TransformOperations.Builder(1).AppendMatrix(matrix).Build()`，这是 Avalonia 12 的现代 API，MatrixTransform 是老的不可靠**。
  - **坑 6：Avalonia 12 的 `Matrix.operator *(a, b)` 语义反转**。文档说是 "multiplies two matrices"，但 pan 日志显示 `Translate(dx, dy) * _matrix` 实际让 `M31 += dx * m11`（dx 被 zoom 缩放），而非预期的 `M31 += dx`。**结论：要 post-multiply translate，写 `_matrix * translate`（反向）**。这条最阴险——矩阵代数直觉会害你。
  - **最终架构**（PanAndZoom `ZoomBorder` 模式）：
    ```
    Border ClipToBounds=True (viewport)
      └─ Canvas (infinite available space, 让 Image 真实 layout)
           └─ Image Stretch="None" HorizontalAlignment=Left VerticalAlignment=Top
                 RenderTransformOrigin = RelativePoint(0, 0, RelativeUnit.Relative)
                 RenderTransform = TransformOperations.Builder.AppendMatrix(_matrix).Build()
    ```
    - `_matrix`：单一 `Matrix` struct（M11/M12/M21/M22/M31/M32），同时管 scale + translate，无 TransformGroup 坑。
    - `FitToWindow`：算 `zoom = min(vw/iw, vh/ih)`，居中 pan = `vw/2 - (iw/2)*zoom`。
    - Wheel zoom-at-cursor：`cursor = _matrix.Inverse.Transform(e.GetPosition(viewport))`，`_matrix = ScaleAt(ratio, ratio, cursor.X, cursor.Y) * _matrix`。
    - Pan：`_matrix = _matrix * Translate(dx, dy)`（注意是 `_matrix * translate`，不是 `translate * _matrix`）。
    - `ApplyMatrix`：`TransformOperations.Builder.AppendMatrix(_matrix).Build()` + `InvalidateVisual()`。
  - **诊断方法**：在 `FitToWindow`/`wheel`/`pan`/`ApplyMatrix` 里临时加 `_logger.Info` 打印 matrix 值 + Image.Bounds，让用户跑一次后读 `%LOCALAPPDATA%\BYH\logs\BYH.log`。**用户多轮报"还是不对"时，停止猜测，让数据说话**。
- **未做（留 v2）**：Shift 多选删除；搜索框（按文件名/OCR 文本过滤）；回收站删除（`SHFileOperation` + `FOF_ALLOWUNDO`）；1000+ 图虚拟化（当前 `ItemsControl` + `WrapPanel` 不虚拟化，>200 张会有滚动卡顿）；预览的左右翻页键（←/→ 在预览中切换上一张/下一张）；预览缩放的双击放大/重置。

#### R50 — 带壳截图（device mockup）

- **触发**：Ocean Eyes 工具栏按 **M**（Mockup）→ 弹出外壳选择菜单（MacBook / iMac / iPhone / Android / Browser），选中后用 Skia 把截图合成到外壳模板里，复制到剪贴板。
- **代码量**：~250 行（外壳加载 + Skia 合成 + 菜单）。
- **依赖**：无新增（外壳是 PNG 资源 + Skia draw image）。
- **资源开销**：常驻 0；每个外壳模板 PNG ~50-200KB 打包进 `avares://`。
- **验收**：截图自动缩放到外壳"屏幕区域"；输出 PNG 透明背景；常用 5 个外壳（MacBook / iMac / iPhone 15 / Pixel / Edge browser）。
- **关键点**：外壳模板需自己画或下载免费授权的——不要从其他截图软件扒。

#### R51 — 截图美化（padding + shadow + rounded corners）❌ 已撤销

> **2026-07-21 实施 → 同日撤销**（第四十五批）。用户真机测试后判定"美化了啥 / 不搞了"。
> 落地版用了 CleanShot X 风格的浮动截图模型（padding 透明 + 阴影投射），对深色内容截图美化效果几乎不可见——背景色被不透明原图完全遮盖，阴影 RGB 与深色内容相近。
> **教训**：若未来重做，默认应走 **iShot 风格**（padding 也是香槟底色 + 图像居中 + 卡片整体阴影），并加大默认半径/padding（≥16px / ≥48px）让效果在任何内容上都明显。规格与撤销详情见本批头部更新。

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

#### R53 — 长截图（scrolling screenshot with stitch）❌ 已撤销

> **2026-07-22 实施 → 同日撤销**（第四十八批）。用户真机测试后判定"弹窗有啥用 / 不做了"。
> 落地版经历了 4 轮 UX 模型迭代，踩了 3 个架构陷阱：
> 1. **Ocean Eyes 全屏 RegionSelectOverlay 吞鼠标滚轮事件**——overlay 是全屏 Topmost 窗口，root Canvas 可命中测试但无 PointerWheelChanged handler，滚轮事件被静默丢弃，用户无法滚动目标。
> 2. **DismissOverlay→Cancel()→RegionCancelled→ResetForRedraw() 事件链**——调 `DismissOverlay` 隐藏 overlay 会触发 `RegionCancelled`，进而 `ResetForRedraw()` 清零 `_oceanEyesActive` + 禁用 hook，导致按键全失效。只能改用 `_annotationOverlay?.Hide()` 直接隐藏（不触发 Cancel），但这不够直观。
> 3. **WS_EX_NOACTIVATE 窗口无法接收键盘焦点**——浮动工具条不抢焦点是正确设计，但 Space/Enter/Esc 必须通过全局 hook 路由，增加了复杂度。
>
> **根因总结**：长截图这类"需要边操作目标窗口边截屏"的功能，与 BYH 的全局 hook + 全屏 overlay 架构根本冲突——overlay 不隐藏则吞鼠标事件，隐藏则走 Cancel 事件链杀掉 Ocean Eyes 会话。用户要的不是"另一个窗口"，而是滚轮自动截帧的无感体验，这需要更底层的重构。
>
> **若未来重做**：需先重构 RegionSelectOverlay 支持"鼠标事件穿透"模式（Win32 `WS_EX_TRANSPARENT` 让鼠标事件直达下层窗口，或 Avalonia `IsHitTestVisible=False` 的全屏透明层），而非 Hide/Show 切换。在此基础上，纯状态机模型（L 进入模式 → 滚轮自动截帧 → Enter 保存 → Esc 退出）是正确方向——撤销前的最后一版已经走到了这个架构，只差 overlay 穿透这一步。stitcher 算法本身（ShareX 风格逐行字节重叠匹配）已验证可用，可复用。
  - ❌ OCR / PDF 导出留 v2/v3。
- **代码量**：~550 行（`LongScreenshotStitcher.cs` 130 + `LongScreenshotWindow.axaml(.cs)` 250 + `SelectionRuntime.cs` 编辑 100 + 8 个单元测试 130）。
- **依赖**：无新增（stitch 纯 `Buffer.BlockCopy` + `for` 循环；截图复用 `ScreenRegionCapture.CaptureRawBgra`；PNG 编码复用手写 `PngEncoder`；预览用 `WriteableBitmap` + `Lock/Marshal.Copy`）。
- **资源开销**：常驻 0；运行时 stitch 每帧 ~50-150ms（1080p，C# 逐字节，后台 `Task.Run` 不卡 UI）。v2 换 `memcmp` 后降到 ~10ms/帧。
- **验收（v1 终态，2026-07-22）**：
  - Debug build 0 警告 0 错误。
- **撤销的代码**：`LongScreenshotStitcher.cs`（纯函数 stitcher，可复用）/ `LongScreenshotWindow.axaml(.cs)`（标准窗口 → no-activate 浮动工具条，已删）/ L 键分支 / mouse hook 的 `WM_MOUSEWHEEL` 捕获 + `MouseMessageType.MouseWheel` 枚举 + `MouseEventData.WheelDelta` 字段 / 8 个 stitcher 单测。
- **可复用的资产**：
  - **stitcher 算法**（ShareX 风格逐行字节重叠匹配）已验证可用，逻辑正确，单测全过。未来重做可直接拿回。
  - **4 轮迭代经验**记录了所有踩过的坑（overlay 吞事件 / Cancel 事件链 / no-activate 焦点），避免重蹈覆辙。
- **算法**（已验证，记录备查）：移植 ShareX `ScrollingCaptureManager.CombineImages`——找 canvas 底部与 frame 顶部的最大连续匹配行段，追加非重叠尾部。左右裁 5% 容忍侧栏漂移。完全匹配失败 → 兜底整帧追加。O(overlap × width × height)，1080p ~50-150ms/帧（C# 逐字节），v2 换 `msvcrt.memcmp` 降一个数量级。
- **参考实现**：
  - `ShareX/ShareX.ScreenCaptureLib/ScrollingCaptureManager.cs`（金标准，~25k stars，stitch 算法来源）。
  - `Susskind2/ScrollCapture`（C#/WinForms，更简单；零拼接只导 PDF）。

---

### ✅ R55 — 置顶截图四周立体阴影（large drop shadow on pinned window）

- **触发**：Ocean Eyes 截图后按 **T** 贴图 → 浮动置顶窗口四周加大范围柔和投影，看起来像悬浮在桌面上方的卡片（iShot/CleanShot X 风格）。
- **v1 范围（用户 2026-07-22 选定"大投影立体感"）**：
  - ✅ **AXAML 样式变更**：把承载图片的 `Frame` Border 包进一个新的外层 Border，外层 `BoxShadow="0 12 32 0 #66000000"`（offset y=12、blur=32、spread=0、黑 alpha=40%）+ `Margin="24"`。原 `Frame`（CornerRadius=8, ClipToBounds=True）嵌入其内不变。
  - ✅ **吸附计算修正**（初版误判"吸附边界落在阴影边缘是预期行为"，用户实测发现图片离屏幕/彼此边缘永远隔 24px gap）：拖拽/吸附改为对 **IMAGE rect**（窗口 rect 四边各内缩 `ShadowMarginDip×RenderScaling`）运算，再把吸附后的 image 左上角转回 window 左上角。runtime 的 `GetOtherPinnedBounds` 改报 peer 的 image rect（新公开属性 `PinnedScreenshotWindow.ImagePhysicalRect`）。
- **核心技术约束（Avalonia 12.1）**：
  - `BoxShadow` 只在元素边界**内**绘制，受 `ClipToBounds` 裁剪。frameless 透明窗口 `SizeToContent=WidthAndHeight` 时窗口精确等于图片尺寸，图片紧贴窗口边缘 → 阴影无渲染空间会被裁掉。
  - **阴影渲染解法**：外层 Border 加 `Margin="24"` 让窗口在四周各扩大 24px（透明区），阴影画在这个透明区内。窗口因 `SizeToContent` 自动扩大到 `图片 + 48px`。外层 Border **不设 ClipToBounds**（默认 false），否则阴影被裁。
  - **吸附 gap 解法**：窗口变大后吸附若仍用 `ClientSize`（= 窗口 = 图片+48px），图片永远离目标边缘 24px。改用 image rect 运算。inset 纯函数放 `MagneticSnapCalculator.InsetRect`（可单测），window 的 `ImageRectForWindow` 委托给它。
  - 外层 `CornerRadius="10"` 略大于内层 `8`，让柔和阴影贴合圆角。
- **连带影响（实际需要改的）**：
  - `SizeToContent=WidthAndHeight` → 窗口自动 = 图片 + 48px（无需改）。
  - 拖拽（`OnPointerMoved/Released`）→ **改**：算 image rect 喂 `ComputeSnap`，吸附结果 `-margin` 转回 window 坐标。
  - 吸附（`GetOtherPinnedBounds`，runtime 回调）→ **改**：报 `w.ImagePhysicalRect`（peer image rect），两个贴图 image 边贴 image 边无 gap。
  - 吸附引导线（`UpdateSnapGuideCanvas`）→ **改**：线画在 image 边缘（`ShadowMarginDip..w-m`），不画在阴影边缘。
  - `ApplyScale`（DPI baseline `1/RenderScaling`）只缩放 Scaler 内部图片，窗口/阴影边框尺寸不变（无需改）。
  - 初始位置（region 左上 +16）、滑入动画 `_slide=(400,100)→(0,0)`、双击检测（`DoubleClickPx=8`）、ContextMenu 全部不变（无需改）。
- **`Frame` 在 code-behind 从未被引用**（只是 axaml 的 x:Name）→ 改其结构安全。
- **与 R51（已撤销）的区别**：R51 截图美化的阴影"看不出"，根因是 CleanShot X 模型的 padding 透明色被不透明原图遮盖、阴影 RGB 与深色内容相近。本次针对**贴图浮窗**（已在桌面上的 PNG），投影落在**桌面背景**上（不透明图像四周全是桌面），一定可见，与 R51 那种 padding 填色被原图遮盖的情况本质不同。
- **NativeAOT 安全**：`BoxShadow` 是 Avalonia 内置 styled property，项目已大量使用（IvoryJade.axaml 15+ 处、GalleryWindow.axaml），12.1 已验证可用。无新依赖、无 P/Invoke、无反射。**0 AOT 风险**。
- **验收（2026-07-22 第四十九批，含吸附修正）**：
  - `dotnet build` → **0 警告 0 错误**（NU1900 漏洞库拉取 EOF 是网络瞬时问题，`-p:NuGetAudit=false` 即 0 警告，非代码问题）。
  - **255/255 测试通过**（Core 250 + 新增 5：3 个 `InsetRect` + 2 个吸附 gap 回归）。
  - NativeAOT Release publish **0 trim/AOT 警告**。
  - exe 27,925,504 → **27,926,528 字节（+1,024B = 1KB，远低于 5KB 预算）**。
- **总改动**：AXAML +1 层 Border（~10 行）+ C# 适配层修正（`PinnedScreenshotWindow.axaml.cs` +`ImagePhysicalRect`/`ImageRectForWindow`/吸附坐标转换/引导线 inset，~40 行；`SelectionRuntime.cs` `GetOtherPinnedBounds` 改 4 行；`MagneticSnapCalculator.cs` +`InsetRect` 纯函数 10 行；测试 +5 个 ~70 行）。

---

### ✅ R56 — 贴图浮窗缩放弹出动画（scale pop-in + pop-out，timer + TransformOperations）

- **触发**：用户"改一下贴图的动画,改成简单弹动效果"。Ocean Eyes 截图后按 **T** 贴图，浮窗开启从 R46 v13 的**侧面滑入**改为**缩放弹出**（卡片从 0.85 弹到 1.0，轻微过冲后落定，BackEaseOut 350ms）；关闭也对称改成缩走（1.0→0.85 CubicEaseIn 200ms + 淡出）。用户从三选里选了"缩放弹出（推荐）"，确认后又要求关闭也用同款。
- **终态架构（v4）**：
  - **Pop-IN**：`0.85 → ~1.05 过冲 → 1.0`（BackEaseOut 350ms）。**Pop-OUT（关闭）**：`1.0 → 0.85`（CubicEaseIn 200ms）+ Opacity 淡出（Window.Transitions 250ms）。两者复用同一套机制：一个 `DispatcherTimer`（16ms，`DispatcherPriority.Render`），按 `PopDirection` 枚举（In/Out）选 duration + easing + scale 公式。
  - **绕过 RenderTransformOrigin**：每帧把 eased scale 烘进**中心缩放矩阵** `Matrix(scale,0,0,scale,(1-scale)·w/2,(1-scale)·h/2)`（即 translate(-w/2,-h/2)×scale×translate(w/2,h/2)，中心偏移在矩阵里硬算）。
  - **NativeAOT 安全的 transform API**：`TransformOperations.Builder(1).AppendMatrix(matrix).Build()` 推到 `OuterShadowBorder.RenderTransform`——**不用** plain `MatrixTransform`（§R47/R49 记录它在 NativeAOT 静默失效）。
  - **从 LayoutUpdated 启动 pop-in**（不在 Opened 启动）。
  - **打开时 DPI 基准瞬间到位**：`ApplyScale(animate:false)`。
- **⚠️ 踩了 4 个坑才成（4 轮迭代，每轮都有用户真机反馈 + BYH.log 诊断）**——这段记录的教训比代码本身更值钱：

  **v1（ScaleTransform + RenderTransformOrigin）—— 用户"闪烁的弹两下,和之前一样"。**
  - 方案：外层 Border 加 `RenderTransformOrigin=0.5,0.5` + `<ScaleTransform 0.85>`，DoubleTransition + BackEaseOut。
  - **错误判断**：以为 R55 新增的外层 Border 能让原点居中。**handoff §R46-22 的 v11 早已证伪**：把 ScaleTransform 从 Window 移到内部 Border，原点仍失效（scale 从左上角长）。我没读日志就重蹈覆辙。
  - **教训**：改动画前**必读 §R46-22**——scale 类动画在 BYH 的 frameless `ExtendClientAreaToDecorationsHint` 窗口上不能用 RenderTransformOrigin（Window 或 Border 上都不行）。

  **v2（timer + TransformOperations 中心矩阵，但 Ease 当绝对值）—— 用户"确实有回弹了,但总是从屏幕右下角滑出来,不是从中央弹"。**
  - 方案对了一半（绕过 RenderTransformOrigin 用矩阵），但 `OnPopTick` 把 `BackEaseOut.Ease(progress)` 的返回值**直接当绝对 scale**。BackEaseOut 是**进度函数**（0→1，末段 overshoot ~1.1），不是目标值。第一个 tick `progress≈0.046→Ease≈0.21`，卡片瞬间从 0.85 **暴跌到 0.21**，中心矩阵偏移把小卡片往右下角推 = "从右下角滑出来"。
  - **教训**：缓动函数的返回值是**进度**，要插值进 `[起值, 终值]` 区间，不能当绝对值。

  **v2.1（Ease 插值进 [0.85,1.0]）—— 用户"更丝滑了,但还是侧面划入"。**
  - 修了 v2 的数学（`scale = 0.85 + 0.15×Ease(progress)`），但**只是更顺了，根因没解**。这一版加了诊断日志（§R47 "停止猜测"原则），让用户复现后读 `BYH.log`。

  **v3/v4（读日志定位真正根因）—— 日志揭示 Bounds 在动画全程从物理像素 822 缩到 DIP 470，比例 = RenderScaling=1.75，耗时 ~120ms。**
  - **真正根因**：`ApplyScale`（设 DPI 基准 `_scaleTransform=1/RenderScaling`）的 ScaleTransform 上挂着 R46 v7 加的 **120ms DoubleTransition**。所以窗口打开时 DPI 校正在 120ms 里慢慢缩，`OuterShadowBorder.Bounds` 实时变化（822→470），我的 pop 动画每帧都拿**正在变的 Bounds** 算中心矩阵 → 中心基准漂移 = "侧面滑入"。
  - **v3 修**：pop 从 `LayoutUpdated` 启动（不在 Opened；Opened 时 Bounds 还是物理像素 927×533@153,71，LayoutUpdated 后才是 DIP 530×305@24,24），timer 提到 `DispatcherPriority.Render`。**只解决了"首帧 Bounds 错"的表象，Bounds 仍在缩** → 用户"还是侧面"。
  - **v4 修（真正的解）**：`ApplyScale(animate:false)`——打开时 DPI 基准**瞬间到位**（临时摘 transition 再设值），Bounds 立即稳定在真实 DIP 尺寸，pop 第一帧就在正确基准上跑。日志确认 v4 后 bounds 从 tick#1 到 tick#10 完全不变（497.1×127.4 稳定）。**用户："好了!"**
  - **教训**：DPI 基准是**校正**不是用户可见缩放，不该动画；动画 transition 只该留给用户可见的滚轮缩放。诊断日志（§R47 "停止猜测"）是唯一可靠定位手段——4 轮里前 3 轮都在猜。

- **关键技术约束（最终）**：
  - `RenderTransform` 纯视觉，不改 layout/hit-test/Bounds → 拖拽（Position+ClientSize）和 R52 磁吸（ImagePhysicalRect）**完全不受影响**。
  - `_popTimer` 的 RenderTransform（弹动）与 `_scaleTransform`（Scaler 的 LayoutTransform，DPI/wheel-zoom）是两套独立 transform；`ApplyScale(animate:false)` 只在打开时摘 transition，滚轮缩放仍 `ApplyScale()`（默认 animate:true）平滑。
  - pop-in 从 `LayoutUpdated` 启动（`_popPending` flag + 一次性消费），不在 Opened。
  - pop-out（关闭）`StopPopTimer` 不清 transform（避免缩走后闪一下全尺寸再消失），留给 Hide 直接收。
  - R55 阴影随外层 Border 一起 scale（自然，阴影和卡片一起 pop）。
- **连带影响（逐项确认，全部安全）**：拖拽不变 / R52 磁吸不变 / 初始位置（region 左上 +16）不变 / 双击检测（DoubleClickPx=8）不变 / ContextMenu 不变 / 滚轮缩放平滑不变（ApplyScale 默认 animate）。
- **诊断代码**：开发期临时加了 `RedactedLogger _diag` + `_popTickCount` + START/tick#n 日志（写 BYH.log），**v4 确认后已全部删除**（连 using 一起）。
- **`anim:` 命名空间**：仍被 `<Window.Transitions>` Opacity transition 使用，保留。
- **NativeAOT 安全**：DispatcherTimer / TransformOperations / Matrix / BackEaseOut / CubicEaseIn 全是 Avalonia 12.1 内置（已验证 Avalonia.Base.xml 含全套 easing）。DispatcherTimer 项目大量用（ColorPickerLoupe/GalleryWindow/App/SelectionRuntime），TransformOperations GalleryWindow 在用。无新依赖、无 P/Invoke、无反射。**0 AOT 风险**。
- **验收（2026-07-22 第五十批，v4 终态，已用户确认"好了"）**：
  - `dotnet build` → **0 警告 0 错误**。
  - **255/255 测试通过**（Core，回归无破坏）。
  - NativeAOT Release publish **0 trim/AOT 警告**。
  - exe 27,926,528 → **27,928,064 字节（+1,536B = 1.5KB，远低于 5KB 预算）**。
  - ✅ **动画正确性已用户真机确认**（不再是"待验证"）：开启从中央弹 + 轻微过冲落定，关闭从中央缩走 + 淡出，PopIn/PopOut 日志均确认 bounds 稳定、scale 曲线正确。
- **总改动**：AXAML ~15 行（含注释）+ CS ~110 行（含注释 + pop in/out helper + ApplyScale animate 参数）。0 新依赖、0 新测试、0 新 P/Invoke。诊断日志代码已删。

---

### ✅ R54 — 剪贴板历史（clipboard history with smart auto-grouping，v1 已落地 2026-07-22）

> **独立于 Ocean Eyes 系列**：R44-R53 都是"Ocean Eyes 框选时触发的临时动作"，零常驻；R54 是**常驻型**功能（监听器 24/7 运行 + 历史缓存），是唯一会突破"常驻开销 ≈ 0"契约的 backlog 项。
> 来源：用户 2026-07-20 提出"如果把剪贴板功能加进来，资源影响怎么样，剪贴板历史、分组等"——目前用 CopyQ（Qt C++，~80-100MB），希望 BYH 整合更轻量的替代。

#### 社区调研（2026-07-20，5 个项目对比）

| 项目 | 技术栈 | Stars | 内存 | 分组 | 图片 | 关键卖点 |
|---|---|---|---|---|---|---|
| **CopyQ**（用户在用）| Qt C++ | 9.5k | ~80-100MB ⚠️ | ✅ Tab 式 | ✅ | 脚本最强；最重 |
| Ditto | C++/C | 6.7k | ~30-50MB | ✅ Group | ✅ | Windows 老牌 |
| **Ortu** | Rust+Tauri+Svelte | 32 | ~15-25MB | ✅ **Smart auto-group** | ✅ | 现代化+轻量+规则引擎 |
| Maccy (Mac) | Swift | — | ~5MB | ❌ | ✅ | 极简 |
| Win+V | 内置 | — | ~10MB | ❌ | ✅ | 零安装 |

**CopyQ 痛点（BYH 要解决的）**：Qt 框架重 → 80-100MB RAM；冷启动 1-3s；EXE ~80MB；Tab 式分组要手动切；与 BYH 工作流割裂（划词/截图/序号都不进历史）。

**借鉴各家最佳实践**：
1. **Ortu Smart auto-grouping**（最值得抄）— 规则引擎自动归类，零摩擦
2. **CopyQ Pin/Favorite** — 重要条目置顶，LRU 不删 pinned
3. **Ditto SQLite + FTS5** — 全文搜索 < 5ms（v2 才用，v1 走 JSON）
4. **Ortu Paste Stack** — 队列多条按序粘贴（高级可选）
5. **Maccy 极简弹出** — 全局快捷键弹出 → 输入即过滤 → Enter 粘贴 → 自动隐藏

#### 资源影响实测（2026-07-20，基线已实测）

**当前 BYH 进程内存（未加 R54）**：
```
PID=42600  WorkingSet=123.4MB  PrivateMem=200.1MB  VirtualMem=70.5GB  Uptime=1.1h
```
123MB 含完整 Ocean Eyes 功能集（截图/翻译/取色/贴图/标注/UIA hook/Provider HTTP 客户端）。

**加 R54 后预估**：
| 配置 | 内存增量 | 最终 WorkingSet | 涨幅 |
|---|---|---|---|
| + 1000 条文本 | +0.7MB | 124.1MB | +0.6% |
| + 1000 文本 + 50 图（缩略图）| **+3MB** | **126.4MB** | **+2.4%**（用户感知不到）|
| + 1000 文本 + 200 图 | +10MB | 133MB | +8% |
| 极端：+ 5000 文本 + 500 图 | +30MB | 153MB | +24% |

**横向对比（同等功能集）**：
| 程序 | WorkingSet | 备注 |
|---|---|---|
| Win+V | ~50-100MB | 系统内置 |
| **BYH 当前** | **123MB** | 含截图/翻译/取色/贴图/标注全套 |
| **BYH + R54（1000文+50图）** | **126MB** | 涨幅 +2.4% |
| CopyQ | 80-100MB | 只做剪贴板 |
| Ortu | 15-25MB | 只做剪贴板 |
| Ditto | 30-50MB | 只做剪贴板 |

BYH 在功能集远超 CopyQ 的情况下只多 23MB，加 R54 后**仍只比 CopyQ 重 50%**，但功能多一倍。

#### 图片的"双重代价"分析（永久记录，未来类似功能复用）

| 图片尺寸 | PNG 磁盘 | 解码后内存（BGRA）|
|---|---|---|
| 1920×1080 截图 | 1-3MB | **~8MB** ⚠️ |
| 800×600 | ~200KB | ~2MB |
| 200×200 | ~20KB | ~160KB |

**关键**：PNG 压缩，解码到内存是未压缩 BGRA（4 字节/像素），**内存比磁盘大 3-5 倍**。
→ 必须用"缩略图驻留内存 + 原图按需加载"策略，不能整图驻留。

**3 种存储策略对比**（1000 张 1080p 截图）：
| 策略 | 内存 | 磁盘 |
|---|---|---|
| 全部解码驻留内存（傻办法）| 8GB ⚠️ 会爆 | 2GB |
| 只存磁盘按需加载 | ≈ 0 | 2GB |
| **缩略图缓存（业界标准）✅** | 36MB（96×96 缩略图）| 2GB（原图）|

→ **"限制数量省内存"只在原图驻留时成立；用缩略图后，限制数量主要省磁盘**。

#### R54 v1 规格（待开工）

- **触发**：全局快捷键 **Ctrl+Alt+V**（避开 Win+V，与 BYH 现有 Ctrl+Alt+Q/Space 风格一致）。
- **存储**：JSON（v1）→ SQLite + FTS5（v2 可选，避免 +1.5MB SQLite 依赖）。
- **UI**：复用 SpotlightWindow 模式（Maccy 风格弹出 → 输入即过滤 → Enter 粘贴 → 自动隐藏，Ivory Jade 主题 + 拼音搜索表直接复用）。
- **分组**：**Ortu Smart auto-grouping** 规则集（零摩擦，用户不用手动选 tab）：
  - URL → 链接组
  - 含 `function/class/import/namespace` → 代码组
  - 含 `{...}` 且可 parse → JSON 组
  - 含 `sudo/apt/git/chmod` → shell 组
  - 含 `api_key/secret/token/password/AKIA/private_key` → **敏感组（自动 mask + DPAPI 加密存储）**
  - 纯数字 → 临时组
  - 邮箱 / 手机号 → 联系人组
- **图片**：默认关，设置可开；开启后走"原图存磁盘 + 96×96 缩略图驻留内存"策略。
- **淘汰策略（分级 LRU）**：
  - ✅ Pinned（置顶）→ 永不删（用户标记重要的）
  - ✅ 图片 ≤ 50 张 → 不删
  - 🟡 图片 > 50 张 → 最旧的进待清理队列
  - 🟡 文本 ≤ 1000 条 → 不删
  - 🔴 文本 > 1000 条 → LRU 淘汰（pinned 除外）
  - 🔴 总磁盘 > 500MB → 强制清理最旧图片
- **隐私保护（必须）**：
  - 设置页"排除应用"列表（密码管理器 / 浏览器隐身模式）—— 通过 `GetForegroundWindow` 读源 app 路径跳过
  - "粘贴后立即清除"选项
  - 敏感组自动 DPAPI 加密（复用现有 `SecretStore`）
  - 设置页一键"清空全部历史"
- **代码量预估**：~1000 行
  - 监听器 + 持久化 + 去重 + LRU：~250 行
  - `ClipboardClassifier`（Smart auto-group 纯函数 + 规则集）：~150 行
  - UI（复用 Spotlight 模式）：~200 行
  - 设置页配置区：~80 行
  - 图片缩略图生成 + 缩略图缓存：~100 行
  - 测试：~200 行
- **依赖**：**0 新增**（避免 SQLite；Win32 `AddClipboardFormatListener` P/Invoke ~10KB；DPAPI 已在用；Avalonia WriteableBitmap 已在用）。
- **资源预算**：
  - exe 增量 **< 150KB**（略超 Ocean Eyes 100KB 标准，但 R54 是独立模块不归 Ocean Eyes 管辖）
  - 常驻内存增量 **< 5MB**（默认 1000 文本 + 50 图配置）
  - 磁盘 **< 500MB**（50 张原图 + JSON 元数据）
- **验收清单**（v1）：
  1. `dotnet build -c Debug` — 0 警告 0 错误
  2. `dotnet test` — 全过（含新增 `ClipboardClassifier` 规则测试 + `ClipboardStore` LRU 测试）
  3. `dotnet publish -c Release -r win-x64` — 0 AOT/trim 警告
  4. exe 增量 < 150KB
  5. 默认配置下 BYH 进程 WorkingSet 增量 < 5MB（用 `Get-Process` 实测对比）
  6. 机器侧验证：复制 1000 条文本 → Ctrl+Alt+V → 搜索/分组/粘贴/置顶/删除全通
  7. 隐私验证：从 1Password / KeePass 复制密码 → 不进历史（被排除规则拦截）

#### 与 CopyQ 的对比（切到 BYH 后的收益）

| 维度 | CopyQ | BYH R54 | 差距 |
|---|---|---|---|
| 内存 | 80-100MB | +5MB（在 BYH 已有基线上）| **省 95%**（独立对比）|
| 冷启动 | 1-3s | <100ms | 快 10-30x |
| EXE | ~80MB | +100KB | 省 99% |
| 分组 | 手动切 Tab | Smart auto-group | 零摩擦 |
| 与 BYH 闭环 | ❌ | ✅ 划词/截图/序号自动进历史 | 新能力 |
| 搜索 | 正则 | 拼音 + 子串 + 规则筛选 | 相当 |
| 脚本能力 | ✅ JS 脚本 | ❌（不做）| CopyQ 唯一不可替代点 |

#### 风险点（永久记录）

1. **常驻内存契约**：R44-R53 都遵守"常驻增量 ≈ 0"，R54 会破坏这条（监听器 + 缓存常驻）。但 R54 是独立模块不归 Ocean Eyes 管辖，单独验收。Ocean Eyes 系列后续新增功能仍遵守原契约。
2. **隐私敏感**：剪贴板常有密码/验证码/私聊。**必须**加排除规则 + DPAPI 加密 + 一键清除。
3. **图片膨胀**：默认关，否则 1000 张图能吃 100MB+。开启后用缩略图策略限制在 50 张上限。
4. **CopyQ 脚本依赖**：如果用户重度依赖 CopyQ 的 JS 脚本（如"复制后自动 POST 到某 API"），BYH 替代不了。99% 用户不用。
5. **NativeAOT + `AddClipboardFormatListener`**：必须给隐藏的 message-only 窗口（或复用现有 tray window）注册监听，确保 WM_CLIPBOARDUPDATE 不被 Avalonia 主窗口吞掉。

#### 实现（v1 落地，2026-07-22 第五十一批）

> 用户拍板 4 档范围：**最小可用 v1（文本 only）+ 图片不做（留 v2）+ 敏感只 mask 不 DPAPI（留 v2）+ 粘贴都做（设置选）**。

**调研最大发现（决定改动范围）**：`Win32Clipboard.cs`（815 行）**早已有**消息窗口 + `SubscribeChanges(Action)` + 序列号去重 + `Backup/Restore` + `GetOwnerProcessId`——spec 风险点 #5（"必须建 message-only window"）不存在，直接调 `SubscribeChanges`。**唯一要加的是 `SetText(string)`（照 `SetPng` 抄 GlobalAlloc/SetClipboardData）+ `GetForegroundProcessName()`（排除 app 读源进程名）**。多实例安全：每个 `Win32Clipboard` 唯一 window class name（`BYH.Clipboard.{pid}.{guid}`），长存监听实例与现有临时 `using` 块互不冲突。

**新增文件（11 个）**：
- `Core/Clipboard/ClipboardEntry.cs`（record：Id/Text/Source/CapturedAt/IsPinned/Group/IsSensitive）
- `Core/Clipboard/ClipboardGroup.cs`（enum：Sensitive>Link>Json>Code>Shell>Contact>Number>Text 优先级）
- `Core/Clipboard/ClipboardClassifier.cs`（**Smart auto-group 纯函数**，GeneratedRegex，Sensitive 最高优先级——密码不漏到别的组）
- `Core/Clipboard/ClipboardHistorySettings.cs`（功能开关：Enabled/AutoPaste/MaxEntries/ExcludeProcessNames/MaskSensitive）
- `Core/Input/ClipboardHistoryTriggerSettings.cs`（Ctrl+Alt+V 快捷键 record，照 SpotlightTriggerSettings 抄）
- `Infrastructure/Configuration/ClipboardHistoryStore.cs`（JSON 持久化 + LRU 淘汰 + dedup + BuildPreview mask）
- `Infrastructure/Configuration/ClipboardHistoryTriggerStore.cs`（快捷键 store，1:1 抄 SpotlightTriggerStore）
- `Infrastructure/Configuration/ClipboardHistorySettingsStore.cs`（功能开关 store）
- `App/ClipboardHistoryService.cs`（**核心控制器**：长存 Win32Clipboard + SubscribeChanges + 序号去重 + 排除 app 过滤 + Classifier 归类 + Store 持久化 + LRU；PasteAsync 写剪贴板/可选 Ctrl+V；放在 App 层因为跨 Platform.Windows+Infrastructure+Core 三项目）
- `UI/Views/PinyinSearchHelper.cs`（**从 SpotlightWindow 提取共享**的拼音三段搜索：substring+initials+pinyin，SpotlightWindow 改成调 helper 零行为变化）
- `UI/Views/ClipboardHistoryWindow.axaml(.cs)` + `ClipboardHistoryEntryRow.cs`（Maccy 风格弹窗，克隆 SpotlightWindow 结构，加 Pin/Delete/敏感 mask）

**修改文件（4 个）**：
- `Win32Clipboard.cs`：+`SetText`（照 SetPng）+`GetForegroundProcessName`（GetForegroundWindow+Process.GetProcessById）+`[DllImport] GetForegroundWindow`
- `ByhApplicationPaths.cs`：+3 path 属性（trigger/settings/history 文件）
- `App.axaml.cs`：照 Spotlight 三件套对位加 hotkey+window+service+事件+dispose（RegisterInitialClipboardHistoryHotKey/OnClipboardHistoryTriggered toggle/OnClipboardHistoryTriggerSettingsSaved 事务流/OnClipboardHistorySettingsSaved/OnClipboardHistoryClearRequested/Paste/Pin/Delete/Settings）
- `SettingsWindow.axaml(.cs)`：+SettingsPage.ClipboardHistory 枚举+nav 按钮+section（快捷键卡+功能开关卡：启用/自动粘贴/mask/上限/排除 app/清空按钮）

**Smart auto-group 规则（纯函数 ClipboardClassifier）**：Sensitive（api_key/secret/token/password/AKIA[16]/private_key/Bearer，**最高优先级**）> Link（http(s)/ftp/www）> Json（可 parse 的 {/[）> Code（function/class/import/namespace/def/public/private/return）> Shell（sudo/apt/git/chmod/cd/ls/mkdir/rm）> Contact（邮箱/11位手机）> Number（纯数字金额）> Text（兜底）。

**安全性（三道防线）**：① 排除 app（GetForegroundProcessName 匹配 ExcludeProcessNames，默认 1password/keepass/authy/bitwarden/lastpass/enpass/dashlane，挡密码管理器）② 敏感组 mask（●●●● 点击展开，防肩窥）③ 剪贴板内容**不写日志**（RedactedLogger 只 redact 凭据 shape，不保护任意文本）。DPAPI 加密留 v2。

**踩坑（实施中修复）**：
1. **ClipboardHistorySettings 静态字段初始化顺序**：`DefaultExcludeProcessNames` 必须在 `Default` 之前声明，否则 `Default = new()` 时 `ExcludeProcessNames` 初始化器引用到 null（C# 静态字段按文本顺序初始化）。
2. **EvictToMax 初版按列表位置淘汰是错的**：`AddAndEvict` 产出的列表是 `[newest, ...old_in_input_order]`，位置不保证时间序。改为按 `CapturedAt` 时间戳淘汰最旧的非 pinned（LINQ OrderBy + Take + HashSet drop）。
3. **ClipboardHistoryService 不能放 Platform.Windows**：它依赖 Infrastructure（Store+RedactedLogger），而 Platform.Windows 不引用 Infrastructure。改放 **App 层**（composition root，三项目都能见），与 SelectionRuntime 同位置。

**验收（v1）**：`dotnet build` **0 警告 0 错误**；`dotnet test` **390/390 通过**（+27 剪贴板单测：Classifier 8 组正反例 + Store LRU/Pin/dedup/round-trip/corrupt-file + Settings normalize/clamp/dedup + Trigger）；NativeAOT publish **0 trim/AOT 警告**；exe 27,928,064 → **28,167,680（+234KB**，超 150KB 预算——R54 是独立模块含完整新窗口+服务+设置页+拼音表共享，合理）。已部署。⚠️ **动画/分组/mask/搜索/粘贴/Pin/删除/排除 app 正确性需真机验证**。

---

### R44-R54 完成顺序建议

```
P0（高性价比，先做）
  ✅ R44 取色器 (2026-07-19 落地)
  ❌ R45 二维码 (2026-07-20 落地 → 同日撤销，ZXing +595KB 不划算)
  ✅ R47 数字标注 (2026-07-20 落地)
  ✅ R46 贴图 (2026-07-19 落地，v13 终态)
    ──→ ✅ R52 磁力吸（依赖 R46，2026-07-20 第四十三批落地）

P1（中等，按需做）
  ✅ R48 标注工具集（依赖 R47 标注 layer，2026-07-20 第四十三批 v2 落地）
  ❌ R51 截图美化（2026-07-21 第四十五批落地 → 同日撤销，CleanShot X 模型对深色内容看不出效果；未来重做改 iShot 模型）
  ✅ R49 截图相册（2026-07-20 第四十六批落地）
  ✅ R55 置顶截图阴影（2026-07-22 第四十九批落地，纯 AXAML +0 行 C#，T 贴图浮窗四周大投影）
  ✅ R56 贴图缩放弹出/缩走动画（2026-07-22 第五十批 v4 终态，timer + TransformOperations 中心矩阵；开 4 轮迭代——v1 RenderTransformOrigin 失效、v2 Ease 当绝对值、v3 首帧 Bounds 错、v4 ApplyScale 瞬间到位解真根因；诊断日志确认后已删）
  R50 带壳截图

P1+（重功能，最后做）
  ❌ R53 长截图（2026-07-22 第四十八批落地 → 同日撤销，overlay 架构冲突；stitcher 算法可复用）

独立模块（不依赖 Ocean Eyes）
  ✅ R54 剪贴板历史（2026-07-22 第五十一批 v1 落地，文本 only + Smart auto-group + mask 敏感 + Ctrl+Alt+V 弹窗；图片/DPAPI/SQLite 留 v2）
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

## 待办（非紧急，与 R44-R54 平行）

- **R54 剪贴板历史 v2**（v1 已于 2026-07-22 第五十一批落地，文本 only + Smart auto-group + mask 敏感 + Ctrl+Alt+V 弹窗）。v2 待办：图片条目（96×96 缩略图 + 原图存盘 + 50 张上限，复用 GalleryWindow 的 `Bitmap.DecodeToWidth`）/ 敏感组 DPAPI 加密（复用 `DpapiSecretStore`）/ SQLite + FTS5 全文搜索（+1.5MB 依赖）。详见 §R54。
- **安装包 / 代码签名 / 开机启动**（v0.1 收尾）。
- **P1.7 DPI/多显示器定位**（单屏 mouse+16px）。
- **P1.8 应用语料库 95% 验收**。
- **R24 轨道 B② PaddleOCR-VL-1.6 本地 OCR**：0.9B SOTA 离线兜底，AOT 验证 + 模型分发，v0.2 目标。
- **R24 轨道 B③ WinRT OCR**：`Windows.Media.Ocr`，长期 backlog，性价比最低。
- **R23 启动器可选增强**（图标缓存落盘 / UWP / CLI / 多 prompt UX）— 主体已在第二十批落地，这里只是可选优化。
- **macOS 未开始**。
