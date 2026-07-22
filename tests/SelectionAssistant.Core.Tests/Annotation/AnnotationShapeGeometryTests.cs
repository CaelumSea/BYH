using SelectionAssistant.Core.Annotation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Annotation;

/// <summary>
/// R48: tests for AnnotationShapeGeometry pure functions.
/// </summary>
public sealed class AnnotationShapeGeometryTests
{
    // ── NormalizeRect ────────────────────────────────────────────────

    [Fact]
    public void NormalizeRect_PositiveDrag_ReturnsCorrectRect()
    {
        var rect = AnnotationShapeGeometry.NormalizeRect(10, 20, 110, 70);
        Assert.Equal(10, rect.Left);
        Assert.Equal(20, rect.Top);
        Assert.Equal(100, rect.Width);
        Assert.Equal(50, rect.Height);
    }

    [Fact]
    public void NormalizeRect_NegativeDrag_SwapsCorners()
    {
        var rect = AnnotationShapeGeometry.NormalizeRect(110, 70, 10, 20);
        Assert.Equal(10, rect.Left);
        Assert.Equal(20, rect.Top);
        Assert.Equal(100, rect.Width);
        Assert.Equal(50, rect.Height);
    }

    [Fact]
    public void NormalizeRect_ZeroSize_ReturnsZeroRect()
    {
        var rect = AnnotationShapeGeometry.NormalizeRect(50, 50, 50, 50);
        Assert.Equal(50, rect.Left);
        Assert.Equal(50, rect.Top);
        Assert.Equal(0, rect.Width);
        Assert.Equal(0, rect.Height);
    }

    // ── NormalizeEllipse ─────────────────────────────────────────────

    [Fact]
    public void NormalizeEllipse_PositiveDrag_ReturnsCorrectEllipse()
    {
        var ellipse = AnnotationShapeGeometry.NormalizeEllipse(10, 20, 110, 70);
        Assert.Equal(10, ellipse.Left);
        Assert.Equal(20, ellipse.Top);
        Assert.Equal(100, ellipse.Width);
        Assert.Equal(50, ellipse.Height);
    }

    [Fact]
    public void NormalizeEllipse_NegativeDrag_SwapsCorners()
    {
        var ellipse = AnnotationShapeGeometry.NormalizeEllipse(110, 70, 10, 20);
        Assert.Equal(10, ellipse.Left);
        Assert.Equal(20, ellipse.Top);
        Assert.Equal(100, ellipse.Width);
        Assert.Equal(50, ellipse.Height);
    }

    // ── ApplyShiftConstraint ─────────────────────────────────────────

    [Fact]
    public void ApplyShiftConstraint_Rectangle_ShiftHeld_ConstrainsToSquare()
    {
        var rect = new RectangleAnnotation(10, 20, 100, 50);
        var constrained = AnnotationShapeGeometry.ApplyShiftConstraint(rect, shift: true);
        Assert.Equal(50, constrained.Width);
        Assert.Equal(50, constrained.Height);
        Assert.Equal(10, constrained.Left);
        Assert.Equal(20, constrained.Top);
    }

    [Fact]
    public void ApplyShiftConstraint_Rectangle_ShiftNotHeld_ReturnsUnchanged()
    {
        var rect = new RectangleAnnotation(10, 20, 100, 50);
        var result = AnnotationShapeGeometry.ApplyShiftConstraint(rect, shift: false);
        Assert.Equal(rect, result);
    }

    [Fact]
    public void ApplyShiftConstraint_Ellipse_ShiftHeld_ConstrainsToCircle()
    {
        var ellipse = new EllipseAnnotation(10, 20, 100, 50);
        var constrained = AnnotationShapeGeometry.ApplyShiftConstraint(ellipse, shift: true);
        Assert.Equal(50, constrained.Width);
        Assert.Equal(50, constrained.Height);
        Assert.Equal(10, constrained.Left);
        Assert.Equal(20, constrained.Top);
    }

    [Fact]
    public void ApplyShiftConstraint_Ellipse_ShiftNotHeld_ReturnsUnchanged()
    {
        var ellipse = new EllipseAnnotation(10, 20, 100, 50);
        var result = AnnotationShapeGeometry.ApplyShiftConstraint(ellipse, shift: false);
        Assert.Equal(ellipse, result);
    }

    // ── ComputeArrowHead ─────────────────────────────────────────────

    [Fact]
    public void ComputeArrowHead_HorizontalArrow_TipAtEndPoint()
    {
        var (tipX, tipY, b1x, b1y, b2x, b2y) = AnnotationShapeGeometry.ComputeArrowHead(0, 0, 100, 0);
        // Tip should be at the end point.
        Assert.Equal(100, tipX);
        Assert.Equal(0, tipY);
        // Barbs should exist (not degenerate).
        Assert.True(b1x != tipX || b1y != tipY);
        Assert.True(b2x != tipX || b2y != tipY);
    }

    [Fact]
    public void ComputeArrowHead_VerticalArrow_TipAtEndPoint()
    {
        var (tipX, tipY, b1x, b1y, b2x, b2y) = AnnotationShapeGeometry.ComputeArrowHead(0, 0, 0, 100);
        // Tip should be at the end point.
        Assert.Equal(0, tipX);
        Assert.Equal(100, tipY);
        // Barbs should exist (not degenerate).
        Assert.True(b1x != tipX || b1y != tipY);
        Assert.True(b2x != tipX || b2y != tipY);
    }

    [Fact]
    public void ComputeArrowHead_DegenerateArrow_ReturnsEndPoint()
    {
        var (tipX, tipY, b1x, b1y, b2x, b2y) = AnnotationShapeGeometry.ComputeArrowHead(50, 50, 50, 50);
        Assert.Equal(50, tipX);
        Assert.Equal(50, tipY);
        Assert.Equal(50, b1x);
        Assert.Equal(50, b1y);
        Assert.Equal(50, b2x);
        Assert.Equal(50, b2y);
    }
}
