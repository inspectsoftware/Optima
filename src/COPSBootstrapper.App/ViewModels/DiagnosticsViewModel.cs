using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using COPSBootstrapper.Core.Abstractions;
using COPSBootstrapper.Core.Models;
using Microsoft.Extensions.Logging;

namespace COPSBootstrapper.App.ViewModels;

/// <summary>DIAGNOSTICS page (§15): runs every registered check and lists status/reason/fix.</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IReadOnlyList<IDiagnosticCheck> _checks;
    private readonly ILogger<DiagnosticsViewModel> _logger;

    public DiagnosticsViewModel(IEnumerable<IDiagnosticCheck> checks, ILogger<DiagnosticsViewModel> logger)
    {
        _checks = checks.OrderBy(c => c.Order).ToList();
        _logger = logger;
    }

    public ObservableCollection<DiagnosticResult> Results { get; } = [];

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _summary = string.Empty;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (Results.Count == 0)
        {
            await RunAllAsync(ct);
        }
    }

    [RelayCommand]
    private async Task RunAllAsync(CancellationToken ct = default)
    {
        if (IsRunning)
        {
            return;
        }
        IsRunning = true;
        Results.Clear();
        try
        {
            foreach (var check in _checks)
            {
                DiagnosticResult result;
                try
                {
                    result = await check.RunAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Diagnostic check {Check} crashed", check.Name);
                    result = new DiagnosticResult
                    {
                        CheckName = check.Name,
                        Status = DiagnosticStatus.Fail,
                        Reason = "The check itself failed — see Logs.",
                    };
                }
                Results.Add(result);
            }

            var passed = Results.Count(r => r.Status == DiagnosticStatus.Pass);
            var failed = Results.Count(r => r.Status == DiagnosticStatus.Fail);
            Summary = failed == 0
                ? $"All good — {passed}/{Results.Count} checks passed."
                : $"{failed} check(s) need attention.";
        }
        finally
        {
            IsRunning = false;
        }
    }
}
