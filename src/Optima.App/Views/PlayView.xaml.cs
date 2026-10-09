using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Optima.App.Controls;
using Optima.App.Services;
using Optima.App.ViewModels;
using Optima.Core.Theming;

namespace Optima.App.Views;

public partial class PlayView : UserControl
{
    public PlayView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            foreach (var step in (e.OldValue as PlayViewModel)?.Steps ?? [])
            {
                step.PropertyChanged -= OnStepChanged;
            }
            foreach (var step in (e.NewValue as PlayViewModel)?.Steps ?? [])
            {
                step.PropertyChanged += OnStepChanged;
            }
        };
        Loaded += (_, _) => PlaceStepMarker();
    }

    private void OnStepChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LaunchStep.State))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceStepMarker);
        }
    }

    /// <summary>Sends the marker to the step that is running, and hides it when none is.</summary>
    private void PlaceStepMarker()
    {
        var live = (DataContext as PlayViewModel)?.Steps.FirstOrDefault(step => step.State == StepState.Live);
        var row = live is null ? null : StepList.ItemContainerGenerator.ContainerFromItem(live) as FrameworkElement;
        Flow.SetShow(StepMarker, row is not null);
        if (row is null || !row.IsVisible)
        {
            return;
        }
        // The first time it appears it is simply there; after that it travels.
        var length = StepMarker.Opacity > 0 ? Motion.Duration(MotionSpec.MoveMs) : TimeSpan.Zero;
        var top = row.TransformToAncestor(StepList).Transform(new Point(0, 0)).Y + 1;
        StepMarkerShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(top, length) { EasingFunction = Motion.Ease });
    }
}