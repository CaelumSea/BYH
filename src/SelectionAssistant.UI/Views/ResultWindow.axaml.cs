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
    // Tracks whether the editable SourceTextBox currently holds keyboard
    // focus, so the C-key copy accelerator can be suppressed while the user
    // is typing inside it (C is both a copy shortcut and a normal letter).
    // Same pattern OcrTextWindow uses.
    private bool _sourceTextBoxFocused;

    // The display name of the action driving the current session
    // (翻译/解释/总结/自定义名), captured from TranslationRequest on each
    // ShowLoading. Null for the ad-hoc "Prompt Now" flow. When non-null, the
    // window title, loading hint, empty-result text, and copy-button label
    // all switch from the legacy "翻译" wording to action-aware wording. The
    // OnCopyClick feedback message reads the same field so "已复制译文"
    // becomes "已复制解释结果" etc.
    //
    // Retry path: TranslationSessionManager.RetryAsync reuses the original
    // request (with ActionDisplayName), so a retry goes through ShowLoading
    // again and re-captures the action name — no special-casing needed.
    private string? _actionDisplayName;

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
            _armTimer.Stop(); // Audit M13: cancel any pending arm tick on handoff
            Hide();
            CloseRequested?.Invoke();
        };

        // Audit M13: single shared timer, armed once. Tick fires the 400ms grace
        // expiry, sets the armed flag, and self-stops. Re-armament via
        // ShowAndActivate Stop()s the timer first so a rapid re-show inside the
        // grace window restarts the clock cleanly.
        _armTimer.Tick += (_, _) =>
        {
            _armTimer.Stop();
            _autoCloseArmed = true;
        };

        // Track source box focus so the C-key copy accelerator can tell
        // "C typed inside the source" apart from "C pressed elsewhere".
        SourceTextBox.GotFocus += (_, _) => _sourceTextBoxFocused = true;
        SourceTextBox.LostFocus += (_, _) => _sourceTextBoxFocused = false;
    }

    // R54 v2 bug fix: false during the grace period right after Show(), so an
    // immediate spurious Deactivated (focus handoff from the previously-active
    // window) doesn't hide the just-opened window. Armed by a dispatcher timer
    // ~400ms after activation — long enough to ride out the Show transition,
    // short enough that a genuine "user clicked away" shortly after is still
    // caught. Volatile: written on the UI thread (timer callback), read in the
    // Deactivated handler (also UI thread, but volatile documents intent).
    private volatile bool _autoCloseArmed;

    // Audit M13: the arm timer is stored as a field (was new'd per
    // ShowAndActivate call). A rapid re-show inside the 400ms grace window
    // would otherwise start a second timer; both would set _autoCloseArmed =
    // true, and neither was stopped on Hide()/Closing — so a pending tick
    // could fire after the window was hidden, leaving _autoCloseArmed true
    // for the next show (which would then auto-close on the first stray
    // Deactivated). Reusing one timer + Stop-on-Closing fixes both.
    private readonly DispatcherTimer _armTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>
    /// Raised when the user clicks "Re-run" with the current (possibly edited)
    /// contents of the source box. Arg = the trimmed source text. The runtime
    /// re-creates the translation request from this text so a fix to a
    /// mis-recognized source actually re-runs the model.
    /// </summary>
    public event Action<string>? RetryWithTextRequested;

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
        _actionDisplayName = request.ActionDisplayName;
        ApplyActionWording();
        SourceTextBox.Text = request.SourceText;
        // The "Source → Target" language pair is only meaningful for the
        // translate action (and the ad-hoc Prompt Now flow, which has no
        // action name). For explain / summarize / custom actions, the
        // direction is irrelevant — hide the badge so the header doesn't lie
        // about what the model is doing.
        if (request.ActionDisplayName is null)
        {
            LanguagePairText.IsVisible = true;
            LanguagePairText.Text = FormatLanguage(request.SourceLanguage) +
                " → " + FormatLanguage(request.TargetLanguage);
        }
        else
        {
            LanguagePairText.IsVisible = false;
        }
        ProviderText.Text = providerName;
        ResultTextBox.Text = ActionLoadingText();
        LoadingBar.IsVisible = true;
        ErrorText.IsVisible = false;
        CopyButton.IsEnabled = false;
        CopySourceButton.IsEnabled = true;  // source text is already available
        ReplaceButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        ShowAndActivate();
    }

    /// <summary>
    /// Switches the window title, content heading, and copy-button label to
    /// action-aware wording when <see cref="_actionDisplayName"/> is set, or
    /// restores the legacy "翻译" defaults when it is null. Called on every
    /// ShowLoading so a retry / new session never shows stale wording from a
    /// previous action.
    /// </summary>
    private void ApplyActionWording()
    {
        string? name = _actionDisplayName;
        if (string.IsNullOrEmpty(name))
        {
            Title = Strings.Result_WindowTitle;
            TitleText.Text = Strings.Result_Title;
            CopyButton.Content = Strings.Result_CopyTranslation;
            return;
        }
        Title = string.Format(Strings.Result_WindowTitleForAction, name);
        TitleText.Text = string.Format(Strings.Result_TitleForAction, name);
        CopyButton.Content = string.Format(Strings.Result_CopyResultForAction, name);
    }

    private string ActionLoadingText() =>
        string.IsNullOrEmpty(_actionDisplayName)
            ? Strings.Result_Loading
            : string.Format(Strings.Result_LoadingForAction, _actionDisplayName);

    private string ActionEmptyResultText() =>
        string.IsNullOrEmpty(_actionDisplayName)
            ? Strings.Result_EmptyResult
            : string.Format(Strings.Result_EmptyResultForAction, _actionDisplayName);

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
        ResultTextBox.Text = ActionEmptyResultText();
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
        ErrorText.Text = string.IsNullOrEmpty(_actionDisplayName)
            ? Strings.Result_CopiedTranslation
            : string.Format(Strings.Result_CopiedResultForAction, _actionDisplayName);
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

    private void OnRetryClick(object? sender, RoutedEventArgs eventArgs)
    {
        // Re-run uses the current contents of the source box so a user fix to
        // a mis-recognized or mistyped source actually feeds back into the
        // model. Trim to drop incidental whitespace.
        string text = SourceTextBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            // Nothing to re-run — leave the window as-is rather than firing
            // an empty request that the manager would reject anyway.
            return;
        }
        RetryWithTextRequested?.Invoke(text);
    }

    private void OnReplaceClick(object? sender, RoutedEventArgs eventArgs) =>
        ReplaceRequested?.Invoke();

    private void OnCloseClick(object? sender, RoutedEventArgs eventArgs)
    {
        Hide();
        CloseRequested?.Invoke();
    }

    // ESC closes the result window; C copies the translated result and closes
    // it (a keyboard shortcut for the Copy button). The C accelerator is
    // suppressed while the editable SourceTextBox has focus so typing the
    // letter c works normally. Unlike the WS_EX_NOACTIVATE toolbar (which
    // needs a low-level hook), this window activates normally and receives
    // Avalonia KeyDown directly.
    private void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Hide();
            CloseRequested?.Invoke();
            return;
        }

        if (eventArgs.Key == Key.C && !_sourceTextBoxFocused)
        {
            // C accelerator: copy the translated result (same path as the
            // Copy button) and close. Reuses OnCopyClick so feedback state and
            // clipboard logic stay in one place.
            eventArgs.Handled = true;
            OnCopyClick(this, new RoutedEventArgs());
            Hide();
            CloseRequested?.Invoke();
        }
    }

    /// <summary>
    /// Handles key presses while the editable SourceTextBox has focus. Esc
    /// closes the window from anywhere (matching OnWindowKeyDown); Enter with
    /// Ctrl is treated as "re-run with the edited source" (Ctrl so plain
    /// Enter still inserts a newline in the multi-line source). Plain C is
    /// left alone — it types the letter c (the C copy accelerator is gated on
    /// <see cref="_sourceTextBoxFocused"/> in OnWindowKeyDown).
    /// </summary>
    private void OnSourceTextBoxKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Hide();
            CloseRequested?.Invoke();
            return;
        }

        // Ctrl+Enter = re-run with the (possibly edited) source. Ctrl-free
        // Enter inserts a newline so multi-line paste/edit still works.
        if (eventArgs.Key == Key.Enter &&
            (eventArgs.KeyModifiers & KeyModifiers.Control) != 0)
        {
            eventArgs.Handled = true;
            OnRetryClick(this, new RoutedEventArgs());
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
        // Default to "view mode": the window activates but the editable
        // SourceTextBox must NOT auto-receive focus. Avalonia gives focus to the
        // first focusable control in tab order when a window activates, which
        // would put SourceTextBox into edit mode and select its text — the user
        // asked for the popup to open in a read/inspect state where a stray
        // keystroke can't wipe the source, and where C copies+close works
        // immediately. We defer placing focus on CopySourceButton (always
        // enabled, a non-edit control) to AFTER Avalonia's auto-focus pass, so
        // _sourceTextBoxFocused stays false until the user clicks the source.
        Dispatcher.Post(() => CopySourceButton.Focus(), DispatcherPriority.Input);
        // Topmost stays true for the window's visible lifetime — see comment
        // above. (The previous R54 v2 code dropped Topmost on the next
        // dispatch, which is exactly what let the permanently-topmost pinned
        // screenshot window cover this popup.)

        // Arm auto-close after a short grace period. The delay rides out the
        // spurious Deactivated that Windows fires during the Show/Activate
        // transition (the previously-focused window momentarily reclaims focus).
        // 400ms is long enough to cover the handoff, short enough that a genuine
        // "user clicked away" right after is still caught.
        // Audit M13: reuse the shared _armTimer (Stop cancels a pending tick if
        // ShowAndActivate is called again inside the grace window).
        _armTimer.Stop();
        _armTimer.Start();
    }

    private static string FormatLanguage(string language) => language switch
    {
        "zh-CN" => Strings.Result_LangChinese,
        "en" => Strings.Result_LangEnglish,
        _ => language,
    };
}
