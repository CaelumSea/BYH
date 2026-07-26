using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SelectionAssistant.Core.I18n;
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

        // R54 v2 bug fix: auto-close on deactivation (popup semantics). When the
        // user clicks away / Alt-Tabs / switches windows / minimizes, the
        // translation window hides itself. This fixes "trigger translate again
        // after the previous window was left open → the new result doesn't come
        // to the front": because the old window was deactivated and hidden, the
        // next trigger is a fresh Show() that lands on top naturally. Skipped
        // while busy (loading or streaming) so a long translation isn't aborted
        // mid-stream when the user glances at another window.
        //
        // Guard against an immediate Deactivated right after Show(): Windows
        // sometimes fires Deactivated if the previously-focused window reclaims
        // focus during the Show transition. Without the grace period, the just-
        // opened window would be hidden before the user ever sees it ("first
        // trigger doesn't come to top" bug). We arm the auto-close only after a
        // short delay following activation, so the initial popup is stable.
        Deactivated += (_, _) =>
        {
            if (IsBusy)
            {
                return;
            }
            if (!_autoCloseArmed)
            {
                return; // within the post-Show grace period — ignore stray deactivation
            }
            _autoCloseArmed = false;
            Hide();
            CloseRequested?.Invoke();
        };
    }

    // R54 v2 bug fix: false during the grace period right after Show(), so an
    // immediate spurious Deactivated (focus handoff from the previously-active
    // window) doesn't hide the just-opened window. Armed by a dispatcher timer
    // ~400ms after activation — long enough to ride out the Show transition,
    // short enough that a genuine "user clicked away" shortly after is still
    // caught. Volatile: written on the UI thread (timer callback), read in the
    // Deactivated handler (also UI thread, but volatile documents intent).
    private volatile bool _autoCloseArmed;

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

    /// <summary>
    /// R54 v2 bug fix: true while a translation is in flight (loading placeholder
    /// shown, or streaming chunks still arriving). The Deactivated auto-close
    /// handler checks this so it never hides the window mid-translation — the
    /// result must land somewhere visible, and a hidden window during streaming
    /// would silently drop the user's request. Once ShowResult / ShowError runs
    /// (both set LoadingBar.IsVisible = false), the window becomes eligible for
    /// auto-close on the next deactivation.
    /// </summary>
    /// <remarks>
    /// Driven solely by <see cref="LoadingBar"/> visibility: ShowLoading sets it
    /// true, and both ShowResult and ShowError set it false. Streaming providers
    /// leave LoadingBar visible throughout the stream (AppendPartialResult never
    /// touches it), so this correctly tracks busy state across both one-shot and
    /// streaming flows without depending on <c>_streamingStarted</c> (which
    /// ShowResult doesn't reset and would wrongly keep the window busy forever).
    /// </remarks>
    private bool IsBusy => LoadingBar.IsVisible;

    public void ShowLoading(TranslationRequest request, string providerName)
    {
        _translatedText = null;
        _streamingStarted = false;
        SourceTextBox.Text = request.SourceText;
        LanguagePairText.Text = FormatLanguage(request.SourceLanguage) +
            " → " + FormatLanguage(request.TargetLanguage);
        ProviderText.Text = providerName;
        ResultTextBox.Text = Strings.Result_Loading;
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
        ResultTextBox.Text = Strings.Result_EmptyResult;
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
            ErrorText.Text = Strings.Result_ClipboardAccessError;
            SetFeedbackTone(ErrorText, isError: true);
            ErrorText.IsVisible = true;
            return;
        }

        await clipboard.SetTextAsync(text);
        ErrorText.Text = Strings.Result_CopiedTranslation;
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
            ErrorText.Text = Strings.Result_ClipboardAccessError;
            SetFeedbackTone(ErrorText, isError: true);
            ErrorText.IsVisible = true;
            return;
        }

        await clipboard.SetTextAsync(text);
        ErrorText.Text = Strings.Result_CopiedSource;
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
        // R97: hold Topmost=true for the window's entire visible lifetime.
        // Pinned screenshot windows are permanently WS_EX_TOPMOST
        // (PinnedScreenshotWindow sets Topmost="True" in AXAML), so a
        // translation window that only briefly flashes Topmost and then drops
        // it (the old R54 v2 approach) sinks back below the pinned screenshot
        // the moment Topmost flips off — the translation popup ends up hidden
        // behind the pin. Holding Topmost keeps the result above the pin for
        // as long as it's shown.
        //
        // This does NOT leave a rogue topmost window on screen: the Deactivated
        // handler above auto-hides the window the instant the user clicks away
        // / Alt-Tabs (after the 400ms grace period), so the topmost state is
        // ephemeral in practice. The only case where Topmost persists through a
        // focus switch is a streaming translation (IsBusy gates auto-close),
        // which is the desired behavior — the user is waiting on that result.
        _autoCloseArmed = false; // disarm during the Show transition
        if (!IsVisible)
        {
            Show();
        }

        Topmost = true;
        Activate();
        // Topmost stays true for the window's visible lifetime — see comment
        // above. (The previous R54 v2 code dropped Topmost on the next
        // dispatch, which is exactly what let the permanently-topmost pinned
        // screenshot window cover this popup.)

        // Arm auto-close after a short grace period. The delay rides out the
        // spurious Deactivated that Windows fires during the Show/Activate
        // transition (the previously-focused window momentarily reclaims focus).
        // 400ms is long enough to cover the handoff, short enough that a genuine
        // "user clicked away" right after is still caught.
        var armTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        armTimer.Tick += (_, _) =>
        {
            armTimer.Stop();
            _autoCloseArmed = true;
        };
        armTimer.Start();
    }

    private static string FormatLanguage(string language) => language switch
    {
        "zh-CN" => Strings.Result_LangChinese,
        "en" => Strings.Result_LangEnglish,
        _ => language,
    };
}
