using SelectionAssistant.Providers;
using Xunit;

namespace SelectionAssistant.Providers.Tests;

public sealed class ProviderUriBuilderTests
{
    /// <summary>
    /// The canonical §9.3 example: a /v1 base must not lose its segment when a
    /// path is appended. String concatenation would corrupt this.
    /// </summary>
    [Theory]
    [InlineData(
        "https://gateway.example/company/openai/v1",
        "chat/completions",
        "https://gateway.example/company/openai/v1/chat/completions")]
    [InlineData(
        "https://api.deepseek.com/v1/",
        "chat/completions",
        "https://api.deepseek.com/v1/chat/completions")]
    [InlineData(
        "https://api.openai.com/v1",
        "chat/completions",
        "https://api.openai.com/v1/chat/completions")]
    [InlineData(
        "http://127.0.0.1:11434/v1",
        "chat/completions",
        "http://127.0.0.1:11434/v1/chat/completions")]
    public void Build_AppendsPathWithoutLosingTrailingSegment(
        string baseUrl, string path, string expected)
    {
        string actual = ProviderUriBuilder.Build(baseUrl, path);
        Assert.Equal(expected, actual);
    }
}
