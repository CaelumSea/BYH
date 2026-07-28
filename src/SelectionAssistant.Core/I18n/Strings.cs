namespace SelectionAssistant.Core.I18n;

/// <summary>
/// Single entry point for every user-visible UI string in BYH. Consumed two
/// ways:
/// <list type="bullet">
///   <item>Avalonia AXAML: <c>Text="{x:Static loc:Strings.Toolbar_Translate}"</c>
///       (compiled, AOT/trim-safe).</item>
///   <item>C# code-behind: <c>Strings.Toolbar_Translate</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Design — why a static class, not ResourceManager / .resx / a source
/// generator?</b> BYH publishes with <c>PublishAot=true</c> +
/// <c>TrimMode=full</c>. The default <c>ResourceManager</c> walk over
/// satellite assemblies is reflection-driven and gets aggressively trimmed;
/// rooting it is fiddly. The Everywhere reference project ships a Roslyn
/// source generator that compiles <c>.resx</c> into per-locale
/// <c>ResourceDictionary</c> subclasses — over-engineered for a 2-language
/// MVP. A pair of plain <c>Dictionary&lt;string,string&gt;</c> literals,
/// picked once at startup by <see cref="AppLanguage.Current"/>, is the
/// smallest AOT-safe surface that matches the codebase's hand-rolled-store
/// idiom.
/// <para>
/// <b>Lifecycle.</b> <c>_dict</c> is initialized by the static constructor,
/// which reads <see cref="AppLanguage.Current"/>. <c>App</c> sets
/// <c>AppLanguage.Current</c> <i>before</i> the first window is constructed
/// (and therefore before any <c>Strings</c> member is touched), so the right
/// dictionary is in place by the time XAML resolves the bindings. Switching
/// languages is done by saving <c>ui-language.json</c> and calling
/// <c>RequestRestart()</c> — a fresh process re-runs the static ctor with
/// the new language.
/// </para>
/// <para>
/// <b>Key naming.</b> <c>ViewName_Purpose</c> — e.g. <c>Toolbar_Translate</c>,
/// <c>Spotlight_FooterSettings</c>, <c>Common_Cancel</c>. Common buttons
/// shared across dialogs live under the <c>Common_</c> prefix so they're
/// trivially reusable.
/// </para>
/// <para>
/// <b>Missing-key behavior.</b> <see cref="Get"/> returns the key itself when
/// a translation is absent, so a forgotten string is immediately visible
/// during development (renders as the key) rather than silently blank.
/// </para>
/// </remarks>
public static partial class Strings
{
    private static readonly Dictionary<string, string> _dict = LoadCurrent();

    private static Dictionary<string, string> LoadCurrent() =>
        AppLanguage.Current.IsChinese ? Strings_zh_CN.Build() : Strings_en.Build();

    /// <summary>
    /// Looks up a translated string by key. Returns the key itself if the key
    /// is missing from the active dictionary — surfaces a forgotten string
    /// immediately instead of rendering blank.
    /// </summary>
    public static string Get(string key) =>
        _dict.TryGetValue(key, out string? value) ? value : key;

    /// <summary>
    /// The set of keys present in the English dictionary. Exposed so tests
    /// can verify the English and Chinese dictionaries ship the exact same
    /// key set (a key present in one but not the other is a bug). Property
    /// count is small (~80), so allocating on each call is fine — this is
    /// only ever called from tests.
    /// </summary>
    public static IReadOnlyCollection<string> GetEnglishKeys() =>
        Strings_en.Build().Keys.ToArray();

    /// <summary>See <see cref="GetEnglishKeys"/>. Must match it exactly.</summary>
    public static IReadOnlyCollection<string> GetChineseKeys() =>
        Strings_zh_CN.Build().Keys.ToArray();

    // ── Per-key strongly-typed accessors ──────────────────────────────────
    // Each property below corresponds to one entry in BOTH Strings_en and
    // Strings_zh_CN. The StringsTests suite asserts every key exists in both
    // dictionaries, so a typo'd property name is caught at test time.

    // Common (shared across dialogs)
    public static string Common_Confirm => Get(nameof(Common_Confirm));
    public static string Common_Cancel => Get(nameof(Common_Cancel));
    public static string Common_Save => Get(nameof(Common_Save));
    public static string Common_Close => Get(nameof(Common_Close));
    public static string Common_Delete => Get(nameof(Common_Delete));
    public static string Common_Retry => Get(nameof(Common_Retry));
    public static string Common_Run => Get(nameof(Common_Run));
    public static string Common_Copy => Get(nameof(Common_Copy));

    // ToolbarWindow
    public static string Toolbar_Translate => Get(nameof(Toolbar_Translate));
    public static string Toolbar_Explain => Get(nameof(Toolbar_Explain));
    public static string Toolbar_Summarize => Get(nameof(Toolbar_Summarize));
    public static string Toolbar_Prompt => Get(nameof(Toolbar_Prompt));
    public static string Toolbar_Copy => Get(nameof(Toolbar_Copy));
    public static string Toolbar_StatusWaiting => Get(nameof(Toolbar_StatusWaiting));
    public static string Toolbar_StatusCapturing => Get(nameof(Toolbar_StatusCapturing));
    public static string Toolbar_StatusCaptured => Get(nameof(Toolbar_StatusCaptured));
    public static string Toolbar_StatusNeedManualCopy => Get(nameof(Toolbar_StatusNeedManualCopy));
    public static string Toolbar_StatusEmpty => Get(nameof(Toolbar_StatusEmpty));
    public static string Toolbar_PromptTooltip => Get(nameof(Toolbar_PromptTooltip));
    public static string Toolbar_CopyTooltip => Get(nameof(Toolbar_CopyTooltip));

    // ResultWindow
    public static string Result_Title => Get(nameof(Result_Title));
    public static string Result_WindowTitle => Get(nameof(Result_WindowTitle));
    public static string Result_DefaultLanguagePair => Get(nameof(Result_DefaultLanguagePair));
    public static string Result_DefaultProvider => Get(nameof(Result_DefaultProvider));
    public static string Result_SourceLabel => Get(nameof(Result_SourceLabel));
    public static string Result_Loading => Get(nameof(Result_Loading));
    public static string Result_EmptyResult => Get(nameof(Result_EmptyResult));
    public static string Result_PrivacyTestMode => Get(nameof(Result_PrivacyTestMode));
    public static string Result_CopySource => Get(nameof(Result_CopySource));
    public static string Result_CopyTranslation => Get(nameof(Result_CopyTranslation));
    public static string Result_Replace => Get(nameof(Result_Replace));
    public static string Result_CopiedTranslation => Get(nameof(Result_CopiedTranslation));
    public static string Result_CopiedSource => Get(nameof(Result_CopiedSource));
    public static string Result_ClipboardAccessError => Get(nameof(Result_ClipboardAccessError));
    public static string Result_LangChinese => Get(nameof(Result_LangChinese));
    public static string Result_LangEnglish => Get(nameof(Result_LangEnglish));

