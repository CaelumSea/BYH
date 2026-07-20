using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// R24: full-screen dimmed overlay that lets the user select (and fine-tune) a
/// rectangular screen region for OCR. Shown from the QuickTools "画框 OCR"
/// button. On confirm, raises <see cref="RegionSelected" /> with the rectangle
/// in <b>physical screen pixels</b> (what the screenshot layer needs). On
/// cancel, raises <see cref="RegionCancelled" /> and hides.
/// </summary>
/// <remarks>
/// Coordinate story: Avalonia lays out + reports pointer positions in
/// device-independent (logical) pixels. The screenshot capture / the mouse hook
/// work in physical pixels. <see cref="RenderScaling" /> is the factor (1.0 at
/// 100%, 1.5 at 150%, …). Going logical→physical REQUIRES multiplying (the
/// inverse of the chord-positioning bug, which wrongly multiplied a value that
/// was already physical).
/// </remarks>
public partial class RegionSelectOverlay : Window
{
    private const double MinSize = 8;        // smallest selectable rect
    private const double HandleHalf = 6;     // handle is 12px, half for centering

    /// <summary>
    /// Minimum interval between two UIA auto-box polls while the mouse roams
    /// the dim background. UIA's ElementFromPoint is comparatively expensive
    /// (and runs on a dedicated MTA worker, round-tripping through the queue),
    /// so we throttle to keep the overlay smooth on high-DPI / multi-element
    /// desktops. 40ms ≈ 25Hz is responsive without saturating the MTA worker.
    /// Lowered from 80ms after removing the SW_HIDE/SW_SHOW flicker workaround
    /// (now using UIA_WindowVisibilityOverridden, which has zero per-poll cost).
    /// </summary>
    private const int TrackingThrottleMs = 40;

    private bool _allowClose;
    private double _rectLeft, _rectTop, _rectWidth, _rectHeight;

    // Active drag operation.
    private DragMode _mode = DragMode.None;
    private Point _dragStart;                 // logical px, relative to canvas
    private double _origLeft, _origTop, _origWidth, _origHeight;

    // R24 live-tracking state: when set, the overlay polls UIA on background
    // MouseMove and moves the auto-box to follow the element under the cursor.
    // Tracking stops the moment the user starts any drag (draw/move/resize) so
    // manual edits are never fought over. Set by <see cref="EnableLiveTracking"/>.
    private Func<int, int, OverlayRect?>? _tracker;
    private bool _userTouchedRect;             // user has drawn/edited once
    private DateTime _lastTrackAt = DateTime.MinValue;

    // Latched true the moment ESC/Cancel fires. TryLiveTrack checks this before
    // the (potentially ~30ms-blocking) UIA round-trip, so ESC is responsive
    // even if a MouseMove landed a hair before the keypress. Without this, the
    // user perceives ESC as "laggy" because it has to wait for the in-flight
    // GetElementBoundsAt (which blocks the UI thread) to return.
    private bool _cancelling;

    // R42: true once the user confirms a rect (single-click on UIA box, or
    // release after a drag). While true: the overlay stays visible (locked on
    // the confirmed frame), the rect doesn't follow UIA, left-clicks are
    // ignored (the user must press F/J/Z/R/C/Enter/Esc to proceed), and
    // right-click resets (clears the rect + re-arms UIA tracking).
    private bool _confirmed;

    // Physical screen-pixel origin of the overlay window. Canvas-local logical
    // coords + (this origin × scaling) = physical screen coords that UIA needs.
    private int _physOriginX, _physOriginY;

    public RegionSelectOverlay()
    {
        InitializeComponent();
        Closing += (_, eventArgs) =>
        {
            if (_allowClose)
            {
                return;
            }

            eventArgs.Cancel = true;
            Cancel();   // window-close gesture (Alt+F4) = cancel
        };

        // Each handle controls a different edge/corner. Wire their PointerPressed.
        HandleNW.PointerPressed += (_, e) => BeginHandleDrag(DragMode.ResizeNW, e);
        HandleN.PointerPressed += (_, e) => BeginHandleDrag(DragMode.ResizeN, e);
        HandleNE.PointerPressed += (_, e) => BeginHandleDrag(DragMode.ResizeNE, e);
        HandleE.PointerPressed += (_, e) => BeginHandleDrag(DragMode.ResizeE, e);
        HandleSE.PointerPressed += (_, e) => BeginHandleDrag(DragMode.ResizeSE, e);
        HandleS.PointerPressed += (_, e) => BeginHandleDrag(DragMode.ResizeS, e);
        HandleSW.PointerPressed += (_, e) => BeginHandleDrag(DragMode.ResizeSW, e);
        HandleW.PointerPressed += (_, e) => BeginHandleDrag(DragMode.ResizeW, e);

        // R41: confirmation is now left-button-release (OnCanvasPointerReleased
        // → Confirm). The old DoubleTapped path is removed — release is faster
        // and matches the user's "left-click = confirm" mental model.

        // Keep handles + size badge glued to the rect as it changes.
        LayoutUpdated += (_, _) => PositionAdorners();
    }

