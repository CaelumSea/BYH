using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SelectionAssistant.Core.Input;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// R44: Color picker loupe — a small magnifier window that follows the cursor
/// and displays a 15×15 pixel block at 10× magnification (150×150 device px)
/// with a gold crosshair marking the sampled pixel. Shows HEX and RGB readout
/// below the image. Left-click confirms the pick; Esc (routed externally by the
/// runtime) cancels.
/// </summary>
public partial class ColorPickerLoupe : Window
{
    private const int GridSize = 15;
    private const int Scale = 10;
    private const int BitmapSize = GridSize * Scale; // 150

    /// <summary>BGRA pixel buffer returned by the sampler (GridSize² × 4 = 900 bytes).</summary>
    private readonly byte[] _bgra = new byte[GridSize * GridSize * 4];

    /// <summary>RGBA back-buffer we push into the WriteableBitmap each tick.</summary>
    private readonly byte[] _rgba = new byte[BitmapSize * BitmapSize * 4];

    private readonly WriteableBitmap _bitmap;
    private readonly DispatcherTimer _timer;

    private Func<(int CursorX, int CursorY, byte[]? Bgra15x15)>? _sample;
    private Action<byte, byte, byte>? _onPicked;

    public ColorPickerLoupe()
    {
        InitializeComponent();

        _bitmap = new WriteableBitmap(
            new PixelSize(BitmapSize, BitmapSize),
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Opaque);

        MagImage.Source = _bitmap;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTimerTick;

        PointerPressed += OnPointerPressed;

        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>
    /// Exposes the native HWND so the runtime can wrap it with
    /// <c>NoActivateWindowHost</c> (WS_EX_NOACTIVATE).
    /// </summary>
    public nint? NativeHandle => TryGetPlatformHandle()?.Handle;

    /// <summary>
    /// Shows the loupe and starts the ~30 Hz sampler. The sampler returns the
    /// current physical cursor position (X, Y) together with a 15×15 BGRA byte
    /// array (900 bytes; row-major from top-left; pixel = B,G,R,A) centered on
    /// that position, or null on capture failure. On each successful sample,
    /// the loupe moves itself to (cursorX + offset, cursorY + offset) and
    /// updates the magnified image + HEX/RGB text. The center pixel of the
    /// 15×15 block is the pixel under the cursor.
    /// <para>
    /// Confirmation routing: the runtime subscribes to the global mouse hook
    /// and calls <see cref="ConfirmPick"/> on left-button-down (so the user can
    /// click anywhere, not just on the loupe itself). This <c>OnPointerPressed</c>
    /// handler is a fallback for direct clicks on the loupe window.
    /// </para>
    /// </summary>
    public void Show(
        Func<(int CursorX, int CursorY, byte[]? Bgra15x15)> sample,
        Action<byte, byte, byte> onPicked)
    {
        _sample = sample;
        _onPicked = onPicked;

        Show();
        _timer.Start();

        // Initial tick so the loupe appears populated immediately.
        OnTimerTick(this, EventArgs.Empty);
    }

    /// <summary>
    /// R44: invoked by the runtime's mouse hook on left-button-down while the
    /// loupe is active. Reads the center pixel of the last sample (the pixel
    /// that was under the cursor when the click happened) and fires the
    /// <c>onPicked</c> callback. Idempotent — does nothing if no sample is
    /// available or if the loupe has already been hidden.
    /// </summary>
    public void ConfirmPick()
    {
        if (_onPicked is null)
        {
            return;
        }

        const int centerOffset = (GridSize / 2 * GridSize + GridSize / 2) * 4;
        byte r = _bgra[centerOffset + 2];
        byte g = _bgra[centerOffset + 1];
        byte b = _bgra[centerOffset];

        Action<byte, byte, byte>? picked = _onPicked;
        HideLoupe();
        picked?.Invoke(r, g, b);
    }

    /// <summary>
    /// Hides the loupe and stops the timer. Idempotent — safe to call multiple
    /// times (e.g. when Esc is pressed while already hidden).
    /// </summary>
    public void HideLoupe()
    {
        _timer.Stop();
        _sample = null;
        _onPicked = null;
        Hide();
    }

    /// <summary>
    /// 30 Hz tick: calls the sampler to grab a 15×15 BGRA block, converts it
    /// to RGBA at 10× magnification, pushes it into the WriteableBitmap, and
    /// updates the HEX/RGB text labels.
    /// </summary>
    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_sample is null)
        {
            return;
        }

