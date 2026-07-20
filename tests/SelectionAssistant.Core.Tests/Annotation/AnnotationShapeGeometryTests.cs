using SelectionAssistant.Core.Annotation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Annotation;

public sealed class AnnotationShapeGeometryTests
{
    // ── Shift constraint tests ──────────────────────────────────────────

    [Fact]
    public void ApplyShiftConstraint_Rectangle_WithShift_ConstrainsToSquare()
    {
        var rect = new RectangleAnnotation(10, 20, 100, 50);

        var result = AnnotationShapeGeometry.ApplyShiftConstraint(rect, shift: true);

        Assert.Equal(10, result.Left);
        Assert.Equal(20, result.Top);
        Assert.Equal(50, result.Width);   // min(100, 50) = 50
        Assert.Equal(50, result.Height);
    }

    [Fact]
    public void ApplyShiftConstraint_Rectangle_NoShift_ReturnsOriginal()
    {
        var rect = new RectangleAnnotation(10, 20, 100, 50);

        var result = AnnotationShapeGeometry.ApplyShiftConstraint(rect, shift: false);

        Assert.Equal(rect, result);
    }

    [Fact]
    public void ApplyShiftConstraint_Ellipse_WithShift_ConstrainsToCircle()
    {
        var ellipse = new EllipseAnnotation(5, 5, 80, 120);

        var result = AnnotationShapeGeometry.ApplyShiftConstraint(ellipse, shift: true);

        Assert.Equal(80, result.Width);   // min(80, 120) = 80
        Assert.Equal(80, result.Height);
    }

    [Fact]
    public void ApplyShiftConstraint_Ellipse_NoShift_ReturnsOriginal()
    {
        var ellipse = new EllipseAnnotation(5, 5, 80, 120);

        var result = AnnotationShapeGeometry.ApplyShiftConstraint(ellipse, shift: false);

        Assert.Equal(ellipse, result);
    }

    // ── Arrow head geometry tests ───────────────────────────────────────

    [Fact]
    public void ComputeArrowHead_HorizontalRight_TipAtEnd()
    {
        // Arrow from (0,0) to (100,0) — pointing right.
        var (tipX, tipY, b1x, b1y, b2x, b2y) =
            AnnotationShapeGeometry.ComputeArrowHead(0, 0, 100, 0, headLength: 12);

        Assert.Equal(100, tipX);
        Assert.Equal(0, tipY);
        // Both barbs should be to the left of the tip (smaller X).
        Assert.True(b1x < tipX);
        Assert.True(b2x < tipX);
        // Barbs should be symmetric around the line Y=0.
        Assert.True(Math.Abs(b1y + b2y) < 1e-6);
    }

    [Fact]
    public void ComputeArrowHead_VerticalDown_TipAtEnd()
    {
        // Arrow from (50,0) to (50,100) — pointing down.
        var (tipX, tipY, b1x, b1y, b2x, b2y) =
            AnnotationShapeGeometry.ComputeArrowHead(50, 0, 50, 100, headLength: 12);

        Assert.Equal(50, tipX);
        Assert.Equal(100, tipY);
        // Both barbs should be above the tip (smaller Y).
        Assert.True(b1y < tipY);
        Assert.True(b2y < tipY);
    }

    [Fact]
    public void ComputeArrowHead_Degenerate_ZeroLength()
    {
        var (tipX, tipY, b1x, b1y, b2x, b2y) =
            AnnotationShapeGeometry.ComputeArrowHead(50, 50, 50, 50);

        Assert.Equal(50, tipX);
        Assert.Equal(50, tipY);
        Assert.Equal(50, b1x);
        Assert.Equal(50, b1y);
    }

    // ── Normalize rect tests ───────────────────────────────────────────

    [Fact]
    public void NormalizeRect_ForwardDrag_PreservesOrigin()
    {
        var rect = AnnotationShapeGeometry.NormalizeRect(10, 20, 110, 70);

        Assert.Equal(10, rect.Left);
        Assert.Equal(20, rect.Top);
        Assert.Equal(100, rect.Width);
        Assert.Equal(50, rect.Height);
    }

    [Fact]
    public void NormalizeRect_ReverseDrag_SwapsOrigin()
    {
        var rect = AnnotationShapeGeometry.NormalizeRect(110, 70, 10, 20);

        Assert.Equal(10, rect.Left);
        Assert.Equal(20, rect.Top);
        Assert.Equal(100, rect.Width);
        Assert.Equal(50, rect.Height);
    }

    [Fact]
    public void NormalizeEllipse_ForwardDrag_PreservesOrigin()
    {
        var ellipse = AnnotationShapeGeometry.NormalizeEllipse(5, 5, 85, 65);

        Assert.Equal(5, ellipse.Left);
        Assert.Equal(5, ellipse.Top);
        Assert.Equal(80, ellipse.Width);
        Assert.Equal(60, ellipse.Height);
    }

    // ── Burn-in draw helpers tests (BGRA buffer) ───────────────────────

    [Fact]
    public void DrawLineOnBgra_Horizontal_DrawsPixels()
    {
        // 100x100 BGRA buffer, all zeros.
        var bgra = new byte[100 * 100 * 4];
        int imgW = 100, imgH = 100;

        // Draw a horizontal gold line from (10,50) to (90,50).
        BurnInHelpers.DrawLineOnBgra(bgra, imgW, imgH, 10, 50, 90, 50,
            thickness: 2, AnnotationShapeGeometry.GoldB, AnnotationShapeGeometry.GoldG,
            AnnotationShapeGeometry.GoldR, AnnotationShapeGeometry.GoldA);

        // Verify at least some pixels were drawn.
        bool anyDrawn = false;
        for (int x = 10; x <= 90; x++)
        {
            int off = (50 * imgW + x) * 4;
            if (bgra[off + 3] > 0) // alpha > 0
            {
                anyDrawn = true;
                // Check gold BGRA color.
                Assert.Equal(AnnotationShapeGeometry.GoldB, bgra[off]);
                Assert.Equal(AnnotationShapeGeometry.GoldG, bgra[off + 1]);
                Assert.Equal(AnnotationShapeGeometry.GoldR, bgra[off + 2]);
                break;
            }
        }
        Assert.True(anyDrawn, "Expected at least one drawn pixel on the line.");
    }

    [Fact]
    public void DrawRectangleStrokeOnBgra_DrawsBorder()
    {
        var bgra = new byte[100 * 100 * 4];
        int imgW = 100, imgH = 100;

        BurnInHelpers.DrawRectangleStrokeOnBgra(bgra, imgW, imgH,
            10, 10, 90, 90, thickness: 2,
            AnnotationShapeGeometry.GoldB, AnnotationShapeGeometry.GoldG,
            AnnotationShapeGeometry.GoldR, AnnotationShapeGeometry.GoldA);

        // Check top-left corner pixel area was drawn.
        bool topLeftDrawn = false;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int px = 10 + dx, py = 10 + dy;
                if (px >= 0 && px < imgW && py >= 0 && py < imgH)
                {
                    int off = (py * imgW + px) * 4;
                    if (bgra[off + 3] > 0)
                    {
                        topLeftDrawn = true;
                    }
                }
            }
        }
        Assert.True(topLeftDrawn, "Expected drawn pixels near rectangle top-left corner.");
    }
}
