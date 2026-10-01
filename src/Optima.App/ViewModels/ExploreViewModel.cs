using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Stats;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>Sortable, filterable row of a player leaderboard.</summary>
public sealed partial class ExplorePlayerRow : ObservableObject
{
    public ExplorePlayerRow(int rank, string name, long kills, long deaths, long assists, double ratio)
    {
        Rank = rank;
        Name = name;
        Kills = kills;
        Deaths = deaths;
        Assists = assists;
        Ratio = ratio;
    }

    public int Rank { get; }
    public string Name { get; }
    public long Kills { get; }
    public long Deaths { get; }
    public long Assists { get; }
    public double Ratio { get; }

    public string KillsText => Kills.ToString("N0", CultureInfo.InvariantCulture);
    public string DeathsText => Deaths.ToString("N0", CultureInfo.InvariantCulture);
    public string AssistsText => Assists.ToString("N0", CultureInfo.InvariantCulture);
    public string RatioText => Ratio.ToString("F2", CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _isVisible = true;
}

/// <summary>Sortable, filterable row of the clan leaderboard.</summary>
public sealed partial class ExploreClanRow : ObservableObject
{
    public ExploreClanRow(CopsLeaderboardClanRow source)
    {
        Source = source;
    }

    public CopsLeaderboardClanRow Source { get; }

    public int Rank => Source.Rank;
    public string Name => Source.Name;
    public string Tag => Source.Tag;
    public string RatingText => Source.Rating.ToString("N0", CultureInfo.InvariantCulture);
    public string PlayersText => Source.Players.ToString(CultureInfo.InvariantCulture);
    public string AverageRatingText => Source.AverageRating.ToString("F0", CultureInfo.InvariantCulture);
    public string KdrText => Source.Kdr.ToString("F2", CultureInfo.InvariantCulture);
    public string WlrText => Source.Wlr.ToString("F2", CultureInfo.InvariantCulture);
    public string KillsText => Source.Kills.ToString("N0", CultureInfo.InvariantCulture);

    public string RecordText => Source.Wins.ToString(CultureInfo.InvariantCulture) + "W / " + Source.Losses.ToString(CultureInfo.InvariantCulture) + "L";

    [ObservableProperty]
    private bool _isVisible = true;
}

/// <summary>One member row of the clan detail pane, enriched from the public profile API.</summary>
public sealed partial class ClanMemberRow : ObservableObject
{
    public ClanMemberRow(string token, int index)
    {
        Token = token;
        RankText = (index + 1).ToString(CultureInfo.InvariantCulture) + ".";
    }

    /// <summary>The raw pasted token: an account id or an in-game name.</summary>
    public string Token { get; }

    public string RankText { get; }

    [ObservableProperty] private string _name = "…";
    [ObservableProperty] private string _levelText = "—";
    [ObservableProperty] private string _rankText2 = string.Empty;
    [ObservableProperty] private string _eloText = string.Empty;
    [ObservableProperty] private string _kdText = string.Empty;
    [ObservableProperty] private string _clanText = string.Empty;
    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private bool _isResolved;
    [ObservableProperty] private bool _isMissing;

    public long? UserId { get; private set; }

    public void Show(CopsPlayerProfile profile, long seasonKills, long seasonDeaths)
    {
        UserId = profile.UserId;
        Name = profile.Name;
        LevelText = profile.Level > 0 ? profile.Level.ToString(CultureInfo.InvariantCulture) : "—";
        var rank = CopsRankLadder.Resolve(profile.Rank, profile.Mmr);
        if (rank is not null && profile.Mmr is > 0)
        {
            var division = CopsRankLadder.Division(profile.Rank, profile.Mmr);
            RankText2 = division is { } d ? $"{rank.ShortName} {d}" : rank.Name;
            EloText = profile.Mmr.Value.ToString(CultureInfo.InvariantCulture);
        }
        var kd = seasonDeaths == 0 ? seasonKills : (double)seasonKills / seasonDeaths;
        KdText = kd.ToString("F2", CultureInfo.InvariantCulture);
        ClanText = profile.ClanTag is { Length: > 0 } ? "[" + profile.ClanTag + "]" : string.Empty;
        StateText = string.Empty;
        IsMissing = false;
        IsResolved = true;
    }

