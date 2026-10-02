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
        => _settings ??= await _store.LoadAsync<AppSettings>(_paths.ConfigFile, ct).ConfigureAwait(false) ?? new AppSettings();

    /// <summary>
    /// Synchronous load for startup work that has to finish before the first paint. Cheaper than
    /// blocking on <see cref="GetSettingsAsync"/>: one small file read, no thread pool hop.
    /// </summary>
    public AppSettings GetSettings()
        => _settings ??= _store.Load<AppSettings>(_paths.ConfigFile) ?? new AppSettings();

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        _settings = settings;
        await _store.SaveAsync(_paths.ConfigFile, settings, ct).ConfigureAwait(false);
        SettingsChanged?.Invoke(this, settings);
    }

    public async Task<AppSettings> UpdateSettingsAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
    {
        var updated = mutate(await GetSettingsAsync(ct).ConfigureAwait(false));
        await SaveSettingsAsync(updated, ct).ConfigureAwait(false);
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
}
