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

    /// <summary>The API rejects a profile batch larger than this (it also caps the request itself).</summary>
    private const int MaxBatchTokens = 50;

    /// <summary>
    /// Ceiling on the discovery cache. Every leaderboard visit enriches up to ten more players, so an
    /// unbounded map grew for as long as the page was used; the oldest entries fall out instead.
    /// </summary>
    private const int MaxCachedProfileKeys = 800;

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
    private readonly Queue<string> _profileCacheOrder = new();

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

    // The filtered tables are materialised once per filter or data change. They used to be computed
    // properties, so every read by the bindings re-ran Where/Take/ToList, and every keystroke
    // re-raised the change notification that made WPF read them again.
    private IReadOnlyList<ExplorePlayerRow> _visiblePlayers = [];
    private IReadOnlyList<ExploreClanRow> _visibleClans = [];
    private int _visiblePlayerCount;
    private int _visibleClanCount;
    private string _totalCountText = string.Empty;

    /// <summary>Rows currently shown for player tabs (search-filtered slice of PlayerRows).</summary>
    public IReadOnlyList<ExplorePlayerRow> VisiblePlayerRows => _visiblePlayers;

    /// <summary>Rows currently shown for the clan tab.</summary>
    public IReadOnlyList<ExploreClanRow> VisibleClanRows => _visibleClans;

    public string TotalCountText => _totalCountText;

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

    /// <summary>The refresh button and the timer go to the network; a tab switch may reuse a fresh page.</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default) => await LoadAsync(refresh: true, ct).ConfigureAwait(true);

    private async Task LoadAsync(bool refresh, CancellationToken ct = default)
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
                await LoadClansAsync(refresh, ct).ConfigureAwait(true);
            }
            else
            {
                await LoadPlayersAsync(refresh, ct).ConfigureAwait(true);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadPlayersAsync(bool refresh, CancellationToken ct)
    {
        StatusText = "loading leaderboard…";
        var endpoint = IsEliteTab ? "elite" : IsRankedTab ? "ranked" : "kills";
        var (rows, problem) = await _api.GetLeaderboardAsync(endpoint, CopsLeaderboardParser.ParsePlayers, ct, refresh).ConfigureAwait(true);
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
            RememberProfile(profile.Name, profile);
        }
        if (profile.UserId > 0)
        {
            RememberProfile(profile.UserId.ToString(CultureInfo.InvariantCulture), profile);
        }
    }

    /// <summary>
    /// Caches a profile under one key, dropping the oldest keys once the map is at its ceiling. The
    /// enrichment is best effort: a profile that falls out is looked up again the next time it is
    /// needed, which is cheaper than a cache that grows for the whole session.
    /// </summary>
    private void RememberProfile(string key, CopsPlayerProfile profile)
    {
        if (!_profileCache.ContainsKey(key))
        {
            _profileCacheOrder.Enqueue(key);
        }
        _profileCache[key] = profile;

        while (_profileCacheOrder.Count > MaxCachedProfileKeys)
        {
            _profileCache.Remove(_profileCacheOrder.Dequeue());
        }
    }

    private async Task LoadClansAsync(bool refresh, CancellationToken ct)
    {
        StatusText = "loading clans…";
        var (rows, problem) = await _api.GetLeaderboardAsync("clan", CopsLeaderboardParser.ParseClans, ct, refresh).ConfigureAwait(true);
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
    /// Rebuilds both tables from the search term and re-raises what they bind to. The data is
    /// walked once here, not once per row per binding read, and rows are no longer individually
    /// flagged and notified.
    /// </summary>
    private void ApplyFilters()
    {
        var term = SearchText.Trim();

        _visiblePlayerCount = 0;
        var players = new List<ExplorePlayerRow>(Math.Min(PlayerRows.Count, MaxVisibleRows));
        foreach (var row in PlayerRows)
        {
            if (!MatchesPlayer(row, term))
            {
                continue;
            }
            _visiblePlayerCount++;
            if (players.Count < MaxVisibleRows)
            {
                players.Add(row);
            }
        }

        _visibleClanCount = 0;
        var clans = new List<ExploreClanRow>(Math.Min(ClanRows.Count, MaxVisibleRows));
        foreach (var row in ClanRows)
        {
            if (!MatchesClan(row, term))
            {
                continue;
            }
            _visibleClanCount++;
            if (clans.Count < MaxVisibleRows)
            {
                clans.Add(row);
            }
        }

        _visiblePlayers = players;
        _visibleClans = clans;
        _totalCountText = IsClansTab
            ? $"{_visibleClanCount} of {ClanRows.Count} clans"
            : $"{_visiblePlayerCount} of {PlayerRows.Count} players";

        OnPropertyChanged(nameof(VisiblePlayerRows));
        OnPropertyChanged(nameof(VisibleClanRows));
        OnPropertyChanged(nameof(TotalCountText));
    }

    private static bool MatchesPlayer(ExplorePlayerRow row, string term)
        => term.Length == 0
            || row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Rank.ToString(CultureInfo.InvariantCulture).StartsWith(term, StringComparison.Ordinal);

    private static bool MatchesClan(ExploreClanRow row, string term)
        => term.Length == 0
            || row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Tag.Contains(term, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void ShowTab(string tab)
    {
        IsEliteTab = tab == "elite";
        IsRankedTab = tab == "ranked";
        IsCasualTab = tab == "casual";
        IsClansTab = tab == "clans";
        SearchText = string.Empty;
        // Not a forced refresh: a page fetched moments ago (or the score of a tab the user is
        // flipping through) is reused instead of costing a request per click.
        _ = LoadAsync(refresh: false);
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
    /// Resolves id/name tokens to profiles in batches. Two API quirks shape this: a batch is capped at
    /// 50 tokens, and a batch containing one unknown token fails whole (HTTP 500). A batch that answers
    /// short is therefore halved recursively instead of retried token by token — with a few unknown
    /// lines that is a handful of requests rather than one request per line, and a roster longer than
    /// the cap is now resolved completely instead of silently truncated.
    /// </summary>
    private async Task<List<CopsPlayerProfile>> ResolveTokensAsync(IReadOnlyList<string> tokens)
    {
        var ids = new List<long>();
        var names = new List<string>();
        foreach (var token in tokens)
        {
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                if (id > 0)
                {
                    ids.Add(id);
                }
            }
            else if (token.Trim().Length > 0)
            {
                names.Add(token.Trim());
            }
        }

        var results = new List<CopsPlayerProfile>();
        results.AddRange(await ResolveBatchedAsync(
            ids.Distinct().ToList(),
            batch => _api.GetProfilesByIdsAsync(batch)).ConfigureAwait(true));
        results.AddRange(await ResolveBatchedAsync(
            names.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            batch => _api.GetProfilesByNamesAsync(batch)).ConfigureAwait(true));
        return results;
    }

    private static async Task<List<CopsPlayerProfile>> ResolveBatchedAsync<TToken>(
        List<TToken> tokens, Func<IReadOnlyList<TToken>, Task<IReadOnlyList<CopsPlayerProfile>>> fetch)
    {
        var results = new List<CopsPlayerProfile>();
        for (var offset = 0; offset < tokens.Count; offset += MaxBatchTokens)
        {
            var batch = tokens.GetRange(offset, Math.Min(MaxBatchTokens, tokens.Count - offset));
            results.AddRange(await ResolveSplitAsync(batch, fetch).ConfigureAwait(true));
        }
        return results;
    }

    /// <summary>Fetches a batch whole; halves it only when the answer came back short.</summary>
    private static async Task<List<CopsPlayerProfile>> ResolveSplitAsync<TToken>(
        List<TToken> batch, Func<IReadOnlyList<TToken>, Task<IReadOnlyList<CopsPlayerProfile>>> fetch)
    {
        IReadOnlyList<CopsPlayerProfile> profiles;
        try
        {
            profiles = await fetch(batch).ConfigureAwait(true);
        }
        catch (Exception)
        {
            profiles = [];
        }

        // Everyone answered, or there is nothing left to split: an unknown token is simply absent.
        if (profiles.Count >= batch.Count || batch.Count == 1)
        {
            return [.. profiles];
        }

        var half = batch.Count / 2;
        var results = await ResolveSplitAsync(batch.GetRange(0, half), fetch).ConfigureAwait(true);
        results.AddRange(await ResolveSplitAsync(batch.GetRange(half, batch.Count - half), fetch).ConfigureAwait(true));
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
