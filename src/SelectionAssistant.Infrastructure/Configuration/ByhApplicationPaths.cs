namespace SelectionAssistant.Infrastructure.Configuration;

public sealed record ByhApplicationPaths(string BaseDirectory)
{
    public string CapturePolicyFile =>
        Path.Combine(BaseDirectory, "capture-policies.json");

    public string ProvidersFile =>
        Path.Combine(BaseDirectory, "providers.json");

    /// <summary>
    /// Global prompt-template overrides (translate/summarize/explain). Shared by
    /// all providers. Missing file = use built-in defaults.
    /// </summary>
    public string PromptTemplatesFile =>
        Path.Combine(BaseDirectory, "prompt-templates.json");

    /// <summary>
    /// R24 vision OCR tier settings (enabled flag + which provider/model/prompt
    /// to use for screenshot OCR). Missing file = built-in defaults.
    /// </summary>
    public string VisionCaptureFile =>
        Path.Combine(BaseDirectory, "vision.json");

    /// <summary>
    /// R40 Ocean Eyes trigger (keyboard shortcut + optional mouse chord).
    /// Renamed from <c>quick-tools.json</c>; the store migrates the legacy
    /// file transparently on first load.
    /// </summary>
    public string OceanEyesTriggerFile =>
        Path.Combine(BaseDirectory, "ocean-eyes.json");

    /// <summary>
    /// R40 legacy migration source. Returned only so
    /// <see cref="OceanEyesTriggerStore"/> can read pre-R40 bindings; never
    /// written. May be deleted by the user after the first R40 launch.
    /// </summary>
    public string QuickToolsTriggerFileLegacy =>
        Path.Combine(BaseDirectory, "quick-tools.json");

    /// <summary>
    /// R40 Ocean Eyes screenshot save settings (path + auto-save toggle +
    /// clipboard toggle + UIA assist toggle). Missing file = defaults.
    /// </summary>
    public string OceanEyesCaptureFile =>
        Path.Combine(BaseDirectory, "ocean-eyes-capture.json");

    /// <summary>R32 Spotlight launcher-search panel keyboard shortcut.</summary>
    public string SpotlightTriggerFile =>
        Path.Combine(BaseDirectory, "spotlight-trigger.json");

    /// <summary>R54 clipboard-history popup keyboard shortcut (default Ctrl+Alt+V).</summary>
    public string ClipboardHistoryTriggerFile =>
        Path.Combine(BaseDirectory, "clipboard-history-trigger.json");

    /// <summary>R54 clipboard-history feature toggles (enabled / auto-paste /
    /// max entries / exclude-app list / mask-sensitive).</summary>
    public string ClipboardHistorySettingsFile =>
        Path.Combine(BaseDirectory, "clipboard-history-settings.json");

    /// <summary>R54 clipboard-history entries (text records, JSON).</summary>
    public string ClipboardHistoryFile =>
        Path.Combine(BaseDirectory, "clipboard-history.json");

    /// <summary>R54 v1.1 clipboard-history tags + entry→tag assignments (JSON).
    /// Kept separate from <see cref="ClipboardHistoryFile"/> so the main history
    /// schema stays frozen; tag edits never rewrite the (large) history file.</summary>
    public string ClipboardHistoryTagsFile =>
        Path.Combine(BaseDirectory, "clipboard-history-tags.json");

    /// <summary>R54 v1.2 v6: user-imported icon library (SVG path-data extracted
    /// from imported SVG files). Kept separate so importing icons never
    /// rewrites the tag-assignment file. Each entry is (name, pathData); the
    /// stored tag-icon value is <c>user:&lt;name&gt;</c>.</summary>
    public string ClipboardHistoryIconLibraryFile =>
        Path.Combine(BaseDirectory, "clipboard-history-icons.json");

    /// <summary>R54 v2: directory holding PNG files for image clipboard entries
    /// (screenshots / copied bitmaps). Each image entry stores only the file
    /// name in <c>clipboard-history.json</c>; the PNG lives here. Evicted/deleted
    /// entries have their PNG removed to avoid the Ditto-style disk bloat where
    /// images embedded in the store file balloon its size.</summary>
    public string ClipboardImagesDirectory =>
        Path.Combine(BaseDirectory, "clipboard-images");

    /// <summary>R102: directory holding monthly archive shards
    /// (<c>YYYY-MM.json</c>) for clipboard entries evicted from
    /// <see cref="ClipboardHistoryFile"/> by LRU. Text entries that fall off
    /// the end of the live window are appended here instead of being silently
    /// dropped, giving users a long-term searchable history. Managed by
    /// <c>ClipboardArchiveStore</c>.</summary>
    public string ClipboardArchiveDirectory =>
        Path.Combine(BaseDirectory, "clipboard-archive");

    /// <summary>
    /// R37: user-configurable toolbar built-in shortcut keys for Prompt/Copy/
    /// Paste (defaults R/C/V). Missing file = built-in defaults.
    /// </summary>
    public string ToolbarShortcutsFile =>
        Path.Combine(BaseDirectory, "toolbar-shortcuts.json");

    /// <summary>
    /// R23 launcher entries (quick-launch software/URLs). User-added only; no
    /// built-ins. Missing file = empty set.
    /// </summary>
    public string LauncherEntriesFile =>
        Path.Combine(BaseDirectory, "launcher-entries.json");

    /// <summary>
    /// Cache directory for extracted launcher icons (PNG files keyed by entry
    /// id + target hash). Created on first use.
    /// </summary>
    public string LauncherIconsDirectory =>
        Path.Combine(BaseDirectory, "launcher-icons");

    /// <summary>UI language preference ("language": "en"|"zh-CN"). Missing
    /// file = auto-detect from the OS UI culture (see
    /// <see cref="SelectionAssistant.Core.I18n.AppLanguage.DetectFromOS"/>).
    /// First launch on a Chinese Windows → Chinese UI; everything else →
    /// English. Once the user picks a language in Settings → General, the
    /// choice is persisted here and wins over OS detection on every later
    /// launch.</summary>
    public string UiLanguageFile =>
        Path.Combine(BaseDirectory, "ui-language.json");

    public string SecretsDirectory =>
        Path.Combine(BaseDirectory, "secrets");

    public string LogsDirectory => Path.Combine(BaseDirectory, "logs");

    public string LogFile => Path.Combine(LogsDirectory, "BYH.log");

    public static ByhApplicationPaths CreateDefault() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BYH"));

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(SecretsDirectory);
        Directory.CreateDirectory(ClipboardImagesDirectory);
        Directory.CreateDirectory(ClipboardArchiveDirectory);
        // Launcher icon cache (extracted .exe icons persisted as PNG so the
        // settings/spotlight lists don't re-extract on every refresh).
        Directory.CreateDirectory(LauncherIconsDirectory);
    }
}
