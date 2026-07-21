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
            if (PreviewOverlay.IsVisible && _previewBitmap is not null)
            {
                FitToWindow();
            }
        };
    }

    /// <summary>
    /// Computes the initial fit-to-window matrix and applies it. Mirrors
    /// PanAndZoom's <c>CalculateMatrix</c> with <c>StretchMode.Uniform</c>:
    /// scale = min(vw/iw, vh/ih), centered on the viewport.
    /// </summary>
    private void InitPreviewTransform()
    {
        // RenderTransformOrigin is set in AXAML to (0,0). The matrix itself
        // will encode all the scaling and translation.
        PreviewImage.RenderTransform = new MatrixTransform(Matrix.Identity);
    }

    /// <summary>
    /// Pushes <see cref="_matrix"/> to <c>PreviewImage.RenderTransform</c>.
    /// MatrixTransform.Value is read-only in Avalonia 12, so we allocate a
    /// new MatrixTransform each time (cheap — RenderTransform is a
    /// StyledProperty, and updates are infrequent: only on wheel/drag events).
    /// </summary>
    private void ApplyMatrix()
    {
        PreviewImage.RenderTransform = new MatrixTransform(_matrix);
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

        // Centered: image center maps to viewport center.
        // Pan offset = viewportCenter - imageCenter * zoom.
        double cx = iw / 2.0;
        double cy = ih / 2.0;
        double panX = vw / 2.0 - cx * zoom;
        double panY = vh / 2.0 - cy * zoom;

        _matrix = new Matrix(zoom, 0, 0, zoom, panX, panY);
        ApplyMatrix();
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
                if (!PreviewOverlay.IsVisible || PreviewTitle.Text != Path.GetFileName(path))
                {
                    full.Dispose();
                    return;
                }
                _previewBitmap?.Dispose();
                _previewBitmap = full;
                PreviewImage.Source = full;
                // Compute the initial fit-to-window matrix. Post at Loaded
                // priority so the layout pass triggered by Source change has
                // completed and PreviewViewport.Bounds is correct.
                Dispatcher.UIThread.Post(FitToWindow, DispatcherPriority.Loaded);
            });
        });
    }

    private void ClosePreview()
    {
        PreviewOverlay.IsVisible = false;
        PreviewImage.Source = null;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        _matrix = Matrix.Identity;
        _isPanning = false;
        ApplyMatrix();
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

        // Position in image (pre-transform) space — the matrix's input space.
        Point cursor = e.GetPosition(PreviewImage);

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
        // Click on the dark backdrop (not on the image) closes the preview.
        ClosePreview();
        e.Handled = true;
    }

    private void OnPreviewImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Left button on the image starts a pan-drag. Swallow so the
        // backdrop close handler doesn't fire mid-drag.
        e.Handled = true;

        var props = e.GetCurrentPoint(PreviewViewport).Properties;
        if (!props.IsLeftButtonPressed)
        {
            return;
        }

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

        // Translate in viewport space — drag distance maps 1:1 to pan offset
        // regardless of zoom. Prepend Translate(dx, dy) so the translation
        // applies AFTER scaling (i.e. in viewport space).
        // Translate * _matrix means: first apply _matrix (scale+pan), then
        // translate by (dx, dy) — which is exactly what we want.
        Matrix translate = new(1, 0, 0, 1, dx, dy);
        _matrix = translate * _matrix;
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
        return fit > 1.0 ? 1.0 : fit;
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