    public void MarkMissing(string reason)
    {
        Name = Token;
        StateText = reason;
        IsMissing = true;
        IsResolved = true;
    }
}

/// <summary>
/// The EXPLORE page: ranked / casual / elite / clan leaderboards from Critical Force's public API,
/// refreshed on a timer while the page is visible, with client-side search (the API has none), and
/// a clan pane: the API exposes no clan-members endpoint, so a clan's roster is pasted in (names or
/// ids, one per line) and enriched via batch profile lookups. Rosters persist in config per clan tag.
/// </summary>
public sealed partial class ExploreViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private const int MaxVisibleRows = 250;

    private readonly CopsApiClient _api;
    private readonly SettingsService _settings;
    private readonly ILogger<ExploreViewModel> _logger;
    private System.Timers.Timer? _timer;
    private bool _loading;

    /// <summary>
    /// Profiles seen via batch enrichment of the leaderboards, keyed by name and by id string, so a
    /// clan pane can show its members without any pasting when they appear on the leaderboards.
    /// </summary>
    private readonly Dictionary<string, CopsPlayerProfile> _profileCache = new(StringComparer.OrdinalIgnoreCase);

    public ExploreViewModel(CopsApiClient api, SettingsService settings, ILogger<ExploreViewModel> logger)
    {
        _api = api;
        _settings = settings;
        _logger = logger;
    }

    public ObservableCollection<ExplorePlayerRow> PlayerRows { get; } = [];
    public ObservableCollection<ExploreClanRow> ClanRows { get; } = [];
    public ObservableCollection<ClanMemberRow> ClanMembers { get; } = [];

    [ObservableProperty] private bool _isEliteTab = true;
    [ObservableProperty] private bool _isRankedTab;
    [ObservableProperty] private bool _isCasualTab;
    [ObservableProperty] private bool _isClansTab;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _statusText = "loading…";
    [ObservableProperty] private string _updatedText = string.Empty;
    [ObservableProperty] private bool _isClanDetailOpen;
    [ObservableProperty] private string _clanDetailTitle = string.Empty;

    /// <summary>The leaderboard row of the open clan, when it is on the top-100 board, for the stats strip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClanStats))]
    private ExploreClanRow? _selectedClan;

    public bool HasClanStats => SelectedClan is not null;

    [ObservableProperty] private string _rosterText = string.Empty;
    [ObservableProperty] private bool _isResolvingRoster;
    [ObservableProperty] private bool _hasClanMembers;
    [ObservableProperty] private string _rosterHint = string.Empty;

    // Player profile pane (any IGN or id).
    [ObservableProperty] private bool _isProfileOpen;
    [ObservableProperty] private bool _isProfileLoading;
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private string _profileClanText = string.Empty;
    [ObservableProperty] private string _profileLevelText = string.Empty;
    [ObservableProperty] private string _profileSeasonText = string.Empty;
    [ObservableProperty] private string _profileIdText = string.Empty;
    [ObservableProperty] private string _profileStatusText = string.Empty;
    [ObservableProperty] private bool _hasProfileRank;
    [ObservableProperty] private string _profileRankText = string.Empty;
    [ObservableProperty] private string _profileEloText = string.Empty;
    [ObservableProperty] private string _profileColorHex = "#757D88";
    [ObservableProperty] private int _profileTier = -1;
    [ObservableProperty] private int? _profileDivision;
    [ObservableProperty] private string _profileClanTag = string.Empty;
    [ObservableProperty] private bool _hasProfileClan;

    /// <summary>The three mode rows of the opened profile.</summary>
    public ObservableCollection<PlayerModeRow> ProfileModes { get; } = [];

    /// <summary>Rows currently shown for player tabs (search-filtered slice of PlayerRows).</summary>
    public IReadOnlyList<ExplorePlayerRow> VisiblePlayerRows => PlayerRows.Where(r => r.IsVisible).Take(MaxVisibleRows).ToList();

