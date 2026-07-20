namespace SelectionAssistant.Core.Input;

[Flags]
public enum GlobalHotKeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// Persistent input settings for opening the Ocean Eyes region selector
/// (formerly "QuickTools"). Same fields/shape as the legacy
/// <c>QuickToolsTriggerSettings</c>; only the class name + persisted file name
/// (<c>ocean-eyes.json</c> instead of <c>quick-tools.json</c>) changed. The
/// store performs a one-time migration read of any legacy file so existing
/// users keep their bindings after upgrade.
/// </summary>
public sealed record OceanEyesTriggerSettings
{
    private static readonly string[] Keys =
    [
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "Space",
    ];

    private const GlobalHotKeyModifiers AllModifiers =
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.Shift |
        GlobalHotKeyModifiers.Windows;

    public bool KeyboardShortcutEnabled { get; init; } = true;

    public GlobalHotKeyModifiers Modifiers { get; init; } =
        GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Alt;

    public string Key { get; init; } = "Q";

    /// <summary>
    /// Disabled by default because a left+right mouse chord also opens the
    /// source application's context menu and can conflict with normal input.
    /// </summary>
    public bool MouseChordEnabled { get; init; }

    public static OceanEyesTriggerSettings Default { get; } = new();

    public static IReadOnlyList<string> SupportedKeys => Keys;

    public OceanEyesTriggerSettings Normalize() => this with
    {
        Key = NormalizeKey(Key),
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
        if (!Keys.Contains(normalizedKey, StringComparer.Ordinal))
        {
            throw new ArgumentException("The primary hotkey key is invalid.", nameof(Key));
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