    // ResultWindow — action-aware wording. {0} = action display name
    // (翻译/解释/总结/自定义). Used when TranslationRequest.ActionDisplayName
    // is set; the legacy Result_* keys above remain the fallback for the
    // ad-hoc "Prompt Now" path which has no action name.
    public static string Result_WindowTitleForAction => Get(nameof(Result_WindowTitleForAction));
    public static string Result_TitleForAction => Get(nameof(Result_TitleForAction));
    public static string Result_LoadingForAction => Get(nameof(Result_LoadingForAction));
    public static string Result_EmptyResultForAction => Get(nameof(Result_EmptyResultForAction));
    public static string Result_CopyResultForAction => Get(nameof(Result_CopyResultForAction));
    public static string Result_CopiedResultForAction => Get(nameof(Result_CopiedResultForAction));

    // SpotlightWindow
    public static string Spotlight_Title => Get(nameof(Spotlight_Title));
    public static string Spotlight_WindowTitle => Get(nameof(Spotlight_WindowTitle));
    public static string Spotlight_SearchPlaceholder => Get(nameof(Spotlight_SearchPlaceholder));
    public static string Spotlight_CategoryLauncher => Get(nameof(Spotlight_CategoryLauncher));
    public static string Spotlight_FooterSettings => Get(nameof(Spotlight_FooterSettings));
    public static string Spotlight_FooterSelect => Get(nameof(Spotlight_FooterSelect));
    public static string Spotlight_FooterLaunch => Get(nameof(Spotlight_FooterLaunch));
    public static string Spotlight_FooterEdit => Get(nameof(Spotlight_FooterEdit));
    public static string Spotlight_FooterClose => Get(nameof(Spotlight_FooterClose));

    // PromptWindow
    public static string Prompt_Title => Get(nameof(Prompt_Title));
    public static string Prompt_Heading => Get(nameof(Prompt_Heading));
    public static string Prompt_DefaultPreview => Get(nameof(Prompt_DefaultPreview));
    public static string Prompt_Placeholder => Get(nameof(Prompt_Placeholder));
    public static string Prompt_FooterHint => Get(nameof(Prompt_FooterHint));
    public static string Prompt_SelectionPrefix => Get(nameof(Prompt_SelectionPrefix));
    public static string Prompt_NoSelection => Get(nameof(Prompt_NoSelection));

    // GalleryWindow
    public static string Gallery_Title => Get(nameof(Gallery_Title));
    public static string Gallery_Heading => Get(nameof(Gallery_Heading));
    public static string Gallery_Hint => Get(nameof(Gallery_Hint));
    public static string Gallery_CountSuffix => Get(nameof(Gallery_CountSuffix));
    public static string Gallery_CopiedSuffix => Get(nameof(Gallery_CopiedSuffix));
    public static string Gallery_EmptyTitle => Get(nameof(Gallery_EmptyTitle));
    public static string Gallery_CtxCopy => Get(nameof(Gallery_CtxCopy));
    public static string Gallery_CtxPreview => Get(nameof(Gallery_CtxPreview));
    public static string Gallery_CtxDelete => Get(nameof(Gallery_CtxDelete));
    public static string Gallery_CtxReveal => Get(nameof(Gallery_CtxReveal));
    public static string Gallery_DeleteConfirmPrompt => Get(nameof(Gallery_DeleteConfirmPrompt));
    public static string Gallery_DeleteConfirmButton => Get(nameof(Gallery_DeleteConfirmButton));
    public static string Gallery_Today => Get(nameof(Gallery_Today));
    public static string Gallery_Yesterday => Get(nameof(Gallery_Yesterday));
    public static string Gallery_PreviewCloseHint => Get(nameof(Gallery_PreviewCloseHint));
    public static string Gallery_PreviewCopy => Get(nameof(Gallery_PreviewCopy));
    public static string Gallery_PreviewDelete => Get(nameof(Gallery_PreviewDelete));
    public static string Gallery_PreviewReveal => Get(nameof(Gallery_PreviewReveal));

    // ParameterInputDialog
    public static string ParamDialog_Title => Get(nameof(ParamDialog_Title));
    public static string ParamDialog_DefaultPrompt => Get(nameof(ParamDialog_DefaultPrompt));

    // SettingsWindow — Language card (the only new card we add; the rest of
    // SettingsWindow is already English and stays as literal text for now)
    public static string Settings_LanguageCard_Title => Get(nameof(Settings_LanguageCard_Title));
    public static string Settings_LanguageCard_Subtitle => Get(nameof(Settings_LanguageCard_Subtitle));
    public static string Settings_LanguageCard_SaveButton => Get(nameof(Settings_LanguageCard_SaveButton));
    public static string Settings_LanguageCard_StatusCurrent => Get(nameof(Settings_LanguageCard_StatusCurrent));
    public static string Settings_LanguageCard_StatusSaved => Get(nameof(Settings_LanguageCard_StatusSaved));
    public static string Settings_LanguageName_English => Get(nameof(Settings_LanguageName_English));
    public static string Settings_LanguageName_Chinese => Get(nameof(Settings_LanguageName_Chinese));

    // ── Second i18n pass: full app coverage ───────────────────────────────

    // Common — shared across many views (added in second pass).
    // NOTE: Common_Confirm / Common_Cancel / Common_Save / Common_Close /
    // Common_Delete / Common_Retry / Common_Run / Common_Copy already exist
    // from the first pass; only the NEW Common_* keys are declared here.
    public static string Common_Cut => Get(nameof(Common_Cut));
    public static string Common_Paste => Get(nameof(Common_Paste));
    public static string Common_SelectAll => Get(nameof(Common_SelectAll));
    public static string Common_Edit => Get(nameof(Common_Edit));
    public static string Common_Browse => Get(nameof(Common_Browse));
    public static string Common_Width => Get(nameof(Common_Width));
    public static string Common_Height => Get(nameof(Common_Height));
    public static string Common_Placeholder_None => Get(nameof(Common_Placeholder_None));
    public static string Common_Status_Saved => Get(nameof(Common_Status_Saved));
    public static string Common_Toggle_On => Get(nameof(Common_Toggle_On));
    public static string Common_Toggle_Off => Get(nameof(Common_Toggle_Off));
    // Relative-time labels (ClipboardHistoryWindow row age, image popup header)
    public static string Common_TimeJustNow => Get(nameof(Common_TimeJustNow));
    public static string Common_TimeMinutesAgo => Get(nameof(Common_TimeMinutesAgo));
    public static string Common_TimeDaysAgo => Get(nameof(Common_TimeDaysAgo));

    // SettingsWindow — window chrome / nav rail
    public static string Settings_Title => Get(nameof(Settings_Title));
    public static string Settings_Nav_General => Get(nameof(Settings_Nav_General));
    public static string Settings_Nav_Provider => Get(nameof(Settings_Nav_Provider));
    public static string Settings_Nav_Functions => Get(nameof(Settings_Nav_Functions));
    public static string Settings_Nav_Vision => Get(nameof(Settings_Nav_Vision));
    public static string Settings_Nav_Launcher => Get(nameof(Settings_Nav_Launcher));
    public static string Settings_Nav_Clipboard => Get(nameof(Settings_Nav_Clipboard));
    public static string Settings_Nav_Dashboard => Get(nameof(Settings_Nav_Dashboard));

