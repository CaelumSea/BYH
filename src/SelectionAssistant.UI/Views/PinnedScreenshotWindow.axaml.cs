using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using SelectionAssistant.Core.Annotation;
using SelectionAssistant.Core.I18n;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// R46: Pinned screenshot window — a clean, frameless, always-on-top window
/// that displays a captured PNG screenshot pinned to the desktop. The user
/// can drag the window (left-button drag anywhere on the image, with a 3px
/// threshold so single clicks still register), scale it with the mouse wheel
/// (×1.1 per notch, clamped to 10%-500%), close it with a double-click
/// (manually tracked via timestamp + distance, since Avalonia's built-in
/// DoubleTapped gesture is unreliable with PointerCapture on no-activate
/// windows), or right-click for a context menu (copy / close / close-all).
/// Multiple instances can coexist (one per T key press). The runtime owns the
/// lifecycle — Close() is intercepted and replaced with Hide(); the runtime
/// calls Dispose() when tearing down.
/// <para>
/// DPI + scaling architecture (v4): the captured PNG has dimensions in
/// <b>physical screen pixels</b> (the overlay's <c>Confirm()</c> multiplies
/// the canvas-logical rect by <see cref="Visual.RenderScaling"/>). Avalonia's
/// <see cref="Bitmap"/> defaults to 96 DPI, so on a non-100% scaled monitor
/// the bitmap's natural logical <c>Size</c> (= PixelSize / 1) would render
/// bigger than the original region. We wrap the <see cref="Image"/> in a
/// <see cref="LayoutTransformControl"/> holding a <see cref="ScaleTransform"/>
/// of <c>1 / RenderScaling</c>; the LayoutTransformControl re-measures, so
/// <see cref="Window.SizeToContent"/> = "WidthAndHeight" snaps the window to
/// exactly <c>PixelSize / RenderScaling</c> logical DIP — at any screen DPI
/// that's <c>PixelSize</c> physical pixels = the original capture footprint.
/// User zoom then multiplies on top of that 1.0 baseline.
/// </para>
/// <para>
/// <b>v14 (R56) status:</b> animation = symmetric scale pop. Pop-IN: card grows
/// 0.85 → ~1.05 overshoot → 1.0 (BackEaseOut 350ms). Pop-OUT (close): card
/// shrinks 1.0 → 0.85 (CubicEaseIn 200ms) + Opacity fade. Both driven by a
/// single <see cref="DispatcherTimer"/> (16ms, Render priority) that each tick
/// bakes the eased scale into a center-scaled <see cref="Matrix"/> and pushes
/// it as <see cref="TransformOperations"/> on the outer shadow Border. Three
/// bugs were fixed to get here (see handoff §R56): (1) <c>RenderTransformOrigin</c>
/// is broken on these frameless <c>ExtendClientAreaToDecorationsHint</c> windows,
/// so the center offset is baked into the matrix instead; (2) the pop starts
/// from <c>LayoutUpdated</c> (not <c>Opened</c>), where Bounds is final — during
/// Opened it's still physical-px/pre-DPI; (3) the DPI baseline is snapped
/// (<c>ApplyScale(animate:false)</c>) on open so its 120ms LayoutTransform
/// transition can't shrink Bounds under the running pop. TransformOperations
/// (not plain MatrixTransform) is used because the R47/R49 pan-zoom log proved
/// MatrixTransform silently fails on NativeAOT.
/// </para>
/// <para>
/// <b>R52:</b> magnetic snap during drag. The window snaps to screen work-area
/// edges and other pinned-window edges within a 20px threshold. Shift
/// temporarily disables snapping. A gold guide line appears during drag when a
/// snap edge is hit. The pure-function calculator is
/// <see cref="MagneticSnapCalculator.ComputeSnap"/>.
/// </para>
/// </summary>
public partial class PinnedScreenshotWindow : Window, IDisposable
{
    /// <summary>Minimum zoom factor (25%). Below this the pinned image gets too small to read or drag reliably.</summary>
    private const double MinScale = 0.25;
    /// <summary>Maximum zoom factor (500%).</summary>
    private const double MaxScale = 5.0;
    /// <summary>Multiplier applied per wheel notch.</summary>
    private const double ScaleStep = 1.1;
    /// <summary>
    /// Left-button movement (in physical pixels) required to commit a drag.
    /// Below this threshold the press is treated as a click (so the manual
    /// double-click detection in <see cref="OnPointerReleased"/> still works).
    /// </summary>
    private const double DragThreshold = 3.0;
    /// <summary>
    /// Maximum interval between two clicks for the pair to count as a
    /// double-click. Matches the Windows default (GetDoubleClickTime ≈ 500ms).
    /// </summary>
    private const int DoubleClickMs = 500;
    /// <summary>
    /// Maximum distance (in physical pixels) between two clicks for the pair
    /// to count as a double-click. Matches the Windows default
    /// (GetSystemMetrics(SM_CXDOUBLECLK) ≈ 4px; we use a slightly larger 8px
    /// to be forgiving of imprecise double-clicks).
    /// </summary>
    private const double DoubleClickPx = 8.0;

