using System.Runtime.InteropServices;
using SelectionAssistant.Core.Annotation; // NumberedBadge, NumberedBadgeGeometry, MagneticSnapCalculator, PhysicalRect
using SelectionAssistant.Core.Capture;
using SelectionAssistant.Core.I18n;
using SelectionAssistant.Core.Input;
using SelectionAssistant.Core.Launcher;
using SelectionAssistant.Core.Selection;
using SelectionAssistant.Core.Translation;
using SelectionAssistant.Infrastructure.Capture;
using SelectionAssistant.Infrastructure.Configuration;
using SelectionAssistant.Infrastructure.Logging;
using SelectionAssistant.Infrastructure.Translation;
using SelectionAssistant.Platform.Abstractions;
using SelectionAssistant.Platform.Abstractions.Secrets;
using SelectionAssistant.Platform.Windows;
using SelectionAssistant.Platform.Windows.Clipboard;
using SelectionAssistant.Platform.Windows.Capture;
using SelectionAssistant.Platform.Windows.Hooks;
using SelectionAssistant.Platform.Windows.Launcher;
using SelectionAssistant.Platform.Windows.Secrets;
using SelectionAssistant.Platform.Windows.Windowing;
using SelectionAssistant.Providers;
using SelectionAssistant.UI.Views;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace SelectionAssistant.App;

/// <summary>Windows composition root for the Phase 1 selection path.</summary>
internal sealed class SelectionRuntime : IDisposable
{
    private const nuint OurInjectedInputMarker = 0x53454C41;

    private readonly object _taskGate = new();
    private readonly HashSet<Task> _sessionTasks = [];
    private readonly ToolbarWindow _toolbarWindow;
    private readonly ResultWindow _resultWindow;
    private readonly RedactedLogger _logger;
    private readonly LowLevelMouseHook _mouseHook;
    // R34: low-level keyboard hook active only while the toolbar is visible.
    // The toolbar is WS_EX_NOACTIVATE and cannot receive Avalonia KeyDown, so
    // single-character shortcuts (F/J/Z/R + any user-bound custom action) are
    // dispatched through this hook instead. Start() on show, Stop() on hide.
    private readonly LowLevelKeyboardHook _keyboardHook;
    private readonly WindowsMouseContextProvider _contextProvider = new();
    private readonly SystemMetricGestureClassifier _gestureClassifier;
    private readonly ChordDetector _chordDetector = new();
    private readonly SelectionSessionManager _sessionManager;
    private readonly IProcessCapturePolicyProvider _capturePolicyProvider;
    private readonly TranslationSessionManager _translationManager;  // null until assigned in ctor
    private ITranslationProvider _translationProvider = new MyMemoryTranslationProvider();  // mutable for hot-swap, set by SwitchToProvider
    private IDisposable? _disposableProvider;           // mutable for hot-swap
    private readonly ISecretStore _secretStore;
    private string? _apiKeyReference;                   // mutable for hot-swap
    private string _providerLabel = "未配置";            // mutable for hot-swap, set by SwitchToProvider
    private readonly ByhApplicationPaths _paths;
    private readonly MutableProviderConfiguration _providerConfig;
    private PromptTemplateSet _promptTemplates;  // global templates, loaded at start
    // R23 launcher entries (quick-launch software/URLs). User-added only.
    private LauncherEntrySet _launcherEntries;
    private readonly WindowsSelectionTextCapture _textCapture;
    private readonly NoActivateWindowHost _windowHost;
    // R24 track B: vision OCR tier (screenshot → cloud OCR). Owned here so the
    // backend + OCR client share one disposal point. Null until configured.
    // _visionOcrClient is typed as IVisionOcrClient (not IDisposable) so the
    // region-select OCR path can call RecognizeAsync; the client is also
    // IDisposable (interface inherits) and disposed in ApplyVisionCapture/Dispose.
    private WindowsUiAutomationBackend? _visionBackend;
    private IVisionOcrClient? _visionOcrClient;
    private VisionCaptureSettings _visionSettings = VisionCaptureSettings.Default;
    // R37/R41: user-configurable toolbar built-in shortcut keys (Prompt/Copy,
    // defaults R/C). Mutable for hot-swap from the settings page. Read on the
    // keyboard hook thread by TryInvokeBuiltinToolbarShortcut — assignments from
    // the UI thread are reference-atomic (reference swap), so no lock needed.
    private ToolbarShortcutSettings _toolbarShortcuts = ToolbarShortcutSettings.Default;
    private int _mouseChordEnabled;
    private int _disposed;

    // R40 Ocean Eyes: when the user completes a region box, we capture the PNG
    // bytes once (before showing the toolbar so the toolbar isn't in the shot),
    // then show the SAME ToolbarWindow that the selection flow uses — the
    // F/J/Z/R/C shortcuts work unchanged. The only new key is Enter (save the
    // cached PNG), gated by _oceanEyesActive so the selection flow's Enter
    // still passes through to the source app.
    //
    // R41: OCR is now LAZY — the toolbar shows immediately in "未识别" state
    // with all action buttons disabled; OCR only fires when the user presses
    // F/J/Z/R/C. The OCR task is cached so subsequent action keys on the same
    // region reuse the result (no re-OCR).
    //
    // _oceanEyesActive is read on the keyboard hook thread (Enter handler) and
    // written from the UI thread (FeedOceanEyesCapture / Esc); Volatile for
    // cross-thread visibility without a lock.
    private int _oceanEyesActive;
    // PNG captured for the current Ocean Eyes session. Cached so Enter can save
    // immediately without re-capturing (which would also pick up the toolbar).
    // nulled out when the Ocean Eyes toolbar is dismissed (Esc/Enter/action).
    private byte[]? _oceanEyesPng;
    /// <summary>R48: raw BGRA pixel buffer for the captured region. Used by
    /// annotation burn-in to bypass the Avalonia 12 Bitmap.CopyPixels stride
    /// bug. Null when no Ocean Eyes session is active.</summary>
    private byte[]? _oceanEyesBgra;
    // R41: the screen rect (physical px) of the current Ocean Eyes region. Kept
    // so EnsureOceanEyesOcrAsync can (re)start OCR without App re-passing it.
    private (int X, int Y, int W, int H) _oceanEyesRect;
    // R41: the lazy OCR task. Null = OCR not yet started; non-null = started
    // (maybe still running, maybe completed). Same Task is awaited by every
    // action key so OCR runs at most once per region. Nulled on Dismiss/Reset.
    private Task<string?>? _oceanEyesOcrTask;
    // R41: the OCR result once the task completes. Null until then. Read on the
    // hook thread to decide "cached text ready" vs "must await task".
    private string? _oceanEyesOcrText;
    private int _oceanEyesOcrDone; // Volatile flag: 0 = not done, 1 = done
    // R54 v2 bug fix: true while the selection/Ocean Eyes toolbar is actually
    // visible on screen. Set in the onToolbarShown / ShowToolbarForOceanEyes
    // paths; cleared in onToolbarHidden / DismissOceanEyes. Read on the keyboard
    // hook thread to gate action-key dispatch (F/J/Z/R/C): without this check,
    // pressing F would fall through to DispatchToolbarActionKey →
    // GetLastCapturedText (which has a clipboard fallback) → wrongly trigger a
    // translation. Only a visible toolbar gives F/J/Z their action semantics.
    // (R97: the hook is no longer kept armed while pins exist — Esc no longer
    // closes pinned windows — so the only time the hook is armed is while the
    // toolbar / Ocean Eyes is active, which is exactly when this flag is true.)
    // volatile: written on the UI thread, read on the WH_KEYBOARD_LL hook thread.
    private volatile bool _toolbarVisible;
    // User-configurable: where screenshots go + whether to auto-save / copy.
    // Reference-atomic swap from the UI thread; read by SaveOceanEyesScreenshot.
    private OceanEyesCaptureSettings _oceanEyesCapture = OceanEyesCaptureSettings.Default;
    // R44 color picker loupe. Lazily constructed on first P press (so the cost
    // is paid only when the feature is used). The HWND is wrapped in a
    // NoActivateWindowHost so the loupe never steals focus from the keyboard
    // hook. Owned by the runtime; disposed in Dispose().
    private ColorPickerLoupe? _colorPickerLoupe;
    private NoActivateWindowHost? _loupeHost;
    // R44: 1 while the loupe is active (between P-on and pick/Esc). Volatile —
    // read on the keyboard + mouse hook threads, written from the UI thread.
    private int _colorPickerActive;
    // R46: pinned screenshot windows (one per T press during an Ocean Eyes
    // session). Decoupled from the Ocean Eyes lifecycle — closing the toolbar
    // (Esc/Enter/action) does NOT close pinned windows; only runtime Dispose
    // tears them down. The HWNDs are wrapped in NoActivateWindowHost so they
    // stay always-on-top without stealing focus.
    // R97: closing a pinned window is via double-click or right-click context
    // menu only (handled inside PinnedScreenshotWindow). Esc no longer closes
    // pins — the global keyboard hook is NOT kept armed while pins exist.
    private readonly List<PinnedScreenshotWindow> _pinnedWindows = new();
    private readonly List<NoActivateWindowHost> _pinnedHosts = new();

    // R49: screenshot gallery window. Singleton (G while one is already open
    // just activates it). Set back to null in its Closed handler so a fresh
    // G press creates a new window. Torn down on runtime Dispose.
    private GalleryWindow? _galleryWindow;

    // R47: numbered badge annotation mode. _oceanEyesAnnotating is 1 while
    // the user is placing badges (between A-on and A-off/Esc). Volatile —
    // read on the keyboard + mouse hook threads, written from the UI thread.
    // _annotationSession holds the badge list + undo stack; created on A-on,
    // cleared on dismiss. _annotationOverlay is the RegionSelectOverlay whose
    // AnnotationCanvas we draw badges on; set by App.axaml.cs.
    private int _oceanEyesAnnotating;
    private AnnotationSession? _annotationSession;
    private RegionSelectOverlay? _annotationOverlay;

    // R48: annotation tool state. _currentAnnotationTool is set by 0-5 keys.
    // _annotationDragging is true while the user is dragging a shape.
    // _annotationDragStart is the physical screen px where the drag started.
    // _annotationDragPoints accumulates points for pen/highlight strokes.
    private AnnotationTool _currentAnnotationTool = AnnotationTool.Number;
    private bool _annotationDragging;
    private (double X, double Y) _annotationDragStart;
    private List<(double X, double Y)> _annotationDragPoints = new();

    public SelectionRuntime(
        ToolbarWindow toolbarWindow,
        ResultWindow resultWindow,
        ByhApplicationPaths paths)
    {
        _toolbarWindow = toolbarWindow;
        _resultWindow = resultWindow;
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _logger = new RedactedLogger(paths.LogFile);
        nint windowHandle = toolbarWindow.NativeHandle
            ?? throw new InvalidOperationException("Toolbar HWND is not available after Opened.");

        _windowHost = new NoActivateWindowHost(windowHandle);
        _gestureClassifier = new SystemMetricGestureClassifier(new WindowsSystemMetrics());
        _mouseHook = new LowLevelMouseHook(message => _logger.Info("MouseHook", message));
        _keyboardHook = new LowLevelKeyboardHook(message => _logger.Info("KeyboardHook", message));
        _keyboardHook.KeyPressed += OnToolbarKeyPressed;
        // NOTE: the keyboard hook is NOT installed here in the ctor. Starting
        // its background message-loop thread at ctor time (before the clipboard
        // message window is created) caused intermittent "Clipboard message
        // window startup timed out" crashes — the second Win32 thread racing
        // on window-class registration / desktop attach appears to disrupt the
        // main thread's clipboard window creation. We install it lazily in
        // Start() instead, after the mouse hook is already running and the
        // runtime is otherwise ready.

        var dispatcher = new AvaloniaSelectionUiDispatcher();
        var view = new ToolbarSessionView(
            toolbarWindow,
            _windowHost,
            onToolbarShown: () =>
            {
                // Enable shortcut dispatching while the toolbar is visible.
                // The hook itself stays installed for the whole app lifetime;
                // this only flips a flag read inside the callback.
                _toolbarVisible = true; // R54 v2 bug fix: gate action-key dispatch
                _keyboardHook.SetEnabled(true);
            },
            onToolbarHidden: () =>
            {
                // Disable dispatching so typing in the source app is not
                // filtered while the toolbar is hidden.
                // R97: the hook is unconditionally disabled here. Esc no longer
                // closes pinned windows (removed), so there's no reason to keep
                // the global hook armed while pins exist — it would only
                // intercept keystrokes the user intended for the focused app.
                _toolbarVisible = false; // R54 v2 bug fix
                _keyboardHook.SetEnabled(false);
            });
        IReadOnlyList<PolicyRule> userPolicyRules = LoadUserCapturePolicies(paths.CapturePolicyFile);
        _capturePolicyProvider = WindowsDefaultCapturePolicies.CreateProvider(userPolicyRules);
        _textCapture = new WindowsSelectionTextCapture(_capturePolicyProvider);
        _sessionManager = new SelectionSessionManager(
            _textCapture,
            view,
            dispatcher,
            diagnosticSink: message => _logger.Info("Capture", message));

        _secretStore = new DpapiSecretStore(paths.SecretsDirectory);
        _providerConfig = LoadProviderConfig(paths);
        _promptTemplates = LoadPromptTemplates(paths);
        _launcherEntries = LoadLauncherEntries(paths);
        SwitchToProvider(ResolveDefaultEntry(), logOnMiss: true);
        _translationManager = new TranslationSessionManager(
            _translationProvider!,
            new ResultTranslationView(resultWindow),
            dispatcher);

        _toolbarWindow.TranslateRequested += OnTranslateRequested;
        _toolbarWindow.ActionRequested += (actionId, text) => RunActionAsync(actionId, text);
        _resultWindow.RetryRequested += OnRetryRequested;
        _resultWindow.ReplaceRequested += OnReplaceRequested;
        _resultWindow.CloseRequested += OnResultCloseRequested;

        // Chord (left+right button together) → quick-tools panel. The detector
        // fires on the mouse-hook thread; ChordTriggered surfaces it so App.axaml
        // can marshal to the UI thread and open the Ocean Eyes region overlay.
        _chordDetector.ChordDetected += (x, y) =>
        {
            if (Volatile.Read(ref _mouseChordEnabled) != 0)
            {
                ChordTriggered?.Invoke(x, y);
            }
        };

        // R24 track B: wire the vision OCR tier (screenshot → cloud OCR) from
        // vision.json. Loads settings, resolves the OCR provider entry, and
        // injects the capture into _textCapture. Non-fatal if anything is
        // missing — the tier just stays disabled (UIA + clipboard only).
        ConfigureVisionCapture();
    }

    /// <summary>
    /// R24 track B: loads <c>vision.json</c>, resolves the OCR provider entry
    /// from providers.json, builds the OCR client + screenshot backend, and
    /// injects the vision tier into the text capture. Safe to call with no
    /// provider configured (tier stays disabled). Called once at construction.
    /// </summary>
    private void ConfigureVisionCapture()
    {
        try
        {
            _visionSettings = VisionCaptureStore.LoadIfExists(_paths.VisionCaptureFile);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("VisionCapture", "vision.json rejected; vision tier disabled.", exception);
            _visionSettings = VisionCaptureSettings.Default with { Enabled = false };
        }

        ApplyVisionCapture();
    }

    /// <summary>
    /// (Re)builds and injects the vision tier from <see cref="_visionSettings" />.
    /// Disposes the previous backend/OCR client first. No-op (tier disabled) when
    /// the setting is off or the OCR provider entry can't be resolved.
    /// </summary>
    private void ApplyVisionCapture()
    {
        // Tear down any previous vision wiring.
        _textCapture.SetVisionCapture(null);
        _textCapture.SetVisionEnabled(false);
        _visionOcrClient?.Dispose();
        _visionOcrClient = null;
        _visionBackend?.Dispose();
        _visionBackend = null;

        if (!_visionSettings.Enabled)
        {
            return;
        }

        ProviderProfileEntry? entry = _providerConfig.FindById(_visionSettings.ProviderId);
        if (entry is null)
        {
            _logger.Info(
                "VisionCapture",
                $"Vision tier disabled: provider '{_visionSettings.ProviderId}' not found in providers.json.");
            return;
        }

        var options = new OpenAiCompatibleProviderOptions
        {
            Id = entry.Id,
            DisplayName = entry.Name,
            BaseUrl = entry.BaseUrl,
            ApiKeyReference = entry.ApiKeyReference,
            // Override the provider's default model with the configured OCR model
            // (e.g. Qwen/Qwen3.5-4B), keeping the translation model intact.
            DefaultModel = _visionSettings.Model,
            ChatCompletionsPath = entry.ChatCompletionsPath,
            Timeout = entry.TimeoutSeconds < 30 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(entry.TimeoutSeconds),
            MaxSourceCharacters = entry.MaxSourceCharacters,
        };

        var ocrClient = new OpenAiCompatibleVisionOcrClient(
            options, _secretStore, _visionSettings.OcrPrompt, _visionSettings.DisableThinking);
        _visionOcrClient = ocrClient;
        _visionBackend = new WindowsUiAutomationBackend();
        var visionCapture = new VisionTextCapture(_visionBackend, ocrClient);
        _textCapture.SetVisionCapture(visionCapture);
        _textCapture.SetVisionEnabled(true);

        _logger.Info(
            "VisionCapture",
            $"Vision OCR tier enabled: provider='{entry.Id}' model='{_visionSettings.Model}'.");
    }