    // SettingsWindow — page headers / subtitles (set from code-behind)
    public static string Settings_Header_Kicker => Get(nameof(Settings_Header_Kicker));
    public static string Settings_PageTitle_General => Get(nameof(Settings_PageTitle_General));
    public static string Settings_PageTitle_Provider => Get(nameof(Settings_PageTitle_Provider));
    public static string Settings_PageTitle_Functions => Get(nameof(Settings_PageTitle_Functions));
    public static string Settings_PageTitle_Vision => Get(nameof(Settings_PageTitle_Vision));
    public static string Settings_PageTitle_Launcher => Get(nameof(Settings_PageTitle_Launcher));
    public static string Settings_PageTitle_Clipboard => Get(nameof(Settings_PageTitle_Clipboard));
    public static string Settings_PageTitle_Dashboard => Get(nameof(Settings_PageTitle_Dashboard));
    public static string Settings_PageSubtitle_General => Get(nameof(Settings_PageSubtitle_General));
    public static string Settings_PageSubtitle_Provider => Get(nameof(Settings_PageSubtitle_Provider));
    public static string Settings_PageSubtitle_Functions => Get(nameof(Settings_PageSubtitle_Functions));
    public static string Settings_PageSubtitle_Vision => Get(nameof(Settings_PageSubtitle_Vision));
    public static string Settings_PageSubtitle_Launcher => Get(nameof(Settings_PageSubtitle_Launcher));
    public static string Settings_PageSubtitle_Clipboard => Get(nameof(Settings_PageSubtitle_Clipboard));
    public static string Settings_PageSubtitle_Dashboard => Get(nameof(Settings_PageSubtitle_Dashboard));

    // SettingsWindow — Ocean Eyes Trigger card
    public static string Settings_OceanEyesTrigger_Title => Get(nameof(Settings_OceanEyesTrigger_Title));
    public static string Settings_OceanEyesTrigger_Hint => Get(nameof(Settings_OceanEyesTrigger_Hint));
    public static string Settings_Hotkey => Get(nameof(Settings_Hotkey));
    public static string Settings_Mod_Ctrl => Get(nameof(Settings_Mod_Ctrl));
    public static string Settings_Mod_Alt => Get(nameof(Settings_Mod_Alt));
    public static string Settings_Mod_Shift => Get(nameof(Settings_Mod_Shift));
    public static string Settings_Mod_Win => Get(nameof(Settings_Mod_Win));
    public static string Settings_SaveHotkey => Get(nameof(Settings_SaveHotkey));
    public static string Settings_MouseChord_Title => Get(nameof(Settings_MouseChord_Title));
    public static string Settings_MouseChord_Hint => Get(nameof(Settings_MouseChord_Hint));

    // SettingsWindow — Toolbar Shortcuts card
    public static string Settings_ToolbarShortcuts_Title => Get(nameof(Settings_ToolbarShortcuts_Title));
    public static string Settings_ToolbarShortcut_Prompt => Get(nameof(Settings_ToolbarShortcut_Prompt));
    public static string Settings_ToolbarShortcut_Prompt_Hint => Get(nameof(Settings_ToolbarShortcut_Prompt_Hint));
    public static string Settings_ToolbarShortcut_Copy => Get(nameof(Settings_ToolbarShortcut_Copy));
    public static string Settings_ToolbarShortcut_Copy_Hint => Get(nameof(Settings_ToolbarShortcut_Copy_Hint));
    public static string Settings_SaveShortcuts => Get(nameof(Settings_SaveShortcuts));

    // SettingsWindow — Ocean Eyes Capture card
    public static string Settings_OceanEyesCapture_Title => Get(nameof(Settings_OceanEyesCapture_Title));
    public static string Settings_SaveLocation => Get(nameof(Settings_SaveLocation));
    public static string Settings_OceanEyesSavePath_Placeholder => Get(nameof(Settings_OceanEyesSavePath_Placeholder));
    public static string Settings_AutoSave => Get(nameof(Settings_AutoSave));
    public static string Settings_ClipboardToggle => Get(nameof(Settings_ClipboardToggle));
    public static string Settings_UiaSnap => Get(nameof(Settings_UiaSnap));
    public static string Settings_SaveCapture => Get(nameof(Settings_SaveCapture));

    // SettingsWindow — Provider section
    public static string Settings_ProviderProfiles_Title => Get(nameof(Settings_ProviderProfiles_Title));
    public static string Settings_AddProvider => Get(nameof(Settings_AddProvider));
    public static string Settings_Editing => Get(nameof(Settings_Editing));
    public static string Settings_Provider_Name => Get(nameof(Settings_Provider_Name));
    public static string Settings_Provider_Model => Get(nameof(Settings_Provider_Model));
    public static string Settings_Provider_BaseUrl => Get(nameof(Settings_Provider_BaseUrl));
    public static string Settings_Provider_ChatPath => Get(nameof(Settings_Provider_ChatPath));
    public static string Settings_Provider_SystemPrompt => Get(nameof(Settings_Provider_SystemPrompt));
    public static string Settings_Provider_ApiKey => Get(nameof(Settings_Provider_ApiKey));
    public static string Settings_Provider_ApiKey_Hint => Get(nameof(Settings_Provider_ApiKey_Hint));
    public static string Settings_Show => Get(nameof(Settings_Show));
    public static string Settings_SaveKey => Get(nameof(Settings_SaveKey));
    public static string Settings_Provider_Timeout => Get(nameof(Settings_Provider_Timeout));
    public static string Settings_SetActive => Get(nameof(Settings_SetActive));
    public static string Settings_SaveProfile => Get(nameof(Settings_SaveProfile));
    // R26: "Refresh Models" button + status line, shown next to the Model
    // dropdown on the Provider and Vision pages. {0} = error text or minutes.
    public static string Settings_Provider_FetchModels => Get(nameof(Settings_Provider_FetchModels));
    public static string Settings_Provider_FetchingModels => Get(nameof(Settings_Provider_FetchingModels));
    public static string Settings_Provider_FetchFailed => Get(nameof(Settings_Provider_FetchFailed));
    public static string Settings_Provider_LastFetched => Get(nameof(Settings_Provider_LastFetched));
    public static string Settings_Provider_LastFetched_Never => Get(nameof(Settings_Provider_LastFetched_Never));

    // SettingsWindow — Functions section
    public static string Settings_Actions_Title => Get(nameof(Settings_Actions_Title));
    public static string Settings_AddAction => Get(nameof(Settings_AddAction));
    // Built-in action display names + prompt-preview fallbacks (code-behind)
    public static string Settings_ActionName_Translate => Get(nameof(Settings_ActionName_Translate));
    public static string Settings_ActionName_Summarize => Get(nameof(Settings_ActionName_Summarize));
    public static string Settings_ActionName_Explain => Get(nameof(Settings_ActionName_Explain));
    public static string Settings_ProviderPromptDefault => Get(nameof(Settings_ProviderPromptDefault));
    public static string Settings_ProviderPromptNotSet => Get(nameof(Settings_ProviderPromptNotSet));

