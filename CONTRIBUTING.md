# Contributing to BYH

First off — thanks for taking the time to contribute. 🤍

This is a small, single-maintainer project, so the bar is "be useful and
don't break things," not "navigate a bureaucracy." The sections below cover
the essentials.

## Quick start

You need:

- **Windows 10+** (the app targets `net10.0-windows`; the platform layer
  uses Win32 P/Invoke that can't be built or tested on macOS/Linux)
- **.NET 10 SDK**
- Any editor (VS / VS Code / Rider all work)

```bash
git clone <your-fork-url>
cd selection-assistant
dotnet build SelectionAssistant.slnx -c Release     # 0 warnings expected
dotnet test SelectionAssistant.slnx                  # ~660 tests
```

If build or tests fail on a clean clone, that's a bug — please open an issue.

## Project layout

```
src/
├── SelectionAssistant.App/             ← composition root: Program / App / tray
├── SelectionAssistant.Core/            ← domain models, settings records, i18n
├── SelectionAssistant.Infrastructure/  ← config stores, logging, JSON serialization
├── SelectionAssistant.Platform.Abstractions/  ← platform-agnostic interfaces
├── SelectionAssistant.Platform.Windows/       ← Win32 P/Invoke, hooks, UIA, GDI
├── SelectionAssistant.Providers/      ← OpenAI-compatible translation / OCR client
└── SelectionAssistant.UI/             ← Avalonia windows, Ivory Jade theme, settings
tests/
├── SelectionAssistant.Core.Tests/
├── SelectionAssistant.Providers.Tests/
└── SelectionAssistant.Windows.IntegrationTests/
```

Architecture rationale lives in `docs/architecture/`. The roadmap and
audit findings live at `docs/BACKLOG-roadmap.md` and `docs/AUDIT-findings.md`.
Read them if you're doing anything non-trivial.

## Things that are easy to get wrong

These are project-specific landmines. Violating them will fail CI or break
the app at runtime:

- **i18n keys must be 1:1 across three files**: `Strings.cs` (property),
  `Strings_en.cs`, `Strings_zh_CN.cs`. The `StringsTests` suite guards this —
  a missing entry or typo fails the build. AXAML binds via
  `{x:Static i18n:Strings.X}`; code-behind uses `Strings.X`.
- **No reflection in the published app.** `PublishAot=true` +
  `TrimMode=full`. JSON serialization is hand-written `Utf8JsonReader/Writer`.
  Don't introduce `System.Text.Json` reflection-based (de)serialization or
  `Activator.CreateInstance`.
- **`[DllImport]` → `[LibraryImport]` migration is partial (66/112).** New
  P/Invoke should use `[LibraryImport]` with explicit `EntryPoint="...W"`
  where Win32 only exports the `W` variant. See `docs/AUDIT-findings.md`
  entry M4 for the trap list (`StringMarshalling.Utf16` ≠ `CharSet.Unicode`,
  bool params need `[MarshalAs(Bool)]`, etc.).
- **Secrets never go in source or config.** Use the `secret://` URI scheme +
  `ISecretStore` (DPAPI-encrypted at `%LOCALAPPDATA%\BYH\secrets\`). Never
  commit a real API key — even to a branch.
- **Single-instance mutex.** The app holds `Global\BYH_ByYourHand_SingleInstance`.
  Always `taskkill /F /IM BYH.exe` before publishing/redeploying — the running
  process locks the exe and will block file writes.

## Before you open a PR

- [ ] `dotnet build SelectionAssistant.slnx -c Release` → **0 warnings, 0 errors**
      (CI runs with `/warnaserror`, so warnings fail the build)
- [ ] `dotnet test SelectionAssistant.slnx` → all green
- [ ] If you touched AXAML or `Strings.*`, switch UI language to the other
      one and verify no residual hardcoded strings
- [ ] No secrets, API keys, or absolute user paths in your diff
- [ ] Commit message follows the existing style (see `git log`):
      `type(scope): summary` — e.g. `feat(clipboard): add export`,
      `fix(ocr): handle empty result`, `refactor(app): split god-class`

## What kinds of contributions help most

Right now the highest-leverage areas (also the DEFER list in CHANGELOG):

- **Accessibility**: `AutomationProperties.Name` across the AXAML files
  (audit finding L3 — needs a screen-reader verification pass)
- **Code structure**: the two god-classes (`ClipboardHistoryWindow.axaml.cs`
  ~2940 lines, `App.axaml.cs` ~2240 lines) would benefit from splitting
- **NativeAOT hygiene**: finishing the `[LibraryImport]` migration (46 sites
  remaining, all high-risk core paths)
- **New provider support**: BYH is OpenAI-compatible; adding native support
  for providers with non-standard auth is welcome

If your change is non-trivial, consider opening an issue first to check
direction — it saves wasted work.

## License

By contributing, you agree your contributions are licensed under the
project's [MIT license](LICENSE).
