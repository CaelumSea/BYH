using SelectionAssistant.Providers;
using Xunit;

namespace SelectionAssistant.Providers.Tests;

/// <summary>
/// Tests for <see cref="OpenAiCompatibleVisionOcrClient.CleanOcrText"/> — the
/// guard against reasoning-model leakage that some vision models emit on a
/// pure OCR extraction prompt (the "OCR 多余文字" bug). These tests pin the
/// exact cleaning behavior so a regex change can't silently regress the OCR
/// result shown to the user.
/// </summary>
public class OpenAiCompatibleVisionOcrClientTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("Hello", "Hello")]
    [InlineData("  Hello  ", "Hello")]
    public void CleanOcrText_PlainText_PassesThrough(string input, string expected)
    {
        Assert.Equal(expected, OpenAiCompatibleVisionOcrClient.CleanOcrText(input));
    }

    [Fact]
    public void CleanOcrText_ClosedThinkBlock_IsRemoved()
    {
        string raw = "<think>I should describe the image</think>Hello world";
        Assert.Equal("Hello world", OpenAiCompatibleVisionOcrClient.CleanOcrText(raw));
    }

    [Fact]
    public void CleanOcrText_ClosedThinkBlock_Multiline_IsRemoved()
    {
        // Reasoning blocks span many lines; DOTALL must be on.
        string raw = "<think>\nLine 1\nLine 2\nStill thinking\n</think>\nVisible text";
        Assert.Equal("Visible text", OpenAiCompatibleVisionOcrClient.CleanOcrText(raw));
    }

    [Fact]
    public void CleanOcrText_MultipleClosedThinkBlocks_AllRemoved()
    {
        string raw = "<think>a</think>one<think>b</think>two";
        Assert.Equal("onetwo", OpenAiCompatibleVisionOcrClient.CleanOcrText(raw));
    }

    [Fact]
    public void CleanOcrText_UnterminatedThinkOpen_DropsToEof()
    {
        // Stream truncated mid-reasoning: the visible answer never arrived.
        string raw = "Hello<think>internal monologue that never ends";
        Assert.Equal("Hello", OpenAiCompatibleVisionOcrClient.CleanOcrText(raw));
    }

    [Fact]
    public void CleanOcrText_DanglingCloseWithoutOpen_IsRemoved()
    {
        // Opening tag was in a lost SSE chunk; only the closing tag survives.
        string raw = "Hello</think>world";
        Assert.Equal("Helloworld", OpenAiCompatibleVisionOcrClient.CleanOcrText(raw));
    }

    [Fact]
    public void CleanOcrText_RealDeepSeekOcrScenario_KeepsOnlyVisibleText()
    {
        // Realistic hybrid-model output: a reasoning block followed by the
        // actual OCR result. The user must see only the OCR result.
        string raw = "<think>\nThe image contains the text \"By Your Hand\".\nI will output it directly.\n</think>By Your Hand";
        Assert.Equal("By Your Hand", OpenAiCompatibleVisionOcrClient.CleanOcrText(raw));
    }

    [Fact]
    public void CleanOcrText_EmptyThinkBlock_LeavesNoResidue()
    {
        Assert.Equal("AB", OpenAiCompatibleVisionOcrClient.CleanOcrText("A<think></think>B"));
    }
}