    // SettingsWindow — Vision section
    public static string Settings_Vision_Title => Get(nameof(Settings_Vision_Title));
    public static string Settings_Vision_OcrModel => Get(nameof(Settings_Vision_OcrModel));
    public static string Settings_Vision_Provider => Get(nameof(Settings_Vision_Provider));
    public static string Settings_Vision_Model => Get(nameof(Settings_Vision_Model));
    public static string Settings_Vision_Prompt => Get(nameof(Settings_Vision_Prompt));
    public static string Settings_Vision_Prompt_Placeholder => Get(nameof(Settings_Vision_Prompt_Placeholder));
    public static string Settings_Vision_Strategy => Get(nameof(Settings_Vision_Strategy));
    public static string Settings_Vision_UiaPrefill => Get(nameof(Settings_Vision_UiaPrefill));
    public static string Settings_Vision_DisableThinking => Get(nameof(Settings_Vision_DisableThinking));
    public static string Settings_Vision_DisableThinking_Hint => Get(nameof(Settings_Vision_DisableThinking_Hint));
    public static string Settings_Vision_Thinking_Disabled => Get(nameof(Settings_Vision_Thinking_Disabled));
    public static string Settings_Vision_Thinking_Allowed => Get(nameof(Settings_Vision_Thinking_Allowed));
    public static string Settings_SaveVision => Get(nameof(Settings_SaveVision));

    // SettingsWindow — Launcher section
    public static string Settings_Launcher_Title => Get(nameof(Settings_Launcher_Title));
    public static string Settings_ScanInstalledApps => Get(nameof(Settings_ScanInstalledApps));
    public static string Settings_AddLauncher => Get(nameof(Settings_AddLauncher));
    public static string Settings_Spotlight_Title => Get(nameof(Settings_Spotlight_Title));
    public static string Settings_Spotlight_WindowSize_Title => Get(nameof(Settings_Spotlight_WindowSize_Title));

    // SettingsWindow — Clipboard section
    public static string Settings_Clipboard_Title => Get(nameof(Settings_Clipboard_Title));
    public static string Settings_Clipboard_Subtitle => Get(nameof(Settings_Clipboard_Subtitle));
    public static string Settings_Clipboard_On => Get(nameof(Settings_Clipboard_On));
    public static string Settings_Clipboard_Off => Get(nameof(Settings_Clipboard_Off));
    public static string Settings_Clipboard_GlobalHotkey => Get(nameof(Settings_Clipboard_GlobalHotkey));
    public static string Settings_Clipboard_BehaviorTitle => Get(nameof(Settings_Clipboard_BehaviorTitle));
    public static string Settings_SaveSettings => Get(nameof(Settings_SaveSettings));
    public static string Settings_ClearHistory => Get(nameof(Settings_ClearHistory));
    public static string Settings_Clipboard_AutoPasteOn => Get(nameof(Settings_Clipboard_AutoPasteOn));
    public static string Settings_Clipboard_AutoPasteOff => Get(nameof(Settings_Clipboard_AutoPasteOff));
    public static string Settings_Clipboard_MaskOn => Get(nameof(Settings_Clipboard_MaskOn));
    public static string Settings_Clipboard_MaskOff => Get(nameof(Settings_Clipboard_MaskOff));
    public static string Settings_Clipboard_MaxEntries => Get(nameof(Settings_Clipboard_MaxEntries));
    public static string Settings_Clipboard_ImagesOn => Get(nameof(Settings_Clipboard_ImagesOn));
    public static string Settings_Clipboard_ImagesOff => Get(nameof(Settings_Clipboard_ImagesOff));
    public static string Settings_Clipboard_MaxImages => Get(nameof(Settings_Clipboard_MaxImages));
    public static string Settings_Clipboard_Excluded => Get(nameof(Settings_Clipboard_Excluded));
    public static string Settings_Clipboard_WindowSize_Title => Get(nameof(Settings_Clipboard_WindowSize_Title));

    // SettingsWindow — Phone summary / system overview / window controls
    public static string Settings_Phone_Kicker => Get(nameof(Settings_Phone_Kicker));
    public static string Settings_Phone_Tagline => Get(nameof(Settings_Phone_Tagline));
    public static string Settings_Phone_CurrentSetup => Get(nameof(Settings_Phone_CurrentSetup));
    public static string Settings_Phone_ProviderLabel => Get(nameof(Settings_Phone_ProviderLabel));
    public static string Settings_Phone_OceanEyesLabel => Get(nameof(Settings_Phone_OceanEyesLabel));
    public static string Settings_Phone_VisionLabel => Get(nameof(Settings_Phone_VisionLabel));
    public static string Settings_Phone_ClipboardLabel => Get(nameof(Settings_Phone_ClipboardLabel));
    public static string Settings_Loading => Get(nameof(Settings_Loading));
    public static string Settings_Overview_Title => Get(nameof(Settings_Overview_Title));
    public static string Settings_Overview_Runtime => Get(nameof(Settings_Overview_Runtime));
    public static string Settings_Overview_CaptureActive => Get(nameof(Settings_Overview_CaptureActive));
    public static string Settings_Overview_Theme => Get(nameof(Settings_Overview_Theme));
    public static string Settings_Overview_ThemeValue => Get(nameof(Settings_Overview_ThemeValue));
    public static string Settings_Overview_Diagnostics => Get(nameof(Settings_Overview_Diagnostics));
    public static string Settings_OpenConfigFolder => Get(nameof(Settings_OpenConfigFolder));
    public static string Settings_OpenLogFolder => Get(nameof(Settings_OpenLogFolder));
    public static string Settings_WindowControls_Title => Get(nameof(Settings_WindowControls_Title));
    public static string Settings_WindowControls_Hint => Get(nameof(Settings_WindowControls_Hint));
    public static string Settings_Hide => Get(nameof(Settings_Hide));
    public static string Settings_Exit => Get(nameof(Settings_Exit));

    // SettingsWindow.axaml.cs — status messages & dynamic strings
    public static string Settings_Status_CurrentPrefix => Get(nameof(Settings_Status_CurrentPrefix));
    public static string Settings_Status_LocationPrefix => Get(nameof(Settings_Status_LocationPrefix));
    public static string Settings_Status_NoProvider => Get(nameof(Settings_Status_NoProvider));
    public static string Settings_Status_Disabled => Get(nameof(Settings_Status_Disabled));
    public static string Settings_Picker_SaveFolder => Get(nameof(Settings_Picker_SaveFolder));
    public static string Settings_Key_Saved => Get(nameof(Settings_Key_Saved));
    public static string Settings_Key_NotSet => Get(nameof(Settings_Key_NotSet));
    public static string Settings_Key_NotRequired => Get(nameof(Settings_Key_NotRequired));
    public static string Settings_Key_Custom => Get(nameof(Settings_Key_Custom));
    public static string Settings_Key_NotRequired_Long => Get(nameof(Settings_Key_NotRequired_Long));
    public static string Settings_Key_EnterFirst => Get(nameof(Settings_Key_EnterFirst));
    public static string Settings_Status_HotkeyDisabled => Get(nameof(Settings_Status_HotkeyDisabled));
    public static string Settings_Status_SpotlightDisabled => Get(nameof(Settings_Status_SpotlightDisabled));
    public static string Settings_Status_ClipboardDisabled => Get(nameof(Settings_Status_ClipboardDisabled));
    public static string Settings_Status_Registered => Get(nameof(Settings_Status_Registered));
    public static string Settings_Status_Saved => Get(nameof(Settings_Status_Saved));
    public static string Settings_Status_MouseChordOn => Get(nameof(Settings_Status_MouseChordOn));
    public static string Settings_Status_MouseChordOff => Get(nameof(Settings_Status_MouseChordOff));
    public static string Settings_Status_ToolbarShortcuts => Get(nameof(Settings_Status_ToolbarShortcuts));
    public static string Settings_ToolbarStatusCurrent => Get(nameof(Settings_ToolbarStatusCurrent));
    public static string Settings_Unbound => Get(nameof(Settings_Unbound));
    public static string Settings_Launcher_NoNewApps => Get(nameof(Settings_Launcher_NoNewApps));
    public static string Settings_Launcher_Imported => Get(nameof(Settings_Launcher_Imported));
    public static string Settings_Launcher_NoneImported => Get(nameof(Settings_Launcher_NoneImported));

