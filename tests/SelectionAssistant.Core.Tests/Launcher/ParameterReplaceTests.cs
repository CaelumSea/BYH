using SelectionAssistant.Core.Launcher;
using Xunit;

namespace SelectionAssistant.Core.Tests.Launcher;

public sealed class ParameterReplaceTests
{
    [Fact]
    public void Expand_NoPlaceholders_ReturnsAsIs()
    {
        var result = ParameterReplace.Expand("--verbose --output file.txt", "clip", "sel");

        Assert.Equal("--verbose --output file.txt", result.ExpandedArguments);
        Assert.Empty(result.Prompts);
        Assert.False(result.NeedsPrompt);
    }

    [Fact]
    public void Expand_ClipPlaceholder_ReplacedWithClip()
    {
        var result = ParameterReplace.Expand("--input {clip}", "CLIP_DATA", "sel");

        Assert.Equal("--input CLIP_DATA", result.ExpandedArguments);
        Assert.False(result.NeedsPrompt);
    }

    [Fact]
    public void Expand_SelPlaceholder_ReplacedWithSel()
    {
        var result = ParameterReplace.Expand("--text {sel}", "clip", "SELECTED");

        Assert.Equal("--text SELECTED", result.ExpandedArguments);
        Assert.False(result.NeedsPrompt);
    }

    [Fact]
    public void Expand_NullClipAndSel_TreatedAsEmpty()
    {
        var result = ParameterReplace.Expand("before{clip}mid{sel}after", null, null);

        Assert.Equal("beforemidafter", result.ExpandedArguments);
    }

    [Fact]
    public void Expand_PromptPlaceholder_LeavesTokenAndReportsPrompt()
    {
        var result = ParameterReplace.Expand("--name {prompt:输入姓名}", "clip", "sel");

        Assert.Equal("--name {prompt:输入姓名}", result.ExpandedArguments);
        Assert.Single(result.Prompts);
        Assert.Equal("输入姓名", result.Prompts[0]);
        Assert.True(result.NeedsPrompt);
    }

    [Fact]
    public void Expand_MultipleDifferentPlaceholders_AllReplaced()
    {
        var result = ParameterReplace.Expand("{clip} - {sel}", "AAA", "BBB");

        Assert.Equal("AAA - BBB", result.ExpandedArguments);
        Assert.False(result.NeedsPrompt);
    }

    [Fact]
    public void Expand_MultiplePromptPlaceholders_AllReported()
    {
        var result = ParameterReplace.Expand("{prompt:First} then {prompt:Second}", "c", "s");

        Assert.Equal(2, result.Prompts.Count);
        Assert.Equal("First", result.Prompts[0]);
        Assert.Equal("Second", result.Prompts[1]);
        Assert.True(result.NeedsPrompt);
    }

    [Fact]
    public void ExtractPromptPlaceholders_FindsAll()
    {
        var prompts = ParameterReplace.ExtractPromptPlaceholders("a{prompt:X}b{prompt:Y}c{prompt:Z}d");

        Assert.Equal(3, prompts.Count);
        Assert.Equal("X", prompts[0]);
        Assert.Equal("Y", prompts[1]);
        Assert.Equal("Z", prompts[2]);
    }

    [Fact]
    public void ExtractPromptPlaceholders_NoClosingBrace_StopsGracefully()
    {
        // {prompt:abc has no closing brace — should not crash, returns empty.
        var prompts = ParameterReplace.ExtractPromptPlaceholders("{prompt:abc");

        Assert.Empty(prompts);
    }

    [Fact]
    public void ApplyPromptValues_SubstitutesInOrder()
    {
        string expanded = "{prompt:Name} and {prompt:Age}";
        var result = ParameterReplace.ApplyPromptValues(expanded, new[] { "Alice", "30" });

        Assert.Equal("Alice and 30", result);
    }

    [Fact]
    public void ApplyPromptValues_MoreAnswersThanTokens_ExtrasIgnored()
    {
        string expanded = "{prompt:Only}";
        var result = ParameterReplace.ApplyPromptValues(expanded, new[] { "A", "B", "C" });

        Assert.Equal("A", result);
    }

    [Fact]
    public void ApplyPromptValues_FewerAnswersThanTokens_MissingBecomeEmpty()
    {
        string expanded = "{prompt:A} and {prompt:B}";
        var result = ParameterReplace.ApplyPromptValues(expanded, new[] { "X" });

        Assert.Equal("X and ", result);
    }

    [Fact]
    public void ApplyPromptValues_NoTokens_ReturnsAsIs()
    {
        string expanded = "no prompts here";
        var result = ParameterReplace.ApplyPromptValues(expanded, new[] { "unused" });

        Assert.Equal("no prompts here", result);
    }

    [Fact]
    public void StripPromptTokens_RemovesAll()
    {
        string result = ParameterReplace.StripPromptTokens("a{prompt:x}b");

        Assert.Equal("ab", result);
    }
}
