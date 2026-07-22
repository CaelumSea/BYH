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
using SelectionAssistant.Core.Annotation;

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
/// <b>v13 status:</b> animation = v9-style side slide-in (TranslateTransform
/// from (400,100) → (0,0), CubicEaseOut 300ms). v8-v12 tried various scale
/// pop-in animations (BackEaseOut / keyframe spring 0.5→1.15→1.0) but none
/// matched the user's expectation of "Mac spring from center" — the
/// RenderTransformOrigin refused to apply correctly on
/// ExtendClientAreaToDecorationsHint windows (scale grew from top-left).
/// User decided to defer the scale animation and ship the slide-in for now.
/// See handoff §3x §22 for the full exploration log + future fix ideas.
/// </para>
/// <para>
/// <b>R52:</b> magnetic snap during drag. The window snaps to screen work-area
/// edges and other pinned-window edges within an 8px threshold. Shift
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
    /// R46 v9/v13: the Window's RenderTransform TranslateTransform, used for
    /// the slide-in/slide-out animation. Initialized in the ctor from
    /// <see cref="Visual.RenderTransform"/> (set in AXAML on the Window).
    /// Initial X/Y in AXAML = (400, 100) — window renders offset to the
    /// bottom-right, offscreen-ish. Opened slides it to (0, 0).
    /// </summary>
    private TranslateTransform _slide = new(400, 100);

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

        // R46 v9/v13: grab the TranslateTransform declared as Window.RenderTransform
        // in AXAML. Avalonia codegen doesn't auto-generate a field for it
        // (Window-attribute-level elements aren't content children), so we
        // reach it via RenderTransform. The AXAML sets X=400 Y=100 initial
        // (offset to bottom-right, offscreen-ish).
        if (RenderTransform is TranslateTransform slide)
        {
            _slide = slide;
        }

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
        // R46 v9/v13: Opened triggers the slide-in (TranslateTransform
        // (400,100) → (0,0) with CubicEaseOut 300ms).
        Opened += (_, _) =>
        {
            ApplyScale();
            _slide.X = 0;
            _slide.Y = 0;
            Opacity = 1.0;
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

        // R46 v9/v13: slide-in/slide-out. CubicEaseOut gives fast deceleration
        // (the window slides in quickly then settles). 300ms matches the
        // macOS default for sheet/window slide.
        _slide.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = TimeSpan.FromMilliseconds(300),
                Easing = new CubicEaseOut(),
            },
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = TimeSpan.FromMilliseconds(300),
                Easing = new CubicEaseOut(),
            },
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
        ApplyScale();
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
    private void ApplyScale()
    {
        double dpi = RenderScaling > 0 ? RenderScaling : 1.0;
        _dpiScale = 1.0 / dpi;
        double effective = _userScale * _dpiScale;
        _scaleTransform.ScaleX = effective;
        _scaleTransform.ScaleY = effective;
    }

    /// <summary>
    /// R46 v9/v13: slides the window out to (400, 100) — reverse of the
    /// slide-in — giving a macOS-style slide-out on close. The runtime awaits
    /// this before calling Hide()+Dispose() so the user sees a smooth slide
    /// instead of an instant disappearance. Must run on the UI thread.
    /// </summary>
    public async Task AnimateOutAsync()
    {
        _animatingOut = true;
        Opacity = 0.0;
        _slide.X = 400;
        _slide.Y = 100;
        // Cover the slide transition (300ms) + small margin.
        await Task.Delay(330);
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
        int targetX = _startWindowPos.X + dx;
        int targetY = _startWindowPos.Y + dy;
        int w = (int)(ClientSize.Width * RenderScaling);
        int h = (int)(ClientSize.Height * RenderScaling);
        var targetRect = new PhysicalRect(targetX, targetY, targetX + w, targetY + h);

        bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
        var (snapped, hints) = MagneticSnapCalculator.ComputeSnap(
            targetRect,
            GetWorkAreas(),
            GetOtherPinnedBounds?.Invoke() ?? Array.Empty<PhysicalRect>(),
            shift);

        Position = new PixelPoint(snapped.X, snapped.Y);
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
        if (_dragCommitted)
        {
            var pos = Position;
            int w = (int)(ClientSize.Width * RenderScaling);
            int h = (int)(ClientSize.Height * RenderScaling);
            var rect = new PhysicalRect(pos.X, pos.Y, pos.X + w, pos.Y + h);
            bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
            var (snapped, _) = MagneticSnapCalculator.ComputeSnap(
                rect,
                GetWorkAreas(),
                GetOtherPinnedBounds?.Invoke() ?? Array.Empty<PhysicalRect>(),
                shift);
            Position = new PixelPoint(snapped.X, snapped.Y);
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
        var copy = new MenuItem { Header = "复制图像" };
        copy.Click += (_, _) => RequestCopy?.Invoke();

        var close = new MenuItem { Header = "关闭" };
        close.Click += (_, _) => RequestClose?.Invoke();

        var closeAll = new MenuItem { Header = "关闭所有" };
        closeAll.Click += (_, _) => RequestCloseAll?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(copy);
        menu.Items.Add(close);
        menu.Items.Add(closeAll);
        return menu;
    }

    // ── R52: magnetic snap helpers ────────────────────────────────────────

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
    /// across the window. The canvas is made visible when there are lines.
    /// </summary>
    private void UpdateSnapGuideCanvas(IReadOnlyList<SnapHint> hints)
    {
        SnapGuideCanvas.Children.Clear();

        if (hints.Count == 0)
        {
            SnapGuideCanvas.IsVisible = false;
            return;
        }

        double w = ClientSize.Width;
        double h = ClientSize.Height;
        var brush = new SolidColorBrush(SnapGuideColor);

        foreach (var hint in hints)
        {
            if (hint.Axis == SnapAxis.X)
            {
                // Vertical guide line at the snapped edge.
                double x = hint.Target switch
                {
                    SnapTarget.ScreenLeft or SnapTarget.WindowRight => 0,
                    SnapTarget.ScreenRight or SnapTarget.WindowLeft => w,
                    _ => 0,
                };
                SnapGuideCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(x, 0),
                    EndPoint = new Point(x, h),
                    Stroke = brush,
                    StrokeThickness = 2,
                });
            }
            else
            {
                // Horizontal guide line at the snapped edge.
                double y = hint.Target switch
                {
                    SnapTarget.ScreenTop or SnapTarget.WindowBottom => 0,
                    SnapTarget.ScreenBottom or SnapTarget.WindowTop => h,
                    _ => 0,
                };
                SnapGuideCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(0, y),
                    EndPoint = new Point(w, y),
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
