using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using SelectionAssistant.Core.Capture;
using SelectionAssistant.Core.I18n;
using SelectionAssistant.Infrastructure.Logging;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// R49: screenshot gallery window. Scans the Ocean Eyes save folder and
/// shows a thumbnail grid, newest-first. Double-click opens a full-size
/// preview overlay (lightbox); right-click opens a context menu with
/// copy / preview / delete / reveal-in-explorer. Delete key removes the
/// selected thumbnail's underlying file; Esc closes (the preview if open,
/// otherwise the window).
/// <para>
/// Clipboard and explorer calls are raised back to the runtime via events
/// — the UI layer stays free of Platform.Windows. The runtime subscribes
/// to <see cref="RequestCopy"/> and <see cref="RequestReveal"/>.
/// </para>
/// </summary>
public partial class GalleryWindow : Window
{
    /// <summary>
    /// Target thumbnail width (pixels). Height is computed per-image from
    /// the source aspect ratio so nothing gets squashed.
    /// </summary>
    private const int ThumbnailWidth = 172;

    /// <summary>Max concurrent thumbnail decodes. Bound to avoid disk thrash.</summary>
    private const int LoadParallelism = 4;

    /// <summary>Multiply zoom by this per wheel notch (1.2 = ~5 notches to double).</summary>
    private const double ZoomPerNotch = 1.2;

    /// <summary>
    /// Batch 125: the fit-to-window result is scaled by this factor so the
    /// image renders smaller than the viewport at 1×, leaving free pan room
    /// (the user can drag the image around and past its own layout box up to
    /// the viewport clip without first zooming out). Mirrors the clipboard
    /// image-popup's "display size &lt; clip size" design. 0.65 = the fitted
    /// image fills ~65% of the viewport — a comfortable default that isn't
    /// overwhelmingly large on open, with ample margin to pan into.
    /// </summary>
    private const double FitMarginRatio = 0.65;

    /// <summary>
    /// Batch 125: corner radius (DIP) applied to the preview image via
    /// Image.Clip (a RectangleGeometry with matching RadiusX/RadiusY). The
    /// clip lives in the image's local coordinate space, so it tracks the
    /// bitmap's own edges through zoom/pan (the corners stay rounded on the
    /// image, not on some fixed viewport rectangle that would crop panning).
    /// A small value — just enough to soften the hard corners.
    /// </summary>
    private const double PreviewCornerRadius = 16.0;

    /// <summary>Minimum zoom relative to fit-to-window. 1.0 = exactly fit.</summary>
    private const double MinZoom = 1.0;

    /// <summary>Maximum zoom relative to fit-to-window. 8.0 = 8× the fit size.</summary>
    private const double MaxZoom = 8.0;

    private readonly string _savePath;
    private readonly RedactedLogger _logger;
    private GalleryItemViewModel? _selected;
    private readonly ObservableCollection<GalleryItemViewModel> _items = new();

    // R49 + audit N4: in-app confirmation popup for screenshot deletion.
    // The Delete key + right-click menu both route here first; the actual
    // File.Delete only runs after the user clicks "Delete" in the popup.
    // Prevents unrecoverable loss from a stray keypress while a thumbnail
    // is hover-selected. Reused across deletions (Close + rebuild on each
    // request) — same pattern as ClipboardHistoryWindow.ConfirmClearOlder.
    private Popup? _deleteConfirmPopup;

    /// <summary>
    /// Full-size bitmap currently shown in the preview overlay, if any.
    /// Tracked separately so it can be disposed on close to release native
    /// image memory immediately (not waiting on GC).
    /// </summary>
    private Bitmap? _previewBitmap;

    /// <summary>
    /// The full content-to-viewport transform matrix. Combines fit-to-window
    /// scaling + user zoom + pan offset. Stored as a single Matrix to avoid
    /// TransformGroup children-order pitfalls. Updated by <see cref="ApplyMatrix"/>
    /// and pushed to <c>PreviewImage.RenderTransform</c> as a new MatrixTransform
    /// instance (MatrixTransform.Value is read-only in Avalonia 12).
    /// <para>
    /// Layout: viewportPoint = matrix.Transform(imagePoint). The matrix is
    /// Scale(zoom) * Translate(pan) where zoom already includes the fit factor.
    /// </para>
    /// </summary>
    private Matrix _matrix = Matrix.Identity;

    /// <summary>True while the user is left-button-dragging to pan the zoomed image.</summary>
    private bool _isPanning;

    /// <summary>Pointer position (in viewport coordinates) from the previous Moved event.</summary>
    private Point _panPrevious;

    /// <summary>
    /// Raised with the absolute path of a PNG the user wants copied to the
    /// clipboard (double-click-then-button, context menu, preview button).
    /// The runtime owns the Win32 clipboard call.
    /// </summary>
    public event Action<string>? RequestCopy;

