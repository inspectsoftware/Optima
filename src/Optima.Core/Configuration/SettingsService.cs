using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Configuration;

/// <summary>Loads/saves config.json and detection.json with in-memory caching.</summary>
public sealed class SettingsService
{
    private readonly AppPaths _paths;
    private readonly JsonStore _store;
    private readonly ILogger<SettingsService> _logger;
    private AppSettings? _settings;
    private DetectionRules? _rules;

    public SettingsService(AppPaths paths, JsonStore store, ILogger<SettingsService> logger)
    {
        _paths = paths;
        _store = store;
        _logger = logger;
    }

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task<AppSettings> GetSettingsAsync(CancellationToken ct = default)
        => _settings ??= await _store.LoadAsync<AppSettings>(_paths.ConfigFile, ct).ConfigureAwait(false) ?? new AppSettings();

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

    /// <summary>Detection rules: user override file when present, otherwise built-in defaults (§29).</summary>
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