    /// <summary>
    /// Raised when a left+right mouse chord is detected. Fires on the mouse-hook
    /// thread — the handler must marshal to the UI thread. Args = screen coords.
    /// </summary>
    public event Action<int, int>? ChordTriggered;

    /// <summary>
    /// R42: callback set by App.axaml.cs to close the region-select overlay
    /// when the Ocean Eyes session is dismissed (Esc / Enter / action key).
    /// Called at the end of <see cref="DismissOceanEyes"/> on the UI thread.
    /// </summary>
    public Action? DismissOverlay { get; set; }

    /// <summary>
    /// R47: the RegionSelectOverlay whose AnnotationCanvas receives numbered
    /// badges. Set by App.axaml.cs when the overlay is created. Null when no
    /// overlay exists (non-Ocean-Eyes sessions).
    /// </summary>
    public RegionSelectOverlay? AnnotationOverlay
    {
        get => _annotationOverlay;
        set => _annotationOverlay = value;
    }

    /// <summary>
    /// Enables the legacy left+right mouse chord. Disabled by default because
    /// the right-button half conflicts with source-application context menus.
    /// The detector still observes release events while disabled so its latch
    /// cannot become stuck across a live setting change.
    /// </summary>
    public void SetMouseChordEnabled(bool enabled) =>
        Volatile.Write(ref _mouseChordEnabled, enabled ? 1 : 0);

    /// <summary>
    /// R37: replaces the user-configurable toolbar built-in shortcut keys
    /// (Prompt/Copy). Applied immediately — the next key press on the
    /// toolbar will use the new bindings. Reference swap is atomic, so this is
    /// safe to call from the UI thread while the keyboard hook thread is
    /// reading <see cref="_toolbarShortcuts"/>.
    /// </summary>
    public void SetToolbarShortcuts(ToolbarShortcutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _toolbarShortcuts = settings.Normalize();
    }

    // ── R40 Ocean Eyes: region-select → toolbar → OCR → F/J/Z/R/C/V + Enter ──

    /// <summary>
    /// Pushes the Ocean Eyes screenshot/save settings from the settings UI.
    /// Applied immediately — the next Enter (save) will use the new path /
    /// toggles. Reference swap is atomic, safe from the UI thread.
    /// </summary>
    public void SetOceanEyesCaptureSettings(OceanEyesCaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _oceanEyesCapture = settings.Normalize();
    }

    /// <summary>Read-only accessor for App.axaml.cs (UIA assist toggle).</summary>
    public OceanEyesCaptureSettings GetOceanEyesCaptureSettings() => _oceanEyesCapture;

    /// <summary>
    /// R40: shows the toolbar at the Ocean Eyes anchor (region's right edge,
    /// top of the box) and arms the Enter key for saving the cached PNG. The
    /// toolbar is the SAME ToolbarWindow used by the selection flow — its
    /// F/J/Z/R/C/V shortcuts work unchanged once OCR text is fed in via
    /// <see cref="FeedOceanEyesCapture"/>. Must be called on the UI thread.
    /// </summary>
    /// <param name="regionRightX">Right edge of the drawn region (physical px).</param>
    /// <param name="regionTopY">Top edge of the drawn region (physical px).</param>
    /// <param name="png">Pre-captured PNG bytes (captured before this call so
    /// the toolbar window isn't in the shot). Cached for Enter-to-save.</param>
    public void ShowToolbarForOceanEyes(
        int regionRightX, int regionTopY, byte[] png, byte[] bgra,
        int regionX, int regionY, int regionW, int regionH)
    {
        ArgumentNullException.ThrowIfNull(png);
        _logger.Info("Usage", "module=OceanEyes feature=RegionCapture");

        // Cache the PNG + rect first so Enter (save) and EnsureOceanEyesOcrAsync
        // (lazy OCR on first action key) can use them without re-capturing.
        _oceanEyesPng = png;
        // R48: cache the raw BGRA buffer for annotation burn-in (avoids the
        // Avalonia 12 Bitmap.CopyPixels stride bug — see BurnAnnotationsIntoPng).
        _oceanEyesBgra = bgra;
        _oceanEyesRect = (regionX, regionY, regionW, regionH);
        // R41: lazy OCR — do NOT start the OCR task here. It starts on the first
        // F/J/Z/R/C press via EnsureOceanEyesOcrAsync.
        _oceanEyesOcrTask = null;
        _oceanEyesOcrText = null;
        Volatile.Write(ref _oceanEyesOcrDone, 0);

        // Put the toolbar in "未识别" state: all action buttons disabled (the
        // user hasn't triggered OCR yet), status tells them what to press.
        // ShowPending gives us the disabled-buttons state; we then override
        // the status text to the lazy-OCR prompt.
        _toolbarWindow.ShowPending(new SelectionGesture(
            MouseUpX: regionRightX, MouseUpY: regionTopY,
            MouseDownX: regionRightX, MouseDownY: regionTopY,
            MouseDownTimestampMs: 0, MouseUpTimestampMs: 0,
            SourceRootHwnd: 0, SourceProcessId: 0));
        _toolbarWindow.SetDiagnosticStatus(Strings.Runtime_Status_Unrecognized);
        // R42: Ocean Eyes mode — hide buttons, show signature.
        _toolbarWindow.SetOceanEyesSignatureMode();

        // Anchor at the region's top-right corner. ClampAnchor's flip logic
        // (mirrors selection flow) keeps the toolbar inside the working area:
        // if the top-right anchor overflows the right edge, the toolbar flips
        // to the left of the box; same for the top edge.
        Avalonia.PixelPoint topLeft = _toolbarWindow.ClampAnchor(regionRightX, regionTopY);
        _windowHost.ShowAtNoActivatePoint(topLeft.X, topLeft.Y);

        // Arm the Ocean Eyes flag + the keyboard hook so Enter / F / J / Z /
        // R / C all route through OnToolbarKeyPressed.
        Volatile.Write(ref _oceanEyesActive, 1);
        _toolbarVisible = true; // R54 v2 bug fix: gate action-key dispatch
        _keyboardHook.SetEnabled(true);
    }

    /// <summary>
    /// R41: lazily starts (or re-awaits) the OCR task for the current Ocean
    /// Eyes region. Called on the first F/J/Z/R/C press. The OCR runs at most
    /// once per region — subsequent action keys await the same task and reuse
    /// the cached text. When the task completes, the toolbar's action buttons
    /// are enabled and the status switches from "未识别" to "已取词 · Vision".
    /// </summary>
    /// <returns>The OCR'd text (trimmed), or null/empty if OCR failed.</returns>
    private async Task<string?> EnsureOceanEyesOcrAsync()
    {
        // Snapshot the task under a null-check. Two action keys arriving in
        // quick succession could both see null and both start a task — but the
        // hook thread is single-threaded (one key at a time), so this race is
        // impossible in practice. Belt-and-suspenders: Interlocked compare.
        Task<string?>? task = _oceanEyesOcrTask;
        if (task is null)
        {
            var rect = _oceanEyesRect;
            task = CaptureAndRecognizeRegionAsync(rect.X, rect.Y, rect.W, rect.H, CancellationToken.None);
            _oceanEyesOcrTask = task;
            _logger.Info("OceanEyes", $"Lazy OCR started for region {rect.W}x{rect.H}.");
        }

        string? text;
        try
        {
            text = await task.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("OceanEyes", "Lazy OCR task failed.", exception);
            text = null;
        }

        // Cache + arm only if still active (user may have Esc'd during OCR).
        if (Volatile.Read(ref _oceanEyesActive) == 0)
        {
            return text;
        }

        _oceanEyesOcrText = text;
        Volatile.Write(ref _oceanEyesOcrDone, 1);
        FeedOceanEyesCapture(text);
        return text;
    }

