namespace Optima.Core.Stats;

/// <summary>Aggregates for the SESSIONS weekly digest.</summary>
public sealed record WeeklyDigest(
    DateTimeOffset WeekStart,
    int SessionCount,
    TimeSpan TotalPlaytime,
    int DecidedMatches,
    int Wins,
    int Losses,
    double? AverageFps,
    double? OnePercentLow)
{
    public string Headline => SessionCount == 0
        ? "no sessions this week"
        : $"{SessionCount} session{(SessionCount == 1 ? "" : "s")} · {(int)TotalPlaytime.TotalHours}h {TotalPlaytime.Minutes}m"
            + (DecidedMatches > 0 ? $" · {Wins}W-{Losses}L" : "")
            + (AverageFps is { } fps ? $" · {fps:F0} fps avg" : "");
    public string WinRateText => DecidedMatches == 0 ? "-" : $"{(double)Wins / DecidedMatches:P0}";
}

/// <summary>Digests over the session history: the SESSIONS weekly card and the CSV export.</summary>
public static class TrackedPlayerDigest
{
    /// <summary>Sessions started within the last seven days, aggregated.</summary>
    public static WeeklyDigest BuildWeekly(IReadOnlyList<Models.SessionRecord> sessions, DateTimeOffset now)
    {
        var weekStart = now.AddDays(-7);
        var week = sessions.Where(s => s.StartedAt >= weekStart).ToList();
        var withStats = week.Where(s => s.Stats.HasData).ToList();

        var matches = week.SelectMany(s => new[] { s.StatsDelta?.Ranked }).Where(m => m is not null).ToList();
        var wins = (int)matches.Sum(m => m!.Wins);
        var losses = (int)matches.Sum(m => m!.Losses);

        return new WeeklyDigest(
            weekStart,
            week.Count,
            TimeSpan.FromSeconds(week.Sum(s => s.Duration.TotalSeconds)),
            wins + losses,
            wins,
            losses,
            withStats.Count > 0 ? withStats.Average(s => s.Stats.AverageFps) : null,
            withStats.Count > 0 ? withStats.Average(s => s.Stats.OnePercentLowFps) : null);
    }

    /// <summary>The full history as CSV (Excel-safe quoting; semicolon separator for EU locales).</summary>
    public static string ToCsv(IReadOnlyList<Models.SessionRecord> sessions)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("started_at;profile;kind;duration_seconds;avg_fps;one_percent_low;p99_frametime_ms;avg_ping_ms;loss_pct;ranked_k;ranked_d;ranked_w;ranked_l");
        foreach (var s in sessions)
        {
            builder.Append(Escape(s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"))).Append(';');
            builder.Append(Escape(s.ProfileName)).Append(';');
            builder.Append(Escape(s.LaunchKind.ToString())).Append(';');
            builder.Append((int)s.Duration.TotalSeconds).Append(';');
            builder.Append(s.Stats.HasData ? s.Stats.AverageFps.ToString("F1") : "").Append(';');
            builder.Append(s.Stats.HasData ? s.Stats.OnePercentLowFps.ToString("F1") : "").Append(';');
            builder.Append(s.Stats.HasData ? s.Stats.P99FrametimeMs.ToString("F2") : "").Append(';');
            builder.Append(s.Network is { } n ? n.AveragePingMs.ToString("F0") : "").Append(';');
            builder.Append(s.Network is { } net ? net.PacketLossPct.ToString("F1") : "").Append(';');
            builder.Append(s.StatsDelta?.Ranked.Kills ?? 0).Append(';');
            builder.Append(s.StatsDelta?.Ranked.Deaths ?? 0).Append(';');
            builder.Append(s.StatsDelta?.Ranked.Wins ?? 0).Append(';');
            builder.AppendLine((s.StatsDelta?.Ranked.Losses ?? 0).ToString());
        }
        return builder.ToString();

        static string Escape(string value)
            => value.Contains(';') || value.Contains('"') || value.Contains('\n')
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
    }
}
