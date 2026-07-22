namespace SelectionAssistant.Core.Annotation;

/// <summary>
/// R47: manages the ordered list of numbered badges for an annotation session.
/// Push appends with auto-incrementing number; Undo removes the most recent.
/// Designed for R48 extensibility — the undo stack abstraction is reusable
/// for other annotation tools (rectangle, ellipse, arrow, pen).
/// </summary>
public sealed class NumberedAnnotationSession
{
    private readonly List<NumberedBadge> _badges = new();

    /// <summary>Snapshot of all badges in placement order.</summary>
    public IReadOnlyList<NumberedBadge> Badges => _badges;

    /// <summary>Current badge count.</summary>
    public int Count => _badges.Count;

    /// <summary>
    /// Appends a new badge at the given overlay DIP coordinates.
    /// Number auto-increments from 1.
    /// </summary>
    public NumberedBadge Push(double x, double y)
    {
        var badge = new NumberedBadge(_badges.Count + 1, x, y);
        _badges.Add(badge);
        return badge;
    }

    /// <summary>
    /// Removes the most recently placed badge.
    /// Returns false if the session is already empty.
    /// </summary>
    public bool Undo()
    {
        if (_badges.Count == 0)
        {
            return false;
        }

        _badges.RemoveAt(_badges.Count - 1);
        return true;
    }

    /// <summary>Clears all badges (used on session dismiss).</summary>
    public void Clear() => _badges.Clear();
}
