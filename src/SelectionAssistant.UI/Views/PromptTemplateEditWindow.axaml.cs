using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SelectionAssistant.Core.I18n;
using SelectionAssistant.Core.Translation;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Modal editor for a single prompt template. Shows the action name (with both
/// Chinese and English variants for custom actions), the current prompt, and a
/// hint with the built-in default. Saving raises <see cref="TemplateSaved" />
/// with the new prompt text (and optional new name for custom actions);
/// "恢复默认" raises <see cref="TemplateReset" /> so the caller can restore the
/// built-in value.
/// </summary>
public partial class PromptTemplateEditWindow : Window
{
    private string _actionId = string.Empty;
    private string _defaultPrompt = string.Empty;
    private bool _isNew;
    private bool _isCustomAction;

    public PromptTemplateEditWindow()
    {
        InitializeComponent();
    }

    /// <summary>Raised on save (edit mode). Args = (actionId, newPrompt, thinkingEnabled, shortcut, newName). <paramref name="newName" /> is non-null only when editing a custom action whose name changed.</summary>
    public event Action<string, string, bool, string?, LocalizedName?>? TemplateSaved;

    /// <summary>Raised on "恢复默认" (edit mode). Arg = actionId.</summary>
    public event Action<string>? TemplateReset;

    /// <summary>Raised on save (new mode). Args = (name, prompt, thinkingEnabled, shortcut).</summary>
    public event Action<LocalizedName, string, bool, string?>? TemplateCreated;

    /// <summary>
    /// Seeds the editor for creating a NEW custom function. Shows both the
    /// Chinese and English name inputs (at least one must be filled); the save
    /// button raises <see cref="TemplateCreated" />.
    /// </summary>
    public void ShowForNew()
    {
        _isNew = true;
        _isCustomAction = true;
        Title = Strings.PromptEdit_TitleNew;
        TitleText.Text = Strings.PromptEdit_HeadingNew;
        SubtitleText.Text = Strings.PromptEdit_SubtitleNew;
        NamePanel.IsVisible = true;
        NameHintText.Text = Strings.PromptEdit_NameHintNew;
        DefaultHintText.IsVisible = false;
        ResetButton.IsVisible = false;
        NameZhInput.Text = string.Empty;
        NameEnInput.Text = string.Empty;
        PromptInput.Text = string.Empty;
        ThinkingCheckBox.IsChecked = false;
        ShortcutInput.Text = string.Empty;

        Show();
        Activate();
        NameZhInput.Focus();
    }

    /// <summary>
    /// Seeds the editor for the given action (edit mode). <paramref name="thinkingEnabled" />
    /// is the current thinking flag (shown + editable via the checkbox). The
    /// <paramref name="defaultPrompt" /> is shown as a hint and used when the
    /// user clicks "恢复默认". <paramref name="currentShortcut" /> seeds the
    /// single-character toolbar shortcut field (null/empty = no shortcut).
    /// <para>
    /// For custom actions, both name input boxes are shown (pre-filled with the
    /// action's Chinese and English name variants) so the user can rename or
    /// fill in a missing variant. For built-in actions, the name panel stays
    /// hidden — built-in names come from i18n and are not user-editable.
    /// </para>
    /// </summary>
    public void ShowFor(
        string actionId,
        LocalizedName actionName,
        string currentPrompt,
        bool thinkingEnabled,
        string defaultPrompt,
        string? currentShortcut)
    {
        _isNew = false;
        _actionId = actionId;
        _defaultPrompt = defaultPrompt;
        _isCustomAction = PromptActionIds.IsCustom(actionId);

        Title = Strings.PromptEdit_TitleEdit;
        // Heading shows the current-UI-language variant of the name.
        TitleText.Text = string.Format(Strings.PromptEdit_HeadingEditName, actionName.Current(actionId));
        SubtitleText.Text = Strings.PromptEdit_SubtitleEdit;
        // Custom actions can be renamed (show both name boxes, pre-filled);
        // built-ins hide the name panel (name is i18n-driven, not editable).
        NamePanel.IsVisible = _isCustomAction;
        if (_isCustomAction)
        {
            NameZhInput.Text = actionName.Zh;
            NameEnInput.Text = actionName.En;
            NameHintText.Text = Strings.PromptEdit_NameHintEdit;
        }
        DefaultHintText.IsVisible = !_isCustomAction;
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

        if (_isNew || _isCustomAction)
        {
            // Validate: at least one name variant must be non-empty.
            string nameZh = NameZhInput.Text?.Trim() ?? string.Empty;
            string nameEn = NameEnInput.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(nameZh) && string.IsNullOrEmpty(nameEn))
            {
                NameZhInput.Focus();
                return;
            }
            LocalizedName name = new(nameZh, nameEn);

            if (_isNew)
            {
                TemplateCreated?.Invoke(name, prompt, thinking, shortcut);
            }
            else
            {
                // Edit mode for a custom action: pass the (possibly changed)
                // name along so the runtime can rename + save prompt in one go.
                TemplateSaved?.Invoke(_actionId, prompt, thinking, shortcut, name);
            }
        }
        else
        {
            // Built-in action edit: no name to save (i18n-driven).
            TemplateSaved?.Invoke(_actionId, prompt, thinking, shortcut, null);
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
