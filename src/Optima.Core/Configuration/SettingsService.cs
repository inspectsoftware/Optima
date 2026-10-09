using Optima.Core.Linking;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Configuration;

/// <summary>Loads/saves config.json and detection.json with in-memory caching.</summary>
public sealed class SettingsService
{
    private readonly AppPaths _paths;
    private readonly JsonStore _store;
    private readonly ILogger<SettingsService> _logger;
    private volatile AppSettings? _settings;
    private DetectionRules? _rules;
    private readonly SemaphoreSlim _updateGate = new(1, 1);

    // True while the settings in memory hold something the file does not: what the load-time
    // migration changed, or a first start with no file at all. The next update is then written
    // even when it changes nothing itself.
    private volatile bool _unsaved;

    public SettingsService(AppPaths paths, JsonStore store, ILogger<SettingsService> logger)
    {
        _paths = paths;
        _store = store;
        _logger = logger;
    }

    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>
    /// The last loaded settings, or null before the first load. Synchronous readers (the theme
    /// before first paint, presence composition, DI factories) use this snapshot instead of
    /// blocking a thread pool thread on <see cref="GetSettingsAsync"/>.
    /// </summary>
    public AppSettings? Current => _settings;

    public async Task<AppSettings> GetSettingsAsync(CancellationToken ct = default)
        => _settings ??= Loaded(await _store.LoadAsync<AppSettings>(_paths.ConfigFile, ct).ConfigureAwait(false));

    /// <summary>
    /// Synchronous load for startup work that has to finish before the first paint. Cheaper than
    /// blocking on <see cref="GetSettingsAsync"/>: one small file read, no thread pool hop.
    /// </summary>
    public AppSettings GetSettings()
        => _settings ??= Loaded(_store.Load<AppSettings>(_paths.ConfigFile));

    private AppSettings Loaded(AppSettings? read)
    {
        var migrated = Migrate(read ?? new AppSettings());
        _unsaved = !ReferenceEquals(read, migrated);
        return migrated;
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        settings = Migrate(settings);
        _settings = settings;
        await _store.SaveAsync(_paths.ConfigFile, settings, ct).ConfigureAwait(false);
        _unsaved = false;
        SettingsChanged?.Invoke(this, settings);
    }

    public async Task<AppSettings> UpdateSettingsAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
    {
        // One update at a time: two overlapping updates would each start from the same snapshot and
        // the later save would drop the other's change.
        AppSettings updated;
        await _updateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await GetSettingsAsync(ct).ConfigureAwait(false);
            updated = Migrate(mutate(current));
            // An update that changes nothing is not a save. The app makes several at every start
            // (re-selecting the profile that is already selected, for one), and each of them
            // rewrote the file and sent every listener off to re-read what had not changed.
            if (!_unsaved && updated.Equals(current))
            {
                return current;
            }
            // Kept only once it is on disk: a save that failed must leave the same update to be
            // tried again, not judged "nothing changed" against a value that was never written.
            await _store.SaveAsync(_paths.ConfigFile, updated, ct).ConfigureAwait(false);
            _settings = updated;
            _unsaved = false;
        }
        finally
        {
            _updateGate.Release();
        }
        // Raised outside the gate so a listener that updates settings in turn cannot wait on itself.
        SettingsChanged?.Invoke(this, updated);
        return updated;
    }

    public async Task<DetectionRules> GetDetectionRulesAsync(CancellationToken ct = default)
    {
        if (_rules is not null)
        {
            return _rules;
        }

        var overridden = await _store.LoadAsync<DetectionRules>(_paths.DetectionFile, ct).ConfigureAwait(false);
        if (overridden is not null)
        {
            _logger.LogInformation("Using detection rule overrides from {Path}", _paths.DetectionFile);
        }
        return _rules = overridden ?? new DetectionRules();
    }

    public async Task SaveDetectionRulesAsync(DetectionRules rules, CancellationToken ct = default)
    {
        _rules = rules;
        await _store.SaveAsync(_paths.DetectionFile, rules, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The address every configuration written before the community bot existed carries. It was the
    /// built-in default rather than a choice, and no user's machine has a bot on it, so a claim against
    /// it fails with "Nothing answered" and the only way to link was to know and type the real address.
    /// </summary>
    private const string LegacyLocalBotAddress = "http://127.0.0.1:5099";

    /// <summary>
    /// The one-time move off the old built-in bot address. It runs on every load and save: an unmigrated
    /// configuration means the address was never chosen, so the legacy local default becomes the community
    /// bot and the user only ever types the link code. The flag it sets is what lets a self-hoster type the
    /// local address back in and keep it, instead of having it replaced on every start.
    /// </summary>
    private static AppSettings Migrate(AppSettings settings)
    {
        if (settings.DiscordBotUrlMigrated)
        {
            return settings;
        }

        return settings with
        {
            DiscordBotUrl = BotLinkClient.NormalizeBaseUrl(settings.DiscordBotUrl) == LegacyLocalBotAddress
                ? BotLinkClient.DefaultBaseUrl
                : settings.DiscordBotUrl,
            DiscordBotUrlMigrated = true,
        };
    }
}
