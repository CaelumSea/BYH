namespace SelectionAssistant.Core.I18n;

/// <summary>
/// English (neutral) dictionary for <see cref="Strings"/>. Built once per
/// process by <see cref="Strings"/>'s static ctor. See the design notes on
/// <see cref="Strings"/> for why this is a hand-rolled literal dictionary
/// rather than .resx / ResourceManager (NativeAOT + full trim).
/// </summary>
internal static partial class Strings_en
{
    public static Dictionary<string, string> Build() => new(128, StringComparer.Ordinal)
    {
        // Common
        ["Common_Confirm"] = "OK",
        ["Common_Cancel"] = "Cancel",
        ["Common_Save"] = "Save",
        ["Common_Close"] = "Close",
        ["Common_Delete"] = "Delete",
        ["Common_Retry"] = "Retry",
        ["Common_Run"] = "Run",
        ["Common_Copy"] = "Copy",

        // ToolbarWindow
        ["Toolbar_Translate"] = "Translate",
        ["Toolbar_Explain"] = "Explain",
        ["Toolbar_Summarize"] = "Summarize",
        ["Toolbar_Prompt"] = "Prompt",
        ["Toolbar_Copy"] = "Copy",
        ["Toolbar_StatusWaiting"] = "Waiting for selection",
        ["Toolbar_StatusCapturing"] = "Capturing · {0},{1}",
        ["Toolbar_StatusCaptured"] = "Captured · {0}",
        ["Toolbar_StatusNeedManualCopy"] = "Manual copy needed",
        ["Toolbar_StatusEmpty"] = "No text captured",
        ["Toolbar_PromptTooltip"] = "Prompt (shortcut R)",
        ["Toolbar_CopyTooltip"] = "Copy (shortcut C)",

        // ResultWindow
        ["Result_Title"] = "Translation",
        ["Result_DefaultLanguagePair"] = "English → Simplified Chinese",
        ["Result_DefaultProvider"] = "Test provider",
        ["Result_SourceLabel"] = "Source",
        ["Result_Loading"] = "Translating…",
        ["Result_EmptyResult"] = "No translation to show",
        ["Result_PrivacyTestMode"] = "MyMemory test mode · the selected text is sent over HTTPS",
        ["Result_CopySource"] = "Copy source",
        ["Result_CopyTranslation"] = "Copy translation",
        ["Result_Replace"] = "Replace",
        ["Result_CopiedTranslation"] = "Translation copied",
        ["Result_CopiedSource"] = "Source copied",
        ["Result_ClipboardAccessError"] = "Unable to access the system clipboard.",
        ["Result_LangChinese"] = "Simplified Chinese",
        ["Result_LangEnglish"] = "English",

        // SpotlightWindow
        ["Spotlight_Title"] = "BYH · Launcher",
        ["Spotlight_SearchPlaceholder"] = "Search…",
        ["Spotlight_CategoryLauncher"] = "Launcher",
        ["Spotlight_FooterSettings"] = "⚙ Settings",
        ["Spotlight_FooterSelect"] = "↑↓ Select",
        ["Spotlight_FooterLaunch"] = "↵ Launch",
        ["Spotlight_FooterEdit"] = "Ctrl+↵ Settings",
        ["Spotlight_FooterClose"] = "Esc Close",

        // PromptWindow
        ["Prompt_Title"] = "BYH · Prompt Now",
        ["Prompt_Heading"] = "Prompt Now",
        ["Prompt_DefaultPreview"] = "Run the instruction you type on the currently selected text.",
        ["Prompt_Placeholder"] = "e.g. explain what this code does in one sentence",
        ["Prompt_FooterHint"] = "Runs with the current provider · result streams in",
        ["Prompt_SelectionPrefix"] = "Selected text: ",
        ["Prompt_NoSelection"] = "No selected text captured.",

        // GalleryWindow
        ["Gallery_Title"] = "BYH · Screenshot Gallery",
        ["Gallery_Heading"] = "📷 Screenshot Gallery",
        ["Gallery_Hint"] = "Double-click to view · right-click for more · Esc to close",
        ["Gallery_CountSuffix"] = " shot(s)",
        ["Gallery_CopiedSuffix"] = " · copied",
        ["Gallery_EmptyTitle"] = "No screenshots yet",
        ["Gallery_CtxCopy"] = "Copy to clipboard",
        ["Gallery_CtxPreview"] = "View full size",
        ["Gallery_CtxDelete"] = "Delete file",
        ["Gallery_CtxReveal"] = "Reveal in Explorer",
        ["Gallery_PreviewCloseHint"] = "Esc / click blank to close",
        ["Gallery_PreviewCopy"] = "📋 Copy",
        ["Gallery_PreviewDelete"] = "🗑 Delete",
        ["Gallery_PreviewReveal"] = "📁 Open folder",

        // ParameterInputDialog
        ["ParamDialog_Title"] = "BYH · Enter parameter",
        ["ParamDialog_DefaultPrompt"] = "Enter a parameter",

        // SettingsWindow — Language card
        ["Settings_LanguageCard_Title"] = "Language",
        ["Settings_LanguageCard_Subtitle"] = "Switching requires a quick restart.",
        ["Settings_LanguageCard_SaveButton"] = "Save Language",
        ["Settings_LanguageCard_StatusCurrent"] = "Current: {0}",
        ["Settings_LanguageCard_StatusSaved"] = "Saved. Restarting…",
        ["Settings_LanguageName_English"] = "English",
        ["Settings_LanguageName_Chinese"] = "简体中文",
    };
}
