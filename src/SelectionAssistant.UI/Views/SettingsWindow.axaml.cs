using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SelectionAssistant.Core.Capture;
using SelectionAssistant.Core.Input;
using SelectionAssistant.Core.Launcher;
using SelectionAssistant.Core.Translation;
using SelectionAssistant.Infrastructure.Configuration;
using System.Collections.ObjectModel;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// One row in the provider ComboBox. Public + top-level so Avalonia compiled
/// bindings can resolve <see cref="DisplayLabel" /> at XAML compile time (a
/// private nested record forces reflection bindings, which break NativeAOT).
/// </summary>
public sealed record ProviderOption(string Id, string DisplayLabel);

public partial class SettingsWindow : Window
{
    private enum SettingsPage
    {
        General,
        Provider,
        Functions,
        Vision,
        Launcher,
    }

    private bool _allowClose;

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

        ShowSettingsPage(SettingsPage.General);
    }

    // ── Settings information architecture ──

    private void ShowSettingsPage(SettingsPage page)
    {
        GeneralSection.IsVisible = page == SettingsPage.General;
        ProviderSection.IsVisible = page == SettingsPage.Provider;
        FunctionsSection.IsVisible = page == SettingsPage.Functions;
        VisionSection.IsVisible = page == SettingsPage.Vision;
        LauncherSection.IsVisible = page == SettingsPage.Launcher;

        SetNavigationState(GeneralNavButton, page == SettingsPage.General);
        SetNavigationState(ProviderNavButton, page == SettingsPage.Provider);
        SetNavigationState(FunctionsNavButton, page == SettingsPage.Functions);
        SetNavigationState(VisionNavButton, page == SettingsPage.Vision);
        SetNavigationState(LauncherNavButton, page == SettingsPage.Launcher);

        (PageTitleText.Text, PageSubtitleText.Text) = page switch
        {
            SettingsPage.General =>
                ("General", "Hotkeys, capture behavior, and runtime status."),
            SettingsPage.Provider =>
                ("Translation", "Providers, connection settings, and encrypted API keys."),
            SettingsPage.Functions =>
                ("Actions", "Commands shared by the selection toolbar and Ocean Eyes."),
            SettingsPage.Vision =>
                ("Vision", "OCR model, prompt, and UI Automation strategy."),
            SettingsPage.Launcher =>
                ("Launcher", "Apps and web shortcuts."),
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };

        SettingsContentScroll.Offset = default;
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

    private void OnShowLauncherClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsPage(SettingsPage.Launcher);

    // ── Events wired to the runtime in App.axaml.cs ──

    public event Action? OpenConfigDirectoryRequested;
    public event Action? OpenLogDirectoryRequested;
    public event Action? ExitRequested;

    /// <summary>Request to set the active provider (hot-swap). Arg = provider id.</summary>
    public event Action<string>? SetActiveProviderRequested;

    /// <summary>Request to add a provider from a preset. Arg = preset id.</summary>
    public event Action<string>? AddProviderFromPresetRequested;

    /// <summary>Request to save an edited provider config. Arg = the full entry.</summary>
    public event Action<ProviderProfileEntry>? SaveProviderRequested;

    /// <summary>Request to delete a provider. Arg = provider id.</summary>
    public event Action<string>? DeleteProviderRequested;

    /// <summary>Request to save an API key. Args = (apiKeyReference, keyValue).</summary>
    public event Action<string, string>? ApiKeySaveRequested;

    /// <summary>Request to save a prompt template. Args = (actionId, newPrompt, thinkingEnabled, shortcut).</summary>
    public event Action<string, string, bool, string?>? PromptTemplateSaved;

    /// <summary>Request to reset a prompt template to built-in default. Arg = actionId.</summary>
    public event Action<string>? PromptTemplateReset;

    /// <summary>Request to add a new custom function. Args = (name, prompt, thinkingEnabled, shortcut).</summary>
    public event Action<string, string, bool, string?>? PromptTemplateAdded;

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

    /// <summary>R24 track B: request to save the vision OCR settings.</summary>
    public event Action<VisionCaptureSettings>? VisionSettingsSaved;

    /// <summary>Request to atomically apply and persist Ocean Eyes trigger settings.</summary>
    public event Action<OceanEyesTriggerSettings>? OceanEyesTriggerSettingsSaved;

    /// <summary>
    /// R40: request to apply and persist the Ocean Eyes screenshot/save settings
    /// (path + auto-save / clipboard / UIA-assist toggles).
    /// </summary>
    public event Action<OceanEyesCaptureSettings>? OceanEyesCaptureSettingsSaved;

    /// <summary>R32: request to apply and persist the Spotlight (launcher-search) hotkey.</summary>
    public event Action<SpotlightTriggerSettings>? SpotlightTriggerSettingsSaved;

    /// <summary>
    /// R37: request to apply and persist the toolbar built-in shortcut keys
    /// (Prompt/Copy/Paste, defaults R/C/V). Raised after Validate passes.
    /// </summary>
    public event Action<ToolbarShortcutSettings>? ToolbarShortcutsSaved;

    // ── Data push from runtime → UI ──

    public void Configure(string capturePolicyFile)
    {
        PolicyPathText.Text = capturePolicyFile;
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
        ShortcutStatusText.Text = statusMessage ?? $"Current: {settings.ToDisplayText()}";
        SummaryShortcutText.Text = settings.ToDisplayText();
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
        OceanEyesCaptureStatusText.Text = statusMessage ?? $"Location: {settings.SavePath}";
        SetFeedbackTone(OceanEyesCaptureStatusText, isError);
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
        SpotlightShortcutStatusText.Text = statusMessage ?? $"Current: {settings.ToDisplayText()}";
        SetFeedbackTone(SpotlightShortcutStatusText, isError);
    }

    private static void SetFeedbackTone(TextBlock target, bool isError)
    {
        target.Classes.Remove("FeedbackSuccess");
        target.Classes.Remove("FeedbackError");
        target.Classes.Add(isError ? "FeedbackError" : "FeedbackSuccess");
    }

    /// <summary>
    /// Pushes the current prompt templates into the three preview rows. Called
    /// by App whenever templates change (load, save, reset).
    /// </summary>
    public void SetPromptTemplates(PromptTemplateSet templates)
    {
        _promptTemplates = templates;
        RefreshPromptPreviews();
    }

    private void RefreshPromptPreviews()
    {
        _functionRows.Clear();
        foreach (PromptTemplate t in _promptTemplates.AsList())
        {
            bool isBuiltIn = PromptActionIds.IsBuiltIn(t.Id);
            string fallback = t.Id == PromptActionIds.Translate
                ? "(uses provider default)"
                : "(not set)";
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
        PromptActionIds.Translate => "Translate",
        PromptActionIds.Summarize => "Summarize",
        PromptActionIds.Explain => "Explain",
        _ => template.Name,
    };

    // ── Launcher entry management ──

    /// <summary>
    /// Pushes the current launcher entries into the settings card rows.
    /// Called by App whenever entries change (load, add, save, delete, move).
    /// </summary>
    public void SetLauncherEntries(IReadOnlyList<LauncherEntry> entries) =>
        RefreshLauncherRows(entries);

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
        editor.TemplateSaved += (savedId, newPrompt, thinking, shortcut) =>
            PromptTemplateSaved?.Invoke(savedId, newPrompt, thinking, shortcut);
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
        _providers = [..providers];
        _currentProviderId = currentId;
        _hasKeyChecker = hasKeyChecker;

        ProviderProfileEntry? activeProvider = providers.FirstOrDefault(p => p.Id == currentId);
        SummaryProviderText.Text = activeProvider is null
            ? "No provider selected"
            : $"{activeProvider.Name}\n{activeProvider.DefaultModel}";

        // Rebuild ComboBox options. Keep the label short: just the provider
        // display name (e.g. "DeepSeek"). The model id is visible in the edit
        // form below, so repeating it in the dropdown made the label cramped
        // and redundant.
        _providerOptions.Clear();
        foreach (ProviderProfileEntry p in providers)
        {
            _providerOptions.Add(new ProviderOption(p.Id, p.Name));
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
            : "Disabled";
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
                Title = "Choose Ocean Eyes save folder",
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

        var settings = new SpotlightTriggerSettings
        {
            KeyboardShortcutEnabled = SpotlightKeyboardShortcutToggle.IsChecked == true,
            Modifiers = modifiers,
            Key = SpotlightShortcutKeyComboBox.SelectedItem as string
                ?? SpotlightTriggerSettings.Default.Key,
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
        ToolbarShortcutsStatusText.Text = statusMessage
            ?? $"Current: Prompt {DisplayKey(settings.PromptKey)} · Copy {DisplayKey(settings.CopyKey)}";
        SetFeedbackTone(ToolbarShortcutsStatusText, isError);
    }

    /// <summary>
    /// Reads the two TextBoxes, constructs and validates a
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
    private static string DisplayKey(string? key) => string.IsNullOrEmpty(key) ? "Unbound" : key;

    private async Task LoadSelectedProviderIntoForm()
    {
        ProviderProfileEntry? entry = GetSelectedProvider();
        if (entry is null)
        {
            NameInput.Text = string.Empty;
            EditingProviderNameHint.Text = string.Empty;
            BaseUrlInput.Text = string.Empty;
            ModelInput.Text = string.Empty;
            ApiKeyInput.Text = string.Empty;
            ApiKeyStatusText.Text = "No provider selected";
            SetFeedbackTone(ApiKeyStatusText, isError: true);
            return;
        }

        NameInput.Text = entry.Name;
        EditingProviderNameHint.Text = $"({entry.Name})";
        BaseUrlInput.Text = entry.BaseUrl;
        ModelInput.Text = entry.DefaultModel;
        ChatPathInput.Text = string.IsNullOrEmpty(entry.ChatCompletionsPath)
            ? "chat/completions"
            : entry.ChatCompletionsPath;
        SystemPromptInput.Text = entry.SystemPrompt ?? string.Empty;
        TimeoutInput.Value = entry.TimeoutSeconds;
        ApiKeyInput.Text = string.Empty;

        // Check key status for this provider.
        if (!string.IsNullOrEmpty(entry.ApiKeyReference) && _hasKeyChecker is not null)
        {
            bool hasKey = await _hasKeyChecker(entry.ApiKeyReference);
            ApiKeyStatusText.Text = hasKey ? "Key saved ✓" : "Key not set";
            SetFeedbackTone(ApiKeyStatusText, isError: !hasKey);
        }
        else
        {
            ApiKeyStatusText.Text = "No key required";
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
            var item = new MenuItem { Header = $"{preset.Name} ({preset.DefaultModel})" };
            string presetId = preset.Id;
            item.Click += (_, _) => AddProviderFromPresetRequested?.Invoke(presetId);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var custom = new MenuItem { Header = "Custom…" };
        custom.Click += (_, _) => AddProviderFromPresetRequested?.Invoke(ProviderPresets.CustomPresetId);
        menu.Items.Add(custom);

        menu.Open(this);
    }

    private void OnSaveKeyClick(object? sender, RoutedEventArgs e)
    {
        ProviderProfileEntry? entry = GetSelectedProvider();
        if (entry is null || string.IsNullOrEmpty(entry.ApiKeyReference))
        {
            ApiKeyStatusText.Text = "This provider does not require a key";
            SetFeedbackTone(ApiKeyStatusText, isError: false);
            return;
        }

        string key = ApiKeyInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(key))
        {
            ApiKeyStatusText.Text = "Enter a key first";
            SetFeedbackTone(ApiKeyStatusText, isError: true);
            return;
        }

        ApiKeySaveRequested?.Invoke(entry.ApiKeyReference, key);
        ApiKeyInput.Text = string.Empty;
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
            DefaultModel = ModelInput.Text?.Trim() ?? current.DefaultModel,
            ChatCompletionsPath = string.IsNullOrWhiteSpace(ChatPathInput.Text)
                ? "chat/completions"
                : ChatPathInput.Text.Trim(),
            SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptInput.Text)
                ? null
                : SystemPromptInput.Text,
            TimeoutSeconds = (int)(TimeoutInput.Value ?? 60),
        };
        SaveProviderRequested?.Invoke(updated);
    }

    private void OnSetActiveClick(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedProvider() is { } entry)
        {
            SetActiveProviderRequested?.Invoke(entry.Id);
        }
    }

    private void OnDeleteProviderClick(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedProvider() is { } entry)
        {
            DeleteProviderRequested?.Invoke(entry.Id);
        }
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
