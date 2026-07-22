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
/// Code, Shell, Contact, Number, Text.
/// </summary>
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

    [GeneratedRegex(
        @"\b(?:function|class|interface|import|namespace|def\s|public\s|private\s|protected\s|return\s|var\s|let\s|const\s|using\s|package\s)\b",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex CodePattern { get; }

    [GeneratedRegex(
        @"(?:^|\s)(?:sudo|apt|brew|git|chmod|chown|cd|ls|mkdir|rmdir|rm|cp|mv|echo|curl|wget|pip|npm|dotnet|cargo)\s",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex ShellPattern { get; }

    // Email: local@domain. Phone: optional + then exactly 11 digits.
    [GeneratedRegex(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$|^\+?\d{11}$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex ContactPattern { get; }

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

        // Priority 5: contact (email / phone).
        if (SafeMatch(ContactPattern, trimmed))
        {
            return ClipboardGroup.Contact;
        }

        // Priority 6: pure number / currency.
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