    // R52: magnetic snap constants and P/Invoke.
    private const int VK_SHIFT = 0x10;
    private static readonly Color SnapGuideColor = Color.FromArgb(0x99, 0xD9, 0xC2, 0x8A); // #FFD9C28A @ alpha=0.6

    // R55: transparent margin (DIP) around the image inside the window, left
    // blank so the large drop shadow (BoxShadow on the outer Border in AXAML)
    // has room to render. Snap/drag geometry must operate on the IMAGE rect,
    // not the (now larger) window rect, otherwise the image sits this many
    // physical pixels away from every screen/peer edge it snaps to. Keep this
    // in sync with the Margin on the outer shadow Border in the .axaml.
    private const double ShadowMarginDip = 24.0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private byte[]? _pngBytes;
    private Bitmap? _bitmap;

    /// <summary>
    /// User zoom factor on top of the DPI-correct baseline. 1.0 = exact
    /// physical-pixel match with the original screen region. Wheel changes
    /// this by <see cref="ScaleStep"/> or its reciprocal.
    /// </summary>
    private double _userScale = 1.0;

    /// <summary>
    /// The DPI-correcting baseline scale (1 / RenderScaling). Computed in
    /// <see cref="ApplyScale"/> once the window is opened and RenderScaling
    /// is finalized.
    /// </summary>
    private double _dpiScale = 1.0;

    /// <summary>The <see cref="ScaleTransform"/> attached to Scaler (wheel zoom).</summary>
    private readonly ScaleTransform _scaleTransform = new(1.0, 1.0);

    /// <summary>
    /// R56: the pop-in scale animation start factor (the card pops from this
    /// size up to 1.0). 0.85 = a modest 15% grow, like a macOS dock bounce.
    /// </summary>
    private const double PopStartScale = 0.85;

    /// <summary>
    /// R56: pop-IN animation duration. BackEaseOut adds a light overshoot; 350ms
    /// is long enough to read the overshoot but still snappy.
    /// </summary>
    private static readonly TimeSpan PopDuration = TimeSpan.FromMilliseconds(350);

    /// <summary>
    /// R56: pop-OUT (close) animation duration. Shorter than the pop-in so close
    /// feels decisive; the scale shrinks 1.0 → <see cref="PopStartScale"/> with a
    /// CubicEaseIn (accelerate into the shrink), paired with the Window.Transitions
    /// Opacity fade. This is the symmetric counterpart to the pop-in.
    /// </summary>
    private static readonly TimeSpan PopOutDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// R56: easing for the pop-OUT (close) shrink. CubicEaseIn accelerates into the
    /// shrink — reads as "sucked away", the natural inverse of the BackEaseOut pop.
    /// </summary>
    private static readonly Easing PopOutEasing = new CubicEaseIn();

    /// <summary>R56: which way the running pop animation is going.</summary>
    private enum PopDirection { None, In, Out }
    private PopDirection _popDirection = PopDirection.None;

    /// <summary>
    /// R56: the easing applied to the pop progress (0→1). BackEaseOut produces a
    /// single slight overshoot past 1.0 — the classic "pop" settle.
    /// </summary>
    private readonly Easing _popEasing = new BackEaseOut();