    // App.axaml.cs — tray menu
    public static string Tray_OpenSettings => Get(nameof(Tray_OpenSettings));
    public static string Tray_OpenConfig => Get(nameof(Tray_OpenConfig));
    public static string Tray_OpenGallery => Get(nameof(Tray_OpenGallery));
    public static string Tray_Restart => Get(nameof(Tray_Restart));
    public static string Tray_Exit => Get(nameof(Tray_Exit));

    // ClipboardHistoryWindow — axaml + code-behind
    public static string Clip_Title => Get(nameof(Clip_Title));
    public static string Clip_TabsKicker => Get(nameof(Clip_TabsKicker));
    public static string Clip_FooterSettings => Get(nameof(Clip_FooterSettings));
    public static string Clip_SearchPlaceholder => Get(nameof(Clip_SearchPlaceholder));
    public static string Clip_CategoryDefault => Get(nameof(Clip_CategoryDefault));
    public static string Clip_ImageLabel => Get(nameof(Clip_ImageLabel));
    public static string Clip_ArchivedBadge => Get(nameof(Clip_ArchivedBadge));
    public static string Clip_FooterSelect => Get(nameof(Clip_FooterSelect));
    public static string Clip_FooterPaste => Get(nameof(Clip_FooterPaste));
    public static string Clip_FooterMenu => Get(nameof(Clip_FooterMenu));
    public static string Clip_FooterClose => Get(nameof(Clip_FooterClose));
    // Nav tab labels (built-in)
    public static string Clip_Tab_All => Get(nameof(Clip_Tab_All));
    public static string Clip_Tab_Links => Get(nameof(Clip_Tab_Links));
    public static string Clip_Tab_Code => Get(nameof(Clip_Tab_Code));
    public static string Clip_Tab_Commands => Get(nameof(Clip_Tab_Commands));
    public static string Clip_Tab_Sensitive => Get(nameof(Clip_Tab_Sensitive));
    public static string Clip_Tab_Images => Get(nameof(Clip_Tab_Images));
    public static string Clip_Tab_Favorites => Get(nameof(Clip_Tab_Favorites));
    public static string Clip_NewTab => Get(nameof(Clip_NewTab));
    // Group badges
    public static string Clip_Group_Sensitive => Get(nameof(Clip_Group_Sensitive));
    public static string Clip_Group_Link => Get(nameof(Clip_Group_Link));
    public static string Clip_Group_Json => Get(nameof(Clip_Group_Json));
    public static string Clip_Group_Code => Get(nameof(Clip_Group_Code));
    public static string Clip_Group_Command => Get(nameof(Clip_Group_Command));
    public static string Clip_Group_Number => Get(nameof(Clip_Group_Number));
    // Move-to submenu labels (badge variants above lack the emoji prefix; the
    // submenu shows the emoji to match the nav-tab visuals)
    public static string Clip_Group_Auto => Get(nameof(Clip_Group_Auto));
    public static string Clip_Group_LinkMenu => Get(nameof(Clip_Group_LinkMenu));
    public static string Clip_Group_CodeMenu => Get(nameof(Clip_Group_CodeMenu));
    public static string Clip_Group_CommandMenu => Get(nameof(Clip_Group_CommandMenu));
    public static string Clip_Group_SensitiveMenu => Get(nameof(Clip_Group_SensitiveMenu));
    // Category header counts
    public static string Clip_CategoryCount => Get(nameof(Clip_CategoryCount));
    // Batch 123: footer hint shown when incremental rendering still has
    // un-materialized rows. {0} = remaining count.
    public static string Clip_LoadMore_Remaining => Get(nameof(Clip_LoadMore_Remaining));
    public static string Clip_CategoryHeader_Links => Get(nameof(Clip_CategoryHeader_Links));
    public static string Clip_CategoryHeader_Code => Get(nameof(Clip_CategoryHeader_Code));
    public static string Clip_CategoryHeader_Commands => Get(nameof(Clip_CategoryHeader_Commands));
    public static string Clip_CategoryHeader_Sensitive => Get(nameof(Clip_CategoryHeader_Sensitive));
    public static string Clip_CategoryHeader_Images => Get(nameof(Clip_CategoryHeader_Images));
    public static string Clip_CategoryHeader_Favorites => Get(nameof(Clip_CategoryHeader_Favorites));
    // Icon picker
    public static string Clip_IconPicker_MyIcons => Get(nameof(Clip_IconPicker_MyIcons));
    public static string Clip_IconPicker_Import => Get(nameof(Clip_IconPicker_Import));
    public static string Clip_IconPicker_ImportHint => Get(nameof(Clip_IconPicker_ImportHint));
    public static string Clip_IconPicker_Emoji => Get(nameof(Clip_IconPicker_Emoji));
    public static string Clip_IconPicker_None => Get(nameof(Clip_IconPicker_None));
    public static string Clip_IconPicker_ChooseFor => Get(nameof(Clip_IconPicker_ChooseFor));
    public static string Clip_IconPicker_ImportTitle => Get(nameof(Clip_IconPicker_ImportTitle));
    public static string Clip_IconPicker_Delete => Get(nameof(Clip_IconPicker_Delete));
    // Tag management
    public static string Clip_RenameTo => Get(nameof(Clip_RenameTo));
    public static string Clip_NewTag => Get(nameof(Clip_NewTag));
    public static string Clip_TagNamePlaceholder => Get(nameof(Clip_TagNamePlaceholder));
    public static string Clip_Error_NameEmpty => Get(nameof(Clip_Error_NameEmpty));
    public static string Clip_Error_NameExists => Get(nameof(Clip_Error_NameExists));
    public static string Clip_IconOptional => Get(nameof(Clip_IconOptional));
    public static string Clip_AddTag => Get(nameof(Clip_AddTag));
    // Row context menu
    public static string Clip_Row_CopyPaste => Get(nameof(Clip_Row_CopyPaste));
    public static string Clip_Row_CopyKeepOpen => Get(nameof(Clip_Row_CopyKeepOpen));
    public static string Clip_Row_Pin => Get(nameof(Clip_Row_Pin));
    public static string Clip_Row_Unpin => Get(nameof(Clip_Row_Unpin));
    public static string Clip_Row_Favorite => Get(nameof(Clip_Row_Favorite));
    public static string Clip_Row_Unfavorite => Get(nameof(Clip_Row_Unfavorite));
    public static string Clip_Row_ViewFull => Get(nameof(Clip_Row_ViewFull));
    // Tag navigation tooltip and image popup header (parameterized, code-behind)
    public static string Clip_TagNavTooltip => Get(nameof(Clip_TagNavTooltip));
    public static string Clip_ImagePopupHeader => Get(nameof(Clip_ImagePopupHeader));
    public static string Clip_Row_AddTag => Get(nameof(Clip_Row_AddTag));
    public static string Clip_Row_RemoveTag => Get(nameof(Clip_Row_RemoveTag));
    public static string Clip_Row_ViewImage => Get(nameof(Clip_Row_ViewImage));
    public static string Clip_Row_MoveTo => Get(nameof(Clip_Row_MoveTo));
    public static string Clip_Row_NoTabsHint => Get(nameof(Clip_Row_NoTabsHint));
    public static string Clip_Row_NewTabForEntry => Get(nameof(Clip_Row_NewTabForEntry));
    public static string Clip_Row_HidePlain => Get(nameof(Clip_Row_HidePlain));
    public static string Clip_Row_RevealPlain => Get(nameof(Clip_Row_RevealPlain));
    public static string Clip_Row_ClearOlder => Get(nameof(Clip_Row_ClearOlder));
    public static string Clip_Tag_Rename => Get(nameof(Clip_Tag_Rename));
    public static string Clip_Tag_SetIcon => Get(nameof(Clip_Tag_SetIcon));
    public static string Clip_Tag_ClearIcon => Get(nameof(Clip_Tag_ClearIcon));
    public static string Clip_Tag_Delete => Get(nameof(Clip_Tag_Delete));
    // Clear older dialog
    public static string Clip_ClearNone_Kept => Get(nameof(Clip_ClearNone_Kept));
    public static string Clip_ClearNone_Empty => Get(nameof(Clip_ClearNone_Empty));
    public static string Clip_ClearConfirm => Get(nameof(Clip_ClearConfirm));
    public static string Clip_ClearConfirmButton => Get(nameof(Clip_ClearConfirmButton));

