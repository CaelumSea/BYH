using System.Collections.Concurrent;
using SelectionAssistant.Core.Selection;
using SelectionAssistant.Platform.Abstractions;
using Xunit;

namespace SelectionAssistant.Core.Tests.Selection;

public sealed class SelectionSessionManagerTests
{
    [Fact]
    public async Task CaptureStartsBeforeToolbarDelay()
    {
        var order = new ConcurrentQueue<string>();
        var capture = new ImmediateCapture(order);
        var view = new RecordingView(order);
        await using var manager = new SelectionSessionManager(
            capture,
            view,
            new InlineDispatcher(),
            TimeSpan.FromMilliseconds(10));

        await manager.StartOrReplaceSessionAsync(Gesture(1));

        Assert.Equal(["capture:1", "show:1", "result:Text 1"], order);
    }

    [Fact]
    public async Task ReplacementPreventsStaleToolbarAndResultWrites()
    {
        var capture = new ControlledCapture();
        var view = new RecordingView();
        await using var manager = new SelectionSessionManager(
            capture,
            view,
            new InlineDispatcher(),
            TimeSpan.FromMilliseconds(25));

        Task first = manager.StartOrReplaceSessionAsync(Gesture(1));
        await capture.WaitUntilStartedAsync(1);

        Task second = manager.StartOrReplaceSessionAsync(Gesture(2));
        await capture.WaitUntilStartedAsync(2);

        capture.Complete(1, "stale"); // Simulates a capture source that ignored cancellation.
        capture.Complete(2, "latest");

        await Task.WhenAll(first, second);

        Assert.DoesNotContain(view.ShownGestureIds, id => id == 1);
        Assert.DoesNotContain(view.Results, result => result == "stale");
        Assert.Contains(2, view.ShownGestureIds);
        Assert.Contains("latest", view.Results);
    }

    [Fact]
    public async Task TenRapidSessions_OnlyLatestSessionUpdatesUi()
    {
        var capture = new ControlledCapture();
        var view = new RecordingView();
        await using var manager = new SelectionSessionManager(
            capture,
            view,
            new InlineDispatcher(),
            TimeSpan.FromMilliseconds(20));

        var sessions = new List<Task>();
        for (int id = 1; id <= 10; id++)
        {
            sessions.Add(manager.StartOrReplaceSessionAsync(Gesture(id)));
            await capture.WaitUntilStartedAsync(id);
        }

        for (int id = 1; id <= 10; id++)
        {
            capture.Complete(id, $"Text {id}");
        }

        await Task.WhenAll(sessions);

        Assert.Equal([10], view.ShownGestureIds);
        Assert.Equal(["Text 10"], view.Results);
    }

    [Fact]
    public async Task DismissCancelsCaptureAndPreventsLateUiWrites()
    {
        var capture = new ControlledCapture();
        var view = new RecordingView();
        await using var manager = new SelectionSessionManager(
            capture,
            view,
            new InlineDispatcher(),
            TimeSpan.FromMilliseconds(1));

        Task session = manager.StartOrReplaceSessionAsync(Gesture(1));
        await capture.WaitUntilStartedAsync(1);

        // Dismiss before capture resolves. Under the new "capture-first" model
        // the toolbar is never shown (no result → no toolbar), and the late
        // capture result is suppressed by the dismiss.
        await manager.DismissCurrentSessionAsync();
        capture.Complete(1, "late");
        await session;

        Assert.Empty(view.ShownGestureIds);
        Assert.Empty(view.Results);
    }

    [Fact]
    public async Task NoSelectedText_DoesNotShowToolbar()
    {
        // The core fix: a drag/double-click that captures NO text must not pop
        // the toolbar. Previously the toolbar showed eagerly after the
        // anti-flicker delay, before capture resolved.
        var capture = new ImmediateCapture(text: "", source: CaptureSource.Accessibility);
        var view = new RecordingView();
        await using var manager = new SelectionSessionManager(
            capture,
            view,
            new InlineDispatcher(),
            TimeSpan.FromMilliseconds(1));

        await manager.StartOrReplaceSessionAsync(Gesture(1));

        Assert.Empty(view.ShownGestureIds);
        Assert.Empty(view.Results);
    }

    [Fact]
    public async Task ManualFallbackSourceWithNoText_DoesNotShowToolbar()
    {
        // Regression guard for the "double-click empty space" misfire.
        // WindowsSelectionTextCapture returns ManualFallback whenever BOTH UIA
        // and clipboard come back empty — including double-clicks on empty
        // space. The session manager must NOT treat ManualFallback as a signal
        // to show the toolbar: no captured text → no toolbar, regardless of
        // source. Otherwise any empty-space double-click pops the toolbar.
        var capture = new ImmediateCapture(text: "", source: CaptureSource.ManualFallback);
        var view = new RecordingView();
        await using var manager = new SelectionSessionManager(
            capture,
            view,
            new InlineDispatcher(),
            TimeSpan.FromMilliseconds(1));

        await manager.StartOrReplaceSessionAsync(Gesture(1));

        Assert.Empty(view.ShownGestureIds);
    }

