using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SelectionAssistant.Core.Appearance;
using SelectionAssistant.Core.Capture;
using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Core.I18n;
using SelectionAssistant.Core.Input;
using SelectionAssistant.Core.Launcher;
using SelectionAssistant.Core.Translation;
using SelectionAssistant.Infrastructure.Configuration;
using SelectionAssistant.Infrastructure.Logging;
using SelectionAssistant.Platform.Abstractions;
using SelectionAssistant.Platform.Windows.Capture;
using SelectionAssistant.Platform.Windows.Clipboard;
using SelectionAssistant.Platform.Windows.Input;
using SelectionAssistant.Platform.Windows.Launcher;
using SelectionAssistant.Platform.Windows.Secrets;
using SelectionAssistant.UI.Views;

namespace SelectionAssistant.App;

public partial class App : Application
{
    private SelectionRuntime? _runtime;
    // R54 v2: installed-app detector for the Launcher "scan installed apps"
    // feature. Constructed eagerly (cheap — it just holds the Start Menu paths)
    // and invoked off the UI thread when the user requests a scan.
    private readonly IInstalledAppDetector _detector = new WindowsStartMenuDetector();

    // Audit M3: favicon fetch cache + shared HTTP client. Previously every
    // settings refresh (which fires on every save) called LoadLauncherIconsAsync
    // which called LoadFaviconAsync which (a) new'd a fresh HttpClient per URL
    // — defeating socket pooling, churning TIME_WAIT sockets — and (b) re-
    // fetched the same favicon for the same host on every refresh. Now:
    //   - One static HttpClient reuses the socket pool across all favicon
    //     fetches (DNS staleness is acceptable here: favicons rarely change
    //     host, and a stale-IP fetch fails fast → null → cached null → no
    //     retry storm).
    //   - An in-memory Dictionary<host, Bitmap?> caches the result per host
    //     for the process lifetime, so a host seen once is never re-fetched.
    // LocalApp launcher icons already have disk caching (cacheFile pattern);
    // this only affects the WebUrl path.
    private static readonly System.Net.Http.HttpClient _faviconHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> _faviconCache = new(StringComparer.OrdinalIgnoreCase);
    private TrayIcon? _trayIcon;
    private ToolbarWindow? _toolbarWindow;
    private ResultWindow? _resultWindow;
    private SettingsWindow? _settingsWindow;
    private PromptWindow? _promptWindow;
    // R24: region-select overlay for chord → draw-region OCR. R40: the panel
    // (QuickToolsWindow) is retired; Ctrl+Alt+Q now enters this overlay
    // directly and the OCR text flows into the shared ToolbarWindow.
    private RegionSelectOverlay? _regionOverlay;
    private WindowsGlobalHotKey? _oceanEyesHotKey;
    private OceanEyesTriggerSettings _oceanEyesTrigger = OceanEyesTriggerSettings.Default;
    private string? _oceanEyesLoadWarning;
    // R40 Ocean Eyes: screenshot save path + auto-save/clipboard/UIA-assist
    // toggles. Read by the runtime when Enter fires; hot-swappable from settings.
    private OceanEyesCaptureSettings _oceanEyesCapture = OceanEyesCaptureSettings.Default;

    // R32 Spotlight launcher-search panel — independent window + independent
    // global hotkey (default Ctrl+Alt+Space). Shares the same LauncherEntry
    // source as the retired QuickTools but is wired as a fully separate consumer.
    private SpotlightWindow? _spotlightWindow;
    private WindowsGlobalHotKey? _spotlightHotKey;
    private SpotlightTriggerSettings _spotlightTriggerSettings = SpotlightTriggerSettings.Default;
    private string? _spotlightLoadWarning;
    // R54: clipboard history — independent module. Owns its own window, hotkey,
    // feature settings, and a long-lived background listener (ClipboardHistoryService).
    private ClipboardHistoryWindow? _clipboardHistoryWindow;
    private WindowsGlobalHotKey? _clipboardHistoryHotKey;
    private ClipboardHistoryTriggerSettings _clipboardHistoryTriggerSettings = ClipboardHistoryTriggerSettings.Default;
    private ClipboardHistorySettings _clipboardHistorySettings = ClipboardHistorySettings.Default;
    private string? _clipboardHistoryLoadWarning;
    private ClipboardHistoryService? _clipboardHistoryService;
    // R37: toolbar built-in shortcut keys (Prompt/Copy/Paste, defaults R/C/V).
    private ToolbarShortcutSettings _toolbarShortcuts = ToolbarShortcutSettings.Default;
    // UI language. Loaded BEFORE any window is constructed so the Strings
    // static ctor (which reads AppLanguage.Current) picks the right dictionary
    // and XAML {x:Static loc:Strings.Xxx} bindings resolve correctly. Missing
    // ui-language.json → auto-detect from the OS UI culture.
    private AppLanguage _uiLanguage = AppLanguage.English;
    // REQ-027: user profile (greeting, theme preferences, phone summary views).
    private UserProfileSettings _userProfileSettings = UserProfileSettings.Default;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private ByhApplicationPaths? _paths;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _paths = ByhApplicationPaths.CreateDefault();
            _paths.EnsureDirectories();

