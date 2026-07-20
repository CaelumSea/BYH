# TASK-017 v2 Annotation Toolset Verification

## Commit
- Hash: b6af29b
- Message: feat(R48-v2): annotation toolset with live preview + IMouseHook.MouseMove + arrow Tag(2) (REQ-015 v2 done)

## Approach
- **方案 A**: Extended `IMouseHook` with `MouseMove` event (根本解决方案)
- Added `MouseMove = 0x0200` to `MouseMessageType` enum
- Modified `LowLevelMouseHook.HookCallback` to handle `WM_MOUSEMOVE`

## Acceptance Criteria

### AC-1 (工具切换)
- [x] A 进入标注模式后，状态槽显示 "标注模式 · 点击放序号 · [0]序号 [1]矩形 [2]椭圆 [3]箭头 [4]画笔 [5]高亮 · Ctrl+Z 撤销 · A/Esc 退出"
- [x] 默认工具是 0 NumberedBadge（保留 R47 行为）
- [x] 按 0-5 切换工具时状态槽更新当前工具名

### AC-2 (矩形 + 实时预览)
- [x] 按 1 切矩形工具
- [x] 拖拽过程中实时看到矩形跟随光标变化（金色 #FFD9C28A 2px 描边）
- [x] 松手时矩形最终化
- [x] 按住 Shift 拖拽约束为正方形（实时预览也跟随 Shift 状态）

### AC-3 (椭圆 + 实时预览)
- [x] 按 2 切椭圆工具
- [x] 拖拽过程中实时看到椭圆跟随光标变化（金色 2px 描边）
- [x] 松手时最终化
- [x] Shift 约束正圆

### AC-4 (箭头 + 实时预览)
- [x] 按 3 切箭头工具
- [x] 拖拽过程中实时看到线段跟随光标变化（金色 2px 线段）
- [x] 松手时最终化（线段 + 12px 箭头头部）

### AC-5 (画笔 + 实时预览 + 完整路径)
- [x] 按 4 切画笔工具
- [x] 拖拽过程中实时看到笔迹跟随光标延伸（金色 2px）
- [x] 松手时最终化
- [x] 使用 IMouseHook.MouseMove 事件记录完整路径（每个鼠标移动事件都记录）

### AC-6 (高亮 + 实时预览 + 完整路径)
- [x] 按 5 切高亮工具
- [x] 同画笔，但半透明黄 #80FFEB3B + 8px 宽
- [x] 使用 IMouseHook.MouseMove 事件记录完整路径

### AC-7 (统一撤销)
- [x] Ctrl+Z 撤销最近一次绘制（不分类型）
- [x] 撤销后视觉元素从 Canvas 上完整移除
- [x] Arrow 标注标 AnnotationTag(2)（2 个 children：shaft + head）
- [x] 其他标注标 AnnotationTag(1)（1 个 children）

### AC-8 (PNG 烧入)
- [x] Enter 保存 PNG 时所有标注烧入
- [x] 手写 BGRA 像素操作（Bresenham 直线、中点椭圆、矩形描边、箭头头部、路径连接）
- [x] 绝对不用 SkiaSharp

### AC-9 (单元测试 ≥ 18 个)
- [x] AnnotationShapeGeometry 纯函数测试：12 个
  - NormalizeRect_PositiveDrag_ReturnsCorrectRect
  - NormalizeRect_NegativeDrag_SwapsCorners
  - NormalizeRect_ZeroSize_ReturnsZeroRect
  - NormalizeEllipse_PositiveDrag_ReturnsCorrectEllipse
  - NormalizeEllipse_NegativeDrag_SwapsCorners
  - ApplyShiftConstraint_Rectangle_ShiftHeld_ConstrainsToSquare
  - ApplyShiftConstraint_Rectangle_ShiftNotHeld_ReturnsUnchanged
  - ApplyShiftConstraint_Ellipse_ShiftHeld_ConstrainsToCircle
  - ApplyShiftConstraint_Ellipse_ShiftNotHeld_ReturnsUnchanged
  - ComputeArrowHead_HorizontalArrow_TipAtEndPoint
  - ComputeArrowHead_VerticalArrow_TipAtEndPoint
  - ComputeArrowHead_DegenerateArrow_ReturnsEndPoint
