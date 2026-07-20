using System.Windows.Input;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Row view-model for a single prompt template / custom function shown in the
/// settings "自定义功能" card and the QuickTools panel. Must be a public
/// top-level type for Avalonia compiled bindings (private nested types fail
/// AVLN2000 and fall back to reflection, which breaks NativeAOT 0-warnings).
/// <para>
/// The <see cref="RunCommand"/> is bound by the QuickTools panel (click = run
/// the action against the selected text). The settings card uses its own
/// edit/delete commands via <see cref="EditCommand"/> / <see cref="DeleteCommand"/>.
/// </para>
/// </summary>
public sealed class PromptFunctionRow
{
    /// <summary>Stable action id ("translate", "summarize", "explain", or "custom-*").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name (e.g. "翻译", "润色").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Truncated prompt preview for the settings card display.</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>True for built-in actions (no delete button); false for custom.</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>QuickTools: click the row button → run this action. Set by QuickTools.</summary>
    public ICommand? RunCommand { get; set; }

    /// <summary>Settings: edit this function's prompt. Set by SettingsWindow.</summary>
    public ICommand? EditCommand { get; set; }

    /// <summary>Settings: delete this custom function. Set by SettingsWindow. Null for built-ins.</summary>
    public ICommand? DeleteCommand { get; set; }
}
