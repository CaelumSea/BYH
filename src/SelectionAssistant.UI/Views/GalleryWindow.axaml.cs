using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SelectionAssistant.Core.Capture;
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

    /// <summary>Minimum zoom relative to fit-to-window. 1.0 = exactly fit.</summary>
    private const double MinZoom = 1.0;

    /// <summary>Maximum zoom relative to fit-to-window. 8.0 = 8× the fit size.</summary>
    private const double MaxZoom = 8.0;

    private readonly string _savePath;
    private readonly RedactedLogger _logger;
    private GalleryItemViewModel? _selected;
    private readonly ObservableCollection<GalleryItemViewModel> _items = new();

    /// <summary>
    /// Full-size bitmap currently shown in the preview overlay, if any.
    /// Tracked separately so it can be disposed on close to release native
    /// image memory immediately (not waiting on GC).
    /// </summary>
    private Bitmap? _previewBitmap;

    /// <summary>
    /// User zoom factor on top of the fit-to-window baseline. 1.0 = exactly
    /// fit. Wheel changes this by <see cref="ZoomPerNotch"/> or its
    /// reciprocal, clamped to [<see cref="MinZoom"/>, <see cref="MaxZoom"/>].
    /// </summary>
    private double _userZoom = 1.0;

    /// <summary>
    /// Scale that fits the current image into the preview viewport, capped
    /// at 1.0 (no upscaling past native pixels). Recomputed on image load
    /// and on viewport size change.
    /// </summary>
    private double _fitScale = 1.0;

    /// <summary>
    /// ScaleTransform applied to PreviewImage.RenderTransform (the first
    /// child of <see cref="_previewTransformGroup"/>). Owns both the
    /// fit-to-window baseline (<see cref="_fitScale"/>) and the user zoom
    /// (<see cref="_userZoom"/>). Effective scale = fitScale * userZoom.
    /// </summary>
    private readonly ScaleTransform _previewScale = new(1.0, 1.0);

    /// <summary>
    /// TranslateTransform applied to PreviewImage.RenderTransform (the
    /// second child of <see cref="_previewTransformGroup"/>). Set by
    /// fit-to-window centering, pan drag, and zoom-at-cursor anchor math.
    /// Origin (0,0) = image's top-left aligned with viewport's top-left
    /// (before scale). Positive X moves right, positive Y moves down.
    /// </summary>
    private readonly TranslateTransform _previewTranslate = new(0.0, 0.0);

    /// <summary>
    /// The TransformGroup holding <see cref="_previewScale"/> then
    /// <see cref="_previewTranslate"/>. Order matters: scale first, then
    /// translate, so translate operates in viewport space (post-scale).
    /// </summary>
    private readonly TransformGroup _previewTransformGroup = new();

    /// <summary>True while the user is left-button-dragging to pan the zoomed image.</summary>
    private bool _isPanning;

    /// <summary>Pointer position (in viewport coordinates) at pan start.</summary>
    private Point _panStart;

    /// <summary>_previewTranslate.X/Y at pan start, so we apply delta on move.</summary>
    private Point _panStartTranslate;

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
        // R49 preview zoom: LayoutUpdated fires after every layout pass, so
        // it catches both window-resize AND the first time the overlay
        // becomes visible (SizeChanged may NOT fire in that case because
        // the viewport size was already set when the window opened — only
        // its visibility changed). Each call is cheap (early-returns if
        // state hasn't changed).
        PreviewViewport.LayoutUpdated += (_, _) => RecalcFitAndApply();
    }

    /// <summary>
    /// Wires up <see cref="_previewTransformGroup"/> as PreviewImage's
    /// RenderTransform and adds Translate + Scale in the order that makes
    /// the combined matrix <c>Translate * Scale</c>.
    /// <para>
    /// Avalonia's <c>TransformGroup.Value</c> is the product
    /// <c>Children[0].Value * Children[1].Value * ...</c>, and matrix-times-
    /// point applies the rightmost factor first. So with children ordered
    /// <c>[Translate, Scale]</c>, a point <c>p</c> maps as
    /// <c>Translate * (Scale * p)</c> — i.e. scale first (in image/pixel
    /// space, anchored at the image origin), then translate (in viewport
    /// space). This matches what all our fit/pan/zoom math assumes:
    /// <c>viewportPoint = imagePoint * scale + translate</c>.
    /// </para>
    /// </summary>
    private void InitPreviewTransform()
    {
        // CRITICAL: Translate MUST be added before Scale so the combined
        // matrix is Translate * Scale. Adding Scale first would give
        // Scale * Translate (translate-applied-first-in-image-space),
        // which breaks every other calculation in this file.
        _previewTransformGroup.Children.Add(_previewTranslate);
        _previewTransformGroup.Children.Add(_previewScale);
        PreviewImage.RenderTransform = _previewTransformGroup;
        PreviewImage.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
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

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: GalleryItemViewModel vm })
        {
            _selected = vm;
            // Double-click = open preview (the conventional "open" gesture
            // for image grids). Copy lives on the right-click menu and the
            // preview's Copy button — don't surprise users by copying on
            // what looks like an "open" double-click.
            if (e.ClickCount >= 2)
            {
                OpenPreview(vm);
                e.Handled = true;
            }
        }
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Placeholder kept for future click-vs-drag distinction (e.g. marquee
        // selection). Not used in v1 — single-click just updates _selected,
        // which happens in PointerPressed above.
    }

    private void OnItemPointerEntered(object? sender, PointerEventArgs e)
    {
        // Hover-select: a lightweight UX nicety so Delete works on the
        // last-hovered thumbnail without requiring a click first.
        if (sender is Control { DataContext: GalleryItemViewModel vm })
        {
            _selected = vm;
        }
    }

    // ── Context menu ───────────────────────────────────────────────────

    private void OnContextCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: GalleryItemViewModel vm })
        {
            RequestCopy?.Invoke(vm.Entry.FilePath);
            UpdateCount(_items.Count, suffix: " · 已复制");
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

    private void OpenPreview(GalleryItemViewModel vm)
    {
        // Load the full-resolution PNG on a worker thread to avoid UI
        // hitches on big 4K screenshots. The overlay shows immediately
        // (with the title) so the user gets feedback; the image fills in.
        PreviewTitle.Text = Path.GetFileName(vm.Entry.FilePath);
        PreviewOverlay.IsVisible = true;
        _selected = vm;

        // Clear any previous preview bitmap first. Reset zoom so the new
        // image starts at fit-to-window (don't inherit the previous image's
        // zoom level — different aspect ratios would confuse the user).
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        PreviewImage.Source = null;
        _userZoom = 1.0;
        // Reset scale/translate to identity so the just-cleared Image
        // (no Source) doesn't briefly render at the previous image's
        // transform. RecalcFitAndApply below will set them once the
        // new bitmap arrives.
        _previewScale.ScaleX = 1.0;
        _previewScale.ScaleY = 1.0;
        _previewTranslate.X = 0.0;
        _previewTranslate.Y = 0.0;

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
                if (!PreviewOverlay.IsVisible || PreviewTitle.Text != Path.GetFileName(path))
                {
                    full.Dispose();
                    return;
                }
                _previewBitmap?.Dispose();
                _previewBitmap = full;
                PreviewImage.Source = full;
                // Now that we know the bitmap's pixel size, recompute fit.
                // Use Background priority so this runs after the layout pass
                // triggered by the Source change — otherwise Bounds may
                // still reflect the previous image's layout.
                Dispatcher.UIThread.Post(RecalcFitAndApply, DispatcherPriority.Background);
            });
        });
    }

    private void ClosePreview()
    {
        PreviewOverlay.IsVisible = false;
        PreviewImage.Source = null;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        _userZoom = 1.0;
        _isPanning = false;
    }

    /// <summary>
    /// Recomputes <see cref="_fitScale"/> from the current viewport size and
    /// the loaded bitmap's pixel size, applies the combined scale, and
    /// re-centers the translate. Called on image load and on viewport size
    /// change (window resize).
    /// </summary>
    private void RecalcFitAndApply()
    {
        if (_previewBitmap is not { } bmp)
        {
            return;
        }

        double vw = PreviewViewport.Bounds.Width;
        double vh = PreviewViewport.Bounds.Height;
        if (vw <= 1 || vh <= 1)
        {
            return; // viewport not laid out yet
        }

        // Use Size (DIP) — NOT PixelSize. Avalonia's Image with Stretch=None
        // measures at source.Size (DIP), and the RenderTransform operates in
        // DIP space. On a high-DPI display a screenshot may carry DPI != 96,
        // making PixelSize ≠ Size; using PixelSize would under-fit by the
        // DPI ratio.
        double iw = bmp.Size.Width;
        double ih = bmp.Size.Height;
        if (iw <= 0 || ih <= 0)
        {
            return;
        }

        const double pad = 40.0;
        double fit = Math.Min((vw - pad) / iw, (vh - pad) / ih);
        if (fit > 1.0)
        {
            fit = 1.0; // don't upscale past 1:1
        }
        else if (fit < 0.05)
        {
            fit = 0.05; // floor to avoid divide-by-zero edge cases on tiny viewports
        }
        _fitScale = fit;

        // Push the combined scale, then center the (possibly scaled-down)
        // image in the viewport. Centering keeps the picture visually
        // centered regardless of zoom level relative to fit.
        double s = _fitScale * _userZoom;
        _previewScale.ScaleX = s;
        _previewScale.ScaleY = s;
        double scaledW = iw * s;
        double scaledH = ih * s;
        // Translate is in viewport space (post-scale). Center the scaled
        // image; if zoomed larger than viewport, the clamp below will pull
        // it back into bounds on the next pan/zoom.
        _previewTranslate.X = (vw - scaledW) / 2.0;
        _previewTranslate.Y = (vh - scaledH) / 2.0;
        ClampTranslate();
    }

    /// <summary>
    /// Clamps <see cref="_previewTranslate"/> so the scaled image cannot
    /// leave the viewport on any side. When the image is smaller than the
    /// viewport (zoomed-out), it stays centered; when larger, the near edge
    /// is kept inside.
    /// </summary>
    private void ClampTranslate()
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

        double s = _fitScale * _userZoom;
        // DIP size — see RecalcFitAndApply for why not PixelSize.
        double scaledW = bmp.Size.Width * s;
        double scaledH = bmp.Size.Height * s;

        double minX, maxX, minY, maxY;
        if (scaledW <= vw)
        {
            // Image smaller than viewport — keep centered.
            minX = maxX = (vw - scaledW) / 2.0;
        }
        else
        {
            // Image larger — keep both edges inside the viewport.
            minX = vw - scaledW;
            maxX = 0;
        }
        if (scaledH <= vh)
        {
            minY = maxY = (vh - scaledH) / 2.0;
        }
        else
        {
            minY = vh - scaledH;
            maxY = 0;
        }

        _previewTranslate.X = Math.Clamp(_previewTranslate.X, minX, maxX);
        _previewTranslate.Y = Math.Clamp(_previewTranslate.Y, minY, maxY);
    }

    /// <summary>
    /// Wheel-zoom on the preview. Up = zoom in, Down = zoom out. Multiplies
    /// <see cref="_userZoom"/> by <see cref="ZoomPerNotch"/> per notch,
    /// clamped to [<see cref="MinZoom"/>, <see cref="MaxZoom"/>]. The image
    /// point under the cursor stays anchored to the same viewport point
    /// before/after the zoom — standard "zoom at cursor" behavior.
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

        // We are NOT inside a ScrollViewer, so there's no default scroll
        // behavior to compete with. e.Handled is still set so any bubbling
        // ancestor (the overlay Border) doesn't try to do something with it.
        e.Handled = true;

        Point cursor = e.GetPosition(PreviewViewport);
        double oldScale = _fitScale * _userZoom;

        double factor = delta > 0 ? ZoomPerNotch : 1.0 / ZoomPerNotch;
        _userZoom = Math.Clamp(_userZoom * factor, MinZoom, MaxZoom);
        double newScale = _fitScale * _userZoom;
        if (oldScale <= 0 || newScale <= 0)
        {
            return;
        }

        // Keep the image-space point under the cursor anchored:
        //   imgPoint = (cursor - oldTranslate) / oldScale
        //   newTranslate = cursor - imgPoint * newScale
        double imgX = (cursor.X - _previewTranslate.X) / oldScale;
        double imgY = (cursor.Y - _previewTranslate.Y) / oldScale;
        _previewScale.ScaleX = newScale;
        _previewScale.ScaleY = newScale;
        _previewTranslate.X = cursor.X - imgX * newScale;
        _previewTranslate.Y = cursor.Y - imgY * newScale;
        ClampTranslate();
    }

    private void OnPreviewOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Click on the dark backdrop (not on the image) closes the preview.
        ClosePreview();
        e.Handled = true;
    }

    private void OnPreviewImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Left button on the image starts a pan-drag (the standard "pro
        // image viewer" gesture — wheel = zoom, drag = pan). We swallow the
        // event so the backdrop close handler doesn't fire mid-drag.
        e.Handled = true;

        var props = e.GetCurrentPoint(PreviewViewport).Properties;
        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _panStart = e.GetPosition(PreviewViewport);
        _panStartTranslate = new Point(_previewTranslate.X, _previewTranslate.Y);
        e.Pointer.Capture(PreviewImage);
    }

    private void OnPreviewImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        Point current = e.GetPosition(PreviewViewport);
        double dx = current.X - _panStart.X;
        double dy = current.Y - _panStart.Y;

        // Translate moves with the pointer: dragging right shifts the image
        // right. ClampTranslate keeps edges inside the viewport.
        _previewTranslate.X = _panStartTranslate.X + dx;
        _previewTranslate.Y = _panStartTranslate.Y + dy;
        ClampTranslate();
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

    private void OnPreviewCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is { } vm)
        {
            RequestCopy?.Invoke(vm.Entry.FilePath);
            UpdateCount(_items.Count, suffix: " · 已复制");
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

    // ── Keyboard ───────────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc has two levels: close preview if open, else close the window.
        if (e.Key == Key.Escape)
        {
            if (PreviewOverlay.IsVisible)
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
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private void DeleteEntry(GalleryItemViewModel vm)
    {
        try
        {
            RequestDelete?.Invoke(vm.Entry.FilePath);
            File.Delete(vm.Entry.FilePath);
            _items.Remove(vm);
            if (ReferenceEquals(_selected, vm))
            {
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

    private void UpdateCount(int entriesCount, string suffix = "")
    {
        CountText.Text = entriesCount == 0
            ? $"0 张{suffix}"
            : $"{entriesCount} 张{suffix}";
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

    public event PropertyChangedEventHandler? PropertyChanged;
}
