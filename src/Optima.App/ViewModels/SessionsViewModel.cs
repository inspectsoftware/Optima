using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Statistics;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>One session history row with its display strings and config-change marker.</summary>
public sealed record SessionRowViewModel(SessionRecord Record, bool ConfigChanged)
{
    public string StartedText => Record.StartedAt.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    public string ProfileName => Record.ProfileName;
    public string KindTag => Record.LaunchKind switch
    {
        LaunchKind.Watch => "[ WATCH ]",
        LaunchKind.Benchmark => "[ BENCH ]",
        _ => "[ PLAY ]",
    };
    public string DurationText => Record.Duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    public string AvgFpsText => Record.Stats.HasData ? Record.Stats.AverageFps.ToString("F0", CultureInfo.InvariantCulture) : "-";
    public string OnePercentLowText => Record.Stats.HasData ? Record.Stats.OnePercentLowFps.ToString("F0", CultureInfo.InvariantCulture) : "-";
    public string P99Text => Record.Stats.HasData ? Record.Stats.P99FrametimeMs.ToString("F1", CultureInfo.InvariantCulture) + " ms" : "-";
    public string NetworkText => Record.Network is { } n
        ? $"{n.AveragePingMs:F0} ms · {n.JitterMs:F1} jit · {n.PacketLossPct:F1}%"
        : string.Empty;
    public string ConfigTag => ConfigChanged ? "[ CFG ]" : string.Empty;
}

/// <summary>Per-profile average FPS bar in the TRENDS section.</summary>
public sealed record ProfileTrendRow(string ProfileName, double AverageFps, double BarMaximum, int SessionCount)
{
    public string AverageText => AverageFps.ToString("F0", CultureInfo.InvariantCulture) + " fps";
    public string CountText => SessionCount == 1 ? "1 session" : $"{SessionCount} sessions";
}

/// <summary>
/// SESSIONS page (§13/§14): history and trends over the recorded sessions, per-session
/// drill-down of the persisted per-second FPS series, and benchmark comparison.
/// </summary>
public sealed partial class SessionsViewModel : ObservableObject
{
    private const int TrendLength = 20;
    private const int DetailWrapWidth = 100;

    private readonly ISessionStore _sessions;
    private readonly ProfileService _profiles;
    private readonly ILogger<SessionsViewModel> _logger;

    public SessionsViewModel(
        ISessionStore sessions,
        ProfileService profiles,
        GuidedBenchmarkViewModel guided,
        ILogger<SessionsViewModel> logger)
    {
        _sessions = sessions;
        _profiles = profiles;
        Guided = guided;
        _logger = logger;
    }

    public GuidedBenchmarkViewModel Guided { get; }

    public ObservableCollection<SessionRowViewModel> Rows { get; } = [];
    public ObservableCollection<ProfileTrendRow> ProfileTrends { get; } = [];
    public ObservableCollection<LaunchProfile> Profiles { get; } = [];

    // ---- Trends ----
    [ObservableProperty] private string _trendSparkline = string.Empty;
    [ObservableProperty] private string _trendLegend = "no completed sessions with fps data yet";
    [ObservableProperty] private bool _hasTrend;

    // ---- Detail ----
    [ObservableProperty] private SessionRowViewModel? _selectedRow;
    public ObservableCollection<InfoRow> DetailRows { get; } = [];
    public ObservableCollection<string> DetailSparklineLines { get; } = [];
    [ObservableProperty] private string _detailTweaks = string.Empty;
    [ObservableProperty] private string _detailSparklineLegend = string.Empty;

