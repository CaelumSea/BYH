namespace SelectionAssistant.Core.Launcher;

/// <summary>
/// Outcome of a launch attempt. The caller drives a small state machine:
/// <list type="number">
///   <item>Call <c>LaunchAsync</c> with current clipboard/selection.</item>
///   <item>If <see cref="LauncherLaunchResult.NeedsPrompt"/> is true, show the
///     user an input dialog with <see cref="LauncherLaunchResult.Prompts"/>,
///     collect answers, then call <c>LaunchWithPromptAnswersAsync</c>.</item>
///   <item>Otherwise the launch either succeeded (<see cref="LauncherLaunchResult.Success"/>)
///     or failed (check <see cref="LauncherLaunchResult.ErrorMessage"/>).</item>
/// </list>
/// </summary>
public sealed record LauncherLaunchResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<string> Prompts,
    bool NeedsPrompt);
