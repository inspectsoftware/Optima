using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;

namespace Optima.App.ViewModels;

/// <summary>First-launch setup (§23): run detection, show the findings, create defaults, done.</summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public SetupWizardViewModel(StatusViewModel status, DiagnosticsViewModel diagnostics, SettingsService settings)
    {
        Status = status;
        Diagnostics = diagnostics;
        _settings = settings;
    }

    public StatusViewModel Status { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    [ObservableProperty] private bool _isDetecting = true;
    [ObservableProperty] private string _headline = "Setting things up…";

    public event EventHandler? Completed;

    public async Task RunDetectionAsync()
    {
        IsDetecting = true;
        await Status.RefreshAsync();
        await Diagnostics.InitializeAsync();
        IsDetecting = false;
        Headline = Status.CriticalOps.Kind == StatusKind.Good
            ? "Everything found — you're ready to play."
            : "Setup finished — check the notes below.";
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        // Default profiles are built-ins and always exist; just mark first run done.
        await _settings.UpdateSettingsAsync(s => s with { FirstRunCompleted = true });
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