    // LauncherEntryEditWindow
    public static string LauncherEdit_Title => Get(nameof(LauncherEdit_Title));
    public static string LauncherEdit_TitleNew => Get(nameof(LauncherEdit_TitleNew));
    public static string LauncherEdit_Heading => Get(nameof(LauncherEdit_Heading));
    public static string LauncherEdit_HeadingNew => Get(nameof(LauncherEdit_HeadingNew));
    public static string LauncherEdit_HeadingEditName => Get(nameof(LauncherEdit_HeadingEditName));
    public static string LauncherEdit_Subtitle => Get(nameof(LauncherEdit_Subtitle));
    public static string LauncherEdit_SubtitleNew => Get(nameof(LauncherEdit_SubtitleNew));
    public static string LauncherEdit_SubtitleEdit => Get(nameof(LauncherEdit_SubtitleEdit));
    public static string LauncherEdit_Kind => Get(nameof(LauncherEdit_Kind));
    public static string LauncherEdit_KindLocal => Get(nameof(LauncherEdit_KindLocal));
    public static string LauncherEdit_KindWeb => Get(nameof(LauncherEdit_KindWeb));
    public static string LauncherEdit_Name => Get(nameof(LauncherEdit_Name));
    public static string LauncherEdit_NamePlaceholder => Get(nameof(LauncherEdit_NamePlaceholder));
    public static string LauncherEdit_Target => Get(nameof(LauncherEdit_Target));
    public static string LauncherEdit_TargetPlaceholder => Get(nameof(LauncherEdit_TargetPlaceholder));
    public static string LauncherEdit_TargetPlaceholder_Exe => Get(nameof(LauncherEdit_TargetPlaceholder_Exe));
    public static string LauncherEdit_TargetPlaceholder_Web => Get(nameof(LauncherEdit_TargetPlaceholder_Web));
    public static string LauncherEdit_Args => Get(nameof(LauncherEdit_Args));
    public static string LauncherEdit_ArgsPlaceholder => Get(nameof(LauncherEdit_ArgsPlaceholder));
    public static string LauncherEdit_ArgsHint => Get(nameof(LauncherEdit_ArgsHint));
    public static string LauncherEdit_WorkDir => Get(nameof(LauncherEdit_WorkDir));
    public static string LauncherEdit_WorkDirPlaceholder => Get(nameof(LauncherEdit_WorkDirPlaceholder));
    public static string LauncherEdit_IconPreview => Get(nameof(LauncherEdit_IconPreview));
    public static string LauncherEdit_Picker_Exe => Get(nameof(LauncherEdit_Picker_Exe));
    public static string LauncherEdit_Picker_ExeFilter => Get(nameof(LauncherEdit_Picker_ExeFilter));
    public static string LauncherEdit_Picker_AllFilter => Get(nameof(LauncherEdit_Picker_AllFilter));
    public static string LauncherEdit_Picker_WorkDir => Get(nameof(LauncherEdit_Picker_WorkDir));

    // PromptTemplateEditWindow
    public static string PromptEdit_Title => Get(nameof(PromptEdit_Title));
    public static string PromptEdit_TitleNew => Get(nameof(PromptEdit_TitleNew));
    public static string PromptEdit_TitleEdit => Get(nameof(PromptEdit_TitleEdit));
    public static string PromptEdit_Heading => Get(nameof(PromptEdit_Heading));
    public static string PromptEdit_HeadingNew => Get(nameof(PromptEdit_HeadingNew));
    public static string PromptEdit_HeadingEditName => Get(nameof(PromptEdit_HeadingEditName));
    public static string PromptEdit_Subtitle => Get(nameof(PromptEdit_Subtitle));
    public static string PromptEdit_SubtitleNew => Get(nameof(PromptEdit_SubtitleNew));
    public static string PromptEdit_SubtitleEdit => Get(nameof(PromptEdit_SubtitleEdit));
    public static string PromptEdit_Name => Get(nameof(PromptEdit_Name));
    public static string PromptEdit_NamePlaceholder => Get(nameof(PromptEdit_NamePlaceholder));
    // Custom-action bilingual name fields (Chinese + English). Both are shown
    // when creating or editing a custom action so the name can follow the UI
    // language. At least one must be filled.
    public static string PromptEdit_NameZh => Get(nameof(PromptEdit_NameZh));
    public static string PromptEdit_NameEn => Get(nameof(PromptEdit_NameEn));
    public static string PromptEdit_NameZhPlaceholder => Get(nameof(PromptEdit_NameZhPlaceholder));
    public static string PromptEdit_NameEnPlaceholder => Get(nameof(PromptEdit_NameEnPlaceholder));
    public static string PromptEdit_NameHintNew => Get(nameof(PromptEdit_NameHintNew));
    public static string PromptEdit_NameHintEdit => Get(nameof(PromptEdit_NameHintEdit));
    public static string PromptEdit_AllowThinking => Get(nameof(PromptEdit_AllowThinking));
    public static string PromptEdit_ShortcutLabel => Get(nameof(PromptEdit_ShortcutLabel));
    public static string PromptEdit_ShortcutHint => Get(nameof(PromptEdit_ShortcutHint));
    public static string PromptEdit_Reset => Get(nameof(PromptEdit_Reset));
    public static string PromptEdit_DefaultHint_Translate => Get(nameof(PromptEdit_DefaultHint_Translate));
    public static string PromptEdit_DefaultHint => Get(nameof(PromptEdit_DefaultHint));

