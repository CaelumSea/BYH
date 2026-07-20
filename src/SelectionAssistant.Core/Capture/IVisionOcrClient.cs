namespace SelectionAssistant.Core.Capture;

/// <summary>
/// R24 track B: runs OCR on a single captured image and returns the recognized
/// text. Defined in Core so the platform screenshot layer (Platform.Windows) can
/// depend on it without referencing the provider layer; implementations live in
/// Providers and reuse the OpenAI-compatible chat-completion surface (shared
/// provider config, DPAPI secret store, SSE plumbing).
/// </summary>
public interface IVisionOcrClient : IDisposable
{
    /// <summary>
    /// Recognizes text in <paramref name="imageDataUri" /> (a
    /// <c>data:image/png;base64,...</c> URI) using the configured OCR model.
    /// Returns the recognized text (possibly empty if no text is found).
    /// </summary>
    Task<string> RecognizeAsync(string imageDataUri, CancellationToken cancellationToken);
}
