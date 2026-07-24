# TASK-027 Verification

## Scope

Replace DPI-sensitive settings navigation clicks with stable Windows UI
Automation identifiers while retaining the existing measured-coordinate
fallback.

## Implementation

- `SettingsWindow.axaml` exposes a window Automation ID and six unique
  navigation IDs:
  - `BYH.Settings.Nav.General`
  - `BYH.Settings.Nav.Translation`
  - `BYH.Settings.Nav.Actions`
  - `BYH.Settings.Nav.Vision`
  - `BYH.Settings.Nav.Launcher`
  - `BYH.Settings.Nav.Clipboard`
- `artifacts/qa/capture-all-tabs.py` invokes the Avalonia buttons through the
  Windows `UIAutomationClient` assembly.
- No new Python package is required; the script uses the Windows PowerShell
  and UI Automation components already present on supported Windows systems.
- Normal mode falls back to the previously measured coordinates when UIA is
  unavailable.
- `--require-uia` disables fallback so CI/manual QA can prove that every
  Automation ID is actually exposed and invokable.

## Verification

```text
Python source compile: PASS
AXAML XML parse: PASS
Automation ID uniqueness (6/6): PASS
Simulated UIA-unavailable coordinate fallback: PASS
git diff --check: PASS

dotnet build SelectionAssistant.slnx -c Release
PASS — 0 warnings, 0 errors

dotnet test SelectionAssistant.slnx -c Release --no-build
PASS — Providers 35/35, Core 314/314, Windows 41/41; total 390/390

dotnet publish -c Release -r win-x64 /p:PublishAot=true
PASS — Windows NativeAOT

capture-all-tabs.py <NativeAOT BYH.exe> <output> --require-uia
PASS — all six pages reported navigation=UIA; zero coordinate fallbacks
```

Published artifact:

- Path: `artifacts/publish/win-x64-nativeuia/BYH.exe`
- Size: `28,227,584` bytes
- SHA-256:
  `258162A3E9691CF7E6A158886ACA6249E6090188C0DCBFCA9FFA4927F75F1A42`

Visual evidence:

- `artifacts/qa/req-025-uia-nativeaot/`
- Six distinct `2334 × 1464` screenshots were generated.
- The Clipboard capture was visually inspected and showed the correct active
  navigation state and page content.

The daily main-repository BYH instance was restored after QA and was
responsive.

## Reqbase fix review

The review was read-only. No files in the dirty Skills-Hub worktree were
changed.

### Confirmed correct

- `quick` now derives the next task ID from existing `tasks/TASK-*.yaml`
  files instead of always writing `TASK-001`.
- A live BYH regression created `REQ-025 + TASK-027`; SHA-256 values for the
  existing `TASK-001.yaml` and `TASK-026.yaml` remained unchanged.
- `secretary.py quick` completed successfully in the current GBK terminal,
  showing that `ensure_utf8_stdout()` fixes the original core-command crash.
- With `PYTHONUTF8=1`, the full reqbase suite passed `98/98`.

### Still incomplete

1. Running `python tests/run_tests.py` normally in the GBK terminal still
   exits with `UnicodeEncodeError` at the runner's own `✓` output. The runner
   does not apply the new UTF-8 stream policy.
2. Test helpers use `subprocess.run(..., text=True)` without
   `encoding="utf-8"`. The child now emits UTF-8 while the GBK parent attempts
   GBK decoding, producing `_readerthread` `UnicodeDecodeError` failures.
3. Many tests call `Path.read_text()` without an explicit encoding, so they
   fail when reading reqbase's UTF-8 YAML on a GBK-default Python process.
4. The new quick REQ.md renderer receives only AC IDs. It writes
   `AC-1（待执行后核对）` instead of the actual acceptance-criterion text, and
   its regression test checks only that `AC-1` exists. REQ-025 was repaired
   manually in this project record.

### Recommended follow-up

- Apply one UTF-8 policy to `tests/run_tests.py`.
- Set `encoding="utf-8", errors="strict"` in every test subprocess helper.
- Set `encoding="utf-8"` on test fixture `read_text`/`write_text` calls.
- Pass the full AC objects/texts into `_quick_req_md` and assert the original
  AC text in the regression test.
- Add a Windows regression that deliberately runs with a GBK-default parent,
  rather than relying only on `PYTHONUTF8=1`.