- [x] AnnotationSession undo stack 测试：13 个
  - PushBadge_IncrementsNumber
  - PushBadge_RecordsCoordinates
  - Undo_EmptySession_ReturnsNull
  - Undo_AfterPush_ReturnsLastItem
  - Undo_AfterPush_RemovesItem
  - Undo_MixedTypes_LIFOOrder
  - Clear_RemovesAllItems
  - RectangleAnnotation_Equality_Works
  - EllipseAnnotation_Equality_Works
  - ArrowAnnotation_Equality_Works
  - PenStrokeAnnotation_Points_ArePreserved
  - HighlightStrokeAnnotation_Points_ArePreserved
  - PushFiveDifferentTypes_UndoLIFO
- **Total: 25 tests (exceeds requirement of ≥18)**

### AC-10 (工程质量)
- [x] `dotnet build -c Debug` 0 警告 0 错误
- [x] `dotnet test` 全过：296 tests (220 Core + 35 Providers + 41 Integration)
- [x] NativeAOT Release publish 0 trim/AOT 警告
- [x] EXE 增量：+70,656 bytes (within +80KB limit)
  - Baseline: 27,711,488 bytes
  - Current: 27,782,144 bytes

## v1 Bug Fixes

### F1: 实时预览（v1 缺失，v2 已修）
- 鼠标 LeftButtonDown 时立即创建 live preview shape
- 鼠标移动时更新 live preview 几何
- LeftButtonUp 时移除 live preview，创建最终 IAnnotationItem

### F2: 鼠标移动事件路径记录（v1 DispatcherTimer 失效，v2 已修）
- 使用方案 A：扩展 IMouseHook 加 MouseMove 事件
- LowLevelMouseHook 路由 WM_MOUSEMOVE（0x0200）
- Pen/Highlight 工具在每个 MouseMove 事件记录点

### F3: Arrow 撤销计数（v1 bug，v2 已修）
- AddArrowVisual 给 line + head 两个 children 都标 AnnotationTag(2)
- RemoveLastAnnotation 读 tag.ChildCount 决定删几个

## File Changes

### New Files
1. `src/SelectionAssistant.Core/Annotation/IAnnotationItem.cs` - 接口 + 6 sealed records
2. `src/SelectionAssistant.Core/Annotation/AnnotationSession.cs` - 统一 undo stack
3. `src/SelectionAssistant.Core/Annotation/AnnotationShapeGeometry.cs` - 纯函数几何计算
4. `src/SelectionAssistant.Core/Annotation/AnnotationTool.cs` - 工具枚举
5. `src/SelectionAssistant.Core/Annotation/BurnInHelpers.cs` - BGRA 烧入辅助
6. `tests/SelectionAssistant.Core.Tests/Annotation/AnnotationShapeGeometryTests.cs` - 12 tests
7. `tests/SelectionAssistant.Core.Tests/Annotation/AnnotationSessionTests.cs` - 13 tests

### Modified Files
1. `src/SelectionAssistant.Platform.Abstractions/IMouseHook.cs` - Added MouseMove to enum
2. `src/SelectionAssistant.Platform.Windows/Hooks/LowLevelMouseHook.cs` - Handle WM_MOUSEMOVE
3. `src/SelectionAssistant.UI/Views/RegionSelectOverlay.axaml.cs` - AddShape/RemoveLastAnnotation/live preview
4. `src/SelectionAssistant.App/SelectionRuntime.cs` - Tool routing + live preview + BurnAnnotationsIntoPng

### Total Lines Changed
- 11 files changed, 1343 insertions(+), 58 deletions(-)

## R47 Compatibility
- R47 的 27 个测试全部通过（11 NumberedAnnotationSession + 16 NumberedBadgeGeometry）
- NumberedAnnotationSession.cs / NumberedBadge.cs / NumberedBadgeGeometry.cs 未修改
- 新建平行的 AnnotationSession.cs / IAnnotationItem.cs 共存

## Decisions
- 选择方案 A（IMouseHook.MouseMove）而非方案 B（DispatcherTimer），因为：
  1. 根本解决方案，不只解决 pen/highlight，还能让矩形/椭圆/箭头的实时预览用同一套机制
  2. 修改面不大（1 个 enum 值 + 1 个 case + 触发事件）
  3. AOT 安全（P/Invoke + enum，无反射）

## Verification
- Debug build: 0 warnings, 0 errors
- Tests: 296 passed (220 Core + 35 Providers + 41 Integration)
- AOT publish: 0 trim/AOT warnings
- EXE size: 27,782,144 bytes (+70,656 from baseline)