    /// <summary>
    /// R40/R41: feeds the OCR'd text into the existing toolbar pipeline. Called
    /// by <see cref="EnsureOceanEyesOcrAsync"/> when the lazy OCR completes.
    /// Enables the action buttons and lets F/J/Z/R/C all act on the recognized
    /// text via the unchanged RunActionAsync / TryInvokeBuiltinToolbarShortcut
    /// paths.
    /// </summary>
    public void FeedOceanEyesCapture(string? ocrText)
    {
        // If the user already dismissed the toolbar (Esc) before OCR landed,
        // the OCR result is dropped — there's no visible toolbar to update.
        if (Volatile.Read(ref _oceanEyesActive) == 0)
        {
            _oceanEyesPng = null;
            _oceanEyesBgra = null;
            return;
        }

        string text = ocrText?.Trim() ?? string.Empty;
        var result = new CaptureResult(
            string.IsNullOrEmpty(text) ? null : text,
            CaptureSource.Vision,
            IsAmbiguous: false);

        // R42 fix: called from EnsureOceanEyesOcrAsync which runs on a
        // thread-pool thread (Task.Run from the hook thread). Toolbar UI
        // operations must run on the Avalonia UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (Volatile.Read(ref _oceanEyesActive) == 0)
                {
                    return; // dismissed while we were queuing
                }
                _toolbarWindow.SetCaptureResult(result);
                _sessionManager.SeedLastCapturedText(result);
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "FeedOceanEyesCapture failed to update toolbar.", exception);
            }
        });
    }

    /// <summary>
    /// R40: writes the cached PNG to <c>SavePath/ocean-eyes-yyyyMMdd-HHmmss.png</c>
    /// (if AutoSaveEnabled) and copies it to the clipboard (if
    /// CopyToClipboardEnabled). Then hides the toolbar and clears the Ocean Eyes
    /// state. Called from OnToolbarKeyPressed's Enter branch on the keyboard
    /// hook thread → all UI work dispatched via Dispatcher.UIThread.Post.
    /// </summary>
    private void SaveOceanEyesScreenshot()
    {
        byte[]? png = _oceanEyesPng;
        var settings = _oceanEyesCapture;
        if (png is null)
        {
            // Nothing captured (shouldn't happen — flag is only set when png is).
            return;
        }

        // R48: snapshot annotations before marshaling to UI thread.
        // The session may be cleared by DismissOceanEyes before the Post runs.
        IReadOnlyList<IAnnotationItem> annotations =
            _annotationSession?.Items ?? Array.Empty<IAnnotationItem>();

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // R48: burn annotations into PNG before saving. DPI scale is read
                // on the UI thread (RenderScaling is a UI property).
                double dpiScale = _annotationOverlay?.RenderScaling ?? 1.0;
                // Annotation DIP coords are screen-absolute (the overlay canvas
                // covers the whole screen). The PNG is just the captured region,
                // so we must subtract the region's top-left screen DIP to get
                // PNG-local DIP, then multiply by dpiScale for PNG pixels.
                // _oceanEyesRect is in physical px, so convert back to DIP.
                double originXDip = _oceanEyesRect.X / dpiScale;
                double originYDip = _oceanEyesRect.Y / dpiScale;
                // R48: pass the raw BGRA buffer if we have it (captured alongside
                // the PNG). This bypasses Avalonia 12's Bitmap.CopyPixels which
                // throws ArgumentOutOfRangeException('stride') on some PNGs.
                // If BGRA is null (older capture path), fall back to PNG decode.
                byte[] finalPng = BurnAnnotationsIntoPng(
                    png, _oceanEyesBgra, _oceanEyesRect.W, _oceanEyesRect.H,
                    annotations, dpiScale, originXDip, originYDip,
                    out byte[]? finalBgra);

                if (settings.AutoSaveEnabled)
                {
                    string? directory = settings.SavePath;
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        string file = Path.Combine(directory, $"ocean-eyes-{stamp}.png");
                        File.WriteAllBytes(file, finalPng);
                        _logger.Info("OceanEyes", $"Saved screenshot: {file}");
                    }
                }

                if (settings.CopyToClipboardEnabled)
                {
                    byte[] clipPng = finalPng;
                    // R54 v2 bug fix: convert to CF_DIB so EVERY image consumer
                    // sees the screenshot. Previously SetPng wrote only the
                    // registered "PNG" format, which BYH's own clipboard history
                    // (reads CF_DIB) and most Windows apps (Word/Paint/chat) ignore
                    // — that was the "history doesn't capture Ocean Eyes screenshots"
                    // bug. SetImageDibAndPng writes both formats atomically.
                    //
                    // CRITICAL: build the DIB from finalBgra (the raw/annotated
                    // BGRA buffer) via ConvertBgraToDib — NOT by decoding the PNG.
                    // Avalonia 12's Bitmap.CopyPixels throws
                    // ArgumentOutOfRangeException('stride') on many PNGs (the very
                    // bug that crashed SaveOceanEyesScreenshot here before). The
                    // BGRA path is pure byte-array math (BuildDibFromBgra), zero
                    // Avalonia dependency, zero stride risk. Falls back to PNG
                    // decoding only if finalBgra is null (capture path didn't
                    // provide it), which may fail — that's the degraded case.
                    int dibW = _oceanEyesRect.W, dibH = _oceanEyesRect.H;
                    byte[]? clipDib = finalBgra is not null
                        ? PngToDibConverter.ConvertBgraToDib(finalBgra, dibW, dibH)
                        : PngToDibConverter.ConvertPngToDib(clipPng);
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            using var clipboard = new Win32Clipboard();
                            clipboard.SetImageDibAndPng(clipPng, clipDib);
                            string dibInfo = clipDib is null ? " (DIB convert failed, PNG only)" : $", {clipDib.Length} DIB";
                            _logger.Info("OceanEyes", $"Copied {clipPng.Length} PNG bytes{dibInfo} to clipboard.");
                        }
                        catch (Exception exception)
                        {
                            _logger.Error("OceanEyes", "Clipboard copy failed.", exception);
                        }
                    });
                }
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "SaveOceanEyesScreenshot failed.", exception);
            }
            finally
            {
                // Always dismiss the toolbar after Enter — saving is a terminal
                // action (mirrors how F/J/Z hide the toolbar after RunActionAsync).
                DismissOceanEyes();
            }
        });
    }

    /// <summary>
    /// R40: hides the toolbar, disables the hook, and clears the Ocean Eyes
    /// state. Called after Enter (save done) or Esc (cancel). Safe to call
    /// multiple times (idempotent).
    /// </summary>
    private void DismissOceanEyes()
    {
        // R44: dismiss the color picker too (it can't outlive the Ocean Eyes
        // session). HideColorPicker is idempotent and safe to call from any
        // thread — it clears the Volatile flag synchronously and marshals the
        // window Hide to the UI thread.
        HideColorPicker();

        // R47: exit annotation mode + clear session + clear overlay badges.
        Volatile.Write(ref _oceanEyesAnnotating, 0);
        _annotationSession?.Clear();
        _annotationSession = null;

        // Clear flags immediately (thread-safe Volatile writes) so any
        // concurrent hook callback sees the inactive state right away.
        Volatile.Write(ref _oceanEyesActive, 0);
        _toolbarVisible = false; // R54 v2 bug fix
        _oceanEyesPng = null;
        _oceanEyesBgra = null;
        // R41: clear lazy-OCR state so the next Ocean Eyes session starts fresh.
        _oceanEyesOcrTask = null;
        _oceanEyesOcrText = null;
        Volatile.Write(ref _oceanEyesOcrDone, 0);
        // Disable the hook immediately (Volatile.Write, thread-safe) so no
        // second keypress can sneak in before the UI work below runs.
        // R97: unconditional. Esc no longer closes pinned windows, so nothing
        // in the hook callback needs the hook to stay armed after the toolbar
        // is dismissed — keeping it armed would only intercept keys meant for
        // the focused app.
        _keyboardHook.SetEnabled(false);

        // R42 fix: this method is called from the keyboard hook callback
        // (hook thread) via OnToolbarKeyPressed. UI operations (_windowHost,
        // DismissOverlay → _regionOverlay) must run on the Avalonia UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            // R48: clear all annotation visuals from the overlay.
            _annotationOverlay?.ClearAnnotations();
            _windowHost.Hide();
            // R42: tell App.axaml.cs to close the region-select overlay.
            DismissOverlay?.Invoke();
        });
    }

    // ── R44 color picker ──────────────────────────────────────────────────

    /// <summary>
    /// R44: lazily constructs the loupe (first use), wraps its HWND with a
    /// NoActivateWindowHost, arms the active flag, and tells the loupe to
    /// start sampling. Must run on the UI thread (touches Avalonia controls
    /// + creates the host). Caller (<see cref="OnToolbarKeyPressed"/>) is on
    /// the keyboard hook thread, so this method marshals itself onto the UI
    /// thread.
    /// </para>
    /// <para>
    /// The sampler closure reads <see cref="GetCursorPos"/> + BitBlts a 15×15
    /// region via <see cref="ScreenRegionCapture.CaptureRawBgra"/>. Both run
    /// on the UI thread (DispatcherTimer callback) at ~30 Hz.
    /// </para>
    /// </summary>
    private void StartColorPicker()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Reject if the user pressed P in the tiny window before the prior
            // DismissOceanEyes's UI-thread Post ran.
            if (Volatile.Read(ref _oceanEyesActive) == 0)
            {
                return;
            }

            if (_colorPickerLoupe is null)
            {
                _colorPickerLoupe = new ColorPickerLoupe();
                // Construct the host once and reuse — NoActivateWindowHost
                // applies WS_EX_NOACTIVATE | WS_EX_TOPMOST on first show.
                nint handle = _colorPickerLoupe.NativeHandle
                    ?? throw new InvalidOperationException(
                        "Color picker HWND is not available after construction.");
                _loupeHost = new NoActivateWindowHost(handle);
            }

            Volatile.Write(ref _colorPickerActive, 1);
            _colorPickerLoupe.Show(SampleCursorRegion, OnColorPicked);
        });
    }

    /// <summary>
    /// R44: sampler closure handed to <see cref="ColorPickerLoupe.Show"/>. Reads
    /// the physical cursor position via <see cref="GetCursorPos"/> and BitBlts a
    /// 15×15 region centered on it. Returns (cursorX, cursorY, bgra) where bgra
    /// is null on capture failure. Runs on the UI thread (DispatcherTimer tick).
    /// </summary>
    private (int CursorX, int CursorY, byte[]? Bgra15x15) SampleCursorRegion()
    {
        if (!GetCursorPos(out NativePoint pt))
        {
            return (0, 0, null);
        }
        // Center the 15×15 box on the cursor so the center pixel == cursor pixel.
        const int half = 15 / 2;
        byte[]? bgra = ScreenRegionCapture.CaptureRawBgra(pt.X - half, pt.Y - half, 15, 15);
        return (pt.X, pt.Y, bgra);
    }

    /// <summary>
    /// R44: callback invoked by the loupe when the user confirms a pick (left-
    /// click or loupe direct click). Copies <c>#RRGGBB</c> to the clipboard and
    /// flashes the toolbar status line with the value. Runs on the UI thread.
    /// </summary>
    private void OnColorPicked(byte r, byte g, byte b)
    {
        Volatile.Write(ref _colorPickerActive, 0);
        string hex = ColorFormatter.ToHexRgb(r, g, b);
        _logger.Info("OceanEyes", $"Color picked: {hex}.");
        try
        {
            var clipboard = (_toolbarWindow as Avalonia.Controls.TopLevel)?.Clipboard;
            if (clipboard is not null)
            {
                _ = clipboard.SetTextAsync(hex);
            }
        }
        catch (Exception exception)
        {
            _logger.Error("OceanEyes", "Color picker clipboard copy failed.", exception);
        }
        // Surface the value in the toolbar status slot so the user sees a copy
        // confirmation without a separate toast subsystem (none exists today).
        _toolbarWindow.SetDiagnosticStatus(string.Format(Strings.Runtime_Status_CopiedColor, hex));
    }

    /// <summary>
    /// R44: cancels the loupe without firing onPicked. Idempotent — safe from
    /// any thread. Clears the active flag synchronously and marshals the Hide
    /// to the UI thread (Avalonia controls can't be touched off-thread).
    /// </summary>
    private void HideColorPicker()
    {
        Volatile.Write(ref _colorPickerActive, 0);
        Dispatcher.UIThread.Post(() => _colorPickerLoupe?.HideLoupe());
    }

    // ── R47 numbered badge annotation ──────────────────────────────────

    /// <summary>
    /// R47: enters annotation mode. Creates a new session, arms the
    /// annotation flag, and updates the toolbar status. Called from
    /// OnToolbarKeyPressed on the keyboard hook thread; UI ops marshaled.
    /// R48: also resets the current tool to Number (default).
    /// </summary>
    private void EnterAnnotationMode()
    {
        _annotationSession = new AnnotationSession();
        _currentAnnotationTool = AnnotationTool.Number;
        Volatile.Write(ref _oceanEyesAnnotating, 1);
        Dispatcher.UIThread.Post(() =>
        {
            _toolbarWindow.SetDiagnosticStatus(
                string.Format(Strings.Runtime_Status_AnnotateIntro, Strings.Runtime_Annotation_ToolHint));
        });
    }

    /// <summary>
    /// R47: exits annotation mode (badges stay on overlay). Clears the
    /// active flag and restores the toolbar status. Idempotent — safe from
    /// any thread. Does NOT clear the session or overlay badges (they
    /// persist until Ocean Eyes is dismissed or Enter saves).
    /// </summary>
    private void ExitAnnotationMode()
    {
        Volatile.Write(ref _oceanEyesAnnotating, 0);
        _annotationDragging = false;
        Dispatcher.UIThread.Post(() =>
        {
            _annotationOverlay?.RemoveLivePreview();
            _toolbarWindow.SetDiagnosticStatus(Strings.Runtime_Status_AnnotateExited);
        });
    }

    // ── R48 live preview helpers (must run on UI thread) ──────────────

    /// <summary>
    /// Finalizes the live preview: removes the preview, creates the final
    /// IAnnotationItem, pushes to session, and adds the final visual.
    /// </summary>
    private void FinalizeLivePreviewShape(double dipX, double dipY)
    {
        _annotationOverlay?.RemoveLivePreview();

        if (_annotationSession is not { } session ||
            Volatile.Read(ref _oceanEyesAnnotating) == 0)
        {
            return;
        }

        bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
        double startX = _annotationDragStart.X;
        double startY = _annotationDragStart.Y;

        IAnnotationItem? item = _currentAnnotationTool switch
        {
            AnnotationTool.Rectangle =>
                AnnotationShapeGeometry.NormalizeRect(startX, startY, dipX, dipY) is { } r
                    ? (shift ? AnnotationShapeGeometry.ApplyShiftConstraint(r, true) : r)
                    : null,
            AnnotationTool.Ellipse =>
                AnnotationShapeGeometry.NormalizeEllipse(startX, startY, dipX, dipY) is { } e
                    ? (shift ? AnnotationShapeGeometry.ApplyShiftConstraint(e, true) : e)
                    : null,
            AnnotationTool.Arrow => new ArrowAnnotation(startX, startY, dipX, dipY),
            AnnotationTool.Pen => new PenStrokeAnnotation(_annotationDragPoints.ToArray()),
            AnnotationTool.Highlight => new HighlightStrokeAnnotation(_annotationDragPoints.ToArray()),
            _ => null,
        };

        if (item is not null)
        {
            session.Push(item);
            _annotationOverlay?.AddShape(item);
            _logger.Info("OceanEyes",
                $"Annotation: finalized {_currentAnnotationTool} at ({dipX:F1}, {dipY:F1}).");
        }

        _annotationDragPoints.Clear();
    }

    /// <summary>
    /// R47/R48: burns all annotations (badges + shapes) into the PNG byte array.
    /// Draws directly onto the BGRA pixel buffer (no SkiaSharp dependency).
    /// Returns the modified PNG bytes.
    /// </summary>
    private static byte[] BurnAnnotationsIntoPng(
        byte[]? png,
        byte[]? rawBgra,
        int regionW,
        int regionH,
        IReadOnlyList<IAnnotationItem> items,
        double dpiScale,
        double originXDip,
        double originYDip,
        out byte[]? finalBgra)
    {
        // R54 v2 bug fix: out param returns the annotation-burned BGRA buffer
        // (top-down, width×height×4) so the caller can build a CF_DIB directly
        // from it — bypassing Avalonia's Bitmap.CopyPixels stride bug that
        // throws on many PNGs. Null when there are no annotations AND no raw
        // BGRA was provided (caller must then decode the PNG itself, which may
        // fail). When items.Count == 0 but rawBgra is present, we still return
        // it (cheap clone) so the DIB path works for annotation-free captures.
        finalBgra = null;
        if (png is null)
        {
            return Array.Empty<byte>();
        }
        if (items.Count == 0)
        {
            // R54 v2 bug fix: even with no annotations, surface the raw BGRA so
            // the clipboard path can build a DIB without PNG decoding. Cheap
            // clone (no drawing happens); the caller treats null as "must decode
            // PNG, may fail".
            if (rawBgra is { Length: > 0 } && regionW > 0 && regionH > 0
                && rawBgra.Length == regionW * regionH * 4)
            {
                finalBgra = (byte[])rawBgra.Clone();
            }
            return png;
        }

        // Prefer the raw BGRA buffer captured alongside the PNG (R40+ capture
        // path always produces it). This bypasses Avalonia 12's
        // Bitmap.CopyPixels, which throws ArgumentOutOfRangeException('stride')
        // for many PNGs — without this, burn-in silently returned the original
        // PNG and saved screenshots had no annotations.
        byte[] bgra;
        int width, height;
        if (rawBgra is { Length: > 0 } && regionW > 0 && regionH > 0
            && rawBgra.Length == regionW * regionH * 4)
        {
            // Clone: drawing annotations mutates the buffer; we must not
            // modify the cached _oceanEyesBgra or subsequent saves would
            // stack annotations on top of already-burned-in ones.
            bgra = (byte[])rawBgra.Clone();
            width = regionW;
            height = regionH;
        }
        else
        {
            // Fallback: decode PNG → BGRA (may fail on Avalonia 12).
            byte[]? decoded = DecodePngToBgra(png, out width, out height);
            if (decoded is null || width <= 0 || height <= 0)
            {
                return png; // decode failed — return original (finalBgra stays null)
            }
            bgra = decoded;
        }

        // Draw each annotation onto the BGRA buffer.
        foreach (IAnnotationItem item in items)
        {
            switch (item)
            {
                case NumberedBadgeAnnotation badge:
                {
                    // Subtract region origin (screen DIP) before scaling to PNG px.
                    var nb = new NumberedBadge(badge.Number, badge.X - originXDip, badge.Y - originYDip);
                    (double cx, double cy) = NumberedBadgeGeometry.GetPhysicalCenter(nb, dpiScale);
                    double radius = NumberedBadgeGeometry.GetRadius(dpiScale);
                    DrawCircleOnBgra(bgra, width, height, (int)cx, (int)cy, (int)radius,
                        0xAA, 0xC2, 0xD9, 0xFF); // BGRA for #FFD9C28A (gold)
                    DrawCircleStrokeOnBgra(bgra, width, height, (int)cx, (int)cy, (int)radius,
                        0x95, 0xB8, 0xFF, 0xFF); // BGRA for #FFB8956A (dark gold stroke)
                    DrawDigitOnBgra(bgra, width, height, (int)cx, (int)cy, (int)radius,
                        badge.Number);
                    break;
                }
                case RectangleAnnotation rect:
                {
                    int left = (int)((rect.Left - originXDip) * dpiScale);
                    int top = (int)((rect.Top - originYDip) * dpiScale);
                    int right = (int)((rect.Left + rect.Width - originXDip) * dpiScale);
                    int bottom = (int)((rect.Top + rect.Height - originYDip) * dpiScale);
                    int thickness = (int)(AnnotationShapeGeometry.StrokeThicknessDip * dpiScale);
                    BurnInHelpers.DrawRectangleStrokeOnBgra(bgra, width, height,
                        left, top, right, bottom, thickness,
                        AnnotationShapeGeometry.GoldB, AnnotationShapeGeometry.GoldG,
                        AnnotationShapeGeometry.GoldR, AnnotationShapeGeometry.GoldA);
                    break;
                }
                case EllipseAnnotation ellipse:
                {
                    int cx = (int)((ellipse.Left + ellipse.Width / 2 - originXDip) * dpiScale);
                    int cy = (int)((ellipse.Top + ellipse.Height / 2 - originYDip) * dpiScale);
                    int rx = (int)((ellipse.Width / 2) * dpiScale);
                    int ry = (int)((ellipse.Height / 2) * dpiScale);
                    int thickness = (int)(AnnotationShapeGeometry.StrokeThicknessDip * dpiScale);
                    BurnInHelpers.DrawEllipseStrokeOnBgra(bgra, width, height,
                        cx, cy, rx, ry, thickness,
                        AnnotationShapeGeometry.GoldB, AnnotationShapeGeometry.GoldG,
                        AnnotationShapeGeometry.GoldR, AnnotationShapeGeometry.GoldA);
                    break;
                }
                case ArrowAnnotation arrow:
                {
                    int sx = (int)((arrow.StartX - originXDip) * dpiScale);
                    int sy = (int)((arrow.StartY - originYDip) * dpiScale);
                    int ex = (int)((arrow.EndX - originXDip) * dpiScale);
                    int ey = (int)((arrow.EndY - originYDip) * dpiScale);
                    int thickness = (int)(AnnotationShapeGeometry.StrokeThicknessDip * dpiScale);
                    BurnInHelpers.DrawArrowOnBgra(bgra, width, height,
                        sx, sy, ex, ey, thickness,
                        AnnotationShapeGeometry.GoldB, AnnotationShapeGeometry.GoldG,
                        AnnotationShapeGeometry.GoldR, AnnotationShapeGeometry.GoldA);
                    break;
                }
                case PenStrokeAnnotation pen:
                {
                    var scaledPoints = new List<(double X, double Y)>(pen.Points.Count);
                    foreach (var (x, y) in pen.Points)
                    {
                        scaledPoints.Add(((x - originXDip) * dpiScale, (y - originYDip) * dpiScale));
                    }
                    int thickness = (int)(AnnotationShapeGeometry.StrokeThicknessDip * dpiScale);
                    BurnInHelpers.DrawPathOnBgra(bgra, width, height,
                        scaledPoints, thickness,
                        AnnotationShapeGeometry.GoldB, AnnotationShapeGeometry.GoldG,
                        AnnotationShapeGeometry.GoldR, AnnotationShapeGeometry.GoldA);
                    break;
                }
                case HighlightStrokeAnnotation highlight:
                {
                    var scaledPoints = new List<(double X, double Y)>(highlight.Points.Count);
                    foreach (var (x, y) in highlight.Points)
                    {
                        scaledPoints.Add(((x - originXDip) * dpiScale, (y - originYDip) * dpiScale));
                    }
                    int thickness = (int)(AnnotationShapeGeometry.HighlightThicknessDip * dpiScale);
                    BurnInHelpers.DrawPathOnBgra(bgra, width, height,
                        scaledPoints, thickness,
                        AnnotationShapeGeometry.HighlightB, AnnotationShapeGeometry.HighlightG,
                        AnnotationShapeGeometry.HighlightR, AnnotationShapeGeometry.HighlightA);
                    break;
                }
            }
        }

        // Re-encode to PNG. R54 v2 bug fix: surface the burned BGRA so the
        // caller can build a CF_DIB without PNG decoding (Avalonia stride bug).
        finalBgra = bgra;
        return ScreenRegionCapture.EncodeBgraToPng(bgra, width, height);
    }

    /// <summary>
    /// Decodes a PNG byte array to a BGRA pixel buffer using Avalonia Bitmap.
    /// Returns null on failure.
    /// </summary>
    private static byte[]? DecodePngToBgra(byte[] png, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var stream = new MemoryStream(png);
            using var bmp = new Bitmap(stream);
            var pixelSize = bmp.PixelSize;
            width = pixelSize.Width;
            height = pixelSize.Height;
            if (width <= 0 || height <= 0) return null;

            int stride = width * 4;
            int totalBytes = width * height * 4;
            var bgra = new byte[totalBytes];
            nint nativeBuffer = Marshal.AllocHGlobal(totalBytes);
            try
            {
                bmp.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), nativeBuffer, stride, 0);
                Marshal.Copy(nativeBuffer, bgra, 0, totalBytes);
            }
            finally
            {
                Marshal.FreeHGlobal(nativeBuffer);
            }
            return bgra;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Draws a filled circle on a BGRA pixel buffer.
    /// </summary>
    private static void DrawCircleOnBgra(
        byte[] bgra, int imgW, int imgH,
        int cx, int cy, int radius,
        byte b, byte g, byte r, byte a)
    {
        int left = Math.Max(0, cx - radius);
        int top = Math.Max(0, cy - radius);
        int right = Math.Min(imgW - 1, cx + radius);
        int bottom = Math.Min(imgH - 1, cy + radius);
        int r2 = radius * radius;

        for (int y = top; y <= bottom; y++)
        {
            int dy = y - cy;
            int dy2 = dy * dy;
            int rowOffset = y * imgW * 4;
            for (int x = left; x <= right; x++)
            {
                int dx = x - cx;
                if (dx * dx + dy2 <= r2)
                {
                    int off = rowOffset + x * 4;
                    // Alpha-blend: src over dst.
                    float srcA = a / 255f;
                    float dstA = bgra[off + 3] / 255f;
                    float outA = srcA + dstA * (1 - srcA);
                    if (outA > 0)
                    {
                        bgra[off] = (byte)((b * srcA + bgra[off] * dstA * (1 - srcA)) / outA);
                        bgra[off + 1] = (byte)((g * srcA + bgra[off + 1] * dstA * (1 - srcA)) / outA);
                        bgra[off + 2] = (byte)((r * srcA + bgra[off + 2] * dstA * (1 - srcA)) / outA);
                        bgra[off + 3] = (byte)(outA * 255);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Draws a 1px circle stroke on a BGRA pixel buffer.
    /// </summary>
    private static void DrawCircleStrokeOnBgra(
        byte[] bgra, int imgW, int imgH,
        int cx, int cy, int radius,
        byte b, byte g, byte r, byte a)
    {
        int left = Math.Max(0, cx - radius - 1);
        int top = Math.Max(0, cy - radius - 1);
        int right = Math.Min(imgW - 1, cx + radius + 1);
        int bottom = Math.Min(imgH - 1, cy + radius + 1);
        int rOuter2 = (radius + 1) * (radius + 1);
        int rInner2 = Math.Max(0, radius - 1) * Math.Max(0, radius - 1);

        for (int y = top; y <= bottom; y++)
        {
            int dy = y - cy;
            int dy2 = dy * dy;
            int rowOffset = y * imgW * 4;
            for (int x = left; x <= right; x++)
            {
                int dx = x - cx;
                int dist2 = dx * dx + dy2;
                if (dist2 <= rOuter2 && dist2 >= rInner2)
                {
                    int off = rowOffset + x * 4;
                    float srcA = a / 255f;
                    float dstA = bgra[off + 3] / 255f;
                    float outA = srcA + dstA * (1 - srcA);
                    if (outA > 0)
                    {
                        bgra[off] = (byte)((b * srcA + bgra[off] * dstA * (1 - srcA)) / outA);
                        bgra[off + 1] = (byte)((g * srcA + bgra[off + 1] * dstA * (1 - srcA)) / outA);
                        bgra[off + 2] = (byte)((r * srcA + bgra[off + 2] * dstA * (1 - srcA)) / outA);
                        bgra[off + 3] = (byte)(outA * 255);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Draws a centered digit (1-99) on a BGRA pixel buffer using a built-in
    /// 5×7 bitmap font. White color, bold appearance via 2× scaling.
    /// </summary>
    private static void DrawDigitOnBgra(
        byte[] bgra, int imgW, int imgH,
        int cx, int cy, int radius, int number)
    {
        string text = number.ToString();
        // Each glyph is 5 wide × 7 tall; scale factor 2 → 10×14 per glyph.
        const int glyphW = 5;
        const int glyphH = 7;
        const int scale = 2;
        int charSpacing = 1; // extra pixels between chars at scale 1
        int totalWidth = text.Length * (glyphW * scale + charSpacing * scale) - charSpacing * scale;
        int totalHeight = glyphH * scale;
        int startX = cx - totalWidth / 2;
        int startY = cy - totalHeight / 2;

        foreach (char ch in text)
        {
            int digit = ch - '0';
            if (digit < 0 || digit > 9) continue;

            ReadOnlySpan<byte> glyph = GetGlyph(digit);
            for (int gy = 0; gy < glyphH; gy++)
            {
                byte row = glyph[gy];
                for (int gx = 0; gx < glyphW; gx++)
                {
                    if ((row & (1 << (4 - gx))) != 0)
                    {
                        // Draw scaled pixel.
                        for (int sy = 0; sy < scale; sy++)
                        {
                            for (int sx = 0; sx < scale; sx++)
                            {
                                int px = startX + gx * scale + sx;
                                int py = startY + gy * scale + sy;
                                if (px >= 0 && px < imgW && py >= 0 && py < imgH)
                                {
                                    int off = (py * imgW + px) * 4;
                                    bgra[off] = 0xFF;     // B (white)
                                    bgra[off + 1] = 0xFF; // G
                                    bgra[off + 2] = 0xFF; // R
                                    bgra[off + 3] = 0xFF; // A
                                }
                            }
                        }
                    }
                }
            }
            startX += (glyphW + charSpacing) * scale;
        }
    }

    /// <summary>
    /// Returns the 5×7 bitmap font glyph for a digit (0-9).
    /// Each row is 5 bits wide (MSB = leftmost pixel).
    /// </summary>
    private static ReadOnlySpan<byte> GetGlyph(int digit) => digit switch
    {
        0 => [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        1 => [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        2 => [0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111],
        3 => [0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110],
        4 => [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        5 => [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110],
        6 => [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
        7 => [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        8 => [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        9 => [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100],
        _ => [0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
    };

    /// <summary>
    /// R46: pins the currently-cached Ocean Eyes PNG as a new always-on-top
    /// floating window at the region's top-left corner. The pinned window
    /// lives independently of the Ocean Eyes session — Esc/Enter/action keys
    /// that dismiss the toolbar do NOT close it. Only runtime Dispose tears
    /// down pinned windows (or the window's own close button / right-click
    /// menu). Multiple windows can coexist (one per T press).
    /// <para>
    /// Caller (<see cref="OnToolbarKeyPressed"/>) is on the keyboard hook
    /// thread, so this method marshals itself onto the UI thread.
    /// </para>
    /// </summary>
    private void PinOceanEyesScreenshot()
    {
        // Capture the PNG + anchor on the hook thread so the UI-thread Post
        // sees a stable snapshot even if the Ocean Eyes session is dismissed
        // in the tiny window before the Post runs.
        byte[]? png = _oceanEyesPng;
        if (png is null || png.Length == 0)
        {
            _logger.Info("OceanEyes", "Pin screenshot: no cached PNG, ignoring.");
            return;
        }
        int anchorX = _oceanEyesRect.X;
        int anchorY = _oceanEyesRect.Y;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var window = new PinnedScreenshotWindow();
                nint handle = window.NativeHandle
                    ?? throw new InvalidOperationException(
                        "Pinned screenshot HWND is not available after construction.");
                var host = new NoActivateWindowHost(handle);

                // Wire the three context-menu / close-button events. Each
                // closure captures `window` so the handlers can identify which
                // pinned instance raised them.
                window.RequestCopy += () => CopyPinnedToClipboard(window);
                window.RequestClose += () => ClosePinned(window);
                window.RequestCloseAll += CloseAllPinned;

                // R52: inject magnetic-snap bounds callback. Returns the
                // physical-pixel rects of all OTHER pinned windows (excluding
                // this one) so the snap calculator can align edges.
                window.GetOtherPinnedBounds = () =>
                {
                    var result = new List<PhysicalRect>();
                    foreach (var w in _pinnedWindows)
                    {
                        if (ReferenceEquals(w, window)) continue;
                        // R55: report the peer's IMAGE rect (window rect inset by
                        // the shadow margin), not its window rect, so two pinned
                        // windows snap image-edge to image-edge instead of leaving
                        // a full shadow-margin gap between them.
                        result.Add(w.ImagePhysicalRect);
                    }
                    return result;
                };

                // Decode + show before positioning — SizeToContent=WidthAndHeight
                // needs the image loaded to compute bounds.
                window.ShowPng(png);

                // Place at the region's top-left (with a small offset so the
                // pinned window doesn't perfectly overlap the original area).
                const int offset = 16;
                host.ShowAtNoActivatePoint(anchorX + offset, anchorY + offset);

                _pinnedWindows.Add(window);
                _pinnedHosts.Add(host);

                _logger.Info("OceanEyes", $"Pinned screenshot ({png.Length} bytes) at +{offset} from region.");
                // R46 v5: T now dismisses the toolbar, so no status-slot update
                // (no one would see it). The pinned window itself is the
                // feedback that the action succeeded.
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "Pin screenshot spawn failed.", exception);
            }
        });
    }

    /// <summary>
    /// R49: opens the screenshot gallery. Browses the user's full
    /// <c>ocean-eyes-*.png</c> history in <c>_oceanEyesCapture.SavePath</c>,
    /// independent of the current Ocean Eyes session. Does NOT dismiss
    /// Ocean Eyes — same pattern as Pin (T), the user can close the gallery
    /// and keep working in the active session. Singleton: a second G press
    /// while the gallery is already visible just activates the existing
    /// window.
    /// <para>
    /// Public so the tray-icon menu (App layer) can call it without an Ocean
    /// Eyes session active — the user might want to browse history without
    /// first pressing Ctrl+Alt+Q. SavePath is read from settings, not from
    /// the active session, so this works cold-start too.
    /// </para>
    /// </summary>
    public void ShowGallery()
    {
        // Snapshot the save path on the hook thread so the UI lambda is
        // immune to settings changes racing in between.
        string savePath = string.IsNullOrEmpty(_oceanEyesCapture.SavePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "Ocean Eyes")
            : _oceanEyesCapture.Normalize().SavePath;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_galleryWindow is { } existing && existing.IsVisible)
                {
                    existing.Activate();
                    return;
                }
                _galleryWindow?.Close();
                var window = new GalleryWindow(savePath, _logger);
                // Gallery→runtime callbacks: the UI layer can't reach
                // Win32Clipboard (no Platform.Windows ref), so it hands the
                // path back here for the actual clipboard set + logging.
                window.RequestCopy += path => CopyGalleryEntryToClipboard(path);
                window.RequestDelete += path =>
                    _logger.Info("OceanEyes", $"Gallery: user deleted {path}");
                window.RequestReveal += path => RevealGalleryEntryInExplorer(path);
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_galleryWindow, window))
                    {
                        _galleryWindow = null;
                    }
                };
                window.Show();
                _galleryWindow = window;
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "Gallery spawn failed.", exception);
            }
        });
    }

    /// <summary>
    /// R49: gallery "double-click to copy" handler. Reads the PNG from disk
    /// and forwards it to the same Win32Clipboard.SetPng path used by
    /// Enter-to-save and Copy-from-pinned. Background thread — clipboard
    /// I/O must not block the UI thread.
    /// </summary>
    private void CopyGalleryEntryToClipboard(string filePath)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _logger.Info("OceanEyes", $"Gallery copy: file vanished: {filePath}");
                    return;
                }
                byte[] png = File.ReadAllBytes(filePath);
                using var cb = new Win32Clipboard();
                cb.SetPng(png);
                _logger.Info("OceanEyes", $"Gallery: copied {filePath} ({png.Length} bytes) to clipboard.");
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "Gallery copy failed.", exception);
            }
        });
    }

    /// <summary>
    /// R49: gallery "reveal in explorer" handler. Spawns
    /// <c>explorer.exe /select,"&lt;path&gt;"</c> which opens the containing
    /// folder with the file pre-selected — matches what every Windows app
    /// does for "show in folder". Fire-and-forget on a background thread:
    /// Process.Start itself is fast but we don't want to block the UI thread
    /// on shell launch in case Explorer is slow to hand off.
    /// </summary>
    private void RevealGalleryEntryInExplorer(string filePath)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _logger.Info("OceanEyes", $"Gallery reveal: file vanished: {filePath}");
                    return;
                }
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + filePath + "\"",
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(startInfo);
                _logger.Info("OceanEyes", $"Gallery: revealed {filePath} in Explorer.");
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "Gallery reveal failed.", exception);
            }
        });
    }

    /// <summary>
    /// R46: copies the pinned window's PNG to the clipboard via the same
    /// Win32Clipboard.SetPng path used by Enter-to-save. Runs on a background
    /// thread (clipboard I/O shouldn't block the UI thread).
    /// </summary>
    private void CopyPinnedToClipboard(PinnedScreenshotWindow window)
    {
        byte[]? png = window.PngBytes;
        if (png is null || png.Length == 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                using var clipboard = new Win32Clipboard();
                clipboard.SetPng(png);
                _logger.Info("OceanEyes", $"Copied pinned PNG ({png.Length} bytes) to clipboard.");
                Dispatcher.UIThread.Post(() => _toolbarWindow.SetDiagnosticStatus(Strings.Runtime_Status_PinnedCopied));
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "Pinned clipboard copy failed.", exception);
            }
        });
    }

    /// <summary>
    /// R46: closes a single pinned window. Hides + disposes the window, then
    /// removes both window and host from the tracking lists. Idempotent —
    /// safe to call multiple times (the window's Closing handler intercepts
    /// native Close, so we drive Hide + Dispose explicitly). The host has no
    /// native resources of its own (it only applies Win32 styles on the HWND
    /// owned by the window), so it needs no Dispose call.
    /// <para>
    /// R46 v6: when the pinned list becomes empty AND no Ocean Eyes session
    /// is active, disables the keyboard hook — no reason to keep watching
    /// keys globally if there's nothing to act on. (If Ocean Eyes IS active,
    /// the hook stays armed for toolbar shortcuts.)
    /// </para>
    /// <para>
    /// R46 v7: plays the close animation (Opacity 1 → 0, ~150ms) before
    /// Hide+Dispose so the window fades out instead of vanishing. The window
    /// is removed from the tracking lists BEFORE the animation starts so
    /// subsequent Esc presses don't try to close the same window again while
    /// it's mid-fade.
    /// </para>
    /// </summary>
    private void ClosePinned(PinnedScreenshotWindow window)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                // Remove from tracking lists FIRST so a rapid second Esc (or
                // a second double-click) doesn't try to close the same window
                // again while it's mid-fade-out.
                int idx = _pinnedWindows.IndexOf(window);
                if (idx >= 0)
                {
                    _pinnedWindows.RemoveAt(idx);
                    if (idx < _pinnedHosts.Count)
                    {
                        _pinnedHosts.RemoveAt(idx);
                    }
                }

                // R46 v7: fade out before disposing. AnimateOutAsync sets
                // _animatingOut so the window's Closing handler won't double-Hide.
                await window.AnimateOutAsync();

                window.Hide();
                window.Dispose();

                // R46 v6 / R97: defensive hook disarm when no pinned windows
                // remain AND no Ocean Eyes session is active. Esc no longer
                // closes pins, so nothing keeps the hook armed for pins
                // anymore — this just guarantees the hook is off if we're
                // fully idle, in case some earlier path forgot to disarm.
                if (_pinnedWindows.Count == 0 &&
                    Volatile.Read(ref _oceanEyesActive) == 0)
                {
                    _keyboardHook.SetEnabled(false);
                }
                _logger.Info("OceanEyes", "Closed pinned window.");
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "Close pinned failed.", exception);
            }
        });
    }

    /// <summary>
    /// R46: closes every pinned window. Invoked by the "关闭所有" context-menu
    /// item. Iterates a snapshot to avoid mutation-during-enumeration.
    /// <para>
    /// R46 v6: disables the keyboard hook if no Ocean Eyes session is active
    /// (same logic as <see cref="ClosePinned"/>).
    /// </para>
    /// </summary>
    private void CloseAllPinned()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                foreach (var window in _pinnedWindows)
                {
                    window.Hide();
                    window.Dispose();
                }
                int count = _pinnedWindows.Count;
                _pinnedWindows.Clear();
                _pinnedHosts.Clear();
                // R46 v6: hook disarm when no pinned windows + no Ocean Eyes.
                if (Volatile.Read(ref _oceanEyesActive) == 0)
                {
                    _keyboardHook.SetEnabled(false);
                }
                _logger.Info("OceanEyes", $"Closed {count} pinned window(s).");
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "Close-all pinned failed.", exception);
            }
        });
    }

    /// <summary>
    /// R42: clears the Ocean Eyes toolbar + OCR state for a right-click
    /// redraw, but does NOT close the overlay (it stays visible with UIA
    /// tracking re-armed). The overlay's own <c>Reset()</c> handles the
    /// visual rect clearing. Distinct from <see cref="DismissOceanEyes"/>
    /// which also closes the overlay via <see cref="DismissOverlay"/>.
    /// </summary>
    public void ResetForRedraw()
    {
        // R44: right-click redraw should also cancel the color picker — the
        // loupe doesn't make sense on an unconfirmed region.
        HideColorPicker();
        // R48: also exit annotation mode + clear annotations on redraw.
        Volatile.Write(ref _oceanEyesAnnotating, 0);
        _annotationSession?.Clear();
        _annotationSession = null;
        Dispatcher.UIThread.Post(() => _annotationOverlay?.ClearAnnotations());
        Volatile.Write(ref _oceanEyesActive, 0);
        _oceanEyesPng = null;
        _oceanEyesOcrTask = null;
        _oceanEyesOcrText = null;
        Volatile.Write(ref _oceanEyesOcrDone, 0);
        _windowHost.Hide();
        // R97: unconditional disarm — Esc no longer closes pins (see the
        // matching change in OnToolbarKeyPressed / onToolbarHidden).
        _keyboardHook.SetEnabled(false);
    }

    /// <summary>
    /// Returns the most recently captured selection text (from the last
    /// selection session), if any. The chord flow uses this to seed the
    /// quick-tools panel when the user chords over already-selected text.
    /// Falls back to the clipboard when no BYH session has captured text yet
    /// — the user often selects text in an app that doesn't trigger a BYH
    /// toolbar (different policy, or selection via keyboard), but the text is
    /// on the clipboard after Ctrl+C. Best-effort; swallows clipboard errors.
    /// </summary>
    public string? GetLastCapturedText()
    {
        string? text = _sessionManager.GetLastCapturedText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        // Clipboard fallback: best-effort read so the chord flow works even
        // when no BYH selection session fired (e.g. user selected text in an
        // app whose capture policy is off, then Ctrl+C'd).
        try
        {
            using var clipboard = new Win32Clipboard();
            string? clip = clipboard.GetText();
            return string.IsNullOrWhiteSpace(clip) ? null : clip.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Runs the built-in translate action on the given text (used by the
    /// quick-tools panel's "Translate" button). Hides the toolbar first so the
    /// result window is the focus.
    /// </summary>
    public void TranslateAsync(string sourceText)
    {
        OnTranslateRequested(sourceText);
    }

    private IReadOnlyList<PolicyRule> LoadUserCapturePolicies(string path)
    {
        try
        {
            IReadOnlyList<PolicyRule> rules = CapturePolicyConfigurationLoader.LoadIfExists(path);
            if (rules.Count > 0)
            {
                _logger.Info("CapturePolicy", $"Loaded {rules.Count} user capture policy rules.");
            }

            return rules;
        }
        catch (CapturePolicyConfigurationException exception)
        {
            _logger.Error(
                "CapturePolicy",
                "User capture policy file was rejected; safe defaults remain active.",
                exception);
            return [];
        }
    }

    /// <summary>Loads providers.json into a mutable config (empty on missing/invalid).</summary>
    private MutableProviderConfiguration LoadProviderConfig(ByhApplicationPaths paths)
    {
        try
        {
            ProviderConfiguration loaded = ProviderConfigurationLoader.LoadIfExists(paths.ProvidersFile);
            return new MutableProviderConfiguration(loaded.DefaultProviderId, loaded.Providers);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("Translation", "Provider config rejected; using empty config.", exception);
            return new MutableProviderConfiguration();
        }
    }

    /// <summary>
    /// Loads the global prompt-templates.json. Missing/corrupt file falls back
    /// to built-in defaults (logged, no crash).
    /// </summary>
    private PromptTemplateSet LoadPromptTemplates(ByhApplicationPaths paths)
    {
        try
        {
            return PromptTemplatesStore.LoadIfExists(paths.PromptTemplatesFile);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("PromptTemplates", "Prompt template file rejected; using built-in defaults.", exception);
            return PromptTemplateDefaults.CreateDefault();
        }
    }

    /// <summary>
    /// Loads launcher-entries.json. Missing/corrupt file falls back to an empty
    /// set (logged, no crash). User-added entries only — no built-ins.
    /// </summary>
    private LauncherEntrySet LoadLauncherEntries(ByhApplicationPaths paths)
    {
        try
        {
            return LauncherEntryStore.LoadIfExists(paths.LauncherEntriesFile);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("Launcher", "Launcher entries file rejected; using empty set.", exception);
            return LauncherEntryDefaults.CreateDefault();
        }
    }

    /// <summary>Resolves the provider entry that should be active right now.</summary>
    private ProviderProfileEntry? ResolveDefaultEntry()
    {
        if (!string.IsNullOrEmpty(_providerConfig.DefaultProviderId))
        {
            ProviderProfileEntry? byId = _providerConfig.FindById(_providerConfig.DefaultProviderId);
            if (byId is not null) return byId;
        }
        return _providerConfig.Providers.FirstOrDefault();
    }

    /// <summary>
    /// Hot-swaps the active provider to the given entry. Disposes the old
    /// provider, injects the new one into the session manager, and updates the
    /// label/reference fields. If entry is null, falls back to MyMemory.
    /// </summary>
    private void SwitchToProvider(ProviderProfileEntry? entry, bool logOnMiss = false)
    {
        // Dispose the old provider first.
        _disposableProvider?.Dispose();
        _disposableProvider = null;

        if (entry is null)
        {
            if (logOnMiss)
            {
                _logger.Info("Translation", "No provider configured; using MyMemory test fallback.");
            }
            _translationProvider = new MyMemoryTranslationProvider();
            _apiKeyReference = null;
            _providerLabel = "MyMemory 测试提供器";
        }
        else
        {
            var options = new OpenAiCompatibleProviderOptions
            {
                Id = entry.Id,
                DisplayName = entry.Name,
                BaseUrl = entry.BaseUrl,
                ApiKeyReference = entry.ApiKeyReference,
                DefaultModel = entry.DefaultModel,
                ChatCompletionsPath = entry.ChatCompletionsPath,
                Timeout = TimeSpan.FromSeconds(entry.TimeoutSeconds),
                MaxSourceCharacters = entry.MaxSourceCharacters,
                SystemPrompt = entry.SystemPrompt,
            };
            var streaming = new OpenAiCompatibleStreamingProvider(options, _secretStore);
            _translationProvider = streaming;
            _disposableProvider = streaming;
            _apiKeyReference = entry.ApiKeyReference;
            _providerLabel = $"{entry.Name} · {entry.DefaultModel}";
            _logger.Info("Translation", $"Switched to provider '{entry.Id}' (model {entry.DefaultModel}).");
        }

        // If the manager already exists (not first construction), hot-swap it.
        // On first construction the manager is built AFTER this method, so we
        // skip the ReplaceProvider call — the manager gets the right provider
        // at construction time.
        if (_translationManager is not null)
        {
            _translationManager.ReplaceProvider(_translationProvider);
        }
    }

    // ── Public query methods for the settings UI ──

    public IReadOnlyList<ProviderProfileEntry> GetProviders() => _providerConfig.Providers;

    public string? GetCurrentProviderId() => _providerConfig.DefaultProviderId;

    public string GetProviderLabel() => _providerLabel;

    // ── R24 track B: vision OCR settings (read/written by the settings UI) ──

    public VisionCaptureSettings GetVisionSettings() => _visionSettings;

    /// <summary>
    /// Updates the vision OCR settings, persists them to vision.json, and
    /// (re)wires the vision tier. Returns false if persistence failed.
    /// </summary>
    public bool UpdateVisionSettings(VisionCaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _visionSettings = settings;
        try
        {
            VisionCaptureStore.Save(settings, _paths.VisionCaptureFile);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("VisionCapture", "Failed to persist vision.json.", exception);
            // Still apply in-memory so the UI reflects intent; will not survive restart.
        }

        ApplyVisionCapture();
        return true;
    }

    // ── R24 region-select OCR (chord → draw-region path) ──

    /// <summary>
    /// R24: returns the UIA bounding box of the element under the given point,
    /// to pre-fill the region-select overlay. Null when vision is disabled or
    /// UIA can't resolve a box (canvas/games/scanned PDF) — the overlay then
    /// starts in free-draw mode.
    /// </summary>
    public Rect? GetInitialRegionAt(int x, int y)
    {
        if (!_visionSettings.Enabled || _visionBackend is null)
        {
            return null;
        }

        return _visionBackend.GetElementBoundsAt(x, y);
    }

    /// <summary>
    /// R24: returns text from the given screen region, trying UI Automation
    /// first and falling back to cloud OCR. Used by the chord → draw-region
    /// flow. UIA is preferred when the region covers real desktop controls
    /// (browser, editor, list, dialog) — it's instant, free, and returns
    /// structurally clean text with no model-hallucinated markdown wrappers.
    /// OCR is the fallback for regions over images, video, canvas, scanned
    /// PDFs, or any content UIA can't navigate.
    /// </summary>
    /// <remarks>
    /// Default path is OCR only — it captures exactly the drawn rectangle and
    /// is trustworthy on every kind of content. The UIA tier (which reads
    /// structured text from desktop controls inside the region) is opt-in via
    /// <see cref="VisionCaptureSettings.UiaPrefillEnabled"/>: it's faster and
    /// cleaner when it works, but the ancestor-walk can return text from
    /// outside the drawn box on apps whose UIA tree doesn't match the visual
    /// layout. OCR is the safe default. Returns null only when both tiers fail
    /// or vision is disabled. Never throws — the caller shows "识别失败" on null.
    /// </remarks>
    public async Task<string?> CaptureAndRecognizeRegionAsync(
        int x, int y, int width, int height, CancellationToken token)
    {
        if (!_visionSettings.Enabled)
        {
            return null;
        }

        // Opt-in UIA tier. Disabled by default because the ancestor-walk can
        // return text from outside the drawn box on apps whose UIA container
        // structure doesn't match the visual layout. OCR is the trustworthy
        // default. Users who want the faster UIA path on simple desktop apps
        // can enable it via the "UIA 预填框" toggle in settings (the same
        // toggle controls the prefill box — they're a package deal).
        if (_visionSettings.UiaPrefillEnabled && _visionBackend is not null)
        {
            var region = new Rect(x, y, width, height);
            try
            {
                IReadOnlyList<string> texts = _visionBackend.GetTextsInRegion(region);
                if (texts.Count > 0)
                {
                    string joined = string.Join('\n', texts).Trim();
                    if (!string.IsNullOrEmpty(joined))
                    {
                        _logger.Info("RegionOcr", $"UIA tier hit: {texts.Count} text element(s).");
                        return joined;
                    }
                }
                _logger.Info("RegionOcr", "UIA tier empty; falling back to OCR.");
            }
            catch (Exception exception)
            {
                // UIA failures are non-fatal — fall through to OCR.
                _logger.Error("RegionOcr", "UIA tier failed; falling back to OCR.", exception);
            }
        }

        // Default tier: cloud OCR. Captures exactly the drawn rectangle, so
        // the result is always within the user's selection regardless of the
        // app's UIA tree structure.
        if (_visionOcrClient is null)
        {
            return null;
        }

        string? dataUri = ScreenRegionCapture.CaptureAsDataUri(x, y, width, height);
        if (string.IsNullOrEmpty(dataUri))
        {
            _logger.Info("RegionOcr", "Screen capture returned empty data URI.");
            return null;
        }

        // R42 fix: removed the hardcoded 10s per-call timeout — it was too
        // aggressive for large selections (image bigger → model slower).
        // The OCR client already has a configurable Timeout from vision
        // settings (default 60s, min 30s). Let the client-level timeout
        // handle this instead.
        try
        {
            string text = await _visionOcrClient
                .RecognizeAsync(dataUri, token)
                .ConfigureAwait(false);
            text = text.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (OperationCanceledException)
        {
            _logger.Info("RegionOcr", "Region OCR cancelled or timed out.");
            return null;
        }
        catch (Exception exception)
        {
            _logger.Error("RegionOcr", "Region OCR failed.", exception);
            return null;
        }
    }

    public async Task<bool> HasApiKeyAsync(string? apiKeyReference = null)
    {
        string? reference = apiKeyReference ?? _apiKeyReference;
        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }
        string? key = await _secretStore.GetAsync(reference).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(key);
    }

    /// <summary>
    /// Saves an API key to DPAPI storage for the given secret reference. If the
    /// reference matches the active provider, hot-swaps so it takes effect
    /// immediately (no restart).
    /// </summary>
    public async Task<bool> SaveApiKeyAsync(string apiKeyReference, string keyValue)
    {
        if (string.IsNullOrEmpty(apiKeyReference))
        {
            _logger.Error("Translation", "Cannot save API key: empty reference.");
            return false;
        }

        try
        {
            await _secretStore.SetAsync(apiKeyReference, keyValue).ConfigureAwait(false);
            _logger.Info("Translation", $"API key saved for {apiKeyReference}.");

            // If this is the active provider's key, hot-swap so it takes effect now.
            if (apiKeyReference == _apiKeyReference)
            {
                ProviderProfileEntry? current = ResolveDefaultEntry();
                if (current is not null)
                {
                    SwitchToProvider(current);
                }
            }
            return true;
        }
        catch (Exception exception)
        {
            _logger.Error("Translation", "Failed to save API key.", exception);
            return false;
        }
    }

    // ── Public CRUD methods for the settings UI ──

    public Task<bool> AddProviderAsync(ProviderProfileEntry entry)
    {
        try
        {
            if (_providerConfig.FindById(entry.Id) is not null)
            {
                _logger.Error("Translation", $"Provider '{entry.Id}' already exists.");
                return Task.FromResult(false);
            }
            _providerConfig.Providers.Add(entry);
            PersistConfig();
            _logger.Info("Translation", $"Added provider '{entry.Id}'.");
            return Task.FromResult(true);
        }
        catch (Exception exception)
        {
            _logger.Error("Translation", "Failed to add provider.", exception);
            return Task.FromResult(false);
        }
    }

    public Task<bool> UpdateProviderAsync(ProviderProfileEntry entry)
    {
        try
        {
            int index = _providerConfig.Providers.FindIndex(
                p => string.Equals(p.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                _logger.Error("Translation", $"Provider '{entry.Id}' not found for update.");
                return Task.FromResult(false);
            }
            _providerConfig.Providers[index] = entry;
            PersistConfig();

            // If this is the active provider, hot-swap with updated fields.
            if (string.Equals(_providerConfig.DefaultProviderId, entry.Id, StringComparison.OrdinalIgnoreCase))
            {
                SwitchToProvider(entry);
            }
            _logger.Info("Translation", $"Updated provider '{entry.Id}'.");
            return Task.FromResult(true);
        }
        catch (Exception exception)
        {
            _logger.Error("Translation", "Failed to update provider.", exception);
            return Task.FromResult(false);
        }
    }

    public async Task<bool> DeleteProviderAsync(string id)
    {
        try
        {
            int index = _providerConfig.Providers.FindIndex(
                p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            // Clean up the secret for this provider.
            ProviderProfileEntry entry = _providerConfig.Providers[index];
            if (!string.IsNullOrEmpty(entry.ApiKeyReference))
            {
                await _secretStore.DeleteAsync(entry.ApiKeyReference).ConfigureAwait(false);
            }

            _providerConfig.Providers.RemoveAt(index);
            bool wasDefault = string.Equals(_providerConfig.DefaultProviderId, id, StringComparison.OrdinalIgnoreCase);
            if (wasDefault)
            {
                _providerConfig.DefaultProviderId = _providerConfig.Providers.FirstOrDefault()?.Id;
            }
            PersistConfig();

            // If we just deleted the active provider, switch to the new default.
            if (wasDefault)
            {
                SwitchToProvider(ResolveDefaultEntry(), logOnMiss: true);
            }

            _logger.Info("Translation", $"Deleted provider '{id}'.");
            return true;
        }
        catch (Exception exception)
        {
            _logger.Error("Translation", "Failed to delete provider.", exception);
            return false;
        }
    }

    /// <summary>Sets the default provider and hot-swaps to it immediately.</summary>
    public Task<bool> SetDefaultProviderAsync(string id)
    {
        try
        {
            if (_providerConfig.FindById(id) is not { } entry)
            {
                _logger.Error("Translation", $"Provider '{id}' not found.");
                return Task.FromResult(false);
            }
            _providerConfig.DefaultProviderId = id;
            PersistConfig();
            SwitchToProvider(entry);
            _logger.Info("Translation", $"Default provider set to '{id}'.");
            return Task.FromResult(true);
        }
        catch (Exception exception)
        {
            _logger.Error("Translation", "Failed to set default provider.", exception);
            return Task.FromResult(false);
        }
    }

    private void PersistConfig()
    {
        try
        {
            ProviderConfigurationLoader.Save(_providerConfig.ToImmutable(), _paths.ProvidersFile);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("Translation", "Failed to persist providers.json.", exception);
        }
    }

    // ── Prompt template management ──

    /// <summary>Returns the current global prompt templates (snapshot copy).</summary>
    public PromptTemplateSet GetPromptTemplates() => _promptTemplates;

    /// <summary>
    /// Updates one action's prompt (preserving its current thinking flag),
    /// persists to prompt-templates.json, and returns success. Translate's
    /// empty string means "use provider built-in".
    /// </summary>
    public Task<bool> SavePromptTemplateAsync(string actionId, string prompt) =>
        SavePromptTemplateAsync(actionId, prompt, GetPromptTemplates().Find(actionId)?.ThinkingEnabled ?? false);

    /// <summary>
    /// Updates one action's prompt AND thinking flag together, persists to
    /// prompt-templates.json. Used by the edit window, which saves both fields
    /// in one go.
    /// </summary>
    public Task<bool> SavePromptTemplateAsync(string actionId, string prompt, bool thinkingEnabled)
        => SavePromptTemplateAsync(actionId, prompt, thinkingEnabled,
            GetPromptTemplates().Find(actionId)?.Shortcut);

    /// <summary>
    /// Updates one action's prompt, thinking flag, AND single-character toolbar
    /// shortcut together, persists to prompt-templates.json. Pass <c>null</c>
    /// for <paramref name="shortcut" /> to clear the binding. Used by the edit
    /// window (R34), which commits all three fields in one save.
    /// </summary>
    public Task<bool> SavePromptTemplateAsync(string actionId, string prompt, bool thinkingEnabled, string? shortcut)
    {
        try
        {
            if (!_promptTemplates.TrySet(actionId, prompt, thinkingEnabled, shortcut))
            {
                _logger.Error("PromptTemplates", $"Unknown action id '{actionId}'.");
                return Task.FromResult(false);
            }
            PromptTemplatesStore.Save(_promptTemplates, _paths.PromptTemplatesFile);
            string? norm = _promptTemplates.Find(actionId)?.Shortcut;
            _logger.Info("PromptTemplates",
                $"Updated prompt for action '{actionId}' (thinking={thinkingEnabled}, shortcut={norm ?? "<none>"}).");
            return Task.FromResult(true);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("PromptTemplates", "Failed to persist prompt-templates.json.", exception);
            return Task.FromResult(false);
        }
    }

    /// <summary>Resets one action's prompt to the built-in default + persists.</summary>
    public Task<bool> ResetPromptTemplateAsync(string actionId)
    {
        var defaults = PromptTemplateDefaults.CreateDefault();
        PromptTemplate? @default = defaults.Find(actionId);
        if (@default is null)
        {
            return Task.FromResult(false);
        }
        // Reset also restores the built-in default shortcut (F/J/Z) so a
        // full "恢复默认" returns the template to its shipped state.
        return SavePromptTemplateAsync(actionId, @default.Prompt, @default.ThinkingEnabled, @default.Shortcut);
    }

    /// <summary>
    /// Adds a new user custom function. Generates a stable <c>custom-*</c> id,
    /// appends to the template set, and persists. Returns the new action id on
    /// success, or null if the add failed (e.g. duplicate name handling).
    /// </summary>
    public Task<string?> AddPromptTemplateAsync(string name, string prompt, bool thinkingEnabled)
        => AddPromptTemplateAsync(name, prompt, thinkingEnabled, shortcut: null);

    /// <summary>
    /// Adds a new user custom function with an optional single-character toolbar
    /// shortcut. Generates a stable <c>custom-*</c> id, appends to the template
    /// set, and persists. Returns the new action id on success, or null if the
    /// add failed. Used by the edit window's "new" mode (R34).
    /// </summary>
    public Task<string?> AddPromptTemplateAsync(string name, string prompt, bool thinkingEnabled, string? shortcut)
    {
        try
        {
            string id = PromptActionIds.CustomPrefix + Guid.NewGuid().ToString("N")[..8];
            string? normalizedShortcut = string.IsNullOrWhiteSpace(shortcut) ? null : shortcut.Trim().ToUpperInvariant();
            var template = new PromptTemplate(id, name, prompt, thinkingEnabled, normalizedShortcut);
            if (!_promptTemplates.Add(template))
            {
                _logger.Error("PromptTemplates", $"Failed to add custom function '{name}'.");
                return Task.FromResult<string?>(null);
            }
            PromptTemplatesStore.Save(_promptTemplates, _paths.PromptTemplatesFile);
            _logger.Info("PromptTemplates",
                $"Added custom function '{name}' (id={id}, thinking={thinkingEnabled}, shortcut={normalizedShortcut ?? "<none>"}).");
            return Task.FromResult<string?>(id);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("PromptTemplates", "Failed to persist prompt-templates.json.", exception);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Deletes a user custom function. Built-in actions (translate/summarize/
    /// explain) cannot be deleted — returns false. Returns true on success.
    /// </summary>
    public Task<bool> DeletePromptTemplateAsync(string actionId)
    {
        try
        {
            if (!_promptTemplates.Remove(actionId))
            {
                _logger.Info("PromptTemplates", $"Cannot delete '{actionId}' (built-in or not found).");
                return Task.FromResult(false);
            }
            PromptTemplatesStore.Save(_promptTemplates, _paths.PromptTemplatesFile);
            _logger.Info("PromptTemplates", $"Deleted custom function '{actionId}'.");
            return Task.FromResult(true);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("PromptTemplates", "Failed to persist prompt-templates.json.", exception);
            return Task.FromResult(false);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // R23 launcher entries (quick-launch software/URLs).
    // Mirrors the prompt-template CRUD pattern: in-memory set + atomic persist
    // on every change. Failures log + return false but do NOT roll back memory
    // (consistent with the rest of the runtime).
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Snapshot of the current launcher entries, in display order.</summary>
    public LauncherEntrySet GetLauncherEntries() => _launcherEntries;

    /// <summary>
    /// Adds a new launcher entry. Generates a stable <c>launcher-*</c> id,
    /// appends to the set, and persists. Returns the new id on success, or
    /// null if the add failed.
    /// </summary>
    public Task<string?> AddLauncherEntryAsync(
        string name, LauncherKind kind, string target, string arguments, string workingDirectory)
    {
        try
        {
            string id = LauncherEntryIds.CustomPrefix + Guid.NewGuid().ToString("N")[..8];
            var entry = new LauncherEntry(id, name, kind, target, arguments, workingDirectory);
            if (!_launcherEntries.Add(entry))
            {
                _logger.Error("Launcher", $"Failed to add launcher entry '{name}'.");
                return Task.FromResult<string?>(null);
            }
            LauncherEntryStore.Save(_launcherEntries, _paths.LauncherEntriesFile);
            _logger.Info("Launcher", $"Added entry '{name}' (id={id}, kind={kind}).");
            return Task.FromResult<string?>(id);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("Launcher", "Failed to persist launcher-entries.json.", exception);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Batch-adds auto-detected launcher entries (from the Start Menu scanner).
    /// Each entry gets a fresh <c>launcher-*</c> id with <see cref="LauncherEntry.IsAutoDetected"/>
    /// set true so the UI can mark them as scanner-imported. Persists once at
    /// the end (not per-entry) for efficiency. Entries whose Target already
    /// exists in the set are skipped (dedup). Returns the number actually added.
    /// </summary>
    public Task<int> AddAutoDetectedLauncherEntriesAsync(IReadOnlyList<DetectedApp> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        try
        {
            int added = 0;
            foreach (DetectedApp app in apps)
            {
                // Dedup by target — skip if the user already added this exe
                // (manually or via a previous scan).
                if (_launcherEntries.FindByTarget(app.ExecutablePath) is not null)
                {
                    continue;
                }
                string id = LauncherEntryIds.CustomPrefix + Guid.NewGuid().ToString("N")[..8];
                var entry = new LauncherEntry(
                    Id: id,
                    Name: app.Name,
                    Kind: LauncherKind.LocalApp,
                    Target: app.ExecutablePath,
                    IsAutoDetected: true);
                if (_launcherEntries.Add(entry))
                {
                    added++;
                }
            }
            if (added > 0)
            {
                LauncherEntryStore.Save(_launcherEntries, _paths.LauncherEntriesFile);
                _logger.Info("Launcher", $"Imported {added} auto-detected app(s) from scan.");
            }
            return Task.FromResult(added);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("Launcher", "Failed to persist auto-detected launcher entries.", exception);
            return Task.FromResult(0);
        }
    }

    /// <summary>
    /// Updates an existing launcher entry (looked up by id). All fields are
    /// replaced with the supplied values, including the name (the editor allows
    /// renaming). Returns false if the id was not found.
    /// </summary>
    public Task<bool> SaveLauncherEntryAsync(
        string id, string name, LauncherKind kind, string target,
        string arguments, string workingDirectory)
    {
        try
        {
            LauncherEntry? existing = _launcherEntries.Find(id);
            if (existing is null)
            {
                _logger.Error("Launcher", $"Unknown launcher id '{id}'.");
                return Task.FromResult(false);
            }
            var updated = existing with
            {
                Name = name,
                Kind = kind,
                Target = target,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
            };
            if (!_launcherEntries.Update(updated))
            {
                return Task.FromResult(false);
            }
            LauncherEntryStore.Save(_launcherEntries, _paths.LauncherEntriesFile);
            _logger.Info("Launcher", $"Updated entry '{name}' (id={id}).");
            return Task.FromResult(true);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("Launcher", "Failed to persist launcher-entries.json.", exception);
            return Task.FromResult(false);
        }
    }

    /// <summary>Deletes the entry with the given id. Returns true on success.</summary>
    public Task<bool> DeleteLauncherEntryAsync(string id)
    {
        try
        {
            if (!_launcherEntries.Remove(id))
            {
                _logger.Info("Launcher", $"Cannot delete '{id}' (not found).");
                return Task.FromResult(false);
            }
            LauncherEntryStore.Save(_launcherEntries, _paths.LauncherEntriesFile);
            _logger.Info("Launcher", $"Deleted entry '{id}'.");
            return Task.FromResult(true);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("Launcher", "Failed to persist launcher-entries.json.", exception);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Moves the entry by <paramref name="delta"/> positions (negative = up).
    /// Clamped to list bounds. Returns true if the entry was found (no-op moves
    /// at the edges still return true).
    /// </summary>
    public Task<bool> MoveLauncherEntryAsync(string id, int delta)
    {
        try
        {
            if (!_launcherEntries.Move(id, delta))
            {
                return Task.FromResult(false);
            }
            LauncherEntryStore.Save(_launcherEntries, _paths.LauncherEntriesFile);
            return Task.FromResult(true);
        }
        catch (ProviderConfigurationException exception)
        {
            _logger.Error("Launcher", "Failed to persist launcher-entries.json.", exception);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Expands {clip}/{sel} in the entry's arguments and reports any {prompt:...}
    /// placeholders the caller must ask the user to fill before launching. When
    /// <see cref="LauncherLaunchResult.NeedsPrompt"/> is true, the caller shows
    /// the prompts, collects answers, and calls
    /// <see cref="CompleteLauncherLaunchAsync"/> with the answers.
    /// </summary>
    public Task<LauncherLaunchResult> StartLauncherLaunchAsync(
        string entryId, string? clipText, string? selectedText)
    {
        LauncherEntry? entry = _launcherEntries.Find(entryId);
        if (entry is null)
        {
            return Task.FromResult(new LauncherLaunchResult(
                Success: false, ErrorMessage: $"找不到启动项 '{entryId}'。", Prompts: Array.Empty<string>(), NeedsPrompt: false));
        }

        ParameterReplaceResult expanded = ParameterReplace.Expand(entry.Arguments, clipText, selectedText);
        if (expanded.NeedsPrompt)
        {
            // Stash the partially-expanded args for the follow-up call. The
            // stash is single-slot — the UI is modal, so only one launch can
            // be pending at a time per runtime instance.
            _pendingLaunch = (entryId, expanded.ExpandedArguments);
            return Task.FromResult(new LauncherLaunchResult(
                Success: false, ErrorMessage: null, Prompts: expanded.Prompts, NeedsPrompt: true));
        }

        // No prompt needed: launch immediately.
        string? err = LauncherRunner.Start(entry, expanded.ExpandedArguments);
        if (err is null)
        {
            _logger.Info("Usage", "module=Launcher feature=OpenEntry");
        }
        _pendingLaunch = null;
        return Task.FromResult(new LauncherLaunchResult(
            Success: err is null, ErrorMessage: err, Prompts: Array.Empty<string>(), NeedsPrompt: false));
    }

    /// <summary>
    /// Completes a launch that returned <see cref="LauncherLaunchResult.NeedsPrompt"/>
    /// by substituting the user's answers into the {prompt:...} placeholders and
    /// starting the entry. Returns null on success, or an error message.
    /// </summary>
    public Task<string?> CompleteLauncherLaunchAsync(IReadOnlyList<string> answers)
    {
        if (_pendingLaunch is not { } pending)
        {
            return Task.FromResult<string?>("没有待完成的启动操作。");
        }
        LauncherEntry? entry = _launcherEntries.Find(pending.EntryId);
        if (entry is null)
        {
            _pendingLaunch = null;
            return Task.FromResult<string?>($"找不到启动项 '{pending.EntryId}'。");
        }
        string finalArgs = ParameterReplace.ApplyPromptValues(pending.ExpandedArgs, answers);
        string? err = LauncherRunner.Start(entry, finalArgs);
        if (err is null)
        {
            _logger.Info("Usage", "module=Launcher feature=OpenEntry");
        }
        _pendingLaunch = null;
        return Task.FromResult(err);
    }

    /// <summary>Cancels any pending launch (e.g. user closed the prompt dialog).</summary>
    public void CancelPendingLaunch() => _pendingLaunch = null;

    // Single-slot stash for a launch awaiting user prompt answers. Nullable so
    // "no pending" is cleanly null (ValueTuple isn't nullable by default).
    private (string EntryId, string ExpandedArgs)? _pendingLaunch;

    /// <summary>
    /// Runs a built-in action (translate/summarize/explain) against the given
    /// text using the global prompt template for that action. Each action has
    /// an editable default; translate falls back to the provider's built-in
    /// template only when its prompt is cleared to empty.
    /// </summary>
    public void RunActionAsync(string actionId, string selectedText)
    {
        PromptTemplate? template = _promptTemplates.Find(actionId);
        if (template is null)
        {
            return;
        }
        _logger.Info("Usage", $"module=Actions feature={actionId}");

        // All actions: use the template prompt. Translate with an empty prompt
        // → null SystemPrompt → provider built-in translation template.
        string? systemPrompt = string.IsNullOrWhiteSpace(template.Prompt) ? null : template.Prompt;

        // Use the shared language selector so translate-direction logic stays
        // consistent with the built-in path; then attach the template prompt
        // and the per-action thinking flag (the single source of truth — the
        // provider no longer carries its own thinking setting).
        TranslationRequest request = TranslationLanguageSelector.CreateRequest(selectedText);
        if (systemPrompt is not null)
        {
            request = request with { SystemPrompt = systemPrompt };
        }
        request = request with { ThinkingEnabled = template.ThinkingEnabled };

        _windowHost.Hide();
        // Action invoked → toolbar hidden → stop the keyboard hook so it
        // doesn't filter typing in the source app anymore. Matches the
        // start/stop pairing in ToolbarSessionView.
        StopKeyboardHookQuiet();
        TrackSessionTask(_translationManager.StartOrReplaceAsync(request));
    }

    /// <summary>
    /// Disables the keyboard hook's shortcut dispatching, swallowing failures.
    /// The hook itself stays installed for the whole app lifetime; this only
    /// flips the enable flag so typing in the source app is not filtered
    /// after the toolbar disappears. Call from any code path that hides the
    /// toolbar without going through <see cref="ToolbarSessionView.HideToolbar" />
    /// (e.g. <see cref="RunActionAsync" />, <see cref="RunPromptAsync" />).
    /// </summary>
    private void StopKeyboardHookQuiet()
    {
        // R40: any path that hides the selection toolbar must also clear the
        // Ocean Eyes state, otherwise the cached PNG + flag would linger into
        // the next (selection-flow) toolbar session and Enter would wrongly
        // try to save a stale screenshot. DismissOceanEyes is idempotent.
        if (Volatile.Read(ref _oceanEyesActive) != 0)
        {
            DismissOceanEyes();
        }
        try
        {
            // R46 v6 / R97: defensive disarm — see ClosePinned for the same
            // check. Esc no longer closes pins, so this just ensures the hook
            // is off when fully idle.
            if (_pinnedWindows.Count == 0)
            {
                _keyboardHook.SetEnabled(false);
            }
        }
        catch (Exception exception)
        {
            _logger.Error("KeyboardHook", "Failed to disable keyboard hook.", exception);
        }
    }

    /// <summary>
    /// R38 fix: hides the selection toolbar and disables its keyboard hook
    /// without starting a translation session. Used when the toolbar's "Prompt"
    /// button is clicked — that opens the Prompt input window (handled by the
    /// caller in App.axaml.cs), and the toolbar itself must disappear first so
    /// the two windows don't coexist on screen. This is exactly the first two
    /// steps of <see cref="RunActionAsync"/> / <see cref="RunPromptAsync"/>
    /// (hide + disable hook), minus the translation kickoff (the user still
    /// needs to type their prompt).
    /// </summary>
    public void HideToolbarAndDisableHook()
    {
        _windowHost.Hide();
        StopKeyboardHookQuiet();
    }

    private void OnTranslateRequested(string sourceText)
    {
        // Honor a user-customized translate prompt; if empty, RunActionAsync
        // passes null SystemPrompt and the provider uses its built-in template.
        RunActionAsync(PromptActionIds.Translate, sourceText);
    }

    /// <summary>
    /// R34: keyboard-shortcut dispatcher for the selection toolbar. Called by the
    /// low-level keyboard hook (on its background thread) for every key-down
    /// while the toolbar is visible. Returns <c>true</c> to swallow the key
    /// (block it from the focused source app), <c>false</c> to pass it through.
    /// <para>
    /// Behavior:
    ///   • Bound single-character (A-Z) → fire the matching action + swallow.
    ///     If no captured text exists, the key passes through (don't eat typing
    ///     when there's nothing to act on).
    ///   • R37/R41: if no user template is bound to R/C, those keys fall through
    ///     to built-in toolbar shortcuts — R = Prompt, C = Copy — and swallow
    ///     only if the action actually fires (both require captured text).
    ///     These actions keep the toolbar visible. (R41: V/Paste removed.)
    ///   • Esc → hide the toolbar + swallow (escape hatch if the user changed
    ///     their mind).
    ///   • Any other key → pass through (so the source app keeps working while
    ///     the toolbar is up — e.g. backspace, arrows, digits).
    /// </para>
    /// </summary>
    private bool OnToolbarKeyPressed(int vkCode)
    {
        // R97: Esc no longer closes pinned windows. The pinned window is
        // permanently WS_EX_TOPMOST (and the ESC-close mechanism required
        // keeping the global keyboard hook armed while any pin existed),
        // which meant Esc was intercepted to close the pin instead of
        // reaching whatever the user actually wanted to close (a modal
        // dialog, an editor, etc.). Close a pinned window with double-click
        // or the right-click context menu instead (both handled inside
        // PinnedScreenshotWindow itself, no global hook involvement).
        const int vkEscape = 0x1B;
        if (vkCode == vkEscape)
        {
            try
            {
                // R47: if annotation mode is active, Esc exits annotation mode
                // (badges stay on overlay) without dismissing Ocean Eyes.
                if (Volatile.Read(ref _oceanEyesAnnotating) != 0)
                {
                    _logger.Info("OceanEyes", "Annotation: Esc → exit annotation mode.");
                    ExitAnnotationMode();
                    return true;
                }

                // R40: Esc clears the Ocean Eyes state too so a leftover
                // cached PNG / flag doesn't fire on the next session. Idempotent
                // — DismissOceanEyes is a no-op when the flag is already 0.
                // R42 fix: DismissOceanEyes now marshals UI ops to the UI
                // thread internally, so no direct _windowHost / SetEnabled
                // calls here (they'd crash on the hook thread).
                DismissOceanEyes();
            }
            catch (Exception exception)
            {
                _logger.Error("KeyboardHook", "Failed to dismiss toolbar on Esc.", exception);
            }
            return true;
        }

        // R47 Ctrl+Z: undo the most recent annotation (badge or shape). Only
        // fires during active annotation mode. Checked before other branches so
        // it works even if Ctrl is held (which would otherwise skip single-key
        // branches).
        const int vkZ = 0x5A;
        if (vkCode == vkZ &&
            (GetKeyState(VK_CONTROL) & 0x8000) != 0 &&
            Volatile.Read(ref _oceanEyesAnnotating) != 0)
        {
            try
            {
                _logger.Info("OceanEyes", "Annotation: Ctrl+Z → undo last annotation.");
                Dispatcher.UIThread.Post(() =>
                {
                    if (_annotationSession?.Undo() is not null)
                    {
                        _annotationOverlay?.RemoveLastAnnotation();
                    }
                });
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "Annotation Ctrl+Z failed.", exception);
            }
            return true;
        }

        // R48 tool switching: 0-5 keys switch annotation tool. Only fires during
        // active annotation mode. Keys 0x30-0x35 are before the A-Z filter.
        if (vkCode >= 0x30 && vkCode <= 0x35 &&
            Volatile.Read(ref _oceanEyesAnnotating) != 0)
        {
            try
            {
                var newTool = (AnnotationTool)(vkCode - 0x30);
                _currentAnnotationTool = newTool;
                string toolName = newTool switch
                {
                    AnnotationTool.Number => Strings.Runtime_Tool_Number,
                    AnnotationTool.Rectangle => Strings.Runtime_Tool_Rectangle,
                    AnnotationTool.Ellipse => Strings.Runtime_Tool_Ellipse,
                    AnnotationTool.Arrow => Strings.Runtime_Tool_Arrow,
                    AnnotationTool.Pen => Strings.Runtime_Tool_Pen,
                    AnnotationTool.Highlight => Strings.Runtime_Tool_Highlight,
                    _ => Strings.Runtime_Tool_Unknown,
                };
                _logger.Info("OceanEyes", $"Annotation: switched to tool {toolName} ({(int)newTool}).");
                Dispatcher.UIThread.Post(() =>
                {
                    _toolbarWindow.SetDiagnosticStatus(
                        string.Format(Strings.Runtime_Status_AnnotateCurrent, toolName, Strings.Runtime_Annotation_ToolHint));
                });
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "Annotation tool switch failed.", exception);
            }
            return true;
        }

        // R40 Ocean Eyes: Enter saves the cached PNG (if AutoSaveEnabled) and
        // copies it to the clipboard (if CopyToClipboardEnabled), then dismisses
        // the toolbar. Only fires when the Ocean Eyes flag is set — the
        // selection flow's Enter passes through unchanged (source app keeps its
        // Enter, e.g. newline in an editor).
        const int vkReturn = 0x0D;
        if (vkCode == vkReturn && Volatile.Read(ref _oceanEyesActive) != 0)
        {
            try
            {
                _logger.Info("KeyboardHook", "Ocean Eyes: Enter → save screenshot.");
                SaveOceanEyesScreenshot();
            }
            catch (Exception exception)
            {
                _logger.Error("KeyboardHook", "Ocean Eyes Enter failed.", exception);
                DismissOceanEyes();
            }
            return true;
        }

        // R44 color picker: P toggles the loupe on/off. Only fires during an
        // active Ocean Eyes session (the selection flow doesn't need it).
        // Skips the OCR-lazy gate entirely — the picker samples pixels, not
        // text, so OCR is irrelevant. P is not configurable in this iteration
        // (a future ToolbarShortcutSettings.ColorPickerKey could override it).
        const int vkPick = 0x50;
        if (vkCode == vkPick && Volatile.Read(ref _oceanEyesActive) != 0)
        {
            try
            {
                if (Volatile.Read(ref _colorPickerActive) != 0)
                {
                    _logger.Info("OceanEyes", "Color picker: P → cancel.");
                    HideColorPicker();
                }
                else
                {
                    _logger.Info("OceanEyes", "Color picker: P → start.");
                    StartColorPicker();
                }
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "Color picker toggle failed.", exception);
                HideColorPicker();
            }
            return true;
        }

        // R46 pinned screenshot: T drops the currently-cached PNG as an
        // always-on-top floating window. Only fires during an active Ocean
        // Eyes session (the selection flow doesn't need it). Skips the OCR
        // gate — pinning is a pure image operation, OCR is irrelevant.
        // R46 v5: T is now a TERMINAL action — after spawning the pinned
        // window, the user has committed to the screenshot and no longer
        // needs the overlay / toolbar. Call DismissOceanEyes to close both.
        // The pinned window outlives the session (DismissOceanEyes doesn't
        // touch _pinnedWindows — only runtime Dispose tears them down).
        // PNG snapshot is taken in PinOceanEyesScreenshot before the
        // Dispatcher.Post, so nulling _oceanEyesPng in DismissOceanEyes
        // can't race the pinned window's decode.
        const int vkPin = 0x54;
        if (vkCode == vkPin && Volatile.Read(ref _oceanEyesActive) != 0)
        {
            try
            {
                _logger.Info("OceanEyes", "Pin screenshot: T → spawn pinned window + dismiss Ocean Eyes.");
                PinOceanEyesScreenshot();
                // R97: PinOceanEyesScreenshot registers the window in
                // _pinnedWindows on a UI-thread Post, then calls
                // DismissOceanEyes (which disarms the hook unconditionally).
                // Esc no longer closes pins, so there is nothing to re-arm
                // the hook for after pinning — let it stay disarmed. Close a
                // pinned window with double-click or right-click instead.
                DismissOceanEyes();
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "Pin screenshot failed.", exception);
                DismissOceanEyes();
            }
            return true;
        }

        // R47 annotation mode: A toggles numbered badge placement. Only fires
        // during an active Ocean Eyes session. MUST be before the A-Z filter
        // (0x41-0x5A) because A=0x41 is the first letter — the OCR-lazy gate
        // below would swallow it otherwise.
        const int vkAnnotate = 0x41; // 'A'
        if (vkCode == vkAnnotate && Volatile.Read(ref _oceanEyesActive) != 0)
        {
            try
            {
                if (Volatile.Read(ref _oceanEyesAnnotating) != 0)
                {
                    _logger.Info("OceanEyes", "Annotation: A → exit annotation mode.");
                    ExitAnnotationMode();
                }
                else
                {
                    _logger.Info("OceanEyes", "Annotation: A → enter annotation mode.");
                    EnterAnnotationMode();
                }
            }
            catch (Exception exception)
            {
                _logger.Error("OceanEyes", "Annotation toggle failed.", exception);
                ExitAnnotationMode();
            }
            return true;
        }

        // R49 gallery: G opens the screenshot gallery (history browser for
        // ocean-eyes-*.png in the save folder). Does NOT dismiss Ocean Eyes
        // — the user can close the gallery and keep working in the current
        // session. MUST be before the A-Z filter, same reason as A above.
        const int vkGallery = 0x47; // 'G'
        if (vkCode == vkGallery && Volatile.Read(ref _oceanEyesActive) != 0)
        {
            _logger.Info("OceanEyes", "Gallery: G → open screenshot gallery (no dismiss).");
            ShowGallery();
            return true;
        }

        // Only single-character A-Z (0x41-0x5A) are eligible for shortcuts.
        if (vkCode < 0x41 || vkCode > 0x5A)
        {
            return false;
        }

        // R54 v2 bug fix: only dispatch action keys (F/J/Z/R/C and any
        // user-bound A-Z template) when the toolbar is actually visible.
        // Without this gate, pressing F with no visible toolbar would fall
        // through to DispatchToolbarActionKey → GetLastCapturedText (which has
        // a clipboard fallback) and wrongly trigger a translation. Pass the
        // key through to the focused app instead — F/J/Z have no action
        // semantics without a visible toolbar.
        // (R97: the hook is no longer kept armed for pins, so in practice the
        // only time this callback runs at all is while the toolbar / Ocean
        // Eyes is active — but this gate remains a correct, cheap defense.)
        // The OE-specific branches (Enter/T/P/A/G) gate on _oceanEyesActive
        // themselves and are unaffected.
        if (!_toolbarVisible)
        {
            return false;
        }

        string key = ((char)vkCode).ToString();

        // R41 Ocean Eyes lazy OCR: in Ocean Eyes mode, action keys (F/J/Z/R/C
        // or any user-bound A-Z) must NOT fire until OCR has produced text.
        // Two cases:
        //   • OCR already done (cache hit) → fall through to normal dispatch
        //     (PromptTemplate lookup → RunActionAsync). Zero added latency.
        //   • OCR not yet done → swallow the key now, kick off EnsureOceanEyesOcrAsync,
        //     and when it completes, re-dispatch THIS key on the UI thread.
        //     Subsequent keys find _oceanEyesOcrDone==1 and take the fast path.
        if (Volatile.Read(ref _oceanEyesActive) != 0 &&
            Volatile.Read(ref _oceanEyesOcrDone) == 0)
        {
            // Capture the key for the async redispatch. Fire-and-forget: the
            // hook thread can't block on OCR (~1s) without freezing the user's
            // keyboard, so we swallow now and redispatch later.
            string pendingKey = key;
            _ = Task.Run(async () =>
            {
                try
                {
                    string? ocrText = await EnsureOceanEyesOcrAsync().ConfigureAwait(true);
                    if (string.IsNullOrEmpty(ocrText))
                    {
                        // OCR failed — leave the toolbar in "识别失败" state.
                        // User can Esc or right-click to redraw.
                        Dispatcher.UIThread.Post(() =>
                            _toolbarWindow.SetDiagnosticStatus(Strings.Runtime_Status_OcrFailed));
                        return;
                    }
                    // OCR succeeded: redispatch the original key on the UI thread
                    // so it flows through the normal PromptTemplate / builtin
                    // shortcut path with the cached text now in _sessionManager.
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (Volatile.Read(ref _oceanEyesActive) == 0)
                        {
                            return; // user dismissed during OCR
                        }
                        DispatchToolbarActionKey(pendingKey);
                    });
                }
                catch (Exception exception)
                {
                    _logger.Error("OceanEyes", "Lazy OCR redispatch failed.", exception);
                }
            });
            return true; // swallow the key — redispatch will handle it
        }

        // R41: OCR done (or selection-flow mode) → dispatch the key through the
        // normal path. Extracted to DispatchToolbarActionKey so the lazy-OCR
        // redispatch (above) reuses the exact same logic on the UI thread.
        return DispatchToolbarActionKey(key);
    }

    /// <summary>
    /// R41: dispatches an A-Z toolbar action key through the PromptTemplate
    /// lookup (F/J/Z/custom) → RunActionAsync, or the builtin shortcut fallback
    /// (R/C). Returns true if swallowed, false if passed through. Called from:
    /// <list type="bullet">
    ///   <item><see cref="OnToolbarKeyPressed"/> after the Ocean Eyes lazy-OCR
    ///   gate passes (OCR done, or selection-flow mode).</item>
    ///   <item>The lazy-OCR completion callback (Dispatcher.UIThread.Post) to
    ///   redispatch the original key once OCR text is cached.</item>
    /// </list>
    /// Must run on the UI thread (RunActionAsync / TryInvokeBuiltinToolbarShortcut
    /// touch UI + _sessionManager).
    /// </summary>
    private bool DispatchToolbarActionKey(string key)
    {
        PromptTemplate? template;
        try
        {
            template = _promptTemplates.FindByShortcut(key);
        }
        catch (Exception exception)
        {
            _logger.Error("KeyboardHook", $"FindByShortcut('{key}') failed.", exception);
            return false;
        }

        if (template is null)
        {
            // R37: 内建工具栏快捷键 R/C 作为"用户未配置该键"时的兜底。它们
            // 不是 PromptTemplate（不属于翻译/总结/解释任一动作），而是工具栏
            // 自身的复制/提示按钮。两者都需已取词（按钮本身也 disabled 直到
            // 取词成功）。与 F/J/Z 不同：这两个动作不隐藏工具栏（用户可能
            // 复制完继续翻译），所以不调 SetEnabled(false)。
            return TryInvokeBuiltinToolbarShortcut(key);
        }

        string? text = _sessionManager.GetLastCapturedText();
        if (string.IsNullOrEmpty(text))
        {
            // Toolbar is visible but the last capture yielded nothing (e.g. the
            // user dragged over unselectable content). Pass the key through so
            // we don't eat typing that the user might be doing in the source app.
            return false;
        }

        try
        {
            _logger.Info("KeyboardHook",
                $"Shortcut '{key}' → action '{template.Id}' ('{template.Name}').");
            RunActionAsync(template.Id, text);
            // RunActionAsync hides the toolbar; disable dispatching so the
            // next keystroke goes to the source app, not the shortcut handler.
            _keyboardHook.SetEnabled(false);
        }
        catch (Exception exception)
        {
            _logger.Error("KeyboardHook",
                $"Failed to run action '{template.Id}' for shortcut '{key}'.", exception);
        }

        return true;
    }

    /// <summary>
    /// R37: 工具栏内建快捷键（默认 R/C/V，用户可在设置里改）的派发。在
    /// <see cref="OnToolbarKeyPressed"/> 中，当用户未给该键配置自定义
    /// PromptTemplate 时作为兜底入口。三个动作各自映射到一个工具栏按钮：
    /// <list type="bullet">
    ///   <item>PromptKey（默认 R）→ 打开提示词窗口（需已取词，否则透传）。</item>
    ///   <item>CopyKey（默认 C）→ 复制选中文本到剪贴板（需已取词，否则透传）。</item>
    /// </list>
    /// 返回 true 吞键，false 透传。R41: PasteKey 已删除。两个动作都不隐藏工具栏（用户可能复制完继续
    /// 做别的动作），所以不调 <c>_keyboardHook.SetEnabled(false)</c>。
    ///
    /// <para>
    /// <b>线程模型（关键）</b>：本方法跑在 keyboard hook 的后台线程上
    /// （<c>WH_KEYBOARD_LL</c> 回调），而复制/提示最终要调 Avalonia UI 线程
    /// API（<c>clipboard.SetTextAsync</c>、显示 PromptWindow）。直接同步调
    /// UI API 会崩（C 崩）或静默无效（R 没反应）。所以：
    /// <list type="number">
    ///   <item>吞键判断在 hook 线程同步完成——读 <see cref="GetLastCapturedText"/>
    ///     （线程安全）判断 R/C 是否有文本可操作；V 恒吞。</item>
    ///   <item>实际 UI 操作用 <c>Dispatcher.UIThread.Post</c> fire-and-forget
    ///     派发到 UI 线程（不阻塞 hook 线程，避免 <c>InvokeAsync</c> 同步等
    ///     待可能的死锁）。这是 codebase 的标准模式（见 App.axaml.cs 多处
    ///     chord/hotkey → UI 派发）。</item>
    /// </list>
    /// </para>
    /// </summary>
    private bool TryInvokeBuiltinToolbarShortcut(string key)
    {
        // Snapshot the current bindings (reference swap is atomic, so a settings
        // update from the UI thread mid-dispatch is safe — we use a consistent
        // snapshot for the whole decision).
        ToolbarShortcutSettings bindings = _toolbarShortcuts;

        // Resolve which built-in action (if any) this key is bound to. Null key
        // in settings = that action's shortcut is disabled.
        // R41: Paste removed — only Prompt + Copy remain.
        bool isCopy = KeysEqual(key, bindings.CopyKey);
        bool isPrompt = !isCopy && KeysEqual(key, bindings.PromptKey);
        if (!isCopy && !isPrompt)
        {
            return false;  // Not bound to any built-in toolbar shortcut — pass through.
        }

        // Pre-dispatch swallow decision (must be synchronous on the hook thread;
        // we can't block on the UI thread). Both Copy and Prompt need captured
        // text — mirror the buttons' IsEnabled. When empty, return false so the
        // key reaches the source app (don't eat typing the user might be doing).
        string? captured = _sessionManager.GetLastCapturedText();
        if (string.IsNullOrEmpty(captured))
        {
            return false;
        }

        // Dispatch the UI-touching work to the UI thread. Post is fire-and-forget;
        // we've already decided to swallow, so we don't need the call's result.
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (isCopy)
                    {
                        _toolbarWindow.InvokeCopyShortcut();
                    }
                    else
                    {
                        _toolbarWindow.InvokePromptShortcut();
                    }
                }
                catch (Exception exception)
                {
                    _logger.Error("KeyboardHook",
                        $"Built-in toolbar shortcut '{key}' failed on UI thread.", exception);
                }
            });
        }
        catch (Exception exception)
        {
            // Dispatcher.Post itself should not throw, but guard anyway so a
            // shutdown race never crashes the hook thread (which would kill the
            // whole keyboard hook chain for the session).
            _logger.Error("KeyboardHook",
                $"Failed to dispatch built-in toolbar shortcut '{key}'.", exception);
            return false;
        }

        _logger.Info("KeyboardHook", $"Built-in toolbar shortcut '{key}' dispatched.");
        return true;
    }

    /// <summary>
    /// Ordinal case-insensitive compare against a possibly-null shortcut key.
    /// Null/empty key = disabled, so it never matches a real pressed key.
    /// </summary>
    private static bool KeysEqual(string pressedKey, string? boundKey)
    {
        return !string.IsNullOrEmpty(boundKey) &&
               pressedKey.Equals(boundKey, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs a custom user prompt against the captured selection, using the
    /// active provider. The user's prompt becomes the system message; the
    /// selected text becomes the fenced user message. Direction defaults to
    /// → Simplified Chinese for non-Chinese source, → English for Chinese
    /// source (purely for the result-window language badge; the prompt itself
    /// controls the actual behavior).
    /// </summary>
    public void RunPromptAsync(string selectedText, string userPrompt)
    {
        bool hasCjk = selectedText.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        var request = new TranslationRequest(
            selectedText,
            hasCjk ? "zh-CN" : "en",
            hasCjk ? "en" : "zh-CN")
        {
            SystemPrompt = userPrompt,
        };

        _windowHost.Hide();
        StopKeyboardHookQuiet();
        TrackSessionTask(_translationManager.StartOrReplaceAsync(request));
    }

    private void OnRetryRequested()
    {
        TrackSessionTask(_translationManager.RetryAsync());
    }

    /// <summary>
    /// Replaces the originally selected text in the source app with the
    /// translated text: writes the translation to the clipboard, hides the
    /// result window (so the source app regains foreground), then injects a
    /// Ctrl+V chord. The 80ms delay lets the OS complete the focus switch
    /// before the paste lands.
    /// </summary>
    private async void OnReplaceRequested()
    {
        string? translated = _resultWindow.GetTranslatedText();
        if (string.IsNullOrEmpty(translated))
        {
            return;
        }

        var clipboard = (_resultWindow as Avalonia.Controls.TopLevel)?.Clipboard;
        if (clipboard is null)
        {
            _logger.Error("Replace", "Clipboard unavailable; aborting replace.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(translated);
        }
        catch (Exception exception)
        {
            _logger.Error("Replace", "Failed to write translation to clipboard.", exception);
            return;
        }

        // Hide so the source window becomes foreground again and receives the
        // injected Ctrl+V. Must happen before SendPasteChord.
        _resultWindow.Hide();

        await Task.Delay(80);

        try
        {
            var injector = new SendInputHelper();
            injector.SendPasteChord();
            _logger.Info("Replace", "Injected Ctrl+V to replace selection with translation.");
        }
        catch (Exception exception)
        {
            _logger.Error("Replace", "Failed to inject Ctrl+V paste chord.", exception);
        }

        TrackSessionTask(_translationManager.CancelAndHideAsync());
    }

    private void OnResultCloseRequested()
    {
        TrackSessionTask(_translationManager.CancelAndHideAsync());
    }

    public void Start()
    {
        _windowHost.Hide();
        _mouseHook.MouseEvent += OnMouseEvent;

        try
        {
            _mouseHook.Start();
            _logger.Info("Runtime", "Phase 1 selection runtime started.");
        }
        catch (Exception exception)
        {
            _toolbarWindow.SetDiagnosticStatus(Strings.Runtime_Status_MouseHookFailed);
            _logger.Error("Runtime", "Mouse hook startup failed.", exception);
        }

        // Install the persistent keyboard hook AFTER the mouse hook is running
        // and the runtime is otherwise ready. Doing this in the ctor (before
        // the clipboard message window is created) caused intermittent
        // "Clipboard message window startup timed out" crashes — see the note
        // in the constructor. Start() is called once at app startup after the
        // ctor completes, so this is still effectively a single install for
        // the whole app lifetime. The hook stays disabled (SetEnabled=false
        // by default) until the toolbar is shown.
        try
        {
            _keyboardHook.Start();
            _logger.Info("KeyboardHook", "Persistent keyboard hook installed at runtime start.");
        }
        catch (Exception exception)
        {
            // Non-fatal: toolbar still works via mouse clicks, just no keyboard shortcuts.
            _logger.Error("KeyboardHook", "Failed to install persistent keyboard hook; shortcuts disabled.", exception);
        }
    }

    private void OnMouseEvent(MouseEventData mouseEvent)
    {
        if (mouseEvent.IsInjected && mouseEvent.ExtraInfo == OurInjectedInputMarker)
        {
            return;
        }

        // R44 color picker: when the loupe is active, a left-button-down confirms
        // the pick (the pixel under the cursor). Short-circuit the rest of the
        // handler so the click doesn't ALSO dismiss the Ocean Eyes toolbar or
        // start a new selection session. Other mouse events (right-down, moves,
        // etc.) are ignored by the picker path — the loupe samples via its own
        // DispatcherTimer, not via hook-projected moves.
        if (Volatile.Read(ref _colorPickerActive) != 0)
        {
            if (mouseEvent.Message == MouseMessageType.LeftButtonDown)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        _colorPickerLoupe?.ConfirmPick();
                    }
                    catch (Exception exception)
                    {
                        _logger.Error("OceanEyes", "Color picker ConfirmPick failed.", exception);
                        HideColorPicker();
                    }
                });
            }
            return;
        }

        // R47/R48 annotation mode: routes mouse events based on current tool.
        // Short-circuit so the click doesn't trigger selection or toolbar
        // dismiss. Right-click and other events pass through.
        if (Volatile.Read(ref _oceanEyesAnnotating) != 0)
        {
            // Convert physical screen px → overlay DIP coordinates.
            RegionSelectOverlay? overlay = _annotationOverlay;
            double dpiScale = overlay?.RenderScaling ?? 1.0;
            double originX = 0;
            double originY = 0;
            if (overlay?.Screens?.Primary is { } primaryScreen)
            {
                originX = primaryScreen.Bounds.X;
                originY = primaryScreen.Bounds.Y;
            }
            double dipX = (mouseEvent.X - originX) / dpiScale;
            double dipY = (mouseEvent.Y - originY) / dpiScale;

            if (mouseEvent.Message == MouseMessageType.LeftButtonDown)
            {
                if (_currentAnnotationTool == AnnotationTool.Number)
                {
                    // Number tool: click to place badge (R47 behavior).
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (_annotationSession is { } session &&
                                Volatile.Read(ref _oceanEyesAnnotating) != 0)
                            {
                                NumberedBadgeAnnotation badge = session.PushBadge(dipX, dipY);
                                _annotationOverlay?.AddShape(badge);
                                _logger.Info("OceanEyes",
                                    $"Annotation: placed badge #{badge.Number} at ({dipX:F1}, {dipY:F1}).");
                            }
                        }
                        catch (Exception exception)
                        {
                            _logger.Error("OceanEyes", "Annotation badge placement failed.", exception);
                        }
                    });
                }
                else
                {
                    // Shape tools: start drag. Create live preview on UI thread.
                    _annotationDragStart = (dipX, dipY);
                    _annotationDragPoints.Clear();
                    _annotationDragPoints.Add((dipX, dipY));
                    _annotationDragging = true;
                    var tool = _currentAnnotationTool;
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            _annotationOverlay?.CreateLivePreview(tool, dipX, dipY);
                        }
                        catch (Exception exception)
                        {
                            _logger.Error("OceanEyes", "Annotation live preview creation failed.", exception);
                        }
                    });
                }
            }
            else if (mouseEvent.Message == MouseMessageType.MouseMove && _annotationDragging)
            {
                // Update live preview on UI thread.
                // For pen/highlight, also accumulate points.
                if (_currentAnnotationTool is AnnotationTool.Pen or AnnotationTool.Highlight)
                {
                    _annotationDragPoints.Add((dipX, dipY));
                }
                double startX = _annotationDragStart.X;
                double startY = _annotationDragStart.Y;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                        _annotationOverlay?.UpdateLivePreview(dipX, dipY, startX, startY, shift);
                    }
                    catch (Exception exception)
                    {
                        _logger.Error("OceanEyes", "Annotation live preview update failed.", exception);
                    }
                });
            }
            else if (mouseEvent.Message == MouseMessageType.LeftButtonUp && _annotationDragging)
            {
                // Finalize shape: remove live preview, create final item, push to session.
                _annotationDragging = false;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        FinalizeLivePreviewShape(dipX, dipY);
                    }
                    catch (Exception exception)
                    {
                        _logger.Error("OceanEyes", "Annotation finalize failed.", exception);
                    }
                });
            }
            return;
        }

        // Feed every event to the chord detector first. A chord (both buttons
        // down together) takes priority over normal selection / dismiss logic —
        // when a chord fires we bail out before the outside-click dismiss runs,
        // so the chord gesture isn't mistaken for a click-away.
        bool chordFired = _chordDetector.OnMouseEvent(mouseEvent);
        if (chordFired && Volatile.Read(ref _mouseChordEnabled) != 0)
        {
            return;
        }

        if (mouseEvent.Message == MouseMessageType.LeftButtonDown &&
            _windowHost.IsVisible &&
            !_windowHost.ContainsScreenPoint(mouseEvent.X, mouseEvent.Y))
        {
            TrackSessionTask(_sessionManager.DismissCurrentSessionAsync());
        }

        WindowsWindowContext context = _contextProvider.GetContext(mouseEvent.X, mouseEvent.Y);
        SelectionGesture? gesture = _gestureClassifier.Process(
            mouseEvent,
            context.RootWindowHandle,
            context.ProcessId);

        if (gesture is null)
        {
            return;
        }

        if (!_capturePolicyProvider.Resolve(gesture.SourceProcessId).DetectionEnabled)
        {
            return;
        }

        TrackSessionTask(_sessionManager.StartOrReplaceSessionAsync(gesture));
    }

    private void TrackSessionTask(Task task)
    {
        lock (_taskGate)
        {
            _sessionTasks.Add(task);
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                if (completedTask.IsFaulted)
                {
                    _logger.Error("Session", "Selection session failed.", completedTask.Exception);
                }

                lock (_taskGate)
                {
                    _sessionTasks.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _mouseHook.MouseEvent -= OnMouseEvent;
        _keyboardHook.KeyPressed -= OnToolbarKeyPressed;
        _toolbarWindow.TranslateRequested -= OnTranslateRequested;
        _resultWindow.RetryRequested -= OnRetryRequested;
        _resultWindow.ReplaceRequested -= OnReplaceRequested;
        _resultWindow.CloseRequested -= OnResultCloseRequested;
        _mouseHook.Dispose();
        _keyboardHook.Dispose();
        _sessionManager.Dispose();
        _translationManager.Dispose();
        _disposableProvider?.Dispose();
        _visionOcrClient?.Dispose();
        _visionBackend?.Dispose();
        // R44: close + dispose the color picker loupe if it was ever constructed.
        try
        {
            _colorPickerLoupe?.HideLoupe();
            _colorPickerLoupe?.Close();
        }
        catch (Exception exception)
        {
            _logger.Error("Runtime", "Color picker cleanup failed.", exception);
        }
        // R46: close all pinned screenshot windows. They outlive individual
        // Ocean Eyes sessions by design, but must be torn down when the
        // runtime itself disposes (app shutdown).
        try
        {
            foreach (var pinned in _pinnedWindows)
            {
                pinned.Hide();
                pinned.Dispose();
            }
            _pinnedWindows.Clear();
            _pinnedHosts.Clear();
        }
        catch (Exception exception)
        {
            _logger.Error("Runtime", "Pinned window cleanup failed.", exception);
        }
        // R49: close the gallery window if it happens to be open at shutdown.
        try
        {
            _galleryWindow?.Close();
            _galleryWindow = null;
        }
        catch (Exception exception)
        {
            _logger.Error("Runtime", "Gallery window cleanup failed.", exception);
        }
        _textCapture.Dispose();
        _logger.Info("Runtime", "Phase 1 selection runtime stopped.");
    }

    // ── R44 P/Invoke: GetCursorPos for the color picker sampler ──────────

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    // ── R47 P/Invoke: GetKeyState for Ctrl+Z detection ──────────────────

    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private sealed class ToolbarSessionView : ISelectionSessionView
    {
        private readonly ToolbarWindow _window;
        private readonly IWindowFocusController _windowHost;
        // R34: optional hooks so the runtime can start/stop the low-level
        // keyboard hook alongside the toolbar's visibility. Null = no-op.
        private readonly Action? _onToolbarShown;
        private readonly Action? _onToolbarHidden;

        public ToolbarSessionView(
            ToolbarWindow window,
            IWindowFocusController windowHost,
            Action? onToolbarShown = null,
            Action? onToolbarHidden = null)
        {
            _window = window;
            _windowHost = windowHost;
            _onToolbarShown = onToolbarShown;
            _onToolbarHidden = onToolbarHidden;
        }

        public void ShowToolbar(SelectionGesture gesture)
        {
            _window.ShowPending(gesture);
            // R35: Anchor the toolbar at the true bottom-right corner of the
            // drag rectangle so it works no matter which direction the user
            // dragged (LTR selection ends at mouse-up; RTL/upward selections
            // end at mouse-down — taking max of both yields the right corner).
            int anchorX = Math.Max(gesture.MouseUpX, gesture.MouseDownX);
            int anchorY = Math.Max(gesture.MouseUpY, gesture.MouseDownY);
            // ClampAnchor turns the anchor into the final window top-left,
            // handling: (a) the +16 offset, (b) flipping to the opposite side
            // of the anchor if it would overflow the screen right/bottom edge,
            // (c) clamping the top-left to the working area so the toolbar
            // never sits under the taskbar or in a monitor gap.
            Avalonia.PixelPoint topLeft = _window.ClampAnchor(anchorX, anchorY);
            _windowHost.ShowAtNoActivatePoint(topLeft.X, topLeft.Y);
            _onToolbarShown?.Invoke();
        }

        public void HideToolbar()
        {
            _windowHost.Hide();
            _onToolbarHidden?.Invoke();
        }

        public void SetCaptureResult(CaptureResult result)
        {
            _window.SetCaptureResult(result);
        }
    }

    private sealed class ResultTranslationView : ITranslationSessionView
    {
        private readonly ResultWindow _window;

        public ResultTranslationView(ResultWindow window)
        {
            _window = window;
        }

        public void ShowLoading(TranslationRequest request, string providerName) =>
            _window.ShowLoading(request, providerName);

        public void ShowResult(TranslationResult result) =>
            _window.ShowResult(result);

        public void AppendPartialResult(string chunk) =>
            _window.AppendPartialResult(chunk);

        public void ShowError(string userMessage) =>
            _window.ShowError(userMessage);

        public void Hide() => _window.Hide();
    }

}
