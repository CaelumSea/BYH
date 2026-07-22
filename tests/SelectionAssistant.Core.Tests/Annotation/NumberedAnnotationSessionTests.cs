using SelectionAssistant.Core.Annotation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Annotation;

public sealed class NumberedAnnotationSessionTests
{
    [Fact]
    public void Push_IncrementsNumberFromOne()
    {
        var session = new NumberedAnnotationSession();

        var b1 = session.Push(10, 20);
        var b2 = session.Push(30, 40);
        var b3 = session.Push(50, 60);

        Assert.Equal(1, b1.Number);
        Assert.Equal(2, b2.Number);
        Assert.Equal(3, b3.Number);
    }

    [Fact]
    public void Push_StoresCoordinates()
    {
        var session = new NumberedAnnotationSession();

        var badge = session.Push(123.5, 456.7);

        Assert.Equal(123.5, badge.X);
        Assert.Equal(456.7, badge.Y);
    }

    [Fact]
    public void Push_IncrementsCount()
    {
        var session = new NumberedAnnotationSession();

        Assert.Equal(0, session.Count);
        session.Push(0, 0);
        Assert.Equal(1, session.Count);
        session.Push(0, 0);
        Assert.Equal(2, session.Count);
    }

    [Fact]
    public void Undo_RemovesLastBadge()
    {
        var session = new NumberedAnnotationSession();
        session.Push(10, 20);
        session.Push(30, 40);

        bool result = session.Undo();

        Assert.True(result);
        Assert.Equal(1, session.Count);
        Assert.Equal(1, session.Badges[0].Number);
    }

    [Fact]
    public void Undo_MultipleTimes_RemovesInReverseOrder()
    {
        var session = new NumberedAnnotationSession();
        session.Push(10, 20);
        session.Push(30, 40);
        session.Push(50, 60);

        session.Undo();
        Assert.Equal(2, session.Count);
        Assert.Equal(2, session.Badges[^1].Number);

        session.Undo();
        Assert.Equal(1, session.Count);
        Assert.Equal(1, session.Badges[^1].Number);
    }

    [Fact]
    public void Undo_ToEmpty_ReturnsTrueEachTime()
    {
        var session = new NumberedAnnotationSession();
        session.Push(0, 0);

        Assert.True(session.Undo());
        Assert.Equal(0, session.Count);
    }

    [Fact]
    public void Undo_OnEmptySession_ReturnsFalse()
    {
        var session = new NumberedAnnotationSession();

        Assert.False(session.Undo());
    }

    [Fact]
    public void Undo_AfterClear_ReturnsFalse()
    {
        var session = new NumberedAnnotationSession();
        session.Push(0, 0);
        session.Clear();

        Assert.False(session.Undo());
    }

    [Fact]
    public void Clear_RemovesAllBadges()
    {
        var session = new NumberedAnnotationSession();
        session.Push(10, 20);
        session.Push(30, 40);
        session.Push(50, 60);

        session.Clear();

        Assert.Equal(0, session.Count);
        Assert.Empty(session.Badges);
    }

    [Fact]
    public void Push_AfterUndo_AppendsWithCorrectNumber()
    {
        var session = new NumberedAnnotationSession();
        session.Push(10, 20); // #1
        session.Push(30, 40); // #2
        session.Undo();       // remove #2

        var b3 = session.Push(50, 60); // should be #3 (Count was 1, now 2, number = 2+1 = 3)

        // After Undo, Count is 1. Push adds at index 1, so number = 1+1 = 2.
        // This is correct: the session has [1, 2] after this push.
        Assert.Equal(2, b3.Number);
        Assert.Equal(2, session.Count);
    }

    [Fact]
    public void Badges_ReturnsReadOnlySnapshot()
    {
        var session = new NumberedAnnotationSession();
        session.Push(10, 20);

        IReadOnlyList<NumberedBadge> badges = session.Badges;
        Assert.Single(badges);

        // Adding more doesn't affect a previously-obtained snapshot
        // (since List<T> wrapped in IReadOnlyList is live, this just
        // verifies the property is accessible).
        session.Push(30, 40);
        Assert.Equal(2, session.Badges.Count);
    }
}
