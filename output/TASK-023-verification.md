# TASK-023 Verification — REQ-021 v6

## Outcome

The compact phone remains 390 logical pixels tall but now uses centered vertical alignment inside the upper-right grid cell. The left flat rail is a single uninterrupted specimen sheet divided into four reference-led sections: Theme Concept, Color Palette, Typography, and Icon Style.

## Acceptance evidence

| AC | Evidence | Result |
|---|---|---|
| AC-1 | `general-default-nativeaot.png`: the upper row's main panel spans approximately physical y=63–812 while the phone spans y=149–730, leaving about 86 px above and 82 px below. | Pass |
| AC-2 | Default and 1240×680 logical minimum captures show the avatar, three setup rows and five-item dock without overlap or clipping. | Pass |
| AC-3 | The left sheet shows four separated sections with shared one-DIP rules, square outer geometry and no nested editorial cards. | Pass |
| AC-4 | Seven visible swatches: Primary `#E8C89A`, Secondary `#D5A86A`, Accent `#8FAE5A`, Deep Brown `#6B4B2A`, Cream `#FFF7EE`, Ivory `#F6F1E6`, Border `#EAD8B8`. | Pass |
| AC-5 | Release build, 334 tests and Windows NativeAOT publish passed; five tab captures completed. | Pass |

## Mechanical verification

- XML parsing: `SettingsWindow.axaml` and `IvoryJade.axaml` parse successfully.
- Whitespace: `git diff --check` passed.
- Build: `dotnet build SelectionAssistant.slnx -c Release` passed.
- Tests: Providers 35 + Core 258 + Windows Integration 41 = **334 passed, 0 failed, 0 skipped**.
- Publish: `dotnet publish -c Release -r win-x64 /p:PublishAot=true` passed.
- Delegated executor: `omp-worker`, model `xiaomi-mimo/mimo-v2.5-pro`, low thinking, read/execute-only prompt.

## Visual QA artifacts

- `artifacts/qa/req-021-v6-reference-rail/general-default-nativeaot.png`
- `artifacts/qa/req-021-v6-reference-rail/general-minimum-nativeaot.png`
- `artifacts/qa/req-021-v6-reference-rail/v25-unified-tabs-general-default-nativeaot.png`
- `artifacts/qa/req-021-v6-reference-rail/v25-unified-tabs-provider-default-nativeaot.png`
- `artifacts/qa/req-021-v6-reference-rail/v25-unified-tabs-actions-default-nativeaot.png`
- `artifacts/qa/req-021-v6-reference-rail/v25-unified-tabs-vision-default-nativeaot.png`
- `artifacts/qa/req-021-v6-reference-rail/v25-unified-tabs-launcher-default-nativeaot.png`

## Regression boundary

No event handler, binding, settings persistence, provider configuration, capture path, launcher command, or navigation behavior changed. The implementation is AXAML/theme-only plus the generated NativeAOT artifact.