    [Fact]
    public async Task PhaseOneEmpty_DoesNotShowToolbar_AndDoesNotRunVision()
    {
        // R24 redesign: visual OCR moved OUT of the selection path (it's now a
        // chord → draw-region overlay flow). So when phase 1 (UIA+clipboard) is
        // empty, the session shows NO toolbar and must NOT call the vision tier
        // at all — even if a vision capture is wired. This is the regression
        // guard for "selection no longer auto-OCR".
        var order = new ConcurrentQueue<string>();
        var capture = new VisionCapableCapture(order, visionText: "should-not-run");
        var view = new RecordingView(order);
        await using var manager = new SelectionSessionManager(
            capture,
            view,
            new InlineDispatcher(),
            TimeSpan.FromMilliseconds(5));

        await manager.StartOrReplaceSessionAsync(Gesture(1));

        // Only the phase-1 capture ran; no toolbar shown, no vision call.
        Assert.Equal(["capture:1"], order);
        Assert.Empty(view.Results);
        Assert.Empty(view.ShownGestureIds);
    }

    private static SelectionGesture Gesture(int id) => new(
        MouseUpX: id,
        MouseUpY: 20,
        MouseDownX: id - 1,
        MouseDownY: 20,
        MouseDownTimestampMs: id * 10,
        MouseUpTimestampMs: id * 10 + 5,
        SourceRootHwnd: id,
        SourceProcessId: (uint)id);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class InlineDispatcher : ISelectionUiDispatcher
    {
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateCapture : ISelectionTextCapture
    {
        private readonly ConcurrentQueue<string>? _order;
        private readonly string? _textOverride;
        private readonly CaptureSource? _sourceOverride;

        public ImmediateCapture(ConcurrentQueue<string>? order = null, string? text = null, CaptureSource? source = null)
        {
            _order = order;
            _textOverride = text;
            _sourceOverride = source;
        }

        public Task<CaptureResult> CaptureAsync(SelectionGesture gesture, CancellationToken token)
        {
            _order?.Enqueue($"capture:{gesture.MouseUpX}");
            string text = _textOverride ?? $"Text {gesture.MouseUpX}";
            CaptureSource source = _sourceOverride ?? CaptureSource.Accessibility;
            return Task.FromResult(new CaptureResult(text, source, false));
        }
    }

    /// <summary>
    /// Capture whose phase-1 always returns empty (ManualFallback, mimicking
    /// "unselectable" content) but exposes a vision tier that yields a fixed
    /// (or null) phase-2 result. Used to exercise the two-phase toolbar flow.
    /// </summary>
    private sealed class VisionCapableCapture : ISelectionTextCapture
    {
        private readonly ConcurrentQueue<string>? _order;
        private readonly string? _visionText;   // null = vision yields nothing

        public VisionCapableCapture(ConcurrentQueue<string>? order = null, string? visionText = null)
        {
            _order = order;
            _visionText = visionText;
        }

        // Phase 1: always empty (unselectable content).
        public Task<CaptureResult> CaptureAsync(SelectionGesture gesture, CancellationToken token)
        {
            _order?.Enqueue($"capture:{gesture.MouseUpX}");
            return Task.FromResult(new CaptureResult(null, CaptureSource.ManualFallback, false));
        }

        public bool VisionTierAvailable => true;

        public Task<CaptureResult?> CaptureVisionAsync(SelectionGesture gesture, CancellationToken token)
        {
            _order?.Enqueue($"vision:{gesture.MouseUpX}");
            return _visionText is null
                ? Task.FromResult<CaptureResult?>(null)
                : Task.FromResult<CaptureResult?>(
                    new CaptureResult(_visionText, CaptureSource.Vision, false));
        }
    }

    private sealed class ControlledCapture : ISelectionTextCapture
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<CaptureResult>> _captures = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _started = new();

        public Task<CaptureResult> CaptureAsync(SelectionGesture gesture, CancellationToken token)
        {
            int id = gesture.MouseUpX;
            var completion = new TaskCompletionSource<CaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _captures[id] = completion;
            _started.GetOrAdd(id, _ => NewSignal()).TrySetResult();
            return completion.Task;
        }

        public Task WaitUntilStartedAsync(int id) =>
            _started.GetOrAdd(id, _ => NewSignal()).Task.WaitAsync(TimeSpan.FromSeconds(2));

        public void Complete(int id, string text)
        {
            _captures[id].TrySetResult(new CaptureResult(text, CaptureSource.Accessibility, false));
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingView : ISelectionSessionView
    {
        private readonly ConcurrentQueue<string>? _order;

        public RecordingView(ConcurrentQueue<string>? order = null)
        {
            _order = order;
        }

        public List<int> ShownGestureIds { get; } = [];

        public List<string> Results { get; } = [];

        public int HideCount { get; private set; }

        public void ShowToolbar(SelectionGesture gesture)
        {
            ShownGestureIds.Add(gesture.MouseUpX);
            _order?.Enqueue($"show:{gesture.MouseUpX}");
        }

        public void HideToolbar()
        {
            HideCount++;
            _order?.Enqueue("hide");
        }

        public void SetCaptureResult(CaptureResult result)
        {
            string text = result.Text ?? string.Empty;
            Results.Add(text);
            _order?.Enqueue($"result:{text}");
        }
    }
}
