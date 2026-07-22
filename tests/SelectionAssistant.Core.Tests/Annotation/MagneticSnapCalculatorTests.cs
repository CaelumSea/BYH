using SelectionAssistant.Core.Annotation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Annotation;

public sealed class MagneticSnapCalculatorTests
{
    // Helper: single 1920x1080 screen work area at (0,0).
    private static readonly PhysicalRect Screen1920x1080 = new(0, 0, 1920, 1080);
    private static readonly PhysicalRect[] SingleScreen = [Screen1920x1080];
    private static readonly PhysicalRect[] NoOthers = [];

    [Fact]
    public void ScreenLeft_HitsWithinThreshold()
    {
        // Moving window Left=3, threshold 8 => within threshold => snap to Left=0.
        var moving = new PhysicalRect(3, 100, 203, 300);
        var (pos, hints) = MagneticSnapCalculator.ComputeSnap(moving, SingleScreen, NoOthers);
        Assert.Equal(0, pos.X);
        Assert.Equal(100, pos.Y);
        Assert.Contains(hints, h => h.Target == SnapTarget.ScreenLeft && h.Axis == SnapAxis.X);
    }

    [Fact]
    public void ScreenRight_HitsWithinThreshold()
    {
        // Moving window Right = 1920-5 = 1915, screen right = 1920 => delta=5, within threshold.
        var moving = new PhysicalRect(1715, 100, 1915, 300);
        var (pos, hints) = MagneticSnapCalculator.ComputeSnap(moving, SingleScreen, NoOthers);
        // Snapped so Right=1920 => Left = 1920 - Width = 1920 - 200 = 1720
        Assert.Equal(1720, pos.X);
        Assert.Contains(hints, h => h.Target == SnapTarget.ScreenRight && h.Axis == SnapAxis.X);
    }

    [Fact]
    public void ScreenTop_HitsWithinThreshold()
    {
        // Moving window Top=2, screen top=0 => delta=2, within threshold.
        var moving = new PhysicalRect(100, 2, 300, 202);
        var (pos, hints) = MagneticSnapCalculator.ComputeSnap(moving, SingleScreen, NoOthers);
        Assert.Equal(100, pos.X);
        Assert.Equal(0, pos.Y);
        Assert.Contains(hints, h => h.Target == SnapTarget.ScreenTop && h.Axis == SnapAxis.Y);
    }

    [Fact]
    public void ScreenBottom_HitsWithinThreshold()
    {
        // Moving window Bottom=1080-3=1077, screen bottom=1080 => delta=3, within threshold.
        var moving = new PhysicalRect(100, 877, 300, 1077);
        var (pos, hints) = MagneticSnapCalculator.ComputeSnap(moving, SingleScreen, NoOthers);
        // Snapped so Bottom=1080 => Top = 1080 - Height = 1080 - 200 = 880
        Assert.Equal(880, pos.Y);
        Assert.Contains(hints, h => h.Target == SnapTarget.ScreenBottom && h.Axis == SnapAxis.Y);
    }

    [Fact]
    public void ThresholdBoundary_7px_Hits()
    {
        // 7px from screen left, threshold=8 => should hit.
        var moving = new PhysicalRect(7, 100, 207, 300);
        var (pos, _) = MagneticSnapCalculator.ComputeSnap(moving, SingleScreen, NoOthers, threshold: 8.0);
        Assert.Equal(0, pos.X);
    }

    [Fact]
    public void ThresholdBoundary_9px_DoesNotHit()
    {
        // 9px from screen left, threshold=8 => should NOT hit.
        var moving = new PhysicalRect(9, 100, 209, 300);
        var (pos, hints) = MagneticSnapCalculator.ComputeSnap(moving, SingleScreen, NoOthers, threshold: 8.0);
        Assert.Equal(9, pos.X); // unchanged
        Assert.Empty(hints);
    }

    [Fact]
    public void OtherWindowEdge_HitsWithinThreshold()
    {
        // Two windows: other at (500,100,700,300), moving at (493,100,693,300).
        // Moving right = 693, other left = 500 => delta = 500-693 = -193 (too far).
        // Moving left = 493, other right = 700 => delta = 700-493 = 207 (too far).
        // Let's position moving so its right edge is within 8px of other's left:
        // other.Left=500, moving.Right should be 493-500 => let's do moving right=497.
        var other = new PhysicalRect(500, 100, 700, 300);
        var moving = new PhysicalRect(297, 100, 497, 300);
        var (pos, hints) = MagneticSnapCalculator.ComputeSnap(moving, SingleScreen, [other]);
        // Snap: moving right (497) -> other left (500) => delta=3 => snapped left = 500-200 = 300
        Assert.Equal(300, pos.X);
        Assert.Contains(hints, h => h.Target == SnapTarget.WindowLeft && h.Axis == SnapAxis.X);
    }

    [Fact]
    public void MultipleWindows_PicksClosest()
    {
        // Screen at 1920x1080. Two other windows:
        //   other1 at (100,100,300,300) — right edge = 300
        //   other2 at (200,100,400,300) — right edge = 400
        // Moving at (294,100,494,300): Left=294.
        //   distance to other1.Right (300): |300-294|=6 => snap target=300
        //   distance to other2.Right (400): |400-294|=106 => too far
        // Also check against other2.Left (200): moving right=494, distance |200-494|=294 => too far
        var other1 = new PhysicalRect(100, 100, 300, 300);
        var other2 = new PhysicalRect(200, 100, 400, 300);
        var moving = new PhysicalRect(294, 100, 494, 300);
        var (pos, hints) = MagneticSnapCalculator.ComputeSnap(moving, SingleScreen, [other1, other2]);
        // Snap to other1.Right=300 => left = 300 (moving left aligns with other1 right)
        Assert.Equal(300, pos.X);
        Assert.Contains(hints, h => h.Target == SnapTarget.WindowRight);
    }

