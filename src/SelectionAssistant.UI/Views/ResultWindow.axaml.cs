using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SelectionAssistant.Core.Translation;

namespace SelectionAssistant.UI.Views;

public partial class ResultWindow : Window
{
    private string? _translatedText;
    private bool _allowClose;

    public ResultWindow()
    {
        InitializeComponent();
        // Attach the standard Copy/Cut/Paste/Select-all context menu to both
        // text panes. ResultTextBox is read-only, so cut/paste are hidden there.
        TextBoxContextMenu.Attach(SourceTextBox);
        TextBoxContextMenu.Attach(ResultTextBox);

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
    }

    public event Action? RetryRequested;

    public event Action? ReplaceRequested;

    public event Action? CloseRequested;

    public void PrepareForShutdown() => _allowClose = true;

    /// <summary>
    /// Returns the most recent completed translation text (null while loading
    /// or on error). Read by the runtime to drive the "replace selection"
    /// action without exposing the private field.
    /// </summary>
    public string? GetTranslatedText() => _translatedText;

    public void ShowLoading(TranslationRequest request, string providerName)
    {
        _translatedText = null;
        _streamingStarted = false;
        SourceTextBox.Text = request.SourceText;
        LanguagePairText.Text = FormatLanguage(request.SourceLanguage) +
            " → " + FormatLanguage(request.TargetLanguage);
        ProviderText.Text = providerName;
        ResultTextBox.Text = "正在翻译…";
        LoadingBar.IsVisible = true;
        ErrorText.IsVisible = false;
        CopyButton.IsEnabled = false;
        CopySourceButton.IsEnabled = true;  // source text is already available
        ReplaceButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        ShowAndActivate();
    }

    public void ShowResult(TranslationResult result)
    {
        _translatedText = result.TranslatedText;
        ResultTextBox.Text = result.TranslatedText;
        ProviderText.Text = result.ProviderName;
        LoadingBar.IsVisible = false;
        ErrorText.IsVisible = false;
        CopyButton.IsEnabled = true;
        CopySourceButton.IsEnabled = true;
        ReplaceButton.IsEnabled = true;
        RetryButton.IsEnabled = true;
    }

    private bool _streamingStarted;

    public void AppendPartialResult(string chunk)
    {
        // First chunk of a streaming run clears the loading placeholder and
        // enters streaming mode; later chunks append.
        if (!_streamingStarted)
        {
            _streamingStarted = true;
            _translatedText = null;
            ResultTextBox.Text = chunk;
            ErrorText.IsVisible = false;
        }
        else
        {
            ResultTextBox.Text += chunk;
        }
    }

    public void ResetStreamingState() => _streamingStarted = false;

    public void ShowError(string userMessage)
    {
        _translatedText = null;
        ResultTextBox.Text = "没有可显示的译文";
        LoadingBar.IsVisible = false;
        ErrorText.Text = userMessage;
        SetFeedbackTone(ErrorText, isError: true);
        ErrorText.IsVisible = true;
        CopyButton.IsEnabled = false;
        CopySourceButton.IsEnabled = SourceTextBox.Text is { Length: > 0 };
        ReplaceButton.IsEnabled = false;
        RetryButton.IsEnabled = true;
        ShowAndActivate();
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_translatedText is not { Length: > 0 } text)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            ErrorText.Text = "无法访问系统剪贴板。";
            SetFeedbackTone(ErrorText, isError: true);
            ErrorText.IsVisible = true;
            return;
        }

        await clipboard.SetTextAsync(text);
        ErrorText.Text = "已复制译文";
        SetFeedbackTone(ErrorText, isError: false);
        ErrorText.IsVisible = true;
    }

    private async void OnCopySourceClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (SourceTextBox.Text is not { Length: > 0 } text)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            ErrorText.Text = "无法访问系统剪贴板。";
            SetFeedbackTone(ErrorText, isError: true);
            ErrorText.IsVisible = true;
            return;
        }

        await clipboard.SetTextAsync(text);
        ErrorText.Text = "已复制原文";
        SetFeedbackTone(ErrorText, isError: false);
        ErrorText.IsVisible = true;
    }

    private static void SetFeedbackTone(TextBlock target, bool isError)
    {
        target.Classes.Remove("FeedbackSuccess");
        target.Classes.Remove("FeedbackError");
        target.Classes.Add(isError ? "FeedbackError" : "FeedbackSuccess");
    }

    private void OnRetryClick(object? sender, RoutedEventArgs eventArgs) =>
        RetryRequested?.Invoke();

    private void OnReplaceClick(object? sender, RoutedEventArgs eventArgs) =>
        ReplaceRequested?.Invoke();

    private void OnCloseClick(object? sender, RoutedEventArgs eventArgs)
    {
        Hide();
        CloseRequested?.Invoke();
    }

    // ESC closes the result window. Unlike the WS_EX_NOACTIVATE toolbar
    // (which needs a low-level hook), this window activates normally and
    // receives Avalonia KeyDown directly.
    private void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Hide();
            CloseRequested?.Invoke();
        }
    }

    private void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    private static string FormatLanguage(string language) => language switch
    {
        "zh-CN" => "简体中文",
        "en" => "English",
        _ => language,
    };
}
