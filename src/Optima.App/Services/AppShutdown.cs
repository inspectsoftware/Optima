using System.Windows;
using Optima.App.Views;
using Optima.Core.Abstractions;
using Serilog;

namespace Optima.App.Services;

/// <summary>
/// The one way out of the app. Closing the window (without keep-in-tray) and EXIT in the
/// tray menu both land in <see cref="RequestExit"/>, which warns that the virtual display
/// driver stays installed after Optima quits and lets the user keep it, remove it, or stay.
/// Crashes (<see cref="ShutdownNow"/>) and Windows logoff skip the question.
/// </summary>
public sealed class AppShutdown : IDisposable
{
    private readonly Window _window;
    private readonly IDriverInstaller _driverInstaller;
    private bool _asking;

    /// <summary>True once the exit is decided; window Closing handlers must let the close through.</summary>
    public bool IsShuttingDown { get; private set; }

    public AppShutdown(Window window, IDriverInstaller driverInstaller)
    {
        _window = window;
        _driverInstaller = driverInstaller;
        // Logoff / Windows shutdown: never hold the session up with a dialog.
        Application.Current.SessionEnding += OnSessionEnding;
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => IsShuttingDown = true;

    /// <summary>Asks about the driver if it is installed, then shuts the application down.</summary>
    public void RequestExit()
    {
        if (IsShuttingDown || _asking)
        {
            return;
        }
        _asking = true;
        _ = RunAsync();
    }

    /// <summary>Exit without the driver question; used by the crash handler.</summary>
    public void ShutdownNow(int exitCode = 0)
    {
        IsShuttingDown = true;
        Application.Current.Shutdown(exitCode);
    }

    private async Task RunAsync()
    {
        bool proceed;
        try
        {
            proceed = await ConfirmDriverAsync();
        }
        catch (Exception ex)
        {
            // A broken prompt must never trap the user inside the app.
            Log.Error(ex, "Exit prompt failed; closing anyway");
            proceed = true;
        }
        finally
        {
            _asking = false;
        }

        if (proceed)
        {
            ShutdownNow();
        }
    }

    private async Task<bool> ConfirmDriverAsync()
    {
        DriverState state;
        try
        {
            state = await _driverInstaller.GetStateAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not check the virtual display driver before exit");
            return true;
        }
        if (state != DriverState.Installed)
        {
            return true;
        }

        var choice = DriverExitDialog.Ask(_window);
        Log.Information("Exit with the driver still installed: user chose {Choice}", choice);
        switch (choice)
        {
            case DriverExitChoice.Cancel:
                return false;
            case DriverExitChoice.Keep:
                return true;
        }

        var result = await _driverInstaller.UninstallAsync();
        if (result.Success)
        {
            Log.Information("Virtual display driver removed on exit");
            return true;
        }

        // The user asked for a removal that did not happen: say so and stay open rather than
        // quietly leaving the driver behind.
        Log.Warning("Driver uninstall on exit failed: {Title}", result.Error?.Title);
        var detail = result.Error is { } error
            ? $"{error.Title}\n{error.SuggestedFixes.FirstOrDefault()}".TrimEnd()
            : "The elevated helper did not report a reason.";
        var text = "The virtual display driver could not be removed, so Optima stays open.\n\n" +
                   detail + "\n\n" +
                   "Try again from the Display page, or close Optima and choose \"Keep driver\".";
        if (_window.IsVisible)
        {
            MessageBox.Show(_window, text, "Optima", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show(text, "Optima", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return false;
    }

    public void Dispose()
    {
        Application.Current.SessionEnding -= OnSessionEnding;
    }
}
