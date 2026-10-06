using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.App.Services;
using Optima.Core.Abstractions;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>Outcome of the PC scan for one tutorial step.</summary>
public enum PlayGuideCheckState
{
    Pending,
    Running,
    Pass,
    Warn,
    Fail,
}

/// <summary>One readiness row inside a tutorial step: what the scan checked and what it found.</summary>
public sealed partial class PlayGuideCheckRow : ObservableObject
{
    public PlayGuideCheckRow(string title, string detail, PlayGuideCheckState state)
    {
        Title = title;
        _detail = detail;
        _state = state;
    }

    public string Title { get; }

    [ObservableProperty]
    private string _detail;

    [ObservableProperty]
    private PlayGuideCheckState _state;

    /// <summary>The fix text from the underlying diagnostic check, shown when a check did not pass.</summary>
    [ObservableProperty]
    private string _fix = string.Empty;
}

/// <summary>One tutorial step (the wizard page).</summary>
public sealed partial class PlayGuideStep : ObservableObject
{
    public PlayGuideStep(int index, string number, string title, string subtitle, string body, string actionLabel)
    {
        Index = index;
        Number = number;
        Title = title;
        Subtitle = subtitle;
        Body = body;
        ActionLabel = actionLabel;
    }

    public int Index { get; }
    public string Number { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Body { get; }
    public string ActionLabel { get; }

    public ObservableCollection<PlayGuideCheckRow> Checks { get; } = [];

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private PlayGuideCheckState _state = PlayGuideCheckState.Pending;

    partial void OnStateChanged(PlayGuideCheckState value) => OnPropertyChanged(nameof(StateText));

    public string ProgressText => $"STEP {Index + 1} / 5";

    /// <summary>Badge text for the step's scan state.</summary>
    public string StateText => State switch
    {
        PlayGuideCheckState.Pass => "OK",
        PlayGuideCheckState.Warn => "CHECK",
        PlayGuideCheckState.Fail => "FIX",
        PlayGuideCheckState.Running => "…",
        _ => "—",
    };

    /// <summary>False when the step is a manual (BIOS) step with no in-app action button.</summary>
    public bool HasAction => Index > 0;
}

/// <summary>
/// The Critical Ops on PC setup guide, opened from the Play page: a five-step visualizer following the official
/// path (BIOS virtualization → Windows hypervisor → beta program → install → play), each step carrying a live PC
/// scan built on the Diagnostics checks, and the automatable fixes running through the same elevation broker as
/// the first-run wizard.
/// </summary>
public sealed partial class PlayGuideViewModel : ObservableObject
{
    private const string BetaOptInUrl = "https://play.google.com/apps/testing/com.criticalforceentertainment.criticalops";
    private const string GpgDownloadPage = "https://play.google.com/googleplaygames";

    private readonly ISystemInfoService _systemInfo;
    private readonly IGameDetector _detector;
    private readonly FirstRunFixService _fix;
    private readonly ILogger<PlayGuideViewModel> _logger;

    /// <summary>Set when the user opens the beta opt-in page from step 3; survives rescans, not app restarts.</summary>
    private bool _betaConfirmed;

