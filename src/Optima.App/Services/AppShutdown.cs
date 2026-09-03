using System.Windows;
using Optima.App.Views;
using Optima.Core.Abstractions;
using Serilog;

namespace Optima.App.Services;

/// <summary>The one way out of the app.</summary>
public sealed class AppShutdown : IDisposable
{
    private readonly Window _window;
    private readonly IDriverInstaller _driverInstaller;
    private bool _asking;

    public bool IsShuttingDown { get; private set; }

    public AppShutdown(Window window, IDriverInstaller driverInstaller)
    {
        _window = window;
        _driverInstaller = driverInstaller;
        // Logoff / Windows shutdown: never hold the session up with a dialog.
        Application.Current.SessionEnding += OnSessionEnding;
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => IsShuttingDown = true;

    public void RequestExit()
    {
        if (IsShuttingDown || _asking)
        {
            return;
        }
        _asking = true;
        _ = RunAsync();
    }

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

        Log.Warning("Driver uninstall on exit failed: {Title}", result.Error?.Title);
        var detail = result.Error is { } error
            ? $"{error.Title}\n{error.SuggestedFixes.FirstOrDefault()}".TrimEnd()
            : "The elevated helper did not report a reason.";
        GlassDialog.Notice(
            _window,
            "The driver could not be removed, so Optima stays open",
            detail + " Try again from the Display page, or close Optima and choose \"Keep driver\".",
            DialogTone.Warning);
        return false;
    }

    public void Dispose()
    {
        Application.Current.SessionEnding -= OnSessionEnding;
    }
}