    // ---- Benchmark comparison ----
    [ObservableProperty] private LaunchProfile? _compareProfileA;
    [ObservableProperty] private LaunchProfile? _compareProfileB;
    [ObservableProperty] private BenchmarkComparison? _comparison;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            await ReloadAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Loading session history failed");
        }
    }

    [RelayCommand]
    private async Task ReloadAsync(CancellationToken ct = default)
    {
        var history = await _sessions.GetSessionsAsync(200, ct);
        var trend = SessionTrendBuilder.Build(history, TrendLength);
        var changedByid = trend.Where(p => p.ConfigChanged).Select(p => p.Session.Id).ToHashSet();

        Rows.Clear();
        foreach (var record in history)
        {
            Rows.Add(new SessionRowViewModel(record, changedByid.Contains(record.Id)));
        }

        BuildTrend(trend);
        BuildProfileTrends(history);

        Profiles.Clear();
        foreach (var profile in await _profiles.GetProfilesAsync(ct))
        {
            Profiles.Add(profile);
        }

        SelectedRow ??= Rows.FirstOrDefault();
    }

    private void BuildTrend(IReadOnlyList<SessionTrendPoint> trend)
    {
        var values = trend.Select(p => p.Session.Stats.AverageFps).ToList();
        HasTrend = values.Count > 0;
        if (!HasTrend)
        {
            TrendSparkline = string.Empty;
            TrendLegend = "no completed sessions with fps data yet";
            return;
        }

        TrendSparkline = AsciiSparkline.Render(values, TrendLength * 2);
        var changes = trend.Count(p => p.ConfigChanged);
        TrendLegend =
            $"avg fps · last {values.Count} sessions · oldest to newest · " +
            $"{values.Min():F0} min · {values.Average():F0} avg · {values.Max():F0} max" +
            (changes > 0 ? $" · {changes} config change{(changes == 1 ? string.Empty : "s")}" : string.Empty);
    }

    private void BuildProfileTrends(IReadOnlyList<SessionRecord> history)
    {
        ProfileTrends.Clear();
        var groups = history
            .Where(s => s.Stats.HasData)
            .GroupBy(s => s.ProfileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Stats: BenchmarkComparer.AggregateStats([.. g]), Count: g.Count()))
            .Where(g => g.Stats.HasData)
            .OrderByDescending(g => g.Stats.AverageFps)
            .ToList();
        if (groups.Count == 0)
        {
            return;
        }
        var max = groups.Max(g => g.Stats.AverageFps);
        foreach (var group in groups)
        {
            ProfileTrends.Add(new ProfileTrendRow(group.Name, group.Stats.AverageFps, max, group.Count));
        }
    }

    partial void OnSelectedRowChanged(SessionRowViewModel? value)
    {
        DetailRows.Clear();
        DetailSparklineLines.Clear();
        DetailTweaks = string.Empty;
        DetailSparklineLegend = string.Empty;
        if (value is null)
        {
            return;
        }

        var record = value.Record;
        DetailRows.Add(new InfoRow("Started", record.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        DetailRows.Add(new InfoRow("Profile", $"{record.ProfileName} · {record.ProfileHash}"));
        DetailRows.Add(new InfoRow("Kind", record.LaunchKind.ToString().ToLowerInvariant()));
        DetailRows.Add(new InfoRow("Duration", value.DurationText));
        if (record.Stats.HasData)
        {
            DetailRows.Add(new InfoRow("Average FPS", record.Stats.AverageFps.ToString("F1", CultureInfo.InvariantCulture)));
            DetailRows.Add(new InfoRow("1% low", record.Stats.OnePercentLowFps.ToString("F1", CultureInfo.InvariantCulture)));
            DetailRows.Add(new InfoRow("0.1% low", record.Stats.PointOnePercentLowFps.ToString("F1", CultureInfo.InvariantCulture)));
            DetailRows.Add(new InfoRow("Average frametime", record.Stats.AverageFrametimeMs.ToString("F2", CultureInfo.InvariantCulture) + " ms"));
            DetailRows.Add(new InfoRow("P95 / P99 frametime",
                $"{record.Stats.P95FrametimeMs.ToString("F2", CultureInfo.InvariantCulture)} / {record.Stats.P99FrametimeMs.ToString("F2", CultureInfo.InvariantCulture)} ms"));
        }
        else
        {
            DetailRows.Add(new InfoRow("FPS data", "not captured for this session"));
        }
        if (record.Network is { } network)
        {
            DetailRows.Add(new InfoRow("Network", value.NetworkText));
        }

        DetailTweaks = record.TweakIds.Count > 0
            ? string.Join(" · ", record.TweakIds)
            : "no tweaks were enabled";

        if (record.FpsSamples.Count > 1)
        {
            foreach (var line in AsciiSparkline.RenderWrapped(record.FpsSamples, DetailWrapWidth))
            {
                DetailSparklineLines.Add(line);
            }
            DetailSparklineLegend =
                $"fps per second · {record.FpsSamples.Count} samples · " +
                $"{record.FpsSamples.Min():F0} min · {record.FpsSamples.Average():F0} avg · {record.FpsSamples.Max():F0} max";
        }
    }

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
