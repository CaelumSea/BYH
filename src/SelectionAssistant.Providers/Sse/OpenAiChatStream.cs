using SelectionAssistant.Core.Translation;

namespace SelectionAssistant.Providers.Sse;

/// <summary>
/// Combines <see cref="SseFrameReader" /> and <see cref="OpenAiSseEventParser" />
/// to turn an OpenAI-compatible chat-completion SSE response body into a stream
/// of <see cref="TranslationDelta" /> values. Handles case 7 (cancellation
/// mid-frame): the cancellation token is checked between every frame read, and
/// disposal of the reader releases the underlying stream on cancel.
/// </summary>
internal static class OpenAiChatStream
{
    /// <summary>
    /// Enumerates translation deltas from an SSE response stream. The caller
    /// owns the HTTP message and must dispose it; this method disposes only the
    /// framing <see cref="StreamReader" /> it creates.
    /// </summary>
    /// <param name="responseStream">The raw response body stream (typically
    /// obtained via <c>HttpCompletionOption.ResponseHeadersRead</c>).</param>
    public static async IAsyncEnumerable<TranslationDelta> EnumerateDeltasAsync(
        Stream responseStream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var framer = new SseFrameReader(responseStream);

        while (true)
        {
            // Case 7: prompt cancellation between frames. ReadLineAsync itself
            // accepts the token, so even a blocked read responds to cancel.
            cancellationToken.ThrowIfCancellationRequested();

            string? data = await framer
                .ReadDataAsync(cancellationToken)
                .ConfigureAwait(false);

            if (data is null)
            {
                // Clean end of stream.
                yield break;
            }

            OpenAiSseEventParser.ParseOutcome outcome;
            try
            {
                outcome = OpenAiSseEventParser.Parse(data);
            }
            catch (TranslationProviderException)
            {
                // Re-throw mid-stream errors (case 5) as-is so the caller maps
                // them to a user-facing message.
                throw;
            }

            switch (outcome.Kind)
            {
                case OpenAiSseEventParser.ParseOutcomeKind.Done:
                    yield break;
                case OpenAiSseEventParser.ParseOutcomeKind.Skip:
                    continue;
                case OpenAiSseEventParser.ParseOutcomeKind.Delta:
                    yield return new TranslationDelta(outcome.Content!);
                    break;
            }
        }
    }
}
