using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SelectionAssistant.Core.I18n;
using SelectionAssistant.Core.Translation;
using SelectionAssistant.Platform.Abstractions;
using System.Collections.ObjectModel;

namespace SelectionAssistant.UI.Views;

public partial class ToolbarWindow : Window
{
    private string? _capturedText;
    private bool _allowClose;
    private bool _moreExpanded;
    private readonly ObservableCollection<PromptFunctionRow> _actionRows = [];

    public ToolbarWindow()
    {
        InitializeComponent();
        MoreActionsList.ItemsSource = _actionRows;
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

    public nint? NativeHandle => TryGetPlatformHandle()?.Handle;

    public event Action<string>? TranslateRequested;

    /// <summary>Raised when the user clicks "Prompt" with captured text selected.</summary>
    public event Action<string>? PromptRequested;

    /// <summary>Raised when the user clicks "Speak" with captured text — the
    /// runtime synthesizes via MiniMax T2A and plays it back. Audio runs in the
    /// background; the toolbar stays visible so the user can re-click to restart
    /// or trigger other actions while audio plays.</summary>
    public event Action<string>? SpeakRequested;

    /// <summary>
    /// Raised to run a built-in action (summarize/explain) using the global
    /// prompt template for that action. Arg = (actionId, selectedText).
    /// </summary>
    public event Action<string, string>? ActionRequested;

    public void PrepareForShutdown() => _allowClose = true;

    /// <summary>
    /// Pushes the current custom functions (user-added custom-* only; the three
    /// built-ins are hardcoded as buttons in the main row). Each becomes a
    /// button in the expandable "more" row. The "▼" toggle is shown only when
    /// there is at least one custom function.
    /// </summary>
    public void SetActions(IReadOnlyList<PromptTemplate> templates)
    {
        _actionRows.Clear();
        foreach (PromptTemplate t in templates)
        {
            // Skip the three built-ins — they have dedicated buttons in the main row.
            if (PromptActionIds.IsBuiltIn(t.Id))
            {
                continue;
            }
            string actionId = t.Id;
            _actionRows.Add(new PromptFunctionRow
            {
                Id = actionId,
                Name = t.Name.Current(actionId),
                RunCommand = new RelayCommand(() => RunAction(actionId)),
            });
        }
        MoreButton.IsVisible = _actionRows.Count > 0;
    }

    public void ShowPending(SelectionGesture gesture)
    {
        _capturedText = null;
        // Collapse the "more" row on each fresh selection so the toolbar starts compact.
        SetMoreExpanded(false);
        // R42: restore button visibility (Ocean Eyes mode hides them).
        TranslateButton.IsVisible = true;
        ExplainButton.IsVisible = true;
        SummarizeButton.IsVisible = true;
        PromptButton.IsVisible = true;
        CopyButton.IsVisible = true;
        SpeakButton.IsVisible = true;
        OceanEyesSignature.IsVisible = false;
        TranslateButton.IsEnabled = false;
        ExplainButton.IsEnabled = false;
        SummarizeButton.IsEnabled = false;
        PromptButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        SpeakButton.IsEnabled = false;
        StatusText.Text = string.Format(Strings.Toolbar_StatusCapturing, gesture.MouseUpX, gesture.MouseUpY);
        // Pending/diagnostic states show StatusText; hide the wordmark so the
        // user only sees the "byh" art text once capture actually succeeded.
        StatusText.IsVisible = true;
        WordmarkImage.IsVisible = false;
    }

    /// <summary>
    /// R42: Ocean Eyes mode — hide all action buttons (user triggers via
    /// keyboard shortcuts F/J/Z/R/C) and show the signature image instead
    /// of the wordmark/status area.
    /// </summary>
    public void SetOceanEyesSignatureMode()
    {
        TranslateButton.IsVisible = false;
        ExplainButton.IsVisible = false;
        SummarizeButton.IsVisible = false;
        PromptButton.IsVisible = false;
        CopyButton.IsVisible = false;
        SpeakButton.IsVisible = false;
        MoreButton.IsVisible = false;
        SetMoreExpanded(false);

        WordmarkImage.IsVisible = false;
        StatusText.IsVisible = true;
        OceanEyesSignature.IsVisible = true;
    }

    /// <summary>
    /// Computes the toolbar's final window top-left (in physical screen
    /// pixels) for an anchor at the selection's bottom-right corner, so the
    /// toolbar stays fully inside the screen's working area.
    ///
    /// Strategy (mirrors SpotlightWindow.ClampToScreen):
    /// 1. Default placement: window top-left = (anchor + 16, anchor + 16).
    /// 2. If it overflows the right/bottom edge, flip the toolbar to the
    ///    opposite side of the anchor (mirrors how context menus place
    ///    themselves), so the anchor point stays visible.
    /// 3. Clamp the resulting top-left to the working area origin so the
    ///    toolbar never sits under the taskbar or in a monitor gap.
    ///
    /// The toolbar is <c>SizeToContent="WidthAndHeight"</c>, so on the very
    /// first show <see cref="Bounds"/> may be 0×0 (Avalonia hasn't measured
    /// yet). Fall back to a conservative width/height estimate in that case;
    /// subsequent shows (the window is reused, not recreated) read the real
    /// measured size.
    /// </summary>
    public PixelPoint ClampAnchor(int x, int y)
    {
        const double Offset = 16;
        // Conservative fallback for the very first show before Avalonia
        // has measured the SizeToContent window.
        const double FallbackWidth = 460;
        const double FallbackHeight = 40;

        var screen = Screens.ScreenFromPoint(new PixelPoint(x, y));
        if (screen is null)
        {
            // No screen info — keep the legacy +Offset placement.
            return new PixelPoint((int)(x + Offset), (int)(y + Offset));
        }

        PixelRect work = screen.WorkingArea;
        double width = Bounds.Width > 0 ? Bounds.Width : FallbackWidth;
        double height = Bounds.Height > 0 ? Bounds.Height : FallbackHeight;

        // Default placement below-right of the anchor.
        double left = x + Offset;
        double top = y + Offset;

        // Flip to the opposite side of the anchor if the default placement
        // would overflow the right/bottom edge.
        if (left + width > work.Right)
        {
            left = x - Offset - width;
        }
        if (top + height > work.Bottom)
        {
            top = y - Offset - height;
        }

        // Clamp the top-left to the working area origin so the toolbar never
        // sits under the taskbar or in a monitor gap.
        left = Math.Max(left, work.X);
        top = Math.Max(top, work.Y);
        // If clamping to the top-left still overflows (toolbar taller/wider
        // than the work area — very rare), pull back so the top-left corner
        // stays visible.
        if (left + width > work.Right)
        {
            left = Math.Max(work.X, work.Right - width);
        }
        if (top + height > work.Bottom)
        {
            top = Math.Max(work.Y, work.Bottom - height);
        }

        return new PixelPoint((int)left, (int)top);
    }

    public void SetCaptureResult(CaptureResult result)
    {
        _capturedText = string.IsNullOrWhiteSpace(result.Text) ? null : result.Text.Trim();
        // Translate / explain / summarize / prompt / copy / speak all need captured text;
        // toggle them together for a consistent enabled state. Paste is always
        // enabled (it acts on the clipboard + source app, not the captured text).
        bool enabled = _capturedText is not null;
        TranslateButton.IsEnabled = enabled;
        ExplainButton.IsEnabled = enabled;
        SummarizeButton.IsEnabled = enabled;
        PromptButton.IsEnabled = enabled;
        CopyButton.IsEnabled = enabled;
        SpeakButton.IsEnabled = enabled;
        StatusText.Text = _capturedText is not null
            ? string.Format(Strings.Toolbar_StatusCaptured, result.Source)
            : result.Source == CaptureSource.ManualFallback
                ? Strings.Toolbar_StatusNeedManualCopy
                : Strings.Toolbar_StatusEmpty;
        // R37/R39: 取词成功时切到透明底 "By Your Hand" 艺术字 wordmark（替代
        // "已取词 · Accessibility" 这种诊断式文本——品牌字更有"已就绪"的语义且
        // 符合品牌统一目标）；其它状态（需要手动复制 / 暂未取到 / 取词中）仍显示
        // StatusText 诊断文字。
        bool captured = _capturedText is not null;
        WordmarkImage.IsVisible = captured;
        StatusText.IsVisible = !captured;
    }

    public void SetDiagnosticStatus(string status)
    {
        StatusText.Text = status;
        StatusText.IsVisible = true;
        WordmarkImage.IsVisible = false;
    }

    private void OnTranslateClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_capturedText is { Length: > 0 } text)
        {
            TranslateRequested?.Invoke(text);
        }
    }

    private void OnPromptClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_capturedText is { Length: > 0 } text)
        {
            PromptRequested?.Invoke(text);
        }
    }

    /// <summary>Runs the "explain" action via the global prompt template.</summary>
    private void OnExplainClick(object? sender, RoutedEventArgs eventArgs) =>
        RunAction("explain");

    /// <summary>Runs the "summarize" action via the global prompt template.</summary>
    private void OnSummarizeClick(object? sender, RoutedEventArgs eventArgs) =>
        RunAction("summarize");

    /// <summary>
    /// Raises ActionRequested for a built-in action (handled by the runtime,
    /// which resolves the current global prompt template for that action).
    /// </summary>
    private void RunAction(string actionId)
    {
        if (_capturedText is { Length: > 0 } text)
        {
            ActionRequested?.Invoke(actionId, text);
        }
    }

    /// <summary>Copies the captured selection to the clipboard, then hides.</summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_capturedText is not { Length: > 0 } text)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>Raises <see cref="SpeakRequested"/> for the captured text.</summary>
    private void OnSpeakClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_capturedText is { Length: > 0 } text)
        {
            SpeakRequested?.Invoke(text);
        }
    }

    // ── Speak (TTS) playback status feedback ──────────────────────────────
    // The toolbar stays visible during playback (unlike translate/prompt which
    // hide it), so the user can re-click Speak to restart. These three methods
    // swap StatusText/wordmark visibility to surface speaking/error state, then
    // revert to the captured-wordmark state on success. Save + restore the
    // prior StatusText so a transient speak error doesn't wipe a pending
    // "已取词" label.

    private bool _speakingStateActive;
    private string? _preSpeakingStatusText;
    private bool _preSpeakingWordmarkVisible;

    /// <summary>Shows "朗读中…" status while synthesis/playback is active.</summary>
    public void StartSpeaking()
    {
        if (_speakingStateActive)
        {
            return;
        }
        _speakingStateActive = true;
        _preSpeakingStatusText = StatusText.Text;
        _preSpeakingWordmarkVisible = WordmarkImage.IsVisible;
        StatusText.Text = Strings.Toolbar_StatusSpeaking;
        StatusText.IsVisible = true;
        WordmarkImage.IsVisible = false;
    }

    /// <summary>Reverts to the pre-speaking status (called on successful playback end).</summary>
    public void StopSpeaking()
    {
        if (!_speakingStateActive)
        {
            return;
        }
        _speakingStateActive = false;
        StatusText.Text = _preSpeakingStatusText ?? string.Empty;
        StatusText.IsVisible = !_preSpeakingWordmarkVisible;
        WordmarkImage.IsVisible = _preSpeakingWordmarkVisible;
    }

    /// <summary>Shows a transient speak-failure status. Auto-reverts after a delay.</summary>
    public void SpeakFailed(string message)
    {
        if (!_speakingStateActive)
        {
            return;
        }
        _speakingStateActive = false;
        StatusText.Text = string.Format(Strings.Toolbar_StatusSpeakFailed, message);
        StatusText.IsVisible = true;
        WordmarkImage.IsVisible = false;
    }

    // ── R37/R41: 工具栏内建单字符快捷键入口（R/C）──────────────────────
    // 由 SelectionRuntime.OnToolbarKeyPressed 在工具栏可见、用户配置快捷键未命中
    // 时调用。两个方法各自转发到现有按钮处理器，行为与点击按钮完全一致；不直接
    // 处理鼠标事件参数（传 null + Empty）。两者都需要已取词。
    // R41: V（粘贴）入口已删除——Ocean Eyes 流程不再有粘贴动作。

    /// <summary>R = Prompt（打开提示词窗口）。</summary>
    public bool InvokePromptShortcut()
    {
        if (_capturedText is not { Length: > 0 })
        {
            return false;
        }
        OnPromptClick(this, new RoutedEventArgs());
        return true;
    }

    /// <summary>C = 复制选中文本到剪贴板。</summary>
    public bool InvokeCopyShortcut()
    {
        if (_capturedText is not { Length: > 0 })
        {
            return false;
        }
        OnCopyClick(this, new RoutedEventArgs());
        return true;
    }

    /// <summary>S = 朗读选中文本（MiniMax TTS）。Unlike Copy, does not hide the
    /// toolbar — audio is background; the user may re-trigger or act again.</summary>
    public bool InvokeSpeakShortcut()
    {
        if (_capturedText is not { Length: > 0 })
        {
            return false;
        }
        OnSpeakClick(this, new RoutedEventArgs());
        return true;
    }

    /// <summary>Toggles the expandable custom-functions row on/off.</summary>
    private void OnMoreClick(object? sender, RoutedEventArgs eventArgs)
    {
        SetMoreExpanded(!_moreExpanded);
    }

    /// <summary>
    /// Shows or hides the "more" row and flips the toggle arrow. The toolbar
    /// uses SizeToContent=Height so it grows/shrinks automatically.
    /// </summary>
    private void SetMoreExpanded(bool expanded)
    {
        _moreExpanded = expanded;
        MoreActionsList.IsVisible = expanded;
        MoreButton.Content = expanded ? "▲" : "▼";
    }
}
