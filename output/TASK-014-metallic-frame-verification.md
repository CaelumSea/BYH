# TASK-014 · MetallicFrame 结构框实现与验证

## Changes

- Replaced obsolete `DecorativeFrame` / `SettingsFrame` / `NavTowerFrame` documentation in `docs/architecture/08-theme-system.md` with the implemented `MetallicFrame` and `MetallicFrame.Compact` behavior.
- Documented the R54 layered optical structure: 1-DIP metallic gradient outer edge (`ByhMetallicEdgeBrush`), 2-DIP ivory optical gap (inset shadow `#FFFFFCF7`), 1-DIP pale-gold inner curve at 3 DIP (inset shadow `#B8D9B97D`), concentric radii (24px default, 18px Compact), and subtle warm lower shadow.
- Updated invariant 11 to state that SettingsWindow structural panes use MetallicFrame while FlatRail and inner PearlCard/PorcelainCard surfaces do not.
- Added R54 MetallicFrame QA screenshots to the visual evidence list.

## Visual evidence

| Screenshot | Description |
|---|---|
| `artifacts/qa/ivory-jade-settings-v10-metallic-default-nativeaot.png` | R54 MetallicFrame, NativeAOT default size, 2314×1454 physical at 175% DPI |
| `artifacts/qa/ivory-jade-settings-v10-metallic-minimum-nativeaot.png` | R54 MetallicFrame, 1240×680 logical minimum, 2174×1244 physical at 175% DPI |
| `artifacts/qa/ivory-jade-settings-v10-metallic-corner-detail.png` | Main-pane top-left corner detail showing the concentric optical rings |

## Verification status

| Check | Status |
|---|---|
| Build | ✅ 0 warnings / 0 errors |
| Tests (`dotnet test`) | ✅ 232/232 (Core 156 + Providers 35 + Windows 41) |
| NativeAOT publish | ✅ 0 warnings; 0 PDB; `BYH.exe` 27,670,528 bytes |
| Visual QA (default) | ✅ Captured |
| Visual QA (minimum) | ✅ Captured |

REQ-012 summary and all four ACs pass. Global `tasks.py validate` still reports the
pre-existing `TASK-012` / `TASK-013` value `status: dispatched`; those belong to
the active R45/R47 parallel branches and were intentionally not changed here.

## Git delivery

- Branch: `task/REQ-012-metallic-frames`
- Worktree: `C:\dvr\byh-worktrees\REQ-012-metallic-frames`
- Final executable: `artifacts/publish/win-x64-nativeuia/BYH.exe`