    /// <summary>
    /// Raised before this window deletes a file, so the runtime can log it.
    /// The window itself performs the File.Delete (UI owns the entries list).
    /// </summary>
    public event Action<string>? RequestDelete;

    /// <summary>
    /// Raised with the absolute path of a PNG the user wants pinned as an
    /// always-on-top floating sticker (preview button). The runtime owns the
    /// PinnedScreenshotWindow creation + lifecycle (UI layer stays free of
    /// Platform.Windows).
    /// </summary>
    public event Action<string>? RequestPin;

    /// <summary>
    /// Raised with the absolute path of a PNG whose containing folder the
    /// user wants to see in Explorer (context menu / preview button). The
    /// runtime owns the OS shell call.
    /// </summary>
    public event Action<string>? RequestReveal;

    public GalleryWindow()
    {
        // Designer / XAML hot-reload entry point. Not used at runtime —
        // the runtime always calls the (string, RedactedLogger) overload.
        InitializeComponent();
        _savePath = string.Empty;
        _logger = new RedactedLogger();
        GalleryItems.ItemsSource = _items;
        InitPreviewTransform();
    }

    public GalleryWindow(string savePath, RedactedLogger logger)
    {
        _savePath = savePath ?? string.Empty;
        _logger = logger ?? new RedactedLogger();
        InitializeComponent();
        GalleryItems.ItemsSource = _items;
        InitPreviewTransform();
        Loaded += OnLoaded;
        Closed += OnClosed;
        // Re-fit when the viewport size changes (window resize). Only
        // meaningful while a preview is open.
        PreviewViewport.SizeChanged += (_, _) =>
        {
            if (PreviewPopup.IsOpen && _previewBitmap is not null)
            {
                FitToWindow();
            }
        };
    }

    /// <summary>
    /// Wires up the transform pipeline. Mirrors PanAndZoom's ZoomBorder:
    /// RenderTransformOrigin is set to (0,0) Relative so the matrix's
    /// translate components (M31, M32) map 1:1 to viewport coords. This
    /// requires Image.Bounds to equal the bitmap's native DIP size —
    /// achieved by wrapping Image in a Canvas (which gives infinite
    /// available space during measure, defeating Avalonia 12's default
    /// behavior of constraining Image to its parent's size even with
    /// Stretch="None"). The transform itself is set via ApplyMatrix()
    /// using TransformOperations.Builder (the modern Avalonia 12 API;
    /// MatrixTransform is unreliable on NativeAOT).
    /// </summary>
    private void InitPreviewTransform()
    {
        PreviewImage.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        ApplyMatrix();
    }

    /// <summary>
    /// Pushes <see cref="_matrix"/> to PreviewImage.RenderTransform via
    /// Avalonia 12's TransformOperations API. PanAndZoom uses this pattern
    /// because plain MatrixTransform is unreliable on NativeAOT (sometimes
    /// silently fails to apply). TransformOperations.Builder.AppendMatrix
    /// produces a transform that always triggers a repaint.
    /// </summary>
    private void ApplyMatrix()
    {
        var builder = new TransformOperations.Builder(1);
        builder.AppendMatrix(_matrix);
        PreviewImage.RenderTransform = builder.Build();
        PreviewImage.InvalidateVisual();
    }

