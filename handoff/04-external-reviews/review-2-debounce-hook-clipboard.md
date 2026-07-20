## Overall verdict

**V2 is a major improvement and is directionally sound.** It now matches the intended product: customizable text actions, configurable model endpoints, clean-room implementation, cross-platform architecture, secure secret storage, and graceful capture fallback. The explicit clean-room statement also addresses the licensing concern from V1. 

I would mark it **“approved with required revisions.”** There are four implementation blockers and several smaller specification corrections.

## Required revisions

### 1. The 500 ms debounce contradicts the 150 ms toolbar target

The specification promises toolbar appearance at P95 under 150 ms, but detection waits 500 ms before beginning capture and showing the toolbar. Those two requirements cannot both be met.  

Replace the blocking debounce with a **selection session**:

1. Mouse-up creates a candidate session immediately.
2. Begin text capture immediately.
3. Apply a short 60–100 ms anti-flicker delay before showing the toolbar.
4. A second or third click updates or replaces the same session instead of waiting for the click sequence to finish.
5. Cancel prior capture through a `CancellationTokenSource`.

On Windows, do not hardcode 500 ms and 3 px for double-click detection. Use `GetDoubleClickTime`, `SM_CXDOUBLECLK`, and `SM_CYDOUBLECLK`. Likewise, use `SM_CXDRAG` and `SM_CYDRAG` for drag thresholds. These settings reflect the user’s configured mouse behavior. ([Microsoft Learn][1])

Also remove `DRAG_MAX_MS`. A user may legitimately select several paragraphs slowly.

### 2. Correct the Windows hook requirements

This section currently says the hook thread must be STA, highest priority, and meet an approximately 300 ms timeout. 

The corrected requirement should be:

> Install `WH_MOUSE_LL` on a dedicated thread with a Win32 message loop. Keep the delegate rooted for the lifetime of the hook. The callback must only capture event data, classify basic gestures, enqueue work, call `CallNextHookEx`, and return immediately.

STA is not inherently required for `WH_MOUSE_LL`, and highest thread priority should not be required—it can negatively affect the desktop. The timeout is registry-configured; on current Windows versions, values above 1,000 ms are capped at 1,000 ms. Microsoft recommends a dedicated hook thread that hands work to a worker thread. ([Microsoft Learn][2])

Additional detection changes:

* Compare the **root top-level window and process ID**, not exact child HWND equality.
* Treat cursor shape as a confidence signal, never a hard exclusion.
* Record injected-event flags from `MSLLHOOKSTRUCT` so the assistant does not react to its own synthetic input.
* Use a monotonic clock rather than wall-clock timestamps.

### 3. Change “full clipboard backup” to “best-effort preservation”

The plan claims full backup and restore, but only lists text, image, and file formats. 

Windows clipboard data can include registered application formats, owner-display formats, and delayed-rendered formats. Exact universal restoration is therefore not guaranteed. ([Microsoft Learn][3])

The specification should say:

> Preserve clipboard content on a best-effort basis. Snapshot all safely materializable formats up to configured size limits. Guarantee race-safe restoration of supported formats, but do not claim bit-for-bit preservation of every private or delayed-rendered format.

The state machine also needs these changes:

* Retry `OpenClipboard` with bounded backoff because another process may temporarily own it.
* Record `SEQ_B` **after the final clipboard update has stabilized**, not after the first update.
* Restore only when the current sequence still equals `SEQ_B`.
* If an application writes multiple formats asynchronously, reset a short stabilization timer after each update.
* Prefer `AddClipboardFormatListener` and `WM_CLIPBOARDUPDATE` over polling every 5 ms. Windows provides a system clipboard-change notification specifically for this purpose. ([Microsoft Learn][4])
* Do not automatically clear the clipboard when there was no backup unless the sequence still belongs to the assistant.

The fixed 135 ms PDF delay should become a configurable compatibility policy rather than a universal constant.

### 4. The no-focus toolbar needs native behavior, not “hacks”

