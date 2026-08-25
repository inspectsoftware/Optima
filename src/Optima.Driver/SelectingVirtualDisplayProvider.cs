using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Driver.Providers;
using Microsoft.Extensions.Logging;

namespace Optima.Driver;

/// <summary>
/// Routes IVirtualDisplayProvider calls to the configured provider (§6): "Auto" probes the real
/// driver and falls back to the mock; "MttVdd" / "Mock" force a specific one. The choice is made
/// once per app run (re-evaluated when settings change).
/// </summary>
public sealed class SelectingVirtualDisplayProvider : IVirtualDisplayProvider
{
    private readonly MttVddProvider _real;
    private readonly MockVirtualDisplayProvider _mock;
    private readonly SettingsService _settings;
    private readonly ILogger<SelectingVirtualDisplayProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IVirtualDisplayProvider? _selected;

    public SelectingVirtualDisplayProvider(
        MttVddProvider real,
        MockVirtualDisplayProvider mock,
        SettingsService settings,
        ILogger<SelectingVirtualDisplayProvider> logger)
    {
        _real = real;
        _mock = mock;
        _settings = settings;
        _logger = logger;
        _settings.SettingsChanged += (_, _) => _selected = null; // re-probe after settings edits
    }

    public string Name => _selected?.Name ?? "(not selected yet)";

    public async Task<IVirtualDisplayProvider> GetActiveProviderAsync(CancellationToken ct = default)
    {
        if (_selected is { } chosen)
        {
            return chosen;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_selected is not null)
            {
                return _selected;
            }

            var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(false);
            _selected = settings.VirtualDisplayProvider.ToUpperInvariant() switch
            {
                "MTTVDD" => _real,
                "MOCK" => _mock,
                _ => await _real.IsAvailableAsync(ct).ConfigureAwait(false) ? _real : _mock,
            };
            _logger.LogInformation("Virtual display provider selected: {Provider}", _selected.Name);
            return _selected;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).IsAvailableAsync(ct).ConfigureAwait(false);

    public async Task<DriverCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).GetCapabilitiesAsync(ct).ConfigureAwait(false);

    public async Task InitializeAsync(CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).InitializeAsync(ct).ConfigureAwait(false);

    public async Task CreateDisplayAsync(CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).CreateDisplayAsync(ct).ConfigureAwait(false);

    public async Task EnableDisplayAsync(CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).EnableDisplayAsync(ct).ConfigureAwait(false);

    public async Task DisableDisplayAsync(CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).DisableDisplayAsync(ct).ConfigureAwait(false);

    public async Task SetResolutionAsync(int width, int height, CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).SetResolutionAsync(width, height, ct).ConfigureAwait(false);

    public async Task SetRefreshRateAsync(int refreshRate, CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).SetRefreshRateAsync(refreshRate, ct).ConfigureAwait(false);

    public async Task SetModeAsync(DisplayMode mode, CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).SetModeAsync(mode, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).GetSupportedModesAsync(ct).ConfigureAwait(false);

    public async Task<DisplayMode?> GetCurrentModeAsync(CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).GetCurrentModeAsync(ct).ConfigureAwait(false);

    public async Task<bool> IsDisplayActiveAsync(CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).IsDisplayActiveAsync(ct).ConfigureAwait(false);

    public async Task<DisplayInfo?> GetDisplayInfoAsync(CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).GetDisplayInfoAsync(ct).ConfigureAwait(false);

    public async Task RestoreOriginalStateAsync(CancellationToken ct = default)
        => await (await GetActiveProviderAsync(ct).ConfigureAwait(false)).RestoreOriginalStateAsync(ct).ConfigureAwait(false);
}
