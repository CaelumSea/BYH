# TASK-017 Annotation Toolset Verification

## AC Verification

- [x] **AC-1** Tool switching status display. A enters annotation mode; default tool is Number (R47 badge, click-to-place). Keys 0-5 switch tools: 0 = 序号 / 1 = 矩形 / 2 = 椭圆 / 3 = 箭头 / 4 = 画笔 / 5 = 高亮. Status slot updates with current tool name.
- [x] **AC-2** Rectangle tool: drag draws 2px gold stroke rectangle. Shift constrains to square.
- [x] **AC-3** Ellipse tool: drag draws 2px gold stroke ellipse. Shift constrains to circle.
- [x] **AC-4** Arrow tool: drag draws 2px gold line + 12px arrow head.
- [x] **AC-5** Pen tool: drag records path, 2px gold round-cap polyline.
- [x] **AC-6** Highlight tool: same as pen but 8px semi-transparent yellow (#80FFEB3B).
- [x] **AC-7** Ctrl+Z undoes most recent item (badge or shape) from unified stack. A/Esc exits annotation mode (annotations preserved).
- [x] **AC-8** Enter saves PNG with all annotations burned in. Hand-written BGRA pixel operations (BurnInHelpers: Bresenham line, midpoint ellipse, rectangle stroke, arrow head, path). No SkiaSharp. NumberedBadgeAnnotation (R47 badge) routed through the same dispatch via AddShape(NumberedBadgeAnnotation) → AddBadge.
- [x] **AC-9** 23 new unit tests (>= 12 required): AnnotationSessionTests (11) + AnnotationShapeGeometryTests (12). All **282** tests pass (35 Providers + 206 Core + 41 Integration).
- [x] **AC-10** Debug build: 0 warnings, 0 errors. dotnet test: all pass. NativeAOT Release publish: 0 trim/AOT warnings. Exe: **27,760,128 bytes** (baseline 27,691,008 + 69,120 = +68KB, within +80KB budget).

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

- **Default tool & R47 preservation (FIXED post-worker)**: Worker initially set default tool to Rectangle and made NumberedBadge inaccessible. Reviewer caught this as a regression of R47. Fix applied: `EnterAnnotationMode` now defaults to `AnnotationTool.Number` (R47 badge tool), `0` key added to tool-switch routing (vkCode 0x30 → Number), and the mouse hook routes `LeftButtonDown` for Number tool to `session.PushBadge(x, y)` + `AddShape(badge)` immediately (no drag) — exactly preserving R47 behavior. `AddShape(NumberedBadgeAnnotation)` extended to forward to existing `AddBadge(NumberedBadge)` for unified visual dispatch.
- **Pen/highlight uses DispatcherTimer polling** instead of native mouse move events, adding ~16ms latency per point. Acceptable for annotation use. (The low-level mouse hook only fires on button events; adding MouseMove would require modifying the platform abstraction layer.)
