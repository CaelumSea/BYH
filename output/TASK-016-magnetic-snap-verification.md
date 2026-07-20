# TASK-016 Verification: R52 Magnetic Snap (REQ-014)

## Date: 2026-07-20

## AC Verification

### AC-1 Screen work-area edge snap
- [x] `MagneticSnapCalculator.ComputeSnap` checks moving window edges against all screen work-area edges
- [x] `GetWorkAreas()` in PinnedScreenshotWindow uses `Screens.AllScreens.WorkingArea` x `RenderScaling` for physical pixel rects
- [x] Tests: `ScreenLeft_HitsWithinThreshold`, `ScreenRight_HitsWithinThreshold`, `ScreenTop_HitsWithinThreshold`, `ScreenBottom_HitsWithinThreshold`

### AC-2 Other pinned window edge snap
- [x] `GetOtherPinnedBounds` callback injected by SelectionRuntime returns other windows' physical rects
- [x] Calculator checks all 4 edges of each other window
- [x] Tests: `OtherWindowEdge_HitsWithinThreshold`, `MultipleWindows_PicksClosest`

### AC-3 Real-time gold guide lines during drag
- [x] `SnapGuideCanvas` added to PinnedScreenshotWindow.axaml (Panel overlay, IsHitTestVisible=False)
- [x] `UpdateSnapGuideCanvas(hints)` draws gold (#FFD9C28A alpha=0.6) 2px lines at snapped edges
- [x] `ClearSnapGuides()` hides canvas on pointer release
- [x] Guide lines visible during drag, hidden on release

### AC-4 Shift temporarily disables snap
- [x] `GetKeyState(VK_SHIFT=0x10) & 0x8000` checked in both `OnPointerMoved` and `OnPointerReleased`
- [x] `shiftHeld=true` causes `ComputeSnap` to return original position with empty hints
- [x] No guide lines drawn when Shift is held
- [x] Test: `ShiftHeld_ReturnsOriginalPosition`

### AC-5 Unit tests >= 8
- [x] 12 tests in `MagneticSnapCalculatorTests`:
  1. `ScreenLeft_HitsWithinThreshold`
  2. `ScreenRight_HitsWithinThreshold`
  3. `ScreenTop_HitsWithinThreshold`
  4. `ScreenBottom_HitsWithinThreshold`
  5. `ThresholdBoundary_7px_Hits`
  6. `ThresholdBoundary_9px_DoesNotHit`
  7. `OtherWindowEdge_HitsWithinThreshold`
  8. `MultipleWindows_PicksClosest`
  9. `ShiftHeld_ReturnsOriginalPosition`
  10. `MultiScreen_NegativeOffset_Works`
  11. `NoSnapTargets_ReturnsOriginalPosition`
  12. `EmptyWorkAreas_ReturnsOriginalPosition`

### AC-6 Build/test/publish quality
- [x] `dotnet build -c Debug`: 0 warnings, 0 errors
- [x] `dotnet test`: 271 passed (259 baseline + 12 new), 0 failed, 0 skipped
- [x] `dotnet publish -c Release -r win-x64` (NativeAOT): 0 trim/AOT warnings
- [x] EXE size: 27,711,488 bytes (baseline 27,691,008, delta +20,480 bytes = +20 KB, within +30 KB limit)

### AC-7 Machine-side verification
- [ ] Requires manual testing on a real machine (cannot be automated in CI)

## Build Outputs

| Metric | Value |
|--------|-------|
| Debug build | 0 warnings, 0 errors |
| Test total | 271 (35 + 195 + 41) |
| Test passed | 271 |
| Test failed | 0 |
| Test skipped | 0 |
| Release publish | 0 trim/AOT warnings |
| BYH.exe size | 27,711,488 bytes |
| Baseline size | 27,691,008 bytes |
| Delta | +20,480 bytes (+20 KB) |

## Files Changed

| File | Action | Lines |
|------|--------|-------|
| `src/SelectionAssistant.Core/Annotation/MagneticSnapCalculator.cs` | NEW | ~130 |
| `tests/SelectionAssistant.Core.Tests/Annotation/MagneticSnapCalculatorTests.cs` | NEW | ~125 |
| `src/SelectionAssistant.UI/Views/PinnedScreenshotWindow.axaml` | MODIFIED | +8 |
| `src/SelectionAssistant.UI/Views/PinnedScreenshotWindow.axaml.cs` | MODIFIED | +120 |
| `src/SelectionAssistant.App/SelectionRuntime.cs` | MODIFIED | +18 |

## Design Decisions

1. **Snap guide lines in window Canvas**: The guide lines are drawn within the pinned window's own Canvas overlay. For screen-edge snaps, the line appears at the window edge (left/right/top/bottom). For other-window snaps, the line appears at the contact edge. This is simpler than a separate overlay window and matches the spec's constraint ("not another top-level window").

2. **Both axes can snap simultaneously**: `ComputeSnap` returns hints for both X and Y axes independently. If both hit, both guide lines are drawn and both position components are snapped.

3. **Snap happens during drag AND on release**: `OnPointerMoved` applies snap in real-time (so the window "sticks" visually during drag). `OnPointerReleased` applies a final snap to catch edge cases where the release position is within threshold but the last move event didn't snap.

4. **Avalonia Screens API for work areas**: Used `Screens.AllScreens.WorkingArea` x `RenderScaling` instead of P/Invoke `EnumDisplayMonitors`. This is simpler and AOT-safe. The scaling multiplication converts DIP to physical pixels.

5. **Return type changed from single hint to list**: The spec suggested returning a single `SnapHint?`, but since both axes can snap simultaneously, returning `IReadOnlyList<SnapHint>` is more correct and avoids information loss.
