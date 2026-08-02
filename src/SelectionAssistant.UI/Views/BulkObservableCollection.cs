using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Observable collection that can replace its contents with one reset event.
/// Clipboard search renders an initial window of results; notifying once avoids
/// dozens of layout passes when a query completes.
/// </summary>
internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private const int MaximumIncrementalChanges = 16;

    public void ReplaceAll(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Search requests are cancellable, but two adjacent queries can still
        // produce the same visible slice (for example "war" -> "warp"). A
        // Reset notification would make Avalonia discard and recreate every
        // item container even though nothing on screen changed. Keep the
        // existing controls alive when the sequence is already identical.
        if (Items.Count == items.Count)
        {
            bool identical = true;
            for (int index = 0; index < items.Count; index++)
            {
                if (!EqualityComparer<T>.Default.Equals(Items[index], items[index]))
                {
                    identical = false;
                    break;
                }
            }

            if (identical)
            {
                return;
            }
        }

        // Backspacing commonly broadens a result set while preserving the
        // relative order of rows that were already visible. Reconcile a small
        // delta with insert/move/remove notifications so Avalonia keeps those
        // existing containers instead of rebuilding the whole list. Large
        // changes still use one Reset event to avoid dozens of layout passes.
        if (CountIncrementalChanges(items) <= MaximumIncrementalChanges)
        {
            ReconcileIncrementally(items);
            return;
        }

        CheckReentrancy();
        Items.Clear();
        foreach (T item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private int CountIncrementalChanges(IReadOnlyList<T> desired)
    {
        var simulated = Items.ToList();
        int changes = 0;
        for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            if (targetIndex < simulated.Count &&
                EqualityComparer<T>.Default.Equals(simulated[targetIndex], desired[targetIndex]))
            {
                continue;
            }

            int existingIndex = FindIndex(simulated, desired[targetIndex], targetIndex + 1);
            if (existingIndex >= 0)
            {
                T existing = simulated[existingIndex];
                simulated.RemoveAt(existingIndex);
                simulated.Insert(targetIndex, existing);
            }
            else
            {
                simulated.Insert(targetIndex, desired[targetIndex]);
            }

            if (++changes > MaximumIncrementalChanges)
            {
                return changes;
            }
        }

        changes += Math.Max(0, simulated.Count - desired.Count);
        return changes;
    }

    private void ReconcileIncrementally(IReadOnlyList<T> desired)
    {
        for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            if (targetIndex < Count &&
                EqualityComparer<T>.Default.Equals(this[targetIndex], desired[targetIndex]))
            {
                continue;
            }

            int existingIndex = FindIndex(Items, desired[targetIndex], targetIndex + 1);
            if (existingIndex >= 0)
            {
                Move(existingIndex, targetIndex);
            }
            else
            {
                Insert(targetIndex, desired[targetIndex]);
            }
        }

        while (Count > desired.Count)
        {
            RemoveAt(Count - 1);
        }
    }

    private static int FindIndex(IList<T> source, T item, int startIndex)
    {
        for (int index = Math.Max(0, startIndex); index < source.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(source[index], item))
            {
                return index;
            }
        }

        return -1;
    }
}
