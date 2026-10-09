using Optima.Core.Abstractions;
using Optima.Core.Models;

namespace Optima.Core.Launch;

public enum DisplayCheckState
{
    /// <summary>Not got to: the check has not run, or an earlier step failed.</summary>
    NotReached,
    Running,
    Passed,
    Failed,
    /// <summary>Not part of this run (the main-screen step with the switch off).</summary>
    Skipped,
}

/// <summary>The one thing the page offers to do about a failed step.</summary>
public enum DisplayCheckFix
{
    None,
    InstallDriver,
    RestartWindows,
    ReloadDriver,
    OpenGuide,
}

/// <summary>One line of "Check my setup".</summary>
public sealed record DisplayCheckStep(
    string Id,
    string Title,
    DisplayCheckState State = DisplayCheckState.NotReached,
    string Detail = "",
    DisplayCheckFix Fix = DisplayCheckFix.None,
    string? ErrorCode = null);

/// <summary>
/// The virtual display, tried for real and put back: the same steps a launch takes, one at a time,
/// each with its own verdict. A launch that fails says "the display did not appear" somewhere in a
/// session; this says it on the Display page, next to the button that deals with it, before any
/// game is involved.
///
/// It changes the display for as long as it runs. Whatever happens, the layout that was there
/// before is put back at the end, and that is a step of its own with its own verdict.
/// </summary>
public sealed class DisplaySetupCheck
{
    public const string Driver = "driver";
    public const string Helper = "helper";
    public const string Appears = "appears";
    public const string Mode = "mode";
    public const string PutBack = "restore";

    private readonly IDriverInstaller _installer;
    private readonly IVirtualDisplayProvider _provider;
    private readonly IDisplayService _displays;
    private readonly Func<bool> _helperPresent;
    private readonly Func<bool> _restartPending;

    public DisplaySetupCheck(
        IDriverInstaller installer,
        IVirtualDisplayProvider provider,
        IDisplayService displays,
        Func<bool> helperPresent,
        Func<bool> restartPending)
    {
        _installer = installer;
        _provider = provider;
        _displays = displays;
        _helperPresent = helperPresent;
        _restartPending = restartPending;
    }

    /// <summary>The steps as they stand before a run.</summary>
    public static IReadOnlyList<DisplayCheckStep> Blank { get; } =
    [
        new(Driver, "The driver is installed"),
        new(Helper, "Optima's helper is in place"),
        new(Appears, "The virtual display turns on"),
        new(Mode, "The resolution applies"),
        new(PutBack, "Everything is put back"),
    ];

