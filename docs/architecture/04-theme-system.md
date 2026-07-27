# BYH Theme System — Architecture Proposal

Status: **active design intent** (REQ-027 landed). Token contract, theme-pack
schema and the A/B/C/D delivery sequence below are still the design reference
for any future skin work.
Product term: Skin
Engineering term: Theme

> **实现现状**（色值、Brush 表、AXAML 文件位置、唯一事实源清单）见
> [`08-theme-system.md`](08-theme-system.md)。本文回答「为什么是这种架构」，
> 08 回答「现在长什么样」。两份互补，不冲突。

## 1. Recommendation

BYH should use a constrained theme-pack system, not an Electron-style arbitrary
CSS override layer.

Avalonia can switch resources at runtime and `DynamicResource` consumers update
automatically. The safe boundary for BYH is:

1. keep layout, commands, control templates, and behavior in compiled AXAML;
2. move visual values into a stable semantic token contract;
3. let built-in and user themes supply values for that contract;
4. allow only validated image/font choices from a theme manifest.

This provides high visual freedom while preserving NativeAOT compatibility and
the reliability of global-hotkey, capture, clipboard, launcher, and overlay
windows.

## 2. Why this is less free-form than Electron

Electron renders a DOM. A skin can inject CSS selectors and replace almost any
visual property, so experimentation is extremely convenient. The same freedom
also makes skins fragile: DOM/class changes break selectors, arbitrary CSS can
hide controls, and third-party scripts can affect behavior.

Avalonia uses compiled controls, styles, templates, and resource lookup. It has
no browser cascade that a user stylesheet can freely override. The advantage is
that a typed theme contract remains stable and testable. The trade-off is that
the application must explicitly expose every customizable dimension.

For BYH this is a good trade: the app is a small native utility, ships with
NativeAOT, and must keep transparent/no-activate overlays predictable.

## 3. Current readiness

The current code is already partially theme-ready:

- `IvoryJade.axaml` uses roughly 150 `DynamicResource` references.
- common controls consume semantic names such as `ByhSurfaceBrush`,
  `ByhPrimaryBrush`, `ByhBorderBrush`, and `ByhShadowMedium`;
- one application-level style include reaches all windows.

The remaining coupling must be removed before real switching:

- `IvoryJade.axaml` mixes palette values, gradients, shadows, and all component
  styles in one file;
- the theme file still contains many literal colors for derived materials;
- UI views contain many literal-color lines;
- `SettingsWindow.axaml` hard-codes Ivory Jade ornament/emblem asset URIs;
- theme name copy is currently static;
- the application forces `RequestedThemeVariant="Light"`;
- no appearance store or runtime theme service exists.

## 4. Target layers

### 4.1 Component layer — fixed

`Themes/ByhControls.axaml`

Contains selectors and templates only:

- Window, Button, TextBox, ComboBox, ToggleSwitch;
- SurfacePanel, InnerCard, MetallicFrame, ThemePill;
- Settings navigation, clipboard rows, spotlight rows;
- overlay-specific classes.

It references theme values exclusively through `DynamicResource`. A skin cannot
replace command bindings, templates, Automation IDs, layout, or hit testing.

### 4.2 Token layer — switchable

`ThemeDefinition` supplies a versioned contract:

- foundations: background, surface, secondary surface, selected surface;
- brand: primary, primary hover/soft, accent, accent hover/soft, highlight;
- text: primary, secondary, placeholder, on-primary;
- structure: border, subtle border, disabled, focus;
- feedback: success, warning, error, information;
- material: panel gradient, metallic edge, active navigation, atmosphere;
- shape: small/medium/large radius;
- depth: small/medium/deep shadow;
- typography: body/display family;
- artwork: emblem, ornament, wordmark and optional panel texture.

Derived materials should be generated from explicit theme fields or a small
number of documented algorithms. A new theme must not copy the entire component
stylesheet.

### 4.3 Theme manager — runtime

`IThemeManager` owns:

- installed theme discovery;
- manifest validation and fallback;
- temporary preview;
- apply/cancel semantics;
- persistence;
- change notification for view-model copy and artwork.

Applying a theme updates one application resource dictionary in place. Existing
controls using `DynamicResource` receive the new values. Built-in definitions
are compiled and trimming-safe.

Runtime `ResourceInclude`/external AXAML loading should not be used: Avalonia
documents runtime resource includes as potentially unsafe with trimming/AOT.

### 4.4 Persistence and theme packs

`%LOCALAPPDATA%\BYH\appearance.json`

```json
{
  "schemaVersion": 1,
  "activeThemeId": "ivory-jade",
  "customOverrides": {},
  "followSystemMode": false
}
```

