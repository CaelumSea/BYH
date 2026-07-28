using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SelectionAssistant.Core.I18n;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Popup that shows OCR-recognized text in an editable box. Pressing the Q
/// toolbar shortcut opens it (after Ocean Eyes lazy OCR seeds the captured
/// text). The user can fix recognition errors, then press C (when the text
/// box does not have focus) or click "复制并关闭" to copy the current text
/// to the clipboard and close.
/// </summary>
/// <remarks>
/// <b>C-key conflict resolution.</b> C is both a copy accelerator and a
/// normal letter the user may need to type to correct OCR output. The
/// contract (confirmed with the user): when <see cref="OcrTextBox"/> holds
/// keyboard focus, C types the letter c; when focus is elsewhere (title,
/// buttons, window background), C copies and closes. We track an explicit
/// <see cref="_textBoxFocused"/> flag updated by the TextBox's own
/// GotFocus/LostFocus events, and on <see cref="Show(string)"/> we defer
/// focus to <see cref="CopyAndCloseButton"/> (past Avalonia's auto-focus
/// pass) so the box starts in "view mode" — the user must click in to edit.
/// </remarks>
public partial class OcrTextWindow : Window
{
    private bool _allowClose;
    private bool _textBoxFocused;

    public OcrTextWindow()
    {
        InitializeComponent();

        // Same reuse-via-Hide pattern as ResultWindow: the window persists
        // across sessions (created once at startup), and the X button / Esc
        // / deactivation just hide it. _allowClose flips only at shutdown.
        Closing += (_, eventArgs) =>
        {
            if (_allowClose)
            {
                return;
            }
            eventArgs.Cancel = true;
            Hide();
            CloseRequested?.Invoke();
        };

        OcrTextBox.GotFocus += (_, _) => _textBoxFocused = true;
        OcrTextBox.LostFocus += (_, _) => _textBoxFocused = false;
    }

    public event Action? CloseRequested;

    public void PrepareForShutdown() => _allowClose = true;

    /// <summary>
    /// Loads the recognized text into the editable box, selects it all (so
    /// the user can immediately type to replace, or Esc to deselect and edit),
    /// and activates the window.
    /// </summary>
    public void Show(string ocrText)
    {
        OcrTextBox.Text = ocrText ?? string.Empty;
        FeedbackText.Text = string.Empty;
        FeedbackText.IsVisible = false;

        if (!IsVisible)
        {
            Show();
        }
        Topmost = true;
        Activate();
        // Default to "view mode": do NOT auto-focus or auto-select the text box.
        // The window activates (so Esc/C keys work and it's on top), but the text
        // box only enters editing state when the user explicitly clicks into it.
        // Auto-selecting on open would put the user in edit mode unintentionally
        // and a stray keystroke could wipe the recognized text. We defer placing
        // focus on CopyAndCloseButton (always enabled, a non-edit control) to
        // AFTER Avalonia's auto-focus pass, so _textBoxFocused stays false and
        // the C accelerator (copy+close) works immediately on open.
        Dispatcher.Post(() => CopyAndCloseButton.Focus(), DispatcherPriority.Input);
    }

    /// <summary>
    /// Copies the current text box content to the clipboard and closes the
    /// window. Returns false if the clipboard was unavailable.
    /// </summary>
    private async void OnCopyAndCloseClick(object? sender, RoutedEventArgs eventArgs)
    {
        string text = OcrTextBox.Text ?? string.Empty;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            SetFeedback(Strings.Ocr_ClipboardError, isError: true);
            return;
        }

        await clipboard.SetTextAsync(text);
        SetFeedback(Strings.Ocr_Copied, isError: false);
        Hide();
        CloseRequested?.Invoke();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs eventArgs)
    {
        Hide();
        CloseRequested?.Invoke();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Hide();
            CloseRequested?.Invoke();
            return;
        }

        if (eventArgs.Key == Key.C && !_textBoxFocused)
        {
            // C accelerator: only when the editable box does not have focus,
            // so typing the letter c inside it still works.
            eventArgs.Handled = true;
            OnCopyAndCloseClick(this, new RoutedEventArgs());
        }
    }

    /// <summary>
    /// Swallows the Enter key inside the text box so multi-line content keeps
    /// a line break rather than accidentally copying. (A single Enter on
    /// single-line content could be a shortcut, but OCR output is usually
    /// multi-line, so we stay consistent and let Enter insert newlines.)
    /// No-op otherwise — the TextBox handles text input natively.
    /// </summary>
    private void OnOcrTextBoxKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        // The window-level handler already gates C on _textBoxFocused, so we
        // don't need to touch it here. Kept as a hook for future per-key rules.
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Hide();
            CloseRequested?.Invoke();
        }
    }

    private void SetFeedback(string message, bool isError)
    {
        FeedbackText.Text = message;
        FeedbackText.Classes.Remove("FeedbackSuccess");
        FeedbackText.Classes.Remove("FeedbackError");
        FeedbackText.Classes.Add(isError ? "FeedbackError" : "FeedbackSuccess");
        FeedbackText.IsVisible = true;
    }
}