    public PlayGuideViewModel(
        ISystemInfoService systemInfo,
        IGameDetector detector,
        FirstRunFixService fix,
        ILogger<PlayGuideViewModel> logger)
    {
        _systemInfo = systemInfo;
        _detector = detector;
        _fix = fix;
        _logger = logger;
        BuildSteps();
        foreach (var step in Steps)
        {
            step.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PlayGuideStep.State))
                {
                    OnPropertyChanged(nameof(GuideProgress));
                    OnPropertyChanged(nameof(GuideProgressText));
                    OnPropertyChanged(nameof(OverallStateText));
                }
            };
        }
    }

    public ObservableCollection<PlayGuideStep> Steps { get; } = [];

    [ObservableProperty]
    private bool _isScanning;

    /// <summary>True while an automatable fix (e.g. enabling hypervisor features) is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FixInFlightText))]
    private bool _isFixing;

    /// <summary>Fix progress 0-100 while a fix runs; sits at 0 until the first real percentage arrives.</summary>
    [ObservableProperty]
    private double _fixProgress;

    [ObservableProperty]
    private string _scanStatus = "Run the scan to see what this PC is missing.";

    [ObservableProperty]
    private string _overallAdvice = string.Empty;

    [ObservableProperty]
    private string _actionStatus = string.Empty;

    [ObservableProperty]
    private bool _restartNeeded;

    public PlayGuideStep? CurrentStep => CurrentStepIndex >= 0 && CurrentStepIndex < Steps.Count ? Steps[CurrentStepIndex] : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStep))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoForward))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    private int _currentStepIndex;

    public bool IsLastStep => CurrentStepIndex >= Steps.Count - 1;

    public bool CanGoBack => CurrentStepIndex > 0;

    public bool CanGoForward => CurrentStepIndex < Steps.Count - 1;

    /// <summary>Roll-up of all step states for the header badge.</summary>
    public PlayGuideCheckState OverallState
    {
        get
        {
            if (Steps.Any(s => s.State == PlayGuideCheckState.Fail))
            {
                return PlayGuideCheckState.Fail;
            }
            if (Steps.Any(s => s.State == PlayGuideCheckState.Warn))
            {
                return PlayGuideCheckState.Warn;
            }
            if (Steps.All(s => s.State == PlayGuideCheckState.Pass))
            {
                return PlayGuideCheckState.Pass;
            }
            return PlayGuideCheckState.Pending;
        }
    }

    public string FixInFlightText => IsFixing ? " · fixing…" : string.Empty;

    /// <summary>Share of the five steps currently verified by the scan, 0-100, for the always-visible bar.</summary>
    public double GuideProgress => Steps.Count(s => s.State == PlayGuideCheckState.Pass) * 100.0 / Math.Max(1, Steps.Count);

    public string GuideProgressText
    {
        get
        {
            var verified = Steps.Count(s => s.State == PlayGuideCheckState.Pass);
            return verified == Steps.Count
                ? "all 5 steps verified"
                : $"{verified} of {Steps.Count} steps verified";
        }
    }

    public string OverallStateText => OverallState switch
    {
        PlayGuideCheckState.Pass => "READY TO PLAY",
        PlayGuideCheckState.Fail => "NEEDS ATTENTION",
        PlayGuideCheckState.Warn => "MOSTLY READY",
        _ => "NOT SCANNED",
    };

    partial void OnCurrentStepIndexChanged(int value)
    {
        foreach (var step in Steps)
        {
            step.IsCurrent = step.Index == value;
        }
    }

    private void BuildSteps()
    {
        Steps.Add(new PlayGuideStep(0, "01", "Enable virtualization in BIOS",
            "hardware switch — no software can flip this for you",
            "Critical Ops on PC runs inside Google Play Games' Android emulator, and the emulator needs CPU " +
            "virtualization. Restart the PC and press the BIOS key during boot — usually Del or F2, sometimes F10 " +
            "or Esc. Look under CPU Configuration, Advanced or Security for Intel Virtualization Technology (VT-x) " +
            "or AMD-V / SVM Mode, set it to Enabled, then save and exit.",
            actionLabel: "none — this one is in your BIOS"));

        Steps.Add(new PlayGuideStep(1, "02", "Turn on the Windows hypervisor",
            "one administrator prompt · restart afterwards",
            "Windows needs its hypervisor features on top of the firmware switch: Virtual Machine Platform and " +
            "Windows Hypervisor Platform. On Pro/Enterprise this is the Hyper-V feature set; Home gets the same " +
            "underlying features. Press the button and Optima asks Windows to enable them through its elevated " +
            "helper, then offers the restart.",
            actionLabel: "enable the Windows features"));

        Steps.Add(new PlayGuideStep(2, "03", "Join the Critical Ops beta",
            "google account · the same one everywhere",
            "The PC release is limited to beta testers. Open the official testing link in your browser, sign in " +
            "with the Google account you also plan to use on the PC, and press Become a tester. If you sign up on " +
            "your phone, remember the phone's game switches to beta servers too — that is expected.",
            actionLabel: "open the beta opt-in page"));

        Steps.Add(new PlayGuideStep(3, "04", "Install Google Play Games + Critical Ops",
            "the official installer · then the game from inside it",
            "Download Google Play Games for PC from Google's page, install it, and sign in with the same Google " +
            "account from the beta step. Search for Critical Ops inside the app and install it. This is the part " +
            "Optima can check automatically: it looks for the platform, its protocol handler, and the game itself.",
            actionLabel: "open the Google Play Games download page"));

        Steps.Add(new PlayGuideStep(4, "05", "Play",
            "launch from Optima or from the platform",
            "Everything is in place. Launch the game straight from the Play tab with your chosen profile — Optima " +
            "applies the performance profile and the virtual display, then restores everything when you quit. " +
            "Sign in with Google or Facebook to recover your progress; Apple ID / Game Center do not exist on PC.",
            actionLabel: "close the guide and play"));

        Steps[0].IsCurrent = true;
    }

    [RelayCommand]
    private async Task ScanAsync(CancellationToken ct)
    {
        if (IsScanning || IsFixing)
        {
            return;
        }
        IsScanning = true;
        ActionStatus = string.Empty;
        var scanFailed = false;
        try
        {
            ScanStatus = "scanning…";

            // Step 1: firmware virtualization (BIOS).
            var virtualization = await _systemInfo.GetVirtualizationStateAsync(ct);
            SetStep(0, virtualization.FirmwareVirtualizationEnabled == false
                ? (PlayGuideCheckState.Fail, "off in firmware — enable VT-x / AMD-V in your BIOS")
                : (PlayGuideCheckState.Pass, virtualization.HypervisorPresent == true
                    ? "on, and a hypervisor is running"
                    : "on in firmware"));

            // Step 2: Windows hypervisor features.
            SetStep(1, (virtualization.HypervisorPresent == true
                        || virtualization.HyperVFeatureEnabled == true
                        || virtualization.VirtualMachinePlatformEnabled == true
                        || virtualization.WindowsHypervisorPlatformEnabled == true)
                ? (PlayGuideCheckState.Pass, DescribeHypervisor(virtualization))
                : (PlayGuideCheckState.Fail, "no hypervisor feature enabled yet"));

            // Steps 3 and 4 need the detector; a missing platform fails the install step,
            // and the beta step can only be warned about (opt-in state is not queryable).
            var platform = await _detector.DetectPlatformAsync(ct);
            SetStep(3, platform is null
                ? (PlayGuideCheckState.Fail, "Google Play Games for PC is not installed")
                : (PlayGuideCheckState.Pass, $"installed · version {platform.Version}"));

            var game = await _detector.DetectTargetGameAsync(ct);
            SetStep(4, game is null
                ? (PlayGuideCheckState.Fail, "Critical Ops is not installed in Google Play Games")
                : (PlayGuideCheckState.Pass, "installed and launchable from the Play tab"));

            SetStep(2, _betaConfirmed
                ? (PlayGuideCheckState.Pass, "confirmed earlier in this guide — you opened the opt-in page")
                : (PlayGuideCheckState.Warn,
                    platform is null
                        ? "unknown until Google Play Games is installed"
                        : "cannot be verified automatically — opt in via the link, then continue"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Play guide scan failed");
            scanFailed = true;
        }
        finally
        {
            IsScanning = false;
            var failed = Steps.Count(s => s.State == PlayGuideCheckState.Fail);
            var warned = Steps.Count(s => s.State == PlayGuideCheckState.Warn);
            ScanStatus = scanFailed
                ? "scan failed, see the log on the Debug page"
                : (failed, warned) switch
                {
                    (0, 0) => "all five steps check out — you are ready to play",
                    (0, _) => "nothing blocking; the flagged step just needs a one-time confirmation",
                    _ => $"{failed} step(s) need attention — work through them in order",
                };
            OnPropertyChanged(nameof(OverallStateText));
            OnPropertyChanged(nameof(GuideProgress));
            OnPropertyChanged(nameof(GuideProgressText));
        }
    }

    [RelayCommand]
    private async Task RunStepActionAsync(CancellationToken ct)
    {
        if (CurrentStep is null || IsScanning || IsFixing)
        {
            return;
        }
        ActionStatus = string.Empty;
        switch (CurrentStep.Index)
        {
            case 1:
                await EnableHypervisorFeaturesAsync(ct);
                break;
            case 2:
                if (OpenUrl(BetaOptInUrl, "the beta opt-in page"))
                {
                    ConfirmBetaOptIn();
                }
                break;
            case 3:
                OpenUrl(GpgDownloadPage, "the Google Play Games download page");
                break;
            case 4:
                RequestClose?.Invoke();
                break;
        }
    }

    private async Task EnableHypervisorFeaturesAsync(CancellationToken ct)
    {
        IsFixing = true;
        RestartNeeded = false;
        FixProgress = 0;
        void OnFixProgress(object? sender, FixProgress e)
        {
            if (e.Percent is not { } percent)
            {
                return;
            }
            // Helper events arrive on the pipe's background thread; property changes must be marshaled.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                FixProgress = percent;
            }
            else
            {
                _ = dispatcher.BeginInvoke(() => FixProgress = percent);
            }
        }
        _fix.Progress += OnFixProgress;
        try
        {
            ActionStatus = "asking Windows to enable the features (one administrator prompt, can take a few minutes)…";
            var report = new ReadinessReport(
                FirmwareVirtualizationOff: false,
                HypervisorFeaturesMissing: true,
                GpgMissing: false);
            var result = await _fix.RunFixesAsync(report, ct);
            foreach (var line in result.Log)
            {
                ActionStatus = line;
            }
            RestartNeeded = result.RestartRequired;
            if (result.RestartRequired)
            {
                ActionStatus = "features enabled — Windows needs a restart to finish. Rescanning now; the scan still flags this step until you restart.";
            }
            FixProgress = 100;
        }
        finally
        {
            _fix.Progress -= OnFixProgress;
            IsFixing = false;
        }

        // Always re-check afterwards so the rail and badge reflect reality instead of the fix's own claims.
        _systemInfo.InvalidateCache();
        await ScanAsync(ct);
    }

    private bool OpenUrl(string url, string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            ActionStatus = $"opened {what} in your browser";
            return true;
        }
        catch (Exception ex)
        {
            ActionStatus = $"could not open {what}: {ex.Message}";
            _logger.LogWarning(ex, "Opening {Url} failed", url);
            return false;
        }
    }

    /// <summary>
    /// The opt-in state cannot be read from the OS, so the click itself is the attestation: opening the
    /// page marks the beta step verified for this guide session (rescans keep it).
    /// </summary>
    private void ConfirmBetaOptIn()
    {
        _betaConfirmed = true;
        var step = Steps[2];
        step.State = PlayGuideCheckState.Pass;
        step.Checks.Clear();
        step.Checks.Add(new PlayGuideCheckRow(
            step.Title,
            "marked done — you opened the opt-in page; finish 'Become a tester' there",
            PlayGuideCheckState.Pass));
    }

    [RelayCommand]
    private void RestartNow()
    {
        var error = _fix.ScheduleRestart();
        ActionStatus = error is null
            ? "restarting in 10 seconds — reopen the guide afterwards to re-check"
            : "restart could not be scheduled: " + error;
    }

    [RelayCommand]
    private void GoBack() => CurrentStepIndex = Math.Max(0, CurrentStepIndex - 1);

    /// <summary>Closes the guide from the last step (the bottom-right Finish button).</summary>
    [RelayCommand]
    private void Finish() => RequestClose?.Invoke();

    [RelayCommand]
    private void GoForward() => CurrentStepIndex = Math.Min(Steps.Count - 1, CurrentStepIndex + 1);

    public event Action? RequestClose;

    /// <summary>Runs the readiness scan and reports; safe to call after a fix attempt.</summary>
    public Task RescanAsync() => ScanAsync(CancellationToken.None);

    private void SetStep(int index, (PlayGuideCheckState State, string Detail) result)
    {
        var step = Steps[index];
        step.State = result.State;
        step.Checks.Clear();
        step.Checks.Add(new PlayGuideCheckRow(step.Title, result.Detail, result.State));
    }

    private static string DescribeHypervisor(VirtualizationState s)
    {
        var parts = new List<string>();
        if (s.HyperVFeatureEnabled == true) { parts.Add("Hyper-V"); }
        if (s.VirtualMachinePlatformEnabled == true) { parts.Add("Virtual Machine Platform"); }
        if (s.WindowsHypervisorPlatformEnabled == true) { parts.Add("Windows Hypervisor Platform"); }
        if (s.HypervisorPresent == true)
        {
            parts.Add("hypervisor running");
        }
        return parts.Count > 0 ? "enabled: " + string.Join(", ", parts) : "feature flags present";
    }
}