The Windows section currently mentions `SetForegroundWindow hacks`. That is the opposite of the desired behavior because it attempts to activate the application. 

Use:

* `WS_EX_NOACTIVATE`
* `WS_EX_TOOLWINDOW`
* `WS_EX_TOPMOST`
* `ShowWindow(..., SW_SHOWNOACTIVATE)`
* `SetWindowPos(..., SWP_NOACTIVATE)`

Windows explicitly defines `WS_EX_NOACTIVATE` as preventing activation when the window is clicked, while `SW_SHOWNOACTIVATE` and `SWP_NOACTIVATE` display or reposition without activation. ([Microsoft Learn][5])

This should be a **Phase 0 hard gate**: prove that an Avalonia-hosted toolbar can receive pointer input without becoming active and without collapsing the source selection. If Avalonia cannot consistently deliver that behavior, use a small native toolbar host while retaining Avalonia for rendering and the result/settings windows.

For macOS, model the toolbar as a nonactivating panel rather than a normal application window. Also test activation behavior separately from visual transparency.

## Product requirement correction

### Include Translate, Explain, and Summarize as built-ins

The document currently includes only Translate and expects all other actions to be created through JSON. 

That is technically minimal, but it does not fully satisfy the original requested product experience. The MVP should ship with:

* Translate
* Explain
* Summarize
* Custom prompt

These use the same engine, so the engineering cost is negligible. More importantly, custom actions should be editable through the Settings UI—not require manual JSON editing.

JSON should remain the storage and export format, not the primary user interface.

Add the following to `ActionProfile`:

```json
{
  "schemaVersion": 1,
  "inputLimit": {
    "maxCharacters": 30000,
    "maxEstimatedTokens": 12000,
    "overflowBehavior": "AskUser"
  },
  "confirmBeforeSend": false,
  "resultFormat": "Markdown"
}
```

The template renderer should reject unknown variables and malformed templates with a visible validation error.

## Capture-chain corrections

### UI Automation

The UIA strategy is good, but the `LegacyIAccessiblePattern` pseudocode is too optimistic. Selected accessible children are not equivalent to the selected substring inside a text editor.

Use this order:

1. Cache the foreground HWND, process ID, focused element, and element under the mouse before showing UI.
2. Try `TextPattern2`.
3. Try `TextPattern`.
4. Search a bounded number of parent elements for a text pattern.
5. Fall through to simulated copy.

All accessibility calls should run on a dedicated worker with a strict timeout. A malfunctioning accessibility provider must not freeze the application.

Do not concatenate `Name + Value` as selected text; it may return the entire control or unrelated labels.

### Terminals and process policies

The statement that `Ctrl+C` always kills terminal processes is too broad. Behavior depends on the terminal, whether text is selected, and its key bindings.

Replace hardcoded blacklists with policies:

```text
Excluded
AccessibilityOnly
CopyWithCtrlInsertOnly
CopyAllowed
ManualOnly
DelayedClipboardRead
```

Ship sensible defaults, but permit user overrides by executable path, bundle identifier, or process name.

## macOS permissions need a capability model

The document represents macOS permissions as one Accessibility permission checked through `AXIsProcessTrusted`. 

Model these as two separate capabilities in code and onboarding:

* Global event observation
* Reading accessibility-selected text

Their actual permission behavior must be tested on each supported macOS release. The application should explain exactly which capability is unavailable and retain manual-hotkey operation where possible.

Also add the following macOS deliverables to Phase 5:

* Code signing
* Hardened runtime validation
* Notarization
* Permission behavior after application upgrades
* Intel and Apple Silicon packaging
* Start-at-login implementation
* Accessibility-permission reset/recovery testing

## Provider system changes

The full `baseUrl` design is correct and directly satisfies configurable host, path, and port requirements. The settings design is also appropriately provider-centric. 

For broad OpenAI-compatible support, I recommend implementing the core adapter with `HttpClient` and explicit JSON/SSE handling rather than tightly coupling it to one vendor SDK. “OpenAI-compatible” servers frequently differ in small ways.

Add provider capabilities such as:

