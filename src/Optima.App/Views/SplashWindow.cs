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
    /// The handover, first half. Waits until the intro has played in full, then takes in the main
    /// window's bounds (one resize, not one per frame) and grows the card over them. Completes once
    /// the card covers them: the window can be shown underneath from then on.
    /// </summary>
    public async Task GrowIntoAsync(Rect target)
    {
        var wait = _surface.IntroRemainingMs;
        if (wait > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(wait)).ConfigureAwait(true);
        }

        var card = new Rect(Left, Top, ActualWidth, ActualHeight);
        var cover = Rect.Union(card, target);
        Left = cover.Left;
        Top = cover.Top;
        Width = cover.Width;
        Height = cover.Height;
        await _surface.GrowAsync(
            new Rect(card.Left - cover.Left, card.Top - cover.Top, card.Width, card.Height),
            new Rect(target.Left - cover.Left, target.Top - cover.Top, target.Width, target.Height)).ConfigureAwait(true);
    }

    /// <summary>The handover, second half: fades off the main window, which is drawn underneath by now, and closes.</summary>
    public async Task RevealAsync()
    {
        await _surface.RevealAsync().ConfigureAwait(true);
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
