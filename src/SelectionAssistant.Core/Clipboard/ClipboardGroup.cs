namespace SelectionAssistant.Core.Clipboard;

/// <summary>
/// Smart auto-grouping categories (R54, Ortu-inspired). Determined by
/// <see cref="ClipboardClassifier.Classify"/> at capture time and displayed as
/// a badge in the history window. Order matters: it mirrors the classification
/// priority — <see cref="Sensitive"/> first (never leak a password into another
/// group), <see cref="Text"/> last (catch-all).
/// </summary>
public enum ClipboardGroup
{
    /// <summary>api_key / secret / token / password / AKIA… / private_key /
    /// Bearer. Highest priority — a sensitive string never falls through to
    /// Link/Code/etc. Masked in the UI.</summary>
    Sensitive = 0,

    /// <summary>http(s)://, ftp://, www. links.</summary>
    Link = 1,

    /// <summary>Valid JSON object/array (parseable).</summary>
    Json = 2,

    /// <summary>function/class/import/namespace/def/public/private/return …</summary>
    Code = 3,

    /// <summary>sudo/apt/brew/git/chmod/chown/cd/ls/mkdir/rm/cp/mv …</summary>
    Shell = 4,

    /// <summary>Email addresses or 11-digit phone numbers.</summary>
    Contact = 5,

    /// <summary>Pure numbers / currency (digits, separators, sign only).</summary>
    Number = 6,

    /// <summary>Everything else.</summary>
    Text = 7,
}