`%LOCALAPPDATA%\BYH\themes\<theme-id>\theme.json`

```json
{
  "schemaVersion": 1,
  "id": "midnight-moss",
  "name": "Midnight Moss",
  "author": "Local user",
  "baseTheme": "ivory-jade",
  "tokens": {
    "background": "#101411",
    "surface": "#171C18",
    "primary": "#84924F",
    "accent": "#9B5359",
    "textPrimary": "#F1ECE4",
    "border": "#596149"
  },
  "shape": {
    "radiusScale": 1.0
  },
  "depth": {
    "shadowStrength": 0.8
  },
  "assets": {
    "ornament": "ornament.png",
    "emblem": "emblem.png"
  }
}
```

Rules:

- theme IDs are normalized and directory-contained;
- asset paths must remain inside their theme directory;
- only PNG/JPEG/WebP are accepted, with dimension and byte limits;
- missing optional tokens inherit from the declared built-in base;
- unknown required schema versions fail closed;
- no secrets, executable content, URI downloads, XAML, DLLs, or scripts.

## 5. Switching transaction

1. User selects a skin in Settings.
2. Theme manager snapshots the currently applied definition.
3. Selected theme is validated and applied as a temporary preview.
4. `Apply` writes `appearance.json` atomically.
5. `Cancel` restores the snapshot.
6. Startup loads the saved ID; any failure logs a warning and applies
   `ivory-jade`.

The preview window should display representative controls, but the whole
application should also update so transparent overlays and small utility
windows can be checked before committing.

## 6. Integrated Settings title bar

The Settings window should extend its client content into the Windows title-bar
region:

- `ExtendClientAreaToDecorationsHint="True"`;
- `WindowDecorations="Full"` so Windows keeps ownership of minimize, maximize,
  close, Snap Layout, accessibility, and platform behavior;
- a compact themed drag region, approximately 38 DIP high, marked with
  `WindowDecorationProperties.ElementRole="TitleBar"`;
- interactive items must stay outside the native caption-button inset;
- the root background and the title-bar background use the same theme material,
  with no second card, divider, or detached strip.

This produces one continuous surface rather than stacking a custom toolbar
below the operating-system title bar. The title-bar material participates in
skin switching, but window commands and hit-test behavior do not.

`WindowDecorations="None"` and hand-built caption buttons are deliberately not
the first choice. They would add avoidable work for resize borders, system
menus, keyboard accessibility, DPI scaling, hover states, and Windows 11 Snap
Layout.

## 7. Suggested Settings experience

Add an `Appearance` tab rather than hiding theme controls in General:

- top: horizontal gallery of installed skin cards;
- center: live specimen containing text, button, input, tags, panel, and overlay
  swatches;
- lower section: compact editor for palette, radius, shadow, and artwork;
- footer: `Try`, `Apply`, `Save as new`, `Restore`, `Import`, `Export`.

The first delivery should show Import/Export only after the local schema and
validation are proven. The editor should expose semantic roles, not a list of
internal `Byh...` resource keys.

The phone-style summary panel remains a compact secondary navigator. Its dock
uses real focusable buttons:

- Home → General;
- Gem → Translation;
- central leaf → Vision / Ocean Eyes;
- Mail → Clipboard;
- Gear → Launcher.

The selected state mirrors the primary left navigation. Hover, pressed, focus,
and selected feedback use the same Ivory Jade material vocabulary; the larger
central leaf remains visually dominant without becoming a separate command.

## 8. Delivery sequence

### Batch A — theme boundary and integrated title bar

- split `IvoryJade.axaml` into token/material and component layers;
- replace hard-coded theme name and artwork URIs with dynamic resources;
- migrate remaining high-impact literal colors;
- extend the Settings surface into the native title bar while retaining Windows
  caption buttons and drag behavior;
- add a resource-contract test that every built-in theme supplies required keys.

### Batch B — runtime switching

- add `ThemeDefinition`, `ThemeCatalog`, `ThemeManager`;
- add `AppearanceSettingsStore` with atomic persistence and safe fallback;
- add a second built-in theme to prove that switching is real;
- test open-window updates and NativeAOT.

### Batch C — Settings UI

- add Appearance navigation and skin gallery;
- implement preview/apply/cancel/reset;
- add live specimen and accessibility checks.

### Batch D — user customization

- add semantic token editor and local “Save as new skin”;
- validate contrast, formats, assets, and path containment;
- add import/export only after schema v1 is stable.

## 9. Explicit non-goals for v1

- arbitrary user AXAML or C# plugins;
- arbitrary layout/control-template replacement;
- per-window independent skins;
- animated/video backgrounds;
- online theme marketplace;
- automatic downloading of remote assets.

These can be reconsidered later without weakening the v1 safety boundary.
