namespace SelectionAssistant.Core.Input;

/// <summary>
/// Persistent input settings for opening the launcher search panel
/// (<c>SpotlightWindow</c>). Mirrors <see cref="OceanEyesTriggerSettings"/>
/// but without the mouse-chord toggle — Spotlight is keyboard-only. Default
/// shortcut is <c>Ctrl+Alt+Space</c>.
/// </summary>
public sealed record SpotlightTriggerSettings
{
    private const GlobalHotKeyModifiers AllModifiers =
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.Shift |
        GlobalHotKeyModifiers.Windows;

    /// <summary>Inclusive window-size bounds for <see cref="WindowWidth"/> /
    /// <see cref="WindowHeight"/>. Match the AXAML <c>MinWidth/MinHeight</c>
    /// floors where reasonable; upper bounds keep the panel usable on screen.</summary>
    public const int MinWindowWidth = 480;
    public const int MaxWindowWidth = 1600;
    public const int MinWindowHeight = 360;
    public const int MaxWindowHeight = 1200;

    public bool KeyboardShortcutEnabled { get; init; } = true;

    public GlobalHotKeyModifiers Modifiers { get; init; } =
        GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Alt;

    /// <summary>Main key (A-Z, 0-9, F1-F12, Space). Default Space.</summary>
    public string Key { get; init; } = "Space";

    /// <summary>Initial <c>SpotlightWindow</c> width in device-independent
    /// pixels, applied at construction and re-applied when settings are saved.
    /// The window is <c>CanResize="False"</c>, so this is the only way the
    /// user controls its size. Default 560 (the historical XAML literal).
    /// Range [480, 1600].</summary>
    public int WindowWidth { get; init; } = 560;

    /// <summary>Initial <c>SpotlightWindow</c> height in DIP. Default 480.
    /// Range [360, 1200]. See <see cref="WindowWidth"/>.</summary>
    public int WindowHeight { get; init; } = 480;

    public static SpotlightTriggerSettings Default { get; } = new();

    /// <summary>Same key list as Ocean Eyes — exposed for the settings UI dropdown.</summary>
    public static IReadOnlyList<string> SupportedKeys => OceanEyesTriggerSettings.SupportedKeys;

    public SpotlightTriggerSettings Normalize() => this with
    {
        Key = NormalizeKey(Key),
        WindowWidth = Math.Clamp(WindowWidth, MinWindowWidth, MaxWindowWidth),
        WindowHeight = Math.Clamp(WindowHeight, MinWindowHeight, MaxWindowHeight),
    };

    public void Validate()
    {
        if ((Modifiers & ~AllModifiers) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Modifiers), "The hotkey contains an unsupported modifier.");
        }

        if (KeyboardShortcutEnabled && Modifiers == GlobalHotKeyModifiers.None)
        {
            throw new ArgumentException("A global hotkey requires at least one modifier.", nameof(Modifiers));
        }

        string normalizedKey = NormalizeKey(Key);
        if (!OceanEyesTriggerSettings.SupportedKeys.Contains(normalizedKey, StringComparer.Ordinal))
        {
            throw new ArgumentException("The primary hotkey key is invalid.", nameof(Key));
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

    public string ToDisplayText()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(GlobalHotKeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(GlobalHotKeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(GlobalHotKeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(GlobalHotKeyModifiers.Windows)) parts.Add("Win");
        parts.Add(NormalizeKey(Key));
        return string.Join("+", parts);
    }

    private static string NormalizeKey(string? key)
    {
        string text = key?.Trim() ?? string.Empty;
        return text.Equals("space", StringComparison.OrdinalIgnoreCase)
            ? "Space"
            : text.ToUpperInvariant();
    }
}
