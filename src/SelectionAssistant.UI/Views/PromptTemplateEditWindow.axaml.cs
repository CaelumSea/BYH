using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SelectionAssistant.Core.I18n;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Modal editor for a single prompt template. Shows the action name, the
/// current prompt, and a hint with the built-in default. Saving raises
/// <see cref="TemplateSaved" /> with the new prompt text; "恢复默认" raises
/// <see cref="TemplateReset" /> so the caller can restore the built-in value.
/// </summary>
public partial class PromptTemplateEditWindow : Window
{
    private string _actionId = string.Empty;
    private string _defaultPrompt = string.Empty;
    private bool _isNew;

    public PromptTemplateEditWindow()
    {
        InitializeComponent();
    }

    /// <summary>Raised on save (edit mode). Args = (actionId, newPrompt, thinkingEnabled, shortcut).</summary>
    public event Action<string, string, bool, string?>? TemplateSaved;

    /// <summary>Raised on "恢复默认" (edit mode). Arg = actionId.</summary>
    public event Action<string>? TemplateReset;

    /// <summary>Raised on save (new mode). Args = (name, prompt, thinkingEnabled, shortcut).</summary>
    public event Action<string, string, bool, string?>? TemplateCreated;

    /// <summary>
    /// Seeds the editor for creating a NEW custom function. Shows a name input
    /// box; the save button raises <see cref="TemplateCreated" />.
    /// </summary>
    public void ShowForNew()
    {
        _isNew = true;
        Title = Strings.PromptEdit_TitleNew;
        TitleText.Text = Strings.PromptEdit_HeadingNew;
        SubtitleText.Text = Strings.PromptEdit_SubtitleNew;
        NamePanel.IsVisible = true;
        DefaultHintText.IsVisible = false;
        ResetButton.IsVisible = false;
        PromptInput.Text = string.Empty;
        ThinkingCheckBox.IsChecked = false;
        ShortcutInput.Text = string.Empty;

        Show();
        Activate();
        NameInput.Focus();
    }

    /// <summary>
    /// Seeds the editor for the given action (edit mode). <paramref name="thinkingEnabled" />
    /// is the current thinking flag (shown + editable via the checkbox). The
    /// <paramref name="defaultPrompt" /> is shown as a hint and used when the
    /// user clicks "恢复默认". <paramref name="currentShortcut" /> seeds the
    /// single-character toolbar shortcut field (null/empty = no shortcut).
    /// </summary>
    public void ShowFor(
        string actionId,
        string actionName,
        string currentPrompt,
        bool thinkingEnabled,
        string defaultPrompt,
        string? currentShortcut)
    {
        _isNew = false;
        _actionId = actionId;
        _defaultPrompt = defaultPrompt;

        Title = Strings.PromptEdit_TitleEdit;
        TitleText.Text = string.Format(Strings.PromptEdit_HeadingEditName, actionName);
        SubtitleText.Text = Strings.PromptEdit_SubtitleEdit;
        NamePanel.IsVisible = false;
        DefaultHintText.IsVisible = true;
        ResetButton.IsVisible = true;
        PromptInput.Text = currentPrompt;
        ThinkingCheckBox.IsChecked = thinkingEnabled;
        ShortcutInput.Text = currentShortcut ?? string.Empty;

        string hint = string.IsNullOrEmpty(defaultPrompt)
            ? Strings.PromptEdit_DefaultHint_Translate
            : string.Format(Strings.PromptEdit_DefaultHint, Truncate(defaultPrompt, 100));
        DefaultHintText.Text = hint;

        Show();
        Activate();
        PromptInput.Focus();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        string prompt = PromptInput.Text ?? string.Empty;
        bool thinking = ThinkingCheckBox.IsChecked == true;
        // Normalize: trim, upper-case, single char. Empty/whitespace = null.
        string shortcutRaw = ShortcutInput.Text?.Trim() ?? string.Empty;
        string? shortcut = string.IsNullOrEmpty(shortcutRaw) ? null : shortcutRaw.ToUpperInvariant();

        if (_isNew)
        {
            string name = NameInput.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                NameInput.Focus();
                return;
            }
            TemplateCreated?.Invoke(name, prompt, thinking, shortcut);
        }
        else
        {
            TemplateSaved?.Invoke(_actionId, prompt, thinking, shortcut);
        }
        Close();
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        // Reset restores both the prompt and thinking to built-in defaults.
        // The caller's handler (→ runtime.ResetPromptTemplateAsync) rewrites the
        // template; we also set the checkbox here so if the window is reused
        // it reflects the reset state.
        ThinkingCheckBox.IsChecked = false;
        TemplateReset?.Invoke(_actionId);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// R38: Esc cancels the editor. Reuses OnCancelClick so Esc and the cancel
    /// button share one close path. This dialog is created fresh each time, so
    /// Close() is correct (not Hide()).
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Close();
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
