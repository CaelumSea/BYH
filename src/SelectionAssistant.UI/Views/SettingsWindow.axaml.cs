using Avalonia.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SelectionAssistant.Core.Appearance;
using SelectionAssistant.Core.Capture;
using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Core.I18n;
using SelectionAssistant.Core.Input;
using SelectionAssistant.Core.Launcher;
using SelectionAssistant.Core.PowerMonitoring;
using SelectionAssistant.Core.Speech;
using SelectionAssistant.Core.Startup;
using SelectionAssistant.Core.Translation;
using SelectionAssistant.Infrastructure.Configuration;
using SelectionAssistant.Infrastructure.PowerMonitoring;
using System.Collections.ObjectModel;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// One row in the provider ComboBox. Public + top-level so Avalonia compiled
/// bindings can resolve <see cref="DisplayLabel" /> at XAML compile time (a
/// private nested record forces reflection bindings, which break NativeAOT).
/// </summary>
public sealed record ProviderOption(string Id, string DisplayLabel);

/// <summary>
/// One-shot model discovery input. Translation passes the connection values
/// currently visible in the editor so a Custom draft can be tested before it
/// is persisted. A null override keeps the saved value (used by Vision and by
/// blank API-key fields on existing providers).
/// </summary>
public sealed record ProviderModelsFetchRequest(
    string ProviderId,
    string? BaseUrlOverride = null,
    string? ApiKeyOverride = null,
    int? TimeoutSecondsOverride = null);

/// <summary>
/// Privacy-safe aggregate counts derived from BYH's local redacted log.
/// No selected text, filenames, prompts, or API data are carried into the UI.
/// </summary>
public sealed record DashboardUsageSummary(
    int OceanEyes,
    int Actions,
    int Launcher,
    int Clipboard);

public partial class SettingsWindow : Window
{
    private enum SettingsPage
    {
        Dashboard,
        General,
        Provider,
        Functions,
        Vision,
        Tts,
        PowerMonitor,
        Launcher,
        ClipboardHistory,
    }

    private enum PhonePage
    {
        Overview,
        Translation,
        Vision,
        Clipboard,
        Launcher,
    }

    private bool _allowClose;
    // Active dashboard module IDENTIFIERS (not display labels). Each call to
    // SetDashboardModuleActive passes one of the DashboardModule.* constants;
    // DashboardModuleLabel maps an identifier to its localized display name so
    // the module-summary join and the dashboard top-feature row render in the
    // active UI language.
    private readonly HashSet<string> _activeDashboardModules = new(StringComparer.Ordinal);

    /// <summary>
    /// Stable identifiers for the dashboard's feature modules. Callers pass
    /// these to <see cref="SetDashboardModuleActive"/>; the summary row maps
    /// them to localized labels via <see cref="DashboardModuleLabel"/>.
    /// </summary>
    private static class DashboardModule
    {
        public const string OceanEyes = "oceaneyes";
        public const string Actions = "actions";
        public const string Launcher = "launcher";
        public const string Clipboard = "clipboard";
        public const string Vision = "vision";
        public const string Translation = "translation";
    }

    /// <summary>
    /// Maps a <see cref="DashboardModule"/> identifier to its localized
    /// display label. Returns the identifier itself if unknown (defensive —
    /// should never happen since all call sites use the constants above).
    /// </summary>
    private static string DashboardModuleLabel(string module) => module switch
    {
        DashboardModule.OceanEyes => Strings.Settings_Dashboard_Module_OceanEyes,
        DashboardModule.Actions => Strings.Settings_Dashboard_Module_Actions,
        DashboardModule.Launcher => Strings.Settings_Dashboard_Module_Launcher,
        DashboardModule.Clipboard => Strings.Settings_Dashboard_Module_Clipboard,
        DashboardModule.Vision => Strings.Settings_Dashboard_Module_Vision,
        DashboardModule.Translation => Strings.Settings_Dashboard_Module_Translation,
        _ => module,
    };

    private readonly AvaloniaList<ProviderOption> _providerOptions = [];
    private readonly ObservableCollection<PromptFunctionRow> _functionRows = [];
    private List<ProviderProfileEntry> _providers = [];
    private string? _currentProviderId;
    private Func<string, Task<bool>>? _hasKeyChecker;

    // The provider the user is currently editing in the form. Tracked across
    // refreshes so that adding/saving/keying a provider doesn't snap the combo
    // back to the default provider (which lost the user's in-progress edit).
    // Null until the user picks/edits one; falls back to the default on first load.
    private string? _editingProviderId;

    // Custom providers start as an in-memory draft. They are persisted only
    // after the user supplies valid connection fields and clicks Save Profile,
    // preventing orphan entries whose Base URL is just "https://".
    private string? _draftProviderId;

    // R26: reentry guard for the "Refresh Models" buttons. The codebase has no
    // CancellationTokenSource convention — a simple bool flag mirrors the only
    // existing reentry guard (the null-check at the top of
    // OnProviderSelectionChanged). Two flags because translation and vision
    // pages can fetch independently (different providers).
    private bool _isFetchingTranslationModels;
    private bool _isFetchingVisionModels;

    // Current prompt templates (pushed by runtime) + built-in defaults (for
    // the edit window's "恢复默认" hint).
    private PromptTemplateSet _promptTemplates = PromptTemplateDefaults.CreateDefault();
    private static readonly PromptTemplateSet BuiltInDefaults = PromptTemplateDefaults.CreateDefault();

    private readonly ObservableCollection<LauncherEntryRow> _launcherRows = [];

    // R24 track B: vision OCR provider options (same source list as translation
    // providers; the user picks which provider entry powers OCR screenshots).
    private readonly AvaloniaList<ProviderOption> _visionProviderOptions = [];

    public SettingsWindow()
    {
        InitializeComponent();
        ProviderComboBox.ItemsSource = _providerOptions;
        VisionProviderComboBox.ItemsSource = _visionProviderOptions;
        ShortcutKeyComboBox.ItemsSource = OceanEyesTriggerSettings.SupportedKeys;
        SpotlightShortcutKeyComboBox.ItemsSource = SpotlightTriggerSettings.SupportedKeys;
        // Default to "Space" for Spotlight (Ctrl+Alt+Space).
        SpotlightShortcutKeyComboBox.SelectedItem = SpotlightTriggerSettings.Default.Key;
        ClipboardHistoryShortcutKeyComboBox.ItemsSource = ClipboardHistoryTriggerSettings.SupportedKeys;
        // Default to "V" for clipboard history (Ctrl+Alt+V).
        ClipboardHistoryShortcutKeyComboBox.SelectedItem = ClipboardHistoryTriggerSettings.Default.Key;
        FunctionsList.ItemsSource = _functionRows;
        LauncherList.ItemsSource = _launcherRows;
        Closing += (_, eventArgs) =>
        {
            if (_allowClose)
            {
                return;
            }

            eventArgs.Cancel = true;
            Hide();
        };
        SizeChanged += (_, _) => ApplyResponsiveShellWidths();
        PropertyChanged += OnWindowPropertyChanged;

        ShowSettingsPage(SettingsPage.Dashboard);
        ShowPhonePage(PhonePage.Overview);
        ApplyResponsiveShellWidths();
    }

    // ── Settings information architecture ──

    private void ApplyResponsiveShellWidths()
    {
        bool expanded = WindowState == WindowState.Maximized;
        ShellGrid.ColumnDefinitions[0].Width = new GridLength(expanded ? 230 : 190);
        ShellGrid.ColumnDefinitions[1].Width = new GridLength(expanded ? 210 : 170);
        ShellGrid.ColumnDefinitions[3].Width = new GridLength(expanded ? 310 : 270);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.Property != WindowStateProperty)
        {
            return;
        }

