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

    // SpotlightWindow
    public static string Spotlight_Title => Get(nameof(Spotlight_Title));
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
}
