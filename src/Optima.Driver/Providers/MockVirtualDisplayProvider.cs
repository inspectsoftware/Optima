using Optima.Core.Abstractions;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Driver.Providers;

/// <summary>Fully functional in-memory provider (§6/§30): a complete state machine with configurable failure injection.</summary>
public sealed class MockVirtualDisplayProvider : VirtualDisplayProviderBase
{
    private readonly ILogger<MockVirtualDisplayProvider> _logger;
    private readonly object _lock = new();

    private bool _initialized;
    private bool _created;
    private bool _enabled;
    private bool _enabledInitially;
    private DisplayMode _mode = new(1920, 1080, 60);
    private DisplayMode _initialMode = new(1920, 1080, 60);

    public MockVirtualDisplayProvider(ILogger<MockVirtualDisplayProvider> logger)
    {
        _logger = logger;
    }

    public string? FailOperation { get; set; }

    public IReadOnlyList<DisplayMode> Modes { get; set; } =
    [
        new(1920, 1080, 60), new(1920, 1080, 144), new(1920, 1080, 165), new(1920, 1080, 240),
        new(2560, 1440, 60), new(2560, 1440, 144), new(2560, 1440, 165), new(2560, 1440, 240),
    ];

    public override string Name => "Mock virtual display";

    public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public override Task<DriverCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
        => Task.FromResult(new DriverCapabilities
        {
            SupportsCustomModes = true,
            SupportsRefreshRateChange = true,
            SupportsGpuPinning = false,
            SupportsEnableDisable = true,
            RequiresElevation = false,
        });

    public override Task InitializeAsync(CancellationToken ct = default)
    {
        MaybeFail(nameof(InitializeAsync));
        lock (_lock)
        {
            _initialized = true;
            _enabledInitially = _enabled;
            _initialMode = _mode;
        }
        _logger.LogInformation("Mock virtual display initialized");
        return Task.CompletedTask;
    }

    public override Task CreateDisplayAsync(CancellationToken ct = default)
    {
        MaybeFail(nameof(CreateDisplayAsync));
        EnsureInitialized();
        lock (_lock)
        {
            _created = true;
        }
        _logger.LogInformation("Virtual display created (mock)");
        return Task.CompletedTask;
    }

    public override Task EnableDisplayAsync(CancellationToken ct = default)
    {
        MaybeFail(nameof(EnableDisplayAsync));
        EnsureInitialized();
        lock (_lock)
        {
            _created = true;
            _enabled = true;
        }
        _logger.LogInformation("Virtual display enabled (mock)");
        return Task.CompletedTask;
    }

    public override Task DisableDisplayAsync(CancellationToken ct = default)
    {
        MaybeFail(nameof(DisableDisplayAsync));
        lock (_lock)
        {
            _enabled = false;
        }
        _logger.LogInformation("Virtual display disabled (mock)");
        return Task.CompletedTask;
    }

    public override Task SetModeAsync(DisplayMode mode, CancellationToken ct = default)
    {
        MaybeFail(nameof(SetModeAsync));
        lock (_lock)
        {
            if (!_enabled)
            {
                throw new InvalidOperationException("The virtual display is not enabled.");
            }
            if (!Modes.Contains(mode))
            {
                throw OptimaException.From("DISPLAY_MODE_UNSUPPORTED",
                    $"{mode} is not supported by the mock driver.",
                    "The requested mode is not in the mock driver's mode list.");
            }
            _mode = mode;
        }
        _logger.LogInformation("Resolution applied (mock): {Mode}", mode);
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct = default)
        => Task.FromResult(Modes);

    public override Task<DisplayMode?> GetCurrentModeAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<DisplayMode?>(_enabled ? _mode : null);
        }
    }

    public override Task<bool> IsDisplayActiveAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_created && _enabled);
        }
    }

    public override Task<DisplayInfo?> GetDisplayInfoAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<DisplayInfo?>(_enabled
                ? new DisplayInfo
                {
                    DeviceName = @"\\.\MOCKDISPLAY1",
                    FriendlyName = "Mock Virtual Display",
                    AdapterName = "Mock Virtual Display Adapter",
                    CurrentMode = _mode,
                    IsActive = true,
                }
                : null);
        }
    }

    public override Task RestoreOriginalStateAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _enabled = _enabledInitially;
            _mode = _initialMode;
        }
        _logger.LogInformation("Mock virtual display state restored");
        return Task.CompletedTask;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("InitializeAsync must be called first.");
        }
    }

    private void MaybeFail(string operation)
    {
        if (string.Equals(FailOperation, operation, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Mock failure injected in {operation}.");
        }
    }
}
