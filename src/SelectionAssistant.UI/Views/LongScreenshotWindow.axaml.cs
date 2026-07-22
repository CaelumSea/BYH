using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SelectionAssistant.Core.Capture;
using SelectionAssistant.Infrastructure.Logging;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// R53: manual-scroll long-screenshot session window. The user frames a region
/// with Ocean Eyes and presses L; this window opens with the region's (x,y,w,h)
/// and a <see cref="CaptureFrameDelegate"/> that performs the BitBlt capture
/// (injected by the runtime — the UI layer cannot reference Platform.Windows).
/// Each Space press captures one frame; <see cref="LongScreenshotStitcher"/>
/// appends it to a growing BGRA canvas on a background thread; the live preview
/// grows downward and the ScrollViewer pins to the bottom. Enter raises
/// <see cref="RequestSave"/> with the merged BGRA so the runtime can PNG-encode
/// and persist it. Esc raises <see cref="RequestCancel"/> and closes.
/// <para>
/// Closing this window does NOT dismiss Ocean Eyes (same non-terminal pattern
/// as P/G) — the user can close the long-screenshot session and keep operating
/// the current Ocean Eyes toolbar.
/// </para>
/// </summary>
public partial class LongScreenshotWindow : Avalonia.Controls.Window
{
    /// <summary>
    /// Captures the current contents of the framed screen region and returns
    /// the raw 32-bit BGRA byte buffer (width*height*4 bytes, top-down), or null
    /// on capture failure. Implemented by the runtime using
    /// <c>ScreenRegionCapture.CaptureRawBgra</c>.
    /// </summary>
    public delegate byte[]? CaptureFrameDelegate();

    private readonly int _regionX;
    private readonly int _regionY;
    private readonly int _regionW;
    private readonly int _regionH;
    private readonly RedactedLogger _logger;
    private readonly CaptureFrameDelegate _capture;

    // Stitch state (owned by the UI thread; Append runs on a background Task).
    private byte[]? _canvasBgra;
    private int _canvasHeight;
    private WriteableBitmap? _previewBitmap;
    private byte[]? _previewRgba;
    private int _frameCount;
    private int _failedFrames;
    private bool _busy; // guards against overlapping Space presses during stitch

    /// <summary>Raised on Enter with the merged BGRA buffer + dimensions. Runtime PNG-encodes + persists.</summary>
    public event Action<byte[], int, int>? RequestSave;

    /// <summary>Raised on Esc / cancel. Runtime logs it; no payload.</summary>
    public event Action? RequestCancel;

    /// <summary>Designer / hot-reload entry. Not used at runtime.</summary>
    public LongScreenshotWindow()
    {
        InitializeComponent();
        _regionX = _regionY = _regionW = _regionH = 0;
        _logger = new RedactedLogger();
        _capture = () => null;
    }

    public LongScreenshotWindow(
        int regionX, int regionY, int regionW, int regionH,
        CaptureFrameDelegate capture, RedactedLogger logger)
    {
        InitializeComponent();
        _regionX = regionX;
        _regionY = regionY;
        _regionW = regionW;
        _regionH = regionH;
        _capture = capture;
        _logger = logger;
    }

