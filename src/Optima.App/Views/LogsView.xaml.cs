using System.Collections.Specialized;
using System.Windows.Controls;
using Optima.App.ViewModels;

namespace Optima.App.Views;

public partial class LogsView : UserControl
{
    private INotifyCollectionChanged? _observed;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) =>
        {
            Attach();
            ScrollToEnd();
        };
    }

    private LogsViewModel? ViewModel => DataContext as LogsViewModel;

    private void Attach()
    {
        Detach();
        if (ViewModel?.Entries is INotifyCollectionChanged collection)
        {
            _observed = collection;
            collection.CollectionChanged += OnEntriesChanged;
        }
    }

    private void Detach()
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnEntriesChanged;
            _observed = null;
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && ViewModel?.TailEnabled == true)
        {
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count == 0)
        {
            return;
        }
        // Defer out of the collection-changed notification: calling ScrollIntoView while the
        // virtualizing panel is still processing the add/remove batch races its internal index
        // bookkeeping and has thrown ArgumentOutOfRangeException from BringContainerIntoView.
        // Loaded priority also coalesces the flood of entries that arrives when a page opens.
        LogList.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            try
            {
                if (LogList.Items.Count > 0)
                {
                    LogList.ScrollIntoView(LogList.Items[^1]);
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // The collection changed again between scheduling and running; the next
                // arriving entry schedules another scroll, so nothing is lost.
            }
        });
    }
}
