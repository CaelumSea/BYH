namespace SelectionAssistant.Core.Translation;

/// <summary>
/// A streaming-capable translation backend. Providers that emit output
/// incrementally (e.g. OpenAI-compatible chat completions over SSE) implement
/// this in addition to, or instead of, <see cref="ITranslationProvider" />.
/// It is a separate interface so one-shot providers such as MyMemory are not
/// forced to implement streaming.
/// </summary>
public interface IStreamingTranslationProvider
{
    /// <summary>
    /// Streams incremental translation output. Each yielded delta appends to
    /// the in-progress result. The caller is responsible for concatenation and
    /// for honouring cancellation between deltas.
    /// </summary>
    IAsyncEnumerable<TranslationDelta> StreamAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);
}
