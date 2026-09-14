using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace GeneralMaintenanceManager.App.Collections;

internal sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void AppendAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var added = false;
        foreach (var item in items)
        {
            Items.Add(item);
            added = true;
        }
        if (!added) return;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