    /// <summary>
    /// Computes the fit-to-window matrix for the current bitmap and viewport,
    /// centered (image middle maps to viewport middle). Called on image load
    /// and on viewport resize.
    /// </summary>
    private void FitToWindow()
    {
        if (_previewBitmap is not { } bmp)
        {
            return;
        }
        double vw = PreviewViewport.Bounds.Width;
        double vh = PreviewViewport.Bounds.Height;
        if (vw <= 1 || vh <= 1)
        {
            return;
        }

        // Use DIP Size (not PixelSize) — Image.Stretch=None measures at
        // source.Size, and the matrix operates in DIP space.
        double iw = bmp.Size.Width;
        double ih = bmp.Size.Height;
        if (iw <= 0 || ih <= 0)
        {
            return;
        }

        double zoom = Math.Min(vw / iw, vh / ih);
        if (zoom > 1.0)
        {
            zoom = 1.0; // don't upscale past 1:1
        }
        // Batch 125: shrink the fit slightly so the image doesn't fill the
        // viewport edge-to-edge at 1×. The margin this leaves (~20% of the
        // viewport) is free pan room — the user can drag the image around
        // (and past its own layout box, up to the viewport clip) without
        // first having to zoom out. Mirrors the clipboard image-popup, where
        // the display size is deliberately smaller than the clip boundary.
        zoom *= FitMarginRatio;

        // Centered: image center maps to viewport center.
        // Pan offset = viewportCenter - imageCenter * zoom.
        double cx = iw / 2.0;
        double cy = ih / 2.0;
        double panX = vw / 2.0 - cx * zoom;
        double panY = vh / 2.0 - cy * zoom;

        _matrix = new Matrix(zoom, 0, 0, zoom, panX, panY);
        ApplyMatrix();
        // Reveal the image now that it's centered — paired with the Opacity=0
        // set before Source in OpenPreview, this prevents the upper-left flash
        // of the un-positioned bitmap.
        PreviewImage.Opacity = 1;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Empty-state path label is always shown (even before scan).
        EmptyStatePath.Text = _savePath;

        List<ScreenshotGalleryEntry> entries;
        try
        {
            entries = await Task.Run(() =>
            {
                var list = new List<ScreenshotGalleryEntry>(
                    ScreenshotGalleryLoader.Scan(_savePath));
                return list;
            });
        }
        catch (Exception ex)
        {
            _logger.Error("OceanEyes", "Gallery scan failed.", ex);
            entries = new List<ScreenshotGalleryEntry>();
        }

        UpdateCount(entries.Count);

        if (entries.Count == 0)
        {
            EmptyState.IsVisible = true;
            return;
        }

        // Add view-models first (DisplayName is already there, Thumbnail
        // arrives lazily). This gives the user something to look at while
        // the background decode runs.
        foreach (var entry in entries)
        {
            _items.Add(new GalleryItemViewModel(entry));
        }

        // Fire-and-forget: decode + downscale thumbnails on worker threads,
        // post each finished Bitmap back to the UI thread for binding.
        _ = LoadThumbnailsAsync(entries);
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<ScreenshotGalleryEntry> entries)
    {
        await Task.Run(() =>
        {
            _ = Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = LoadParallelism },
                entry =>
                {
                    Bitmap? thumb = null;
                    try
                    {
                        // DecodeToWidth is documented as cheaper than
                        // new Bitmap(stream) + CreateScaledBitmap — it only
                        // decodes the PNG up to the requested resolution,
                        // skipping the full-resolution pixel buffer.
                        using var stream = File.OpenRead(entry.FilePath);
                        thumb = Bitmap.DecodeToWidth(stream, ThumbnailWidth,
                            BitmapInterpolationMode.HighQuality);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("OceanEyes",
                            $"Gallery thumbnail load failed: {entry.FilePath}", ex);
                    }

                    if (thumb is null) return;

                    Dispatcher.UIThread.Post(() =>
                    {
                        // Match by path; if the entry was already removed
                        // (user pressed Delete before decode finished) or
                        // already has a thumbnail (race), dispose to avoid leak.
                        var vm = _items.FirstOrDefault(x => x.Entry.FilePath == entry.FilePath);
                        if (vm is null || vm.Thumbnail is not null)
                        {
                            thumb.Dispose();
                            return;
                        }
                        vm.Thumbnail = thumb;
                    });
                });
        });
    }

    // ── Thumbnail grid interactions ────────────────────────────────────

    /// <summary>
    /// Batch 125: centralizes selection. Updates <see cref="_selected"/> and
    /// mirrors the state onto each VM's <see cref="GalleryItemViewModel.IsSelected"/>
    /// (clears the old, sets the new) so the grid can react to the persistent
    /// "current" shot — the one Delete/Enter act on — independent of the
    /// :pointerover hover cue.
    /// </summary>
    private void SetSelected(GalleryItemViewModel? vm)
    {
        if (ReferenceEquals(_selected, vm)) return;
        if (_selected is { } prev) prev.IsSelected = false;
        _selected = vm;
        if (vm is { } next) next.IsSelected = true;
    }

    // Batch 125: single-click opens the preview, double-click deletes (with
    // the N4 confirmation popup). Because the first click of a double-click
    // would otherwise open the preview overlay immediately — swallowing the
    // second click on the backdrop instead of the thumbnail — the single-click
    // open is deferred by a short timer. If a second click lands within that
    // window (ClickCount >= 2 on the next press), the pending open is cancelled
    // and the delete path runs instead. This mirrors how desktop file managers
    // disambiguate single- vs double-click on the same target.
    private DispatcherTimer? _pendingOpenTimer;
    private GalleryItemViewModel? _pendingOpenVm;
    private const int SingleClickOpenDelayMs = 220;

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: GalleryItemViewModel vm })
        {
            // Only the LEFT button drives open/delete. A right press still
            // raises PointerPressed, but it should only open the context menu
            // (handled separately) — letting it start the deferred-open timer
            // made right-click also expand the preview (batch 125 fix).
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }
            SetSelected(vm);
            if (e.ClickCount >= 2)
            {
                // Second click within the system double-click window: this is a
                // double-click → cancel any pending single-click open and delete.
                CancelPendingOpen();
                DeleteEntry(vm);
                e.Handled = true;
                return;
            }

            // First click: defer the preview open so a potential second click
            // (double-click) can still reach this handler to delete instead.
            CancelPendingOpen();
            _pendingOpenVm = vm;
            _pendingOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SingleClickOpenDelayMs) };
            _pendingOpenTimer.Tick += (_, _) =>
            {
                _pendingOpenTimer.Stop();
                if (_pendingOpenVm is { } target)
                {
                    _pendingOpenVm = null;
                    OpenPreview(target);
                }
            };
            _pendingOpenTimer.Start();
        }
    }

    private void CancelPendingOpen()
    {
        if (_pendingOpenTimer is { } t)
        {
            t.Stop();
            _pendingOpenTimer = null;
        }
        _pendingOpenVm = null;
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Placeholder kept for future click-vs-drag distinction (e.g. marquee
        // selection). Not used in v1 — single-click just updates _selected,
        // which happens in PointerPressed above.
    }

    // Batch 125: while a thumbnail's context menu is open, suppress hover-select.
    // Without this, moving the mouse across other thumbnails fires PointerEntered
    // → SetSelected → IsSelected PropertyChanged → the card's visual state /
    // layout is recomputed, which makes the open ContextMenu's Popup flicker and
    // reposition ("the menu updates several times as the mouse moves"). The
    // clipboard list avoids this by having no PointerEntered at all; we keep
    // hover-select as a feature but gate it on this flag.
    private bool _isContextMenuOpen;

    private void OnContextMenuOpening(object? sender, CancelEventArgs e) => _isContextMenuOpen = true;

    private void OnContextMenuClosed(object? sender, RoutedEventArgs e) => _isContextMenuOpen = false;

    private void OnItemPointerEntered(object? sender, PointerEventArgs e)
    {
        // Hover-select: a lightweight UX nicety so Delete works on the
        // last-hovered thumbnail without requiring a click first. Skipped while
        // a context menu is open (see _isContextMenuOpen).
        if (_isContextMenuOpen) return;
        if (sender is Control { DataContext: GalleryItemViewModel vm })
        {
            SetSelected(vm);
        }
    }

    // ── Context menu ───────────────────────────────────────────────────

    private void OnContextCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: GalleryItemViewModel vm })
        {
            RequestCopy?.Invoke(vm.Entry.FilePath);
            UpdateCount(_items.Count, suffix: Strings.Gallery_CopiedSuffix);
        }
    }

    private void OnContextPreview_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: GalleryItemViewModel vm })
        {
            OpenPreview(vm);
        }
    }

    private void OnContextDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: GalleryItemViewModel vm })
        {
            DeleteEntry(vm);
        }
    }

    private void OnContextReveal_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: GalleryItemViewModel vm })
        {
            RequestReveal?.Invoke(vm.Entry.FilePath);
        }
    }

    // ── Preview overlay (lightbox) ─────────────────────────────────────

    /// <summary>
    /// Sizes the card to 85% of the primary screen (matching the clipboard
    /// image-popup's clip region) and opens the preview Popup. The Popup is a
    /// separate top-level window, so this 85%-screen card can extend beyond
    /// GalleryWindow's bounds — which is what lets the image be panned outside
    /// the gallery window. The entrance scale animation is attached BEFORE
    /// Open (copied verbatim from ClipboardHistoryWindow.AttachPopupEntrance)
    /// so the OS-level white frame a Popup HWND paints on creation is masked
    /// by the 0.85 starting scale.
    /// </summary>
    private void OpenPreviewPopup()
    {
        var primary = Screens.Primary;
        double w = primary is not null ? primary.Bounds.Width * 0.85 : 900;
        double h = primary is not null ? primary.Bounds.Height * 0.85 : 700;
        PreviewOverlay.Width = w;
        PreviewOverlay.Height = h;
        // Attach the entrance animation before opening — same pattern as the
        // clipboard popup. The 0.85 start scale is set synchronously here so
        // the very first rendered frame is already scaled down, hiding the
        // creation-frame flash.
        AttachPreviewEntrance();
        PreviewPopup.IsOpen = true;
        // Move keyboard focus into the popup so Esc / ← / → / Delete are
        // received here (a Popup is a separate top-level window; without
        // focusing into it, key events stay on GalleryWindow and the preview
        // key handlers wouldn't fire). Posted to the next frame so the popup
        // has completed layout before we focus.
        Dispatcher.UIThread.Post(() => PreviewOverlay.Focus());
    }

    /// <summary>
    /// Entrance scale animation for the preview Popup — copied verbatim from
    /// ClipboardHistoryWindow.AttachPopupEntrance. Scales PreviewOverlay (the
    /// Popup's root card) from 0.85 → 1.0 over ~180ms with CubicEaseOut. The
    /// 0.85 start scale is applied BEFORE Popup.Open, so the very first frame
    /// the OS paints (including the creation-frame white HWND background) is
    /// already scaled down — that is what masks the flash. The transition to
    /// 1.0 fires on Popup.Opened's next frame.
    /// CRITICAL: applied to PreviewOverlay (the card), never to PreviewImage —
    /// the Image carries the zoom matrix as its own RenderTransform.
    /// </summary>
    private void AttachPreviewEntrance()
    {
        const double popStartScale = 0.85;
        var popEasing = new CubicEaseOut();
        var scale = new ScaleTransform(popStartScale, popStartScale);
        PreviewOverlay.RenderTransform = scale;
        PreviewOverlay.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        scale.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleXProperty,
                Duration = TimeSpan.FromMilliseconds(180),
                Easing = popEasing,
            },
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleYProperty,
                Duration = TimeSpan.FromMilliseconds(180),
                Easing = popEasing,
            },
        };
        // Replace any prior Opened handler (the Popup is reused across opens)
        // before attaching this one, so handlers don't accumulate.
        PreviewPopup.Opened -= OnPreviewPopupOpened;
        PreviewPopup.Opened += OnPreviewPopupOpened;
        // Capture this scale instance for the handler via a field.
        _entranceScale = scale;
    }

    private ScaleTransform? _entranceScale;

    private void OnPreviewPopupOpened(object? sender, EventArgs e)
    {
        // Next frame: transition to the final scale so the 0.85 start is
        // honored for the first frame before interpolation begins.
        Dispatcher.UIThread.Post(() =>
        {
            if (_entranceScale is { } s)
            {
                s.ScaleX = 1;
                s.ScaleY = 1;
            }
        });
    }

    private void OpenPreview(GalleryItemViewModel vm)
    {
        // Load the full-resolution PNG on a worker thread to avoid UI
        // hitches on big 4K screenshots. The overlay shows immediately
        // (with the title + counter) so the user gets feedback; the image
        // fills in.
        PreviewTitle.Text = Path.GetFileName(vm.Entry.FilePath);
        UpdatePreviewIndex(vm);
        OpenPreviewPopup();
        SetSelected(vm);

        // Reset state for the new image.
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        PreviewImage.Source = null;
        _matrix = Matrix.Identity;
        ApplyMatrix();

        string path = vm.Entry.FilePath;
        _ = Task.Run(() =>
        {
            Bitmap? full = null;
            try
            {
                using var stream = File.OpenRead(path);
                full = new Bitmap(stream);
            }
            catch (Exception ex)
            {
                _logger.Error("OceanEyes", $"Gallery preview load failed: {path}", ex);
            }
            if (full is null) return;

            Dispatcher.UIThread.Post(() =>
            {
                // If the user closed the overlay or switched to a different
                // entry before this finished, dispose the late bitmap.
                if (!PreviewPopup.IsOpen || PreviewTitle.Text != Path.GetFileName(path))
                {
                    full.Dispose();
                    return;
                }
                _previewBitmap?.Dispose();
                _previewBitmap = full;
                // Hide the image until it has been positioned by FitToWindow.
                // Otherwise the bitmap paints for a frame at the Canvas origin
                // (matrix is Identity) before the fit poll moves it to center —
                // a visible flash in the upper-left. Opacity=0 here, restored
                // to 1 inside FitToWindow once the centered matrix is applied.
                PreviewImage.Opacity = 0;
                PreviewImage.Source = full;
                // Batch 125: rounded corners on the image itself. The clip is
                // in the image's local (pre-transform) space at the bitmap's
                // native DIP size, so it rounds the image's own corners and
                // follows it through zoom/pan rather than cropping to a fixed
                // rectangle (which would re-clip panning).
                double iw = full.Size.Width;
                double ih = full.Size.Height;
                PreviewImage.Clip = new RectangleGeometry(
                    new Rect(0, 0, iw, ih), PreviewCornerRadius, PreviewCornerRadius);
                // Fit as soon as the viewport's Bounds become valid. We try
                // a few times via short DispatcherTimer retries because
                // PreviewOverlay's IsVisible=true doesn't synchronously
                // trigger layout — Bounds can still be 0 for a frame or two
                // after Source is set. Once fit succeeds (or the user closes
                // the preview), the timer stops itself.
                TryFitWhenReady();
            });
        });
    }

    /// <summary>
    /// Polls <see cref="FitToWindow"/> on a short timer until it succeeds
    /// (i.e. PreviewViewport.Bounds is non-zero and bitmap is loaded).
    /// Stops itself on success or after ~1s of retries (fails silently —
    /// the worst case is the user sees a 1:1 image and can zoom out).
    /// </summary>
    private void TryFitWhenReady()
    {
        int attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (!PreviewPopup.IsOpen)
            {
                timer.Stop();
                return;
            }
            double vw = PreviewViewport.Bounds.Width;
            double vh = PreviewViewport.Bounds.Height;
            if (vw > 1 && vh > 1)
            {
                FitToWindow();
                timer.Stop();
                return;
            }
            if (attempts > 60) // ~1s at 16ms ticks
            {
                // Fit never succeeded (viewport never reported valid bounds).
                // Reveal the image anyway at its current (Identity) matrix so
                // the user isn't left staring at a blank popup — they can
                // still zoom/pan. Matches the "fails silently" contract above.
                PreviewImage.Opacity = 1;
                timer.Stop();
            }
        };
        timer.Start();
    }

    private void ClosePreview()
    {
        PreviewPopup.IsOpen = false;
        PreviewImage.Source = null;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        _matrix = Matrix.Identity;
        _isPanning = false;
        ApplyMatrix();
    }

    /// <summary>
    /// Batch 125: writes the "N / M" counter for <paramref name="vm"/> into
    /// PreviewIndex. 1-based for display. Hidden entirely when there is a
    /// single shot (a counter of "1 / 1" adds noise without aiding navigation).
    /// </summary>
    private void UpdatePreviewIndex(GalleryItemViewModel vm)
    {
        if (_items.Count <= 1)
        {
            PreviewIndex.Text = string.Empty;
            return;
        }
        int index = _items.IndexOf(vm);
        if (index < 0)
        {
            PreviewIndex.Text = string.Empty;
            return;
        }
        PreviewIndex.Text = string.Format(Strings.Gallery_PreviewCount, index + 1, _items.Count);
    }

    /// <summary>
    /// Batch 125: steps the preview to the previous/next shot, wrapping
    /// around at the ends. Reuses <see cref="OpenPreview"/> so the bitmap
    /// reload, matrix reset, and fit logic all run identically to a fresh
    /// open — no special-case paging path. A no-op for single-item galleries.
    /// </summary>
    private void StepPreview(int delta)
    {
        if (_selected is null || _items.Count == 0) return;
        if (_items.Count == 1) return;
        int i = _items.IndexOf(_selected);
        if (i < 0) return;
        int next = (i + delta + _items.Count) % _items.Count;
        OpenPreview(_items[next]);
    }

    private void OnPreviewPrev_Click(object? sender, RoutedEventArgs e) => StepPreview(-1);

    private void OnPreviewNext_Click(object? sender, RoutedEventArgs e) => StepPreview(+1);

    /// <summary>
    /// Batch 125: double-tap on the image closes the preview — matches the
    /// clipboard image-popup close gesture. A double-tap is NOT a pan-drag,
    /// so there's no conflict with the pointer-pressed pan handler (which
    /// only acts on single-press + move).
    /// </summary>
    private void OnPreviewImageDoubleTapped(object? sender, TappedEventArgs e)
    {
        ClosePreview();
        e.Handled = true;
    }

    /// <summary>
    /// Wheel-zoom at cursor. Mirrors PanAndZoom's <c>ZoomTo(ratio, x, y)</c>:
    /// <code>
    ///   cursor_in_image = e.GetPosition(PreviewImage)
    ///   ratio = wheel up ? ZoomPerNotch : 1/ZoomPerNotch (clamped so
    ///           resulting zoom stays in [fit * MinUserZoom, fit * MaxUserZoom])
    ///   _matrix = ScaleAt(ratio, ratio, cursor.X, cursor.Y) * _matrix
    /// </code>
    /// ScaleAt(ratio, x, y) = | ratio 0 x*(1-ratio) |
    ///                        | 0 ratio y*(1-ratio) |
    /// which keeps the image point (x, y) fixed under the cursor.
    /// <para>
    /// CRITICAL: cursor must be in IMAGE coordinates (PreviewImage), not
    /// viewport (PreviewViewport). The matrix transforms image→viewport, so
    /// the anchor point must be in the pre-transform space.
    /// </para>
    /// </summary>
    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_previewBitmap is null)
        {
            return;
        }

        double delta = e.Delta.Y;
        if (delta == 0)
        {
            return;
        }

        e.Handled = true;

        // Use viewport coordinates (stable — viewport doesn't change with
        // zoom), then inverse-transform through _matrix to get the image-
        // space anchor point. Getting position relative to PreviewImage
        // directly is unreliable because LayoutTransform changes Image's
        // layout box, so the same screen point maps to different Image-
        // local coords at different zoom levels.
        Point cursorViewport = e.GetPosition(PreviewViewport);
        if (!_matrix.TryInvert(out Matrix inverse))
        {
            return;
        }
        Point cursor = inverse.Transform(cursorViewport);

        double oldZoom = _matrix.M11;
        double factor = delta > 0 ? ZoomPerNotch : 1.0 / ZoomPerNotch;
        double newZoom = oldZoom * factor;

        // Clamp zoom to [fit/4, fit*8]. The fit factor is recomputed each
        // time so clamps stay correct as the user resizes the window.
        double fitZoom = ComputeFitZoom();
        if (fitZoom > 0)
        {
            newZoom = Math.Clamp(newZoom, fitZoom / 4.0, fitZoom * 8.0);
        }
        if (Math.Abs(newZoom - oldZoom) < 1e-9)
        {
            return;
        }

        double ratio = newZoom / oldZoom;
        // ScaleAt(ratio, cx, cy) = scale by ratio anchored at (cx, cy).
        // Prepend to _matrix so the new scale applies in image space.
        Matrix scaleAt = new(ratio, 0, 0, ratio,
            cursor.X * (1.0 - ratio),
            cursor.Y * (1.0 - ratio));
        _matrix = scaleAt * _matrix;
        ApplyMatrix();
    }

    private void OnPreviewOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only a LEFT click on the backdrop closes the preview. A right press
        // must fall through so the image's ContextMenu (copy/pin/reveal/delete)
        // can open — handling it here unconditionally made right-click close
        // the preview instead of showing the menu (batch 125 fix).
        if (!e.GetCurrentPoint(PreviewOverlay).Properties.IsLeftButtonPressed)
        {
            return;
        }
        ClosePreview();
        e.Handled = true;
    }

    private void OnPreviewImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(PreviewViewport).Properties;
        // Only the LEFT button starts a pan-drag. A right press must fall
        // through unhandled so the Image's ContextMenu (copy/pin/reveal/delete)
        // can open — marking it Handled here would suppress the menu.
        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        // Swallow the left press so the backdrop close handler doesn't fire
        // mid-drag.
        e.Handled = true;
        _isPanning = true;
        // Use viewport coords for pan delta (drag distance is in viewport
        // space regardless of zoom level).
        _panPrevious = e.GetPosition(PreviewViewport);
        e.Pointer.Capture(PreviewImage);
    }

    private void OnPreviewImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        Point current = e.GetPosition(PreviewViewport);
        double dx = current.X - _panPrevious.X;
        double dy = current.Y - _panPrevious.Y;
        _panPrevious = current;

        // Pan: drag distance maps 1:1 to viewport offset regardless of zoom.
        // _matrix * translate gives M31 += dx (post-multiply translate in
        // standard matrix algebra). Avalonia 12's `a * b` operator returns
        // `b · a` (despite the source-looking name), so to post-multiply by
        // translate we write `_matrix * translate` — verified empirically
        // against the pan log (translate * _matrix was scaling dx by zoom).
        Matrix translate = new(1, 0, 0, 1, dx, dy);
        _matrix = _matrix * translate;
        ApplyMatrix();
    }

    private void OnPreviewImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }
        _isPanning = false;
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// Returns the fit-to-window zoom factor for the current bitmap and
    /// viewport, or 0 if it can't be computed yet (no bitmap or viewport
    /// not laid out). Used by <see cref="OnPreviewPointerWheelChanged"/>
    /// for clamping.
    /// </summary>
    private double ComputeFitZoom()
    {
        if (_previewBitmap is not { } bmp) return 0;
        double vw = PreviewViewport.Bounds.Width;
        double vh = PreviewViewport.Bounds.Height;
        if (vw <= 1 || vh <= 1) return 0;
        double iw = bmp.Size.Width;
        double ih = bmp.Size.Height;
        if (iw <= 0 || ih <= 0) return 0;
        double fit = Math.Min(vw / iw, vh / ih);
        if (fit > 1.0) fit = 1.0;
        // Keep the clamp baseline in lockstep with FitToWindow (which also
        // applies FitMarginRatio) so wheel-zoom limits refer to the actual
        // rendered size, not the raw fit.
        return fit * FitMarginRatio;
    }

    private void OnPreviewCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is { } vm)
        {
            RequestCopy?.Invoke(vm.Entry.FilePath);
            UpdateCount(_items.Count, suffix: Strings.Gallery_CopiedSuffix);
        }
    }

    private void OnPreviewDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is { } vm)
        {
            ClosePreview();
            DeleteEntry(vm);
        }
    }

    private void OnPreviewReveal_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is { } vm)
        {
            RequestReveal?.Invoke(vm.Entry.FilePath);
        }
    }

    /// <summary>
    /// Batch 125: pin the previewed shot as an always-on-top sticker. Hands
    /// the file path to the runtime via <see cref="RequestPin"/> (the runtime
    /// reads the PNG bytes and creates the PinnedScreenshotWindow). Closes the
    /// preview afterward — the sticker is now its own window, so the preview
    /// overlay is no longer needed.
    /// </summary>
    private void OnPreviewPin_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is { } vm)
        {
            RequestPin?.Invoke(vm.Entry.FilePath);
            ClosePreview();
        }
    }

    // ── Keyboard ───────────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc has two levels: close preview if open, else close the window.
        if (e.Key == Key.Escape)
        {
            if (PreviewPopup.IsOpen)
            {
                ClosePreview();
            }
            else
            {
                Close();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && _selected is { } sel)
        {
            DeleteEntry(sel);
            e.Handled = true;
        }

        if (e.Key == Key.Enter && _selected is { } selEnter)
        {
            // Enter opens the preview (matches double-click).
            OpenPreview(selEnter);
            e.Handled = true;
        }

        // Batch 125: ←/→ step through shots while the preview is open
        // (same as the on-screen prev/next arrows). Only handled inside
        // the overlay so plain arrow keys do nothing odd on the grid.
        if (PreviewPopup.IsOpen)
        {
            if (e.Key == Key.Left)
            {
                StepPreview(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                StepPreview(+1);
                e.Handled = true;
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Shows a themed confirmation popup before deleting the screenshot file.
    /// Audit N4: previously <c>DeleteEntry</c> removed the file unconditionally
    /// on the first call (Delete key, right-click menu, or preview button),
    /// which made a stray keypress irrecoverable. Now the popup must be
    /// confirmed before <see cref="PerformDelete"/> runs.
    /// </summary>
    private void DeleteEntry(GalleryItemViewModel vm)
    {
        // Close any prior confirm popup (e.g. user opened a second one).
        _deleteConfirmPopup?.Close();

        var prompt = new TextBlock
        {
            Text = Strings.Gallery_DeleteConfirmPrompt,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var confirmBtn = new Button
        {
            Content = Strings.Gallery_DeleteConfirmButton,
            FontSize = 13,
            Padding = new Thickness(16, 6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        confirmBtn.Click += (_, _) =>
        {
            _deleteConfirmPopup?.Close();
            PerformDelete(vm);
        };

        var cancelBtn = new Button
        {
            Content = Strings.Common_Cancel,
            FontSize = 13,
            Padding = new Thickness(16, 6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        cancelBtn.Click += (_, _) => _deleteConfirmPopup?.Close();

        var card = new Border
        {
            Background = (IBrush?)Application.Current?.FindResource("ByhSurfaceBrush"),
            BorderBrush = (IBrush?)Application.Current?.FindResource("ByhGoldBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 8,
                Children =
                {
                    prompt,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancelBtn, confirmBtn },
                    },
                },
            },
        };

        var popup = new Popup
        {
            Child = card,
            Placement = PlacementMode.Center,
            PlacementTarget = this,
            IsLightDismissEnabled = true,
            WindowManagerAddShadowHint = false,
        };
        _deleteConfirmPopup = popup;
        ((ISetLogicalParent)popup).SetParent(this);
        popup.Open();
    }

    /// <summary>
    /// Performs the actual file deletion + UI row removal. Only called after
    /// the user confirms in the popup raised by <see cref="DeleteEntry"/>.
    /// </summary>
    private void PerformDelete(GalleryItemViewModel vm)
    {
        try
        {
            RequestDelete?.Invoke(vm.Entry.FilePath);
            File.Delete(vm.Entry.FilePath);
            _items.Remove(vm);
            if (ReferenceEquals(_selected, vm))
            {
                vm.IsSelected = false;
                _selected = null;
            }
            UpdateCount(_items.Count);
            if (_items.Count == 0)
            {
                EmptyState.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("OceanEyes", $"Gallery delete failed: {vm.Entry.FilePath}", ex);
        }
    }

    // Batch 125: the in-window count/hint bar was removed, so UpdateCount no
    // longer has a TextBlock to write to. Kept as a no-op so the existing
    // call sites (scan complete, copy feedback, post-delete) stay valid without
    // churn; the entry count is still surfaced inside the preview as "N / M".
    private void UpdateCount(int entriesCount, string suffix = "")
    {
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Dispose every thumbnail bitmap so the gallery's native image
        // allocations are released immediately (not waiting on GC).
        foreach (var vm in _items)
        {
            vm.Thumbnail?.Dispose();
            vm.Thumbnail = null;
        }
        _items.Clear();
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        _selected = null;
        // Batch 125: close the preview Popup so it doesn't outlive the window.
        PreviewPopup.IsOpen = false;
        // Batch 125: cancel any deferred single-click open so its timer
        // doesn't fire OpenPreview after the window is gone.
        CancelPendingOpen();
        // Audit N4: close any pending delete-confirm popup so it doesn't
        // outlive the window (it captured `this` + the row vm in its closure).
        _deleteConfirmPopup?.Close();
        _deleteConfirmPopup = null;
    }
}

/// <summary>
/// One row in the gallery grid. <see cref="Thumbnail"/> is loaded
/// asynchronously after the window opens; until then the cell renders a
/// blank white card with just the <see cref="DisplayName"/> label.
/// </summary>
public sealed class GalleryItemViewModel : INotifyPropertyChanged
{
    public GalleryItemViewModel(ScreenshotGalleryEntry entry)
    {
        Entry = entry;
    }

    public ScreenshotGalleryEntry Entry { get; }

    public string DisplayName => Entry.DisplayName;

    private Bitmap? _thumbnail;
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!ReferenceEquals(_thumbnail, value))
            {
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }
    }

    // Batch 125: selection state for the grid. The GalleryCard style reacts
    // to :pointerover for hover; IsSelected backs a persistent "current"
    // emphasis (the shot the Delete/Enter keys act on) that survives mouse
    // leave. Kept even though the hover style is the primary visual cue, so a
    // future Selected pseudo-class has a backing field ready.
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
