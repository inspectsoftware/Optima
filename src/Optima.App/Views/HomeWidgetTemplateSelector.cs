using System.Windows;
using System.Windows.Controls;
using Optima.App.ViewModels;

namespace Optima.App.Views;

/// <summary>
/// Picks the DataTemplate for a widget by id. A template per id (rather than one template switching
/// on the widget) is what keeps each card's markup exactly as it was on its own page, and it means a
/// widget whose template is missing renders as nothing instead of throwing while scrolling.
/// </summary>
public sealed class HomeWidgetTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not HomeWidgetItem widget || container is not FrameworkElement element)
        {
            return null;
        }

        return element.TryFindResource("Widget." + widget.Definition.Id) as DataTemplate;
    }
}
