using SelectionAssistant.Core.Annotation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Annotation;

/// <summary>
/// R48: tests for AnnotationSession undo stack.
/// </summary>
public sealed class AnnotationSessionTests
{
    // ── PushBadge ────────────────────────────────────────────────────

    [Fact]
    public void PushBadge_IncrementsNumber()
    {
        var session = new AnnotationSession();
        var b1 = session.PushBadge(10, 20);
        var b2 = session.PushBadge(30, 40);
        Assert.Equal(1, b1.Number);
        Assert.Equal(2, b2.Number);
    }

    [Fact]
    public void PushBadge_RecordsCoordinates()
    {
        var session = new AnnotationSession();
        var badge = session.PushBadge(123.5, 456.7);
        Assert.Equal(123.5, badge.X);
        Assert.Equal(456.7, badge.Y);
    }

    // ── Push + Undo ──────────────────────────────────────────────────

    [Fact]
    public void Undo_EmptySession_ReturnsNull()
    {
        var session = new AnnotationSession();
        Assert.Null(session.Undo());
    }

    [Fact]
    public void Undo_AfterPush_ReturnsLastItem()
    {
        var session = new AnnotationSession();
        var rect = new RectangleAnnotation(10, 20, 100, 50);
        session.Push(rect);
        var undone = session.Undo();
        Assert.Equal(rect, undone);
    }

    [Fact]
    public void Undo_AfterPush_RemovesItem()
    {
        var session = new AnnotationSession();
        session.Push(new RectangleAnnotation(10, 20, 100, 50));
        session.Undo();
        Assert.Equal(0, session.Count);
    }

    [Fact]
    public void Undo_MixedTypes_LIFOOrder()
    {
        var session = new AnnotationSession();
        var badge = session.PushBadge(10, 20);
        var rect = new RectangleAnnotation(10, 20, 100, 50);
        session.Push(rect);
        var arrow = new ArrowAnnotation(0, 0, 100, 100);
        session.Push(arrow);

        Assert.Equal(3, session.Count);

        var undone1 = session.Undo();
        Assert.Equal(arrow, undone1);

        var undone2 = session.Undo();
        Assert.Equal(rect, undone2);

        var undone3 = session.Undo();
        Assert.Equal(badge, undone3);

        Assert.Null(session.Undo());
    }

    // ── Clear ────────────────────────────────────────────────────────

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var session = new AnnotationSession();
        session.PushBadge(10, 20);
        session.Push(new RectangleAnnotation(10, 20, 100, 50));
        session.Push(new ArrowAnnotation(0, 0, 100, 100));
        session.Clear();
        Assert.Equal(0, session.Count);
        Assert.Null(session.Undo());
    }

    // ── IAnnotationItem equality ──────────────────────────────────────

    [Fact]
    public void RectangleAnnotation_Equality_Works()
    {
        var r1 = new RectangleAnnotation(10, 20, 100, 50);
        var r2 = new RectangleAnnotation(10, 20, 100, 50);
        Assert.Equal(r1, r2);
    }

    [Fact]
    public void EllipseAnnotation_Equality_Works()
    {
        var e1 = new EllipseAnnotation(10, 20, 100, 50);
        var e2 = new EllipseAnnotation(10, 20, 100, 50);
        Assert.Equal(e1, e2);
    }

    [Fact]
    public void ArrowAnnotation_Equality_Works()
    {
        var a1 = new ArrowAnnotation(0, 0, 100, 100);
        var a2 = new ArrowAnnotation(0, 0, 100, 100);
        Assert.Equal(a1, a2);
    }

    // ── PenStrokeAnnotation / HighlightStrokeAnnotation ──────────────

    [Fact]
    public void PenStrokeAnnotation_Points_ArePreserved()
    {
        var points = new List<(double X, double Y)> { (0, 0), (10, 10), (20, 5) };
        var pen = new PenStrokeAnnotation(points);
        Assert.Equal(3, pen.Points.Count);
        Assert.Equal((0, 0), pen.Points[0]);
        Assert.Equal((10, 10), pen.Points[1]);
        Assert.Equal((20, 5), pen.Points[2]);
    }

    [Fact]
    public void HighlightStrokeAnnotation_Points_ArePreserved()
    {
        var points = new List<(double X, double Y)> { (0, 0), (10, 10), (20, 5) };
        var highlight = new HighlightStrokeAnnotation(points);
        Assert.Equal(3, highlight.Points.Count);
    }

    // ── Push 5 different types then undo LIFO ────────────────────────

    [Fact]
    public void PushFiveDifferentTypes_UndoLIFO()
    {
        var session = new AnnotationSession();
        var badge = session.PushBadge(10, 20);
        var rect = new RectangleAnnotation(10, 20, 100, 50);
        var ellipse = new EllipseAnnotation(10, 20, 100, 50);
        var arrow = new ArrowAnnotation(0, 0, 100, 100);
        var pen = new PenStrokeAnnotation(new List<(double, double)> { (0, 0), (10, 10) });

        session.Push(rect);
        session.Push(ellipse);
        session.Push(arrow);
        session.Push(pen);

        Assert.Equal(5, session.Count);

        Assert.Equal(pen, session.Undo());
        Assert.Equal(arrow, session.Undo());
        Assert.Equal(ellipse, session.Undo());
        Assert.Equal(rect, session.Undo());
        Assert.Equal(badge, session.Undo());
        Assert.Null(session.Undo());
    }
}
