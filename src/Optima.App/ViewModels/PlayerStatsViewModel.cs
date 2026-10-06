using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Stats;
using Optima.Core.Theming;

namespace Optima.App.ViewModels;

/// <summary>One RANKED / CASUAL / CUSTOM row of the Home player panel.</summary>
public sealed record PlayerModeRow(string Mode, string Kills, string Deaths, string Assists, string Record, string Kd);

/// <summary>
/// HOME PLAYER panel: the current player's public Critical Ops profile, resolved by the
/// Settings pair (account id first, in-game name second) and refreshed on demand.
/// </summary>
public sealed partial class PlayerStatsViewModel : ObservableObject
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly CopsApiClient _api;
    private readonly SettingsService _settings;

    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private (string Ign, long? Id) _fetchedFor = ("", null);
    private bool _loading;

    public PlayerStatsViewModel(CopsApiClient api, SettingsService settings)
    {
        _api = api;
        _settings = settings;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public ObservableCollection<PlayerModeRow> Modes { get; } = [];

    [ObservableProperty] private string _playerName = "---";
    [ObservableProperty] private string _clanText = string.Empty;
    [ObservableProperty] private string _levelText = "---";
    [ObservableProperty] private string _accountIdText = "---";
    [ObservableProperty] private string _seasonText = "---";
    [ObservableProperty] private string _rankText = string.Empty;
    [ObservableProperty] private string _rankColorHex = AccentMath.DefaultAccentHex;
    [ObservableProperty] private int _rankTier = -1;
    [ObservableProperty] private int? _rankDivision;
    [ObservableProperty] private string _eloText = string.Empty;
    [ObservableProperty] private bool _hasRank;
    [ObservableProperty] private string _statusText = "No account set. Add your in-game name or account id in Settings.";
    [ObservableProperty] private bool _hasProfile;
    [ObservableProperty] private bool _isRefreshing;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _fetchedAt = DateTimeOffset.MinValue;
        _fetchedFor = ("", null);
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
        => await LoadAsync(force: false, ct).ConfigureAwait(true);

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        // The panel follows a changed name or id immediately, not after the cache expires. A save
        // that leaves the identity alone has nothing to show, and forcing a reload there threw away
        // a good profile (and its cached lookups) for every unrelated toggle in the app.
        var identity = (settings.PlayerIgn.Trim(), settings.PlayerAccountId);
        if (_fetchedFor == identity && _fetchedAt != DateTimeOffset.MinValue)
        {
            return;
        }

        _fetchedFor = identity;
        _fetchedAt = DateTimeOffset.MinValue;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _ = LoadFromUiAsync());
    }

    private async Task LoadFromUiAsync()
    {
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task LoadAsync(bool force, CancellationToken ct = default)
    {
        if (_loading)
        {
            return;
        }
        _loading = true;
        try
        {
            var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(true);
            var ign = settings.PlayerIgn.Trim();
            var accountId = settings.PlayerAccountId;
            _fetchedFor = (ign, accountId);

            if (ign.Length == 0 && accountId is not > 0)
            {
                ClearProfile("No account set. Add your in-game name or account id in Settings.");
                return;
            }

            var age = DateTimeOffset.Now - _fetchedAt;
            if (!force && _fetchedAt != DateTimeOffset.MinValue && age < CacheLifetime)
            {
                return;
            }

            IsRefreshing = true;
            var lookup = await _api.LookupPlayerAsync(ign, accountId, ct).ConfigureAwait(true);

            if (!lookup.IsFound)
            {
                ClearProfile(lookup.Status == CopsLookupStatus.NotFound
                    ? "Player not found. Check the in-game name and account id in Settings."
                    : "The stats API could not be reached. Check the connection and try refresh.");
                return;
            }

            ShowProfile(lookup);
        }
        catch (Exception)
        {
            ClearProfile("The stats API could not be reached. Check the connection and try refresh.");
        }
        finally
        {
            IsRefreshing = false;
            _loading = false;
        }
    }

    private void ShowProfile(CopsLookupResult lookup)
    {
        var profile = lookup.Profile!;
        _fetchedAt = DateTimeOffset.Now;

        // The headline carries the tag before the name ([DK] Wendigo); the full clan line stays below.
        PlayerName = profile.ClanTag is { Length: > 0 }
            ? $"[{profile.ClanTag}] {profile.Name}"
            : profile.Name;
        ClanText = profile.ClanName is null
            ? string.Empty
            : profile.ClanTag is null ? profile.ClanName : $"[{profile.ClanTag}] {profile.ClanName}";
        LevelText = profile.Level > 0 ? "Level " + profile.Level.ToString(CultureInfo.InvariantCulture) : "---";
        AccountIdText = profile.UserId > 0 ? profile.UserId.ToString(CultureInfo.InvariantCulture) : "---";
        SeasonText = profile.CurrentSeason is { } season ? "Season " + season.Season.ToString(CultureInfo.InvariantCulture) : "No season data";

        var rank = CopsRankLadder.Resolve(profile.Rank, profile.Mmr);
        if (rank is not null && profile.Mmr is > 0)
        {
            var division = CopsRankLadder.Division(profile.Rank, profile.Mmr);
            HasRank = true;
            RankText = division is { } d ? $"{rank.ShortName} {d}" : rank.Name;
            RankColorHex = rank.ColorHex;
            RankTier = rank.Tier;
            RankDivision = division;
            EloText = profile.Mmr.Value.ToString(CultureInfo.InvariantCulture) + " elo";
        }
        else
        {
            HasRank = false;
            RankText = string.Empty;
            EloText = string.Empty;
        }

        var current = profile.CurrentSeason;
        Modes.Clear();
        if (current is not null)
        {
            Modes.Add(Row("RANKED", current.Ranked));
            Modes.Add(Row("CASUAL", current.Casual));
            Modes.Add(Row("CUSTOM", current.Custom));
        }
        else
        {
            Modes.Add(Row("RANKED", CopsModeStats.Zero));
            Modes.Add(Row("CASUAL", CopsModeStats.Zero));
            Modes.Add(Row("CUSTOM", CopsModeStats.Zero));
        }

        HasProfile = true;
        StatusText = lookup.Detail;
    }

    private void ClearProfile(string status)
    {
        PlayerName = "---";
        ClanText = string.Empty;
        HasRank = false;
        RankText = string.Empty;
        RankTier = -1;
        RankDivision = null;
        EloText = string.Empty;
        LevelText = "---";
        AccountIdText = "---";
        SeasonText = "---";
        Modes.Clear();
        HasProfile = false;
        StatusText = status;
    }

    private static PlayerModeRow Row(string mode, CopsModeStats stats)
    {
        var kd = stats.Deaths == 0
            ? stats.Kills.ToString(CultureInfo.InvariantCulture)
            : (stats.Kills / (double)stats.Deaths).ToString("F2", CultureInfo.InvariantCulture);
        return new PlayerModeRow(
            mode,
            stats.Kills.ToString(CultureInfo.InvariantCulture),
            stats.Deaths.ToString(CultureInfo.InvariantCulture),
            stats.Assists.ToString(CultureInfo.InvariantCulture),
            stats.Wins.ToString(CultureInfo.InvariantCulture) + "W / " + stats.Losses.ToString(CultureInfo.InvariantCulture) + "L",
            kd);
    }
}