        ApplyResponsiveShellWidths();
    }

    private void ShowSettingsPage(SettingsPage page)
    {
        DashboardSection.IsVisible = page == SettingsPage.Dashboard;
        GeneralSection.IsVisible = page == SettingsPage.General;
        ProviderSection.IsVisible = page == SettingsPage.Provider;
        FunctionsSection.IsVisible = page == SettingsPage.Functions;
        VisionSection.IsVisible = page == SettingsPage.Vision;
        TtsSection.IsVisible = page == SettingsPage.Tts;
        PowerMonitorSection.IsVisible = page == SettingsPage.PowerMonitor;
        LauncherSection.IsVisible = page == SettingsPage.Launcher;
        ClipboardHistorySection.IsVisible = page == SettingsPage.ClipboardHistory;

        SetNavigationState(DashboardNavButton, page == SettingsPage.Dashboard);
        SetNavigationState(GeneralNavButton, page == SettingsPage.General);
        SetNavigationState(ProviderNavButton, page == SettingsPage.Provider);
        SetNavigationState(FunctionsNavButton, page == SettingsPage.Functions);
        SetNavigationState(VisionNavButton, page == SettingsPage.Vision);
        SetNavigationState(TtsNavButton, page == SettingsPage.Tts);
        SetNavigationState(PowerMonitorNavButton, page == SettingsPage.PowerMonitor);
        SetNavigationState(LauncherNavButton, page == SettingsPage.Launcher);
        SetNavigationState(ClipboardHistoryNavButton, page == SettingsPage.ClipboardHistory);

        (PageTitleText.Text, PageSubtitleText.Text) = page switch
        {
            SettingsPage.Dashboard =>
                (Strings.Settings_PageTitle_Dashboard, Strings.Settings_PageSubtitle_Dashboard),
            SettingsPage.General =>
                (Strings.Settings_PageTitle_General, Strings.Settings_PageSubtitle_General),
            SettingsPage.Provider =>
                (Strings.Settings_PageTitle_Provider, Strings.Settings_PageSubtitle_Provider),
            SettingsPage.Functions =>
                (Strings.Settings_PageTitle_Functions, Strings.Settings_PageSubtitle_Functions),
            SettingsPage.Vision =>
                (Strings.Settings_PageTitle_Vision, Strings.Settings_PageSubtitle_Vision),
            SettingsPage.Tts =>
                (Strings.Settings_PageTitle_Tts, Strings.Settings_PageSubtitle_Tts),
            SettingsPage.PowerMonitor =>
                (Strings.Settings_PageTitle_PowerMonitor, Strings.Settings_PageSubtitle_PowerMonitor),
            SettingsPage.Launcher =>
                (Strings.Settings_PageTitle_Launcher, Strings.Settings_PageSubtitle_Launcher),
            SettingsPage.ClipboardHistory =>
                (Strings.Settings_PageTitle_Clipboard, Strings.Settings_PageSubtitle_Clipboard),
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };

        SettingsContentScroll.Offset = default;
    }

    private void OnShowDashboardClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsPage(SettingsPage.Dashboard);

    private void ShowPhonePage(PhonePage page)
    {
        PhoneOverviewView.IsVisible = page == PhonePage.Overview;
        PhoneTranslationView.IsVisible = page == PhonePage.Translation;
        PhoneVisionView.IsVisible = page == PhonePage.Vision;
        PhoneClipboardView.IsVisible = page == PhonePage.Clipboard;
        PhoneLauncherView.IsVisible = page == PhonePage.Launcher;

        SetNavigationState(PhoneGeneralButton, page == PhonePage.Overview);
        SetNavigationState(PhoneProviderButton, page == PhonePage.Translation);
        SetNavigationState(PhoneVisionButton, page == PhonePage.Vision);
        SetNavigationState(PhoneClipboardButton, page == PhonePage.Clipboard);
        SetNavigationState(PhoneLauncherButton, page == PhonePage.Launcher);
    }

    private static void SetNavigationState(Button button, bool isActive)
    {
        button.Classes.Remove("Active");
        if (isActive)
        {
            button.Classes.Add("Active");
        }
    }

    private void OnShowGeneralClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsPage(SettingsPage.General);

    private void OnShowProviderClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsPage(SettingsPage.Provider);

    private void OnShowFunctionsClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsPage(SettingsPage.Functions);

    private void OnShowVisionClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsPage(SettingsPage.Vision);

    private void OnShowTtsClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsPage(SettingsPage.Tts);

    private void OnShowPowerMonitorClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsPage(SettingsPage.PowerMonitor);

    private void OnShowLauncherClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsPage(SettingsPage.Launcher);

    private void OnShowClipboardHistoryClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsPage(SettingsPage.ClipboardHistory);

    private void OnShowPhoneOverviewClick(object? sender, RoutedEventArgs e) =>
        ShowPhonePage(PhonePage.Overview);

    private void OnShowPhoneTranslationClick(object? sender, RoutedEventArgs e) =>
        ShowPhonePage(PhonePage.Translation);

    private void OnShowPhoneVisionClick(object? sender, RoutedEventArgs e) =>
        ShowPhonePage(PhonePage.Vision);

    private void OnShowPhoneClipboardClick(object? sender, RoutedEventArgs e) =>
        ShowPhonePage(PhonePage.Clipboard);

    private void OnShowPhoneLauncherClick(object? sender, RoutedEventArgs e) =>
        ShowPhonePage(PhonePage.Launcher);

    // ── Events wired to the runtime in App.axaml.cs ──

    public event Action? OpenConfigDirectoryRequested;
    public event Action? OpenLogDirectoryRequested;
    public event Action? ExitRequested;

    /// <summary>Request to set the active provider (hot-swap). Arg = provider id.</summary>
    public event Action<string>? SetActiveProviderRequested;

    /// <summary>Request to add a provider from a preset. Arg = preset id.</summary>
    public event Action<string>? AddProviderFromPresetRequested;

    /// <summary>
    /// Request to save a provider config. Args = (full entry, optional new API
    /// key). The App layer adds when the id is new and updates otherwise, then
    /// stores the optional key in DPAPI as one user-visible save operation.
    /// </summary>
    public event Action<ProviderProfileEntry, string?>? SaveProviderRequested;

    /// <summary>Request to delete a provider. Arg = provider id.</summary>
    public event Action<string>? DeleteProviderRequested;

    /// <summary>
    /// R26/R35: request to fetch the upstream model list via
    /// <c>GET {BaseUrl}/models</c>. The request can carry unsaved connection
    /// overrides for a Custom draft. Returns the fetched model ids, UTC
    /// timestamp, and an error string (null on success). The window owns the
    /// UI state; the App layer owns the one-shot network call.
    /// </summary>
    public event Func<ProviderModelsFetchRequest, Task<(IReadOnlyList<string> Models, DateTime FetchedAtUtc, string? Error)>>? FetchModelsRequested;

    /// <summary>Request to save a prompt template. Args = (actionId, newPrompt, thinkingEnabled, shortcut, newName). <paramref name="newName" /> is non-null only when editing a custom action whose name changed.</summary>
    public event Action<string, string, bool, string?, LocalizedName?>? PromptTemplateSaved;

    /// <summary>Request to reset a prompt template to built-in default. Arg = actionId.</summary>
    public event Action<string>? PromptTemplateReset;

    /// <summary>Request to add a new custom function. Args = (name, prompt, thinkingEnabled, shortcut).</summary>
    public event Action<LocalizedName, string, bool, string?>? PromptTemplateAdded;

    /// <summary>Request to delete a custom function. Arg = actionId (must be custom-*).</summary>
    public event Action<string>? PromptTemplateDeleted;

    /// <summary>Request to add a new launcher entry. Args = (name, kind, target, args, workDir).</summary>
    public event Action<string, LauncherKind, string, string, string>? LauncherEntryAdded;
    /// <summary>Request to save an edited launcher entry. Args = (id, kind, target, args, workDir).</summary>
    public event Action<string, string, LauncherKind, string, string, string>? LauncherEntrySaved;

    /// <summary>Request to delete a launcher entry. Arg = entry id.</summary>
    public event Action<string>? LauncherEntryDeleted;

    /// <summary>Request to move a launcher entry. Args = (id, delta) where -1=up, +1=down.</summary>
    public event Action<string, int>? LauncherEntryMoved;

    /// <summary>
    /// Request to scan the system for installed apps (Start Menu shortcuts).
    /// No args — the handler runs the detector, dedupes against existing
    /// entries, and shows a selection dialog itself.
    /// </summary>
    public event Action? ScanInstalledAppsRequested;

    /// <summary>R24 track B: request to save the vision OCR settings.</summary>
    public event Action<VisionCaptureSettings>? VisionSettingsSaved;

    /// <summary>Request to save the 朗读 (TTS) settings. Args = (settings, newApiKey)
    /// where newApiKey is null when the user didn't change the key field.</summary>
    public event Action<TtsSettings, string?>? TtsSettingsSaved;

    /// <summary>Request to test-synthesize + play a sample via the runtime.
    /// Args = (settings, newApiKeyOrNull). The runtime resolves the key, calls
    /// MiniMax T2A, and plays the result via MCI.</summary>
    public event Action<TtsSettings, string?>? TtsTestRequested;

    /// <summary>Request to apply and persist the Libre Hardware Monitor polling settings.</summary>
    public event Action<PowerMonitorSettings>? PowerMonitorSettingsSaved;

    /// <summary>Request a one-shot snapshot read against the configured endpoint.
    /// Runtime reads <c>data.json</c> and calls back via <see cref="ShowPowerMonitorTestResult"/>.</summary>
    public event Action<PowerMonitorSettings>? PowerMonitorTestRequested;

    /// <summary>Request to fire the alert pipeline once (sound + status), useful for the "AlertTest" button.</summary>
    public event Action? PowerMonitorAlertTestRequested;

    /// <summary>Request to wipe the on-disk power history jsonl (after user confirms elsewhere).</summary>
    public event Action? PowerMonitorHistoryClearRequested;

    /// <summary>Request to atomically apply and persist Ocean Eyes trigger settings.</summary>
    public event Action<OceanEyesTriggerSettings>? OceanEyesTriggerSettingsSaved;

    /// <summary>
    /// R40: request to apply and persist the Ocean Eyes screenshot/save settings
    /// (path + auto-save / clipboard / UIA-assist toggles).
    /// </summary>
    public event Action<OceanEyesCaptureSettings>? OceanEyesCaptureSettingsSaved;

    /// <summary>R32: request to apply and persist the Spotlight (launcher-search) hotkey.</summary>
    public event Action<SpotlightTriggerSettings>? SpotlightTriggerSettingsSaved;

    /// <summary>R54: request to apply and persist the clipboard-history popup hotkey.</summary>
    public event Action<ClipboardHistoryTriggerSettings>? ClipboardHistoryTriggerSettingsSaved;

    /// <summary>R54: request to apply and persist the clipboard-history feature toggles.</summary>
    public event Action<ClipboardHistorySettings>? ClipboardHistorySettingsSaved;

    /// <summary>R54: request to clear all non-pinned clipboard history.</summary>
    public event Action? ClipboardHistoryClearRequested;

    /// <summary>
    /// R37: request to apply and persist the toolbar built-in shortcut keys
    /// (Prompt/Copy/Paste, defaults R/C/V). Raised after Validate passes.
    /// </summary>
    public event Action<ToolbarShortcutSettings>? ToolbarShortcutsSaved;

    /// <summary>
    /// UI language changed. App persists <c>ui-language.json</c> and triggers
    /// a restart — Strings' dictionary is snapshotted at process start, so a
    /// fresh process is what actually swaps the UI text.
    /// </summary>
    public event Action<AppLanguage>? UiLanguageSaved;

    /// <summary>Tracks the language the App pushed in at startup.</summary>
    private AppLanguage _currentUiLanguage = AppLanguage.English;

    /// <summary>Request to persist the display name used by the phone greeting.</summary>
    public event Action<UserProfileSettings>? UserProfileSettingsSaved;

    /// <summary>
    /// Request to apply and persist the launch-at-startup toggle. App writes
    /// <c>startup-options.json</c> AND mutates the HKCU Run key (the registry
    /// is the truth source; the JSON records the user's intent).
    /// </summary>
    public event Action<StartupSettings>? StartupSettingsSaved;

    // ── Data push from runtime → UI ──

    public void Configure(string capturePolicyFile)
    {
        PolicyPathText.Text = capturePolicyFile;
    }

    /// <summary>
    /// Pushes the current UI language into the General → Language ComboBox and
    /// status line. Called once at startup after the App reads
    /// <c>ui-language.json</c>. The ComboBox is populated from
    /// <see cref="AppLanguage.Supported"/> (English / 简体中文) and the current
    /// selection is set to match <paramref name="language"/>.
    /// </summary>
    public void SetUiLanguage(AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        _currentUiLanguage = language;
        if (LanguageComboBox is null) return;  // defensive: XAML hot-reload path

        // Populate the dropdown with the two supported languages, shown in
        // their own native names (English / 简体中文) so the user can read the
        // choice regardless of the currently active language.
        LanguageComboBox.Items.Clear();
        int selectedIndex = 0;
        for (int i = 0; i < AppLanguage.Supported.Count; i++)
        {
            AppLanguage lang = AppLanguage.Supported[i];
            LanguageComboBox.Items.Add(lang.IsChinese
                ? Strings.Settings_LanguageName_Chinese
                : Strings.Settings_LanguageName_English);
            if (lang.Code == language.Code)
            {
                selectedIndex = i;
            }
        }
        LanguageComboBox.SelectedIndex = selectedIndex;
        UpdateLanguageStatus(language);
    }

    private void UpdateLanguageStatus(AppLanguage language)
    {
        if (LanguageStatusText is null) return;
        string name = language.IsChinese
            ? Strings.Settings_LanguageName_Chinese
            : Strings.Settings_LanguageName_English;
        LanguageStatusText.Text = string.Format(
            Strings.Settings_LanguageCard_StatusCurrent, name);
    }

    /// <summary>
    /// Save button on the Language card. Reads the ComboBox selection, raises
    /// <see cref="UiLanguageSaved"/> (App persists + restarts). If the user
    /// picked the same language that's already active, no-op — avoids a
    /// pointless restart.
    /// </summary>
    private void OnSaveLanguageClick(object? sender, RoutedEventArgs e)
    {
        int index = LanguageComboBox.SelectedIndex;
        if (index < 0 || index >= AppLanguage.Supported.Count)
        {
            return;
        }
        AppLanguage selected = AppLanguage.Supported[index];
        if (selected.Code == _currentUiLanguage.Code)
        {
            // Nothing to do — spare the user a pointless restart.
            UpdateLanguageStatus(selected);
            return;
        }
        _currentUiLanguage = selected;
        if (LanguageStatusText is not null)
        {
            LanguageStatusText.Text = Strings.Settings_LanguageCard_StatusSaved;
        }
        UiLanguageSaved?.Invoke(selected);
    }

    public void SetDashboardUsage(DashboardUsageSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        int[] counts = [summary.OceanEyes, summary.Actions, summary.Launcher, summary.Clipboard];
        int total = counts.Sum();
        int maximum = Math.Max(1, counts.Max());

        DashboardTotalEventsText.Text = total.ToString();
        DashboardOceanEyesCountText.Text = summary.OceanEyes.ToString();
        DashboardActionsCountText.Text = summary.Actions.ToString();
        DashboardLauncherCountText.Text = summary.Launcher.ToString();
        DashboardClipboardCountText.Text = summary.Clipboard.ToString();

        DashboardOceanEyesBar.Maximum = maximum;
        DashboardActionsBar.Maximum = maximum;
        DashboardLauncherBar.Maximum = maximum;
        DashboardClipboardBar.Maximum = maximum;
        DashboardOceanEyesBar.Value = summary.OceanEyes;
        DashboardActionsBar.Value = summary.Actions;
        DashboardLauncherBar.Value = summary.Launcher;
        DashboardClipboardBar.Value = summary.Clipboard;

        DashboardUsageEmptyText.IsVisible = total == 0;
        if (total == 0)
        {
            DashboardTopFeatureText.Text = Strings.Settings_Dashboard_EmptyTitle;
            DashboardTopFeatureCountText.Text = Strings.Settings_Dashboard_WaitingHistory;
            return;
        }

        (string Id, int Count) top = new[]
        {
            (DashboardModule.OceanEyes, summary.OceanEyes),
            (DashboardModule.Actions, summary.Actions),
            (DashboardModule.Launcher, summary.Launcher),
            (DashboardModule.Clipboard, summary.Clipboard),
        }.OrderByDescending(item => item.Item2).First();

        DashboardTopFeatureText.Text = DashboardModuleLabel(top.Id);
        DashboardTopFeatureCountText.Text = string.Format(
            top.Count == 1
                ? Strings.Settings_Dashboard_LocalEventCount_Singular
                : Strings.Settings_Dashboard_LocalEventCount_Plural,
            top.Count);
    }

    private void SetDashboardModuleActive(string module, bool isActive)
    {
        if (isActive)
        {
            _activeDashboardModules.Add(module);
        }
        else
        {
            _activeDashboardModules.Remove(module);
        }

        DashboardActiveModulesText.Text = _activeDashboardModules.Count.ToString();
        DashboardModuleSummaryText.Text = _activeDashboardModules.Count == 0
            ? Strings.Settings_Dashboard_NoModules
            : string.Join(" · ", _activeDashboardModules
                .Order(StringComparer.Ordinal)
                .Select(DashboardModuleLabel));
    }

    private void RefreshDashboardModelRouteCount()
    {
        string notConfigured = Strings.Settings_Dashboard_NotConfigured;
        int routeCount = 0;
        if (!string.Equals(DashboardTextProviderText.Text, notConfigured, StringComparison.Ordinal))
        {
            routeCount++;
        }
        if (!string.Equals(DashboardVisionProviderText.Text, notConfigured, StringComparison.Ordinal))
        {
            routeCount++;
        }
        DashboardModelRouteCountText.Text = routeCount.ToString();
    }

    public void SetUserProfileSettings(
        UserProfileSettings settings,
        string? statusMessage = null,
        bool isError = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalize();
        ProfileDisplayNameInput.Text = settings.DisplayName;
        GreetingUserNameText.Text = settings.DisplayName;
        ProfileStatusText.Text = statusMessage
            ?? string.Format(Strings.Settings_Profile_StatusGreeting, settings.DisplayName);
        SetFeedbackTone(ProfileStatusText, isError);
    }

    public void SetOceanEyesTriggerSettings(
        OceanEyesTriggerSettings settings,
        string? statusMessage = null,
        bool isError = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalize();
        KeyboardShortcutToggle.IsChecked = settings.KeyboardShortcutEnabled;
        CtrlModifierCheckBox.IsChecked = settings.Modifiers.HasFlag(GlobalHotKeyModifiers.Control);
        AltModifierCheckBox.IsChecked = settings.Modifiers.HasFlag(GlobalHotKeyModifiers.Alt);
        ShiftModifierCheckBox.IsChecked = settings.Modifiers.HasFlag(GlobalHotKeyModifiers.Shift);
        WinModifierCheckBox.IsChecked = settings.Modifiers.HasFlag(GlobalHotKeyModifiers.Windows);
        ShortcutKeyComboBox.SelectedItem = settings.Key;
        if (ShortcutKeyComboBox.SelectedItem is null)
        {
            ShortcutKeyComboBox.SelectedItem = OceanEyesTriggerSettings.Default.Key;
        }
        MouseChordToggle.IsChecked = settings.MouseChordEnabled;
        ShortcutStatusText.Text = statusMessage ?? string.Format(Strings.Settings_Status_CurrentPrefix, settings.ToDisplayText());
        SummaryShortcutText.Text = settings.ToDisplayText();
        SetDashboardModuleActive(
            DashboardModule.OceanEyes,
            settings.KeyboardShortcutEnabled || settings.MouseChordEnabled);
        SetFeedbackTone(ShortcutStatusText, isError);
    }

    public void ShowOceanEyesTriggerStatus(string message, bool isError)
    {
        ShortcutStatusText.Text = message;
        SetFeedbackTone(ShortcutStatusText, isError);
    }

    /// <summary>
    /// R40: pushes the Ocean Eyes screenshot/save settings into the new card.
    /// Fields (OceanEyesSavePathTextBox etc.) are wired by the AXAML card
    /// added in R40.
    /// </summary>
    public void SetOceanEyesCaptureSettings(
        OceanEyesCaptureSettings settings,
        string? statusMessage = null,
        bool isError = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalize();
        // Guard against the AXAML card not being wired yet (early calls during
        // init). If the fields aren't materialized, stash and return.
        if (OceanEyesSavePathTextBox is null)
        {
            return;
        }

        OceanEyesSavePathTextBox.Text = settings.SavePath;
        OceanEyesAutoSaveToggle.IsChecked = settings.AutoSaveEnabled;
        OceanEyesClipboardToggle.IsChecked = settings.CopyToClipboardEnabled;
        OceanEyesUiaAssistToggle.IsChecked = settings.UiaAssistEnabled;
        OceanEyesCaptureStatusText.Text = statusMessage ?? string.Format(Strings.Settings_Status_LocationPrefix, settings.SavePath);
        SetFeedbackTone(OceanEyesCaptureStatusText, isError);
    }

    /// <summary>
    /// Pushes the launch-at-startup setting into the toggle. The toggle reflects
    /// the registry-truth that App calibrated on load, so if the user disabled
    /// BYH in Task Manager / Windows Settings it reads Off here. Status line is
    /// cleared on push; Save sets it to "已启用 / 已关闭 / 启用失败".
    /// </summary>
    public void SetStartupSettings(StartupSettings settings, string? statusMessage = null, bool isError = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalize();
        // Guard against the AXAML card not being wired yet (early calls during init).
        if (LaunchAtStartupToggle is null)
        {
            return;
        }

        LaunchAtStartupToggle.IsChecked = settings.LaunchAtStartup;
        StartupStatusText.Text = statusMessage ?? string.Empty;
        SetFeedbackTone(StartupStatusText, isError);
    }

    /// <summary>
    /// R32: pushes the Spotlight trigger settings into the Spotlight shortcut
    /// card. Mirror of SetOceanEyesTriggerSettings but for the
    /// second card. The AXAML fields (SpotlightKeyboardShortcutToggle etc.)
    /// are added by the launcher-settings card expansion.
    /// </summary>
    public void SetSpotlightTriggerSettings(
        SpotlightTriggerSettings settings,
        string? statusMessage = null,
        bool isError = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalize();
        // Guard against the AXAML card not being wired yet (the fields are
        // populated by the launcher-settings expansion; if missing this is a
        // no-op so the runtime never crashes the UI).
        if (SpotlightKeyboardShortcutToggle is null) return;

        SpotlightKeyboardShortcutToggle.IsChecked = settings.KeyboardShortcutEnabled;
        SpotlightCtrlModifierCheckBox.IsChecked = settings.Modifiers.HasFlag(GlobalHotKeyModifiers.Control);
        SpotlightAltModifierCheckBox.IsChecked = settings.Modifiers.HasFlag(GlobalHotKeyModifiers.Alt);
        SpotlightShiftModifierCheckBox.IsChecked = settings.Modifiers.HasFlag(GlobalHotKeyModifiers.Shift);
        SpotlightWinModifierCheckBox.IsChecked = settings.Modifiers.HasFlag(GlobalHotKeyModifiers.Windows);
        SpotlightShortcutKeyComboBox.SelectedItem = settings.Key;
        if (SpotlightShortcutKeyComboBox.SelectedItem is null)
        {
            SpotlightShortcutKeyComboBox.SelectedItem = SpotlightTriggerSettings.Default.Key;
        }
        // R54 window size — pushed alongside the hotkey card (saved together).
        SpotlightWindowWidthInput.Text = settings.WindowWidth.ToString();
        SpotlightWindowHeightInput.Text = settings.WindowHeight.ToString();
        SpotlightShortcutStatusText.Text = statusMessage ?? string.Format(Strings.Settings_Status_CurrentPrefix, settings.ToDisplayText());
        PhoneLauncherHotkeyText.Text = settings.KeyboardShortcutEnabled
            ? settings.ToDisplayText()
            : Strings.Settings_Status_Disabled;
        SetFeedbackTone(SpotlightShortcutStatusText, isError);
    }

    /// <summary>
    /// R54: pushes the clipboard-history trigger settings into the shortcut card.
    /// Mirror of <see cref="SetSpotlightTriggerSettings"/>.
    /// </summary>
    public void SetClipboardHistoryTriggerSettings(
        ClipboardHistoryTriggerSettings settings,
        string? statusMessage = null,
        bool isError = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalize();
        if (ClipboardHistoryKeyboardShortcutToggle is null) return;

        ClipboardHistoryKeyboardShortcutToggle.IsChecked = settings.KeyboardShortcutEnabled;
        ClipboardHistoryCtrlModifierCheckBox.IsChecked = settings.Modifiers.HasFlag(GlobalHotKeyModifiers.Control);
        ClipboardHistoryAltModifierCheckBox.IsChecked = settings.Modifiers.HasFlag(GlobalHotKeyModifiers.Alt);
        ClipboardHistoryShiftModifierCheckBox.IsChecked = settings.Modifiers.HasFlag(GlobalHotKeyModifiers.Shift);
        ClipboardHistoryWinModifierCheckBox.IsChecked = settings.Modifiers.HasFlag(GlobalHotKeyModifiers.Windows);
        ClipboardHistoryShortcutKeyComboBox.SelectedItem = settings.Key;
        if (ClipboardHistoryShortcutKeyComboBox.SelectedItem is null)
        {
            ClipboardHistoryShortcutKeyComboBox.SelectedItem = ClipboardHistoryTriggerSettings.Default.Key;
        }
        ClipboardHistoryShortcutStatusText.Text = statusMessage ?? string.Format(Strings.Settings_Status_CurrentPrefix, settings.ToDisplayText());
        SummaryClipboardText.Text = settings.KeyboardShortcutEnabled
            ? settings.ToDisplayText()
            : Strings.Settings_Status_Disabled;
        PhoneClipboardHotkeyText.Text = settings.KeyboardShortcutEnabled
            ? settings.ToDisplayText()
            : Strings.Settings_Status_Disabled;
        SetFeedbackTone(ClipboardHistoryShortcutStatusText, isError);
    }

    /// <summary>
    /// R54: pushes the clipboard-history feature toggles into the settings card.
    /// </summary>
    public void SetClipboardHistorySettings(ClipboardHistorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalize();
        if (ClipboardHistoryEnabledToggle is null) return;

        ClipboardHistoryEnabledToggle.IsChecked = settings.Enabled;
        ClipboardHistoryAutoPasteToggle.IsChecked = settings.AutoPasteEnabled;
        ClipboardHistoryMaskSensitiveToggle.IsChecked = settings.MaskSensitiveEnabled;
        ClipboardHistoryMaxEntriesInput.Text = settings.MaxEntries.ToString();
        // R54 v2: image capture master switch + separate (smaller) image cap.
        ClipboardHistoryCaptureImagesToggle.IsChecked = settings.CaptureImagesEnabled;
        ClipboardHistoryMaxImageEntriesInput.Text = settings.MaxImageEntries.ToString();
        // R54 window size — pushed alongside the other behavior fields.
        ClipboardHistoryWindowWidthInput.Text = settings.WindowWidth.ToString();
        ClipboardHistoryWindowHeightInput.Text = settings.WindowHeight.ToString();
        ClipboardHistoryExcludeAppsInput.Text = string.Join(", ", settings.ExcludeProcessNames);
        PhoneClipboardStatusText.Text = settings.Enabled
            ? Strings.Settings_Phone_Clipboard_StatusActive
            : Strings.Settings_Phone_Clipboard_StatusPaused;
        PhoneClipboardRetentionText.Text = string.Format(
            Strings.Settings_Phone_Clipboard_RetentionSummary,
            settings.MaxEntries,
            settings.MaxImageEntries);
        SetDashboardModuleActive(DashboardModule.Clipboard, settings.Enabled);
    }

    /// <summary>R54: sets the feature-settings status line (save result).</summary>
    public void SetClipboardHistorySettingsStatus(string message, bool isError)
    {
        if (ClipboardHistorySettingsStatusText is null) return;
        ClipboardHistorySettingsStatusText.Text = message;
        SetFeedbackTone(ClipboardHistorySettingsStatusText, isError);
    }

    private static void SetFeedbackTone(TextBlock target, bool isError)
    {
        target.Classes.Remove("FeedbackSuccess");
        target.Classes.Remove("FeedbackError");
        target.Classes.Add(isError ? "FeedbackError" : "FeedbackSuccess");
    }

    private void OnSaveUserProfileClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new UserProfileSettings
            {
                DisplayName = ProfileDisplayNameInput.Text ?? string.Empty,
            }.Normalize();
            settings.Validate();
            UserProfileSettingsSaved?.Invoke(settings);
        }
        catch (ArgumentException exception)
        {
            ProfileStatusText.Text = exception.Message;
            SetFeedbackTone(ProfileStatusText, isError: true);
        }
    }

    /// <summary>
    /// Pushes the current prompt templates into the three preview rows. Called
    /// by App whenever templates change (load, save, reset).
    /// </summary>
    public void SetPromptTemplates(PromptTemplateSet templates)
    {
        _promptTemplates = templates;
        RefreshPromptPreviews();
        SetDashboardModuleActive(DashboardModule.Actions, templates.AsList().Count > 0);
    }

    private void RefreshPromptPreviews()
    {
        _functionRows.Clear();
        foreach (PromptTemplate t in _promptTemplates.AsList())
        {
            bool isBuiltIn = PromptActionIds.IsBuiltIn(t.Id);
            string fallback = t.Id == PromptActionIds.Translate
                ? Strings.Settings_ProviderPromptDefault
                : Strings.Settings_ProviderPromptNotSet;
            string preview = string.IsNullOrWhiteSpace(t.Prompt) ? fallback : Truncate(t.Prompt, 60);

            string actionId = t.Id;  // capture for closure
            var row = new PromptFunctionRow
            {
                Id = actionId,
                Name = GetSettingsActionName(t),
                Preview = preview,
                IsBuiltIn = isBuiltIn,
                EditCommand = new RelayCommand(() => OpenPromptEditor(actionId)),
                DeleteCommand = isBuiltIn ? null : new RelayCommand(() => PromptTemplateDeleted?.Invoke(actionId)),
            };
            _functionRows.Add(row);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string GetSettingsActionName(PromptTemplate template) => template.Id switch
    {
        PromptActionIds.Translate => Strings.Settings_ActionName_Translate,
        PromptActionIds.Summarize => Strings.Settings_ActionName_Summarize,
        PromptActionIds.Explain => Strings.Settings_ActionName_Explain,
        // Custom action: pick the variant for the current UI language, falling
        // back to the other variant (and finally the action id) if empty.
        _ => template.Name.Current(template.Id),
    };

    // ── Launcher entry management ──

    /// <summary>
    /// Pushes the current launcher entries into the settings card rows.
    /// Called by App whenever entries change (load, add, save, delete, move).
    /// </summary>
    public void SetLauncherEntries(IReadOnlyList<LauncherEntry> entries)
    {
        RefreshLauncherRows(entries);
        SetDashboardModuleActive(DashboardModule.Launcher, entries.Count > 0);
        PhoneLauncherCountText.Text = string.Format(
            entries.Count == 1
                ? Strings.Settings_Phone_Launcher_DestinationsCount_Singular
                : Strings.Settings_Phone_Launcher_DestinationsCount_Plural,
            entries.Count);
        PhoneLauncherPreviewText.Text = entries.Count == 0
            ? Strings.Settings_Phone_Launcher_NoDestinations
            : string.Join(" · ", entries.Take(3).Select(entry => entry.Name));
    }

    /// <summary>
    /// Sets the launcher scan status line under the Launcher title. Used by App
    /// to report the result of an installed-app scan/import ("已导入 3 个" or
    /// "没有发现新的可导入应用。").
    /// </summary>
    public void SetLauncherSettingsStatus(string message, bool isError)
    {
        if (LauncherScanStatusText is null) return;
        LauncherScanStatusText.Text = message;
        SetFeedbackTone(LauncherScanStatusText, isError);
    }

    /// <summary>
    /// Updates the icon for a launcher row identified by <paramref name="entryId"/>.
    /// Used by App to push asynchronously-loaded icons. Posts to UI thread.
    /// </summary>
    public void UpdateLauncherIcon(string entryId, Avalonia.Media.Imaging.Bitmap? icon)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (LauncherEntryRow row in _launcherRows)
            {
                if (row.Id == entryId)
                {
                    row.Icon = icon;
                    break;
                }
            }
        });
    }

    private void RefreshLauncherRows(IReadOnlyList<LauncherEntry> entries)
    {
        _launcherRows.Clear();
        foreach (LauncherEntry entry in entries)
        {
            string entryId = entry.Id; // capture for closure
            _launcherRows.Add(new LauncherEntryRow
            {
                Id = entryId,
                Name = entry.Name,
                Kind = entry.Kind,
                Target = entry.Target,
                Arguments = entry.Arguments,
                EditCommand = new RelayCommand(() => OpenLauncherEditor(entryId)),
                DeleteCommand = new RelayCommand(() => LauncherEntryDeleted?.Invoke(entryId)),
                MoveUpCommand = new RelayCommand(() => LauncherEntryMoved?.Invoke(entryId, -1)),
                MoveDownCommand = new RelayCommand(() => LauncherEntryMoved?.Invoke(entryId, 1)),
            });
        }
    }

    /// <summary>"＋ 新增启动项" clicked: open the editor in new-mode.</summary>
    private void OnAddLauncherClick(object? sender, RoutedEventArgs e)
    {
        var editor = new LauncherEntryEditWindow();
        editor.EntryCreated += (name, kind, target, args, workDir) =>
            LauncherEntryAdded?.Invoke(name, kind, target, args, workDir);
        editor.ShowForNew();
    }

    /// <summary>
    /// "🔍 扫描已安装应用" clicked: defer to the App layer, which owns the
    /// detector + dedup + selection dialog. The settings window is just the
    /// event source — it stays responsive while App runs the scan off-thread.
    /// </summary>
    private void OnScanInstalledAppsClick(object? sender, RoutedEventArgs e)
    {
        ScanInstalledAppsRequested?.Invoke();
    }

    private void OpenLauncherEditor(string id)
    {
        // Find the entry data from the current row (which has Target, Args, etc.)
        LauncherEntryRow? row = _launcherRows.FirstOrDefault(r => r.Id == id);
        if (row is null)
        {
            return;
        }

        var existing = new LauncherEntry(
            Id: row.Id,
            Name: row.Name,
            Kind: row.Kind,
            Target: row.Target,
            Arguments: row.Arguments);

        var editor = new LauncherEntryEditWindow();
        editor.EntrySaved += (savedId, savedName, kind, target, args, workDir) =>
            LauncherEntrySaved?.Invoke(savedId, savedName, kind, target, args, workDir);
        editor.ShowFor(id, existing);
    }

    // ── Prompt template edit handlers ──

    /// <summary>"＋ 新增功能" clicked: open the editor in new-mode.</summary>
    private void OnAddFunctionClick(object? sender, RoutedEventArgs e)
    {
        var editor = new PromptTemplateEditWindow();
        editor.TemplateCreated += (name, prompt, thinking, shortcut) =>
            PromptTemplateAdded?.Invoke(name, prompt, thinking, shortcut);
        editor.ShowForNew();
    }

    private void OpenPromptEditor(string actionId)
    {
        PromptTemplate? current = _promptTemplates.Find(actionId);
        if (current is null)
        {
            return;
        }
        PromptTemplate? @default = BuiltInDefaults.Find(actionId);

        var editor = new PromptTemplateEditWindow();
        editor.TemplateSaved += (savedId, newPrompt, thinking, shortcut, newName) =>
            PromptTemplateSaved?.Invoke(savedId, newPrompt, thinking, shortcut, newName);
        editor.TemplateReset += (resetId) => PromptTemplateReset?.Invoke(resetId);
        editor.ShowFor(
            actionId,
            current.Name,
            current.Prompt,
            current.ThinkingEnabled,
            @default?.Prompt ?? string.Empty,
            current.Shortcut);
    }

    /// <summary>
    /// Refreshes the provider ComboBox + edit form. Called by App whenever the
    /// provider list or selection changes. The hasKeyChecker resolves each
    /// provider's key status asynchronously.
    /// </summary>
    public async void SetProviders(
        IReadOnlyList<ProviderProfileEntry> providers,
        string? currentId,
        Func<string, Task<bool>> hasKeyChecker)
    {
        ProviderProfileEntry? pendingDraft = _draftProviderId is null
            ? null
            : _providers.FirstOrDefault(p => p.Id == _draftProviderId);
        bool draftWasPersisted = _draftProviderId is not null &&
            providers.Any(p => string.Equals(p.Id, _draftProviderId, StringComparison.OrdinalIgnoreCase));

        _providers = [..providers];
        if (draftWasPersisted)
        {
            _draftProviderId = null;
        }
        else if (pendingDraft is not null)
        {
            // A settings refresh unrelated to provider saving should not make
            // the user's unsaved Custom draft disappear from the editor.
            _providers.Add(pendingDraft);
        }
        _currentProviderId = currentId;
        _hasKeyChecker = hasKeyChecker;

        ProviderProfileEntry? activeProvider = providers.FirstOrDefault(p => p.Id == currentId);
        SummaryProviderText.Text = activeProvider is null
            ? Strings.Settings_Status_NoProvider
            : $"{activeProvider.Name}\n{activeProvider.DefaultModel}";
        PhoneProviderNameText.Text = activeProvider?.Name ?? Strings.Settings_Status_NoProvider;
        PhoneProviderModelText.Text = activeProvider?.DefaultModel ?? Strings.Settings_Phone_Translation_ModelFallback;
        DashboardTextProviderText.Text = activeProvider?.Name ?? Strings.Settings_Dashboard_NotConfigured;
        DashboardTextModelText.Text = activeProvider?.DefaultModel ?? Strings.Settings_Dashboard_NoModelSelected;
        SetDashboardModuleActive(DashboardModule.Translation, activeProvider is not null);
        RefreshDashboardModelRouteCount();
        PhoneProviderCountText.Text = string.Format(
            providers.Count == 1
                ? Strings.Settings_Phone_Translation_ProviderCount_Singular
                : Strings.Settings_Phone_Translation_ProviderCount_Plural,
            providers.Count);

        // Rebuild ComboBox options. Keep the label short: just the provider
        // display name (e.g. "DeepSeek"). The model id is visible in the edit
        // form below, so repeating it in the dropdown made the label cramped
        // and redundant.
        _providerOptions.Clear();
        Dictionary<string, int> providerNameCounts = _providers
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (ProviderProfileEntry p in _providers)
        {
            string label;
            if (p.Id == _draftProviderId)
            {
                label = $"{p.Name} · {Strings.Settings_Provider_New}";
            }
            else if (providerNameCounts[p.Name] > 1)
            {
                // Older versions could create several indistinguishable
                // "Custom Provider" rows. Keep them operable by showing a
                // short stable id suffix instead of silently collapsing them.
                string suffix = p.Id.Length > 4 ? p.Id[^4..] : p.Id;
                label = $"{p.Name} · {suffix}";
            }
            else
            {
                label = p.Name;
            }
            _providerOptions.Add(new ProviderOption(p.Id, label));
        }

        // Select a provider WITHOUT throwing away the user's in-progress edit.
        // Priority: the provider the user is currently editing (set when they
        // pick/add one) → the active default → first. Previously this always
        // jumped to the default on every refresh, so adding SiliconFlow then
        // saving its key would snap the combo back to DeepSeek and clobber the
        // form (looked like "reset to default" + "key not shown").
        string? preferredId = _editingProviderId ?? currentId;
        int selectedIndex = -1;
        if (preferredId is not null)
        {
            ProviderOption? match = _providerOptions.FirstOrDefault(o => o.Id == preferredId);
            if (match is not null)
            {
                selectedIndex = _providerOptions.IndexOf(match);
            }
        }

        // If the editing id is no longer in the list (deleted), fall back to default.
        if (selectedIndex < 0 && currentId is not null)
        {
            ProviderOption? defaultMatch = _providerOptions.FirstOrDefault(o => o.Id == currentId);
            if (defaultMatch is not null)
            {
                selectedIndex = _providerOptions.IndexOf(defaultMatch);
                _editingProviderId = currentId;
            }
        }

        ProviderComboBox.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;

        // R24 track B: the vision OCR provider combo uses the same provider list
        // (the user picks which provider entry powers OCR screenshots).
        _visionProviderOptions.Clear();
        foreach (ProviderProfileEntry p in providers)
        {
            _visionProviderOptions.Add(new ProviderOption(p.Id, p.Name));
        }

        // Load the selected provider into the edit form.
        await LoadSelectedProviderIntoForm();
    }

    /// <summary>
    /// R24 track B: pushes the current vision OCR settings + known model presets
    /// into the vision card. Called by the runtime after providers are loaded.
    /// </summary>
    public void SetVisionSettings(VisionCaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        VisionEnabledToggle.IsChecked = settings.Enabled;

        // Select the configured provider if present.
        int visionIndex = -1;
        for (int i = 0; i < _visionProviderOptions.Count; i++)
        {
            if (_visionProviderOptions[i].Id == settings.ProviderId)
            {
                visionIndex = i;
                break;
            }
        }
        VisionProviderComboBox.SelectedIndex = visionIndex >= 0 ? visionIndex : 0;

        // Model combo: pre-seed common OCR models, then append the configured one
        // if it isn't already listed (keeps custom model ids selectable/editable).
        VisionModelComboBox.Items.Clear();
        foreach (string model in VisionModelPresets.All)
        {
            VisionModelComboBox.Items.Add(model);
        }

        if (!VisionModelPresets.All.Contains(settings.Model))
        {
            VisionModelComboBox.Items.Add(settings.Model);
        }

        VisionModelComboBox.SelectedItem = settings.Model;
        if (VisionModelComboBox.SelectedItem is null && VisionModelComboBox.ItemCount > 0)
        {
            VisionModelComboBox.SelectedIndex = 0;
        }

        VisionPromptInput.Text = settings.OcrPrompt;
        VisionUiaPrefillToggle.IsChecked = settings.UiaPrefillEnabled;
        VisionDisableThinkingToggle.IsChecked = settings.DisableThinking;

        string visionProviderName = _visionProviderOptions
            .FirstOrDefault(p => p.Id == settings.ProviderId)?.DisplayLabel
            ?? settings.ProviderId;
        SummaryVisionText.Text = settings.Enabled
            ? $"{visionProviderName}\n{settings.Model}"
            : Strings.Settings_Status_Disabled;
        PhoneVisionStatusText.Text = settings.Enabled
            ? Strings.Settings_Phone_Vision_StatusReady
            : Strings.Settings_Phone_Vision_StatusDisabled;
        PhoneVisionProviderText.Text = visionProviderName;
        PhoneVisionModelText.Text = settings.Model;
        DashboardVisionProviderText.Text = settings.Enabled ? visionProviderName : Strings.Settings_Dashboard_NotConfigured;
        DashboardVisionModelText.Text = settings.Enabled ? settings.Model : Strings.Settings_Dashboard_NoModelSelected;
        SetDashboardModuleActive(DashboardModule.Vision, settings.Enabled);
        RefreshDashboardModelRouteCount();
        PhoneVisionAssistText.Text = settings.UiaPrefillEnabled
            ? Strings.Settings_Phone_Vision_UiaAssistOn
            : Strings.Settings_Phone_Vision_OcrOnly;
    }

    private void OnSaveVisionClick(object? sender, RoutedEventArgs e)
    {
        string providerId = (VisionProviderComboBox.SelectedItem as ProviderOption)?.Id
            ?? (_visionProviderOptions.Count > 0 ? _visionProviderOptions[0].Id : "siliconflow");
        string? model = VisionModelComboBox.SelectedItem as string
            ?? VisionModelComboBox.Text;
        if (string.IsNullOrWhiteSpace(model))
        {
            model = VisionCaptureSettings.Default.Model;
        }

        var settings = new VisionCaptureSettings
        {
            Enabled = VisionEnabledToggle.IsChecked == true,
            ProviderId = providerId,
            Model = model.Trim(),
            OcrPrompt = string.IsNullOrWhiteSpace(VisionPromptInput.Text)
                ? "Free OCR."
                : VisionPromptInput.Text.Trim(),
            UiaPrefillEnabled = VisionUiaPrefillToggle.IsChecked == true,
            DisableThinking = VisionDisableThinkingToggle.IsChecked == true,
        };

        VisionSettingsSaved?.Invoke(settings);
    }

    // ── 朗读 (TTS) settings ───────────────────────────────────────────────

    /// <summary>Pushed by App after loading tts.json. Populates the TTS form.
    /// The credential source (not a plain bool) drives the status label so the
    /// UI never reports "Key not set" when the runtime can resolve mmx's login.</summary>
    public void SetTtsSettings(TtsSettings settings, TtsCredentialSource credentialSource)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TtsEnabledToggle.IsChecked = settings.Enabled;
        TtsRegionComboBox.SelectedIndex =
            string.Equals(settings.Region, "cn", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        // Voice combo is editable + has presets; select the configured voice if
        // listed, otherwise seed the editable text so the user sees the value.
        SelectTtsVoice(settings.Voice);
        TtsSpeedSlider.Value = settings.Speed;
        UpdateTtsSpeedLabel();
        TtsApiKeyInput.Text = string.Empty; // never echo the secret back
        TtsApiKeyStatusText.Text = credentialSource switch
        {
            TtsCredentialSource.ByhSecret => Strings.Settings_Key_Saved,
            TtsCredentialSource.MmxConfig => Strings.Settings_Key_UsingMmx,
            _ => Strings.Settings_Key_NotSet,
        };
        TtsStatusText.Text = string.Empty;
    }

    private void SelectTtsVoice(string voiceId)
    {
        for (int i = 0; i < TtsVoiceComboBox.ItemCount; i++)
        {
            if (TtsVoiceComboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content as string, voiceId, StringComparison.Ordinal))
            {
                TtsVoiceComboBox.SelectedIndex = i;
                return;
            }
        }
        // Not a preset — type it into the editable combo so it's still visible
        // and saved back unchanged on next Save.
        TtsVoiceComboBox.SelectedItem = null;
        TtsVoiceComboBox.Text = voiceId;
    }

    private string ReadTtsVoice()
    {
        if (TtsVoiceComboBox.SelectedItem is ComboBoxItem item)
        {
            return item.Content as string ?? "auto";
        }
        string? text = TtsVoiceComboBox.Text;
        return string.IsNullOrWhiteSpace(text) ? "auto" : text.Trim();
    }

    private void UpdateTtsSpeedLabel() =>
        TtsSpeedLabel.Text = $"{TtsSpeedSlider.Value:F1}×";

    /// <summary>Builds a TtsSettings snapshot from the current form values.</summary>
    private TtsSettings BuildTtsSettings() => new()
    {
        Enabled = TtsEnabledToggle.IsChecked == true,
        ApiKeyReference = "secret://tts/minimax",
        Region = TtsRegionComboBox.SelectedIndex == 1 ? "cn" : "global",
        Voice = ReadTtsVoice(),
        Speed = TtsSpeedSlider.Value,
    };

    private void OnSaveTtsClick(object? sender, RoutedEventArgs e)
    {
        TtsSettings settings = BuildTtsSettings();
        string? newKey = NormalizeInput(TtsApiKeyInput.Text);
        TtsSettingsSaved?.Invoke(settings, newKey);
        // Clear the field so a re-open doesn't re-save the plaintext.
        TtsApiKeyInput.Text = string.Empty;
    }

    private void OnTtsTestClick(object? sender, RoutedEventArgs e)
    {
        TtsSettings settings = BuildTtsSettings();
        string? newKey = NormalizeInput(TtsApiKeyInput.Text);
        TtsTestButton.IsEnabled = false;
        TtsStatusText.Text = Strings.Settings_TTS_Testing;
        try
        {
            // Fire the test event; the runtime synthesizes + plays on a
            // background thread. We can't await its completion from here, so the
            // status flips to a "sent" message and the user hears the audio.
            TtsTestRequested?.Invoke(settings, newKey);
            TtsStatusText.Text = Strings.Settings_TTS_TestOk;
        }
        catch (Exception exception)
        {
            TtsStatusText.Text = string.Format(Strings.Settings_TTS_TestFailed, exception.Message);
        }
        finally
        {
            TtsTestButton.IsEnabled = true;
        }
    }

    private void OnToggleTtsKeyVisibilityClick(object? sender, RoutedEventArgs e) =>
        TtsApiKeyInput.PasswordChar = TtsApiKeyInput.PasswordChar == '\0' ? '•' : '\0';

    // ── 功耗监控 (PowerMonitor) settings ──────────────────────────────────

    /// <summary>Pushed by App after loading power-monitor.json. Populates the form.</summary>
    public void SetPowerMonitorSettings(
        PowerMonitorSettings settings,
        string historyPath,
        bool historyExists,
        long historyBytes,
        int historySamples)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PowerMonitorEnabledToggle.IsChecked = settings.Enabled;
        PowerMonitorEndpointInput.Text = settings.Endpoint;
        PowerMonitorPollIntervalInput.Value = settings.PollIntervalMs;
        PowerMonitorShowInTrayToggle.IsChecked = settings.ShowInTray;
        PowerMonitorTrackEnergyToggle.IsChecked = settings.TrackEnergy;
        PowerMonitorAlertEnabledToggle.IsChecked = settings.AlertEnabled;
        PowerMonitorCpuTempInput.Value = settings.CpuTempThresholdC;
        PowerMonitorGpuTempInput.Value = settings.GpuTempThresholdC;
        PowerMonitorSsdTempInput.Value = settings.SsdTempThresholdC;
        PowerMonitorHistoryRetentionInput.Value = settings.HistoryRetentionDays;
        PowerMonitorHistorySizeText.Text = historyExists
            ? string.Format(Strings.Settings_PowerMonitor_HistorySize, historySamples, FormatBytes(historyBytes))
            : Strings.Settings_PowerMonitor_Offline;
        PowerMonitorStatusText.Text = string.Empty;
    }

    /// <summary>App callback after the user clicks "Test". Shows the one-shot snapshot
    /// inline in the status line. Connected=false means endpoint unreachable.</summary>
    public void ShowPowerMonitorTestResult(PowerSnapshot snap)
    {
        if (!snap.Connected)
        {
            PowerMonitorStatusText.Text = Strings.Settings_PowerMonitor_Offline;
            return;
        }
        string cpuW = snap.CpuPackageWatts is { } w ? $"{w:F0}W" : "—";
        string cpuT = snap.CpuTempC is { } t ? $"{t:F0}°C" : "—";
        string gpuW = snap.GpuPowerWatts is { } w2 ? $"{w2:F0}W" : "—";
        string gpuT = snap.GpuTempC is { } t2 ? $"{t2:F0}°C" : "—";
        string totalW = $"{snap.TotalWatts:F0}W";
        PowerMonitorStatusText.Text = $"CPU {cpuW} {cpuT} · GPU {gpuW} {gpuT} · {totalW}";
    }

    /// <summary>Builds a <see cref="PowerMonitorSettings"/> from the form values, normalized.</summary>
    private PowerMonitorSettings BuildPowerMonitorSettings()
    {
        PowerMonitorSettings settings = new PowerMonitorSettings
        {
            Enabled = PowerMonitorEnabledToggle.IsChecked == true,
            Endpoint = string.IsNullOrWhiteSpace(PowerMonitorEndpointInput.Text)
                ? PowerMonitorSettings.Default.Endpoint
                : PowerMonitorEndpointInput.Text.Trim(),
            PollIntervalMs = (int)(PowerMonitorPollIntervalInput.Value ?? PowerMonitorSettings.Default.PollIntervalMs),
            ShowInTray = PowerMonitorShowInTrayToggle.IsChecked == true,
            TrackEnergy = PowerMonitorTrackEnergyToggle.IsChecked == true,
            AlertEnabled = PowerMonitorAlertEnabledToggle.IsChecked == true,
            CpuTempThresholdC = (int)(PowerMonitorCpuTempInput.Value ?? PowerMonitorSettings.Default.CpuTempThresholdC),
            GpuTempThresholdC = (int)(PowerMonitorGpuTempInput.Value ?? PowerMonitorSettings.Default.GpuTempThresholdC),
            SsdTempThresholdC = (int)(PowerMonitorSsdTempInput.Value ?? PowerMonitorSettings.Default.SsdTempThresholdC),
            HistoryRetentionDays = (int)(PowerMonitorHistoryRetentionInput.Value ?? PowerMonitorSettings.Default.HistoryRetentionDays),
        };
        return settings.Normalize();
    }

    private void OnSavePowerMonitorClick(object? sender, RoutedEventArgs e)
    {
        PowerMonitorSettings settings = BuildPowerMonitorSettings();
        try
        {
            settings.Validate();
        }
        catch (ArgumentException exception)
        {
            PowerMonitorStatusText.Text = exception.Message;
            return;
        }
        PowerMonitorSettingsSaved?.Invoke(settings);
        PowerMonitorStatusText.Text = Strings.Settings_PowerMonitor_Saved;
    }

    private void OnPowerMonitorTestClick(object? sender, RoutedEventArgs e)
    {
        PowerMonitorSettings settings = BuildPowerMonitorSettings();
        PowerMonitorStatusText.Text = Strings.Settings_PowerMonitor_Test + "...";
        PowerMonitorTestRequested?.Invoke(settings);
    }

    private void OnPowerMonitorAlertTestClick(object? sender, RoutedEventArgs e)
    {
        PowerMonitorAlertTestRequested?.Invoke();
    }

    private void OnPowerMonitorClearHistoryClick(object? sender, RoutedEventArgs e)
    {
        PowerMonitorHistoryClearRequested?.Invoke();
    }

    /// <summary>Byte-size formatter for the history label ("1.2 KB", "3.4 MB").</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F1} KB";
        double mb = kb / 1024.0;
        return $"{mb:F1} MB";
    }

    // ───────────────────────────────────────────────────────────────────────
    // R26: "Refresh Models" feature.
    //
    // _lastFetchedModels is the in-memory mirror of models-cache.json keyed by
    // provider id. Pushed in by the App layer via SetCachedModels after the
    // cache is loaded (no network) and updated in-place after each successful
    // fetch. Kept here (not re-read from disk on every selection) because the
    // provider list can change mid-session and the window already owns the
    // live provider state.
    // ───────────────────────────────────────────────────────────────────────
    private readonly Dictionary<string, (DateTime FetchedAtUtc, IReadOnlyList<string> Models)> _lastFetchedModels =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Pushed by the App layer after loading models-cache.json. Populates the
    /// Model dropdown for the currently-selected translation provider (if it
    /// has a cached entry) WITHOUT making a network call. Idempotent — also
    /// called after a successful fetch to refresh the in-memory mirror.
    /// </summary>
    public void SetCachedModels(IReadOnlyDictionary<string, (DateTime FetchedAtUtc, IReadOnlyList<string> Models)> cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _lastFetchedModels.Clear();
        foreach (KeyValuePair<string, (DateTime, IReadOnlyList<string>)> kv in cache)
        {
            _lastFetchedModels[kv.Key] = kv.Value;
        }

        // If the translation form is currently showing a provider with a cached
        // entry, refresh its dropdown + status line now.
        if (GetSelectedProvider() is { Id: var id, DefaultModel: var model })
        {
            RepopulateTranslationModelCombo(id, model);
        }

        // Vision page: refresh its status line for the currently-selected
        // vision provider. We don't rebuild the vision combo here (SetVisionSettings
        // already seeded it with VisionModelPresets.All + the configured model);
        // we just surface the "last fetched" timestamp and append cached models
        // the user previously pulled for this provider.
        string? visionProviderId = (VisionProviderComboBox.SelectedItem as ProviderOption)?.Id;
        if (!string.IsNullOrEmpty(visionProviderId) &&
            _lastFetchedModels.TryGetValue(visionProviderId, out var visionEntry))
        {
            foreach (string m in visionEntry.Models)
            {
                if (!ContainsStringItem(VisionModelComboBox, m))
                {
                    VisionModelComboBox.Items.Add(m);
                }
            }
        }
        UpdateVisionModelFetchStatus(visionProviderId);
    }

    private void UpdateVisionModelFetchStatus(string? providerId)
    {
        if (!string.IsNullOrEmpty(providerId) &&
            _lastFetchedModels.TryGetValue(providerId, out var entry))
        {
            int minutes = Math.Max(0, (int)(DateTime.UtcNow - entry.FetchedAtUtc).TotalMinutes);
            VisionModelFetchStatus.Text = string.Format(Strings.Settings_Provider_LastFetched, minutes);
            SetFeedbackTone(VisionModelFetchStatus, isError: false);
        }
        else
        {
            VisionModelFetchStatus.Text = Strings.Settings_Provider_LastFetched_Never;
            SetFeedbackTone(VisionModelFetchStatus, isError: false);
        }
    }

    /// <summary>
    /// Rebuilds the translation ModelInput dropdown for the given provider:
    /// cached fetched models first (sorted as returned), then the configured
    /// default model appended if not already present (keeps it selectable even
    /// when the upstream list doesn't include it). Sets SelectedItem to the
    /// configured model and updates the "last fetched" status line.
    /// </summary>
    private void RepopulateTranslationModelCombo(string providerId, string configuredModel)
    {
        ModelInput.Items.Clear();

        IReadOnlyList<string>? cached = _lastFetchedModels.TryGetValue(providerId, out var e) ? e.Models : null;
        if (cached is { Count: > 0 })
        {
            foreach (string m in cached)
            {
                ModelInput.Items.Add(m);
            }
        }

        // Always keep the configured model selectable (it may not appear in the
        // upstream list — e.g. a preset snapshot that drifted, or a hand-typed
        // OpenRouter "provider/model" id).
        if (!string.IsNullOrWhiteSpace(configuredModel) &&
            !ContainsStringItem(ModelInput, configuredModel))
        {
            ModelInput.Items.Add(configuredModel);
        }

        ModelInput.SelectedItem = configuredModel;
        if (ModelInput.SelectedItem is null && ModelInput.ItemCount > 0)
        {
            ModelInput.SelectedIndex = 0;
        }

        UpdateModelFetchStatus(providerId);
    }

    private void UpdateModelFetchStatus(string providerId)
    {
        if (_lastFetchedModels.TryGetValue(providerId, out var entry))
        {
            int minutes = Math.Max(0, (int)(DateTime.UtcNow - entry.FetchedAtUtc).TotalMinutes);
            ModelFetchStatus.Text = string.Format(Strings.Settings_Provider_LastFetched, minutes);
            SetFeedbackTone(ModelFetchStatus, isError: false);
        }
        else
        {
            ModelFetchStatus.Text = Strings.Settings_Provider_LastFetched_Never;
            SetFeedbackTone(ModelFetchStatus, isError: false);
        }
    }

    private async void OnFetchModelsClick(object? sender, RoutedEventArgs e)
    {
        await FetchModelsAsync(isVision: false);
    }

    private async void OnVisionFetchModelsClick(object? sender, RoutedEventArgs e)
    {
        await FetchModelsAsync(isVision: true);
    }

    /// <summary>
    /// Shared fetch driver for both translation and vision pages. Resolves the
    /// target provider id from the relevant combo, flips the reentry flag +
    /// button label, awaits the App-layer handler (which does the actual
    /// GET /models + cache write), then on success repopulates the dropdown
    /// and on failure surfaces the error in the status line. UI updates are
    /// marshalled via Dispatcher.UIThread.Post to be safe if the await resumes
    /// off the UI thread (mirrors UpdateLauncherIcon's pattern).
    /// </summary>
    private async Task FetchModelsAsync(bool isVision)
    {
        bool flag = isVision ? _isFetchingVisionModels : _isFetchingTranslationModels;
        if (flag) { return; }

        // Resolve the target provider id from the relevant combo.
        string? providerId;
        ComboBox modelCombo;
        Button fetchButton;
        TextBlock statusText;
        bool prependPresets;

        if (isVision)
        {
            providerId = (VisionProviderComboBox.SelectedItem as ProviderOption)?.Id
                ?? (_visionProviderOptions.Count > 0 ? _visionProviderOptions[0].Id : null);
            modelCombo = VisionModelComboBox;
            fetchButton = VisionFetchModelsButton;
            statusText = VisionModelFetchStatus;
            // Vision page always shows VisionModelPresets.All first, then appends
            // fetched models (de-duped) — the presets are the curated OCR set.
            prependPresets = true;
            _isFetchingVisionModels = true;
        }
        else
        {
            providerId = GetSelectedProvider()?.Id;
            modelCombo = ModelInput;
            fetchButton = FetchModelsButton;
            statusText = ModelFetchStatus;
            // Translation page: show only fetched models (no preset list).
            prependPresets = false;
            _isFetchingTranslationModels = true;
        }

        if (string.IsNullOrWhiteSpace(providerId) || FetchModelsRequested is null)
        {
            if (isVision) { _isFetchingVisionModels = false; } else { _isFetchingTranslationModels = false; }
            return;
        }

        string? baseUrlOverride = null;
        string? apiKeyOverride = null;
        int? timeoutSecondsOverride = null;
        if (!isVision)
        {
            // Model discovery intentionally validates only the endpoint. The
            // Model field is the output of this operation and therefore cannot
            // be a prerequisite for it.
            baseUrlOverride = BaseUrlInput.Text?.Trim() ?? string.Empty;
            try
            {
                ProviderProfileEntry.ValidateBaseUrl(baseUrlOverride);
            }
            catch (ArgumentException ex)
            {
                statusText.Text = string.Format(Strings.Settings_Provider_FetchFailed, ex.Message);
                SetFeedbackTone(statusText, isError: true);
                _isFetchingTranslationModels = false;
                return;
            }

            apiKeyOverride = NormalizeInput(ApiKeyInput.Text);
            if (string.Equals(providerId, _draftProviderId, StringComparison.OrdinalIgnoreCase) &&
                apiKeyOverride is null)
            {
                statusText.Text = Strings.Settings_Key_EnterFirst;
                SetFeedbackTone(statusText, isError: true);
                _isFetchingTranslationModels = false;
                return;
            }

            timeoutSecondsOverride = Math.Clamp((int)(TimeoutInput.Value ?? 60), 10, 300);
        }

        var request = new ProviderModelsFetchRequest(
            providerId,
            baseUrlOverride,
            apiKeyOverride,
            timeoutSecondsOverride);

        // Preserve the user's current selection/text so we can restore it after
        // the dropdown rebuild (the fetch may add/remove items).
        string currentModel = (modelCombo.SelectedItem as string ?? modelCombo.Text)?.Trim() ?? string.Empty;

        string originalLabel = fetchButton.Content as string ?? string.Empty;
        fetchButton.IsEnabled = false;
        fetchButton.Content = Strings.Settings_Provider_FetchingModels;
        statusText.Text = Strings.Settings_Provider_FetchingModels;
        SetFeedbackTone(statusText, isError: false);

        IReadOnlyList<string> models = Array.Empty<string>();
        DateTime fetchedAtUtc = DateTime.UtcNow;
        string? error = null;
        try
        {
            (models, fetchedAtUtc, error) = await FetchModelsRequested.Invoke(request);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            fetchButton.IsEnabled = true;
            fetchButton.Content = originalLabel;
            if (isVision) { _isFetchingVisionModels = false; } else { _isFetchingTranslationModels = false; }
        }

        // Marshal the UI mutation back to the UI thread. The await above may
        // resume on a thread-pool thread depending on the handler's ConfigureAwait.
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                statusText.Text = string.Format(Strings.Settings_Provider_FetchFailed, error);
                SetFeedbackTone(statusText, isError: true);
                // Don't clobber the dropdown on failure — keep whatever was there
                // (cached or preset list) so the user can still pick a model.
                return;
            }

            // Success: refresh the in-memory cache mirror + rebuild the dropdown.
            _lastFetchedModels[providerId!] = (fetchedAtUtc, models);

            // Remember the user's selection/text so we can restore it if the
            // fetched list still contains it.
            modelCombo.Items.Clear();

            if (prependPresets)
            {
                foreach (string preset in VisionModelPresets.All)
                {
                    if (!ContainsStringItem(modelCombo, preset))
                    {
                        modelCombo.Items.Add(preset);
                    }
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (prependPresets)
            {
                foreach (string p in VisionModelPresets.All) { seen.Add(p); }
            }
            foreach (string m in models)
            {
                if (seen.Add(m))
                {
                    modelCombo.Items.Add(m);
                }
            }

            // Keep the configured/current model selectable even if upstream
            // doesn't list it (mirrors the load path's invariant).
            if (!string.IsNullOrWhiteSpace(currentModel) && !seen.Contains(currentModel))
            {
                modelCombo.Items.Add(currentModel);
            }

            // Restore selection: prefer the prior pick if still listed.
            if (!string.IsNullOrWhiteSpace(currentModel) && ContainsStringItem(modelCombo, currentModel))
            {
                modelCombo.SelectedItem = currentModel;
            }
            else if (modelCombo.ItemCount > 0)
            {
                modelCombo.SelectedIndex = 0;
            }

            int minutes = Math.Max(0, (int)(DateTime.UtcNow - fetchedAtUtc).TotalMinutes);
            statusText.Text = string.Format(Strings.Settings_Provider_LastFetched, minutes);
            SetFeedbackTone(statusText, isError: false);
        });
    }

    /// <summary>Avalonia ComboBox.Items is a non-generic collection; this is the bare-string contains check.</summary>
    private static bool ContainsStringItem(ComboBox combo, string value)
    {
        foreach (object? item in combo.Items)
        {
            if (item is string s && string.Equals(s, value, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }


    private void OnSaveOceanEyesTriggerClick(object? sender, RoutedEventArgs e)
    {
        GlobalHotKeyModifiers modifiers = GlobalHotKeyModifiers.None;
        if (CtrlModifierCheckBox.IsChecked == true) modifiers |= GlobalHotKeyModifiers.Control;
        if (AltModifierCheckBox.IsChecked == true) modifiers |= GlobalHotKeyModifiers.Alt;
        if (ShiftModifierCheckBox.IsChecked == true) modifiers |= GlobalHotKeyModifiers.Shift;
        if (WinModifierCheckBox.IsChecked == true) modifiers |= GlobalHotKeyModifiers.Windows;

        var settings = new OceanEyesTriggerSettings
        {
            KeyboardShortcutEnabled = KeyboardShortcutToggle.IsChecked == true,
            Modifiers = modifiers,
            Key = ShortcutKeyComboBox.SelectedItem as string
                ?? OceanEyesTriggerSettings.Default.Key,
            MouseChordEnabled = MouseChordToggle.IsChecked == true,
        }.Normalize();

        try
        {
            settings.Validate();
            OceanEyesTriggerSettingsSaved?.Invoke(settings);
        }
        catch (ArgumentException exception)
        {
            ShowOceanEyesTriggerStatus(exception.Message, isError: true);
        }
    }

    /// <summary>
    /// R40: read the Ocean Eyes capture card (path + 3 toggles), validate, and
    /// raise <see cref="OceanEyesCaptureSettingsSaved"/>.
    /// </summary>
    private void OnSaveOceanEyesCaptureClick(object? sender, RoutedEventArgs e)
    {
        var settings = new OceanEyesCaptureSettings
        {
            SavePath = OceanEyesSavePathTextBox.Text ?? string.Empty,
            AutoSaveEnabled = OceanEyesAutoSaveToggle.IsChecked == true,
            CopyToClipboardEnabled = OceanEyesClipboardToggle.IsChecked == true,
            UiaAssistEnabled = OceanEyesUiaAssistToggle.IsChecked == true,
        }.Normalize();

        try
        {
            settings.Validate();
            OceanEyesCaptureSettingsSaved?.Invoke(settings);
        }
        catch (ArgumentException exception)
        {
            SetOceanEyesCaptureSettings(settings, exception.Message, isError: true);
        }
    }

    /// <summary>
    /// Read the launch-at-startup toggle and raise
    /// <see cref="StartupSettingsSaved"/>. The App handler writes the JSON file
    /// AND mutates the HKCU Run key, then calls back with the real outcome
    /// (enable may fail under group policy / AV, in which case the toggle rolls
    /// back to Off and the status shows "启用失败").
    /// </summary>
    private void OnSaveStartupClick(object? sender, RoutedEventArgs e)
    {
        var settings = new StartupSettings
        {
            LaunchAtStartup = LaunchAtStartupToggle.IsChecked == true,
        }.Normalize();

        try
        {
            settings.Validate();
            StartupSettingsSaved?.Invoke(settings);
        }
        catch (ArgumentException exception)
        {
            SetStartupSettings(settings, exception.Message, isError: true);
        }
    }

    /// <summary>
    /// R40: opens a folder picker and writes the chosen path into the save-path
    /// text box. Doesn't auto-save — the user clicks Save to persist.
    /// </summary>
    private async void OnBrowseOceanEyesSavePathClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = Strings.Settings_Picker_SaveFolder,
                AllowMultiple = false,
            });

        if (folders.Count > 0)
        {
            // IStorageFolder exposes only Path (not TryGetLocalPath, which is
            // on IStorageItem). Use Path.LocalPath when the URI is a file://,
            // else fall back to the string form.
            OceanEyesSavePathTextBox.Text = folders[0].Path.IsFile
                ? folders[0].Path.LocalPath
                : folders[0].Path.ToString();
        }
    }

    /// <summary>
    /// R32: read the Spotlight shortcut card, validate, and raise
    /// <see cref="SpotlightTriggerSettingsSaved"/>. Mirrors
    /// <see cref="OnSaveOceanEyesTriggerClick"/> without the mouse-chord toggle.
    /// </summary>
    private void OnSaveSpotlightTriggerClick(object? sender, RoutedEventArgs e)
    {
        GlobalHotKeyModifiers modifiers = GlobalHotKeyModifiers.None;
        if (SpotlightCtrlModifierCheckBox.IsChecked == true) modifiers |= GlobalHotKeyModifiers.Control;
        if (SpotlightAltModifierCheckBox.IsChecked == true) modifiers |= GlobalHotKeyModifiers.Alt;
        if (SpotlightShiftModifierCheckBox.IsChecked == true) modifiers |= GlobalHotKeyModifiers.Shift;
        if (SpotlightWinModifierCheckBox.IsChecked == true) modifiers |= GlobalHotKeyModifiers.Windows;

        // R54 window size — falls back to the default if the user clears the
        // box or types non-numeric text. Normalize below clamps it to the
        // [Min, Max] range and Validate raises if somehow out of range.
        int windowWidth = int.TryParse(SpotlightWindowWidthInput.Text, out int wParsed)
            ? wParsed
            : SpotlightTriggerSettings.Default.WindowWidth;
        int windowHeight = int.TryParse(SpotlightWindowHeightInput.Text, out int hParsed)
            ? hParsed
            : SpotlightTriggerSettings.Default.WindowHeight;

        var settings = new SpotlightTriggerSettings
        {
            KeyboardShortcutEnabled = SpotlightKeyboardShortcutToggle.IsChecked == true,
            Modifiers = modifiers,
            Key = SpotlightShortcutKeyComboBox.SelectedItem as string
                ?? SpotlightTriggerSettings.Default.Key,
            WindowWidth = windowWidth,
            WindowHeight = windowHeight,
        }.Normalize();

        try
        {
            settings.Validate();
            SpotlightTriggerSettingsSaved?.Invoke(settings);
        }
        catch (ArgumentException exception)
        {
            SpotlightShortcutStatusText.Text = exception.Message;
            SetFeedbackTone(SpotlightShortcutStatusText, isError: true);
        }
    }

    // ── R54: Clipboard history trigger + feature settings ──

    /// <summary>
    /// Reads the clipboard-history shortcut card, validates, and raises
    /// <see cref="ClipboardHistoryTriggerSettingsSaved"/>. Mirrors
    /// <see cref="OnSaveSpotlightTriggerClick"/>.
    /// </summary>
    private void OnSaveClipboardHistoryTriggerClick(object? sender, RoutedEventArgs e)
    {
        GlobalHotKeyModifiers modifiers = GlobalHotKeyModifiers.None;
        if (ClipboardHistoryCtrlModifierCheckBox.IsChecked == true) modifiers |= GlobalHotKeyModifiers.Control;
        if (ClipboardHistoryAltModifierCheckBox.IsChecked == true) modifiers |= GlobalHotKeyModifiers.Alt;
        if (ClipboardHistoryShiftModifierCheckBox.IsChecked == true) modifiers |= GlobalHotKeyModifiers.Shift;
        if (ClipboardHistoryWinModifierCheckBox.IsChecked == true) modifiers |= GlobalHotKeyModifiers.Windows;

        var settings = new ClipboardHistoryTriggerSettings
        {
            KeyboardShortcutEnabled = ClipboardHistoryKeyboardShortcutToggle.IsChecked == true,
            Modifiers = modifiers,
            Key = ClipboardHistoryShortcutKeyComboBox.SelectedItem as string
                ?? ClipboardHistoryTriggerSettings.Default.Key,
        }.Normalize();

        try
        {
            settings.Validate();
            ClipboardHistoryTriggerSettingsSaved?.Invoke(settings);
        }
        catch (ArgumentException exception)
        {
            ClipboardHistoryShortcutStatusText.Text = exception.Message;
            SetFeedbackTone(ClipboardHistoryShortcutStatusText, isError: true);
        }
    }

    /// <summary>
    /// Reads the clipboard-history feature toggles, validates, and raises
    /// <see cref="ClipboardHistorySettingsSaved"/>.
    /// </summary>
    private void OnSaveClipboardHistorySettingsClick(object? sender, RoutedEventArgs e)
    {
        int maxEntries = int.TryParse(ClipboardHistoryMaxEntriesInput.Text, out int parsed)
            ? parsed
            : ClipboardHistorySettings.Default.MaxEntries;

        // R54 v2: separate (smaller) cap for image entries — falls back to the
        // default if the user clears the box or types non-numeric text. Normalize
        // below clamps it to [5, 500] and Validate raises if somehow out of range.
        int maxImageEntries = int.TryParse(ClipboardHistoryMaxImageEntriesInput.Text, out int imgParsed)
            ? imgParsed
            : ClipboardHistorySettings.Default.MaxImageEntries;

        // R54 window size — same fallback semantics as maxEntries/maxImageEntries.
        int windowWidth = int.TryParse(ClipboardHistoryWindowWidthInput.Text, out int wParsed)
            ? wParsed
            : ClipboardHistorySettings.Default.WindowWidth;
        int windowHeight = int.TryParse(ClipboardHistoryWindowHeightInput.Text, out int hParsed)
            ? hParsed
            : ClipboardHistorySettings.Default.WindowHeight;

        var exclude = (ClipboardHistoryExcludeAppsInput.Text ?? string.Empty)
            .Split(',', '\n', ';')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var settings = new ClipboardHistorySettings
        {
            Enabled = ClipboardHistoryEnabledToggle.IsChecked == true,
            AutoPasteEnabled = ClipboardHistoryAutoPasteToggle.IsChecked == true,
            MaskSensitiveEnabled = ClipboardHistoryMaskSensitiveToggle.IsChecked == true,
            MaxEntries = maxEntries,
            CaptureImagesEnabled = ClipboardHistoryCaptureImagesToggle.IsChecked == true,
            MaxImageEntries = maxImageEntries,
            ExcludeProcessNames = exclude,
            WindowWidth = windowWidth,
            WindowHeight = windowHeight,
        }.Normalize();

        try
        {
            settings.Validate();
            ClipboardHistorySettingsSaved?.Invoke(settings);
            ClipboardHistorySettingsStatusText.Text = Strings.Common_Status_Saved;
            SetFeedbackTone(ClipboardHistorySettingsStatusText, isError: false);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            ClipboardHistorySettingsStatusText.Text = exception.Message;
            SetFeedbackTone(ClipboardHistorySettingsStatusText, isError: true);
        }
    }

    private void OnClearClipboardHistoryClick(object? sender, RoutedEventArgs e) =>
        ClipboardHistoryClearRequested?.Invoke();

    // ── R37: Toolbar built-in shortcut keys (Prompt/Copy/Paste) ──

    /// <summary>
    /// Pushes the current toolbar shortcut bindings into the card's three
    /// single-character TextBoxes. Empty key (disabled) shows as blank.
    /// </summary>
    public void SetToolbarShortcuts(
        ToolbarShortcutSettings settings,
        string? statusMessage = null,
        bool isError = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalize();
        PromptShortcutInput.Text = settings.PromptKey;
        CopyShortcutInput.Text = settings.CopyKey;
        SpeakShortcutInput.Text = settings.SpeakKey;
        ToolbarShortcutsStatusText.Text = statusMessage
            ?? string.Format(Strings.Settings_ToolbarStatusCurrent,
                DisplayKey(settings.PromptKey),
                DisplayKey(settings.CopyKey),
                DisplayKey(settings.SpeakKey));
        SetFeedbackTone(ToolbarShortcutsStatusText, isError);
    }

    /// <summary>
    /// Reads the three TextBoxes, constructs and validates a
    /// <see cref="ToolbarShortcutSettings"/>, and raises
    /// <see cref="ToolbarShortcutsSaved"/>. Validation failures (non-A-Z,
    /// duplicate keys) are shown inline without raising the event.
    /// </summary>
    private void OnSaveToolbarShortcutsClick(object? sender, RoutedEventArgs e)
    {
        var settings = new ToolbarShortcutSettings
        {
            PromptKey = NormalizeInput(PromptShortcutInput.Text),
            CopyKey = NormalizeInput(CopyShortcutInput.Text),
            SpeakKey = NormalizeInput(SpeakShortcutInput.Text),
        }.Normalize();

        try
        {
            settings.Validate();
            ToolbarShortcutsSaved?.Invoke(settings);
        }
        catch (ArgumentException exception)
        {
            ToolbarShortcutsStatusText.Text = exception.Message;
            SetFeedbackTone(ToolbarShortcutsStatusText, isError: true);
        }
    }

    /// <summary>Empty/whitespace → null (disabled); otherwise the trimmed text.</summary>
    private static string? NormalizeInput(string? text)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Display helper: null key shows as "Unbound" so the status line is readable.</summary>
    private static string DisplayKey(string? key) => string.IsNullOrEmpty(key) ? Strings.Settings_Unbound : key;

    private async Task LoadSelectedProviderIntoForm()
    {
        ProviderProfileEntry? entry = GetSelectedProvider();
        if (entry is null)
        {
            NameInput.Text = string.Empty;
            EditingProviderNameHint.Text = string.Empty;
            BaseUrlInput.Text = string.Empty;
            ModelInput.Items.Clear();
            ModelInput.Text = string.Empty;
            ChatPathInput.Text = string.Empty;
            SystemPromptInput.Text = string.Empty;
            TimeoutInput.Value = 60;
            ModelFetchStatus.Text = Strings.Settings_Provider_LastFetched_Never;
            SetFeedbackTone(ModelFetchStatus, isError: false);
            ApiKeyInput.Text = string.Empty;
            ApiKeyStatusText.Text = Strings.Settings_Status_NoProvider;
            SetFeedbackTone(ApiKeyStatusText, isError: true);
            ProviderSaveStatusText.Text = string.Empty;
            SetFeedbackTone(ProviderSaveStatusText, isError: false);
            return;
        }

        NameInput.Text = entry.Name;
        EditingProviderNameHint.Text = $"({entry.Name})";
        BaseUrlInput.Text = entry.BaseUrl;

        // R26: ModelInput is now an editable ComboBox. Repopulate items from
        // the cached fetch (if any) so the dropdown isn't empty on first open,
        // then ensure the configured model is selectable (append if not listed,
        // mirroring the vision combo pattern). Text falls back to DefaultModel.
        RepopulateTranslationModelCombo(entry.Id, entry.DefaultModel);
        ChatPathInput.Text = string.IsNullOrEmpty(entry.ChatCompletionsPath)
            ? "chat/completions"
            : entry.ChatCompletionsPath;
        SystemPromptInput.Text = entry.SystemPrompt ?? string.Empty;
        TimeoutInput.Value = entry.TimeoutSeconds;
        ApiKeyInput.Text = string.Empty;

        bool isDraft = string.Equals(entry.Id, _draftProviderId, StringComparison.OrdinalIgnoreCase);
        ProviderSaveStatusText.Text = isDraft
            ? Strings.Settings_Provider_CustomDraftHint
            : string.Empty;
        SetFeedbackTone(ProviderSaveStatusText, isError: false);

        // Check key status for this provider.
        if (isDraft)
        {
            ApiKeyStatusText.Text = Strings.Settings_Key_NotSet;
            SetFeedbackTone(ApiKeyStatusText, isError: false);
        }
        else if (!string.IsNullOrEmpty(entry.ApiKeyReference) && _hasKeyChecker is not null)
        {
            bool hasKey = await _hasKeyChecker(entry.ApiKeyReference);
            ApiKeyStatusText.Text = hasKey ? Strings.Settings_Key_Saved : Strings.Settings_Key_NotSet;
            SetFeedbackTone(ApiKeyStatusText, isError: !hasKey);
        }
        else
        {
            ApiKeyStatusText.Text = Strings.Settings_Key_NotRequired;
            SetFeedbackTone(ApiKeyStatusText, isError: false);
        }
    }

    private ProviderProfileEntry? GetSelectedProvider()
    {
        if (ProviderComboBox.SelectedItem is ProviderOption option)
        {
            return _providers.FirstOrDefault(p => p.Id == option.Id);
        }
        return null;
    }

    // ── Click handlers ──

    private async void OnProviderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Refresh the edit form (including key status) when the user picks a
        // different provider. SelectionChanged can fire during ItemsSource
        // rebuild with a null SelectedItem; guard against that.
        if (GetSelectedProvider() is not { } selected)
        {
            return;
        }

        // Track which provider the user is now editing, so a later refresh
        // (save/add/key) keeps this one selected instead of snapping to default.
        _editingProviderId = selected.Id;

        await LoadSelectedProviderIntoForm();

        // Bring the edit form into view and focus the first field, so picking a
        // provider "jumps" the user to its config (Base URL / model / chat path).
        EditFormBorder.BringIntoView();
        BaseUrlInput.Focus();
    }

    /// <summary>
    /// Selects a provider in the combo and tracks it as the one being edited,
    /// so the next refresh keeps it selected. Used by the add-provider flow to
    /// land the user on the provider they just created.
    /// </summary>
    public void SelectProviderForEditing(string providerId)
    {
        ShowSettingsPage(SettingsPage.Provider);
        _editingProviderId = providerId;
        ProviderOption? match = _providerOptions.FirstOrDefault(o => o.Id == providerId);
        if (match is not null)
        {
            ProviderComboBox.SelectedIndex = _providerOptions.IndexOf(match);
        }
    }

    private void OnAddProviderClick(object? sender, RoutedEventArgs e)
    {
        // Build a ContextMenu with presets + custom option.
        var menu = new ContextMenu();

        foreach (ProviderPreset preset in ProviderPresets.BuiltIn)
        {
            var item = new MenuItem { Header = preset.Name };
            string presetId = preset.Id;
            item.Click += (_, _) => AddProviderFromPresetRequested?.Invoke(presetId);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var custom = new MenuItem { Header = Strings.Settings_Key_Custom };
        custom.Click += (_, _) => BeginCustomProviderDraft();
        menu.Items.Add(custom);

        menu.Open(this);
    }

    private void BeginCustomProviderDraft()
    {
        // Keep at most one unsaved draft. Repeatedly choosing Custom replaces
        // the untouched draft instead of filling providers.json with several
        // indistinguishable placeholder rows.
        if (_draftProviderId is not null)
        {
            _providers.RemoveAll(p => p.Id == _draftProviderId);
            ProviderOption? oldOption = _providerOptions.FirstOrDefault(o => o.Id == _draftProviderId);
            if (oldOption is not null)
            {
                _providerOptions.Remove(oldOption);
            }
        }

        string id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        string name = BuildUniqueCustomProviderName();
        var draft = new ProviderProfileEntry(
            Id: id,
            Name: name,
            BaseUrl: string.Empty,
            ApiKeyReference: ProviderPresets.BuildSecretReference(id),
            DefaultModel: string.Empty,
            ChatCompletionsPath: "chat/completions",
            TimeoutSeconds: 60,
            MaxSourceCharacters: 8000);

        _draftProviderId = id;
        _editingProviderId = id;
        _providers.Add(draft);
        _providerOptions.Add(new ProviderOption(id, $"{name} · {Strings.Settings_Provider_New}"));

        ShowSettingsPage(SettingsPage.Provider);
        ProviderComboBox.SelectedIndex = _providerOptions.Count - 1;
        EditFormBorder.BringIntoView();
        BaseUrlInput.Focus();
    }

    private string BuildUniqueCustomProviderName()
    {
        string baseName = Strings.Settings_Provider_CustomName;
        HashSet<string> used = _providers
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(baseName))
        {
            return baseName;
        }

        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private void OnToggleVisibilityClick(object? sender, RoutedEventArgs e)
    {
        ApiKeyInput.PasswordChar = ApiKeyInput.PasswordChar == '\0' ? '•' : '\0';
    }

    private void OnSaveProviderClick(object? sender, RoutedEventArgs e)
    {
        ProviderProfileEntry? current = GetSelectedProvider();
        if (current is null)
        {
            return;
        }

        var updated = current with
        {
            Name = NameInput.Text?.Trim() ?? current.Name,
            BaseUrl = BaseUrlInput.Text?.Trim() ?? current.BaseUrl,
            // R26: ModelInput is now an editable ComboBox. SelectedItem wins
            // when the user picked from the list; Text is the fallback for a
            // hand-typed id (e.g. an OpenRouter "provider/model" not yet in
            // the cached list). Mirrors VisionModelComboBox's save read.
            DefaultModel = (ModelInput.SelectedItem as string ?? ModelInput.Text)?.Trim() is { Length: > 0 } picked
                ? picked
                : current.DefaultModel,
            ChatCompletionsPath = string.IsNullOrWhiteSpace(ChatPathInput.Text)
                ? "chat/completions"
                : ChatPathInput.Text.Trim(),
            SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptInput.Text)
                ? null
                : SystemPromptInput.Text,
            // Clamp explicitly — the NumericUpDown's Maximum can be relaxed
            // without notice, and the unchecked (int) cast would silently
            // truncate. Validate() below still asserts the final range.
            TimeoutSeconds = Math.Clamp((int)(TimeoutInput.Value ?? 60), 10, 300),
        };

        // Validate before persisting — every other save handler in this file
        // does the same. Keeps bad input (empty name, empty BaseUrl, out-of-
        // range timeout) from being written to providers.json and propagated
        // to a live provider instance.
        try
        {
            updated.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            ProviderSaveStatusText.Text = ex.Message;
            SetFeedbackTone(ProviderSaveStatusText, isError: true);
            return;
        }

        ProviderSaveStatusText.Text = Strings.Settings_Provider_Saving;
        SetFeedbackTone(ProviderSaveStatusText, isError: false);
        SaveProviderRequested?.Invoke(updated, NormalizeInput(ApiKeyInput.Text));
    }

    private void OnSetActiveClick(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedProvider() is { } entry)
        {
            if (string.Equals(entry.Id, _draftProviderId, StringComparison.OrdinalIgnoreCase))
            {
                ProviderSaveStatusText.Text = Strings.Settings_Provider_SaveBeforeActivate;
                SetFeedbackTone(ProviderSaveStatusText, isError: true);
                return;
            }
            SetActiveProviderRequested?.Invoke(entry.Id);
        }
    }

    private void OnDeleteProviderClick(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedProvider() is { } entry)
        {
            if (string.Equals(entry.Id, _draftProviderId, StringComparison.OrdinalIgnoreCase))
            {
                _providers.RemoveAll(p => p.Id == entry.Id);
                ProviderOption? option = _providerOptions.FirstOrDefault(o => o.Id == entry.Id);
                if (option is not null)
                {
                    _providerOptions.Remove(option);
                }
                _draftProviderId = null;
                _editingProviderId = _currentProviderId;
                ProviderOption? fallback = _currentProviderId is null
                    ? _providerOptions.FirstOrDefault()
                    : _providerOptions.FirstOrDefault(o => o.Id == _currentProviderId)
                        ?? _providerOptions.FirstOrDefault();
                ProviderComboBox.SelectedIndex = fallback is null
                    ? -1
                    : _providerOptions.IndexOf(fallback);
                if (fallback is null)
                {
                    _ = LoadSelectedProviderIntoForm();
                }
                return;
            }
            DeleteProviderRequested?.Invoke(entry.Id);
        }
    }

    /// <summary>Displays the result of the App-layer add/update + key save.</summary>
    public void SetProviderSaveResult(string providerId, bool succeeded, bool keySaveFailed)
    {
        if (!string.Equals(providerId, _editingProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ProviderSaveStatusText.Text = !succeeded
            ? Strings.Settings_Provider_SaveFailed
            : keySaveFailed
                ? Strings.Settings_Provider_SavedKeyFailed
                : Strings.Settings_Provider_Saved;
        SetFeedbackTone(ProviderSaveStatusText, isError: !succeeded || keySaveFailed);
    }

    // ── Existing handlers ──

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }
        Activate();
    }

    /// <summary>
    /// Shows the settings window (if hidden) and scrolls the prompt-templates
    /// card into view. Used by the quick-tools "管理提示词" entry point so the
    /// user lands directly on the editable prompts.
    /// </summary>
    public void ShowAndScrollToPromptTemplates()
    {
        ShowAndActivate();
        ShowSettingsPage(SettingsPage.Functions);
        PromptTemplatesCard.BringIntoView();
    }

    /// <summary>
    /// R32: shows the window on the launcher section (used by Spotlight's
    /// Ctrl+Enter and "⚙ 设置" footer button).
    /// </summary>
    public void ShowAndScrollToLauncher()
    {
        ShowAndActivate();
        ShowSettingsPage(SettingsPage.Launcher);
        LauncherSection.BringIntoView();
    }

    /// <summary>
    /// R32: programmatically opens the launcher editor for a specific entry.
    /// Used by Spotlight's Ctrl+Enter shortcut.
    /// </summary>
    public void RequestLauncherEdit(string entryId)
    {
        OpenLauncherEditor(entryId);
    }

    public void PrepareForShutdown() => _allowClose = true;

    private void OnOpenConfigDirectoryClick(object? sender, RoutedEventArgs e) =>
        OpenConfigDirectoryRequested?.Invoke();

    private void OnOpenLogDirectoryClick(object? sender, RoutedEventArgs e) =>
        OpenLogDirectoryRequested?.Invoke();

    private void OnHideClick(object? sender, RoutedEventArgs e) => Hide();

    /// <summary>
    /// R38: Esc hides settings. Mirrors OnHideClick / the hide button. Safe
    /// because every setting is saved via its own explicit "保存" button (not
    /// on change), so hiding just discards any unsaved edits and re-pushes from
    /// the runtime on next show. Window is created once and reused, so Hide()
    /// is correct — not Close().
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Hide();
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) =>
        ExitRequested?.Invoke();
}
