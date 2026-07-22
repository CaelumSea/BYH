# 08 · Ivory Jade 主题系统

> **改配色、控件状态、圆角、卡片材料或任何窗口视觉前先读本文件。**

---

## 设计定位

Ivory Jade 是 BYH 的首个正式主题：瓷器象牙白承担大面积背景，焦糖棕承载文字，玉石绿只表达真实动作和活动状态，古金仅用于细边框、短分隔线和 OCR 手柄等小面积细节。

主题目标是“柔和、低调、精致、低干扰”。快捷浮层继续克制；设置页作为品牌主界面，可使用受控的奶油渐变、珠光花丝和玉石徽记。装饰必须退到内容之后，不允许降低表单对比度或点击可用性。

## 唯一事实源

| 文件 | 职责 |
|---|---|
| `UI/Themes/IvoryJade.axaml` | 全部色值、语义 Brush、圆角、阴影、控件状态和样式类 |
| `App/App.axaml` | 强制 `RequestedThemeVariant="Light"`，在 `FluentTheme` 后加载 Ivory Jade |
| `UI/Views/*.axaml` | 只消费 `DynamicResource` 和语义 `Classes`，不定义品牌色 |
| `SettingsWindow.axaml.cs` / `ResultWindow.axaml.cs` | 运行时反馈通过 `FeedbackSuccess` / `FeedbackError` class 切换，不直接赋 Brush |
| `UI/Assets/Theme/ivory-jade-*.jpg` | 设置页专用的玉石徽记与珠光花丝；PNG 原稿不参与打包 |

## 核心 Token

| 语义 | 色值 | Avalonia Brush |
|---|---:|---|
| background | `#F8F6F1` | `ByhBackgroundBrush` |
| surface | `#FFFDFC` | `ByhSurfaceBrush` |
| surface-secondary | `#F2EADF` | `ByhSurfaceSecondaryBrush` |
| surface-selected | `#E7EDCF` | `ByhSurfaceSelectedBrush` |
| primary | `#667731` | `ByhPrimaryBrush` |
| primary-hover | `#4C5721` | `ByhPrimaryHoverBrush` |
| primary-soft | `#CFDE96` | `ByhPrimarySoftBrush` |
| accent | `#9F5E30` | `ByhAccentBrush` |
| accent-hover | `#6E3519` | `ByhAccentHoverBrush` |
| accent-soft | `#E8D3B8` | `ByhAccentSoftBrush` |
| decorative-gold | `#C2A36D` | `ByhGoldBrush` |
| text-primary | `#3A2417` | `ByhTextPrimaryBrush` |
| text-secondary | `#7D604A` | `ByhTextSecondaryBrush` |
| text-placeholder | `#A58C78` | `ByhTextPlaceholderBrush` |
| text-on-primary | `#FFFFFF` | `ByhTextOnPrimaryBrush` |
| border | `#E1C4A3` | `ByhBorderBrush` |
| border-subtle | `#E7DDCC` | `ByhBorderSubtleBrush` |
| disabled | `#D8CCBE` | `ByhDisabledBrush` |

## 反馈 Token

| 状态 | 前景 | 柔和背景 | Brush |
|---|---:|---:|---|
| Success | `#667731` | `#EDF2DC` | `ByhSuccessBrush` / `ByhSuccessSoftBrush` |
| Warning | `#A76524` | `#F7E7CF` | `ByhWarningBrush` / `ByhWarningSoftBrush` |
| Error | `#A44E3F` | `#F5DEDA` | `ByhErrorBrush` / `ByhErrorSoftBrush` |
| Information | `#55737A` | `#DFEAEC` | `ByhInfoBrush` / `ByhInfoSoftBrush` |
| Focus | `#899845` | — | `ByhFocusBrush` |

## 组件语义类

