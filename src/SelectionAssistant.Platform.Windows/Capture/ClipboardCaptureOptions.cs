namespace SelectionAssistant.Platform.Windows.Capture;

public sealed record ClipboardCaptureOptions(
    TimeSpan ChangeTimeout,
    TimeSpan StabilizationDelay,
    TimeSpan CancellationCleanupTimeout,
    TimeSpan OverallTimeout,
    int MaxTextLength)
{
    public static ClipboardCaptureOptions Default { get; } = new(
        ChangeTimeout: TimeSpan.FromMilliseconds(300),
        StabilizationDelay: TimeSpan.FromMilliseconds(50),
        CancellationCleanupTimeout: TimeSpan.FromMilliseconds(500),
        OverallTimeout: TimeSpan.FromMilliseconds(1_200),
        MaxTextLength: 100_000);

    public ClipboardCaptureOptions Validate()
    {
        if (ChangeTimeout <= TimeSpan.Zero ||
            StabilizationDelay <= TimeSpan.Zero ||
            CancellationCleanupTimeout <= TimeSpan.Zero ||
            OverallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ClipboardCaptureOptions));
        }

        if (MaxTextLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTextLength));
        }

        return this;
    }
}

public enum SimulatedCopyChord
{
    CtrlInsert,
    CtrlC,
}

public interface ICopyInputInjector
{
    bool HasInterferingModifiers();

    bool CanInjectInto(SelectionAssistant.Platform.Abstractions.SelectionGesture gesture);

    bool SendCopyChord(SimulatedCopyChord chord);
}

public sealed record ClipboardCaptureInvocation(
    IReadOnlyList<SimulatedCopyChord> Chords,
    TimeSpan? StabilizationDelay = null)
{
    public ClipboardCaptureInvocation Validate()
    {
        ArgumentNullException.ThrowIfNull(Chords);
        if (Chords.Count == 0)
        {
            throw new ArgumentException("At least one copy chord is required.", nameof(Chords));
        }

        if (StabilizationDelay is { } delay && delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StabilizationDelay));
        }

        return this;
    }
}

public interface IConfiguredClipboardCapture
{
    Task<SelectionAssistant.Platform.Abstractions.CaptureResult> CaptureAsync(
        SelectionAssistant.Platform.Abstractions.SelectionGesture gesture,
        ClipboardCaptureInvocation invocation,
        CancellationToken token);
}
