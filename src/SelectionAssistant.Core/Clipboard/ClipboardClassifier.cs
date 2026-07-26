using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelectionAssistant.Core.Clipboard;

/// <summary>
/// Smart auto-grouping for clipboard text (R54, Ortu-inspired rule engine).
/// Pure function: <see cref="Classify"/> maps text to a
/// <see cref="ClipboardGroup"/> + sensitivity flag using compiled regexes. No
/// state, no I/O — fully unit-testable. Priority order follows the
/// <see cref="ClipboardGroup"/> enum: Sensitive wins over everything (a token
/// like <c>api_key=...</c> must never be filed as Text/Code), then Link, Json,
/// Code, Shell, Number, Text.
/// </summary>
/// <remarks>
/// <para><b>Why Code/Shell use structured patterns, not bare keywords.</b>
/// Earlier versions matched a single keyword like <c>class</c>, <c>public</c>,
/// <c>return</c>, <c>git</c>, or <c>cd</c> anywhere in the text. Those are all
/// common English words, so noun-heavy prose — image-generation prompts being
/// the canonical victim ("a private garden, students return from class") — was
/// misclassified as Code or Shell. The patterns now require a code-shaped
/// follower: a PascalCase identifier (<c>class Foo</c>), an assignment
/// (<c>let x =</c>), a modifier chain (<c>public static</c>), a shell flag
/// (<c>git commit -m</c>), or a path/quote. The trade-off is that lone
/// snippets like <c>return value;</c> no longer auto-classify as Code — but
/// such fragments are rare in clipboard traffic, and the structured form
/// catches every realistic copy of source.</para>
/// </remarks>
public static partial class ClipboardClassifier
{
    /// <summary>api_key / secret / token / password / passwd / private_key /
    /// Bearer / AWS access-key id (AKIA + 16 base32). Case-insensitive.</summary>
    [GeneratedRegex(
        @"(?:api[_-]?key|secret|token|password|passwd|private[_-]?key|bearer\s+\S)|" +
        @"AKIA[0-9A-Z]{16}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex SensitivePattern { get; }

    [GeneratedRegex(
        @"^(?:https?://|ftp://|www\.)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex LinkPattern { get; }

    // Structured code detection. A single keyword like `class` or `public` is
    // also a common English word, so each alternative requires a code-shaped
    // follower to count: PascalCase identifier, paren, terminator, assignment,
    // or modifier chain. Case-sensitive (code is; prose tends to lowercase).
    [GeneratedRegex(
        // class/interface/namespace Foo (PascalCase identifier after keyword)
        @"\b(?:class|interface|namespace)\s+[A-Z]\w*" +
        // function foo( / def foo(  (identifier + open paren)
        @"|\b(?:function|def)\s+\w+\s*\(" +
        // import os; / package main; / using System;  (dotted name + separator,
        // or `import x from` / `import x as` for ES modules)
        @"|\b(?:import|package|using)\s+[\w.]+(?:\s*[;.(]|\s+from|\s+as)" +
        // var x = / let x = / const x =  (declaration + assignment)
        @"|\b(?:var|let|const)\s+\w+\s*[=:]" +
        // public/private/protected followed by a modifier chain
        //   (public static, private void, public class, protected override…)
        @"|\b(?:public|private|protected)\s+(?:static|readonly|abstract|sealed|virtual|override|async|void|class|interface|partial|internal)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex CodePattern { get; }

    // Shell/command detection. Bare command words (git, cd, ls, mv, echo…) are
    // also common English words (git workflow, cd changer, mv award, echo
    // chamber), so a lookahead scans the rest of the line for a shell-shaped
    // signal: a flag (-x / --xxx / chmod's +x), a quote, a path separator
    // (/ \ . $), or shell punctuation (| > < ; &). The lookahead is non-greedy
    // and won't cross a newline, so a command on one line can't be validated
    // by signals on the next.
    [GeneratedRegex(
        @"(?:^|\n|\s)(?:sudo|apt|brew|git|chmod|chown|cd|ls|mkdir|rmdir|rm|cp|mv|echo|curl|wget|pip|npm|dotnet|cargo)(?=\s+.*?(?:[-+]{1,2}[\w.]|[""'/\\$]|\.[\/\\]|[|><;&]))",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex ShellPattern { get; }

    // Pure number/currency: sign, digits, thousand/decimal separators, currency symbols.
    [GeneratedRegex(
        @"^[\s]*[-+]?[\d.,][\d.,\s]*[\s]*$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex NumberPattern { get; }

    /// <summary>
    /// Classifies <paramref name="text"/> into a group and reports sensitivity.
    /// Sensitive is checked first and short-circuits: any sensitive hit returns
    /// <see cref="ClipboardGroup.Sensitive"/> with <c>IsSensitive=true</c>,
    /// regardless of whether the text also looks like a link or code.
    /// </summary>
    /// <param name="text">The clipboard text. Null/whitespace returns Text.</param>
    /// <returns>The group (paired with the sensitivity flag in
    /// <see cref="ClipboardEntry.IsSensitive"/> by the caller).</returns>
    public static ClipboardGroup Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ClipboardGroup.Text;
        }

        // Priority 0: sensitive. Never let a token fall through to other groups.
        if (SafeMatch(SensitivePattern, text))
        {
            return ClipboardGroup.Sensitive;
        }

        string trimmed = text.Trim();

        // Priority 1: link.
        if (SafeMatch(LinkPattern, trimmed))
        {
            return ClipboardGroup.Link;
        }

        // Priority 2: JSON (must parse as object/array — "123" parses as number
        // so we additionally require a leading { or [ after trimming).
        if ((trimmed.StartsWith('{') ||
             trimmed.StartsWith('[')) &&
            TryParseJson(trimmed))
        {
            return ClipboardGroup.Json;
        }

        // Priority 3: code.
        if (SafeMatch(CodePattern, text))
        {
            return ClipboardGroup.Code;
        }

        // Priority 4: shell command.
        if (SafeMatch(ShellPattern, text))
        {
            return ClipboardGroup.Shell;
        }

        // Priority 5: pure number / currency.
        if (SafeMatch(NumberPattern, trimmed))
        {
            return ClipboardGroup.Number;
        }

        return ClipboardGroup.Text;
    }

    /// <summary>True when <paramref name="text"/> matches the sensitive
    /// pattern. Exposed separately so the caller can set
    /// <see cref="ClipboardEntry.IsSensitive"/> without re-running the full
    /// classification.</summary>
    public static bool IsSensitive(string? text) =>
        !string.IsNullOrWhiteSpace(text) && SafeMatch(SensitivePattern, text);

    private static bool SafeMatch(Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool TryParseJson(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