    /// <summary>Raised with the selected region in physical screen pixels.</summary>
    public event Action<int, int, int, int>? RegionSelected;

    /// <summary>Raised when the user cancels (ESC / right-click / close).</summary>
    public event Action? RegionCancelled;

    /// <summary>
    /// R41: raised when the user requests a re-draw (right-click while the
    /// toolbar is up). The overlay stays open with a cleared rect + UIA
    /// tracking re-armed; App.axaml.cs hides the toolbar + clears OCR cache.
    /// Distinct from <see cref="RegionCancelled"/> (which exits entirely).
    /// </summary>
    public event Action? RegionReset;

    public void PrepareForShutdown() => _allowClose = true;

    /// <summary>
    /// Marks the overlay window as <b>invisible to UI Automation</b> so UIA's
    /// <c>ElementFromPoint</c> skips it and returns the desktop element
    /// underneath (instead of this full-screen Topmost window).
    /// </summary>
    /// <remarks>
    /// <b>Mechanism</b>: sets the undocumented <c>UIA_WindowVisibilityOverridden</c>
    /// window property to <c>2</c> (= "treat as invisible for UIA"). UIA's
    /// internal z-order walk honors this property and skips the window,
    /// exposing whatever sits below it. The window itself is unchanged — it's
    /// still fully visible on screen, still receives Avalonia pointer events
    /// normally, still has its normal Z-order. Only UIA's view of it changes.
    /// <para>
    /// <b>Why not WS_EX_TRANSPARENT?</b> That flag is widely cited for this
    /// purpose but is dangerous in practice. It only causes click-through when
    /// paired with <c>WS_EX_LAYERED</c> — and adding <c>WS_EX_LAYERED</c> to
    /// an Avalonia window (which uses <c>WS_EX_NOREDIRECTIONBITMAP</c> /
    /// DirectComposition) breaks pointer routing entirely: clicks fall through
    /// to the desktop, Avalonia never sees <c>OnPointerMoved</c>, the UI
    /// freezes. Without <c>WS_EX_LAYERED</c>, <c>WS_EX_TRANSPARENT</c> is a
    /// no-op for hit-testing. So the property-based approach is the only safe
    /// way to hide the window from UIA without breaking input.
    /// </para>
    /// <b>This pattern is borrowed from the Everywhere app's
    /// <c>ScreenSelectionSession.OnOpened</c></b>, which uses the identical
    /// <c>SetProp("UIA_WindowVisibilityOverridden", 2)</c> call.
    /// </remarks>
    public void MarkInvisibleToUia()
    {
        nint hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0)
        {
            return;
        }

        // Idempotent: SetProp overwrites if the property already exists.
        // Value 2 = override visibility to "invisible" for UIA clients.
        SetProp(hwnd, "UIA_WindowVisibilityOverridden", (IntPtr)2);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(nint hWnd, string lpString, IntPtr hData);

    /// <summary>
    /// Enables live UIA auto-box tracking. While the overlay is open and the
    /// user has not yet started drawing/moving/resizing, the rect follows the
    /// element under the cursor (polled on MouseMove, throttled to
    /// <see cref="TrackingThrottleMs"/>). Pass null to disable.
    /// </summary>
    /// <remarks>
    /// The tracker runs <c>ElementFromPoint</c> on the UIA backend's MTA worker
    /// and blocks the UI thread for &lt;30ms per call; throttling keeps it
    /// well under one frame. Once any drag starts, <c>_userTouchedRect</c>
    /// latches true and tracking silently stops so manual edits win.
    /// </remarks>
    public void EnableLiveTracking(Func<int, int, OverlayRect?>? tracker)
    {
        _tracker = tracker;
        _userTouchedRect = false;
        _lastTrackAt = DateTime.MinValue;
    }

