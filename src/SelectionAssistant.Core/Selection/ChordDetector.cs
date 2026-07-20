using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Core.Selection;

/// <summary>
/// Detects a simultaneous left + right mouse-button press (a "chord"). Both
/// buttons must go down within a short window of each other. Once a chord is
/// detected, further events are ignored until both buttons are released, so a
/// single chord gesture fires exactly once.
/// </summary>
/// <remarks>
/// This is a passive observer fed by the mouse hook — the hook itself always
/// forwards events to the source application, so the user's right-click
/// context menus keep working. A chord is only recognised when the two buttons
/// go down close together; a normal right-click (right down alone, then up
/// without a near-simultaneous left down) is never reported as a chord.
/// </remarks>
public sealed class ChordDetector
{
    /// <summary>Max milliseconds between the two buttons going down to count as a chord.</summary>
    private const long ChordWindowMs = 600;

    private long _leftDownMs = long.MinValue;   // TickCount64 of last L-down; MinValue = none pending
    private long _rightDownMs = long.MinValue;
    private bool _bothDown;                      // latch: chord fired, waiting for both-up to reset

    /// <summary>
    /// Raised when a left+right chord is detected. Runs on the mouse-hook
    /// thread — the caller must marshal to the UI thread before showing UI.
    /// Args = the screen coordinates where the chord occurred (the later
    /// button-down's position).
    /// </summary>
    public event Action<int, int>? ChordDetected;

    /// <summary>Feeds a raw mouse event. Returns true if a chord fired for this event.</summary>
    public bool OnMouseEvent(MouseEventData mouseEvent)
    {
        switch (mouseEvent.Message)
        {
            case MouseMessageType.LeftButtonDown:
                _leftDownMs = mouseEvent.TimestampMs;
                return TryFire(mouseEvent);

            case MouseMessageType.RightButtonDown:
                _rightDownMs = mouseEvent.TimestampMs;
                return TryFire(mouseEvent);

            case MouseMessageType.LeftButtonUp:
            case MouseMessageType.RightButtonUp:
                // Reset the latch only once both buttons are back up. Either
                // up-event clears the "pending" timestamp for that button; the
                // _bothDown latch is cleared after the first up so a quick
                // second chord needs a fresh both-down.
                _leftDownMs = long.MinValue;
                _rightDownMs = long.MinValue;
                _bothDown = false;
                return false;

            default:
                return false;
        }
    }

    private bool TryFire(MouseEventData mouseEvent)
    {
        if (_bothDown)
        {
            return false;
        }

        // Both buttons must have a recent down timestamp within the window.
        if (_leftDownMs == long.MinValue || _rightDownMs == long.MinValue)
        {
            return false;
        }

        long delta = Math.Abs(_leftDownMs - _rightDownMs);
        if (delta > ChordWindowMs)
        {
            return false;
        }

        _bothDown = true;
        ChordDetected?.Invoke(mouseEvent.X, mouseEvent.Y);
        return true;
    }
}
