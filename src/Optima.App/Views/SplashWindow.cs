using System.Windows;
using System.Windows.Media;
using Optima.App.Controls;

namespace Optima.App.Views;

/// <summary>
/// The launch splash window. It is created and run by <see cref="Services.SplashHost"/> on a thread
/// of its own, so the animation keeps playing while the main thread builds the app. Built in code,
/// not XAML, because nothing on this thread may reach into the application's resources.
/// </summary>
public sealed class SplashWindow : Window
{
    private readonly SplashSurface _surface;
    private bool _closing;

    public SplashWindow(bool animate, string version)
    {
        Width = SplashSurface.CardWidth;
        Height = SplashSurface.CardHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        Title = "Optima";
        _surface = new SplashSurface(animate, version);
        Content = _surface;
    }

    public void SetStatus(string text, double fraction) => _surface.SetStatus(text, fraction);

    public void SetAccent(Color accent) => _surface.SetAccent(accent);

    /// <summary>
    /// The handover. Waits until the intro has played in full, then covers the main window's bounds
    /// (one resize, not one per frame) and lets the centre square open onto the app underneath.
    /// </summary>
    public async Task OpenIntoAsync(Rect target)
    {
        var wait = _surface.IntroRemainingMs;
        if (wait > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(wait)).ConfigureAwait(true);
        }

        var card = new Rect(Left, Top, ActualWidth, ActualHeight);
        var cover = Rect.Union(card, target);
        if (cover.Width > 0 && cover.Height > 0)
        {
            Left = cover.Left;
            Top = cover.Top;
            Width = cover.Width;
            Height = cover.Height;
            await _surface.OpenAsync(
                new Rect(card.Left - cover.Left, card.Top - cover.Top, card.Width, card.Height),
                new Rect(target.Left - cover.Left, target.Top - cover.Top, target.Width, target.Height)).ConfigureAwait(true);
        }
        CloseNow();
    }

    public void CloseNow()
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        Close();
    }
}