            // UI language: read ui-language.json (or auto-detect from the OS on
            // first launch), then publish AppLanguage.Current + the thread UI
            // culture BEFORE any window is constructed. Strings' static ctor
            // snapshots AppLanguage.Current when the first Strings.Xxx member is
            // touched (which happens during XAML resolution in the window ctor),
            // so this ordering is what makes the right dictionary take effect.
            // A corrupt file falls back to OS detection rather than crashing.
            try
            {
                _uiLanguage = UiLanguageStore.LoadIfExists(_paths.UiLanguageFile);
            }
            catch (ProviderConfigurationException)
            {
                _uiLanguage = AppLanguage.DetectFromOS();
            }
            AppLanguage.Set(_uiLanguage);
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo(_uiLanguage.Code);
                CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
            }
            catch (CultureNotFoundException)
            {
                // Unknown culture code — leave the framework defaults in place;
                // AppLanguage.Current is still correct so Strings resolves right.
            }
            // CJK glyphs read roughly 1pt smaller than Latin at the same size
            // (denser strokes, smaller glyph aspect), so when the active UI
            // language is Chinese, bump every font-size token by +1pt. The
            // tokens live in IvoryJade.axaml's Styles.Resources with Latin
            // baseline values; writing them into Application.Current.Resources
            // here overrides those baselines for the whole app (Avalonia
            // resolves app-level resources above theme-level). Must run before
            // any window is constructed so the first measure pass uses the
            // Chinese sizes. English/other languages get no overrides — the
            // theme defaults apply and the UI is byte-identical to before.
            ApplyLanguageFontTokens(_uiLanguage);

            // R40: tell the trigger store where the legacy quick-tools.json lives
            // so a first launch can transparently migrate pre-R40 bindings. The
            // new file (ocean-eyes.json) is written on the next Save.
            OceanEyesTriggerStore.SetLegacyMigrationPath(_paths.QuickToolsTriggerFileLegacy);
            try
            {
                _oceanEyesTrigger = OceanEyesTriggerStore
                    .LoadIfExists(_paths.OceanEyesTriggerFile)
                    .Normalize();
            }
            catch (ProviderConfigurationException)
            {
                _oceanEyesTrigger = OceanEyesTriggerSettings.Default;
                _oceanEyesLoadWarning = "Invalid hotkey settings. Restored the safe Ctrl+Alt+Q default.";
            }

            // R40: Ocean Eyes screenshot/save settings (path + toggles). Missing
            // file = defaults (Pictures/Ocean Eyes, auto-save on, clipboard on,
            // UIA assist on). Used by SelectionRuntime when Enter fires.
            try
            {
                _oceanEyesCapture = OceanEyesCaptureStore
                    .LoadIfExists(_paths.OceanEyesCaptureFile)
                    .Normalize();
            }
            catch (ProviderConfigurationException)
            {
                _oceanEyesCapture = OceanEyesCaptureSettings.Default;
                _oceanEyesLoadWarning ??= "Invalid Ocean Eyes capture settings. Defaults restored.";
            }

            // R32: load the independent Spotlight trigger (default Ctrl+Alt+Space).
            try
            {
                _spotlightTriggerSettings = SpotlightTriggerStore
                    .LoadIfExists(_paths.SpotlightTriggerFile)
                    .Normalize();
            }
            catch (ProviderConfigurationException)
            {
                _spotlightTriggerSettings = SpotlightTriggerSettings.Default;
                _spotlightLoadWarning = "Invalid Spotlight hotkey settings. Restored the safe Ctrl+Alt+Space default.";
            }

            // R54: load the independent clipboard-history trigger (default Ctrl+Alt+V)
            // and feature settings (enabled / auto-paste / exclude-apps / max entries).
            try
            {
                _clipboardHistoryTriggerSettings = ClipboardHistoryTriggerStore
                    .LoadIfExists(_paths.ClipboardHistoryTriggerFile)
                    .Normalize();
            }
            catch (ProviderConfigurationException)
            {
                _clipboardHistoryTriggerSettings = ClipboardHistoryTriggerSettings.Default;
                _clipboardHistoryLoadWarning = "Invalid clipboard history hotkey settings. Restored the safe Ctrl+Alt+V default.";
            }
            try
            {
                _clipboardHistorySettings = ClipboardHistorySettingsStore
                    .LoadIfExists(_paths.ClipboardHistorySettingsFile)
                    .Normalize();
            }
            catch (ProviderConfigurationException)
            {
                _clipboardHistorySettings = ClipboardHistorySettings.Default;
                _clipboardHistoryLoadWarning ??= "Invalid clipboard history settings. Defaults restored.";
            }

            // R37: load the toolbar built-in shortcut keys (Prompt/Copy/Paste,
            // defaults R/C/V). Missing file = built-in defaults (transparent
            // upgrade for existing users — no schema bump).
            try
            {
                _toolbarShortcuts = ToolbarShortcutsStore
                    .LoadIfExists(_paths.ToolbarShortcutsFile)
                    .Normalize();
            }
            catch (ProviderConfigurationException)
            {
                _toolbarShortcuts = ToolbarShortcutSettings.Default;
                _spotlightLoadWarning ??= "Invalid toolbar shortcuts. Safe defaults restored.";
            }

            string? userProfileLoadWarning = null;
            try
            {
                _userProfileSettings = UserProfileStore.LoadIfExists(_paths.UserProfileFile);
            }
            catch (ProviderConfigurationException)
            {
                _userProfileSettings = UserProfileSettings.Default;
                userProfileLoadWarning = "Invalid profile settings. Windows username restored.";
            }

            var toolbarWindow = new ToolbarWindow();
            var resultWindow = new ResultWindow();
            var settingsWindow = new SettingsWindow();
            var promptWindow = new PromptWindow();
            var spotlightWindow = new SpotlightWindow();
            var clipboardHistoryWindow = new ClipboardHistoryWindow();
            _toolbarWindow = toolbarWindow;
            _resultWindow = resultWindow;
            _settingsWindow = settingsWindow;
            _promptWindow = promptWindow;
            _spotlightWindow = spotlightWindow;
            _clipboardHistoryWindow = clipboardHistoryWindow;
            // R54: apply the user-configured default window sizes. The windows
            // are constructed once and reused via Show/Hide, so setting
            // Width/Height here covers every subsequent open this session.
            // (Manual resizes of ClipboardHistoryWindow persist across Show/Hide
            // because the instance is reused; on the next app launch this code
            // re-applies the saved default.)
            spotlightWindow.Width = _spotlightTriggerSettings.WindowWidth;
            spotlightWindow.Height = _spotlightTriggerSettings.WindowHeight;
            clipboardHistoryWindow.Width = _clipboardHistorySettings.WindowWidth;
            clipboardHistoryWindow.Height = _clipboardHistorySettings.WindowHeight;
            settingsWindow.Configure(_paths.CapturePolicyFile);
            settingsWindow.SetUserProfileSettings(
                _userProfileSettings,
                userProfileLoadWarning,
                isError: userProfileLoadWarning is not null);
            settingsWindow.SetOceanEyesTriggerSettings(
                _oceanEyesTrigger,
                _oceanEyesLoadWarning,
                isError: _oceanEyesLoadWarning is not null);
            settingsWindow.SetOceanEyesCaptureSettings(_oceanEyesCapture);
            settingsWindow.SetSpotlightTriggerSettings(
                _spotlightTriggerSettings,
                _spotlightLoadWarning,
                isError: _spotlightLoadWarning is not null);
            settingsWindow.SetClipboardHistoryTriggerSettings(
                _clipboardHistoryTriggerSettings,
                _clipboardHistoryLoadWarning,
                isError: _clipboardHistoryLoadWarning is not null);
            settingsWindow.SetClipboardHistorySettings(_clipboardHistorySettings);
            settingsWindow.SetUiLanguage(_uiLanguage);
            desktop.MainWindow = toolbarWindow;

            settingsWindow.OpenConfigDirectoryRequested += () => OpenDirectory(_paths.BaseDirectory);
            settingsWindow.OpenLogDirectoryRequested += () => OpenDirectory(_paths.LogsDirectory);
            settingsWindow.ExitRequested += RequestExit;
            // Language switch: persist the choice then trigger the existing
            // Mutex-handoff restart path. Strings' static ctor only re-runs in
            // a fresh process, so a restart (not a live refresh) is what makes
            // the new language actually take effect across all XAML bindings.
            settingsWindow.UiLanguageSaved += OnUiLanguageSaved;
            settingsWindow.SetActiveProviderRequested += OnSetActiveProvider;
            settingsWindow.AddProviderFromPresetRequested += OnAddProviderFromPreset;
            settingsWindow.SaveProviderRequested += OnSaveProvider;
            settingsWindow.DeleteProviderRequested += OnDeleteProvider;
            settingsWindow.ApiKeySaveRequested += OnApiKeySaveRequested;
            settingsWindow.FetchModelsRequested += OnFetchModelsRequested;
        settingsWindow.PromptTemplateSaved += OnPromptTemplateSaved;
        settingsWindow.PromptTemplateReset += OnPromptTemplateReset;
        settingsWindow.PromptTemplateAdded += OnPromptTemplateAdded;
        settingsWindow.PromptTemplateDeleted += OnPromptTemplateDeleted;
            settingsWindow.LauncherEntryAdded += OnLauncherEntryAdded;
            settingsWindow.LauncherEntrySaved += OnLauncherEntrySaved;
            settingsWindow.LauncherEntryDeleted += OnLauncherEntryDeleted;
            settingsWindow.LauncherEntryMoved += OnLauncherEntryMoved;
            settingsWindow.ScanInstalledAppsRequested += OnScanInstalledAppsRequested;
            settingsWindow.VisionSettingsSaved += OnVisionSettingsSaved;
            settingsWindow.OceanEyesTriggerSettingsSaved += OnOceanEyesTriggerSettingsSaved;
            settingsWindow.OceanEyesCaptureSettingsSaved += OnOceanEyesCaptureSettingsSaved;
            settingsWindow.ToolbarShortcutsSaved += OnToolbarShortcutsSaved;
            settingsWindow.UserProfileSettingsSaved += OnUserProfileSettingsSaved;
            toolbarWindow.PromptRequested += OnPromptRequested;
            promptWindow.PromptRunRequested += OnPromptRun;

            // R32 Spotlight: independent window, reuses the same launch flow but
            // is wired to its own hotkey + own toggle logic.
            settingsWindow.SpotlightTriggerSettingsSaved += OnSpotlightTriggerSettingsSaved;
            spotlightWindow.LauncherRunRequested += OnLauncherRunRequested;
            spotlightWindow.LauncherEditRequested += OnSpotlightLauncherEditRequested;
            spotlightWindow.SettingsRequested += OnSpotlightSettingsRequested;

            // R54 Clipboard history: independent window + background listener.
            settingsWindow.ClipboardHistoryTriggerSettingsSaved += OnClipboardHistoryTriggerSettingsSaved;
            settingsWindow.ClipboardHistorySettingsSaved += OnClipboardHistorySettingsSaved;
            settingsWindow.ClipboardHistoryClearRequested += OnClipboardHistoryClearRequested;
            clipboardHistoryWindow.PasteRequested += OnClipboardHistoryPasteRequested;
            clipboardHistoryWindow.CopyRequested += OnClipboardHistoryCopyRequested;
            clipboardHistoryWindow.PinToggled += OnClipboardHistoryPinToggled;
            clipboardHistoryWindow.DeleteRequested += OnClipboardHistoryDeleteRequested;
            // R54 v2: per-entry annotation tags (independent of custom-tag tabs).
            clipboardHistoryWindow.EntryTagAdded += OnClipboardHistoryEntryTagAdded;
            clipboardHistoryWindow.EntryTagRemoved += OnClipboardHistoryEntryTagRemoved;
            clipboardHistoryWindow.ClearOlderRequested += OnClipboardHistoryClearOlderRequested;
            clipboardHistoryWindow.PreviewClearOlderRequested += OnClipboardHistoryPreviewClearOlder;
            clipboardHistoryWindow.SettingsRequested += OnClipboardHistorySettingsRequested;
            // R54 v1.1: tag management (custom tags + favorite + per-entry assign).
            clipboardHistoryWindow.CreateCustomTagRequested += OnClipboardHistoryCreateCustomTag;
            clipboardHistoryWindow.RenameCustomTagRequested += OnClipboardHistoryRenameCustomTag;
            clipboardHistoryWindow.DeleteCustomTagRequested += OnClipboardHistoryDeleteCustomTag;
            clipboardHistoryWindow.AssignTagRequested += OnClipboardHistoryAssignTag;
            clipboardHistoryWindow.UnassignTagRequested += OnClipboardHistoryUnassignTag;
            clipboardHistoryWindow.FavoriteToggled += OnClipboardHistoryFavoriteToggled;
            // R54 v2: built-in group override (fix a wrong auto-classification).
            clipboardHistoryWindow.GroupOverrideRequested += OnClipboardHistoryGroupOverride;
            // R54 v1.2: icon + drag-to-reorder.
            clipboardHistoryWindow.SetTagIconRequested += OnClipboardHistorySetTagIcon;
            clipboardHistoryWindow.ReorderTagRequested += OnClipboardHistoryReorderTag;
            // R54 v1.2 v6: user-imported icon library.
            clipboardHistoryWindow.ImportIconsRequested += OnClipboardHistoryImportIcons;
            clipboardHistoryWindow.RemoveUserIconRequested += OnClipboardHistoryRemoveUserIcon;

            // R24/R40: region-select overlay. R40 entry point: Ctrl+Alt+Q goes
            // straight into the overlay (no panel). On confirm → capture PNG →
            // show the shared toolbar at the region's top-right corner → OCR
            // feeds text into the toolbar (F/J/Z/R/C/V then work unchanged).
            _regionOverlay = new RegionSelectOverlay();
            _regionOverlay.RegionSelected += OnRegionSelected;
            // R42: overlay Cancel (Esc before confirm / toggle hotkey) — overlay
            // already hid itself; if runtime is active, clean up toolbar + state.
            _regionOverlay.RegionCancelled += () =>
                _runtime?.ResetForRedraw();
            // R42: overlay Reset (right-click redraw after confirm) — clear
            // toolbar + OCR state but keep overlay visible for redraw.
            _regionOverlay.RegionReset += () =>
                _runtime?.ResetForRedraw();

            _trayIcon = CreateTrayIcon();

            toolbarWindow.Opened += (_, _) =>
            {
                if (_runtime is not null)
                {
                    return;
                }

                _runtime = new SelectionRuntime(toolbarWindow, resultWindow, _paths);
                _runtime.Start();
                _runtime.SetMouseChordEnabled(_oceanEyesTrigger.MouseChordEnabled);
                // R37: push the toolbar built-in shortcut bindings (Prompt/Copy/Paste).
                _runtime.SetToolbarShortcuts(_toolbarShortcuts);
                // R40: push the Ocean Eyes capture settings (path + toggles).
                _runtime.SetOceanEyesCaptureSettings(_oceanEyesCapture);
                RegisterInitialOceanEyesHotKey();
                RegisterInitialSpotlightHotKey();

                // R54: start the clipboard-history background listener. The
                // service owns a long-lived Win32Clipboard (separate message
                // window) + the persisted JSON store. Created after the runtime
                // is up so the existing keyboard-hook/clipboard-window startup
                // ordering is undisturbed (see SelectionRuntime ctor comment).
                var clipboardLogger = new RedactedLogger(_paths.LogFile);
                try
                {
                    var clipboard = new Win32Clipboard();
                    // R54 v2 Phase 2: encrypt sensitive entries' text in the
                    // persisted JSON via DPAPI (CurrentUser scope). Constructed
                    // unconditionally — if construction itself ever fails it
                    // falls into the outer catch and the service stays null.
                    var entryCipher = new DpapiEntryCipher();
                    _clipboardHistoryService = new ClipboardHistoryService(
                        clipboard,
                        _paths.ClipboardHistoryFile,
                        _paths.ClipboardHistoryTagsFile,
                        _paths.ClipboardHistoryIconLibraryFile,
                        _paths.ClipboardImagesDirectory,
                        _clipboardHistorySettings,
                        clipboardLogger,
                        entryCipher,
                        _paths.ClipboardArchiveDirectory);
                }
                catch (Exception exception)
                {
                    _clipboardHistoryService = null;
                    clipboardLogger.Error("ClipboardHistory", "Failed to start service.", exception);
                }
                RegisterInitialClipboardHistoryHotKey();
                // Chord (L+R buttons) → Ocean Eyes region-select overlay at
                // cursor. The event fires on the mouse-hook thread; marshal to
                // the UI thread.
                _runtime.ChordTriggered += (x, y) =>
                    Dispatcher.UIThread.Post(() => EnterOceanEyesAt(x, y));

                // R42: when the runtime dismisses Ocean Eyes (Esc / Enter /
                // action key), close the overlay too. DismissOceanEyes fires
                // this on the UI thread.
                _runtime.DismissOverlay = () =>
                {
                    if (_regionOverlay?.IsVisible == true)
                    {
                        _regionOverlay.Cancel();
                    }
                };

                // R47: pass the overlay reference so the runtime can draw
                // numbered badges on its AnnotationCanvas.
                _runtime.AnnotationOverlay = _regionOverlay;

                // Push the current custom functions into the toolbar's "more"
                // row so user-added actions appear immediately.
                var templates = _runtime.GetPromptTemplates().AsList();
                toolbarWindow.SetActions(templates);

                // R23: also push launcher entries to Spotlight (icons lazy-load).
                var launcherEntries = _runtime.GetLauncherEntries().AsList();
                spotlightWindow.SetLauncherEntries(launcherEntries);
                _ = LoadLauncherIconsAsync(launcherEntries);

                if (desktop.Args?.Contains("--open-settings", StringComparer.OrdinalIgnoreCase) == true)
                {
                    RefreshAndShowSettings();
                }
            };

            desktop.Exit += (_, _) => DisposeApplicationResources();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ── Multi-provider CRUD handlers ──

    private async void OnSetActiveProvider(string providerId)
    {
        if (_runtime is null) return;
        await _runtime.SetDefaultProviderAsync(providerId);
        await RefreshSettingsAsync();
    }

    private async void OnAddProviderFromPreset(string presetId)
    {
        if (_runtime is null) return;

        string addedId;
        if (presetId == SelectionAssistant.Core.Translation.ProviderPresets.CustomPresetId)
        {
            // Add a blank custom provider the user can edit.
            addedId = "custom-" + Guid.NewGuid().ToString("N")[..8];
            var entry = new ProviderProfileEntry(
                Id: addedId,
                Name: "Custom Provider",
                BaseUrl: "https://",
                ApiKeyReference: SelectionAssistant.Core.Translation.ProviderPresets.BuildSecretReference(addedId),
                DefaultModel: "gpt-4o-mini",
                ChatCompletionsPath: "chat/completions",
                TimeoutSeconds: 60,
                MaxSourceCharacters: 8000);
            await _runtime.AddProviderAsync(entry);
        }
        else
        {
            // Find the preset and create an entry from it.
            var preset = SelectionAssistant.Core.Translation.ProviderPresets.BuiltIn
                .FirstOrDefault(p => p.Id == presetId);
            if (preset is null) return;
            addedId = preset.Id;

            var entry = new ProviderProfileEntry(
                Id: preset.Id,
                Name: preset.Name,
                BaseUrl: preset.BaseUrl,
                ApiKeyReference: SelectionAssistant.Core.Translation.ProviderPresets.BuildSecretReference(preset.Id),
                DefaultModel: preset.DefaultModel,
                ChatCompletionsPath: preset.ChatCompletionsPath,
                TimeoutSeconds: 60,
                MaxSourceCharacters: 8000);
            await _runtime.AddProviderAsync(entry);
        }

        await RefreshSettingsAsync();

        // Land the user on the provider they just added (not the default), so
        // they can immediately fill in the API key. Previously the combo snapped
        // back to the default provider after refresh.
        if (_settingsWindow is not null)
        {
            _settingsWindow.SelectProviderForEditing(addedId);
        }
    }

    private async void OnSaveProvider(ProviderProfileEntry entry)
    {
        if (_runtime is null) return;
        await _runtime.UpdateProviderAsync(entry);
        await RefreshSettingsAsync();
    }

    private async void OnDeleteProvider(string providerId)
    {
        if (_runtime is null) return;
        await _runtime.DeleteProviderAsync(providerId);
        await RefreshSettingsAsync();
    }

    private async void OnApiKeySaveRequested(string apiKeyReference, string keyValue)
    {
        if (_runtime is null) return;
        await _runtime.SaveApiKeyAsync(apiKeyReference, keyValue);
        await RefreshSettingsAsync();
    }

    // ── R26: "Refresh Models" handler ──
    //
    // The window raises FetchModelsRequested(providerId); we ask the runtime
    // to GET {BaseUrl}/models + update the on-disk cache, then return the
    // fresh list + timestamp + error to the window (which owns the UI state).
    // Mirrors the existing "window raises event, App awaits runtime" pattern.

    private async Task<(IReadOnlyList<string> Models, DateTime FetchedAtUtc, string? Error)> OnFetchModelsRequested(
        string providerId)
    {
        if (_runtime is null)
        {
            return (Array.Empty<string>(), DateTime.UtcNow, "runtime not initialized.");
        }
        return await _runtime.FetchProviderModelsAsync(providerId, CancellationToken.None);
    }

    // ── Prompt template handlers (R1 global templates) ──

    private async void OnPromptTemplateSaved(
        string actionId, string prompt, bool thinkingEnabled, string? shortcut,
        LocalizedName? newName)
    {
        if (_runtime is null) return;
        await _runtime.SavePromptTemplateAsync(actionId, prompt, thinkingEnabled, shortcut, newName);
        await RefreshSettingsAsync();
    }

    private async void OnPromptTemplateReset(string actionId)
    {
        if (_runtime is null) return;
        await _runtime.ResetPromptTemplateAsync(actionId);
        await RefreshSettingsAsync();
    }

    private async void OnPromptTemplateAdded(
        LocalizedName name, string prompt, bool thinkingEnabled, string? shortcut)
    {
        if (_runtime is null) return;
        await _runtime.AddPromptTemplateAsync(name, prompt, thinkingEnabled, shortcut);
        await RefreshSettingsAsync();
    }

    private async void OnPromptTemplateDeleted(string actionId)
    {
        if (_runtime is null) return;
        await _runtime.DeletePromptTemplateAsync(actionId);
        await RefreshSettingsAsync();
    }

    // ── R23 launcher handlers ──
    // Each handler runs the runtime method then refreshes everything. The
    // refresh pushes the new entries into both QuickTools and Settings, and
    // re-triggers async icon loading.

    private async void OnLauncherEntryAdded(
        string name, LauncherKind kind, string target, string args, string workDir)
    {
        if (_runtime is null) return;
        await _runtime.AddLauncherEntryAsync(name, kind, target, args, workDir);
        await RefreshSettingsAsync();
    }

    /// <summary>
    /// "🔍 扫描已安装应用" clicked. Runs the Start Menu detector off the UI
    /// thread (~100-500ms), dedupes against existing launcher entries by target
    /// path, then shows a selection dialog. Whatever the user confirms is
    /// batch-added with <c>IsAutoDetected=true</c>, then a refresh propagates
    /// the new rows (and kicks off lazy icon loading) to both windows.
    /// </summary>
    private async void OnScanInstalledAppsRequested()
    {
        if (_runtime is null || _settingsWindow is null) return;

        // 1. Scan off-thread (it reads hundreds of .lnk files).
        IReadOnlyList<DetectedApp> allDetected =
            await Task.Run(() => _detector.DetectInstalledApps());

        // 2. Dedupe against entries the user already added (by target path) so
        // the dialog only shows genuinely new apps.
        var existing = _runtime.GetLauncherEntries().AsList();
        var existingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in existing)
        {
            if (!string.IsNullOrEmpty(e.Target))
            {
                existingTargets.Add(e.Target);
            }
        }
        var candidates = allDetected
            .Where(a => !existingTargets.Contains(a.ExecutablePath))
            .ToList();
        if (candidates.Count == 0)
        {
            // Nothing new — don't even open the dialog.
            _settingsWindow.SetLauncherSettingsStatus(Strings.Settings_Launcher_NoNewApps, isError: false);
            return;
        }

        // 3. Show the selection dialog. The iconExtractor callback runs on a
        // background thread per row and returns PNG bytes (or null); the dialog
        // marshals each result to the UI thread itself.
        List<DetectedApp> selected = await InstalledAppsScanDialog.ShowAsync(
            _settingsWindow,
            candidates,
            iconExtractor: exePath => SelectionAssistant.Platform.Windows.Launcher
                .WindowsIconExtractor.ExtractSmallIconPng(exePath));

        if (selected.Count == 0)
        {
            return;
        }

        // 4. Batch-add with IsAutoDetected=true (one persist, not N).
        int added = await _runtime.AddAutoDetectedLauncherEntriesAsync(selected);

        // 5. Refresh pushes the new rows + triggers icon loading.
        await RefreshSettingsAsync();

        if (_settingsWindow is not null)
        {
            _settingsWindow.SetLauncherSettingsStatus(
                added > 0 ? string.Format(Strings.Settings_Launcher_Imported, added) : Strings.Settings_Launcher_NoneImported,
                isError: false);
        }
    }

    private async void OnLauncherEntrySaved(
        string id, string name, LauncherKind kind, string target, string args, string workDir)
    {
        if (_runtime is null) return;
        await _runtime.SaveLauncherEntryAsync(id, name, kind, target, args, workDir);
        await RefreshSettingsAsync();
    }

    private async void OnLauncherEntryDeleted(string id)
    {
        if (_runtime is null) return;
        await _runtime.DeleteLauncherEntryAsync(id);
        await RefreshSettingsAsync();
    }

    private async void OnLauncherEntryMoved(string id, int delta)
    {
        if (_runtime is null) return;
        await _runtime.MoveLauncherEntryAsync(id, delta);
        await RefreshSettingsAsync();
    }

    /// <summary>
    /// User clicked a launcher row in QuickTools. Runs the launch flow:
    /// expand {clip}/{sel} → if {prompt:...} present, show a modal input dialog
    /// for each prompt → actually start the entry. Reports errors silently.
    /// </summary>
    private async void OnLauncherRunRequested(string entryId, string? selectedText, string? clipText)
    {
        if (_runtime is null) return;
        try
        {
            LauncherLaunchResult result = await _runtime.StartLauncherLaunchAsync(entryId, clipText, selectedText);
            if (result.NeedsPrompt)
            {
                await CollectPromptAnswersAndCompleteAsync(result.Prompts);
            }
            else if (!result.Success && !string.IsNullOrEmpty(result.ErrorMessage))
            {
                // Surface only real errors (silent success otherwise — the app
                // launched, no need to nag the user with a popup).
                System.Diagnostics.Debug.WriteLine($"Launcher error: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Launcher run failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows one ParameterInputDialog per prompt, collecting answers in order,
    /// then completes the pending launch with the answers. Cancels the pending
    /// launch if the user dismisses any dialog.
    /// </summary>
    private async Task CollectPromptAnswersAndCompleteAsync(IReadOnlyList<string> prompts)
    {
        var answers = new List<string>(prompts.Count);
        foreach (string prompt in prompts)
        {
            string? answer = await ShowParameterDialogAsync(prompt);
            if (answer is null)
            {
                _runtime?.CancelPendingLaunch();
                return;
            }
            answers.Add(answer);
        }
        await _runtime!.CompleteLauncherLaunchAsync(answers);
    }

    /// <summary>
    /// Shows the ParameterInputDialog modally and returns the user's input, or
    /// null if they cancelled. Implements a simple TaskCompletionSource pattern
    /// over the dialog's Confirmed/Cancelled events.
    /// </summary>
    private Task<string?> ShowParameterDialogAsync(string prompt)
    {
        var dialog = new ParameterInputDialog();
        var tcs = new TaskCompletionSource<string?>();
        dialog.Confirmed += value =>
        {
            tcs.TrySetResult(value);
        };
        dialog.Cancelled += () =>
        {
            tcs.TrySetResult(null);
        };
        // If the user closes the window via the OS (X button) the events may
        // never fire; guard with a Closed handler.
        dialog.Closed += (_, _) =>
        {
            tcs.TrySetResult(null);
        };
        dialog.Show(prompt);
        return tcs.Task;
    }

    // ── Prompt Now handlers (R2) ──

    /// <summary>
    /// Toolbar "Prompt" clicked: open the prompt window seeded with the selection.
    /// R38 fix: also hide the toolbar + disable its keyboard hook first, so the
    /// toolbar and prompt window don't coexist on screen (matches what happens
    /// when translate/summarize/explain is clicked). The prompt window's "Run"
    /// button later calls RunPromptAsync, which is a no-op on the already-hidden
    /// toolbar.
    /// </summary>
    private void OnPromptRequested(string selectedText)
    {
        _runtime?.HideToolbarAndDisableHook();
        _promptWindow?.ShowForSelection(selectedText);
    }

    /// <summary>Prompt window "Run" clicked: run the custom prompt via the active provider.</summary>
    private void OnPromptRun(string selectedText, string userPrompt)
    {
        _runtime?.RunPromptAsync(selectedText, userPrompt);
    }

    // ── R40 Ocean Eyes trigger handlers (global hotkey + optional chord) ──
    // Formerly "QuickTools": the panel is gone; the hotkey now drops straight
    // into the full-screen region-select overlay. On confirm, the captured PNG
    // flows into the shared ToolbarWindow (F/J/Z/R/C/V work unchanged) and
    // OCR seeds the text asynchronously. Enter saves the PNG, Esc cancels.

    /// <summary>
    /// R40: hotkey / chord fired at (x, y). Enters the region-select overlay at
    /// the cursor (UIA assist on by default; the overlay's user-touched latch
    /// still wins once the user draws). If the overlay is already visible, the
    /// second press cancels instead — mirrors the old panel's toggle behavior.
    /// </summary>
    private void OnOceanEyesTriggered(int x, int y)
    {
        if (_regionOverlay?.IsVisible == true)
        {
            _regionOverlay.Cancel();
            return;
        }
        EnterOceanEyesAt(x, y);
    }

    /// <summary>
    /// R40: opens the region-select overlay at (x, y). Shared by the global
    /// hotkey path (which has a real cursor position) and the chord path
    /// (same). Defers one render frame so any prior surface (e.g. Spotlight)
    /// has time to leave the compositor before UIA samples the point.
    /// </summary>
    private void EnterOceanEyesAt(int x, int y)
    {
        if (_regionOverlay is null || _runtime is null)
        {
            return;
        }

        var runtime = _runtime;
        var overlay = _regionOverlay;
        bool uiaAssist = runtime.GetOceanEyesCaptureSettings().UiaAssistEnabled
            && !IsForegroundOwnedByCurrentProcess();

        var timer = new DispatcherTimer { Interval = TimeSpan.Zero };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (uiaAssist)
            {
                // Live UIA tracking: hover an element → its bounding box becomes
                // the preselection. Stops the moment the user draws/edits.
                overlay.EnableLiveTracking((px, py) =>
                {
                    if (runtime.GetInitialRegionAt(px, py) is { } live)
                    {
                        return new OverlayRect(live.X, live.Y, live.Width, live.Height);
                    }
                    return null;
                });
            }
            else
            {
                overlay.EnableLiveTracking(null);
            }

            OverlayRect? initial = null;
            if (uiaAssist && runtime.GetInitialRegionAt(x, y) is { } rect)
            {
                initial = new OverlayRect(rect.X, rect.Y, rect.Width, rect.Height);
            }

            overlay.ShowWithInitialRect(initial);
        };
        timer.Start();
    }

    /// <summary>
    /// Returns true when the current foreground window belongs to BYH itself
    /// (Settings / Spotlight / Clipboard History / Prompt / Result / toolbar).
    /// Used by <see cref="EnterOceanEyesAt"/> to skip the UIA prefill + live
    /// tracking when OE is triggered over our own UI.
    /// </summary>
    /// <remarks>
    /// <b>Why this exists.</b> OE's live UIA tracking polls
    /// <c>ElementFromPoint</c> on the region overlay's MouseMove (~25 Hz), and
    /// each poll blocks the UI thread via <c>GetElementBoundsAt</c>'s
    /// <c>done.Wait</c>. When the foreground window is BYH's own Settings/Spot
    /// light window, UIA must walk Avalonia's on-demand automation-peer tree
    /// (hundreds of peers) for every poll — each query jumps from &lt;30ms to
    /// several hundred ms, so the UI thread is starved and the overlay/input
    /// freezes ("设置窗口在前台的时候使用OE会卡得用不了"). The overlay is marked
    /// invisible to UIA, so it punches through to whatever is below; we can't
    /// exclude BYH windows from that walk cheaply.
    /// <para>
    /// The cheap, correct fix: detect "foreground is ours" by PID and skip
    /// UIA assist for that session entirely. The user never wants to OCR BYH's
    /// own UI anyway, and free-draw always works. We compare PIDs (not HWND
    /// identity) so any BYH window — settings, spotlight, clipboard history —
    /// trips the gate.
    /// </para>
    /// </remarks>
    private static bool IsForegroundOwnedByCurrentProcess()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return false;
        }
        GetWindowThreadProcessId(foreground, out uint pid);
        return pid == Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    private void RegisterInitialOceanEyesHotKey()
    {
        if (!_oceanEyesTrigger.KeyboardShortcutEnabled)
        {
            _settingsWindow?.SetOceanEyesTriggerSettings(
                _oceanEyesTrigger,
                Strings.Settings_Status_HotkeyDisabled,
                isError: false);
            return;
        }

        try
        {
            _oceanEyesHotKey = CreateStartedHotKey(_oceanEyesTrigger);
            _settingsWindow?.SetOceanEyesTriggerSettings(
                _oceanEyesTrigger,
                string.Format(Strings.Settings_Status_Registered, _oceanEyesTrigger.ToDisplayText()),
                isError: false);
        }
        catch (Exception exception) when (exception is GlobalHotKeyRegistrationException or TimeoutException)
        {
            _settingsWindow?.SetOceanEyesTriggerSettings(
                _oceanEyesTrigger,
                exception.Message,
                isError: true);
        }
    }

    private WindowsGlobalHotKey CreateStartedHotKey(OceanEyesTriggerSettings settings)
    {
        var registration = new WindowsGlobalHotKey(settings);
        registration.Triggered += (x, y) =>
            Dispatcher.UIThread.Post(() => OnOceanEyesTriggered(x, y));
        try
        {
            registration.Start();
            return registration;
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    // ── R32 Spotlight hotkey ──
    // The WindowsGlobalHotKey class is typed against OceanEyesTriggerSettings;
    // we adapt SpotlightTriggerSettings to that shape by mapping the shared
    // fields (Modifiers/Key/KeyboardShortcutEnabled) and forcing MouseChord
    // off (Spotlight has no chord).

    private static OceanEyesTriggerSettings ToOceanEyesShape(SpotlightTriggerSettings s) => new()
    {
        KeyboardShortcutEnabled = s.KeyboardShortcutEnabled,
        Modifiers = s.Modifiers,
        Key = s.Key,
        MouseChordEnabled = false,
    };

    private static OceanEyesTriggerSettings ToOceanEyesShape(ClipboardHistoryTriggerSettings s) => new()
    {
        KeyboardShortcutEnabled = s.KeyboardShortcutEnabled,
        Modifiers = s.Modifiers,
        Key = s.Key,
        MouseChordEnabled = false,
    };

    private void RegisterInitialSpotlightHotKey()
    {
        if (!_spotlightTriggerSettings.KeyboardShortcutEnabled)
        {
            _settingsWindow?.SetSpotlightTriggerSettings(
                _spotlightTriggerSettings,
                Strings.Settings_Status_SpotlightDisabled,
                isError: false);
            return;
        }

        try
        {
            _spotlightHotKey = CreateStartedSpotlightHotKey(_spotlightTriggerSettings);
            _settingsWindow?.SetSpotlightTriggerSettings(
                _spotlightTriggerSettings,
                string.Format(Strings.Settings_Status_Registered, _spotlightTriggerSettings.ToDisplayText()),
                isError: false);
        }
        catch (Exception exception) when (exception is GlobalHotKeyRegistrationException or TimeoutException)
        {
            _settingsWindow?.SetSpotlightTriggerSettings(
                _spotlightTriggerSettings,
                exception.Message,
                isError: true);
        }
    }

    private WindowsGlobalHotKey CreateStartedSpotlightHotKey(SpotlightTriggerSettings settings)
    {
        var registration = new WindowsGlobalHotKey(ToOceanEyesShape(settings));
        registration.Triggered += (x, y) =>
            Dispatcher.UIThread.Post(() => OnSpotlightTriggered());
        try
        {
            registration.Start();
            return registration;
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Spotlight hotkey fired. Toggle visibility — same semantics as the
    /// QuickTools fix: pressing the hotkey again dismisses the panel.
    /// </summary>
    private void OnSpotlightTriggered()
    {
        if (_spotlightWindow?.IsVisible == true)
        {
            _spotlightWindow.Hide();
            return;
        }
        _spotlightWindow?.Show();
    }

    /// <summary>
    /// Applies Spotlight trigger settings transactionally (same flow as
    /// OnOceanEyesTriggerSettingsSaved). A conflict or write error leaves the
    /// previous hotkey live.
    /// </summary>
    private void OnSpotlightTriggerSettingsSaved(SpotlightTriggerSettings requested)
    {
        if (_paths is null) return;
        requested = requested.Normalize();

        WindowsGlobalHotKey? candidate = null;
        bool shortcutUnchanged =
            requested.KeyboardShortcutEnabled &&
            _spotlightHotKey is not null &&
            requested.Modifiers == _spotlightHotKey.Settings.Modifiers &&
            requested.Key.Equals(_spotlightHotKey.Settings.Key, StringComparison.Ordinal);

        try
        {
            requested.Validate();
            if (requested.KeyboardShortcutEnabled && !shortcutUnchanged)
            {
                candidate = CreateStartedSpotlightHotKey(requested);
            }

            SpotlightTriggerStore.Save(requested, _paths.SpotlightTriggerFile);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            GlobalHotKeyRegistrationException or
            ProviderConfigurationException)
        {
            candidate?.Dispose();
            _settingsWindow?.SetSpotlightTriggerSettings(
                _spotlightTriggerSettings,
                exception.Message,
                isError: true);
            return;
        }

        WindowsGlobalHotKey? previous = _spotlightHotKey;
        _spotlightHotKey = requested.KeyboardShortcutEnabled ? candidate : null;
        previous?.Dispose();

        _spotlightTriggerSettings = requested;
        // R54: apply the new window size to the live window immediately so the
        // user sees it take effect on the next open (Spotlight is CanResize=False,
        // so this is the only path that ever changes its geometry).
        if (_spotlightWindow is not null)
        {
            _spotlightWindow.Width = requested.WindowWidth;
            _spotlightWindow.Height = requested.WindowHeight;
        }
        _settingsWindow?.SetSpotlightTriggerSettings(
            requested,
            requested.KeyboardShortcutEnabled
                ? string.Format(Strings.Settings_Status_Registered, requested.ToDisplayText())
                : Strings.Settings_Status_SpotlightDisabled,
            isError: false);
    }

    private void OnSpotlightLauncherEditRequested(string entryId)
    {
        _spotlightWindow?.Hide();
        RefreshAndShowSettings();
        _settingsWindow?.ShowAndScrollToLauncher();
        _settingsWindow?.RequestLauncherEdit(entryId);
    }

    private void OnSpotlightSettingsRequested()
    {
        _spotlightWindow?.Hide();
        RefreshAndShowSettings();
        _settingsWindow?.ShowAndScrollToLauncher();
    }

    // ── R54: Clipboard history trigger + service wiring ──

    private void RegisterInitialClipboardHistoryHotKey()
    {
        if (!_clipboardHistoryTriggerSettings.KeyboardShortcutEnabled)
        {
            _settingsWindow?.SetClipboardHistoryTriggerSettings(
                _clipboardHistoryTriggerSettings,
                Strings.Settings_Status_ClipboardDisabled,
                isError: false);
            return;
        }

        try
        {
            _clipboardHistoryHotKey = CreateStartedClipboardHistoryHotKey(_clipboardHistoryTriggerSettings);
            _settingsWindow?.SetClipboardHistoryTriggerSettings(
                _clipboardHistoryTriggerSettings,
                string.Format(Strings.Settings_Status_Registered, _clipboardHistoryTriggerSettings.ToDisplayText()),
                isError: false);
        }
        catch (Exception exception) when (exception is GlobalHotKeyRegistrationException or TimeoutException)
        {
            _settingsWindow?.SetClipboardHistoryTriggerSettings(
                _clipboardHistoryTriggerSettings,
                exception.Message,
                isError: true);
        }
    }

    private WindowsGlobalHotKey CreateStartedClipboardHistoryHotKey(ClipboardHistoryTriggerSettings settings)
    {
        var registration = new WindowsGlobalHotKey(ToOceanEyesShape(settings));
        registration.Triggered += (x, y) =>
            Dispatcher.UIThread.Post(() => OnClipboardHistoryTriggered());
        try
        {
            registration.Start();
            return registration;
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Clipboard-history hotkey fired. Toggle visibility — same semantics as
    /// Spotlight: pressing again dismisses. Before showing, push a fresh
    /// snapshot from the service into the window.
    /// </summary>
    private void OnClipboardHistoryTriggered()
    {
        if (_clipboardHistoryWindow?.IsVisible == true)
        {
            _clipboardHistoryWindow.Hide();
            return;
        }

        RefreshClipboardHistoryWindow();
        _clipboardHistoryWindow?.Show();
    }

    private void RefreshClipboardHistoryWindow()
    {
        if (_clipboardHistoryService is null || _clipboardHistoryWindow is null || _paths is null)
        {
            return;
        }

        bool maskSensitive = _clipboardHistorySettings.MaskSensitiveEnabled;
        IReadOnlyList<ClipboardEntry> snapshot = _clipboardHistoryService.Snapshot;
        // R103: also push the archive snapshot so the window can surface
        // archived entries in search results. Lazily loaded + cached on the
        // service; cheap to call repeatedly.
        IReadOnlyList<ClipboardEntry> archive = _clipboardHistoryService.ArchiveSnapshot;
        // Direct field access after the null guard (same shape the original
        // SetEntries call used; the analyzer accepts it for the same reason).
        _clipboardHistoryWindow.SetImagesDirectory(_paths.ClipboardImagesDirectory);
        _clipboardHistoryWindow.SetEntries(snapshot, archive, maskSensitive);
        PushClipboardHistoryTags();
    }

    /// <summary>
    /// R54 v1.1: pushes the current tag data (custom tags + per-entry favorite
    /// flag + per-entry custom-tag assignments) into the window. Called after
    /// every entry refresh and after every tag mutation, so the nav bar and the
    /// per-row ❤ markers / "移动到…" submenu stay in sync with the store.
    /// </summary>
    private void PushClipboardHistoryTags()
    {
        if (_clipboardHistoryService is null || _clipboardHistoryWindow is null)
        {
            return;
        }

        ClipboardTagData tags = _clipboardHistoryService.Tags;
        IReadOnlyList<ClipboardEntry> snapshot = _clipboardHistoryService.Snapshot;

        // Entry id → favorite flag (true when assigned the ❤收藏 tag).
        var favoriteById = new Dictionary<Guid, bool>();
        // Entry id → custom-tag names assigned (excludes the favorite tag, which
        // has its own dedicated marker/tab).
        var customTagsById = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (ClipboardEntry entry in snapshot)
        {
            if (!tags.Assignments.TryGetValue(entry.Id, out IReadOnlySet<string>? assigned) ||
                assigned.Count == 0)
            {
                continue;
            }

            favoriteById[entry.Id] = assigned.Contains(ClipboardTagData.FavoriteTagName);
            var customs = assigned
                .Where(t => t != ClipboardTagData.FavoriteTagName)
                .ToList();
            if (customs.Count > 0)
            {
                customTagsById[entry.Id] = customs;
            }
        }

        _clipboardHistoryWindow.SetTags(tags.CustomTags, favoriteById, customTagsById, tags.TagIcons);

        // R54 v1.2 v6: also push the user-imported icon library so the picker's
        // "我的图标" group + the import/remove affordances stay current.
        _clipboardHistoryWindow.SetUserIcons(_clipboardHistoryService.IconLibrary.Icons);
    }

    /// <summary>
    /// Applies clipboard-history trigger settings transactionally (same flow as
    /// <see cref="OnSpotlightTriggerSettingsSaved"/>).
    /// </summary>
    private void OnClipboardHistoryTriggerSettingsSaved(ClipboardHistoryTriggerSettings requested)
    {
        if (_paths is null) return;
        requested = requested.Normalize();

        WindowsGlobalHotKey? candidate = null;
        bool shortcutUnchanged =
            requested.KeyboardShortcutEnabled &&
            _clipboardHistoryHotKey is not null &&
            requested.Modifiers == _clipboardHistoryHotKey.Settings.Modifiers &&
            requested.Key.Equals(_clipboardHistoryHotKey.Settings.Key, StringComparison.Ordinal);

        try
        {
            requested.Validate();
            if (requested.KeyboardShortcutEnabled && !shortcutUnchanged)
            {
                candidate = CreateStartedClipboardHistoryHotKey(requested);
            }

            ClipboardHistoryTriggerStore.Save(requested, _paths.ClipboardHistoryTriggerFile);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            GlobalHotKeyRegistrationException or
            ProviderConfigurationException)
        {
            candidate?.Dispose();
            _settingsWindow?.SetClipboardHistoryTriggerSettings(
                _clipboardHistoryTriggerSettings,
                exception.Message,
                isError: true);
            return;
        }

        WindowsGlobalHotKey? previous = _clipboardHistoryHotKey;
        _clipboardHistoryHotKey = requested.KeyboardShortcutEnabled ? candidate : null;
        previous?.Dispose();

        _clipboardHistoryTriggerSettings = requested;
        _settingsWindow?.SetClipboardHistoryTriggerSettings(
            requested,
            requested.KeyboardShortcutEnabled
                ? string.Format(Strings.Settings_Status_Registered, requested.ToDisplayText())
                : Strings.Settings_Status_ClipboardDisabled,
            isError: false);
    }

    /// <summary>
    /// Applies + persists the clipboard-history feature toggles and pushes them
    /// into the live service (which re-applies eviction under the new cap).
    /// </summary>
    private void OnClipboardHistorySettingsSaved(ClipboardHistorySettings requested)
    {
        if (_paths is null) return;
        requested = requested.Normalize();

        try
        {
            requested.Validate();
            ClipboardHistorySettingsStore.Save(requested, _paths.ClipboardHistorySettingsFile);
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or
            ProviderConfigurationException)
        {
            _settingsWindow?.SetClipboardHistorySettings(_clipboardHistorySettings);
            _settingsWindow?.SetClipboardHistorySettingsStatus(exception.Message, isError: true);
            return;
        }

        _clipboardHistorySettings = requested;
        _clipboardHistoryService?.UpdateSettings(requested);
        // R54: apply the new default window size. ClipboardHistoryWindow is
        // CanResize=True, so this sets the default for the next open; an
        // in-session manual resize of the current instance is overwritten
        // here (matching "saved size = next-launch default" semantics).
        if (_clipboardHistoryWindow is not null)
        {
            _clipboardHistoryWindow.Width = requested.WindowWidth;
            _clipboardHistoryWindow.Height = requested.WindowHeight;
        }
        _settingsWindow?.SetClipboardHistorySettings(requested);
        _settingsWindow?.SetClipboardHistorySettingsStatus(Strings.Common_Status_Saved, isError: false);
    }

    private void OnClipboardHistoryClearRequested()
    {
        _clipboardHistoryService?.ClearNonPinned();
        RefreshClipboardHistoryWindow();
    }

    private void OnClipboardHistoryPasteRequested(Guid id)
    {
        if (_clipboardHistoryService is null) return;
        // R103: FindEntryById searches the live snapshot first, then the archive
        // cache — so archived entries can be pasted back without the caller
        // knowing whether the id is live or archived.
        ClipboardEntry? entry = _clipboardHistoryService.FindEntryById(id);
        if (entry is null) return;
        // Double-click paste always auto-pastes into the target (the universal
        // Win+V/Maccy/CopyQ contract: double-click = "paste here now"). The
        // PasteAsync adds a short focus-recovery delay after the window Hide().
        _clipboardHistoryService.PasteAsync(entry, autoPaste: true);
    }

    /// <summary>R54 v1.1: "复制（不关闭）" — writes the clipboard only (no Ctrl+V
    /// auto-paste), window stays open so the user can pick several entries. The
    /// user pastes manually with Ctrl+V into whichever target they choose.</summary>
    private void OnClipboardHistoryCopyRequested(Guid id)
    {
        if (_clipboardHistoryService is null) return;
        // R103: FindEntryById covers both live and archived entries.
        ClipboardEntry? entry = _clipboardHistoryService.FindEntryById(id);
        if (entry is null) return;
        _clipboardHistoryService.PasteAsync(entry, autoPaste: false);
    }

    private void OnClipboardHistoryPinToggled(Guid id)
    {
        _clipboardHistoryService?.TogglePin(id);
        RefreshClipboardHistoryWindow();
    }

    private void OnClipboardHistoryDeleteRequested(Guid id)
    {
        _clipboardHistoryService?.Delete(id);
        RefreshClipboardHistoryWindow();
    }

    // R54 v2: per-entry annotation tags. Add/Remove delegate to the service
    // (which rebuilds the entry via `with` + persists), then the snapshot is
    // re-pushed so the badge appears/disappears immediately.
    private void OnClipboardHistoryEntryTagAdded(Guid id, string tag)
    {
        _clipboardHistoryService?.AddEntryTag(id, tag);
        RefreshClipboardHistoryWindow();
    }

    private void OnClipboardHistoryEntryTagRemoved(Guid id, string tag)
    {
        _clipboardHistoryService?.RemoveEntryTag(id, tag);
        RefreshClipboardHistoryWindow();
    }

    // R54 v2: clear every entry older than the reference, keeping protected
    // entries (pinned/favorited/custom-tag/entry-tag). The service does the
    // protected-check; we just refresh the snapshot afterward.
    private void OnClipboardHistoryClearOlderRequested(Guid id)
    {
        _clipboardHistoryService?.ClearOlderEntries(id);
        RefreshClipboardHistoryWindow();
    }

    // R54 v2: dry-run count for the confirmation dialog (delete N / keep M).
    private (int wouldDelete, int wouldKeep) OnClipboardHistoryPreviewClearOlder(Guid id)
    {
        return _clipboardHistoryService?.PreviewClearOlder(id) ?? (0, 0);
    }

    private void OnClipboardHistorySettingsRequested()
    {
        _clipboardHistoryWindow?.Hide();
        RefreshAndShowSettings();
    }

    // ── R54 v1.1: tag-management handlers ──
    //
    // Each forwards to the matching ClipboardHistoryService method (which
    // persists atomically), then refreshes the window so the nav bar, row
    // markers, and "移动到…" submenu reflect the new state. Tag mutations only
    // rewrite clipboard-history-tags.json (never the large history file).

    private void OnClipboardHistoryCreateCustomTag(string name)
    {
        _clipboardHistoryService?.AddCustomTag(name);
        PushClipboardHistoryTags();
    }

    private void OnClipboardHistoryRenameCustomTag(string oldName, string newName)
    {
        _clipboardHistoryService?.RenameCustomTag(oldName, newName);
        PushClipboardHistoryTags();
    }

    private void OnClipboardHistoryDeleteCustomTag(string name)
    {
        _clipboardHistoryService?.DeleteCustomTag(name);
        PushClipboardHistoryTags();
    }

    private void OnClipboardHistoryAssignTag(Guid entryId, string tagName)
    {
        _clipboardHistoryService?.AssignToTag(entryId, tagName);
        PushClipboardHistoryTags();
    }

    private void OnClipboardHistoryUnassignTag(Guid entryId, string tagName)
    {
        _clipboardHistoryService?.UnassignFromTag(entryId, tagName);
        PushClipboardHistoryTags();
    }

    private void OnClipboardHistoryFavoriteToggled(Guid entryId)
    {
        _clipboardHistoryService?.ToggleFavorite(entryId);
        PushClipboardHistoryTags();
    }

    // R54 v2: set (or clear, when group is null) the user's manual correction
    // of the auto-classified group. The service flips IsSensitive in lockstep
    // when the target/clear is Sensitive (so masking + DPAPI-at-rest follow the
    // user's correction). A snapshot re-push updates the badge and the tab the
    // entry belongs to. Note: tags don't change here, so no PushClipboardHistoryTags.
    private void OnClipboardHistoryGroupOverride(Guid entryId, ClipboardGroup? group)
    {
        _clipboardHistoryService?.SetGroupOverride(entryId, group);
        RefreshClipboardHistoryWindow();
    }

    // ── R54 v1.2: icon + reorder handlers ──

    private void OnClipboardHistorySetTagIcon(string tagName, string emoji)
    {
        _clipboardHistoryService?.SetTagIcon(tagName, emoji);
        PushClipboardHistoryTags();
    }

    private void OnClipboardHistoryReorderTag(string tagName, int toIndex)
    {
        _clipboardHistoryService?.MoveTag(tagName, toIndex);
        PushClipboardHistoryTags();
    }

    // ── R54 v1.2 v6: user-imported icon library ──

    private void OnClipboardHistoryImportIcons(IReadOnlyList<(string FileName, string SvgContent)> files)
    {
        if (_clipboardHistoryService is null) return;
        var extracted = new List<UserIcon>();
        int skipped = 0;
        foreach ((string fileName, string svg) in files)
        {
            string? data = UserIconLibraryStore.ExtractPathData(svg);
            if (string.IsNullOrEmpty(data)) { skipped++; continue; }
            string name = UserIconLibraryStore.NameFromFile(fileName);
            extracted.Add(new UserIcon(name, data));
        }
        if (extracted.Count > 0)
        {
            _clipboardHistoryService.AddUserIcons(extracted);
        }
        PushClipboardHistoryTags();
    }

    private void OnClipboardHistoryRemoveUserIcon(string iconName)
    {
        if (_clipboardHistoryService is null) return;
        _clipboardHistoryService.RemoveUserIcon(iconName);
        PushClipboardHistoryTags();
    }

    /// <summary>
    /// Applies trigger settings transactionally: a changed shortcut is first
    /// registered alongside the old one, then persisted, and only then swaps
    /// in. A conflict or write error therefore leaves the previous hotkey live.
    /// </summary>
    private void OnOceanEyesTriggerSettingsSaved(OceanEyesTriggerSettings requested)
    {
        if (_paths is null) return;
        requested = requested.Normalize();

        WindowsGlobalHotKey? candidate = null;
        bool shortcutUnchanged =
            requested.KeyboardShortcutEnabled &&
            _oceanEyesHotKey is not null &&
            requested.Modifiers == _oceanEyesHotKey.Settings.Modifiers &&
            requested.Key.Equals(_oceanEyesHotKey.Settings.Key, StringComparison.Ordinal);

        try
        {
            requested.Validate();
            if (requested.KeyboardShortcutEnabled && !shortcutUnchanged)
            {
                candidate = CreateStartedHotKey(requested);
            }

            OceanEyesTriggerStore.Save(requested, _paths.OceanEyesTriggerFile);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            GlobalHotKeyRegistrationException or
            TimeoutException or
            ProviderConfigurationException)
        {
            candidate?.Dispose();
            _settingsWindow?.SetOceanEyesTriggerSettings(
                _oceanEyesTrigger,
                exception.Message,
                isError: true);
            return;
        }

        if (!shortcutUnchanged)
        {
            WindowsGlobalHotKey? previous = _oceanEyesHotKey;
            _oceanEyesHotKey = requested.KeyboardShortcutEnabled ? candidate : null;
            previous?.Dispose();
        }

        _oceanEyesTrigger = requested;
        _runtime?.SetMouseChordEnabled(requested.MouseChordEnabled);
        string shortcutState = requested.KeyboardShortcutEnabled
            ? string.Format(Strings.Settings_Status_Saved, requested.ToDisplayText())
            : Strings.Settings_Status_HotkeyDisabled;
        string chordState = requested.MouseChordEnabled ? Strings.Settings_Status_MouseChordOn : Strings.Settings_Status_MouseChordOff;
        _settingsWindow?.SetOceanEyesTriggerSettings(
            requested,
            $"{shortcutState} · {chordState}",
            isError: false);
    }

    /// <summary>
    /// R40: persists the Ocean Eyes screenshot/save settings (path + auto-save
    /// + clipboard + UIA assist toggles) and pushes them to the runtime. The
    /// runtime swap is a reference write, so the next Enter (save) uses the
    /// new path / toggles without any restart.
    /// </summary>
    private void OnOceanEyesCaptureSettingsSaved(OceanEyesCaptureSettings requested)
    {
        if (_paths is null) return;
        requested = requested.Normalize();

        try
        {
            requested.Validate();
            OceanEyesCaptureStore.Save(requested, _paths.OceanEyesCaptureFile);
        }
        catch (Exception exception) when (
            exception is ArgumentException or ProviderConfigurationException)
        {
            _settingsWindow?.SetOceanEyesCaptureSettings(
                _oceanEyesCapture,
                exception.Message,
                isError: true);
            return;
        }

        _oceanEyesCapture = requested;
        _runtime?.SetOceanEyesCaptureSettings(requested);
        // Compose a localized status line. The "Saved: {path}" prefix uses the
        // shared Settings_Status_Saved format string; the three toggle suffixes
        // reuse the same labels shown on the card itself (Auto-save / Clipboard
        // / UIA Snap) so the status echoes what the user just toggled. The
        // "off" branches stay literal English for now (low-traffic diagnostic).
        _settingsWindow?.SetOceanEyesCaptureSettings(
            requested,
            string.Format(Strings.Settings_Status_Saved, requested.SavePath) +
            (requested.AutoSaveEnabled ? " · " + Strings.Settings_AutoSave : " · No file") +
            (requested.CopyToClipboardEnabled ? " · " + Strings.Settings_ClipboardToggle : " · No clipboard") +
            (requested.UiaAssistEnabled ? " · " + Strings.Settings_UiaSnap : " · Free selection"),
            isError: false);
    }

    private void OnUserProfileSettingsSaved(UserProfileSettings requested)
    {
        if (_paths is null) return;
        requested = requested.Normalize();

        try
        {
            requested.Validate();
            UserProfileStore.Save(requested, _paths.UserProfileFile);
        }
        catch (Exception exception) when (
            exception is ArgumentException or ProviderConfigurationException)
        {
            _settingsWindow?.SetUserProfileSettings(
                _userProfileSettings,
                exception.Message,
                isError: true);
            return;
        }

        _userProfileSettings = requested;
        _settingsWindow?.SetUserProfileSettings(
            requested,
            string.Format(Strings.Settings_Profile_StatusSaved, requested.DisplayName),
            isError: false);
    }

    /// <summary>
    /// R37: persists the toolbar built-in shortcut bindings and pushes them to
    /// the runtime. Mirrors OnOceanEyesTriggerSettingsSaved minus
    /// the hotkey-registration dance — these are software-level shortcut keys
    /// dispatched inside BYH (no global hotkey to register/unregister). The
    /// runtime swap is a reference write, so the next keypress uses the new
    /// bindings without any restart.
    /// </summary>
    private void OnToolbarShortcutsSaved(ToolbarShortcutSettings requested)
    {
        if (_paths is null) return;
        requested = requested.Normalize();

        try
        {
            requested.Validate();
            ToolbarShortcutsStore.Save(requested, _paths.ToolbarShortcutsFile);
        }
        catch (Exception exception) when (
            exception is ArgumentException or ProviderConfigurationException)
        {
            _settingsWindow?.SetToolbarShortcuts(
                _toolbarShortcuts,
                exception.Message,
                isError: true);
            return;
        }

        _toolbarShortcuts = requested;
        _runtime?.SetToolbarShortcuts(requested);
        _settingsWindow?.SetToolbarShortcuts(
            requested,
            string.Format(Strings.Settings_Status_ToolbarShortcuts, DisplayKey(requested.PromptKey), DisplayKey(requested.CopyKey)),
            isError: false);
    }

    /// <summary>Empty key = disabled, shown as the localized "Unbound" word in status lines.</summary>
    private static string DisplayKey(string? key) => string.IsNullOrEmpty(key) ? Strings.Settings_Unbound : key;

    /// <summary>
    /// R40 Ocean Eyes: region confirmed in the overlay → capture PNG → show
    /// the shared toolbar at the region's top-right corner (in "未识别"
    /// state, all action buttons disabled) → wait for the user to press
    /// F/J/Z/R/C (lazy OCR, R41) → OCR runs once + caches → action fires.
    /// Enter saves the cached PNG (no OCR), Esc cancels, right-click redraws.
    /// The PNG is captured BEFORE the toolbar is shown so the toolbar isn't
    /// in the shot.
    /// </summary>
    /// <remarks>
    /// CRITICAL CAPTURE RACE: <see cref="ScreenRegionCapture.CaptureAsPng"/>
    /// uses <c>BitBlt</c> on the live compositor surface. The overlay window
    /// (full-screen <c>#80000000</c> dim + bright selection rect + 8 handles +
    /// size badge) MUST be fully removed from the compositor before we capture,
    /// otherwise the PNG — and the OCR'd text — is gibberish. The overlay
    /// already called <c>Hide()</c> in <c>Confirm()</c>; we wait for the
    /// compositor to settle, THEN capture the PNG, THEN show the toolbar.
    /// Showing the toolbar before the capture would self-capture it.
    /// </remarks>
    private async void OnRegionSelected(int x, int y, int w, int h)
    {
        if (_runtime is null)
        {
            return;
        }

        _ = RunOceanEyesCaptureAsync(x, y, w, h);
    }

    /// <summary>
    /// R42: captures the selected region as a clean PNG (no overlay chrome),
    /// then restores the overlay and shows the toolbar. The overlay stays
    /// visible after Confirm() (locked state), so we must temporarily Hide
    /// it before BitBlt, then ShowConfirmed to restore.
    /// </summary>
    private async Task RunOceanEyesCaptureAsync(int x, int y, int w, int h)
    {
        if (_runtime is null || _regionOverlay is null)
        {
            return;
        }

        // R42: overlay is visible + locked after Confirm(). Hide it so the
        // BitBlt capture doesn't include dim mask, dashed border, or handles.
        _regionOverlay.Hide();
        await WaitForCompositorSettleAsync().ConfigureAwait(true);

        var captured = ScreenRegionCapture.CaptureAsPngAndBgra(x, y, w, h);
        if (captured is null)
        {
            // Capture failed — clean up the overlay entirely.
            _regionOverlay.Cancel();
            return;
        }
        byte[] png = captured.Value.Png;
        byte[] bgra = captured.Value.Bgra;

        // R42: restore the overlay in its confirmed/locked state so the user
        // still sees the selected region while the toolbar appears.
        _regionOverlay.ShowConfirmed();

        int anchorX = x + w;   // right edge of the drawn region
        int anchorY = y;       // top edge

        // R41: Show the toolbar in "未识别" state with buttons disabled. OCR
        // does NOT run here — it's deferred to the first F/J/Z/R/C press via
        // SelectionRuntime.EnsureOceanEyesOcrAsync. The rect is passed so the
        // runtime knows where to OCR when the user triggers it.
        // R48: also passes the raw BGRA buffer so annotation burn-in skips
        // the lossy Avalonia.Bitmap decode (which throws on some PNGs in 12).
        _runtime.ShowToolbarForOceanEyes(anchorX, anchorY, png, bgra, x, y, w, h);
    }

    /// <summary>
    /// Waits long enough for the compositor to reflect the most recent
    /// Show/Hide window operations. The overlay's <c>#80000000</c> dim +
    /// selection rect + handles MUST be fully gone from the compositor surface
    /// before <c>BitBlt</c> reads the screen, otherwise the OCR model receives
    /// a dimmed screenshot and produces gibberish ("OCR 多余文字" bug). Three
    /// render frames at 60Hz ≈ 50ms guarantees the Hide() has propagated on
    /// every driver we've seen; a 150ms fixed delay covers slower drivers,
    /// background-tab throttling, and the BitBlt/GetDIBits round-trip latency.
    /// The wait is split into UI-thread pump intervals so the app keeps
    /// processing paint/layout messages (Avalonia Hide/Show completion happens
    /// on this queue).
    /// </summary>
    private static async Task WaitForCompositorSettleAsync()
    {
        // Yield three times to let three render frames pass (covers drivers
        // that need an extra frame to drop a transparency surface), then a
        // fixed safety margin. Dispatcher.Yield(Background) pumps the UI queue.
        await Dispatcher.UIThread.InvokeAsync(
            () => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(
            () => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(
            () => { }, DispatcherPriority.Background);
        await Task.Delay(150).ConfigureAwait(true);
    }

    /// <summary>Pushes the current provider list + key statuses + prompt templates into the settings UI.</summary>
    private async Task RefreshSettingsAsync()
    {
        if (_settingsWindow is null || _runtime is null) return;

        var providers = _runtime.GetProviders();
        string? currentId = _runtime.GetCurrentProviderId();

        _settingsWindow.SetProviders(providers, currentId,
            reference => _runtime.HasApiKeyAsync(reference));

        // R26: push the on-disk model cache so the Model dropdowns (translation
        // + vision) can populate from the last successful fetch without a
        // network call. Must happen AFTER SetProviders (so the combo has a
        // provider to resolve against) and BEFORE SetVisionSettings (so the
        // vision status line reflects the cache).
        ModelsCache modelsCache = _runtime.LoadModelsCache();
        var cacheDict = new Dictionary<string, (DateTime FetchedAtUtc, IReadOnlyList<string> Models)>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ModelsCacheEntry> kv in modelsCache.ByProvider)
        {
            cacheDict[kv.Key] = (kv.Value.FetchedAtUtc, kv.Value.Models);
        }
        _settingsWindow.SetCachedModels(cacheDict);

        _settingsWindow.SetPromptTemplates(_runtime.GetPromptTemplates());
        _settingsWindow.SetVisionSettings(_runtime.GetVisionSettings());
        _settingsWindow.SetOceanEyesTriggerSettings(_oceanEyesTrigger);
        _settingsWindow.SetOceanEyesCaptureSettings(_oceanEyesCapture);
        _settingsWindow.SetToolbarShortcuts(_toolbarShortcuts);
        var templates = _runtime.GetPromptTemplates().AsList();
        _toolbarWindow?.SetActions(templates);

        // R23 launcher: push the entries to Spotlight + settings, then
        // asynchronously load icons (extract-from-exe for LocalApp, fetch
        // favicon for WebUrl). Failures are best-effort — the row stays
        // icon-less, not a crash.
        IReadOnlyList<SelectionAssistant.Core.Launcher.LauncherEntry> launcherEntries =
            _runtime.GetLauncherEntries().AsList();
        _settingsWindow.SetLauncherEntries(launcherEntries);
        _spotlightWindow?.SetLauncherEntries(launcherEntries);
        _ = LoadLauncherIconsAsync(launcherEntries);
        _settingsWindow.SetDashboardUsage(BuildDashboardUsageSummary());

        await Task.CompletedTask;
    }

    private DashboardUsageSummary BuildDashboardUsageSummary()
    {
        if (_paths is null)
        {
            return new DashboardUsageSummary(0, 0, 0, 0);
        }

        int oceanEyes = 0;
        int actions = 0;
        int launcher = 0;

        try
        {
            if (File.Exists(_paths.LogFile))
            {
                foreach (string line in File.ReadLines(_paths.LogFile))
                {
                    if (line.Contains("[Usage] module=OceanEyes ", StringComparison.Ordinal))
                    {
                        oceanEyes++;
                    }
                    else if (line.Contains("[Usage] module=Actions ", StringComparison.Ordinal))
                    {
                        actions++;
                    }
                    else if (line.Contains("[Usage] module=Launcher ", StringComparison.Ordinal))
                    {
                        launcher++;
                    }
                    else if (line.Contains("[OceanEyes] Saved screenshot:", StringComparison.Ordinal))
                    {
                        // Preserve a useful count for installations created
                        // before explicit privacy-safe usage events existed.
                        oceanEyes++;
                    }
                }
            }
        }
        catch (IOException)
        {
            // The logger may append while Settings reads. A transient sharing
            // issue produces an honest empty/partial dashboard, never a crash.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostics are optional; configuration UI remains available.
        }

        int clipboard = _clipboardHistoryService?.Snapshot.Count ?? 0;
        return new DashboardUsageSummary(oceanEyes, actions, launcher, clipboard);
    }

    /// <summary>
    /// Returns the cache file path for a launcher exe icon, or null if the
    /// cache directory is unavailable. The key is the SHA256 hex of the target
    /// path (lowercased) so re-scans and restarts hit the cache instead of
    /// re-running SHGetFileInfo. Files are plain PNGs inside LauncherIconsDirectory.
    /// </summary>
    private string? GetLauncherIconCacheFile(string target)
    {
        if (_paths is null || string.IsNullOrEmpty(target))
        {
            return null;
        }
        // Lowercase before hashing so case-only path differences (C:\ vs c:\)
        // share one cache entry — the extractor ignores case anyway.
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.Unicode.GetBytes(target.ToLowerInvariant()));
        var name = new System.Text.StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            name.Append(b.ToString("x2"));
        }
        return System.IO.Path.Combine(_paths.LauncherIconsDirectory, name.ToString() + ".png");
    }

    /// <summary>
    /// Walks the entries and loads each icon off the UI thread. For LocalApp,
    /// extracts from the exe via <see cref="WindowsIconExtractor"/>; for WebUrl,
    /// fetches a favicon. Pushes results back via the windows' UpdateLauncherIcon
    /// (which marshals to the UI thread internally).
    /// </summary>
    private async Task LoadLauncherIconsAsync(
        IReadOnlyList<SelectionAssistant.Core.Launcher.LauncherEntry> entries)
    {
        foreach (var entry in entries)
        {
            Avalonia.Media.Imaging.Bitmap? bitmap = null;
            try
            {
                if (entry.Kind == SelectionAssistant.Core.Launcher.LauncherKind.LocalApp)
                {
                    if (!string.IsNullOrEmpty(entry.IconOverride) &&
                        System.IO.File.Exists(entry.IconOverride))
                    {
                        try
                        {
                            bitmap = new Avalonia.Media.Imaging.Bitmap(entry.IconOverride);
                        }
                        catch { /* fall through to exe extraction */ }
                    }

                    if (bitmap is null)
                    {
                        // R54 v2: disk cache for extracted exe icons. The cache
                        // key is the SHA256 of the target path so re-scans and
                        // app restarts load from PNG files instead of re-running
                        // SHGetFileInfo on every refresh (hundreds of ms × N).
                        string? cacheFile = GetLauncherIconCacheFile(entry.Target);
                        if (cacheFile is not null && System.IO.File.Exists(cacheFile))
                        {
                            try
                            {
                                bitmap = new Avalonia.Media.Imaging.Bitmap(cacheFile);
                            }
                            catch { /* corrupt cache file — re-extract below */ }
                        }

                        if (bitmap is null)
                        {
                            byte[]? png = await Task.Run(() =>
                                SelectionAssistant.Platform.Windows.Launcher.WindowsIconExtractor
                                    .ExtractSmallIconPng(entry.Target));
                            if (png is { Length: > 0 })
                            {
                                using var stream = new System.IO.MemoryStream(png);
                                bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                                // Persist to cache for next time (best-effort).
                                if (cacheFile is not null)
                                {
                                    try { System.IO.File.WriteAllBytes(cacheFile, png); }
                                    catch { /* cache write failure is non-fatal */ }
                                }
                            }
                        }
                    }
                }
                else
                {
                    bitmap = await LoadFaviconAsync(entry.Target);
                }
            }
            catch
            {
                // Best-effort: leave bitmap null on any failure.
            }

            _settingsWindow?.UpdateLauncherIcon(entry.Id, bitmap);
            _spotlightWindow?.UpdateLauncherIcon(entry.Id, bitmap);
        }
    }

    /// <summary>
    /// Fetches a 32×32 favicon for the URL via Google's S2 favicon service and
    /// decodes it to an Avalonia Bitmap. Returns null on any failure.
    /// </summary>
    private static async Task<Avalonia.Media.Imaging.Bitmap?> LoadFaviconAsync(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }
            string domain = uri.Host;
            if (string.IsNullOrEmpty(domain))
            {
                return null;
            }

            // Audit M3: in-memory cache hit short-circuits the network call.
            // The cache stores null too (negative cache) so a host that 404'd
            // doesn't get re-fetched on every settings refresh. _faviconCache
            // is a Dictionary, not concurrent — but LoadLauncherIconsAsync is
            // awaited serially inside its own async loop, and App is a
            // singleton, so there's at most one favicon fetch in flight at a
            // time. No lock needed.
            if (_faviconCache.TryGetValue(domain, out Avalonia.Media.Imaging.Bitmap? cached))
            {
                return cached;
            }

            string faviconUrl = $"https://www.google.com/s2/favicons?domain={domain}&sz=32";
            // Audit M3: shared static HttpClient reuses the socket pool instead
            // of new'ing per call (which leaked TIME_WAIT sockets and defeated
            // keep-alive).
            byte[] bytes = await _faviconHttpClient.GetByteArrayAsync(faviconUrl);
            if (bytes.Length == 0)
            {
                _faviconCache[domain] = null; // negative cache
                return null;
            }
            using var stream = new System.IO.MemoryStream(bytes);
            var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
            _faviconCache[domain] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>R24 track B: persists + applies vision OCR settings from the UI.</summary>
    private void OnVisionSettingsSaved(VisionCaptureSettings settings)
    {
        if (_runtime is null) return;
        _runtime.UpdateVisionSettings(settings);
    }

    /// <summary>Refreshes provider list on the settings window, then shows it.</summary>
    private async void RefreshAndShowSettings()
    {
        if (_settingsWindow is null)
        {
            return;
        }

        if (_runtime is not null)
        {
            await RefreshSettingsAsync();
        }

        _settingsWindow.ShowAndActivate();
    }

    private TrayIcon CreateTrayIcon()
    {
        var openSettings = new NativeMenuItem(Strings.Tray_OpenSettings);
        openSettings.Click += (_, _) => RefreshAndShowSettings();

        var openConfig = new NativeMenuItem(Strings.Tray_OpenConfig);
        openConfig.Click += (_, _) =>
        {
            if (_paths is not null)
            {
                OpenDirectory(_paths.BaseDirectory);
            }
        };

        // R49: screenshot gallery entry. Works cold-start — does not need an
        // Ocean Eyes session. ShowGallery reads SavePath from settings.
        var openGallery = new NativeMenuItem(Strings.Tray_OpenGallery);
        openGallery.Click += (_, _) => _runtime?.ShowGallery();

        var restart = new NativeMenuItem(Strings.Tray_Restart);
        restart.Click += (_, _) => RequestRestart();

        var exit = new NativeMenuItem(Strings.Tray_Exit);
        exit.Click += (_, _) => RequestExit();

        var menu = new NativeMenu();
        menu.Items.Add(openSettings);
        menu.Items.Add(openConfig);
        menu.Items.Add(openGallery);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(restart);
        menu.Items.Add(exit);

        // Load the tray icon as a full ICO container (not a raw PNG). On
        // Windows the tray uses HICON via CreateIconFromResource, which needs
        // the ICO container format to preserve the alpha channel. Passing a
        // raw PNG stream to WindowIcon loses transparency → opaque background.
        System.IO.Stream? iconStream = null;
        try
        {
            iconStream = Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://BYH/Assets/app-icon.ico"));
        }
        catch { /* asset missing — tray still works without an icon */ }

        var trayIcon = new TrayIcon
        {
            Icon = iconStream is not null ? new WindowIcon(iconStream) : null,
            ToolTipText = "BYH · By Your Hand",
            Menu = menu,
            IsVisible = true,
        };
        trayIcon.Clicked += (_, _) => RefreshAndShowSettings();
        return trayIcon;
    }

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Overrides the theme's font-size tokens when the active UI language is
    /// Chinese. CJK glyphs read smaller than Latin at the same pt size (denser
    /// strokes, smaller glyph aspect), so the small-text tokens are bumped up.
    /// The Latin baseline values live in <c>IvoryJade.axaml</c>'s
    /// <c>Styles.Resources</c>; writing them into
    /// <see cref="Application.Resources"/> here wins over the theme because
    /// Avalonia resolves application resources above theme resources. English
    /// and other non-Chinese languages get no overrides — the theme defaults
    /// apply and the UI is identical to before this hook existed.
    /// </summary>
    /// <remarks>
    /// <b>Non-linear scaling.</b> The original +1pt-across-the-board approach
    /// made already-comfortable text (buttons, inputs, headings) larger while
    /// tiny captions stayed tiny — the opposite of what CJK needs. The user
    /// feedback ("已经够大的更大了，小的没变") confirmed this. So now only the
    /// small text scales, and by more than 1pt; button-sized text and above
    /// stay at the Latin baseline (already large enough). The breakpoints
    /// below were tuned against the actual token usage: Micro/Caption/BodySmall
    /// are captions/hints/metadata, Body is primary body labels, BodyLarge is
    /// inputs/option buttons/row names, Subheading+ are titles.
    /// <para>
    /// Must run BEFORE any window is constructed so the first measure pass
    /// sees the Chinese sizes. Switching languages is restart-after-toggle
    /// (see <see cref="OnUiLanguageSaved"/>), so this method is called exactly
    /// once per process lifetime at startup.
    /// </para>
    /// </remarks>
    private static void ApplyLanguageFontTokens(AppLanguage language)
    {
        if (Application.Current is null)
        {
            return;
        }
        if (!language.IsChinese)
        {
            return;
        }
        var resources = Application.Current.Resources;
        // Non-linear: small text scales up significantly (captions and hints
        // are where Chinese reads worst — denser strokes at tiny sizes), body
        // labels bump modestly, and button-sized text upward stays at the
        // Latin baseline (the user confirmed buttons/inputs were already big
        // enough and shouldn't grow further).
        resources["ByhFontSizeMicro"] = 9.0;       // 7 → 9  (+2, badge micro-labels)
        resources["ByhFontSizeCaption"] = 11.0;    // 9 → 11 (+2, captions/kickers/hints — the "small annotations" the user flagged)
        resources["ByhFontSizeBodySmall"] = 12.0;  // 10 → 12 (+2, metadata / setting-card sub-hints)
        resources["ByhFontSizeBody"] = 12.0;       // 11 → 12 (+1, primary body labels — modest bump)
        resources["ByhFontSizeBodyLarge"] = 13.0;  // 13 → 13 (+0, option buttons / inputs / row names — already large enough)
        resources["ByhFontSizeSubheading"] = 15.0; // 15 → 15 (+0, card titles)
        resources["ByhFontSizeHeading"] = 18.0;    // 18 → 18 (+0, dialog/page headings)
        resources["ByhFontSizeDisplay"] = 25.0;    // 25 → 25 (+0, hero / page title)
    }

    /// <summary>
    /// Persists the user's language choice to <c>ui-language.json</c> then
    /// restarts BYH. Strings' static dictionary is snapshotted at process
    /// start, so a restart (not a live refresh) is what actually swaps the UI
    /// language across all XAML <c>{x:Static loc:Strings.Xxx}</c> bindings.
    /// Reuses the proven Mutex-handoff restart path used by the tray menu.
    /// </summary>
    private void OnUiLanguageSaved(AppLanguage language)
    {
        if (_paths is null)
        {
            return;
        }
        try
        {
            UiLanguageStore.Save(language, _paths.UiLanguageFile);
        }
        catch (ProviderConfigurationException)
        {
            // Persist failed — don't restart into a stale state. The user will
            // see the old language and can retry. (Saving rarely fails; the
            // store wraps I/O errors as ProviderConfigurationException.)
            return;
        }
        RequestRestart();
    }

    /// <summary>
    /// Restarts BYH: spawns a fresh copy of the current executable (detached,
    /// so it survives this process's exit), then performs the normal shutdown
    /// path. The new instance takes over after this one dies. Used by the tray
    /// "重启 BYH" entry so users can pick up config/icon changes without
    /// manually hunting for the process.
    /// </summary>
    private void RequestRestart()
    {
        string? exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            // Drop the single-instance Mutex BEFORE spawning the new copy.
            // The new process will be launched with --restart, which makes it
            // retry the Mutex briefly instead of bailing instantly — but
            // releasing first is the key step: otherwise the old process can
            // still hold the Mutex when the new one tries to acquire it, and
            // the new copy silently exits (R31 fix).
            Program.ReleaseForRestart();

            try
            {
                // Detached spawn: the new copy outlives this process. Let it
                // start normally (it hides to the tray on its own). Pass
                // --restart so the new copy knows to retry the Mutex.
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    ArgumentList = { "--restart" },
                };
                Process.Start(startInfo);
            }
            catch
            {
                // Spawning the restart copy failed — just exit; the user will
                // notice the tray icon is gone and can relaunch manually.
            }
        }
        RequestExit();
    }

    private void RequestExit()
    {
        _toolbarWindow?.PrepareForShutdown();
        _resultWindow?.PrepareForShutdown();
        _settingsWindow?.PrepareForShutdown();
        _promptWindow?.PrepareForShutdown();
        _spotlightWindow?.PrepareForShutdown();
        _clipboardHistoryWindow?.PrepareForShutdown();
        _regionOverlay?.PrepareForShutdown();
        _oceanEyesHotKey?.Dispose();
        _oceanEyesHotKey = null;
        _spotlightHotKey?.Dispose();
        _spotlightHotKey = null;
        _clipboardHistoryHotKey?.Dispose();
        _clipboardHistoryHotKey = null;
        _clipboardHistoryService?.Dispose();
        _clipboardHistoryService = null;
        // Audit H8: dispose the runtime HERE, not only in the desktop.Exit
        // handler. TryShutdown should fire Exit (which routes to
        // DisposeApplicationResources), but on forced shutdown / modal-blocked
        // shutdown / OS power-off the Exit event may not fire — the runtime
        // owns providers, hooks, and the clipboard listener, which would leak
        // (or worse, keep running) in those paths. Disposing here is safe
        // because DisposeApplicationResources is now idempotent (it re-checks
        // _runtime != null).
        _runtime?.Dispose();
        _runtime = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _desktop?.TryShutdown();
    }

    private void DisposeApplicationResources()
    {
        // Audit H8: idempotent residual cleanup. RequestExit is the primary
        // disposer now; this handler (wired to desktop.Exit) only catches
        // anything RequestExit missed (e.g. if RequestExit threw before
        // reaching _runtime?.Dispose()). The null check makes a double-dispose
        // from both paths a no-op instead of an ObjectDisposedException.
        _runtime?.Dispose();
        _runtime = null;
        _oceanEyesHotKey?.Dispose();
        _oceanEyesHotKey = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
