using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

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
                        Reason = "The check itself failed. See Logs.",
                    };
                }
                Results.Add(result);
            }

            var passed = Results.Count(r => r.Status == DiagnosticStatus.Pass);
            var warned = Results.Count(r => r.Status == DiagnosticStatus.Warning);
            var failed = Results.Count(r => r.Status == DiagnosticStatus.Fail);
            Summary = (failed, warned) switch
            {
                (0, 0) => $"All good. {passed}/{Results.Count} checks passed.",
                (0, _) => $"{passed}/{Results.Count} passed, {warned} warning(s), nothing blocking.",
                (_, 0) => $"{failed} check(s) need attention.",
                _ => $"{failed} check(s) need attention, {warned} warning(s).",
            };
        }
        finally
        {
            IsRunning = false;
        }
    }
}
