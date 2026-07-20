# TASK-010 · Settings English and dimensional frame refinement

Date: 2026-07-20

## Result

The Settings window was refined directly from the supplied Ivory Jade reference image. The existing multi-pane layout is preserved, while the visible settings copy is now English, redundant helper text is reduced, navigation uses restrained outline icons, and the principal surfaces use a softer dimensional edge treatment.

## Visual decisions

- Navigation is a compact single-line English list: General, Translation, Actions, Vision, and Launcher.
- Outline icons use the same caramel/gold line language as the reference and turn white in the active warm-gold tab.
- Depth comes from four low-contrast layers: a translucent gold hairline, an inset ivory highlight, a faint champagne inner glint, and a low-opacity warm outer shadow.
- Heavy dark borders and the Fluent focus rectangle on the active navigation item are removed.
- Repeated explanations are removed; essential current values, compatibility warnings, save feedback, and error messages remain.

## Verification

- Static audit: no Chinese user-visible literals remain in `SettingsWindow.axaml`; remaining Chinese matches in edited C# files are comments only.
- Tests: 232/232 passed (Core 156, Providers 35, Windows 41).
- NativeAOT publish: successful, no AOT/trimming warning, 0 PDB.
- `BYH.exe`: 27,671,040 bytes.
- Default-size visual QA: `artifacts/qa/ivory-jade-settings-v8-english-depth-nativeaot.png`.
- 175% DPI minimum-size visual QA (1240×680 logical / 2194×1254 physical): `artifacts/qa/ivory-jade-settings-v8-english-depth-minimum-nativeaot.png`.
- Minimum-size QA confirms that navigation, Current setup, System Overview, and Window controls remain readable without overlap or clipping.

## Runtime artifact

`artifacts/publish/win-x64-nativeuia/BYH.exe` is the current tested build and is running from that path.
