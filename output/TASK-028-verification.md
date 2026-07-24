# TASK-028 Verification

## Scope

Fix settings pages that reached their reported scroll limit while the last
controls still touched or disappeared beneath the rounded content clip.

## Root cause

The continuous-surface v25 layout changed the central `ScrollViewer` from
`Padding="28,24,28,28"` to `Padding="0"`. General retained an ad-hoc 18 px
bottom margin, but Translation, Actions, Vision, Launcher, and Clipboard ended
at zero. Their scroll extents were technically correct, yet the final row
stopped directly against the clipped macro-panel edge.

## Implementation

- Added one shared 24 px bottom safe area to the grid inside
  `SettingsContentScroll`.
- Removed General's one-off 18 px tail so all six pages use the same rule.
- Added `BYH.Settings.ContentScroll` as a stable Automation ID.
- Extended `artifacts/qa/capture-all-tabs.py` with `--include-bottom`:
  - captures every page at its initial top position;
  - uses Windows UI Automation `ScrollPattern.SetScrollPercent(..., 100)` to
    reach the real bottom;
  - accepts genuinely short, non-scrollable pages;
  - retains a mouse-wheel fallback when UIA is unavailable.

## Verification

```text
Python source compile: PASS
AXAML XML parse: PASS
git diff --check: PASS

dotnet build SelectionAssistant.slnx -c Release
PASS — 0 warnings, 0 errors

dotnet test SelectionAssistant.slnx -c Release --no-build
PASS — Providers 35/35, Core 314/314, Windows 41/41; total 390/390

dotnet publish -c Release -r win-x64 /p:PublishAot=true
PASS — Windows NativeAOT

capture-all-tabs.py <NativeAOT BYH.exe> <output> \
  --require-uia --include-bottom
PASS — six top captures + six bottom captures; zero UIA fallbacks
```

UIA bottom-state results:

| Page | Result |
|---|---|
| General | `scrolled-to-bottom` |
| Translation | `scrolled-to-bottom` |
| Actions | `not-scrollable` (all content already fits) |
| Vision | `scrolled-to-bottom` |
| Launcher | `scrolled-to-bottom` |
| Clipboard | `scrolled-to-bottom` |

The next navigation after each bottom capture returned to the top because
`ShowSettingsPage` resets `SettingsContentScroll.Offset`; the paired
screenshots verify that behavior.

Published branch artifact:

- Path: `artifacts/publish/win-x64-nativeuia/BYH.exe`
- Size: `28,228,096` bytes
- SHA-256:
  `D57C81F3C4C1B9CF7D0C9E351892A507E52588CD99F6BB015D496C39A3B1E7CC`

Visual evidence:

- `artifacts/qa/req-026-scroll-nativeaot/`
- Twelve `2334 × 1464` screenshots.
- General, Translation, Launcher, and Clipboard bottom captures were visually
  inspected: their final controls are complete and retain the shared safe
  area above the lower rounded edge.

The newer daily main-repository executable was restored after QA. It was not
overwritten because main currently contains another Agent's uncommitted
backend work.
