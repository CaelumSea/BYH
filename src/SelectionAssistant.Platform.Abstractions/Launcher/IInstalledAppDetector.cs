namespace SelectionAssistant.Platform.Abstractions;

/// <summary>
/// One application discovered by scanning the system (e.g. from Start Menu
/// shortcuts). <see cref="ExecutablePath"/> is an absolute path to the app's
/// launcher target — either a <c>.exe</c> (normal Win32 app) or a <c>.msc</c>
/// (MMC management console, launched via shell). <see cref="Name"/> is the
/// display label taken from the shortcut filename.
/// </summary>
public sealed record DetectedApp(string Name, string ExecutablePath);

/// <summary>
/// Scans the host OS for installed, launchable applications. Used by the
/// Launcher settings tab's "scan installed apps" feature so the user can
/// quickly populate their launcher list instead of browsing for each exe.
/// Implementations must be safe to call off the UI thread and must not throw
/// (return an empty list on failure). NativeAOT-safe (no reflection / dynamic
/// code generation).
/// </summary>
public interface IInstalledAppDetector
{
    /// <summary>
    /// Returns the detected applications, deduplicated by executable path and
    /// filtered to exclude help files, uninstallers, and other non-launchable
    /// shortcuts. The order is implementation-defined (typically the Start
    /// Menu traversal order). Never returns null.
    /// </summary>
    IReadOnlyList<DetectedApp> DetectInstalledApps();
}
