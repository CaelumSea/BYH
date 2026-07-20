namespace SelectionAssistant.Core.Launcher;

/// <summary>
/// Expands placeholder tokens in a launcher entry's argument string at launch
/// time. Supported tokens:
/// <list type="bullet">
///   <item><c>{clip}</c> — replaced with the current clipboard text</item>
///   <item><c>{sel}</c> — replaced with the currently selected text</item>
///   <item><c>{prompt:提示语}</c> — left in place; the caller must show an
///     input dialog with the given prompt, then call
///     <see cref="ApplyPromptValues"/> to fill them in</item>
/// </list>
/// <para>
/// Placeholders are expanded lazily — never at save time — so the saved entry
/// always holds the user's original template. The <c>{prompt:...}</c> token is
/// two-phase: <see cref="Expand"/> returns the prompt that needs to be shown,
/// the caller collects the answer(s), then <see cref="ApplyPromptValues"/>
/// substitutes the answers into the already-once-expanded string.
/// </para>
/// </summary>
public static class ParameterReplace
{
    private const string ClipToken = "{clip}";
    private const string SelToken = "{sel}";
    private const string PromptPrefix = "{prompt:";
    private const string PromptSuffix = "}";

    /// <summary>
    /// Expands <c>{clip}</c> and <c>{sel}</c> placeholders, and reports any
    /// <c>{prompt:提示语}</c> prompts that still need user input. When
    /// <see cref="ParameterReplaceResult.NeedsPrompt"/> is true, the caller
    /// must collect answers via a dialog and call
    /// <see cref="ApplyPromptValues"/> on <see cref="ParameterReplaceResult.ExpandedArguments"/>.
    /// </summary>
    public static ParameterReplaceResult Expand(
        string arguments,
        string? clipText,
        string? selectedText)
    {
        string clip = clipText ?? string.Empty;
        string sel = selectedText ?? string.Empty;
        string expanded = arguments.Replace(ClipToken, clip, StringComparison.Ordinal)
                                   .Replace(SelToken, sel, StringComparison.Ordinal);
        var prompts = ExtractPromptPlaceholders(expanded);
        return new ParameterReplaceResult(
            ExpandedArguments: expanded,
            Prompts: prompts,
            NeedsPrompt: prompts.Count > 0);
    }

    /// <summary>
    /// Extracts every <c>{prompt:提示语}</c> prompt from the arguments, in
    /// order of appearance (duplicates preserved). Returns an empty list if
    /// there are none.
    /// </summary>
    public static IReadOnlyList<string> ExtractPromptPlaceholders(string arguments)
    {
        var prompts = new List<string>();
        int i = 0;
        while (i < arguments.Length)
        {
            int start = arguments.IndexOf(PromptPrefix, i, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }
            int promptTextStart = start + PromptPrefix.Length;
            int end = arguments.IndexOf(PromptSuffix, promptTextStart, StringComparison.Ordinal);
            if (end < 0)
            {
                // Malformed (no closing brace) — stop; leave the rest as-is.
                break;
            }
            string promptText = arguments.Substring(promptTextStart, end - promptTextStart);
            prompts.Add(promptText);
            i = end + 1;
        }
        return prompts;
    }

    /// <summary>
    /// Substitutes the user's answers into the <c>{prompt:...}</c> tokens in
    /// <paramref name="expandedArguments"/> (which must have already been
    /// passed through <see cref="Expand"/>). Answers are matched to prompts
    /// in the order returned by <see cref="ExtractPromptPlaceholders"/>. If
    /// the counts mismatch, the shorter list wins.
    /// </summary>
    public static string ApplyPromptValues(string expandedArguments, IReadOnlyList<string> answers)
    {
        if (answers.Count == 0)
        {
            return StripPromptTokens(expandedArguments);
        }
        int answerIndex = 0;
        int i = 0;
        var result = new System.Text.StringBuilder(expandedArguments.Length);
        while (i < expandedArguments.Length)
        {
            int start = expandedArguments.IndexOf(PromptPrefix, i, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(expandedArguments, i, expandedArguments.Length - i);
                break;
            }
            int promptTextStart = start + PromptPrefix.Length;
            int end = expandedArguments.IndexOf(PromptSuffix, promptTextStart, StringComparison.Ordinal);
            if (end < 0)
            {
                result.Append(expandedArguments, i, expandedArguments.Length - i);
                break;
            }
            result.Append(expandedArguments, i, start - i);
            result.Append(answerIndex < answers.Count ? answers[answerIndex] : string.Empty);
            answerIndex++;
            i = end + 1;
        }
        return result.ToString();
    }

    /// <summary>
    /// Removes any (leftover) <c>{prompt:...}</c> tokens, leaving empty strings
    /// in their place. Used when the user cancels the prompt dialog but the
    /// caller still wants to launch with whatever else is set.
    /// </summary>
    public static string StripPromptTokens(string arguments)
    {
        int i = 0;
        var result = new System.Text.StringBuilder(arguments.Length);
        while (i < arguments.Length)
        {
            int start = arguments.IndexOf(PromptPrefix, i, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(arguments, i, arguments.Length - i);
                break;
            }
            int promptTextStart = start + PromptPrefix.Length;
            int end = arguments.IndexOf(PromptSuffix, promptTextStart, StringComparison.Ordinal);
            if (end < 0)
            {
                // Malformed — drop the rest to avoid leaving the raw token.
                result.Append(arguments, i, start - i);
                break;
            }
            result.Append(arguments, i, start - i);
            i = end + 1;
        }
        return result.ToString();
    }
}

/// <summary>
/// Result of <see cref="ParameterReplace.Expand"/>. When
/// <see cref="NeedsPrompt"/> is true, <see cref="Prompts"/> holds the prompts
/// to show the user (in order); the caller should call
/// <see cref="ParameterReplace.ApplyPromptValues"/> on
/// <see cref="ExpandedArguments"/> with the user's answers.
/// </summary>
public sealed record ParameterReplaceResult(
    string ExpandedArguments,
    IReadOnlyList<string> Prompts,
    bool NeedsPrompt);
