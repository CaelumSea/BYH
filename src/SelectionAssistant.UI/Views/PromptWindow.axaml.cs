using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Pop-up that lets the user type an arbitrary prompt and run it against the
/// currently selected text, using the active provider. The captured text is
/// supplied by the runtime when the window is shown; the user-supplied prompt
/// is returned via <see cref="PromptRunRequested" />.
/// </summary>
public partial class PromptWindow : Window
{
    private string? _capturedText;
    private bool _allowClose;

    public PromptWindow()
    {
        InitializeComponent();
        PromptInput.TextChanged += (_, _) =>
        {
            RunButton.IsEnabled = !string.IsNullOrWhiteSpace(PromptInput.Text) &&
                                  !string.IsNullOrEmpty(_capturedText);
        };

        Closing += (_, eventArgs) =>
        {
            if (_allowClose)
            {
                return;
            }

            eventArgs.Cancel = true;
            Hide();
        };
    }

    /// <summary>Raised when the user clicks Run. Args = (selectedText, userPrompt).</summary>
    public event Action<string, string>? PromptRunRequested;

    public void PrepareForShutdown() => _allowClose = true;

    /// <summary>
    /// Shows the window seeded with the currently selected text (shown as a
    /// preview; the full text is what gets sent). Call this from the runtime
    /// when the toolbar's "Prompt" button is clicked.
    /// </summary>
    public void ShowForSelection(string selectedText)
    {
        _capturedText = string.IsNullOrWhiteSpace(selectedText) ? null : selectedText;
        string preview = _capturedText is null
            ? "未取到选中文本。"
            : (_capturedText.Length <= 80
                ? _capturedText
                : _capturedText[..80] + "…");
        SourcePreview.Text = "选中文字：" + preview;
        PromptInput.Text = string.Empty;
        RunButton.IsEnabled = false;

        if (!IsVisible)
        {
            Show();
        }
        Activate();
        PromptInput.Focus();
    }

    private void OnRunClick(object? sender, RoutedEventArgs e)
    {
        string prompt = PromptInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(prompt) || string.IsNullOrEmpty(_capturedText))
        {
            return;
        }

        PromptRunRequested?.Invoke(_capturedText, prompt);
        Hide();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Hide();

    /// <summary>
    /// R38: Esc closes the prompt window. Reuses OnCancelClick (single source
    /// of truth for the hide path). Window is created once and reused, so
    /// Hide() is correct — not Close().
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Hide();
        }
    }
}
