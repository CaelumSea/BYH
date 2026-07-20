# TASK-017 Annotation Toolset Verification

## AC Verification

- [x] **AC-1** Tool switching status display. A enters annotation mode; status shows "标注模式 · [1]矩形 [2]椭圆 [3]箭头 [4]画笔 [5]高亮 · Ctrl+Z 撤销 · A/Esc 退出". Default tool is Rectangle (1). Keys 1-5 switch tools with status update.
- [x] **AC-2** Rectangle tool: drag draws 2px gold stroke rectangle. Shift constrains to square.
- [x] **AC-3** Ellipse tool: drag draws 2px gold stroke ellipse. Shift constrains to circle.
- [x] **AC-4** Arrow tool: drag draws 2px gold line + 12px arrow head.
- [x] **AC-5** Pen tool: drag records path, 2px gold round-cap polyline.
- [x] **AC-6** Highlight tool: same as pen but 8px semi-transparent yellow (#80FFEB3B).
- [x] **AC-7** Ctrl+Z undoes most recent item (badge or shape) from unified stack. A/Esc exits annotation mode (annotations preserved).
- [x] **AC-8** Enter saves PNG with all annotations burned in. Hand-written BGRA pixel operations (BurnInHelpers: Bresenham line, midpoint ellipse, rectangle stroke, arrow head, path). No SkiaSharp.
- [x] **AC-9** 23 new unit tests (>= 12 required): AnnotationSessionTests (11) + AnnotationShapeGeometryTests (12). All 247 tests pass (183+23 Core + 41 Integration).
- [x] **AC-10** Debug build: 0 warnings, 0 errors. dotnet test: all pass. NativeAOT Release publish: 0 trim/AOT warnings. Exe: 27,750,400 bytes (baseline 27,691,008 + 59,392 = +58KB, within +80KB budget).

## Architecture Decisions

- **Option A chosen**: New `AnnotationSession` class with `IAnnotationItem` interface, keeping `NumberedAnnotationSession` untouched for backward compatibility. All 27 R47 tests remain unmodified.
- **Pen/highlight path recording**: The low-level mouse hook only fires on button events (not moves). Used a DispatcherTimer polling `GetCursorPos` at ~60Hz during drag for pen/highlight tools.
- **Shape visual rendering on overlay**: Uses Avalonia shapes (Rectangle, Ellipse, Line, Polyline) for live preview. Burn-in uses hand-written BGRA Bresenham algorithms.
- **ImmutablePen for stroke definitions**: Gold (2px) and Highlight (8px semi-transparent yellow) pens defined as static fields.

## Files Created (5)

1. `src/SelectionAssistant.Core/Annotation/IAnnotationItem.cs` - Interface + 6 sealed record types
2. `src/SelectionAssistant.Core/Annotation/AnnotationTool.cs` - Enum (Number/Rectangle/Ellipse/Arrow/Pen/Highlight)
3. `src/SelectionAssistant.Core/Annotation/AnnotationSession.cs` - Unified undo stack
4. `src/SelectionAssistant.Core/Annotation/AnnotationShapeGeometry.cs` - Pure geometry functions
5. `src/SelectionAssistant.Core/Annotation/BurnInHelpers.cs` - BGRA pixel drawing primitives

## Files Created (Tests, 2)

6. `tests/SelectionAssistant.Core.Tests/Annotation/AnnotationSessionTests.cs` - 11 tests
7. `tests/SelectionAssistant.Core.Tests/Annotation/AnnotationShapeGeometryTests.cs` - 12 tests

## Files Modified (2)

8. `src/SelectionAssistant.UI/Views/RegionSelectOverlay.axaml.cs` - Added shape visual API (AddShape, RemoveLastAnnotation, AnnotationTag)
9. `src/SelectionAssistant.App/SelectionRuntime.cs` - Tool switching (1-5 keys), mouse drag routing, stroke timer, BurnAnnotationsIntoPng dispatch

## Deviations from Spec

- **No NumberedBadgeAnnotation in EnterAnnotationMode**: The spec says default tool is Rectangle (key 1). The R47 numbered badge placement (click-to-place) is no longer the default behavior. Users switch to numbered badges via... actually, there is no key for Number tool in the 1-5 range. This is a deviation: the Number tool (R47 badge) is not accessible via 1-5 keys. The original R47 click-to-place badge behavior has been replaced by shape drawing tools. If numbered badges need to be accessible, a 6th key binding would be needed.
- **Pen/highlight uses DispatcherTimer polling** instead of native mouse move events, adding ~16ms latency per point. Acceptable for annotation use.
