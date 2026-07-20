using System.Text;
using SelectionAssistant.Providers.Sse;
using Xunit;

namespace SelectionAssistant.Providers.Tests.Sse;

public sealed class SseFrameReaderTests
{
    /// <summary>
    /// Helper: feed one or more byte chunks into a frame reader as if they
    /// arrived over separate TCP reads. This simulates case 1 (frame split
    /// across reads) and case 3 (UTF-8 split across buffers).
    /// </summary>
    private static async Task<List<string?>> ReadAllAsync(params byte[][] chunks)
    {
        var results = new List<string?>();
        await using var stream = new ChunkedMemoryStream(chunks);
        using var reader = new SseFrameReader(stream);

        string? data;
        while ((data = await reader.ReadDataAsync(default)) is not null || results.Count == 0)
        {
            results.Add(data);
            if (data is null)
            {
                break;
            }
        }

        return results;
    }

    [Fact]
    public async Task Case1_FrameSplitAcrossReads_AssemblesCompleteEvent()
    {
        // "data: hel" + "lo\n\n" split across two reads — one logical event.
        byte[][] chunks =
        [
            Encoding.UTF8.GetBytes("data: hel"),
            Encoding.UTF8.GetBytes("lo\n\n"),
        ];

        List<string?> results = await ReadAllAsync(chunks);

        Assert.Equal("hello", results[0]);
    }

    [Fact]
    public async Task Case2_MultipleDataLines_JoinedByNewline()
    {
        // Two data: lines in one event must be joined with "\n" (SSE spec).
        byte[] input = Encoding.UTF8.GetBytes("data: first\ndata: second\n\n");

        List<string?> results = await ReadAllAsync(input);

        Assert.Equal("first\nsecond", results[0]);
    }

    [Fact]
    public async Task Case3_Utf8SplitAcrossBuffers_DecodedWithoutReplacementChars()
    {
        // "你好" in UTF-8 is E4 BD A0 E5 A5 BD (6 bytes). Split it 4|2 so the
        // second character's first byte and the first are split across reads.
        // A naive byte→string decoder would produce U+FFFD; StreamReader keeps
        // the partial sequence and decodes correctly once more bytes arrive.
        byte[] full = Encoding.UTF8.GetBytes("data: 你好\n\n");
        byte[] first = full[..4];   // "data" — but we'll split at byte 4 of payload
        byte[] second = full[4..];

        // Actually split mid-character: "data: " is 6 ASCII bytes, then 6 UTF-8
        // bytes for 你好. Split at 9 (cuts 你's 3 bytes into 2|1).
        byte[] payload = full;
        first = payload[..9];
        second = payload[9..];

        List<string?> results = await ReadAllAsync(first, second);

        Assert.Equal("你好", results[0]);
    }

    [Fact]
    public async Task CommentLinesAreIgnored()
    {
        // A line starting with ':' is a comment and should be skipped.
        byte[] input = Encoding.UTF8.GetBytes(": keep-alive\ndata: ok\n\n");

        List<string?> results = await ReadAllAsync(input);

        Assert.Equal("ok", results[0]);
    }

    [Fact]
    public async Task EventWithoutDataLine_SkipsToNextEvent()
    {
        // An event block with only non-data fields produces no data; the reader
        // continues to the next event.
        byte[] input = Encoding.UTF8.GetBytes("event: ping\n\ndata: real\n\n");

        List<string?> results = await ReadAllAsync(input);

        Assert.Equal("real", results[0]);
    }

    [Fact]
    public async Task TrailingEventWithoutBlankLine_IsStillEmitted()
    {
        // Some servers omit the final CRLFCRLF; the trailing event should still
        // be returned when the stream ends.
        byte[] input = Encoding.UTF8.GetBytes("data: tail");

        List<string?> results = await ReadAllAsync(input);

        Assert.Equal("tail", results[0]);
    }

    [Fact]
    public async Task EmptyStream_ReturnsNullImmediately()
    {
        byte[] input = [];

        List<string?> results = await ReadAllAsync(input);

        Assert.Null(results[0]);
    }

    /// <summary>
    /// A stream that releases bytes in pre-defined chunks, mimicking how a real
    /// HTTP stream yields partial reads. Used to test cross-read frame assembly.
    /// </summary>
    private sealed class ChunkedMemoryStream : Stream
    {
        private readonly byte[][] _chunks;
        private int _chunkIndex;

        public ChunkedMemoryStream(byte[][] chunks) => _chunks = chunks;

        // CanRead stays true for the stream's lifetime; EOF is signalled by
        // Read returning 0 (StreamReader checks CanRead before every read).
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunkIndex >= _chunks.Length)
            {
                return 0; // EOF
            }

            byte[] chunk = _chunks[_chunkIndex];
            _chunkIndex++;
            int toCopy = Math.Min(chunk.Length, count);
            Array.Copy(chunk, 0, buffer, offset, toCopy);
            return toCopy;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