    [Fact]
    public void ShiftHeld_ReturnsOriginalPosition()
    {
        var moving = new PhysicalRect(3, 2, 203, 202);
        var (pos, hints) = MagneticSnapCalculator.ComputeSnap(moving, SingleScreen, NoOthers, shiftHeld: true);
        Assert.Equal(3, pos.X);
        Assert.Equal(2, pos.Y);
        Assert.Empty(hints);
    }

    [Fact]
    public void MultiScreen_NegativeOffset_Works()
    {
        // Second monitor at x=-1920: work area (-1920, 0, 0, 1080).
        var screen = new PhysicalRect(-1920, 0, 0, 1080);
        // Moving window near the right edge of this screen (right edge = 0).
        var moving = new PhysicalRect(-205, 100, -5, 300);
        var (pos, hints) = MagneticSnapCalculator.ComputeSnap(moving, [screen], NoOthers);
        // Right edge = -5, screen right = 0 => delta=5 => snap right=0 => left = 0-200 = -200
        Assert.Equal(-200, pos.X);
        Assert.Contains(hints, h => h.Target == SnapTarget.ScreenRight && h.Axis == SnapAxis.X);
    }

    [Fact]
    public void NoSnapTargets_ReturnsOriginalPosition()
    {
        var moving = new PhysicalRect(500, 500, 700, 700);
        var (pos, hints) = MagneticSnapCalculator.ComputeSnap(moving, SingleScreen, NoOthers);
        Assert.Equal(500, pos.X);
        Assert.Equal(500, pos.Y);
        Assert.Empty(hints);
    }

    [Fact]
    public void EmptyWorkAreas_ReturnsOriginalPosition()
    {
        var moving = new PhysicalRect(3, 2, 203, 202);
        var (pos, hints) = MagneticSnapCalculator.ComputeSnap(moving, [], NoOthers);
        Assert.Equal(3, pos.X);
        Assert.Equal(2, pos.Y);
        Assert.Empty(hints);
    }

    // ── R55: shadow-margin inset (image rect vs window rect) ──────────────

    [Fact]
    public void InsetRect_ShrinksAllFourSidesByMargin()
    {
        // Window rect (0,0)-(200,100), margin 24 → image rect (24,24)-(176,76).
        var window = new PhysicalRect(0, 0, 200, 100);
        var image = MagneticSnapCalculator.InsetRect(window, 24.0);
        Assert.Equal(24, image.Left);
        Assert.Equal(24, image.Top);
        Assert.Equal(176, image.Right);
        Assert.Equal(76, image.Bottom);
    }

    [Fact]
    public void InsetRect_RespectsNonZeroOrigin()
    {
        var window = new PhysicalRect(1000, 500, 1200, 600);
        var image = MagneticSnapCalculator.InsetRect(window, 30.0);
        Assert.Equal(1030, image.Left);
        Assert.Equal(530, image.Top);
        Assert.Equal(1170, image.Right);
        Assert.Equal(570, image.Bottom);
    }

    [Fact]
    public void InsetRect_TruncatesFractionalMargin()
    {
        // margin 24.9 truncates to 24 per edge.
        var window = new PhysicalRect(0, 0, 100, 100);
        var image = MagneticSnapCalculator.InsetRect(window, 24.9);
        Assert.Equal(24, image.Left);
        Assert.Equal(76, image.Right);
    }

    /// <summary>
    /// R55 regression: the pinned window is larger than the image by a
    /// transparent shadow margin on every side. Snap must run against the
    /// INSET image rect so the image edge (not the blank window edge) aligns
    /// to the screen edge. Before the fix, snapping the window rect left the
    /// image a full margin away from the screen edge.
    /// </summary>
    [Fact]
    public void Snap_OnInsetImageRect_AlignsImageEdgeToScreen()
    {
        // Screen at (0,0)-(1920,1080). Window 248×248 (image 200×200 + 24 margin
        // each side). Drag window so its IMAGE left is ~3px from screen left:
        // image left = window left + 24; want window left = -21 → image left = 3.
        var window = new PhysicalRect(-21, 100, 227, 348);
        var image = MagneticSnapCalculator.InsetRect(window, 24.0);
        var (pos, hints) = MagneticSnapCalculator.ComputeSnap(image, SingleScreen, NoOthers);
        // Snapped image left = 0 → window left = 0 - 24 = -24 (image flush to edge).
        Assert.Equal(0, pos.X);
        Assert.Contains(hints, h => h.Target == SnapTarget.ScreenLeft && h.Axis == SnapAxis.X);
    }

    /// <summary>
    /// R55 regression: two pinned windows must snap image-edge to image-edge.
    /// Peer reports its IMAGE rect; the moving window snaps its image edge to
    /// the peer's image edge, leaving no shadow-margin gap between the images.
    /// </summary>
    [Fact]
    public void Snap_PeerReportsImageRect_ImagesAlignNoGap()
    {
        // Peer window image rect right edge = 500. Moving window image rect left
        // within threshold. Both are IMAGE rects (already inset by their owners).
        var peerImage = new PhysicalRect(300, 100, 500, 300);
        // Moving image left = 497, peer image right = 500 → delta 3 → snap to 500.
        var movingImage = new PhysicalRect(497, 100, 697, 300);
        var (pos, _) = MagneticSnapCalculator.ComputeSnap(movingImage, SingleScreen, [peerImage]);
        Assert.Equal(500, pos.X);
    }
}
