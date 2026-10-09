using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Optima.App.Services;
using Optima.Core.Theming;

namespace Optima.App.Views;

public partial class DebugView : UserControl
{
    public DebugView()
    {
        InitializeComponent();
        foreach (UIElement body in ((Grid)Content).Children)
        {
            if (Grid.GetRow(body) == 2)
            {
                // Only on a switch of tab: when the whole page opens, the page brings its parts in.
                body.IsVisibleChanged += (sender, e) =>
                {
                    if (IsLoaded && (bool)e.NewValue)
                    {
                        Motion.Rise((UIElement)sender);
                    }
                };
            }
        }

        // The chip's Tag follows the view model, and a label can change width (the issue count):
        // the marker is placed after either has been laid out.
        void Later(bool glide) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PlaceTabMarker(glide));
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old)
            {
                old.PropertyChanged -= OnTabChanged;
            }
            if (e.NewValue is INotifyPropertyChanged next)
            {
                next.PropertyChanged += OnTabChanged;
            }
        };
        Loaded += (_, _) => Later(glide: false);
        TabStrip.SizeChanged += (_, _) => Later(glide: false);

        void OnTabChanged(object? sender, PropertyChangedEventArgs e) => Later(glide: true);
    }

    /// <summary>Sends the line under the tabs to the chosen one.</summary>
    private void PlaceTabMarker(bool glide)
    {
        foreach (FrameworkElement chip in TabStrip.Children)
        {
            if (chip.Tag is true)
            {
                var length = glide ? Motion.Duration(MotionSpec.MoveMs) : TimeSpan.Zero;
                var left = chip.TransformToAncestor(TabStrip).Transform(new Point(0, 0)).X;
                TabMarkerShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(left, length) { EasingFunction = Motion.Ease });
                TabMarker.BeginAnimation(WidthProperty, new DoubleAnimation(chip.ActualWidth, length) { EasingFunction = Motion.Ease });
                return;
            }
        }
    }
}