    /// <summary>
    /// Shows the overlay full-screen (over the primary screen's working area).
    /// If <paramref name="initialRect" /> is non-null (a UIA auto-box), the rect
    /// is shown pre-placed for the user to adjust; otherwise the overlay opens
    /// in free-draw mode (drag on the dim background to draw a new rect).
    /// Coordinates in <paramref name="initialRect" /> are physical px from UIA.
    /// </summary>
    public void ShowWithInitialRect(OverlayRect? initialRect)
    {
        CoverPrimaryScreen();

        double scaling = RenderScaling;
        if (initialRect is { Width: > 0, Height: > 0 } r)
        {
            // physical → logical for display.
            _rectLeft = r.X / scaling;
            _rectTop = r.Y / scaling;
            _rectWidth = r.Width / scaling;
            _rectHeight = r.Height / scaling;
        }
        else
        {
            // Start with nothing drawn; user drags on the background to create.
            _rectLeft = _rectTop = _rectWidth = _rectHeight = 0;
        }

        // A fresh open: forget any prior manual edits so live UIA tracking can
        // resume. Latched true again on the next PointerPressed (draw/move/resize).
        _userTouchedRect = false;
        _lastTrackAt = DateTime.MinValue;
        _cancelling = false;
        _confirmed = false;

        ApplyRectToVisual();
        if (!IsVisible)
        {
            Show();
        }

        // Hide this window from UI Automation so ElementFromPoint (used by live
        // tracking) sees the desktop element underneath, not the overlay.
        // Applied after Show() so the HWND exists. Idempotent.
        MarkInvisibleToUia();

        Activate();
    }

    private void CoverPrimaryScreen()
    {
        // Size the window to the primary screen's bounds so the overlay covers
        // everything. Avalonia reports screens in physical px; convert to logical
        // for the window size. (Multi-monitor: only the primary is covered for
        // now — cross-monitor drag is a later refinement.)
        var screen = Screens.Primary;
        if (screen is not null)
        {
            double scaling = RenderScaling;
            var bounds = screen.Bounds;
            Position = new PixelPoint(bounds.X, bounds.Y);
            Width = bounds.Width / scaling;
            Height = bounds.Height / scaling;
            WindowState = WindowState.Normal;
            // Remember the physical origin so live UIA tracking can translate
            // canvas-local logical coords into physical screen coords (UIA works
            // in physical px). bounds.X/Y are normally 0 on a single-monitor
            // setup but non-zero if the primary monitor's top-left isn't at 0,0.
            _physOriginX = bounds.X;
            _physOriginY = bounds.Y;
        }
        else
        {
            WindowState = WindowState.Maximized;
            _physOriginX = 0;
            _physOriginY = 0;
        }
    }

    // ── R42 pointer model ──
    // SelectionRect is NOT hit-test-visible (IsHitTestVisible=False in AXAML),
    // so every click on the canvas — whether inside or outside the current rect
    // — lands here. This unifies "drag inside the UIA box" and "drag outside"
    // into a single Draw path (the R41 complaint "must drag outside the box").
    //
    // Click vs drag is disambiguated by a 5px threshold:
    //   • Press → DrawPending (rect NOT cleared; UIA box stays visible).
    //   • Move > 5px while DrawPending → upgrade to Draw (start fresh rect).
    //   • Release while still DrawPending → single click → Confirm (locks the
    //     current UIA-placed or previously-drawn rect). Fixes "click cancels".
    //
    // After Confirm (_confirmed=true): left-clicks are ignored (the user must
    // press F/J/Z/R/C/Enter/Esc to proceed); right-click resets (redraw).
    // Resize still works via the 8 handles (their own PointerPressed handlers
    // call BeginHandleDrag and stopPropagation before this handler runs).

    private const double DrawThreshold = 5;  // px; < this = click, >= this = drag

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Right button: context-dependent.
        //   • _confirmed (toolbar up) → Reset (redraw).
        //   • drawing phase → Cancel (existing behavior).
        // R42 fix: IsPrimary checks pointer identity (first finger), NOT the
        // mouse button. Right-click is still the primary pointer. Use
        // GetCurrentPoint to detect which button was pressed.
        if (e.GetCurrentPoint(RootCanvas).Properties.IsRightButtonPressed)
        {
            if (_confirmed)
            {
                Reset();
            }
            else
            {
                Cancel();
            }
            return;
        }