    /// <summary>
    /// Captures one frame via the injected delegate and stitches it onto the
    /// canvas. Safe to call from the UI thread (Space / button). The actual
    /// stitch runs on a background <see cref="Task"/> to keep the UI responsive;
    /// <c>_busy</c> prevents overlapping Space presses from corrupting state.
    /// </summary>
    public void CaptureFrame()
    {
        if (_busy)
        {
            return;
        }
        if (_regionW <= 0 || _regionH <= 0)
        {
            _logger.Info("OceanEyes", "Long screenshot CaptureFrame: region invalid, ignoring.");
            return;
        }

        byte[]? frame = _capture();
        if (frame is null || frame.Length != _regionW * _regionH * 4)
        {
            _logger.Info("OceanEyes", $"Long screenshot CaptureFrame: capture returned {(frame is null ? "null" : frame.Length)} bytes, expected {_regionW * _regionH * 4}.");
            WarnText.Text = "⚠️ 截屏失败（可能区域不在屏幕上）";
            return;
        }

        // First frame initializes the canvas; no stitch needed.
        if (_canvasBgra is null)
        {
            _canvasBgra = frame;
            _canvasHeight = _regionH;
            _frameCount = 1;
            UpdatePreview();
            UpdateMetrics();
            return;
        }

        // Subsequent frames: stitch on a background thread (Append can take
        // 50-150ms at 1080p). Snapshot the canvas so a concurrent cancel can't
        // mutate it mid-stitch.
        _busy = true;
        byte[] canvasSnapshot = _canvasBgra;
        int canvasH = _canvasHeight;
        _ = Task.Run(() =>
        {
            LongScreenshotStitchResult result;
            try
            {
                result = LongScreenshotStitcher.Append(
                    canvasSnapshot, _regionW, canvasH, frame, _regionH);
            }
            catch (Exception ex)
            {
                _logger.Error("OceanEyes", "Long screenshot stitch failed.", ex);
                Dispatcher.UIThread.Post(() =>
                {
                    _busy = false;
                    WarnText.Text = "⚠️ 拼接异常";
                });
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                // If the window was closed/cancelled during stitch, drop the result.
                if (_canvasBgra is null)
                {
                    _busy = false;
                    return;
                }
                _canvasBgra = result.MergedBgra;
                _canvasHeight = result.Height;
                _frameCount++;
                if (!result.Success)
                {
                    _failedFrames++;
                    WarnText.Text = $"⚠️ {_failedFrames} 帧拼接失败（已兜底追加）";
                }
                UpdatePreview();
                UpdateMetrics();
                _busy = false;
            });
        });
    }

    /// <summary>Updates the WriteableBitmap from the current canvas BGRA buffer and scrolls to bottom.</summary>
    private void UpdatePreview()
    {
        if (_canvasBgra is null || _canvasHeight <= 0)
        {
            return;
        }

        int w = _regionW;
        int h = _canvasHeight;
        // Lazily (re)allocate the RGBA staging buffer + WriteableBitmap for the
        // new height. We can't reuse the bitmap across height changes.
        if (_previewBitmap is null || _previewBitmap.PixelSize.Height != h)
        {
            _previewBitmap?.Dispose();
            _previewBitmap = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96, 96),
                PixelFormat.Rgba8888,
                AlphaFormat.Opaque);
            _previewRgba = new byte[w * h * 4];
            PreviewImage.Source = _previewBitmap;
        }

        // Swizzle BGRA → RGBA (loupe convention; Avalonia has no BGRA write path).
        byte[] src = _canvasBgra;
        byte[] dst = _previewRgba!;
        int pixels = w * h;
        for (int i = 0; i < pixels; i++)
        {
            int s = i * 4;
            byte b = src[s];
            byte g = src[s + 1];
            byte r = src[s + 2];
            dst[s] = r;
            dst[s + 1] = g;
            dst[s + 2] = b;
            dst[s + 3] = 0xFF;
        }

        using (ILockedFramebuffer fb = _previewBitmap.Lock())
        {
            Marshal.Copy(dst, 0, fb.Address, dst.Length);
        }
        PreviewImage.InvalidateVisual();

        // Pin the ScrollViewer to the bottom so the newest content is visible.
        // Defer one render cycle so the new Image size has been measured first.
        // Fully-qualify Avalonia.Vector: ImplicitUsings pulls in System.Numerics
        // which would otherwise shadow it.
        Dispatcher.UIThread.Post(() =>
        {
            var sv = PreviewScroll;
            sv.Offset = new Avalonia.Vector(sv.Offset.X, sv.ScrollBarMaximum.Y);
        });
    }

    private void UpdateMetrics()
    {
        FrameCountText.Text = $"已截 {_frameCount} 帧 · {_regionW}×{_canvasHeight}px";
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                CaptureFrame();
                e.Handled = true;
                break;
            case Key.Enter:
                Save();
                e.Handled = true;
                break;
            case Key.Escape:
                Cancel();
                e.Handled = true;
                break;
        }
    }

    private void OnCaptureClick(object? sender, RoutedEventArgs e) => CaptureFrame();

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Save();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Cancel();

    private void Save()
    {
        if (_canvasBgra is null || _canvasHeight <= 0)
        {
            WarnText.Text = "⚠️ 还没有截到任何帧";
            return;
        }
        // Snapshot so the runtime can PNG-encode off-thread if it wants.
        byte[] snapshot = _canvasBgra;
        int w = _regionW;
        int h = _canvasHeight;
        try
        {
            RequestSave?.Invoke(snapshot, w, h);
        }
        catch (Exception ex)
        {
            _logger.Error("OceanEyes", "Long screenshot save event handler threw.", ex);
        }
        Close();
    }

    private void Cancel()
    {
        try
        {
            RequestCancel?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error("OceanEyes", "Long screenshot cancel event handler threw.", ex);
        }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        _previewRgba = null;
        // Drop the canvas too — the runtime either saved it or the user cancelled.
        _canvasBgra = null;
        base.OnClosed(e);
    }
}
