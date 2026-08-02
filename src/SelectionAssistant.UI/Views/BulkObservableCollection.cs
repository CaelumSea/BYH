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