    /// <summary>
    /// R56: drives the pop-in. On each tick we compute
    /// <c>scale = _popEasing.Ease(elapsed/duration)</c>, bake it into a
    /// center-scaled <see cref="Matrix"/> and push it as a
    /// <c>TransformOperations</c> on <see cref="OuterShadowBorder"/>. The
    /// center offset is computed in the matrix (translate(-w/2,-h/2) × scale ×
    /// translate(w/2,h/2)), so we never depend on <c>RenderTransformOrigin</c>
    /// — which the v8-v12 exploration (handoff §22) proved is broken on
    /// <c>ExtendClientAreaToDecorationsHint</c> frameless windows even when set
    /// on an inner Border.
    /// </summary>
    private DispatcherTimer? _popTimer;
    private long _popStartTicks;

    /// <summary>
    /// R56: set true in <see cref="Opened"/>; <see cref="OnPopLayoutReady"/>
    /// consumes it once on the first <c>LayoutUpdated</c> after open and starts
    /// the pop-in. Gates the pop behind a layout pass so we read valid Bounds.
    /// </summary>
    private bool _popPending;

    private bool _isDragging;
    private bool _dragCommitted;
    private PixelPoint _dragStartScreen;
    private PixelPoint _startWindowPos;

    /// <summary>
    /// R46 v7: true while the close-animation is running. The Closing handler
    /// checks this to avoid double-Hiding — AnimateOut calls Hide explicitly
    /// when the animation completes.
    /// </summary>
    private bool _animatingOut;

    /// <summary>
    /// Timestamp (Environment.TickCount64) + screen position of the last
    /// left-click that didn't commit a drag. The next left-click within
    /// <see cref="DoubleClickMs"/> and <see cref="DoubleClickPx"/> raises
    /// <see cref="RequestClose"/>.
    /// </summary>
    private long _lastClickTicks;
    private PixelPoint _lastClickScreen;

    /// <summary>
    /// R52: callback injected by <c>SelectionRuntime</c> that returns the
    /// physical-pixel rects of all other pinned windows (excluding this one).
    /// Null when no runtime is attached (e.g. unit-test context).
    /// </summary>
    public Func<IReadOnlyList<PhysicalRect>>? GetOtherPinnedBounds { get; set; }

    public PinnedScreenshotWindow()
    {
        InitializeComponent();

        // R56: build the pop-in timer. 16ms ≈ 60fps. Each tick rebuilds a
        // center-scaled matrix and pushes it onto OuterShadowBorder.RenderTransform
        // via TransformOperations (the NativeAOT-reliable path proven by
        // GalleryWindow's pan/zoom). We never touch RenderTransformOrigin — its
        // center-anchor is broken on these frameless
        // ExtendClientAreaToDecorationsHint windows (v8-v12 log, handoff §22);
        // the center offset is baked into the matrix instead.
        // Run at Render priority: the diagnostic log showed the default
        // (Normal-priority) timer's FIRST tick landed at elapsed=140ms — the
        // window-open + DPI re-layout + Opacity transition starved it, so the
        // pop-in jumped to prog≈0.4 (scale≈1.05) and the 0.85→1.05 rise was
        // never drawn. Render priority runs right after layout each frame.
        _popTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            OnPopTick);

        // R56: wait for layout to settle before starting the pop-in (see the
        // Opened handler comment — Bounds is stale during Opened).
        OuterShadowBorder.LayoutUpdated += OnPopLayoutReady;

