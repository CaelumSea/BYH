using System.Text;
using SelectionAssistant.Core.Translation;
using SelectionAssistant.Providers.Sse;
using Xunit;

namespace SelectionAssistant.Providers.Tests.Sse;

public sealed class OpenAiChatStreamTests
{
    [Fact]
    public async Task FullStream_YieldsAllDeltasInOrder()
    {
        // A realistic OpenAI-compatible SSE stream: role-only delta, two
        // content deltas, then [DONE].
        string sse =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"，世界\"}}]}\n\n" +
            "data: [DONE]\n\n";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));

        var deltas = new List<string>();
        await foreach (var delta in OpenAiChatStream.EnumerateDeltasAsync(stream, default))
        {
            deltas.Add(delta.Content);
        }

        Assert.Equal(["你好", "，世界"], deltas);
    }

    [Fact]
    public async Task Case7_CancellationMidFrame_StopsPromptly()
    {
        // A stream that blocks forever once read. We cancel the token after
        // receiving the first delta and expect cancellation to propagate.
        string firstFrame = "data: {\"choices\":[{\"delta\":{\"content\":\"开始\"}}]}\n\n";
        var blockingStream = new BlockingThenHaltStream(Encoding.UTF8.GetBytes(firstFrame));

        using var cts = new CancellationTokenSource();
        var deltas = new List<string>();

        var enumerateTask = Task.Run(async () =>
        {
            await foreach (var delta in OpenAiChatStream.EnumerateDeltasAsync(blockingStream, cts.Token))
            {
                deltas.Add(delta.Content);
                // Cancel right after the first delta lands.
                cts.Cancel();
            }
        });

        // The enumeration should throw OperationCanceledException (case 7).
        await Assert.ThrowsAsync<OperationCanceledException>(() => enumerateTask);

        // The first delta was received before cancellation.
        Assert.Equal(["开始"], deltas);
    }

    [Fact]
    public async Task MidStreamError_PropagatesAsProviderException()
    {
        // An error object arrives after one good delta (case 5 through the stream).
        string sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"部分\"}}]}\n\n" +
            "data: {\"error\":{\"message\":\"context length exceeded\"}}\n\n";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));

        var ex = await Assert.ThrowsAsync<TranslationProviderException>(async () =>
        {
            await foreach (var _ in OpenAiChatStream.EnumerateDeltasAsync(stream, default))
            {
            }
        });

        Assert.Contains("context length exceeded", ex.UserMessage);
    }

    /// <summary>
    /// A stream that yields its initial payload, then blocks forever on
    /// subsequent reads. Used to test that cancellation interrupts a read that
    /// would otherwise hang (case 7).
    /// </summary>
    private sealed class BlockingThenHaltStream : Stream
    {
        private readonly byte[] _payload;
        private int _position;
        private bool _payloadExhausted;

        public BlockingThenHaltStream(byte[] payload) => _payload = payload;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_payloadExhausted)
            {
                int remaining = _payload.Length - _position;
                if (remaining > 0)
                {
                    int toCopy = Math.Min(remaining, count);
                    Array.Copy(_payload, _position, buffer, offset, toCopy);
                    _position += toCopy;
                    return toCopy;
                }

                _payloadExhausted = true;
            }

            // Block forever once the payload is consumed — only cancellation can break this.
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
