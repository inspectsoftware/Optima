using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Optima.App.Controls;
using Optima.App.Services;
using Optima.App.ViewModels;
using Optima.Core.Configuration;

namespace Optima.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _page;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPageChanged;
        }
        _page = DataContext as SettingsViewModel;
        if (_page is not null)
        {
            _page.PropertyChanged += OnPageChanged;
            ShowSections(rise: false);
        }
    }

    /// <summary>
    /// Ctrl+click and Ctrl+Space toggle an item of a single-select list, so on the chosen segment or
    /// the open section they cleared the choice and nothing was marked. These lists always have one
    /// chosen, so the toggle is dropped there; the view model refuses an empty choice as well.
    /// </summary>
    private void KeepChoice(object sender, InputEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && e is MouseButtonEventArgs or KeyEventArgs { Key: Key.Space }
            && ItemsControl.ContainerFromElement((ListBox)sender, (DependencyObject)e.OriginalSource) is ListBoxItem { IsSelected: true })
        {
            e.Handled = true;
        }
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SearchText) or nameof(SettingsViewModel.SelectedSection))
        {
            ShowSections(rise: e.PropertyName == nameof(SettingsViewModel.SelectedSection) && _page?.SelectedSection is not null);
        }
    }

    /// <summary>
    /// The selected section, or with words in the find box every row they match, wherever it is.
    /// The rows are read from the page itself, so a row added to the markup is found and counted
    /// without a list of rows kept anywhere else. The counts go to the list down the left.
    /// </summary>
    private void ShowSections(bool rise)
    {
        if (_page is null)
        {
            return;
        }

        var query = _page.SearchText;
        var searching = query.Trim().Length > 0;
        var anything = false;
        foreach (var page in SectionPages.Children.OfType<StackPanel>())
        {
            var section = _page.Sections.First(s => s.Key == (string)page.Tag);
            var count = 0;
            foreach (var card in page.Children.OfType<HeaderedContentControl>())
            {
                var shown = 0;
                foreach (var row in ((Panel)card.Content).Children.OfType<SettingRow>())
                {
                    // The card and section names too: "discord" or "watchdog" names a whole section, and no row says it.
                    var match = SettingsSearch.Matches(query, (string)row.Header, row.Hint, row.Info, (string)card.Header, section.Label);
                    row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    shown += match ? 1 : 0;
                }
                card.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
                count += shown;
            }
            section.Count = count;

            var visible = searching ? count > 0 : ReferenceEquals(section, _page.SelectedSection);
            anything |= visible;
            page.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible && rise)
            {
                var order = 0;
                foreach (var part in page.Children.OfType<UIElement>())
                {
                    Motion.Rise(part, order++);
                }
            }
        }
        NoMatches.Visibility = anything ? Visibility.Collapsed : Visibility.Visible;
    }
}
