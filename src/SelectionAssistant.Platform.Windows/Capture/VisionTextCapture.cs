using SelectionAssistant.Core.Capture;
using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Platform.Windows.Capture;

/// <summary>
/// R24 track B: the screenshot→cloud-OCR capture tier (Tier 4). Captures the
/// element under the mouse (via UIA <c>GetElementBoundsAt</c>, falling back to a
/// mouse-centered box), encodes it to PNG, and asks the injected OCR client to
/// extract text. Returns a <see cref="CaptureResult" /> with source
/// <see cref="CaptureSource.Vision" />, or null when no text is recognized.
/// </summary>
/// <remarks>
/// Screenshot scoping keeps the captured region (and thus privacy exposure +
/// OCR latency) minimal: the UIA bounding box of the element the user pointed
/// at, not the whole screen. When UIA has no bounds, a 240×80 box centered on
/// the mouse is used.
/// </remarks>
public sealed class VisionTextCapture
{
    // Fallback capture box when UIA can't resolve element bounds (canvas, games).
    private const int FallbackWidth = 240;
    private const int FallbackHeight = 80;

    private readonly WindowsUiAutomationBackend _uiAutomation;
    private readonly IVisionOcrClient _ocrClient;

    public VisionTextCapture(WindowsUiAutomationBackend uiAutomation, IVisionOcrClient ocrClient)
    {
        _uiAutomation = uiAutomation ?? throw new ArgumentNullException(nameof(uiAutomation));
        _ocrClient = ocrClient ?? throw new ArgumentNullException(nameof(ocrClient));
    }

    /// <summary>
    /// Captures and OCRs the region under <paramref name="gesture" />. Returns a
    /// Vision result, or null if capture failed or no text was recognized.
    /// </summary>
    public async Task<CaptureResult?> CaptureAsync(SelectionGesture gesture, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        token.ThrowIfCancellationRequested();

        (int x, int y, int width, int height) = ResolveRegion(gesture);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        string? dataUri = ScreenRegionCapture.CaptureAsDataUri(x, y, width, height);
        if (string.IsNullOrEmpty(dataUri))
        {
            return null;
        }

        string text;
        try
        {
            text = await _ocrClient.RecognizeAsync(dataUri, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // OCR failures are non-fatal: the tier just yields no text and the
            // session falls through to "no capture" (no toolbar), per R20 guard.
            return null;
        }

        text = text.Trim();
        return string.IsNullOrEmpty(text)
            ? null
            : new CaptureResult(text, CaptureSource.Vision, false);
    }

    private (int X, int Y, int Width, int Height) ResolveRegion(SelectionGesture gesture)
    {
        // Prefer the UIA bounding box of the element under the mouse — the exact
        // thing the user pointed at, minimal privacy exposure.
        Rect? bounds = _uiAutomation.GetElementBoundsAt(gesture.MouseUpX, gesture.MouseUpY);
        if (bounds is { Width: > 0, Height: > 0 } rect)
        {
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        // Fallback: mouse-centered box. Anchor at top-left so the cursor sits in
        // the upper portion of the captured area (matches typical text below the
        // caret). Clamp so we never pass negative origins to BitBlt.
        int fx = Math.Max(0, gesture.MouseUpX - FallbackWidth / 2);
        int fy = Math.Max(0, gesture.MouseUpY - FallbackHeight / 2);
        return (fx, fy, FallbackWidth, FallbackHeight);
    }
}
