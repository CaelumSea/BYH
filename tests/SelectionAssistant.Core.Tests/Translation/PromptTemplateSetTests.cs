using SelectionAssistant.Core.Translation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Translation;

/// <summary>
/// Tests for <see cref="PromptTemplateSet"/>'s shortcut lookup. Documents the
/// contract that the OCR-extract Q shortcut relies on: Q is NOT a
/// PromptTemplate (it's a local popup action special-cased in
/// SelectionRuntime.DispatchToolbarActionKey), so FindByShortcut("Q") must
/// return null on the default set, confirming Q flows through the special-case
/// path rather than the PromptTemplate path.
/// </summary>
public sealed class PromptTemplateSetTests
{
    [Fact]
    public void DefaultSet_HasTranslateSummarizeExplainShortcuts()
    {
        var set = new PromptTemplateSet();

        Assert.NotNull(set.FindByShortcut("F"));
        Assert.Equal(PromptActionIds.Translate, set.FindByShortcut("F")!.Id);
        Assert.NotNull(set.FindByShortcut("Z"));
        Assert.Equal(PromptActionIds.Summarize, set.FindByShortcut("Z")!.Id);
        Assert.NotNull(set.FindByShortcut("J"));
        Assert.Equal(PromptActionIds.Explain, set.FindByShortcut("J")!.Id);
    }

    /// <summary>
    /// Q (OCR text extraction popup) is a local action, not an LLM PromptTemplate.
    /// It must return null here so SelectionRuntime's DispatchToolbarActionKey
    /// falls through to its Q special-case (showing OcrTextWindow) rather than
    /// trying to run a translation. This test pins that contract: if someone
    /// later adds a Q-bound PromptTemplate, this assertion catches the clash.
    /// </summary>
    [Fact]
    public void FindByShortcut_Q_ReturnsNull_QIsSpecialCased()
    {
        var set = new PromptTemplateSet();

        Assert.Null(set.FindByShortcut("Q"));
    }

    [Fact]
    public void FindByShortcut_IsCaseInsensitive()
    {
        var set = new PromptTemplateSet();

        Assert.Equal(set.FindByShortcut("F"), set.FindByShortcut("f"));
        Assert.Equal(set.FindByShortcut("J"), set.FindByShortcut("j"));
    }

    [Fact]
    public void FindByShortcut_EmptyOrNullKey_ReturnsNull()
    {
        var set = new PromptTemplateSet();

        Assert.Null(set.FindByShortcut(""));
        Assert.Null(set.FindByShortcut(null!));
    }
}
