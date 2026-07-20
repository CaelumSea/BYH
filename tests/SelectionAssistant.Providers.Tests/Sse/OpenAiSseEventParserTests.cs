using SelectionAssistant.Core.Translation;
using SelectionAssistant.Providers.Sse;
using Xunit;

namespace SelectionAssistant.Providers.Tests.Sse;

public sealed class OpenAiSseEventParserTests
{
    [Fact]
    public void Case4_EmptyDeltaContent_ReturnsSkip()
    {
        // The first chunk of a chat completion often carries only the role,
        // with no content. It must be skipped, not emitted as empty.
        string data = """{"choices":[{"delta":{"role":"assistant"}}]}""";

        var outcome = OpenAiSseEventParser.Parse(data);

        Assert.Equal(OpenAiSseEventParser.ParseOutcomeKind.Skip, outcome.Kind);
    }

    [Fact]
    public void Case4_EmptyContentString_ReturnsSkip()
    {
        string data = """{"choices":[{"delta":{"content":""}}]}""";

        var outcome = OpenAiSseEventParser.Parse(data);

        Assert.Equal(OpenAiSseEventParser.ParseOutcomeKind.Skip, outcome.Kind);
    }

    [Fact]
    public void Case4_NullContent_ReturnsSkip()
    {
        // Some servers emit content: null on the role-only delta.
        string data = """{"choices":[{"delta":{"content":null}}]}""";

        var outcome = OpenAiSseEventParser.Parse(data);

        Assert.Equal(OpenAiSseEventParser.ParseOutcomeKind.Skip, outcome.Kind);
    }

    [Fact]
    public void DeltaWithContent_ReturnsContent()
    {
        string data = """{"choices":[{"delta":{"content":"你好"}}]}""";

        var outcome = OpenAiSseEventParser.Parse(data);

        Assert.Equal(OpenAiSseEventParser.ParseOutcomeKind.Delta, outcome.Kind);
        Assert.Equal("你好", outcome.Content);
    }

    [Fact]
    public void Case5_MidStreamErrorObject_ThrowsProviderException()
    {
        // A rate-limit or auth error arrives as an error object mid-stream.
        string data = """{"error":{"message":"Rate limit exceeded","type":"requests"}}""";

        var ex = Assert.Throws<TranslationProviderException>(() => OpenAiSseEventParser.Parse(data));
        Assert.Contains("Rate limit exceeded", ex.UserMessage);
    }

    [Fact]
    public void Case5_ErrorObjectWithoutMessage_ThrowsGenericMessage()
    {
        string data = """{"error":{"type":"internal"}}""";

        var ex = Assert.Throws<TranslationProviderException>(() => OpenAiSseEventParser.Parse(data));
        Assert.Contains("unknown error", ex.UserMessage);
    }

    [Fact]
    public void Case6_DoneSentinel_ReturnsDone()
    {
        var outcome = OpenAiSseEventParser.Parse("[DONE]");

        Assert.Equal(OpenAiSseEventParser.ParseOutcomeKind.Done, outcome.Kind);
    }

    [Fact]
    public void Case6_DoneSentinelWithWhitespace_ReturnsDone()
    {
        // Some servers pad the sentinel.
        var outcome = OpenAiSseEventParser.Parse("  [DONE]  ");

        Assert.Equal(OpenAiSseEventParser.ParseOutcomeKind.Done, outcome.Kind);
    }

    [Fact]
    public void NoChoicesArray_ReturnsSkip()
    {
        // A usage/stats event with no choices array should be skipped.
        string data = """{"usage":{"prompt_tokens":10}}""";

        var outcome = OpenAiSseEventParser.Parse(data);

        Assert.Equal(OpenAiSseEventParser.ParseOutcomeKind.Skip, outcome.Kind);
    }

    [Fact]
    public void EmptyChoicesArray_ReturnsSkip()
    {
        string data = """{"choices":[]}""";

        var outcome = OpenAiSseEventParser.Parse(data);

        Assert.Equal(OpenAiSseEventParser.ParseOutcomeKind.Skip, outcome.Kind);
    }

    [Fact]
    public void DeltaWithoutDeltaProperty_ReturnsSkip()
    {
        // A finish-reason-only chunk: choices[0] has no delta.
        string data = """{"choices":[{"finish_reason":"stop"}]}""";

        var outcome = OpenAiSseEventParser.Parse(data);

        Assert.Equal(OpenAiSseEventParser.ParseOutcomeKind.Skip, outcome.Kind);
    }
}