    // PinnedScreenshotWindow
    public static string Pinned_Title => Get(nameof(Pinned_Title));
    public static string Pinned_CopyImage => Get(nameof(Pinned_CopyImage));
    public static string Pinned_CloseAll => Get(nameof(Pinned_CloseAll));

    // RegionSelectOverlay
    public static string Region_Title => Get(nameof(Region_Title));
    public static string Region_HintPrimary => Get(nameof(Region_HintPrimary));
    public static string Region_HintSecondary => Get(nameof(Region_HintSecondary));

    // SelectionRuntime — toolbar diagnostic status
    public static string Runtime_Status_Unrecognized => Get(nameof(Runtime_Status_Unrecognized));
    public static string Runtime_Status_CopiedColor => Get(nameof(Runtime_Status_CopiedColor));
    public static string Runtime_Status_AnnotateIntro => Get(nameof(Runtime_Status_AnnotateIntro));
    public static string Runtime_Status_AnnotateExited => Get(nameof(Runtime_Status_AnnotateExited));
    public static string Runtime_Status_PinnedCopied => Get(nameof(Runtime_Status_PinnedCopied));
    public static string Runtime_Status_AnnotateCurrent => Get(nameof(Runtime_Status_AnnotateCurrent));
    public static string Runtime_Status_OcrFailed => Get(nameof(Runtime_Status_OcrFailed));
    public static string Runtime_Status_MouseHookFailed => Get(nameof(Runtime_Status_MouseHookFailed));
    public static string Runtime_Tool_Number => Get(nameof(Runtime_Tool_Number));
    public static string Runtime_Tool_Rectangle => Get(nameof(Runtime_Tool_Rectangle));
    public static string Runtime_Tool_Ellipse => Get(nameof(Runtime_Tool_Ellipse));
    public static string Runtime_Tool_Arrow => Get(nameof(Runtime_Tool_Arrow));
    public static string Runtime_Tool_Pen => Get(nameof(Runtime_Tool_Pen));
    public static string Runtime_Tool_Highlight => Get(nameof(Runtime_Tool_Highlight));
    public static string Runtime_Tool_Unknown => Get(nameof(Runtime_Tool_Unknown));
    public static string Runtime_Annotation_ToolHint => Get(nameof(Runtime_Annotation_ToolHint));

    // InstalledAppsScanDialog
    public static string ScanDialog_Title => Get(nameof(ScanDialog_Title));
    public static string ScanDialog_Heading => Get(nameof(ScanDialog_Heading));
    public static string ScanDialog_Subtitle => Get(nameof(ScanDialog_Subtitle));
    public static string ScanDialog_SearchPlaceholder => Get(nameof(ScanDialog_SearchPlaceholder));
    public static string ScanDialog_SelectAll => Get(nameof(ScanDialog_SelectAll));
    public static string ScanDialog_Import => Get(nameof(ScanDialog_Import));
    public static string ScanDialog_Count => Get(nameof(ScanDialog_Count));

    // ── Third i18n pass: REQ-027 Settings redesign (phone views, dashboard,
    //    profile/greeting, theme concept block). See merge 4af7576.

    // SettingsWindow — Personal Greeting card (General section)
    public static string Settings_Profile_CardTitle => Get(nameof(Settings_Profile_CardTitle));
    public static string Settings_Profile_DisplayNameLabel => Get(nameof(Settings_Profile_DisplayNameLabel));
    public static string Settings_Profile_DisplayNamePlaceholder => Get(nameof(Settings_Profile_DisplayNamePlaceholder));
    public static string Settings_Profile_Hint => Get(nameof(Settings_Profile_Hint));
    // Code-behind interpolated status (SetUserProfileSettings default + App save)
    public static string Settings_Profile_StatusGreeting => Get(nameof(Settings_Profile_StatusGreeting));
    public static string Settings_Profile_StatusSaved => Get(nameof(Settings_Profile_StatusSaved));

    // SettingsWindow — phone kicker greeting (top of phone panel)
    public static string Settings_Greeting_Prefix => Get(nameof(Settings_Greeting_Prefix));
    public static string Settings_Greeting_DefaultName => Get(nameof(Settings_Greeting_DefaultName));

    // SettingsWindow — Dashboard page
    public static string Settings_Dashboard_Modules => Get(nameof(Settings_Dashboard_Modules));
    public static string Settings_Dashboard_Events => Get(nameof(Settings_Dashboard_Events));
    public static string Settings_Dashboard_Models => Get(nameof(Settings_Dashboard_Models));
    public static string Settings_Dashboard_TopFeature => Get(nameof(Settings_Dashboard_TopFeature));
    public static string Settings_Dashboard_TextActive => Get(nameof(Settings_Dashboard_TextActive));
    public static string Settings_Dashboard_VisionActive => Get(nameof(Settings_Dashboard_VisionActive));
    public static string Settings_Dashboard_LocalOnly => Get(nameof(Settings_Dashboard_LocalOnly));
    public static string Settings_Dashboard_FeatureFrequency => Get(nameof(Settings_Dashboard_FeatureFrequency));
    public static string Settings_Dashboard_FeatureFrequencyDesc => Get(nameof(Settings_Dashboard_FeatureFrequencyDesc));
    public static string Settings_Dashboard_ModelPreference => Get(nameof(Settings_Dashboard_ModelPreference));
    public static string Settings_Dashboard_ModelPreferenceDesc => Get(nameof(Settings_Dashboard_ModelPreferenceDesc));
    public static string Settings_Dashboard_EmptyTitle => Get(nameof(Settings_Dashboard_EmptyTitle));
    public static string Settings_Dashboard_EmptyHint => Get(nameof(Settings_Dashboard_EmptyHint));
    public static string Settings_Dashboard_NoLocalHistory => Get(nameof(Settings_Dashboard_NoLocalHistory));
    public static string Settings_Dashboard_NoModules => Get(nameof(Settings_Dashboard_NoModules));
    public static string Settings_Dashboard_Ready => Get(nameof(Settings_Dashboard_Ready));
    public static string Settings_Dashboard_ReadingConfig => Get(nameof(Settings_Dashboard_ReadingConfig));
    public static string Settings_Dashboard_ActiveLocally => Get(nameof(Settings_Dashboard_ActiveLocally));
    public static string Settings_Dashboard_PrivacySafe => Get(nameof(Settings_Dashboard_PrivacySafe));
    public static string Settings_Dashboard_TextAndVision => Get(nameof(Settings_Dashboard_TextAndVision));
    public static string Settings_Dashboard_NotConfigured => Get(nameof(Settings_Dashboard_NotConfigured));
    public static string Settings_Dashboard_NoModelSelected => Get(nameof(Settings_Dashboard_NoModelSelected));
    public static string Settings_Dashboard_WaitingHistory => Get(nameof(Settings_Dashboard_WaitingHistory));
    // Code-behind picks singular/plural by count.
    public static string Settings_Dashboard_LocalEventCount_Singular => Get(nameof(Settings_Dashboard_LocalEventCount_Singular));
    public static string Settings_Dashboard_LocalEventCount_Plural => Get(nameof(Settings_Dashboard_LocalEventCount_Plural));
    // Module display names (dashboard top-feature row + module-summary join).
    // SetDashboardModuleActive receives a module identifier and maps it here.
    public static string Settings_Dashboard_Module_OceanEyes => Get(nameof(Settings_Dashboard_Module_OceanEyes));
    public static string Settings_Dashboard_Module_Actions => Get(nameof(Settings_Dashboard_Module_Actions));
    public static string Settings_Dashboard_Module_Launcher => Get(nameof(Settings_Dashboard_Module_Launcher));
    public static string Settings_Dashboard_Module_Clipboard => Get(nameof(Settings_Dashboard_Module_Clipboard));
    public static string Settings_Dashboard_Module_Vision => Get(nameof(Settings_Dashboard_Module_Vision));
    public static string Settings_Dashboard_Module_Translation => Get(nameof(Settings_Dashboard_Module_Translation));

