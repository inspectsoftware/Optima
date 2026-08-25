using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Statistics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Optima.App.ViewModels;

/// <summary>
/// PERFORMANCE page (§8/§13/§14/§22): profile editing with full per-setting disclosure,
/// session history, and benchmark comparison with the statistical noise guard.
/// </summary>
public sealed partial class PerformanceViewModel : ObservableObject
{
    private readonly ProfileService _profiles;
    private readonly ISessionStore _sessions;
    private readonly PlayViewModel _play;
    private readonly ILogger<PerformanceViewModel> _logger;

    public PerformanceViewModel(ProfileService profiles, ISessionStore sessions, PlayViewModel play, ILogger<PerformanceViewModel> logger)
    {
        _profiles = profiles;
        _sessions = sessions;
        _play = play;
        _logger = logger;
    }

    public ObservableCollection<LaunchProfile> Profiles { get; } = [];
    public ObservableCollection<SessionRecord> Sessions { get; } = [];
    public IReadOnlyList<SettingExplanation> Explanations => SettingExplanations.All;

    public IReadOnlyList<PowerPlanKind> PowerPlanOptions { get; } = Enum.GetValues<PowerPlanKind>();
    public IReadOnlyList<ProcessPriorityLevel> PriorityOptions { get; } = Enum.GetValues<ProcessPriorityLevel>();

    // ---- Editor fields ----
    [ObservableProperty] private LaunchProfile? _selectedProfile;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private bool _editVirtualDisplay;
    [ObservableProperty] private int _editWidth = 1920;
    [ObservableProperty] private int _editHeight = 1080;
    [ObservableProperty] private int _editRefreshRate = 240;
    [ObservableProperty] private PowerPlanKind _editPowerPlan = PowerPlanKind.Unchanged;
    [ObservableProperty] private ProcessPriorityLevel _editPriority = ProcessPriorityLevel.Unchanged;
    [ObservableProperty] private bool _editDisableThrottling;
    [ObservableProperty] private string _editCleanupList = string.Empty;
    [ObservableProperty] private string _editorStatus = string.Empty;

    // ---- Benchmark compare ----
    [ObservableProperty] private LaunchProfile? _compareProfileA;
    [ObservableProperty] private LaunchProfile? _compareProfileB;
    [ObservableProperty] private BenchmarkComparison? _comparison;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await ReloadProfilesAsync(ct);
        await ReloadSessionsAsync(ct);
    }

    private async Task ReloadProfilesAsync(CancellationToken ct = default)
    {
        Profiles.Clear();
        foreach (var profile in await _profiles.GetProfilesAsync(ct))
        {
            Profiles.Add(profile);
        }
        SelectedProfile ??= Profiles.FirstOrDefault();
    }

    private async Task ReloadSessionsAsync(CancellationToken ct = default)
    {
        Sessions.Clear();
        foreach (var session in await _sessions.GetSessionsAsync(50, ct))
        {
            Sessions.Add(session);
        }
    }

    partial void OnSelectedProfileChanged(LaunchProfile? value)
    {
        if (value is null)
        {
            return;
        }
        EditName = value.IsBuiltIn ? value.Name + " (copy)" : value.Name;
        EditVirtualDisplay = value.Display.VirtualDisplay;
        EditWidth = value.Display.Width;
        EditHeight = value.Display.Height;
        EditRefreshRate = value.Display.RefreshRate;
        EditPowerPlan = value.Performance.PowerPlan;
        EditPriority = value.Performance.Priority;
        EditDisableThrottling = value.Performance.DisablePowerThrottling;
        EditCleanupList = string.Join(", ", value.Performance.CleanupProcessNames);
        EditorStatus = value.IsBuiltIn ? "Built-in profile. Saving creates a copy under the new name." : string.Empty;
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        try
        {
            var profile = new LaunchProfile
            {
                Name = EditName.Trim(),
                Display = new DisplayProfile
                {
                    VirtualDisplay = EditVirtualDisplay,
                    Width = EditWidth,
                    Height = EditHeight,
                    RefreshRate = EditRefreshRate,
                },
                Performance = new PerformanceProfile
                {
                    PowerPlan = EditPowerPlan,
                    Priority = EditPriority,
                    DisablePowerThrottling = EditDisableThrottling,
                    CleanupProcessNames = EditCleanupList
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                },
            };
            if (profile.Name.Length == 0)
            {
                EditorStatus = "Give the profile a name first.";
                return;
            }

            await _profiles.SaveProfileAsync(profile);
            await ReloadProfilesAsync();
            await _play.InitializeAsync();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name == profile.Name);
            EditorStatus = $"Saved '{profile.Name}'.";
        }
        catch (InvalidOperationException ex)
        {
            EditorStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is null || SelectedProfile.IsBuiltIn)
        {
            EditorStatus = "Built-in profiles cannot be deleted.";
            return;
        }
        await _profiles.DeleteProfileAsync(SelectedProfile.Name);
        await ReloadProfilesAsync();
        await _play.InitializeAsync();
        SelectedProfile = Profiles.FirstOrDefault();
        EditorStatus = "Profile deleted.";
    }

    [RelayCommand]
    private async Task ExportProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "Optima profile (*.json)|*.json",
            FileName = SelectedProfile.Name + ".json",
        };
        if (dialog.ShowDialog() == true)
        {
            await _profiles.ExportProfileAsync(SelectedProfile.Name, dialog.FileName);
            EditorStatus = $"Exported to {dialog.FileName}.";
        }
    }

    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Optima profile (*.json)|*.json|All files|*.*" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            var imported = await _profiles.ImportProfileAsync(dialog.FileName);
            await ReloadProfilesAsync();
            await _play.InitializeAsync();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name == imported.Name);
            EditorStatus = $"Imported '{imported.Name}'.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            EditorStatus = "That file is not a valid profile.";
            _logger.LogWarning(ex, "Profile import failed");
        }
    }

    [RelayCommand]
    private async Task RefreshSessionsAsync() => await ReloadSessionsAsync();

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (CompareProfileA is null || CompareProfileB is null)
        {
            return;
        }
        var sessionsA = await _sessions.GetSessionsByProfileAsync(CompareProfileA.Name);
        var sessionsB = await _sessions.GetSessionsByProfileAsync(CompareProfileB.Name);
        Comparison = BenchmarkComparer.Compare(CompareProfileA.Name, sessionsA, CompareProfileB.Name, sessionsB);
    }
}