        var (cursorX, cursorY, bgra) = _sample();
        if (bgra is null || bgra.Length < GridSize * GridSize * 4)
        {
            return;
        }

        // Copy to our reusable buffer (the sampler may reuse its own array).
        Buffer.BlockCopy(bgra, 0, _bgra, 0, _bgra.Length);

        // Extract center pixel (row 7, col 7) from the 15×15 BGRA buffer.
        const int centerOffset = (GridSize / 2 * GridSize + GridSize / 2) * 4; // 420
        byte bC = _bgra[centerOffset];
        byte gC = _bgra[centerOffset + 1];
        byte rC = _bgra[centerOffset + 2];

        // Update readout labels.
        HexText.Text = ColorFormatter.ToHexRgb(rC, gC, bC);
        RgbText.Text = ColorFormatter.ToRgbDecimal(rC, gC, bC);

        // Magnify BGRA source → RGBA destination (each source pixel = 10×10 block).
        // We build into _rgba first, then push to the WriteableBitmap backbuffer
        // via Marshal.Copy (avoids requiring /unsafe in the project).
        for (int srcRow = 0; srcRow < GridSize; srcRow++)
        {
            int srcBase = srcRow * GridSize * 4;
            int dstRowStart = srcRow * Scale;

            for (int srcCol = 0; srcCol < GridSize; srcCol++)
            {
                int srcOff = srcBase + srcCol * 4;
                byte bS = _bgra[srcOff];
                byte gS = _bgra[srcOff + 1];
                byte rS = _bgra[srcOff + 2];

                // Fill the 10×10 destination block.
                int dstColStart = srcCol * Scale;
                for (int dy = 0; dy < Scale; dy++)
                {
                    int rowOff = (dstRowStart + dy) * BitmapSize * 4 + dstColStart * 4;
                    for (int dx = 0; dx < Scale; dx++)
                    {
                        int o = rowOff + dx * 4;
                        _rgba[o] = rS;     // R
                        _rgba[o + 1] = gS; // G
                        _rgba[o + 2] = bS; // B
                        _rgba[o + 3] = 255; // A (opaque)
                    }
                }
            }
        }

        using (var fb = _bitmap.Lock())
        {
            Marshal.Copy(_rgba, 0, fb.Address, _rgba.Length);
        }

        // Invalidate the Image so it repaints from the updated bitmap.
        MagImage.InvalidateVisual();

        // Position the loupe near the cursor, clamped to the working area.
        const int offset = 20;
        int px = cursorX + offset;
        int py = cursorY + offset;
        Position = ClampToScreen(px, py);
    }

    /// <summary>
    /// Clamps the loupe window position so it stays fully inside the nearest
    /// screen's working area. Simpler than <see cref="ToolbarWindow.ClampAnchor"/>
    /// — no flip logic needed since the loupe is small (~170×200).
    /// </summary>
    private PixelPoint ClampToScreen(int x, int y)
    {
        var screen = Screens.ScreenFromPoint(new PixelPoint(x, y));
        if (screen is null)
        {
            return new PixelPoint(x, y);
        }

        PixelRect work = screen.WorkingArea;
        double w = Bounds.Width > 0 ? Bounds.Width : BitmapSize + 16;
        double h = Bounds.Height > 0 ? Bounds.Height : BitmapSize + 60;

        double left = Math.Clamp(x, work.X, Math.Max(work.X, work.Right - w));
        double top = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - h));

        return new PixelPoint((int)left, (int)top);
    }

    /// <summary>
    /// Left-click on the loupe itself: confirm the pick. The runtime's mouse
    /// hook is the primary confirmation path (so the user can click anywhere),
    /// this is a fallback for direct clicks on the loupe window.
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }
        ConfirmPick();
    }
}