        // Left button while confirmed: ignore. The user must use the keyboard
        // (F/J/Z/R/C/Enter/Esc) to proceed. This prevents accidental redraws
        // when the user clicks around while deciding what to do.
        if (_confirmed)
        {
            return;
        }

        // Left button during drawing phase: enter DrawPending. DO NOT clear the
        // rect here — if this turns out to be a single click (release before
        // crossing DrawThreshold), we want the UIA-placed rect to remain so
        // Confirm can lock it. Upgrading to Draw (in OnCanvasPointerMoved) is
        // what clears + restarts the rect.
        _dragStart = e.GetPosition(RootCanvas);
        _mode = DragMode.DrawPending;
        e.Pointer.Capture(RootCanvas);
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_mode == DragMode.None)
        {
            // Idle (no button held): feed the cursor to the UIA tracker so the
            // auto-box follows the element under the mouse, unless the user has
            // already drawn/edited once or tracking is disabled. Throttled.
            TryLiveTrack(e.GetPosition(RootCanvas));
            return;
        }

        Point pos = e.GetPosition(RootCanvas);

        // R42: DrawPending upgrades to Draw once movement crosses the threshold.
        // This is the moment we take manual control (stop UIA tracking) and
        // START a fresh rect from the press point (overwriting the UIA box).
        if (_mode == DragMode.DrawPending)
        {
            double dx = pos.X - _dragStart.X;
            double dy = pos.Y - _dragStart.Y;
            if (Math.Abs(dx) < DrawThreshold && Math.Abs(dy) < DrawThreshold)
            {
                return;  // still a potential click — keep the UIA rect intact
            }

            // Threshold crossed: commit to a redraw. Latch manual control + zero
            // the rect at the press point so Draw below extends it.
            _userTouchedRect = true;
            _mode = DragMode.Draw;
            _rectLeft = _dragStart.X;
            _rectTop = _dragStart.Y;
            _rectWidth = 0;
            _rectHeight = 0;
        }

        // Active drag (Draw or Resize*).
        _userTouchedRect = true;
        switch (_mode)
        {
            case DragMode.Draw:
                _rectLeft = Math.Min(_dragStart.X, pos.X);
                _rectTop = Math.Min(_dragStart.Y, pos.Y);
                _rectWidth = Math.Abs(pos.X - _dragStart.X);
                _rectHeight = Math.Abs(pos.Y - _dragStart.Y);
                break;
            case DragMode.ResizeNW:
            case DragMode.ResizeN:
            case DragMode.ResizeNE:
            case DragMode.ResizeE:
            case DragMode.ResizeSE:
            case DragMode.ResizeS:
            case DragMode.ResizeSW:
            case DragMode.ResizeW:
                ApplyResize(pos);
                break;
        }

        ApplyRectToVisual();
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_mode == DragMode.None)
        {
            return;
        }

        e.Pointer.Capture(null);
        DragMode wasMode = _mode;
        _mode = DragMode.None;
        ApplyRectToVisual();

        // R42: any release confirms the current rect.
        //   • DrawPending release (no movement) = single click → Confirm the
        //     UIA-placed rect (or the previously-drawn rect if any). This is
        //     the "click to select UIA element" behavior.
        //   • Draw release = fresh drag complete → Confirm the new rect.
        //   • Resize release = handle adjustment complete → Confirm.
        // Invalid rects (< MinSize, e.g. click with no UIA box present) fall
        // through Confirm's internal Cancel branch.
        _ = wasMode;
        Confirm();
    }

    // ── Handle drag = resize ──
    // R42: SelectionRect is no longer hit-test-visible, so OnRectPointerPressed
    // (the old Move-mode entry) is removed. All canvas clicks now route through
    // OnCanvasPointerPressed → DrawPending/Draw. Move-as-a-gesture is gone;
    // users redraw instead (single-click confirm + drag redraw cover all cases).

    private void BeginHandleDrag(DragMode mode, PointerPressedEventArgs e)
    {
        if (!e.Pointer.IsPrimary)
        {
            return;
        }

        _userTouchedRect = true;
        _mode = mode;
        _dragStart = e.GetPosition(RootCanvas);
        _origLeft = _rectLeft;
        _origTop = _rectTop;
        _origWidth = _rectWidth;
        _origHeight = _rectHeight;
        e.Pointer.Capture((Control)e.Source!);
        e.Handled = true;
    }

    /// <summary>Applies the active resize mode to the rect edges.</summary>
    private void ApplyResize(Point pos)
    {
        double dx = pos.X - _dragStart.X;
        double dy = pos.Y - _dragStart.Y;

        double left = _origLeft, top = _origTop, right = _origLeft + _origWidth, bottom = _origTop + _origHeight;

        if (_mode is DragMode.ResizeNW or DragMode.ResizeN or DragMode.ResizeNE)
        {
            top = _origTop + dy;
        }

        if (_mode is DragMode.ResizeSW or DragMode.ResizeS or DragMode.ResizeSE)
        {
            bottom = _origTop + _origHeight + dy;
        }

        if (_mode is DragMode.ResizeNW or DragMode.ResizeW or DragMode.ResizeSW)
        {
            left = _origLeft + dx;
        }

        if (_mode is DragMode.ResizeNE or DragMode.ResizeE or DragMode.ResizeSE)
        {
            right = _origLeft + _origWidth + dx;
        }

        // Normalize so left<right, top<bottom (handles can cross over).
        _rectLeft = Math.Min(left, right);
        _rectTop = Math.Min(top, bottom);
        _rectWidth = Math.Abs(right - left);
        _rectHeight = Math.Abs(bottom - top);
    }

    // ── Live UIA tracking (R24: pre-fill box follows the cursor) ──

    /// <summary>
    /// Polls the UIA tracker (if any) with the cursor's screen position and
    /// moves the auto-box to the returned element bounds. Skipped when:
    /// <list type="bullet">
    ///   <item>The user has drawn/edited once (<c>_userTouchedRect</c>).</item>
    ///   <item>Less than <see cref="TrackingThrottleMs"/> since the last poll.</item>
    ///   <item>The tracker returns null (canvas / game / off-screen / overlay
    ///   over a region with no UIA element): we DON'T clear the box, just leave
    ///   the last-known rect so the user can still adjust it.</item>
    /// </list>
    /// The tracker blocks the UI thread briefly (UIA round-trip on the MTA
    /// worker); throttling keeps total overhead well under one frame per move.
    /// </summary>
    private void TryLiveTrack(Point canvasLogicalPos)
    {
        Func<int, int, OverlayRect?>? tracker = _tracker;
        if (tracker is null || _userTouchedRect || _cancelling)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if ((now - _lastTrackAt).TotalMilliseconds < TrackingThrottleMs)
        {
            return;
        }
        _lastTrackAt = now;

        double scaling = RenderScaling;
        // logical (canvas-local) → physical screen px. The overlay window sits
        // at the primary screen origin (CoverPrimaryScreen), so canvas-local +
        // window position = screen px. Add window origin in physical space.
        int physX = (int)Math.Round(canvasLogicalPos.X * scaling) + _physOriginX;
        int physY = (int)Math.Round(canvasLogicalPos.Y * scaling) + _physOriginY;

        OverlayRect? box;
        try
        {
            box = tracker(physX, physY);
        }
        catch
        {
            // UIA failures during tracking are non-fatal — just skip this tick.
            return;
        }

        if (box is not { Width: > 0, Height: > 0 })
        {
            return;
        }

        // physical → logical for display.
        double newLeft = (box.X - _physOriginX) / scaling;
        double newTop = (box.Y - _physOriginY) / scaling;
        double newWidth = box.Width / scaling;
        double newHeight = box.Height / scaling;

        // Skip the redraw if nothing moved (avoid needless layout passes).
        if (Math.Abs(newLeft - _rectLeft) < 0.5
            && Math.Abs(newTop - _rectTop) < 0.5
            && Math.Abs(newWidth - _rectWidth) < 0.5
            && Math.Abs(newHeight - _rectHeight) < 0.5)
        {
            return;
        }

        _rectLeft = newLeft;
        _rectTop = newTop;
        _rectWidth = newWidth;
        _rectHeight = newHeight;
        ApplyRectToVisual();
    }

    // ── Confirm / Cancel / Keys ──

    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void Confirm()
    {
        if (_rectWidth < MinSize || _rectHeight < MinSize)
        {
            // Nothing meaningful selected — treat as cancel.
            Cancel();
            return;
        }

        // R42: lock the rect in place + stay visible. The overlay does NOT Hide
        // on confirm — the user wants to "stop on the selected frame" and
        // proceed via F/J/Z/R/C/Enter/Esc. Right-click while confirmed resets
        // (redraw); the App.axaml.cs capture path temporarily Hides the overlay
        // for the BitBlt snapshot then calls ShowConfirmed to restore it.
        _confirmed = true;
        // Stop live tracking so a queued MouseMove can't move the locked rect.
        _cancelling = true;

        double scaling = RenderScaling;
        int x = (int)Math.Round(_rectLeft * scaling);
        int y = (int)Math.Round(_rectTop * scaling);
        int w = (int)Math.Round(_rectWidth * scaling);
        int h = (int)Math.Round(_rectHeight * scaling);
        RegionSelected?.Invoke(x, y, w, h);
    }

    /// <summary>
    /// R42: restores the overlay to visible after App.axaml.cs temporarily Hid
    /// it for the BitBlt snapshot. Preserves the locked-rect state (_confirmed
    /// stays true, DimMask still shows the hole). Called right after
    /// CaptureAsPng so the user sees the overlay again with the confirmed rect.
    /// </summary>
    public void ShowConfirmed()
    {
        if (!IsVisible)
        {
            Show();
        }
        // Re-apply the mask in case the compositor dropped it during Hide.
        ApplyRectToVisual();
    }

    /// <summary>
    /// R40: public cancel entry so the Ocean Eyes toggle (pressing the hotkey
    /// again while the overlay is up) can dismiss the overlay without going
    /// through the Closing-gesture path. Same body as the private version
    /// (used by Esc / right-click / Alt+F4).
    /// </summary>
    public void Cancel()
    {
        // Stop live tracking BEFORE Hide() so a queued MouseMove can't trigger
        // another UIA round-trip (which would re-Show the hidden HWND via
        // RunHidden and cause a visible flicker / perceived ESC lag).
        _cancelling = true;
        _confirmed = false;
        _mode = DragMode.None;
        Hide();
        RegionCancelled?.Invoke();
    }

    /// <summary>
    /// R41: clears the current rect and re-arms UIA live tracking so the user
    /// can draw a new box without re-invoking the hotkey. Raised when the user
    /// right-clicks while the Ocean Eyes toolbar is up (Confirm already fired,
    /// toolbar visible). Distinct from <see cref="Cancel"/>: the overlay stays
    /// open + tracking re-armed; Cancel exits entirely.
    /// </summary>
    public void Reset()
    {
        _cancelling = false;
        _confirmed = false;
        _mode = DragMode.None;
        _userTouchedRect = false;
        _rectLeft = _rectTop = _rectWidth = _rectHeight = 0;
        _lastTrackAt = DateTime.MinValue;
        ApplyRectToVisual();
        // Re-arm UIA assist: the previous Confirm() ran EnableLiveTracking(null)
        // implicitly via _userTouchedRect latch; clear it (done above) so the
        // tracker fires again. The tracker delegate itself is still wired from
        // the original EnterOceanEyesAt call — we don't re-register it.
        RegionReset?.Invoke();
    }

    // ── Visual sync: rect + handles + size badge + dim mask ──

    private void ApplyRectToVisual()
    {
        SelectionRect.Width = _rectWidth;
        SelectionRect.Height = _rectHeight;
        Canvas.SetLeft(SelectionRect, _rectLeft);
        Canvas.SetTop(SelectionRect, _rectTop);
        SelectionRect.IsVisible = _rectWidth >= MinSize && _rectHeight >= MinSize;
        PositionAdorners();
        UpdateDimMask();
    }

    /// <summary>
    /// R42: rebuilds the DimMask Path geometry so the area OUTSIDE the selection
    /// rect is dimmed and the area INSIDE is fully transparent (desktop visible).
    /// Uses a StreamGeometry with FillRule=EvenOdd: an outer rectangle the size
    /// of the canvas + an inner rectangle matching the selection. EvenOdd makes
    /// the overlap (inside the selection) a hole.
    /// </summary>
    /// <remarks>
    /// When the selection is smaller than MinSize (no rect drawn yet), the mask
    /// is just the full-screen dim rectangle (no hole). Canvas size comes from
    /// <see cref="RootCanvas.Bounds"/>; during the very first open before layout
    /// it may be 0×0 — fall back to the window Bounds.
    /// </remarks>
    private void UpdateDimMask()
    {
        double canvasW = RootCanvas.Bounds.Width > 0
            ? RootCanvas.Bounds.Width
            : Bounds.Width;
        double canvasH = RootCanvas.Bounds.Height > 0
            ? RootCanvas.Bounds.Height
            : Bounds.Height;

        // Build a PathGeometry with EvenOdd fill rule: outer rectangle (canvas
        // bounds) + inner rectangle (selection). EvenOdd makes the overlap a
        // hole, so the selection area is transparent and everything else is dim.
        var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };

        // Outer figure (canvas bounds), clockwise.
        var outer = new PathFigure
        {
            StartPoint = new Point(0, 0),
            IsClosed = true,
            IsFilled = true,
        };
        outer.Segments!.Add(new LineSegment { Point = new Point(canvasW, 0) });
        outer.Segments!.Add(new LineSegment { Point = new Point(canvasW, canvasH) });
        outer.Segments!.Add(new LineSegment { Point = new Point(0, canvasH) });
        geometry.Figures!.Add(outer);

        // Inner hole (selection rect) — only when a real rect exists.
        if (_rectWidth >= MinSize && _rectHeight >= MinSize)
        {
            double l = _rectLeft, t = _rectTop;
            double r = _rectLeft + _rectWidth, b = _rectTop + _rectHeight;
            var hole = new PathFigure
            {
                StartPoint = new Point(l, t),
                IsClosed = true,
                IsFilled = true,
            };
            hole.Segments!.Add(new LineSegment { Point = new Point(r, t) });
            hole.Segments!.Add(new LineSegment { Point = new Point(r, b) });
            hole.Segments!.Add(new LineSegment { Point = new Point(l, b) });
            geometry.Figures!.Add(hole);
        }

        DimMask.Data = geometry;
    }

    /// <summary>Places the 8 handles at the rect's corners/edges + the size badge.</summary>
    private void PositionAdorners()
    {
        bool visible = _rectWidth >= MinSize && _rectHeight >= MinSize;
        double l = _rectLeft, t = _rectTop, r = _rectLeft + _rectWidth, b = _rectTop + _rectHeight;
        double cx = (l + r) / 2, cy = (t + b) / 2;

        PlaceHandle(HandleNW, l, t, visible);
        PlaceHandle(HandleN, cx, t, visible);
        PlaceHandle(HandleNE, r, t, visible);
        PlaceHandle(HandleE, r, cy, visible);
        PlaceHandle(HandleSE, r, b, visible);
        PlaceHandle(HandleS, cx, b, visible);
        PlaceHandle(HandleSW, l, b, visible);
        PlaceHandle(HandleW, l, cy, visible);

        // Size badge below-right of the rect.
        if (visible)
        {
            double scaling = RenderScaling;
            SizeText.Text = $"{(int)(_rectWidth * scaling)}×{(int)(_rectHeight * scaling)}";
            SizeBadge.IsVisible = true;
            Canvas.SetLeft(SizeBadge, r - SizeBadge.Bounds.Width);
            Canvas.SetTop(SizeBadge, b + 4);
        }
        else
        {
            SizeBadge.IsVisible = false;
        }
    }

    private void PlaceHandle(Rectangle handle, double x, double y, bool visible)
    {
        handle.IsVisible = visible;
        Canvas.SetLeft(handle, x - HandleHalf);
        Canvas.SetTop(handle, y - HandleHalf);
    }

    private enum DragMode
    {
        None,
        // R42: DrawPending = pointer pressed but movement < 5px threshold. If
        // released before crossing threshold, treat as single click → confirm
        // the current (UIA-placed or previously-drawn) rect. Upgrade to Draw
        // once movement crosses threshold → start a new rect from dragStart.
        DrawPending,
        Draw,
        // R42: Move removed — user redraws instead (single-click confirm or
        // drag from anywhere). Resize via handles remains.
        ResizeNW, ResizeN, ResizeNE, ResizeE, ResizeSE, ResizeS, ResizeSW, ResizeW,
    }
}

/// <summary>
/// Screen-coordinate rectangle (physical px) passed into
/// <see cref="RegionSelectOverlay.ShowWithInitialRect" />. Independent of the
/// Platform.Windows capture-layer Rect so the UI project doesn't depend on
/// Platform.Windows. The App maps between this and the capture-layer Rect.
/// </summary>
public sealed record OverlayRect(int X, int Y, int Width, int Height);
