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
}