    /// <summary>Rows currently shown for the clan tab.</summary>
    public IReadOnlyList<ExploreClanRow> VisibleClanRows => ClanRows.Where(r => r.IsVisible).Take(MaxVisibleRows).ToList();

    public string TotalCountText => IsClansTab
        ? $"{ClanRows.Count(r => r.IsVisible)} of {ClanRows.Count} clans"
        : $"{PlayerRows.Count(r => r.IsVisible)} of {PlayerRows.Count} players";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await RefreshAsync(ct).ConfigureAwait(true);
        StartTimer();
    }

    private void StartTimer()
    {
        if (_timer is not null)
        {
            return;
        }
        _timer = new System.Timers.Timer(RefreshInterval.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += async (_, _) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }
            await dispatcher.InvokeAsync(async () => await RefreshAsync().ConfigureAwait(true));
        };
        _timer.Start();
    }

    public void StopTimer() => _timer?.Dispose();

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_loading)
        {
            return;
        }
        _loading = true;
        try
        {
            if (IsClansTab)
            {
                await LoadClansAsync(ct).ConfigureAwait(true);
            }
            else
            {
                await LoadPlayersAsync(ct).ConfigureAwait(true);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadPlayersAsync(CancellationToken ct)
    {
        StatusText = "loading leaderboard…";
        var endpoint = IsEliteTab ? "elite" : IsRankedTab ? "ranked" : "kills";
        var (rows, problem) = await _api.GetLeaderboardAsync(endpoint, CopsLeaderboardParser.ParsePlayers, ct).ConfigureAwait(true);
        if (problem is not null)
        {
            StatusText = "the leaderboard API could not be reached (" + problem + ")";
            return;
        }
        PlayerRows.Clear();
        foreach (var row in rows)
        {
            PlayerRows.Add(new ExplorePlayerRow(row.Rank, row.Name, row.Kills, row.Deaths, row.Assists, row.Ratio));
        }
        ApplyFilters();
        StatusText = string.Empty;
        UpdatedText = "updated " + DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        // Enrich in the background: each profile names its clan, which powers the clan member discovery.
        _ = EnrichLeaderboardAsync(IsEliteTab || IsRankedTab ? rows.Take(300).Select(r => r.Name) : rows.Take(300).Select(r => r.Name));
    }

    /// <summary>Batch-fetches profiles for leaderboard names to learn their clans (best effort, never blocking the table).</summary>
    private async Task EnrichLeaderboardAsync(IEnumerable<string> names)
    {
        try
        {
            var list = names.Where(n => n.Length > 0 && !_profileCache.ContainsKey(n)).Take(10).ToList();
            if (list.Count == 0)
            {
                return;
            }
            var profiles = await ResolveTokensAsync(list).ConfigureAwait(true);
            foreach (var profile in profiles)
            {
                CacheProfile(profile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Leaderboard enrichment failed");
        }
    }

    private void CacheProfile(CopsPlayerProfile profile)
    {
        if (profile.Name.Length > 0)
        {
            _profileCache[profile.Name] = profile;
        }
        if (profile.UserId > 0)
        {
            _profileCache[profile.UserId.ToString(CultureInfo.InvariantCulture)] = profile;
        }
    }

    private async Task LoadClansAsync(CancellationToken ct)
    {
        StatusText = "loading clans…";
        var (rows, problem) = await _api.GetLeaderboardAsync("clan", CopsLeaderboardParser.ParseClans, ct).ConfigureAwait(true);
        if (problem is not null)
        {
            StatusText = "the leaderboard API could not be reached (" + problem + ")";
            return;
        }
        ClanRows.Clear();
        foreach (var row in rows)
        {
            ClanRows.Add(new ExploreClanRow(row));
        }
        ApplyFilters();
        StatusText = string.Empty;
        UpdatedText = "updated " + DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    /// <summary>
    /// Re-filters and re-raises everything the tables bind to: VisiblePlayerRows / VisibleClanRows are
    /// computed properties, so without these notifications the tables would show only what was bound
    /// at page-open (nothing) — the bug where the counter updated but the list never did.
    /// </summary>
    private void ApplyFilters()
    {
        ApplyPlayerFilter();
        ApplyClanFilter();
        OnPropertyChanged(nameof(VisiblePlayerRows));
        OnPropertyChanged(nameof(VisibleClanRows));
        OnPropertyChanged(nameof(TotalCountText));
    }

    private void ApplyPlayerFilter()
    {
        var term = SearchText.Trim();
        foreach (var row in PlayerRows)
        {
            row.IsVisible = term.Length == 0
                || row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Rank.ToString(CultureInfo.InvariantCulture).StartsWith(term, StringComparison.Ordinal);
        }
    }

    private void ApplyClanFilter()
    {
        var term = SearchText.Trim();
        foreach (var row in ClanRows)
        {
            row.IsVisible = term.Length == 0
                || row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Tag.Contains(term, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    private void ShowTab(string tab)
    {
        IsEliteTab = tab == "elite";
        IsRankedTab = tab == "ranked";
        IsCasualTab = tab == "casual";
        IsClansTab = tab == "clans";
        SearchText = string.Empty;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void OpenClan(ExploreClanRow? row)
    {
        if (row is null)
        {
            return;
        }
        ClanDetailTitle = $"[{row.Tag}] {row.Name}";
        SelectedClan = row;
        IsClanDetailOpen = true;
        _ = LoadRosterAsync(row.Tag, row.Name);
    }

    /// <summary>Opens a clan pane from anywhere by tag and name (e.g. from a player profile).</summary>
    public void OpenClanByTag(string tag, string name)
    {
        if (tag.Length == 0)
        {
            return;
        }
        ClanDetailTitle = $"[{tag}] {name}";
        SelectedClan = ClanRows.FirstOrDefault(r => string.Equals(r.Tag, tag, StringComparison.OrdinalIgnoreCase));
        IsClanDetailOpen = true;
        _ = LoadRosterAsync(tag, name);
    }

    /// <summary>Opens the profile pane for any account id or in-game name.</summary>
    public void OpenProfile(string token)
    {
        if (token.Trim().Length == 0)
        {
            return;
        }
        IsProfileOpen = true;
        _ = LoadProfileAsync(token.Trim());
    }

    [RelayCommand]
    private void OpenProfileFromInput()
    {
        if (SearchText.Trim().Length > 0)
        {
            OpenProfile(SearchText.Trim());
        }
    }

    [RelayCommand]
    private void CloseProfile() => IsProfileOpen = false;

    [RelayCommand]
    private void OpenProfileClan()
    {
        if (HasProfileClan)
        {
            CloseProfile();
            IsClansTab = true;
            IsEliteTab = IsRankedTab = IsCasualTab = false;
            OpenClanByTag(ProfileClanTag, ProfileClanText.Trim('[', ']'));
        }
    }

    private async Task LoadProfileAsync(string token)
    {
        IsProfileLoading = true;
        ProfileStatusText = "looking up " + token + "…";
        try
        {
            var profiles = await ResolveTokensAsync([token]).ConfigureAwait(true);
            var profile = profiles.FirstOrDefault();
            if (profile is null)
            {
                ProfileName = token;
                ProfileStatusText = "no player found for that id or in-game name (names are case sensitive on the API)";
                ProfileModes.Clear();
                HasProfileRank = false;
                HasProfileClan = false;
                return;
            }
            ShowProfilePane(profile);
            ProfileStatusText = string.Empty;
        }
        finally
        {
            IsProfileLoading = false;
        }
    }

    private void ShowProfilePane(CopsPlayerProfile profile)
    {
        CacheProfile(profile);
        ProfileName = profile.ClanTag is { Length: > 0 } ? $"[{profile.ClanTag}] {profile.Name}" : profile.Name;
        ProfileClanText = profile.ClanName is null ? string.Empty
            : profile.ClanTag is null ? profile.ClanName : $"[{profile.ClanTag}] {profile.ClanName}";
        HasProfileClan = profile.ClanTag is { Length: > 0 };
        ProfileClanTag = profile.ClanTag ?? string.Empty;
        ProfileLevelText = profile.Level > 0 ? "Level " + profile.Level.ToString(CultureInfo.InvariantCulture) : string.Empty;
        ProfileSeasonText = profile.CurrentSeason is { } season ? "Season " + season.Season.ToString(CultureInfo.InvariantCulture) : string.Empty;
        ProfileIdText = profile.UserId > 0 ? "id " + profile.UserId.ToString(CultureInfo.InvariantCulture) : string.Empty;

        var rank = CopsRankLadder.Resolve(profile.Rank, profile.Mmr);
        if (rank is not null && profile.Mmr is > 0)
        {
            var division = CopsRankLadder.Division(profile.Rank, profile.Mmr);
            HasProfileRank = true;
            ProfileRankText = division is { } d ? $"{rank.ShortName} {d}" : rank.Name;
            ProfileColorHex = rank.ColorHex;
            ProfileTier = rank.Tier;
            ProfileDivision = division;
            ProfileEloText = profile.Mmr.Value.ToString(CultureInfo.InvariantCulture) + " elo";
        }
        else
        {
            HasProfileRank = false;
            ProfileRankText = string.Empty;
            ProfileEloText = string.Empty;
        }

        ProfileModes.Clear();
        if (profile.CurrentSeason is { } current)
        {
            ProfileModes.Add(ModeRow("RANKED", current.Ranked));
            ProfileModes.Add(ModeRow("CASUAL", current.Casual));
            ProfileModes.Add(ModeRow("CUSTOM", current.Custom));
        }
    }

    private static PlayerModeRow ModeRow(string mode, CopsModeStats stats)
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

    /// <summary>
    /// Resolves id/name tokens to profiles one batch at a time. The API fails a whole batch (HTTP 500)
    /// when any token is unknown, so on failure each token is retried alone; unknown ones are dropped.
    /// </summary>
    private async Task<List<CopsPlayerProfile>> ResolveTokensAsync(IReadOnlyList<string> tokens)
    {
        var ids = tokens.Where(t => long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0).Select(long.Parse).ToList();
        var names = tokens.Where(t => !long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)).ToList();

        var results = new List<CopsPlayerProfile>();
        if (ids.Count > 0)
        {
            var byIds = await _api.GetProfilesByIdsAsync(ids).ConfigureAwait(true);
            if (byIds.Count == ids.Count)
            {
                results.AddRange(byIds);
            }
            else
            {
                results.AddRange(await ResolveIndividuallyAsync(id => _api.GetProfilesByIdsAsync([id]), ids.Select(i => i.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(true));
            }
        }
        if (names.Count > 0)
        {
            var byNames = await _api.GetProfilesByNamesAsync(names).ConfigureAwait(true);
            if (byNames.Count == names.Count)
            {
                results.AddRange(byNames);
            }
            else
            {
                results.AddRange(await ResolveIndividuallyAsync(_ => _api.GetProfilesByNamesAsync(names.Take(1)), names).ConfigureAwait(true));
            }
        }
        return results;
    }

    private static async Task<List<CopsPlayerProfile>> ResolveIndividuallyAsync(
        Func<long, Task<IReadOnlyList<CopsPlayerProfile>>> idFetcher, IEnumerable<string> tokens)
    {
        var results = new List<CopsPlayerProfile>();
        foreach (var token in tokens)
        {
            try
            {
                if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    results.AddRange(await idFetcher(id).ConfigureAwait(true));
                }
            }
            catch (Exception)
            {
                // Unknown token: skip it; the row is marked missing by the caller.
            }
        }
        return results;
    }

    [RelayCommand]
    private void CloseClan() => IsClanDetailOpen = false;

    /// <summary>Loads a clan pane: discovered members first, then the saved roster, else invite paste.</summary>
    private async Task LoadRosterAsync(string tag, string name)
    {
        RosterHint = string.Empty;
        var settings = await _settings.GetSettingsAsync().ConfigureAwait(true);
        var roster = settings.ClanRosters.FirstOrDefault(r => string.Equals(r.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (roster is null)
        {
            ClanMembers.Clear();
            HasClanMembers = false;
            RosterText = string.Empty;
            RosterHint =
                "The public API does not expose clan membership. Optima discovers members it has seen on the " +
                "leaderboards below; for the full roster paste member ids or names (one per line, ids are exact " +
                "and survive renames) and save — the next open resolves everyone automatically.";
        }
        else
        {
            RosterText = string.Join(Environment.NewLine, roster.Tokens);
        }

        // Members already known from leaderboard enrichment show immediately.
        var discovered = _profileCache.Values
            .Where(p => string.Equals(p.ClanTag, tag, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Mmr ?? 0)
            .ToList();
        if (discovered.Count > 0)
        {
            ClanMembers.Clear();
            var index = 0;
            foreach (var profile in discovered)
            {
                var row = new ClanMemberRow(profile.UserId.ToString(CultureInfo.InvariantCulture), index++);
                var season = profile.CurrentSeason;
                row.Show(profile, season?.Ranked.Kills ?? 0, season?.Ranked.Deaths ?? 0);
                ClanMembers.Add(row);
            }
            HasClanMembers = true;
            RosterHint = discovered.Count + " member(s) discovered from the leaderboards so far.";
        }
        if (roster is not null)
        {
            await ResolveRosterAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task SaveAndResolveRosterAsync()
    {
        var tokens = RosterText
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tokens.Count == 0)
        {
            RosterHint = "Paste at least one account id or in-game name.";
            return;
        }
        if (tokens.Count > 50)
        {
            RosterHint = "The batch API answers at most 50 profiles per call; only the first 50 were saved.";
            tokens = tokens.Take(50).ToList();
        }

        var tag = ExtractTag(ClanDetailTitle);
        await _settings.UpdateSettingsAsync(s => s with
        {
            ClanRosters = [.. s.ClanRosters.Where(r => !string.Equals(r.Tag, tag, StringComparison.OrdinalIgnoreCase)),
                new ClanRoster(tag, [.. tokens])],
        }).ConfigureAwait(true);
        RosterHint = $"saved {tokens.Count} member token(s) for [{tag}].";
        await ResolveRosterAsync().ConfigureAwait(true);
    }

    /// <summary>Resolves every pasted token (id or name) into an enriched member row.</summary>
    [RelayCommand]
    private async Task ResolveRosterAsync()
    {
        if (IsResolvingRoster)
        {
            return;
        }
        var tokens = RosterText
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
        if (tokens.Count == 0)
        {
            return;
        }
        IsResolvingRoster = true;
        try
        {
            ClanMembers.Clear();
            var rows = tokens.Select((t, i) => new ClanMemberRow(t, i)).ToList();
            foreach (var row in rows)
            {
                ClanMembers.Add(row);
            }
            HasClanMembers = true;

            var found = new Dictionary<string, CopsPlayerProfile>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var profile in await ResolveTokensAsync(tokens).ConfigureAwait(true))
                {
                    found[profile.UserId.ToString(CultureInfo.InvariantCulture)] = profile;
                    found[profile.Name] = profile;
                    CacheProfile(profile);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Clan roster batch lookup failed");
            }

            foreach (var row in rows)
            {
                if (found.TryGetValue(row.Token, out var profile))
                {
                    var season = profile.CurrentSeason;
                    row.Show(profile, season?.Ranked.Kills ?? 0, season?.Ranked.Deaths ?? 0);
                }
                else
                {
                    row.MarkMissing("not found (check the spelling, or the player left the clan)");
                }
            }
        }
        finally
        {
            IsResolvingRoster = false;
        }
    }

    private static string ExtractTag(string title)
    {
        var start = title.IndexOf('[');
        var end = title.IndexOf(']');
        return start >= 0 && end > start ? title[(start + 1)..end] : title;
    }
}
