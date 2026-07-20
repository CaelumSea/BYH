using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Core.Selection;

/// <summary>
/// Axis-based drag and double-click classifier using platform system metrics.
/// There is intentionally no maximum drag duration: slow text selection is valid.
/// </summary>
public sealed class SystemMetricGestureClassifier : IGestureClassifier
{
    private readonly object _gate = new();
    private readonly ISystemMetrics _metrics;
    private PointerSample? _mouseDown;
    private PointerSample? _lastClickUp;

    public SystemMetricGestureClassifier(ISystemMetrics metrics)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public SelectionGesture? Process(MouseEventData mouseEvent, nint rootWindowHandle, uint processId)
    {
        ArgumentNullException.ThrowIfNull(mouseEvent);

        lock (_gate)
        {
            var current = new PointerSample(
                mouseEvent.X,
                mouseEvent.Y,
                mouseEvent.TimestampMs,
                rootWindowHandle,
                processId);

            if (mouseEvent.Message == MouseMessageType.LeftButtonDown)
            {
                _mouseDown = current;
                return null;
            }

            if (mouseEvent.Message != MouseMessageType.LeftButtonUp || _mouseDown is not { } mouseDown)
            {
                return null;
            }

            _mouseDown = null;

            bool sameSource = mouseDown.RootWindowHandle == current.RootWindowHandle &&
                              mouseDown.ProcessId == current.ProcessId &&
                              current.RootWindowHandle != 0 &&
                              current.ProcessId != 0;

            bool isDrag = AxisDistance(mouseDown.X, current.X) >= _metrics.DragThresholdX ||
                          AxisDistance(mouseDown.Y, current.Y) >= _metrics.DragThresholdY;

            bool isClickCandidate = sameSource && !isDrag;
            bool isDoubleClick = isClickCandidate && IsDoubleClick(current);

            _lastClickUp = isClickCandidate ? current : null;

            if (!sameSource || (!isDrag && !isDoubleClick))
            {
                return null;
            }

            return new SelectionGesture(
                current.X,
                current.Y,
                mouseDown.X,
                mouseDown.Y,
                mouseDown.TimestampMs,
                current.TimestampMs,
                current.RootWindowHandle,
                current.ProcessId);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _mouseDown = null;
            _lastClickUp = null;
        }
    }

    private bool IsDoubleClick(PointerSample current)
    {
        if (_lastClickUp is not { } previous)
        {
            return false;
        }

        long elapsed = current.TimestampMs - previous.TimestampMs;
        return elapsed >= 0 &&
               elapsed <= _metrics.DoubleClickTimeMs &&
               AxisDistance(previous.X, current.X) <= _metrics.DoubleClickWidth / 2 &&
               AxisDistance(previous.Y, current.Y) <= _metrics.DoubleClickHeight / 2 &&
               previous.RootWindowHandle == current.RootWindowHandle &&
               previous.ProcessId == current.ProcessId;
    }

    private static long AxisDistance(int first, int second) => Math.Abs((long)first - second);

    private readonly record struct PointerSample(
        int X,
        int Y,
        long TimestampMs,
        nint RootWindowHandle,
        uint ProcessId);
}
