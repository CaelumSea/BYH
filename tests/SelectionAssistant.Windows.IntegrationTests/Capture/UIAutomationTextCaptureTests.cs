using SelectionAssistant.Platform.Abstractions;
using SelectionAssistant.Platform.Windows.Capture;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests.Capture;

public sealed class UIAutomationTextCaptureTests
{
    [Fact]
    public async Task SuccessfulWorkerResult_IsReportedAsAccessibilityCapture()
    {
        using var capture = new UIAutomationTextCapture(
            () => new ImmediateBackend("selected text"),
            TimeSpan.FromMilliseconds(200));

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("selected text", result.Text);
        Assert.Equal(CaptureSource.Accessibility, result.Source);
        Assert.False(result.IsAmbiguous);
    }

    [Fact]
    public async Task TimedOutWorker_IsQuarantinedAndReplaced()
    {
        using var releaseBlockedWorker = new ManualResetEventSlim(false);
        int workersCreated = 0;

        using var capture = new UIAutomationTextCapture(
            () => Interlocked.Increment(ref workersCreated) == 1
                ? new BlockingBackend(releaseBlockedWorker)
                : new ImmediateBackend("from replacement"),
            TimeSpan.FromMilliseconds(40));

        CaptureResult timedOut = await capture.CaptureAsync(Gesture(), CancellationToken.None);
        CaptureResult replacement = await capture.CaptureAsync(Gesture(), CancellationToken.None);
        releaseBlockedWorker.Set();

        Assert.Null(timedOut.Text);
        Assert.Equal(CaptureSource.None, timedOut.Source);
        Assert.Equal("from replacement", replacement.Text);
        Assert.True(workersCreated >= 2);
    }

    [Fact]
    public async Task CallerCancellation_StopsWaitingWithoutClaimingNativeCancellation()
    {
        using var releaseWorker = new ManualResetEventSlim(false);
        using var capture = new UIAutomationTextCapture(
            () => new BlockingBackend(releaseWorker),
            TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => capture.CaptureAsync(Gesture(), cancellation.Token));

        releaseWorker.Set();
    }

    private static SelectionGesture Gesture() => new(
        MouseUpX: 100,
        MouseUpY: 200,
        MouseDownX: 90,
        MouseDownY: 200,
        MouseDownTimestampMs: 10,
        MouseUpTimestampMs: 20,
        SourceRootHwnd: 1,
        SourceProcessId: 1);

    private sealed class ImmediateBackend(string text) : IUiAutomationBackend
    {
        public UiAutomationReadResult ReadSelection(SelectionGesture gesture) => new(text);
    }

    private sealed class BlockingBackend(ManualResetEventSlim release) : IUiAutomationBackend
    {
        public UiAutomationReadResult ReadSelection(SelectionGesture gesture)
        {
            release.Wait(TimeSpan.FromSeconds(5));
            return new UiAutomationReadResult("late text");
        }
    }
}
