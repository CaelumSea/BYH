namespace SelectionAssistant.Core.Annotation;

/// <summary>
/// R48: unified annotation session with a mixed undo stack.
/// Holds both R47 numbered badges and R48 shapes as <see cref="IAnnotationItem"/>.
/// Ctrl+Z pops the most recent item regardless of type.
/// </summary>
/// <remarks>
/// Replaces R47's <see cref="NumberedAnnotationSession"/> in the runtime.
/// The old class is kept untouched for backward compatibility (existing tests).
/// </remarks>
public sealed class AnnotationSession
{
    private readonly List<IAnnotationItem> _items = new();
    private int _nextBadgeNumber = 1;

    /// <summary>Snapshot of all items in placement order.</summary>
    public IReadOnlyList<IAnnotationItem> Items => _items;

    /// <summary>Current item count.</summary>
    public int Count => _items.Count;

    /// <summary>
    /// Appends a numbered badge at the given DIP coordinates.
    /// Badge numbers auto-increment from 1 across the session lifetime
    /// (undo + re-push continues the sequence, matching R47 behavior).
    /// </summary>
    public NumberedBadgeAnnotation PushBadge(double x, double y)
    {
        var badge = new NumberedBadgeAnnotation(_nextBadgeNumber++, x, y);
        _items.Add(badge);
        return badge;
    }

    /// <summary>
    /// Appends any annotation item (shape) to the undo stack.
    /// </summary>
    public void Push(IAnnotationItem item)
    {
        _items.Add(item);
    }

    /// <summary>
    /// Removes the most recently added item.
    /// Returns the removed item, or null if the session is empty.
    /// </summary>
    public IAnnotationItem? Undo()
    {
        if (_items.Count == 0)
        {
            return null;
        }

        IAnnotationItem last = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        return last;
    }

    /// <summary>Clears all items (used on session dismiss).</summary>
    public void Clear()
    {
        _items.Clear();
        _nextBadgeNumber = 1;
    }
}