| Class | 用途 |
|---|---|
| `Card` | 普通卡片：surface + subtle border + 12px 圆角 + 小阴影 |
| `CardWarm` | 次级分组：surface-secondary，无阴影 |
| `PearlCard` / `PearlInset` | 设置页高保真奶油材料：渐变、细金边、暖色阴影或内嵌分组 |
| `MetallicFrame` | R54 设置页结构外框：1-DIP 金属渐变边（ByhMetallicEdgeBrush）+ 2-DIP 象牙光学间隙 + 1-DIP 浅金内曲线（3 DIP 处），同心圆角 24px，底部暖色柔影 |
| `MetallicFrame.Compact` | MetallicFrame 紧凑变体：圆角降至 18px，用于侧栏等窄面板 |
| `PorcelainCard` | 设置页轻量瓷器卡片；奶油渐变 + 细金边 + 内嵌双层高光，比 PearlCard 更克制 |
| `FlatRail` | 最左侧产品概念栏：无圆角、无外框阴影，仅由右侧古金竖线分隔 |
| `GemPortrait` | 右上 APP icon 人物焦点的金边珠光圆形框 |
| `StatusPill` | 顶部真实能力标签，不承载虚构统计 |
| `SettingsNav` + `Active` | 侧栏导航；默认透明，活动态为垂直三段焦糖渐变（ByhGoldNavBrush：champagne→caramel→bronze）+ 14px 圆角，jade focus ring 保留键盘可达性 |
| `CardTitle` | 卡片分区标题：与 DisplayTitle 同族衬线（Georgia），SemiBold，尺寸降到 15px，保证全页标题衬线统一 |
| `MiniIcon` | 概览行 13px 线图标（Stroke 内联给定），配合 26px 圆形 soft-tint 徽章使用（Current setup / Runtime 行） |
| `Badge` | 小面积暖色徽章：accent-soft + gold 细边 |
| `FloatingSurface` | Toolbar / QuickTools 的带透明度象牙材料 |
| `Primary` | 保存、运行、OCR 等当前区域主要动作 |
| `Danger` | 退出、删除 Provider 等明确危险动作 |
| `DangerQuiet` | 列表删除、恢复默认等低权重危险动作 |
| `Ghost` | 浮层中的次级动作，hover 才出现材料反馈 |
| `Secondary` / `Muted` / `Accent` | 文字层级 |
| `FeedbackSuccess` / `FeedbackError` / `FeedbackInfo` | 运行时状态文字 |

默认 Button 是 secondary：象牙背景、焦糖文字、古金细边。每个区域只选择一个 `Primary`，避免玉色泛滥。

## 透明窗口和 OCR 特例

- `ByhFloatingSurfaceBrush` / `ByhFloatingBorderBrush` 是 approved palette 的 alpha 派生色；Toolbar 和 QuickTools 仍保持 AcrylicBlur。
- OCR overlay 使用暖棕 `ByhOverlayDimBrush`，不再使用纯黑遮罩。
- 选区是有意义的操作，使用 focus jade；8 个手柄面积很小，使用 decorative gold。
- 这些 overlay token 仍属于主题资源，不允许重新写回 View 常量。

## 形状和深度

- 小/中/大圆角：8 / 12 / 18 px。MetallicFrame 使用 24px 圆角，Compact 变体 18px。
- 小阴影：`0 1 3`、约 5% `#4A2B19`；中阴影：`0 7 22`、约 6% `#4A2B19`。
- MetallicFrame 五层 BoxShadow 构成完整的光学层次（由外至内）：
  1. `0 1 1 #7AFFF8E8` — 暖色底部微光
  2. `0 5 15 #176E3519` — 柔和暖棕中层投影
  3. `0 10 28 #0D4A2B19` — 深层外散投影
  4. `inset 0 0 0 2 #FFFFFCF7` — 2-DIP 象牙光学间隙（第一层内阴影）
  5. `inset 0 0 0 3 #B8D9B97D` — 1-DIP 浅金内曲线（第二层内阴影，3 DIP 处）
- 外层真实边框（`ByhMetallicEdgeBrush`，1 DIP）提供金属渐变：左上青铜 → 长边香槟 → 右下深金 → 终点亮金，模拟参考图的金属光泽。
- 内层背景 `#F9FFFDFC` 近白象牙，与 2-DIP 间隙色接近，形成同心圆角的光学连续性。
- 设置页珠光阴影：`0 5 18`、暖焦糖低透明度；`ByhHairlineBrush` 用于大窗格和分隔线。
- Avalonia `TextBox` 不暴露 `BoxShadow`，焦点态用 2px primary 边框；不要重新添加无效 setter。

## 不变量

1. View AXAML 不出现品牌十六进制色；透明字面量只允许用于确实需要透明的窗口/只读 TextBox。
2. 运行时状态切 class，不在 C# 中构造品牌 Brush。
3. 玉色主要表达动作/成功；设置页允许一个品牌玉石徽记作为视觉焦点。金色只用于细框、活动导航和小型花丝，不铺满主画布。
4. 新窗口先复用现有 class，再考虑新增 token；禁止复制一套局部主题。
5. 改 QuickTools 高度/内容后必须在 175% DPI 截图，防止底部重叠。
6. SettingsWindow 使用 R29 四列两行空间骨架：`190,170,*,270` × `*,260`，默认 1320×800、最小 1240×680。产品概览跨两行；导航只占上排；中央设置与右侧人物欢迎卡+摘要位于上排；下排的 `SYSTEM OVERVIEW` 必须横跨导航与中央设置两列，`Window controls` 位于右下。中央设置分区独立滚动。禁止把导航恢复成 `Grid.RowSpan="2"` 的全高独立列。
7. `SummaryProviderText`、`SummaryShortcutText`、`SummaryVisionText` 必须由运行时设置刷新；不允许用虚构 Dashboard 数字填满多窗格。
8. 右上人物图固定复用 `avares://BYH/Assets/app-icon.png`；新增辅助模块只能呈现真实配置或静态说明。
9. `ivory-jade-emblem.jpg` 用于圆形徽记时必须放大后居中裁切，不能把源图四周边缘缩进可视区域；左侧三个说明板块保持等高。
10. Settings 面板导航与界面文案使用简洁英文；说明文字只保留操作语义、当前值、兼容性/安全提示和错误反馈。导航图标统一使用轻量轮廓线，不得混入彩色 emoji 或风格不一致的图标库。

