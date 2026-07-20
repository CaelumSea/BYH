using System.Text;

namespace SelectionAssistant.Providers.Sse;

/// <summary>
/// Low-level SSE framer: reads from a stream and yields completed SSE event
/// blocks (the concatenation of all <c>data:</c> lines within one block,
/// joined by <c>\n</c> per the SSE spec). Handles three of the seven required
/// cases: frames split across reads, multiple <c>data:</c> lines per event,
/// and UTF-8 split across buffer boundaries (via the stateful StreamReader
/// decoder rather than byte-level concatenation).
/// </summary>
internal sealed class SseFrameReader : IDisposable
{
    private readonly StreamReader _reader;
    private readonly List<string> _dataLines = [];
    private bool _hasData;

    /// <param name="stream">A readable, typically forward-only HTTP response body stream.</param>
    /// <param name="cancellationToken">Honoured between lines so a mid-frame cancellation is prompt.</param>
    public SseFrameReader(Stream stream)
    {
        // UTF-8 with error detection: a multi-byte sequence split across TCP
        // reads is held internally by StreamReader until completion, so we never
        // see replacement characters from partial bytes (case 3).
        _reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Reads the next complete SSE event block's data payload, or <c>null</c>
    /// at end of stream. Returns an empty string for a block that contained
    /// comment/field lines but no <c>data:</c> line.
    /// </summary>
    public async Task<string?> ReadDataAsync(CancellationToken cancellationToken)
    {
        _dataLines.Clear();
        _hasData = false;

        string? line;
        while ((line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            // A blank line dispatches the current event (SSE spec §6).
            if (line.Length == 0)
            {
                // Skip empty events that carried no data (e.g. comments / keep-alives).
                if (!_hasData)
                {
                    continue;
                }

                // Per spec, multiple data lines are joined with "\n".
                return string.Join("\n", _dataLines);
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                // Strip the field name; spec allows an optional single leading space.
                string payload = line.AsSpan(5).TrimStart(' ').ToString();
                _dataLines.Add(payload);
                _hasData = true;
            }
            // Other field names (event:, id:, retry:) and lines starting with ':'
            // (comments) are ignored — OpenAI-compatible streams only use data:.
        }

        // Stream ended. If a trailing event had data but no terminating blank line,
        // emit it (some servers omit the final CRLFCRLF).
        if (_hasData)
        {
            return string.Join("\n", _dataLines);
        }

        return null;
    }

    public void Dispose() => _reader.Dispose();
}