    // SettingsWindow — phone Translation view
    public static string Settings_Phone_Translation_Title => Get(nameof(Settings_Phone_Translation_Title));
    public static string Settings_Phone_Translation_Subtitle => Get(nameof(Settings_Phone_Translation_Subtitle));
    public static string Settings_Phone_Translation_ActiveProvider => Get(nameof(Settings_Phone_Translation_ActiveProvider));
    public static string Settings_Phone_Translation_ManageHint => Get(nameof(Settings_Phone_Translation_ManageHint));
    public static string Settings_Phone_Translation_ModelFallback => Get(nameof(Settings_Phone_Translation_ModelFallback));
    // Code-behind picks singular/plural by count.
    public static string Settings_Phone_Translation_ProviderCount_Singular => Get(nameof(Settings_Phone_Translation_ProviderCount_Singular));
    public static string Settings_Phone_Translation_ProviderCount_Plural => Get(nameof(Settings_Phone_Translation_ProviderCount_Plural));

    // SettingsWindow — phone Vision (Ocean Eyes) view
    public static string Settings_Phone_Vision_Title => Get(nameof(Settings_Phone_Vision_Title));
    public static string Settings_Phone_Vision_OcrProvider => Get(nameof(Settings_Phone_Vision_OcrProvider));
    public static string Settings_Phone_Vision_StatusReady => Get(nameof(Settings_Phone_Vision_StatusReady));
    public static string Settings_Phone_Vision_StatusDisabled => Get(nameof(Settings_Phone_Vision_StatusDisabled));
    public static string Settings_Phone_Vision_UiaAssistOn => Get(nameof(Settings_Phone_Vision_UiaAssistOn));
    public static string Settings_Phone_Vision_OcrOnly => Get(nameof(Settings_Phone_Vision_OcrOnly));

    // SettingsWindow — phone Clipboard view
    public static string Settings_Phone_Clipboard_Title => Get(nameof(Settings_Phone_Clipboard_Title));
    public static string Settings_Phone_Clipboard_HistoryHotkey => Get(nameof(Settings_Phone_Clipboard_HistoryHotkey));
    public static string Settings_Phone_Clipboard_LocalRetention => Get(nameof(Settings_Phone_Clipboard_LocalRetention));
    public static string Settings_Phone_Clipboard_StatusActive => Get(nameof(Settings_Phone_Clipboard_StatusActive));
    public static string Settings_Phone_Clipboard_StatusPaused => Get(nameof(Settings_Phone_Clipboard_StatusPaused));
    // Code-behind interpolated: {0}=text count, {1}=image count
    public static string Settings_Phone_Clipboard_RetentionSummary => Get(nameof(Settings_Phone_Clipboard_RetentionSummary));

    // SettingsWindow — phone Launcher view
    public static string Settings_Phone_Launcher_Title => Get(nameof(Settings_Phone_Launcher_Title));
    public static string Settings_Phone_Launcher_Subtitle => Get(nameof(Settings_Phone_Launcher_Subtitle));
    public static string Settings_Phone_Launcher_SpotlightHotkey => Get(nameof(Settings_Phone_Launcher_SpotlightHotkey));
    public static string Settings_Phone_Launcher_NoDestinations => Get(nameof(Settings_Phone_Launcher_NoDestinations));
    // Code-behind picks singular/plural by count.
    public static string Settings_Phone_Launcher_DestinationsCount_Singular => Get(nameof(Settings_Phone_Launcher_DestinationsCount_Singular));
    public static string Settings_Phone_Launcher_DestinationsCount_Plural => Get(nameof(Settings_Phone_Launcher_DestinationsCount_Plural));

    // SettingsWindow — theme concept block (pre-existing hardcoded, now wired)
    public static string Settings_Theme_Concept => Get(nameof(Settings_Theme_Concept));
    public static string Settings_Theme_Tagline => Get(nameof(Settings_Theme_Tagline));
    public static string Settings_Theme_Description => Get(nameof(Settings_Theme_Description));
    public static string Settings_Theme_ColorPalette => Get(nameof(Settings_Theme_ColorPalette));
    public static string Settings_Theme_Typography => Get(nameof(Settings_Theme_Typography));
    public static string Settings_Theme_IconStyle => Get(nameof(Settings_Theme_IconStyle));
    public static string Settings_Theme_IconStyleDesc => Get(nameof(Settings_Theme_IconStyleDesc));
    public static string Settings_Theme_Role_Primary => Get(nameof(Settings_Theme_Role_Primary));
    public static string Settings_Theme_Role_Secondary => Get(nameof(Settings_Theme_Role_Secondary));
    public static string Settings_Theme_Role_Accent => Get(nameof(Settings_Theme_Role_Accent));
    public static string Settings_Theme_Role_Border => Get(nameof(Settings_Theme_Role_Border));
    public static string Settings_Theme_Role_Ivory => Get(nameof(Settings_Theme_Role_Ivory));
    public static string Settings_Theme_Role_Cream => Get(nameof(Settings_Theme_Role_Cream));
    public static string Settings_Theme_Role_DeepBrown => Get(nameof(Settings_Theme_Role_DeepBrown));
    public static string Settings_Theme_BodyFont => Get(nameof(Settings_Theme_BodyFont));
    public static string Settings_Theme_HeadingFont => Get(nameof(Settings_Theme_HeadingFont));

        // SettingsWindow — misc
        public static string Settings_VersionTag => Get(nameof(Settings_VersionTag));

    // OcrTextWindow — OCR 文本提取弹窗（Q 快捷键）
    public static string Ocr_WindowTitle => Get(nameof(Ocr_WindowTitle));
    public static string Ocr_Title => Get(nameof(Ocr_Title));
    public static string Ocr_Subtitle => Get(nameof(Ocr_Subtitle));
    public static string Ocr_CopyAndClose => Get(nameof(Ocr_CopyAndClose));
    public static string Ocr_Copied => Get(nameof(Ocr_Copied));
    public static string Ocr_ClipboardError => Get(nameof(Ocr_ClipboardError));

    // ResultWindow — 源文本可编辑 / C 加速器
    public static string Result_SourceEditable => Get(nameof(Result_SourceEditable));
    public static string Result_RetryWithEdited => Get(nameof(Result_RetryWithEdited));
}
