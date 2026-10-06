using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Optima.App.ViewModels;

namespace Optima.App.Views;

/// <summary>
/// HOME's widget board. The view owns the drag and drop because it is a gesture, not state: the view
/// model only ever hears "move this widget to position n", the same call the edit panel's buttons
/// make, so the two paths cannot drift apart.
/// </summary>
public partial class HomeView : UserControl
{
    /// <summary>The drag payload: a widget id, which is also what a saved layout stores.</summary>
    private const string WidgetFormat = "Optima.HomeWidget";

    private HomeWidgetItem? _pressed;
    private Point _dragOrigin;

    public HomeView() => InitializeComponent();

    private HomeViewModel? Model => DataContext as HomeViewModel;

    private void OnWidgetsMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = InteractiveAncestor(e.OriginalSource as DependencyObject)
            ? null
            : WidgetAt(WidgetHost, e.GetPosition(WidgetHost));
        _dragOrigin = e.GetPosition(WidgetHost);
    }

    private void OnWidgetsMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is null || e.LeftButton != MouseButtonState.Pressed || Model is not { IsEditingWidgets: true })
        {
            return;
        }

        var position = e.GetPosition(WidgetHost);
        if (!MovedPastThreshold(position, _dragOrigin))
        {
            return;
        }

        var item = _pressed;
        _pressed = null;
        DragDrop.DoDragDrop(WidgetHost, new DataObject(WidgetFormat, item.Definition.Id), DragDropEffects.Move);
    }

    private void OnWidgetsDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(WidgetFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWidgetsDrop(object sender, DragEventArgs e)
    {
        if (Model is not { IsEditingWidgets: true } model || e.Data.GetData(WidgetFormat) is not string id)
        {
            return;
        }

        model.MoveOrAdd(id, DropIndex(e.GetPosition(WidgetHost)));
        e.Handled = true;
    }

    private void OnPaletteMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = InteractiveAncestor(e.OriginalSource as DependencyObject)
            ? null
            : WidgetAt(WidgetPalette, e.GetPosition(WidgetPalette));
        _dragOrigin = e.GetPosition(WidgetPalette);
    }

    private void OnPaletteMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is null || Model is not { IsEditingWidgets: true } || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (!MovedPastThreshold(e.GetPosition(WidgetPalette), _dragOrigin))
        {
            return;
        }

        var item = _pressed;
        _pressed = null;
        DragDrop.DoDragDrop(WidgetPalette, new DataObject(WidgetFormat, item.Definition.Id), DragDropEffects.Move);
    }

    /// <summary>Dropping a card back onto the panel is the drag equivalent of remove.</summary>
    private void OnPaletteDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(WidgetFormat) && Model is { IsEditingWidgets: true }
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPaletteDrop(object sender, DragEventArgs e)
    {
        if (Model is not { IsEditingWidgets: true } model || e.Data.GetData(WidgetFormat) is not string id)
        {
            return;
        }

        model.RemoveFromHome(id);
        e.Handled = true;
    }

    /// <summary>
    /// The position a drop lands at: before the first card whose middle the pointer has not passed,
    /// so dropping into a gap or above a card inserts there.
    /// </summary>
    private int DropIndex(Point position)
    {
        for (var i = 0; i < WidgetHost.Items.Count; i++)
        {
            if (WidgetHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            {
                continue;
            }

            var top = container.TranslatePoint(new Point(0, 0), WidgetHost).Y;
            if (position.Y < top + container.ActualHeight / 2)
            {
                return i;
            }
        }
        return WidgetHost.Items.Count;
    }

    /// <summary>The widget whose card is under a point, or null when the point is between cards.</summary>
    private static HomeWidgetItem? WidgetAt(ItemsControl host, Point position)
    {
        var hit = host.InputHitTest(position) as DependencyObject;
        while (hit is not null)
        {
            if (hit is ContentPresenter { DataContext: HomeWidgetItem item })
            {
                return item;
            }
            hit = VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    /// <summary>
    /// A press that starts on a button, text box or any other control that owns the mouse must not
    /// start a drag: the cards are full of refresh buttons and profile chips.
    /// </summary>
    private static bool InteractiveAncestor(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ButtonBase or TextBoxBase or ComboBox or ListBox or ScrollBar)
            {
                return true;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private static bool MovedPastThreshold(Point position, Point origin)
        => Math.Abs(position.X - origin.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(position.Y - origin.Y) >= SystemParameters.MinimumVerticalDragDistance;
}