    /// <summary>
    /// Runs the steps in order and stops at the first that fails; what comes after it stays "not
    /// reached". The display is held on for <paramref name="hold"/> once it is up, so that it can
    /// be looked at, and then everything is put back.
    /// </summary>
    /// <param name="onStep">Called with the whole list each time a step changes, on the caller's context.</param>
    /// <param name="onHold">Called once a second while the display is held, with the seconds left.</param>
    public async Task<IReadOnlyList<DisplayCheckStep>> RunAsync(
        DisplayMode mode,
        TimeSpan hold,
        Action<IReadOnlyList<DisplayCheckStep>>? onStep = null,
        Action<int>? onHold = null,
        CancellationToken ct = default)
    {
        // No ConfigureAwait(false) in here, on purpose: the callbacks update a page, and they are
        // promised the context this was called on.
        var steps = Blank.ToList();
        void Set(string id, DisplayCheckState state, string detail = "", DisplayCheckFix fix = DisplayCheckFix.None, string? code = null)
        {
            var index = steps.FindIndex(s => s.Id == id);
            steps[index] = steps[index] with { State = state, Detail = detail, Fix = fix, ErrorCode = code };
            onStep?.Invoke([.. steps]);
        }

        // 1. The driver.
        Set(Driver, DisplayCheckState.Running);
        var driver = await _installer.GetStateAsync(ct);
        if (driver != DriverState.Installed)
        {
            Set(Driver, DisplayCheckState.Failed,
                driver == DriverState.NotInstalledPackageAvailable
                    ? "The virtual display driver is not on this PC yet."
                    : "The driver is not installed, and this build of Optima has no driver package to install it from.",
                driver == DriverState.NotInstalledPackageAvailable ? DisplayCheckFix.InstallDriver : DisplayCheckFix.OpenGuide,
                "VDD_NOT_INSTALLED");
            return steps;
        }
        if (_restartPending())
        {
            Set(Driver, DisplayCheckState.Failed,
                "The driver is installed, but Windows asked for a restart to finish and has not been restarted since.",
                DisplayCheckFix.RestartWindows);
            return steps;
        }
        Set(Driver, DisplayCheckState.Passed);

        // 2. The helper, which is what switches the device on and off.
        if (!_helperPresent())
        {
            Set(Helper, DisplayCheckState.Failed,
                "Optima.Watchdog.exe is missing from Optima's folder. Security software may have removed it; installing Optima again puts it back.",
                DisplayCheckFix.OpenGuide, "HELPER_MISSING");
            return steps;
        }
        Set(Helper, DisplayCheckState.Passed);

        // The next two change the display. From here on, whatever happens, the last step runs.
        // The virtual display is switched on beside the player's screens and never made the main
        // one: the game opens on the main screen, and that has to stay one they can see.
        string? topology = null;
        var enabledHere = false;
        try
        {
            Set(Appears, DisplayCheckState.Running, "Windows may ask for administrator approval.");
            topology = await _displays.CaptureTopologyAsync(ct);
            await _provider.InitializeAsync(ct);
            if (!await _provider.IsDisplayActiveAsync(ct))
            {
                await _provider.EnableDisplayAsync(ct);
                enabledHere = true;
            }
            Set(Appears, DisplayCheckState.Passed);

            Set(Mode, DisplayCheckState.Running);
            await _provider.SetModeAsync(mode, ct);
            var actual = await _provider.GetCurrentModeAsync(ct);
            Set(Mode, DisplayCheckState.Passed, actual is { } applied ? applied.ToString() : mode.ToString());

            for (var left = (int)Math.Ceiling(hold.TotalSeconds); left > 0; left--)
            {
                onHold?.Invoke(left);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            onHold?.Invoke(0);
        }
        catch (OptimaException ex)
        {
            var failed = steps.FirstOrDefault(s => s.State == DisplayCheckState.Running)?.Id ?? Appears;
            Set(failed, DisplayCheckState.Failed,
                ex.Error.Title + (ex.Error.Explanation.Length > 0 ? " " + ex.Error.Explanation : string.Empty),
                FixFor(ex.Error.Code), ex.Error.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = steps.FirstOrDefault(s => s.State == DisplayCheckState.Running)?.Id ?? Appears;
            Set(failed, DisplayCheckState.Failed, ex.Message, DisplayCheckFix.OpenGuide);
        }
        finally
        {
            // Not on the caller's token: a check that was cancelled still has to put the screens back.
            if (topology is not null)
            {
                Set(PutBack, DisplayCheckState.Running);
                try
                {
                    await _displays.RestoreTopologyAsync(topology, CancellationToken.None);
                    await _provider.RestoreOriginalStateAsync(CancellationToken.None);
                    if (enabledHere)
                    {
                        await _provider.DisableDisplayAsync(CancellationToken.None);
                    }
                    Set(PutBack, DisplayCheckState.Passed);
                }
                catch (Exception ex)
                {
                    Set(PutBack, DisplayCheckState.Failed,
                        "The screens could not be put back the way they were: " + ((ex as OptimaException)?.Error.Title ?? ex.Message),
                        DisplayCheckFix.OpenGuide, (ex as OptimaException)?.Error.Code ?? "DISPLAY_RESTORE_FAILED");
                }
            }
        }
        return steps;
    }

    /// <summary>What can be done on the spot for an error; everything else is a page of the guide.</summary>
    private static DisplayCheckFix FixFor(string code) => code switch
    {
        "VDD_NOT_INSTALLED" => DisplayCheckFix.InstallDriver,
        "VDD_NO_DISPLAY" or "VDD_PIPE_FAILED" => DisplayCheckFix.ReloadDriver,
        // Declined is the player's answer; there is nothing to repair, only to ask again.
        "ELEVATION_DECLINED" => DisplayCheckFix.None,
        _ => DisplayCheckFix.OpenGuide,
    };
}
