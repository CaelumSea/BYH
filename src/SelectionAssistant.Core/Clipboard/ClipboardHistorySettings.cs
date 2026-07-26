namespace SelectionAssistant.Core.Clipboard;

/// <summary>
/// R54: persistent feature settings for clipboard history (the capture/privacy
/// behavior, separate from <see cref="Input.ClipboardHistoryTriggerSettings"/>
/// which holds only the open-popup hotkey). Mirrors the style of
/// <c>OceanEyesCaptureSettings</c>. Persisted by
/// <c>ClipboardHistorySettingsStore</c> as <c>clipboard-history-settings.json</c>.
/// </summary>
public sealed record ClipboardHistorySettings
{
    /// <summary>Master switch. When false the background listener is not started
    /// and <c>Ctrl+Alt+V</c> does nothing. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When true, selecting an entry in the history window not only writes it to
    /// the clipboard but also synthesizes a <c>Ctrl+V</c> so it pastes into the
    /// previously-focused window. Default <b>false</b> — cross-app safest; the
    /// user opts in. SendInput may be refused by elevated/UWP targets; on
    /// failure the entry is still on the clipboard for a manual paste.
    /// </summary>
    public bool AutoPasteEnabled { get; init; } = false;

    /// <summary>Maximum number of non-pinned entries kept. When exceeded the
    /// oldest non-pinned entry is evicted on each capture. Range [10, 5000].</summary>
    public int MaxEntries { get; init; } = 1000;

    /// <summary>R54 v2: master switch for image capture. When false, image
    /// copies are ignored (text-only behavior, same as v1). Default true.</summary>
    public bool CaptureImagesEnabled { get; init; } = true;

    /// <summary>R54 v2: maximum number of non-pinned <b>image</b> entries kept,
    /// independent of <see cref="MaxEntries"/> (images are larger, so they get a
    /// smaller cap). Range [5, 500]. When exceeded the oldest non-pinned image
    /// entry is evicted (and its PNG deleted) on each image capture.</summary>
    public int MaxImageEntries { get; init; } = 50;

    /// <summary>Default privacy exclusion list — common password managers and
    /// authenticator apps. Users can add/remove via Settings. Declared before
    /// <see cref="Default"/> so the static initializer runs first and
    /// <see cref="ExcludeProcessNames"/>'s initializer sees a non-null array.</summary>
    public static readonly string[] DefaultExcludeProcessNames =
        ["1password", "keepass", "authy", "bitwarden", "lastpass", "enpass", "dashlane"];

    /// <summary>
    /// Source process names (case-insensitive, substring match, e.g.
    /// <c>1password</c>, <c>keepass</c>) whose clipboard writes are never
    /// recorded. The first line of defense for passwords. Default list covers
    /// the common password managers and 2FA apps.
    /// </summary>
    public IReadOnlyList<string> ExcludeProcessNames { get; init; } =
        DefaultExcludeProcessNames;

    /// <summary>When true, the history window masks sensitive entries
    /// (api_key/token/password/…) with ●●●● until clicked. Always-on in v1;
    /// kept as a setting so v2 can let the user disable the mask.</summary>
    public bool MaskSensitiveEnabled { get; init; } = true;

    /// <summary>Initial <c>ClipboardHistoryWindow</c> width in DIP, applied at
    /// construction and re-applied when settings are saved. The window is
    /// user-resizable at runtime (<c>CanResize="True"</c>); this value is the
    /// default each app launch starts from (in-session manual resizes persist
    /// across Show/Hide only, not across restarts). Default 800 (the
    /// historical XAML literal). Range [580, 4096] — floor matches the AXAML
    /// <c>MinWidth</c>.</summary>
    public int WindowWidth { get; init; } = 800;

    /// <summary>Initial <c>ClipboardHistoryWindow</c> height in DIP. Default
    /// 620. Range [420, 3072] — floor matches the AXAML <c>MinHeight</c>.
    /// See <see cref="WindowWidth"/>.</summary>
    public int WindowHeight { get; init; } = 620;

    /// <summary>Inclusive window-size bounds. Floors mirror the AXAML
    /// <c>MinWidth/MinHeight</c> so a custom size never underflows the layout;
    /// ceilings are generous caps.</summary>
    public const int MinWindowWidth = 580;
    public const int MaxWindowWidth = 4096;
    public const int MinWindowHeight = 420;
    public const int MaxWindowHeight = 3072;

    public static ClipboardHistorySettings Default { get; } = new();

    public ClipboardHistorySettings Normalize() => this with
    {
        MaxEntries = Math.Clamp(MaxEntries, 10, 5000),
        MaxImageEntries = Math.Clamp(MaxImageEntries, 5, 500),
        WindowWidth = Math.Clamp(WindowWidth, MinWindowWidth, MaxWindowWidth),
        WindowHeight = Math.Clamp(WindowHeight, MinWindowHeight, MaxWindowHeight),
        ExcludeProcessNames = ExcludeProcessNames
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
    };

    public void Validate()
    {
        if (MaxEntries is < 10 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntries), "MaxEntries must be between 10 and 5000.");
        }
        if (MaxImageEntries is < 5 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxImageEntries), "MaxImageEntries must be between 5 and 500.");
        }
        if (WindowWidth is < MinWindowWidth or > MaxWindowWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(WindowWidth), "WindowWidth must be between MinWindowWidth and MaxWindowWidth.");
        }
        if (WindowHeight is < MinWindowHeight or > MaxWindowHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(WindowHeight), "WindowHeight must be between MinWindowHeight and MaxWindowHeight.");
        }
    }
}