11. 最左侧产品概念栏（FlatRail）必须保持无圆角的平直分栏，只用约 1.5px 古金色右侧竖线与工作区分隔；设置页结构性窗格（导航栏与主内容区）使用 MetallicFrame 或 MetallicFrame.Compact，FlatRail 和内部 PearlCard/PorcelainCard 表面不使用 MetallicFrame。两者不能混用同一种外框。

## 验证

```powershell
rg -n '#[0-9A-Fa-f]{6,8}|Brushes\.(LightCoral|LightGreen)' src\SelectionAssistant.UI\Views
dotnet test SelectionAssistant.slnx -c Release --nologo
dotnet publish src\SelectionAssistant.App\SelectionAssistant.App.csproj `
  -c Release -r win-x64 --nologo -o artifacts\publish\win-x64-nativeuia
```

视觉证据位于：

- `artifacts/qa/ivory-jade-settings.png`
- `artifacts/qa/ivory-jade-settings-v3.png`（R27 高保真设置页）
- `artifacts/qa/ivory-jade-settings-v3-provider.png`
- `artifacts/qa/ivory-jade-settings-v3-minimum-provider.png`（175% DPI、860×600 logical 最小尺寸）
- `artifacts/qa/ivory-jade-settings-v4-multipane.png`（R28 四列两行多窗格，175% DPI 默认尺寸）
- `artifacts/qa/ivory-jade-settings-v4-provider.png`（R28 Provider 页面与真实 Current setup）
- `artifacts/qa/ivory-jade-settings-v4-minimum-provider.png`（175% DPI、1240×680 logical 最小尺寸）
- `artifacts/qa/ivory-jade-settings-v5-nativeaot.png`（R29 发布版默认页与 APP icon 人物欢迎卡）
- `artifacts/qa/ivory-jade-settings-v5-provider.png`（R29 Provider 页面）
- `artifacts/qa/ivory-jade-settings-v5-minimum.png`（R29 175% DPI、1240×680 logical 最小尺寸）
- `artifacts/qa/ivory-jade-settings-v6-nativeaot.png`（R30 宝石裁切、柔和边界、右上人物区与等高左栏）
- `artifacts/qa/ivory-jade-settings-v6-minimum.png`（R30 175% DPI、1240×680 logical 最小尺寸）
- `artifacts/qa/ivory-jade-settings-v7-corrected-nativeaot.png`（R30 follow-up：导航上移、下方共享窗格横跨导航与中央区，NativeAOT 默认尺寸）
- `artifacts/qa/ivory-jade-settings-v7-corrected-minimum-nativeaot.png`（R30 follow-up：175% DPI、1240×680 logical 最小尺寸）
- `artifacts/qa/ivory-jade-settings-v8-english-depth-nativeaot.png`（R30 第二次 follow-up：全英文、线性导航图标与层叠立体边缘，NativeAOT 默认尺寸）
- `artifacts/qa/ivory-jade-settings-v8-english-depth-minimum-nativeaot.png`（R30 第二次 follow-up：175% DPI、1240×680 logical 最小尺寸）
- `artifacts/qa/ivory-jade-settings-v9-annotated-default-nativeaot.png`（R30 第三次 follow-up：按用户标注图精修导航双线、背景层级、上下比例与平直左栏）
- `artifacts/qa/ivory-jade-settings-v9-annotated-minimum-nativeaot.png`（R30 第三次 follow-up：175% DPI、1240×680 logical 最小尺寸）
- `artifacts/qa/ivory-jade-quick-tools.png`
- `artifacts/qa/ivory-jade-region-overlay.png`
- `artifacts/qa/ivory-jade-settings-v10-metallic-default-nativeaot.png`（R54 MetallicFrame 结构框，NativeAOT 默认尺寸）
- `artifacts/qa/ivory-jade-settings-v10-metallic-minimum-nativeaot.png`（R54 MetallicFrame 结构框，175% DPI、1240×680 logical 最小尺寸）
- `artifacts/qa/ivory-jade-settings-v10-metallic-corner-detail.png`（R54 主面板左上角金属渐变外缘、象牙缝与浅金内曲线局部）
