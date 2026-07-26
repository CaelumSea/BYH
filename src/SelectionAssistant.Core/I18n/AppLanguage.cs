using System.Globalization;

namespace SelectionAssistant.Core.I18n;

/// <summary>
/// The two UI languages BYH supports in the MVP: English and Simplified Chinese.
/// Held as a tiny sealed record so equality / switch works cleanly. The active
/// language is exposed via <see cref="Current"/> and mutated once at startup
/// (after reading <c>ui-language.json</c> or auto-detecting from the OS).
/// </summary>
/// <remarks>
/// Switching languages at runtime is intentionally NOT supported in the MVP —
/// the user toggles a ComboBox in Settings → General and the app calls
/// <c>RequestRestart()</c>. <see cref="Current"/> is therefore written exactly
/// once per process lifetime (in <c>App.OnFrameworkInitializationCompleted</c>,
/// before any window is constructed), which keeps the <c>Strings</c> accessor
/// trivially AOT/trim-safe (its static field initializes to the chosen
/// dictionary and never mutates).
/// </remarks>
public sealed record AppLanguage(string Code)
{
    /// <summary>English (en). The neutral / fallback language.</summary>
    public static readonly AppLanguage English = new("en");

    /// <summary>Simplified Chinese (zh-CN).</summary>
    public static readonly AppLanguage Chinese = new("zh-CN");

    /// <summary>
    /// All languages the UI actually ships translations for. Order matters —
    /// the Settings ComboBox binds to this list and the first entry is the
    /// default selection.
    /// </summary>
    public static readonly IReadOnlyList<AppLanguage> Supported =
        [English, Chinese];

    /// <summary>
    /// The active UI language. Set exactly once at startup via
    /// <see cref="Set"/>. Defaults to <see cref="English"/> so the static
    /// initializer of <see cref="Strings"/> has a deterministic value even
    /// if (somehow) a window is constructed before the App wiring runs.
    /// </summary>
    public static AppLanguage Current { get; private set; } = English;

    /// <summary>True when this language is one of the Chinese variants.</summary>
    public bool IsChinese => Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the supported language for the given culture name. Any Chinese
    /// variant (zh-CN / zh-Hans / zh-TW / zh-SG / …) maps to <see cref="Chinese"/>;
    /// everything else maps to <see cref="English"/>. Unknown / null → English.
    /// </summary>
    public static AppLanguage FromCultureName(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return English;
        }
        return cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? Chinese
            : English;
    }

    /// <summary>
    /// Reads the OS UI culture (<see cref="CultureInfo.InstalledUICulture"/>)
    /// and maps it to a supported language. Used on first launch when no
    /// <c>ui-language.json</c> exists yet.
    /// </summary>
    public static AppLanguage DetectFromOS() =>
        FromCultureName(CultureInfo.InstalledUICulture?.Name);

    /// <summary>
    /// Sets <see cref="Current"/>. Called once at startup, before any window
    /// is constructed, so that <c>Strings</c>'s static initializer picks up
    /// the right dictionary.
    /// </summary>
    public static void Set(AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        Current = language;
    }
}
