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

    /// <summary>Pan offset in viewport space (DIP). Positive X moves right.</summary>
    private double _panX;

    /// <summary>Pan offset in viewport space (DIP). Positive Y moves down.</summary>
    private double _panY;

    /// <summary>
    /// True while the user is left-button-dragging to pan the zoomed image.
    /// </summary>
    private bool _isPanning;

    /// <summary>Pointer position (in viewport coordinates) at pan start.</summary>
    private Point _panStart;

    /// <summary>_panX / _panY snapshot at pan start, so we apply delta on move.</summary>
    private Point _panStartOffset;

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
    }

    /// <summary>
    /// Attaches a fresh identity MatrixTransform to PreviewImage.RenderTransform.
    /// (We don't keep a long-lived field because MatrixTransform.Value is
    /// read-only — each update allocates a new MatrixTransform instance.)
    /// </summary>
    private void InitPreviewTransform()
    {
        ApplyPreviewTransform();
    }

    /// <summary>
    /// Recomputes the matrix from the current <see cref="_userZoom"/> and
    /// (<see cref="_panX"/>, <see cref="_panY"/>) and assigns it to
    /// PreviewImage.RenderTransform. The matrix is Scale(zoom) * Translate(pan)
    /// so a point p maps as: p' = (p.X * zoom + panX, p.Y * zoom + panY).
    /// Both factors operate in viewport space (DIP), no LayoutTransform
    /// nesting to confuse the math.
    /// </summary>
    private void ApplyPreviewTransform()
    {
        PreviewImage.RenderTransform = new MatrixTransform(new Matrix(
            _userZoom, 0,
            0, _userZoom,
            _panX, _panY));
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

        // Reset zoom + pan for the new image. Image.Stretch="Uniform" will
        // natively fit-to-window once Source is set; we just multiply the
        // user zoom on top via LayoutTransformControl.
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        PreviewImage.Source = null;
        _userZoom = 1.0;
        _panX = 0;
        _panY = 0;
        ApplyPreviewTransform();

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
                // No fit math needed — Image Stretch="Uniform" handles it.
                // LayoutTransformControl scale is already 1.0 = fit baseline.
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
        _panX = 0;
        _panY = 0;
        _isPanning = false;
        ApplyPreviewTransform();
    }

    /// <summary>
    /// Wheel-zoom at cursor. Standard PanAndZoom formula:
    /// <code>
    ///   delta = wheel up ? ZoomPerNotch : 1/ZoomPerNotch
    ///   newZoom = clamp(oldZoom * delta, MinZoom, MaxZoom)
    ///   actualDelta = newZoom / oldZoom
    ///   newPan = (cursor + oldPan) * actualDelta - cursor
    /// </code>
    /// Derivation: the image-space point under the cursor stays put.
    /// Image-space coord of cursor = (cursor - oldPan) / oldZoom.
    /// After zoom, viewport coord of that same point =
    ///   imagePoint * newZoom + newPan.
    /// Setting that equal to cursor and solving for newPan gives the
    /// formula above.
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

        Point cursor = e.GetPosition(PreviewViewport);
        double oldZoom = _userZoom;

        double factor = delta > 0 ? ZoomPerNotch : 1.0 / ZoomPerNotch;
        _userZoom = Math.Clamp(_userZoom * factor, MinZoom, MaxZoom);
        if (_userZoom == oldZoom)
        {
            return; // clamped, no change
        }

        double actualDelta = _userZoom / oldZoom;
        _panX = (cursor.X + _panX) * actualDelta - cursor.X;
        _panY = (cursor.Y + _panY) * actualDelta - cursor.Y;
        ApplyPreviewTransform();
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
        _panStart = e.GetPosition(PreviewViewport);
        _panStartOffset = new Point(_panX, _panY);
        e.Pointer.Capture(PreviewImage);
    }

    private void OnPreviewImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        Point current = e.GetPosition(PreviewViewport);
        _panX = _panStartOffset.X + (current.X - _panStart.X);
        _panY = _panStartOffset.Y + (current.Y - _panStart.Y);
        ApplyPreviewTransform();
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
