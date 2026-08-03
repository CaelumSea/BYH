using System.Runtime.InteropServices;
using SelectionAssistant.Platform.Windows.Capture;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests.Capture;

/// <summary>
/// Concurrency tests for <see cref="ScreenRegionCapture.CaptureRawBgra"/>. The
/// method has three live entry points (UI-thread Ocean Eyes capture, UI-thread
/// 30 Hz color-picker sampling, ThreadPool lazy-OCR capture) that used to run
/// BitBlt/GetDIBits concurrently and intermittently corrupted the heap under
/// NativeAOT (2026-08-03 crash, 0xc0000409). A process-wide lock now serializes
/// every capture. These tests reproduce the concurrency and assert no exception,
/// no deadlock, and sane output.
/// </summary>
/// <remarks>
/// Tests require an interactive desktop session (BitBlt on the screen DC). They
/// skip themselves on headless sessions / RDP-disconnected sessions where
/// GetDC(0) returns 0, so the suite stays green in CI without a desktop.
/// </remarks>
[Trait("Category", "RequiresDesktop")]
public sealed class ScreenRegionCaptureConcurrencyTests
{
    private static bool IsDesktopAvailable()
    {
        // GetDC(0) returns a non-zero screen DC only on a session with a
        // visible desktop. On a disconnected RDP/session-0 context it returns 0
        // and every capture would no-op, making the test meaningless.
        nint dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return false;
        }
        ReleaseDC(IntPtr.Zero, dc);
        return true;
    }

    [Fact]
    public async Task CaptureRawBgra_ConcurrentSmallRegions_AllSucceedNoThrow()
    {
        if (!IsDesktopAvailable())
        {
            return; // headless CI: skip silently
        }

        const int callCount = 60;
        var errors = new List<Exception>();
        var nonNull = 0;

        // 60 concurrent ThreadPool calls, each capturing a tiny 15x15 region
        // (mirrors the color-picker loupe sampling rate x many). Before the
        // GDI lock these interleaved BitBlt/GetDIBits across threads; now they
        // must serialize cleanly.
        await Parallel.ForAsync(0, callCount, async (i, _) =>
        {
            await Task.Run(() =>
            {
                try
                {
                    byte[]? bgra = ScreenRegionCapture.CaptureRawBgra(0, 0, 15, 15);
                    if (bgra is not null)
                    {
                        Interlocked.Increment(ref nonNull);
                        Assert.Equal(15 * 15 * 4, bgra.Length);
                    }
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                }
            });
        });

        Assert.Empty(errors);
        // Every call should have produced pixels on a real desktop. If not, the
        // lock is deadlocking or GetDC is flaking.
        Assert.Equal(callCount, nonNull);
    }

    [Fact]
    public async Task CaptureRawBgra_MixedSizesConcurrent_SerializesSafely()
    {
        if (!IsDesktopAvailable())
        {
            return;
        }

        // Mix the loupe's tiny 15x15 captures with a larger 256x256 capture
        // (the two production shapes). The large capture holds the lock longer,
        // exercising the "wait then enter" path rather than the uncontended
        // path. No torn reads, no AV, no hang.
        var sizes = new (int W, int H)[]
        {
            (15, 15), (256, 256), (15, 15), (128, 128), (15, 15), (15, 15),
        };

        var errors = new List<Exception>();
        int nonNull = 0;

        await Parallel.ForAsync(0, sizes.Length, async (i, _) =>
        {
            await Task.Run(() =>
            {
                try
                {
                    var (w, h) = sizes[i];
                    byte[]? bgra = ScreenRegionCapture.CaptureRawBgra(0, 0, w, h);
                    if (bgra is not null)
                    {
                        Interlocked.Increment(ref nonNull);
                        Assert.Equal(w * h * 4, bgra.Length);
                    }
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                }
            });
        });

        Assert.Empty(errors);
        Assert.Equal(sizes.Length, nonNull);
    }

    [Fact]
    public void CaptureRawBgra_LargeRegionBeyondBudget_ReturnsNullWithoutAllocating()
    {
        // Regression guard: the 64 MB pixel budget short-circuits before the
        // lock so a malformed/huge request never allocates a giant native
        // bitmap. Must return null fast and not contend the GDI gate.
        byte[]? result = ScreenRegionCapture.CaptureRawBgra(0, 0, 50_000, 50_000);
        Assert.Null(result);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
