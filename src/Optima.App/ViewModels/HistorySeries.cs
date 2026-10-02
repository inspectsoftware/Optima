using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Optima.App.ViewModels;

/// <summary>
/// A fixed-length strip of samples behind a sparkline. Feeding one used to be Add plus RemoveAt(0),
/// two collection changes per series per sample, each of which made the chart repaint; this is one
/// change per sample, and it publishes only the two property names a chart reads.
/// </summary>
public sealed class HistorySeries : ObservableCollection<double>
{
    private readonly int _capacity;

    public HistorySeries(int capacity) => _capacity = Math.Max(2, capacity);

    public void Append(double value)
    {
        if (Count >= _capacity)
        {
            Items.RemoveAt(0);
        }
        Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
