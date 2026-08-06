namespace SelectionAssistant.Core.Input;

/// <summary>
/// R37: user-configurable single-character shortcut keys for the three built-in
/// toolbar actions (Prompt / Copy / Speak). Independent of the
/// PromptTemplate.Shortcut system (which only covers translate/summarize/explain
/// + user custom functions). Persisted to <c>toolbar-shortcuts.json</c> via
/// <see cref="SelectionAssistant.Infrastructure.Configuration.ToolbarShortcutsStore"/>.
///
/// <para>
/// Each key is a single uppercase letter A-Z (matching the 0x41-0x5A range
/// that <c>SelectionRuntime.OnToolbarKeyPressed</c> already accepts). A null
/// or empty string means "disabled" — that built-in shortcut is turned off
/// and the key passes through to the source app.
/// </para>
/// <para>
/// Defaults: Prompt = R, Copy = C, Speak = S. These run only as a fallback
/// when no user-configured PromptTemplate is bound to the same key
/// (<c>PromptTemplateSet.FindByShortcut</c> is consulted first, so user
/// configuration always wins).
/// </para>
/// <para>
/// R41: Paste key removed — Ocean Eyes flow no longer has a paste action
/// (the user confirmed V should be deleted). Old <c>toolbar-shortcuts.json</c>
/// files with a <c>pasteKey</c> field are still loadable: the store ignores
/// the field on read and never writes it back.
/// </para>
/// </summary>
public sealed record ToolbarShortcutSettings
{
    /// <summary>Shortcut for the toolbar "Prompt" button. Default "R". Null/empty = disabled.</summary>
    public string? PromptKey { get; init; } = "R";

    /// <summary>Shortcut for the toolbar "复制" button. Default "C". Null/empty = disabled.</summary>
    public string? CopyKey { get; init; } = "C";

    /// <summary>Shortcut for the toolbar "朗读" (Speak) button. Default "S".
    /// Unlike Copy, the Speak action does NOT hide the toolbar after firing —
    /// audio plays in the background and the user may want to re-trigger it.
    /// Null/empty = disabled.</summary>
    public string? SpeakKey { get; init; } = "S";

    public static ToolbarShortcutSettings Default { get; } = new();

    /// <summary>
    /// Normalizes all keys: trims whitespace, uppercases. Empty/whitespace
    /// strings become null (treated as "disabled"). Does not validate character
    /// range — call <see cref="Validate"/> after normalize.
    /// </summary>
    public ToolbarShortcutSettings Normalize() => this with
    {
        PromptKey = NormalizeKey(PromptKey),
        CopyKey = NormalizeKey(CopyKey),
        SpeakKey = NormalizeKey(SpeakKey),
    };

    /// <summary>
    /// Validates every non-null key is a single uppercase A-Z letter, and that
    /// no two keys collide (a key can't be bound to two built-in actions at
    /// once — the runtime dispatch would be ambiguous). Throws
    /// <see cref="ArgumentException"/> on any violation.
    /// </summary>
    public void Validate()
    {
        ValidateKey(PromptKey, nameof(PromptKey));
        ValidateKey(CopyKey, nameof(CopyKey));
        ValidateKey(SpeakKey, nameof(SpeakKey));

        // Reject duplicate bindings (only compares non-null keys, so multiple
        // disabled entries don't count as duplicates).
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? key in new[] { PromptKey, CopyKey, SpeakKey })
        {
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }
            if (!assigned.Add(key))
            {
                throw new ArgumentException(
                    $"Toolbar shortcut '{key}' is assigned to more than one action.", key);
            }
        }
    }

    private static string? NormalizeKey(string? key)
    {
        string text = key?.Trim() ?? string.Empty;
        return text.Length == 0 ? null : text.ToUpperInvariant();
    }

    private static void ValidateKey(string? key, string propertyName)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;  // null/empty = disabled, always valid
        }
        if (key.Length != 1 || key[0] < 'A' || key[0] > 'Z')
        {
            throw new ArgumentException(
                $"{propertyName} must be one letter A-Z, or blank to disable it.", propertyName);
        }
    }
}
