using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Minimal popup that asks the user to supply a value for a single
/// <c>{prompt:...}</c> placeholder before launching a launcher entry.
/// The label is set by the caller; the user types a value and clicks
/// "确定" or presses Enter to confirm.
/// </summary>
public partial class ParameterInputDialog : Window
{
    public ParameterInputDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised when the user confirms. Arg = the trimmed text from the input box.
    /// </summary>
    public event Action<string>? Confirmed;

    /// <summary>Raised when the user cancels (clicks "取消" or presses Escape).</summary>
    public event Action? Cancelled;

    /// <summary>
    /// Shows the dialog with the given prompt text and focuses the input box.
    /// </summary>
    public void Show(string prompt)
    {
        PromptLabel.Text = prompt;
        ValueInput.Text = string.Empty;

        if (!IsVisible)
        {
            Show();
        }
        Activate();
        ValueInput.Focus();
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        string value = ValueInput.Text?.Trim() ?? string.Empty;
        Confirmed?.Invoke(value);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Cancelled?.Invoke();
        Close();
    }

    private void OnValueInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            string value = ValueInput.Text?.Trim() ?? string.Empty;
            Confirmed?.Invoke(value);
            Close();
        }
        else if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke();
            Close();
        }
    }
}
