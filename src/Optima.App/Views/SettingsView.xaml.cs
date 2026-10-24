using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
                    var match = SettingsSearch.Matches(query, (string)row.Header, row.Hint);
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
