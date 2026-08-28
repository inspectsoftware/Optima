using Optima.App.Diagnostics;
using Optima.App.ViewModels;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Detection;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Core.Recovery;
using Optima.Driver;
using Optima.Driver.Providers;
using Optima.Monitoring;
using Optima.Monitoring.Metrics;
using Optima.Monitoring.Network;
using Optima.Platform.Windows.Elevation;
using Optima.Platform.Windows.Launchers;
using Optima.Platform.Windows.Probes;
using Optima.Platform.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Optima.App;

/// <summary>Composition root: the UI / services / platform / driver split of §25 wired together.</summary>
public static class AppServices
{
    public static void Register(IServiceCollection services, AppPaths paths)
    {
        // ---- Configuration ----
        services.AddSingleton(paths);
        services.AddSingleton<JsonStore>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ProfileService>();

        // Rule provider delegate shared by detection, process monitoring and launching (§29).
        services.AddSingleton<Func<CancellationToken, Task<DetectionRules>>>(sp =>
            ct => sp.GetRequiredService<SettingsService>().GetDetectionRulesAsync(ct));

        // ---- Platform probes + services ----
        services.AddSingleton<IRegistryProbe, WindowsRegistryProbe>();
        services.AddSingleton<IFileSystemProbe, WindowsFileSystemProbe>();
        services.AddSingleton<IProcessProbe, WindowsProcessProbe>();
        services.AddSingleton<IShortcutResolver, LnkShortcutResolver>();
        services.AddSingleton<IGameDetector>(sp => new GameDetectionEngine(
            sp.GetRequiredService<IRegistryProbe>(),
            sp.GetRequiredService<IFileSystemProbe>(),
            sp.GetRequiredService<IProcessProbe>(),
            sp.GetRequiredService<IShortcutResolver>(),
            sp.GetRequiredService<Func<CancellationToken, Task<DetectionRules>>>(),
            Environment.ExpandEnvironmentVariables,
            sp.GetRequiredService<ILogger<GameDetectionEngine>>()));

        services.AddSingleton<IDisplayService, WindowsDisplayService>();
        services.AddSingleton<ISystemInfoService, WindowsSystemInfoService>();
        services.AddSingleton<IPowerProfileService, WindowsPowerProfileService>();
        services.AddSingleton<IProcessMonitor, WindowsProcessMonitor>();
        services.AddSingleton<IProcessOptimizer, WindowsProcessOptimizer>();
        services.AddSingleton<IGameTerminator, WindowsGameTerminator>();
        services.AddSingleton<ITweakService, WindowsTweakService>();
        services.AddSingleton<IBackgroundCleanupService, WindowsBackgroundCleanupService>();
        services.AddSingleton<PnpDeviceLocator>();
        services.AddSingleton<IElevationBroker, ElevationBrokerClient>();

        // ---- Virtual display providers (§6) ----
        services.AddSingleton<MttVddProvider>();
        services.AddSingleton<MockVirtualDisplayProvider>();
        services.AddSingleton<SelectingVirtualDisplayProvider>();
        services.AddSingleton<IVirtualDisplayProvider>(sp => sp.GetRequiredService<SelectingVirtualDisplayProvider>());
        services.AddSingleton<IDriverInstaller, VddDriverInstaller>();

        // ---- Recovery / safety (§18/§19) ----
        services.AddSingleton<IRecoveryService, RecoveryService>();

        // ---- Launch strategies (§5) ----
        services.AddSingleton<IGameLauncher, ProtocolUriLauncher>();
        services.AddSingleton<IGameLauncher, BootstrapperExeLauncher>();
        services.AddSingleton<IGameLauncher, ShortcutLauncher>();
        services.AddSingleton<IGameLauncher, CustomCommandLauncher>();
        services.AddSingleton<LaunchOrchestrator>();
        services.AddSingleton<GameWatchService>();

        // ---- Monitoring (§12-14) ----
        services.AddSingleton<IPerformanceMonitor, HardwareMonitor>();
        services.AddSingleton<EtwMetricsProviderClient>();
        services.AddSingleton(_ => new MockMetricsProvider());
        // Developer setting: swap the ETW provider for the deterministic mock (restart applies it).
        services.AddSingleton<IPerformanceMetricsProvider>(sp =>
            sp.GetRequiredService<SettingsService>().GetSettingsAsync().GetAwaiter().GetResult().UseMockMetricsProvider
                ? sp.GetRequiredService<MockMetricsProvider>()
                : sp.GetRequiredService<EtwMetricsProviderClient>());
        services.AddSingleton<ISessionStore, SqliteSessionStore>();
        services.AddSingleton<IGameWindowLocator, WindowsGameWindowLocator>();
        services.AddSingleton<IRemoteEndpointSource, WindowsEndpointDiscovery>();
        services.AddSingleton<INetworkQualityMonitor, NetworkQualityMonitor>();

        // ---- Diagnostics (§15/§16) ----
        services.AddSingleton<IDiagnosticCheck, VirtualizationCheck>();
        services.AddSingleton<IDiagnosticCheck, WindowsHypervisorCheck>();
        services.AddSingleton<IDiagnosticCheck, GooglePlayGamesCheck>();
        services.AddSingleton<IDiagnosticCheck, CriticalOpsCheck>();
        services.AddSingleton<IDiagnosticCheck, VirtualDriverCheck>();
        services.AddSingleton<IDiagnosticCheck, RefreshRateCheck>();
        services.AddSingleton<IDiagnosticCheck, GpuDriverCheck>();
        services.AddSingleton<IDiagnosticCheck, DiskSpaceCheck>();
        services.AddSingleton<IDiagnosticCheck, AdminPermissionsCheck>();

        // ---- View models ----
        services.AddSingleton<StatusViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<PlayViewModel>();
        services.AddSingleton<PerformanceViewModel>();
        services.AddSingleton<SessionsViewModel>();
        services.AddSingleton<GuidedBenchmarkViewModel>();
        services.AddSingleton<DisplayViewModel>();
        services.AddSingleton<SystemViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DeveloperViewModel>();
        services.AddSingleton<UpdateLogViewModel>();
        services.AddSingleton<OverlayViewModel>();
    }
}