```json
{
  "chatCompletionsPath": "/chat/completions",
  "modelsPath": "/models",
  "supportsModelListing": true,
  "supportsStreaming": true,
  "authentication": {
    "type": "Bearer"
  }
}
```

“Fetch model list” must remain optional because some gateways do not implement `/models`.

The streaming parser needs tests for:

* SSE frames split across network reads
* Multiple `data:` lines
* UTF-8 characters split across buffers
* Empty deltas
* Error objects returned mid-stream
* `[DONE]`
* Cancellation while waiting for the next frame

Azure should remain a separate adapter.

## Security wording

Rename **“Prompt injection defense”** to **“Prompt injection risk reduction.”**

Delimiters and instructions help, but they cannot guarantee that a model will ignore instructions embedded in selected text. Because the MVP has no tools or autonomous actions, the resulting risk is mostly incorrect output rather than system compromise.

Also add:

* First-use disclosure that selected text is sent to the configured provider.
* Per-application exclusions for password managers, finance tools, and sensitive internal applications.
* A local-provider-only mode.
* An option to confirm before sending selections from unknown applications.
* No automatic network request until the user clicks an action.
* Token-aware limits, not character limits alone.

For Windows secrets, clarify that DPAPI is encryption rather than a storage location:

> Prefer Windows Credential Manager. Alternatively, store a DPAPI-encrypted blob in the application data directory.

## Project structure

Split the current combined platform project into separately targeted assemblies:

```text
SelectionAssistant.Platform.Abstractions
SelectionAssistant.Platform.Windows   # net10.0-windows
SelectionAssistant.Platform.Mac       # macOS-specific target
```

This avoids pulling Windows UI Automation and macOS native bindings into the same NativeAOT compilation and makes platform-specific trimming, packaging, and testing clearer.

The rest of the project structure is reasonable.

## Timeline assessment

The proposed 20–30 working days is credible for a **functional alpha** built by someone already experienced with Win32 hooks, UI Automation, AppKit accessibility, Avalonia native handles, and macOS distribution. 

For a release-quality cross-platform MVP, I would classify the estimate as:

* Functional alpha: **20–30 days**
* Internal beta with broad application testing: **30–40 days**
* Public release with signed installers, onboarding, updates, crash handling, accessibility testing, and polish: **40–55 days**

The plan should explicitly label its estimate as alpha or beta scope.

## Review-point decisions

| Decision                          | Verdict                                                     |
| --------------------------------- | ----------------------------------------------------------- |
| .NET 10 + Avalonia                | **Approve**                                                 |
| NativeAOT                         | **Approve as Phase 0 experiment only**                      |
| Cross-platform from day one       | **Approve, but expect timeline expansion**                  |
| Two-window design                 | **Approve**                                                 |
| Four-tier capture chain           | **Approve after clipboard/UIA corrections**                 |
| Translate-only built-in           | **Revise: Translate + Explain + Summarize + Custom**        |
| OpenAI-compatible primary adapter | **Approve with capability-based raw HTTP implementation**   |
| Clean-room implementation         | **Approve**                                                 |
| 500 ms debounce                   | **Reject and replace with non-blocking selection sessions** |
| Full clipboard restoration claim  | **Reject; specify best-effort preservation**                |
| macOS single-permission model     | **Revise and validate experimentally**                      |

With those revisions, this becomes a strong implementation specification rather than merely a plausible architecture document.

[1]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdoubleclicktime "GetDoubleClickTime function (winuser.h) - Win32 apps | Microsoft Learn"
[2]: https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc "LowLevelMouseProc callback function - Win32 apps | Microsoft Learn"
[3]: https://learn.microsoft.com/en-us/windows/win32/dataxchg/using-the-clipboard "Using the Clipboard - Win32 apps | Microsoft Learn"
[4]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-addclipboardformatlistener "AddClipboardFormatListener function (winuser.h) - Win32 apps | Microsoft Learn"
[5]: https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles "Extended Window Styles (Winuser.h) - Win32 apps | Microsoft Learn"
