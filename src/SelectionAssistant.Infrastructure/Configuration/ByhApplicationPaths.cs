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
    }
}
