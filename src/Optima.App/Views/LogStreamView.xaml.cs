using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using Optima.App.Logging;
using Optima.App.ViewModels;

namespace Optima.App.Views;

public partial class LogStreamView : UserControl
{
    private LogStreamViewModel? _observed;

    public LogStreamView()
    {
        InitializeComponent();
        // The detail takes a share of the height, not a fixed slice: the floating console is a
        // third as tall as the page, and the list must stay readable in both.
        SizeChanged += (_, e) => DetailScroll.MaxHeight = Math.Max(90, e.NewSize.Height * 0.4);
        DataContextChanged += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) =>
        {
            Attach();
            ScrollToEnd();
        };
    }

    private LogStreamViewModel? ViewModel => DataContext as LogStreamViewModel;

    private void Attach()
    {
        Detach();
        if (ViewModel is { } viewModel)
        {
            _observed = viewModel;
            viewModel.Entries.CollectionChanged += OnEntriesChanged;
            viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    private void Detach()
    {
        if (_observed is not null)
        {
            _observed.Entries.CollectionChanged -= OnEntriesChanged;
            _observed.PropertyChanged -= OnViewModelChanged;
            _observed = null;
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // An open detail pauses the tail: the list must not walk away from the line being read.
        if (e.Action == NotifyCollectionChangedAction.Add && ViewModel is { TailEnabled: true, Selected: null })
        {
            ScrollToEnd();
        }
    }

    /// <summary>
    /// Only a row someone clicked opens the detail. The list's own selection is not bound to the
    /// view model, because a trim resets the list and takes the selection with it, and that would
    /// shut the detail in the middle of a read.
    /// </summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is LogEntry entry && ViewModel is { } viewModel)
        {
            viewModel.Selected = entry;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Closing the detail lets go of the row too, so the same line can be opened again.
        if (e.PropertyName == nameof(LogStreamViewModel.Selected) && ViewModel?.Selected is null)
        {
            LogList.SelectedItem = null;
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