        // Intercept native Close so the runtime can drive Hide()+Dispose().
        // R46 v7: if an animate-out is in flight, let it finish by NOT hiding
        // here — AnimateOut calls Hide explicitly when done.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            if (!_animatingOut)
            {
                Hide();
            }
        };
        // RenderScaling is finalized only after the window is opened; reapply
        // the layout transform then so the DPI baseline is correct.
        // R56: Opened re-applies DPI scale and reveals the window, but does NOT
        // start the pop-in directly. The diagnostic log proved OuterShadowBorder
        // .Bounds is STALE during Opened (returns physical px 927×533 @ 153,71;
        // after layout settles it's DIP 530×305 @ 24,24 — the 1.75× ratio is
        // exactly RenderScaling). Applying the first pop frame on those stale
        // bounds bakes a ~30px wrong offset that the next tick corrects → the
        // "slides in from the side" the user saw. So we wait for LayoutUpdated
        // (fires after the measure/arrange pass finalises), where Bounds is
        // correct, then start the pop. See handoff §R47 "stop guessing, read
        // the log" — the BYH.log PopIn lines diagnosed this.
        Opened += (_, _) =>
        {
            ApplyScale(animate: false); // R56: snap DPI baseline (no transition)
            Opacity = 1.0;
            _popPending = true; // StartPopIn fires from OnPopLayoutReady.
        };

        // Attach the ScaleTransform to the LayoutTransformControl. We mutate
        // _scaleTransform.ScaleX/Y in ApplyScale.
        Scaler.LayoutTransform = _scaleTransform;

        // R46 v7: smooth wheel-zoom. DoubleTransition on ScaleX/Y makes
        // ApplyScale's changes interpolate over 120ms instead of snapping.
        _scaleTransform.Transitions = new Transitions
        {
            new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(120) },
            new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(120) },
        };

        // Pixel-preserving scaling: nearest-neighbour keeps source pixels
        // intact (drop on shrink, replicate on grow).
        RenderOptions.SetBitmapInterpolationMode(
            ScreenshotImage, BitmapInterpolationMode.None);

        // Left-button handlers drive BOTH drag and double-click detection.
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        // Wheel zoom.
        PointerWheelChanged += OnPointerWheelChanged;

        // Right-click context menu (copy / close / close-all).
        ContextMenu = BuildContextMenu();
    }

    /// <summary>
    /// Exposes the native HWND so the runtime can wrap it with
    /// <c>NoActivateWindowHost</c> (WS_EX_NOACTIVATE).
    /// </summary>
    public nint? NativeHandle => TryGetPlatformHandle()?.Handle;

    /// <summary>
    /// The raw PNG bytes of the pinned screenshot. Available after
    /// <see cref="ShowPng"/> has been called; used by the runtime to copy the
    /// image to the clipboard.
    /// </summary>
    public byte[]? PngBytes => _pngBytes;

    /// <summary>Raised when the user requests to copy the pinned image.</summary>
    public event Action? RequestCopy;

    /// <summary>Raised when the user requests to close this pinned window.</summary>
    public event Action? RequestClose;

    /// <summary>Raised when the user requests to close all pinned windows.</summary>
    public event Action? RequestCloseAll;

    /// <summary>
    /// Decodes the given PNG byte array, displays it at <c>_userScale == 1.0</c>
    /// (DPI-correct physical-pixel match), and shows the window.
    /// </summary>
    public void ShowPng(byte[] pngBytes)
    {
        _pngBytes = pngBytes;
        _bitmap?.Dispose();

        using var stream = new MemoryStream(pngBytes);
        _bitmap = new Bitmap(stream);
        ScreenshotImage.Source = _bitmap;
        _userScale = 1.0;

        Show();
        ApplyScale(animate: false); // R56: snap DPI baseline on open.
    }

    /// <summary>
    /// Disposes the decoded bitmap and clears references. The runtime calls
    /// this when tearing down the pinned window.
    /// </summary>
    public void Dispose()
    {
        ScreenshotImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _pngBytes = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Pushes <see cref="_userScale"/> + the DPI baseline into the
    /// <see cref="ScaleTransform"/> on <see cref="Scaler"/>.
    /// </summary>
    /// <param name="animate">
    /// When true (default), the 120ms DoubleTransition on ScaleX/Y smoothly
    /// interpolates — desirable for wheel zoom. When false, the transition is
    /// suppressed so the change snaps. The DPI baseline (1/RenderScaling) is a
    /// CORRECTION, not a user-visible zoom, so on open we snap it; otherwise the
    /// 120ms interpolate fights the R56 pop-in (the diagnostic log showed
    /// OuterShadowBorder.Bounds shrinking 822→470 over ~120ms during the
    /// DPI-shrink animation, and our center-scaling matrix re-read those
    /// changing bounds each frame → the pop drifted off-center = "slides in
    /// from the side").
    /// </param>
    private void ApplyScale(bool animate = true)
    {
        double dpi = RenderScaling > 0 ? RenderScaling : 1.0;
        _dpiScale = 1.0 / dpi;
        double effective = _userScale * _dpiScale;
        Transitions? saved = _scaleTransform.Transitions;
        if (!animate)
        {
            _scaleTransform.Transitions = null;
        }
        try
        {
            _scaleTransform.ScaleX = effective;
            _scaleTransform.ScaleY = effective;
        }
        finally
        {
            if (!animate)
            {
                _scaleTransform.Transitions = saved;
            }
        }
    }

    /// <summary>
    /// <summary>
    /// R56: pop-OUT close animation — the symmetric counterpart to the pop-in.
    /// Shrinks the card from 1.0 → <see cref="PopStartScale"/> (CubicEaseIn:
    /// accelerate into the shrink = "sucked away") while the Window.Transitions
    /// Opacity fades it out. The runtime awaits this before Hide()+Dispose() so
    /// the user sees a smooth shrink-and-fade, not an instant disappearance.
    /// Must run on the UI thread.
    /// </summary>
    public async Task AnimateOutAsync()
    {
        _animatingOut = true;
        // R56: stop any in-flight pop-IN so its direction flag doesn't conflict,
        // then start the pop-OUT. The shrink runs alongside the Opacity fade.
        StopPopTimer();
        StartPopOut();
        Opacity = 0.0;
        // Cover the pop-OUT (200ms) + small margin.
        await Task.Delay((int)PopOutDuration.TotalMilliseconds + 40);
    }

    /// <summary>
    /// R56: fires on OuterShadowBorder.LayoutUpdated. LayoutUpdated runs after
    /// the measure/arrange pass, so OuterShadowBorder.Bounds is final here
    /// (unlike during Opened, where it's still in physical px / pre-DPI). On the
    /// first LayoutUpdated after an open (<see cref="_popPending"/>), start the
    /// pop-in; ignore subsequent LayoutUpdated events.
    /// </summary>
    private void OnPopLayoutReady(object? sender, EventArgs e)
    {
        if (!_popPending)
        {
            return;
        }
        _popPending = false;
        StartPopIn();
    }

    /// <summary>
    /// R56: starts the pop-IN animation. Seeds the card at <see cref="PopStartScale"/>,
    /// records the start time, and starts the timer. Called from
    /// <see cref="OnPopLayoutReady"/> (post-layout, so Bounds is valid).
    /// </summary>
    private void StartPopIn()
    {
        // Render the first frame immediately at the shrunk start scale so the
        // window doesn't flash at full size before the timer's first tick.
        _popDirection = PopDirection.In;
        ApplyPopMatrix(PopStartScale);
        _popStartTicks = Environment.TickCount64;
        _popTimer?.Start();
    }

    /// <summary>
    /// R56: starts the pop-OUT (close) animation — the symmetric counterpart to
    /// the pop-in. Shrinks the card 1.0 → <see cref="PopStartScale"/> over
    /// <see cref="PopOutDuration"/> with <see cref="PopOutEasing"/> (CubicEaseIn:
    /// accelerate into the shrink = "sucked away"). The caller drives Opacity→0
    /// in parallel (Window.Transitions) and awaits ~PopOutDuration before Hide().
    /// </summary>
    private void StartPopOut()
    {
        _popDirection = PopDirection.Out;
        // First frame at full size (1.0 = identity); the shrink begins on tick#1.
        ApplyPopMatrix(1.0);
        _popStartTicks = Environment.TickCount64;
        _popTimer?.Start();
    }

    /// <summary>
    /// R56: stops the pop timer and (for pop-IN) resets the transform to identity.
    /// For pop-OUT we leave the shrunk frame in place — the caller hides the
    /// window next, so clearing it would flash full-size before vanish.
    /// </summary>
    private void StopPopTimer()
    {
        if (_popTimer is { } t)
        {
            t.Stop();
        }
        if (_popDirection != PopDirection.Out)
        {
            OuterShadowBorder.RenderTransform = null;
        }
        _popDirection = PopDirection.None;
    }

    /// <summary>
    /// R56: timer tick. Drives BOTH the pop-in and pop-out animations depending
    /// on <see cref="_popDirection"/>. Computes the eased progress, pushes the
    /// matching center-scale matrix, and stops the timer (clearing the
    /// transform) once the animation completes.
    /// </summary>
    private void OnPopTick(object? sender, EventArgs e)
    {
        bool isOut = _popDirection == PopDirection.Out;
        double durationMs = (isOut ? PopOutDuration : PopDuration).TotalMilliseconds;
        long elapsed = Environment.TickCount64 - _popStartTicks;
        double progress = durationMs <= 0 ? 1.0 : Math.Clamp(elapsed / durationMs, 0.0, 1.0);

        if (progress >= 1.0)
        {
            // Done. For pop-IN: clear the transform so the card sits at its true
            // 1.0 layout size. For pop-OUT: the caller (AnimateOutAsync) owns the
            // subsequent Hide(); leave the shrunk frame in place, it'll vanish on
            // Hide. Either way stop the timer.
            StopPopTimer();
            return;
        }

        // Treat the eased value as a PROGRESS fraction and interpolate scale.
        // Pop-IN: PopStartScale → 1.0 (BackEaseOut overshoots to ~1.05 first).
        // Pop-OUT: 1.0 → PopStartScale (CubicEaseIn accelerates into the shrink).
        double eased = (isOut ? PopOutEasing : _popEasing).Ease(progress);
        double scale = isOut
            ? 1.0 + (PopStartScale - 1.0) * eased   // 1.0 → 0.85
            : PopStartScale + (1.0 - PopStartScale) * eased;  // 0.85 → ~1.05 → 1.0

        ApplyPopMatrix(scale);
    }

    /// <summary>
    /// R56: builds a center-scaled matrix for <paramref name="scale"/> and
    /// pushes it onto <see cref="OuterShadowBorder"/> via
    /// <see cref="TransformOperations"/> (the NativeAOT-reliable path; plain
    /// MatrixTransform silently fails to apply per the R47/R49 pan-zoom log).
    /// The matrix encodes translate(-w/2,-h/2) × scale × translate(w/2,h/2) so
    /// the scale origin is the border's center — this is the whole reason we
    /// can't rely on RenderTransformOrigin (broken on frameless
    /// ExtendClientAreaToDecorationsHint windows per handoff §22).
    /// </summary>
    private void ApplyPopMatrix(double scale)
    {
        double w = OuterShadowBorder.Bounds.Width;
        double h = OuterShadowBorder.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }
        // (1 - scale) × center  =  the offset that keeps the center pinned.
        double offsetX = (1.0 - scale) * (w / 2.0);
        double offsetY = (1.0 - scale) * (h / 2.0);
        var matrix = new Matrix(scale, 0, 0, scale, offsetX, offsetY);
        var builder = new TransformOperations.Builder(1);
        builder.AppendMatrix(matrix);
        OuterShadowBorder.RenderTransform = builder.Build();
    }

    /// <summary>Left-button press anywhere on the image: arm a drag.</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind
            != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        _dragCommitted = false;
        _dragStartScreen = this.PointToScreen(e.GetPosition(this));
        _startWindowPos = Position;
        e.Pointer.Capture(this);
    }

    /// <summary>
    /// Pointer moved while left button is held: once movement exceeds
    /// <see cref="DragThreshold"/>, start updating the window position.
    /// R52: applies magnetic snap before setting Position, and draws
    /// gold guide lines when a snap edge is hit.
    /// </summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var current = this.PointToScreen(e.GetPosition(this));
        int dx = current.X - _dragStartScreen.X;
        int dy = current.Y - _dragStartScreen.Y;

        if (!_dragCommitted)
        {
            if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
            {
                return;
            }
            _dragCommitted = true;
        }

        // R52: compute target position then apply magnetic snap.
        // R55: snap the IMAGE rect (window rect inset by the shadow margin), not
        // the window rect, so the image edges align to screen/peer edges.
        int targetX = _startWindowPos.X + dx;
        int targetY = _startWindowPos.Y + dy;
        int w = (int)(ClientSize.Width * RenderScaling);
        int h = (int)(ClientSize.Height * RenderScaling);
        var targetRect = ImageRectForWindow(targetX, targetY, w, h);

        bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
        var (snapped, hints) = MagneticSnapCalculator.ComputeSnap(
            targetRect,
            GetWorkAreas(),
            GetOtherPinnedBounds?.Invoke() ?? Array.Empty<PhysicalRect>(),
            shift);

        // R55: snapped is the IMAGE top-left; convert back to window top-left.
        int mx = (int)PhysicalShadowMargin;
        Position = new PixelPoint(snapped.X - mx, snapped.Y - mx);
        UpdateSnapGuideCanvas(hints);
    }

    /// <summary>
    /// Left-button release: end drag, and if the press didn't commit a drag
    /// (i.e. was a click), check whether it's the second click of a
    /// double-click within <see cref="DoubleClickMs"/> and
    /// <see cref="DoubleClickPx"/> of the previous one. If so, raise
    /// <see cref="RequestClose"/>.
    /// R52: applies final magnetic snap on release and clears guide lines.
    /// </summary>
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        // R52: apply final snap on release (in case the release position is
        // within threshold but the last OnPointerMoved didn't snap).
        // R55: snap the IMAGE rect, then convert the snapped image top-left back
        // to window top-left (see OnPointerMoved).
        if (_dragCommitted)
        {
            var pos = Position;
            int w = (int)(ClientSize.Width * RenderScaling);
            int h = (int)(ClientSize.Height * RenderScaling);
            var rect = ImageRectForWindow(pos.X, pos.Y, w, h);
            bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
            var (snapped, _) = MagneticSnapCalculator.ComputeSnap(
                rect,
                GetWorkAreas(),
                GetOtherPinnedBounds?.Invoke() ?? Array.Empty<PhysicalRect>(),
                shift);
            int mx = (int)PhysicalShadowMargin;
            Position = new PixelPoint(snapped.X - mx, snapped.Y - mx);
        }

        // R52: clear snap guide lines on release.
        ClearSnapGuides();

        bool wasClick = !_dragCommitted;
        _isDragging = false;
        _dragCommitted = false;
        e.Pointer.Capture(null);

        if (!wasClick)
        {
            _lastClickTicks = 0;
            return;
        }

        long now = Environment.TickCount64;
        var screenPos = this.PointToScreen(e.GetPosition(this));
        if (_lastClickTicks != 0 &&
            (now - _lastClickTicks) <= DoubleClickMs &&
            Math.Abs(screenPos.X - _lastClickScreen.X) <= DoubleClickPx &&
            Math.Abs(screenPos.Y - _lastClickScreen.Y) <= DoubleClickPx)
        {
            _lastClickTicks = 0;
            RequestClose?.Invoke();
        }
        else
        {
            _lastClickTicks = now;
            _lastClickScreen = screenPos;
        }
    }

    /// <summary>
    /// Mouse wheel: scale the pinned image. Up = zoom in, down = zoom out.
    /// Each notch multiplies/divides by <see cref="ScaleStep"/>; clamped to
    /// [<see cref="MinScale"/>, <see cref="MaxScale"/>].
    /// </summary>
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_bitmap is null)
        {
            return;
        }

        double step = e.Delta.Y > 0 ? ScaleStep : 1.0 / ScaleStep;
        double next = Math.Clamp(_userScale * step, MinScale, MaxScale);
        if (Math.Abs(next - _userScale) < 0.001)
        {
            return;
        }

        _userScale = next;
        ApplyScale();
        e.Handled = true;
    }

    /// <summary>
    /// Builds the right-click context menu with copy, close, and close-all
    /// actions.
    /// </summary>
    private ContextMenu BuildContextMenu()
    {
        var copy = new MenuItem { Header = Strings.Pinned_CopyImage };
        copy.Click += (_, _) => RequestCopy?.Invoke();

        var close = new MenuItem { Header = Strings.Common_Close };
        close.Click += (_, _) => RequestClose?.Invoke();

        var closeAll = new MenuItem { Header = Strings.Pinned_CloseAll };
        closeAll.Click += (_, _) => RequestCloseAll?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(copy);
        menu.Items.Add(close);
        menu.Items.Add(closeAll);
        return menu;
    }

    // ── R52: magnetic snap helpers ────────────────────────────────────────

    /// <summary>
    /// R55: physical-pixel size of the transparent shadow margin on one side
    /// (= <see cref="ShadowMarginDip"/> × RenderScaling). The shadow Border in
    /// the .axaml adds this much blank space on all four sides of the image, so
    /// the window is <c>image + 2×margin</c> in each dimension.
    /// </summary>
    private double PhysicalShadowMargin => ShadowMarginDip * RenderScaling;

    /// <summary>
    /// R55: returns the physical-pixel rect of the IMAGE (not the window) when
    /// the window's top-left is at <paramref name="windowX"/>,<paramref name="windowY"/>.
    /// Snap math must run against this rect so the image edges — not the blank
    /// shadow margin — align to screen/peer edges. <paramref name="windowW"/>/
    /// <paramref name="windowH"/> are the window's physical size.
    /// </summary>
    private PhysicalRect ImageRectForWindow(int windowX, int windowY, int windowW, int windowH)
    {
        // Delegate to the pure, unit-tested MagneticSnapCalculator.InsetRect so
        // the inset math lives in one place. margin is the physical shadow margin.
        var windowRect = new PhysicalRect(windowX, windowY, windowX + windowW, windowY + windowH);
        return MagneticSnapCalculator.InsetRect(windowRect, PhysicalShadowMargin);
    }

    /// <summary>
    /// R55: the current physical-pixel rect of the IMAGE (window rect inset by
    /// the shadow margin on all four sides). Exposed publicly so the runtime's
    /// <c>GetOtherPinnedBounds</c> callback can report peer image rects — peer
    /// snapping must align image-to-image edges, not window-to-window, otherwise
    /// two pinned windows sit a full shadow-margin apart with a visible gap.
    /// </summary>
    public PhysicalRect ImagePhysicalRect
    {
        get
        {
            var pos = Position;
            double scaling = RenderScaling > 0 ? RenderScaling : 1.0;
            int w = (int)(ClientSize.Width * scaling);
            int h = (int)(ClientSize.Height * scaling);
            return ImageRectForWindow(pos.X, pos.Y, w, h);
        }
    }

    /// <summary>
    /// Returns the physical-pixel work areas for all screens.
    /// Uses Avalonia's <c>Screens.AllScreens</c> and scales
    /// <c>WorkingArea</c> by <c>RenderScaling</c> to get physical pixels.
    /// </summary>
    private IReadOnlyList<PhysicalRect> GetWorkAreas()
    {
        var screens = Screens?.All;
        if (screens is null || screens.Count == 0)
        {
            return Array.Empty<PhysicalRect>();
        }

        // Avalonia's Screen.WorkingArea is a PixelRect — already in physical
        // pixels (NOT DIP). Do NOT multiply by RenderScaling; doing so makes
        // every edge coordinate unreachable (e.g. at 125% DPI a 1920px screen
        // edge becomes 2400px, so snap targets sit beyond the visible screen).
        var result = new List<PhysicalRect>(screens.Count);
        foreach (var screen in screens)
        {
            var wa = screen.WorkingArea;
            result.Add(new PhysicalRect(wa.X, wa.Y, wa.X + wa.Width, wa.Y + wa.Height));
        }

        return result;
    }

    /// <summary>
    /// Draws gold guide lines on <see cref="SnapGuideCanvas"/> for the given
    /// snap hints. Each hint produces a line at the snapped edge, extending
    /// along the IMAGE edge (not the outer shadow-margin edge), so the guide
    /// visually marks where the image itself aligned. The canvas is made
    /// visible when there are lines.
    /// </summary>
    private void UpdateSnapGuideCanvas(IReadOnlyList<SnapHint> hints)
    {
        SnapGuideCanvas.Children.Clear();

        if (hints.Count == 0)
        {
            SnapGuideCanvas.IsVisible = false;
            return;
        }

        // R55: the image sits inside a ShadowMarginDip border on every side.
        // Guide lines run along the image's own edges (m..w-m, m..h-m).
        double m = ShadowMarginDip;
        double w = ClientSize.Width;
        double h = ClientSize.Height;
        var brush = new SolidColorBrush(SnapGuideColor);

        foreach (var hint in hints)
        {
            if (hint.Axis == SnapAxis.X)
            {
                // Vertical guide line at the snapped image edge.
                double x = hint.Target switch
                {
                    SnapTarget.ScreenLeft or SnapTarget.WindowRight => m,
                    SnapTarget.ScreenRight or SnapTarget.WindowLeft => w - m,
                    _ => m,
                };
                SnapGuideCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(x, m),
                    EndPoint = new Point(x, h - m),
                    Stroke = brush,
                    StrokeThickness = 2,
                });
            }
            else
            {
                // Horizontal guide line at the snapped image edge.
                double y = hint.Target switch
                {
                    SnapTarget.ScreenTop or SnapTarget.WindowBottom => m,
                    SnapTarget.ScreenBottom or SnapTarget.WindowTop => h - m,
                    _ => m,
                };
                SnapGuideCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(m, y),
                    EndPoint = new Point(w - m, y),
                    Stroke = brush,
                    StrokeThickness = 2,
                });
            }
        }

        SnapGuideCanvas.IsVisible = true;
    }

    /// <summary>
    /// Clears all guide lines from <see cref="SnapGuideCanvas"/> and hides it.
    /// </summary>
    private void ClearSnapGuides()
    {
        SnapGuideCanvas.Children.Clear();
        SnapGuideCanvas.IsVisible = false;
    }
}
