using SelectionAssistant.Core.Selection;
using SelectionAssistant.Platform.Abstractions;
using Xunit;

namespace SelectionAssistant.Core.Tests.Selection;

public sealed class ChordDetectorTests
{
    [Fact]
    public void LeftAndRightDownCloseTogether_FiresOnce()
    {
        var detector = new ChordDetector();
        int fires = 0;
        (int, int)? pos = null;
        detector.ChordDetected += (x, y) => { fires++; pos = (x, y); };

        // Left down at t=100, right down at t=200 → within the 600 ms window.
        Feed(detector, MouseMessageType.LeftButtonDown, 30, 40, 100);
        bool fired = Feed(detector, MouseMessageType.RightButtonDown, 35, 45, 200);

        Assert.True(fired);
        Assert.Equal(1, fires);
        Assert.Equal((35, 45), pos);
    }

    [Fact]
    public void RightThenLeftAlso_Fires()
    {
        var detector = new ChordDetector();
        int fires = 0;
        detector.ChordDetected += (_, _) => fires++;

        Feed(detector, MouseMessageType.RightButtonDown, 0, 0, 100);
        bool fired = Feed(detector, MouseMessageType.LeftButtonDown, 0, 0, 300);

        Assert.True(fired);
        Assert.Equal(1, fires);
    }

    [Fact]
    public void DownTooFarApart_DoesNotFire()
    {
        var detector = new ChordDetector();
        detector.ChordDetected += (_, _) => Assert.Fail("should not fire");

        // 700 ms apart exceeds the 600 ms window.
        Feed(detector, MouseMessageType.LeftButtonDown, 0, 0, 100);
        bool fired = Feed(detector, MouseMessageType.RightButtonDown, 0, 0, 800);

        Assert.False(fired);
    }

    [Fact]
    public void SecondChordRequiresBothButtonsUpFirst()
    {
        var detector = new ChordDetector();
        int fires = 0;
        detector.ChordDetected += (_, _) => fires++;

        Feed(detector, MouseMessageType.LeftButtonDown, 0, 0, 100);
        Feed(detector, MouseMessageType.RightButtonDown, 0, 0, 150);
        // Without releasing, another down should NOT re-fire (latched).
        bool second = Feed(detector, MouseMessageType.LeftButtonDown, 0, 0, 200);

        Assert.False(second);
        Assert.Equal(1, fires);
    }

    [Fact]
    public void PlainRightClickWithoutLeft_DoesNotFire()
    {
        var detector = new ChordDetector();
        detector.ChordDetected += (_, _) => Assert.Fail("should not fire");

        Feed(detector, MouseMessageType.RightButtonDown, 0, 0, 100);
        bool fired = Feed(detector, MouseMessageType.RightButtonUp, 0, 0, 200);

        Assert.False(fired);
    }

    [Fact]
    public void ReleasesResetStateForNextChord()
    {
        var detector = new ChordDetector();
        int fires = 0;
        detector.ChordDetected += (_, _) => fires++;

        // First chord.
        Feed(detector, MouseMessageType.LeftButtonDown, 0, 0, 100);
        Feed(detector, MouseMessageType.RightButtonDown, 0, 0, 150);
        // Release both.
        Feed(detector, MouseMessageType.LeftButtonUp, 0, 0, 300);
        Feed(detector, MouseMessageType.RightButtonUp, 0, 0, 310);
        // Second chord.
        Feed(detector, MouseMessageType.LeftButtonDown, 0, 0, 500);
        bool fired = Feed(detector, MouseMessageType.RightButtonDown, 0, 0, 550);

        Assert.True(fired);
        Assert.Equal(2, fires);
    }

    private static bool Feed(
        ChordDetector detector, MouseMessageType message, int x, int y, long timestampMs)
    {
        var evt = new MouseEventData(x, y, message, timestampMs, IsInjected: false, ExtraInfo: 0);
        return detector.OnMouseEvent(evt);
    }
}
