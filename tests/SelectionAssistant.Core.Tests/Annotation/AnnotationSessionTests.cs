using SelectionAssistant.Core.Annotation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Annotation;

public sealed class AnnotationSessionTests
{
    [Fact]
    public void PushBadge_IncrementsNumberFromOne()
    {
        var session = new AnnotationSession();

        var b1 = session.PushBadge(10, 20);
        var b2 = session.PushBadge(30, 40);

        Assert.Equal(1, b1.Number);
        Assert.Equal(2, b2.Number);
    }

    [Fact]
    public void PushBadge_AfterUndo_ContinuesSequence()
    {
        var session = new AnnotationSession();
        session.PushBadge(10, 20); // #1
        session.PushBadge(30, 40); // #2
        session.Undo();            // remove #2

        var b3 = session.PushBadge(50, 60); // should be #3

        Assert.Equal(3, b3.Number);
    }

    [Fact]
    public void Push_AndUndo_MixedTypes()
    {
        var session = new AnnotationSession();
        var badge = session.PushBadge(10, 20);
        var rect = new RectangleAnnotation(5, 5, 100, 50);
        session.Push(rect);
        var ellipse = new EllipseAnnotation(10, 10, 80, 60);
        session.Push(ellipse);

        Assert.Equal(3, session.Count);

        IAnnotationItem? undone = session.Undo();
        Assert.Equal(ellipse, undone);
        Assert.Equal(2, session.Count);

        undone = session.Undo();
        Assert.Equal(rect, undone);
        Assert.Equal(1, session.Count);

        undone = session.Undo();
        Assert.Equal(badge, undone);
        Assert.Equal(0, session.Count);
    }

    [Fact]
    public void Undo_OnEmpty_ReturnsNull()
    {
        var session = new AnnotationSession();

        Assert.Null(session.Undo());
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var session = new AnnotationSession();
        session.PushBadge(10, 20);
        session.Push(new ArrowAnnotation(0, 0, 100, 100));

        session.Clear();

        Assert.Equal(0, session.Count);
        Assert.Empty(session.Items);
    }

    [Fact]
    public void Clear_ResetsBadgeNumbering()
    {
        var session = new AnnotationSession();
        session.PushBadge(10, 20); // #1
        session.Clear();

        var badge = session.PushBadge(30, 40);

        Assert.Equal(1, badge.Number);
    }

    [Fact]
    public void RecordEquality_RectangleAnnotation()
    {
        var a = new RectangleAnnotation(10, 20, 100, 50);
        var b = new RectangleAnnotation(10, 20, 100, 50);
        var c = new RectangleAnnotation(10, 20, 100, 60);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void RecordEquality_ArrowAnnotation()
    {
        var a = new ArrowAnnotation(0, 0, 100, 100);
        var b = new ArrowAnnotation(0, 0, 100, 100);
        var c = new ArrowAnnotation(0, 0, 100, 50);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void PenStrokeAnnotation_HoldsPoints()
    {
        var pts = new List<(double X, double Y)> { (0, 0), (10, 10), (20, 5) };
        var stroke = new PenStrokeAnnotation(pts);

        Assert.Equal(3, stroke.Points.Count);
        Assert.Equal((0, 0), stroke.Points[0]);
        Assert.Equal((10, 10), stroke.Points[1]);
        Assert.Equal((20, 5), stroke.Points[2]);
    }

    [Fact]
    public void HighlightStrokeAnnotation_HoldsPoints()
    {
        var pts = new List<(double X, double Y)> { (0, 0), (10, 10) };
        var stroke = new HighlightStrokeAnnotation(pts);

        Assert.Equal(2, stroke.Points.Count);
        Assert.Equal((0, 0), stroke.Points[0]);
        Assert.Equal((10, 10), stroke.Points[1]);
    }

    [Fact]
    public void PenStrokeAnnotation_SameReference_Equal()
    {
        var pts = new List<(double X, double Y)> { (0, 0), (10, 10) };
        var a = new PenStrokeAnnotation(pts);
        var b = new PenStrokeAnnotation(pts);

        Assert.Equal(a, b); // same reference for Points => equal
    }
}
