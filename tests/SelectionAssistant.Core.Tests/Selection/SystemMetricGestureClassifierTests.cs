using SelectionAssistant.Core.Selection;
using SelectionAssistant.Platform.Abstractions;
using Xunit;

namespace SelectionAssistant.Core.Tests.Selection;

public sealed class SystemMetricGestureClassifierTests
{
    private readonly SystemMetricGestureClassifier _classifier = new(new FixedMetrics());

    [Fact]
    public void HorizontalMovementAtThreshold_IsDrag()
    {
        ProcessDown(10, 20, 100, root: 1, process: 11);

        SelectionGesture? gesture = ProcessUp(14, 20, 200, root: 1, process: 11);

        Assert.NotNull(gesture);
    }

    [Fact]
    public void DiagonalMovementBelowBothAxes_IsNotDrag()
    {
        ProcessDown(10, 20, 100, root: 1, process: 11);

        SelectionGesture? gesture = ProcessUp(13, 23, 200, root: 1, process: 11);

        Assert.Null(gesture);
    }

    [Fact]
    public void SlowDrag_HasNoMaximumDuration()
    {
        ProcessDown(0, 0, 100, root: 1, process: 11);

        SelectionGesture? gesture = ProcessUp(0, 5, 60_100, root: 1, process: 11);

        Assert.NotNull(gesture);
    }

    [Fact]
    public void SecondClickInSystemRectangle_IsDoubleClickSelection()
    {
        Click(100, 100, downAt: 100, upAt: 120, root: 1, process: 11);

        SelectionGesture? secondClick = Click(103, 102, downAt: 300, upAt: 320, root: 1, process: 11);

        Assert.NotNull(secondClick);
    }

    [Fact]
    public void QuickClicksAcrossRootWindows_AreNotDoubleClick()
    {
        Click(100, 100, downAt: 100, upAt: 120, root: 1, process: 11);

        SelectionGesture? secondClick = Click(100, 100, downAt: 300, upAt: 320, root: 2, process: 11);

        Assert.Null(secondClick);
    }

    [Fact]
    public void MouseDownAndUpAcrossProcesses_AreRejected()
    {
        ProcessDown(0, 0, 100, root: 1, process: 11);

        SelectionGesture? gesture = ProcessUp(10, 0, 200, root: 2, process: 22);

        Assert.Null(gesture);
    }

    private SelectionGesture? Click(
        int x,
        int y,
        long downAt,
        long upAt,
        nint root,
        uint process)
    {
        ProcessDown(x, y, downAt, root, process);
        return ProcessUp(x, y, upAt, root, process);
    }

    private void ProcessDown(int x, int y, long at, nint root, uint process)
    {
        _classifier.Process(
            new MouseEventData(x, y, MouseMessageType.LeftButtonDown, at, false, 0),
            root,
            process);
    }

    private SelectionGesture? ProcessUp(int x, int y, long at, nint root, uint process) =>
        _classifier.Process(
            new MouseEventData(x, y, MouseMessageType.LeftButtonUp, at, false, 0),
            root,
            process);

    private sealed class FixedMetrics : ISystemMetrics
    {
        public int DragThresholdX => 4;
        public int DragThresholdY => 4;
        public int DoubleClickTimeMs => 500;
        public int DoubleClickWidth => 8;
        public int DoubleClickHeight => 8;
    }
}
