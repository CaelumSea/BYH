namespace SelectionAssistant.Core.I18n;

/// <summary>
/// Simplified Chinese (zh-CN) dictionary for <see cref="Strings"/>. Built
/// once per process by <see cref="Strings"/>'s static ctor. The key set
/// MUST match <see cref="Strings_en"/> exactly — <c>StringsTests</c>
/// asserts this so a key added to one file but not the other fails CI.
/// </summary>
internal static partial class Strings_zh_CN
{
    public static Dictionary<string, string> Build() => new(128, StringComparer.Ordinal)
    {
        // Common
        ["Common_Confirm"] = "确定",
        ["Common_Cancel"] = "取消",
        ["Common_Save"] = "保存",
        ["Common_Close"] = "关闭",
        ["Common_Delete"] = "删除",
        ["Common_Retry"] = "重试",
        ["Common_Run"] = "运行",
        ["Common_Copy"] = "复制",

        // ToolbarWindow
        ["Toolbar_Translate"] = "翻译",
        ["Toolbar_Explain"] = "解释",
        ["Toolbar_Summarize"] = "总结",
        ["Toolbar_Prompt"] = "Prompt",
        ["Toolbar_Copy"] = "复制",
        ["Toolbar_StatusWaiting"] = "等待选词",
        ["Toolbar_StatusCapturing"] = "取词中 · {0},{1}",
        ["Toolbar_StatusCaptured"] = "已取词 · {0}",
        ["Toolbar_StatusNeedManualCopy"] = "需要手动复制",
        ["Toolbar_StatusEmpty"] = "暂未取到文本",
        ["Toolbar_PromptTooltip"] = "提示词（快捷键 R）",
        ["Toolbar_CopyTooltip"] = "复制（快捷键 C）",

        // ResultWindow
        ["Result_Title"] = "Translation",
        ["Result_DefaultLanguagePair"] = "English → 简体中文",
        ["Result_DefaultProvider"] = "测试提供器",
        ["Result_SourceLabel"] = "原文",
        ["Result_Loading"] = "正在翻译…",
        ["Result_EmptyResult"] = "没有可显示的译文",
        ["Result_PrivacyTestMode"] = "MyMemory 测试模式 · 选中文字会通过 HTTPS 发送",
        ["Result_CopySource"] = "复制原文",
        ["Result_CopyTranslation"] = "复制译文",
        ["Result_Replace"] = "替换",
        ["Result_CopiedTranslation"] = "已复制译文",
        ["Result_CopiedSource"] = "已复制原文",
        ["Result_ClipboardAccessError"] = "无法访问系统剪贴板。",
        ["Result_LangChinese"] = "简体中文",
        ["Result_LangEnglish"] = "English",

        // SpotlightWindow
        ["Spotlight_Title"] = "BYH · Launcher",
        ["Spotlight_SearchPlaceholder"] = "Search…",
        ["Spotlight_CategoryLauncher"] = "启动器",
        ["Spotlight_FooterSettings"] = "⚙ 设置",
        ["Spotlight_FooterSelect"] = "↑↓ 选择",
        ["Spotlight_FooterLaunch"] = "↵ 启动",
        ["Spotlight_FooterEdit"] = "Ctrl+↵ 设置",
        ["Spotlight_FooterClose"] = "Esc 关闭",

        // PromptWindow
        ["Prompt_Title"] = "BYH · Prompt Now",
        ["Prompt_Heading"] = "Prompt Now",
        ["Prompt_DefaultPreview"] = "对已选中的文字运行你输入的指令。",
        ["Prompt_Placeholder"] = "例如：用一句话解释这段代码在做什么",
        ["Prompt_FooterHint"] = "用当前 Provider 运行 · 结果会流式显示",
        ["Prompt_SelectionPrefix"] = "选中文字：",
        ["Prompt_NoSelection"] = "未取到选中文本。",

        // GalleryWindow
        ["Gallery_Title"] = "BYH · 截图相册",
        ["Gallery_Heading"] = "📷 截图相册",
        ["Gallery_Hint"] = "双击查看 · 右键更多操作 · Esc 关闭",
        ["Gallery_CountSuffix"] = " 张",
        ["Gallery_CopiedSuffix"] = " · 已复制",
        ["Gallery_EmptyTitle"] = "还没有截图",
        ["Gallery_CtxCopy"] = "复制到剪贴板",
        ["Gallery_CtxPreview"] = "查看大图",
        ["Gallery_CtxDelete"] = "删除文件",
        ["Gallery_CtxReveal"] = "在资源管理器中显示",
        ["Gallery_PreviewCloseHint"] = "Esc / 点击空白处关闭",
        ["Gallery_PreviewCopy"] = "📋 复制",
        ["Gallery_PreviewDelete"] = "🗑 删除",
        ["Gallery_PreviewReveal"] = "📁 打开目录",

        // ParameterInputDialog
        ["ParamDialog_Title"] = "BYH · 输入参数",
        ["ParamDialog_DefaultPrompt"] = "请输入参数",

        // SettingsWindow — Language card
        ["Settings_LanguageCard_Title"] = "语言 / Language",
        ["Settings_LanguageCard_Subtitle"] = "切换语言后会自动重启应用。",
        ["Settings_LanguageCard_SaveButton"] = "保存语言",
        ["Settings_LanguageCard_StatusCurrent"] = "当前：{0}",
        ["Settings_LanguageCard_StatusSaved"] = "已保存。正在重启…",
        ["Settings_LanguageName_English"] = "English",
        ["Settings_LanguageName_Chinese"] = "简体中文",
    };
}
