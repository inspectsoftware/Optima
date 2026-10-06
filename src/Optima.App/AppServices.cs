using Optima.App.Diagnostics;
using Optima.App.ViewModels;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Detection;
using Optima.Core.Crashes;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Core.Monitoring;
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
        services.AddSingleton(paths);
        services.AddSingleton<JsonStore>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ProfileService>();

        services.AddSingleton<Func<CancellationToken, Task<DetectionRules>>>(sp =>
            ct => sp.GetRequiredService<SettingsService>().GetDetectionRulesAsync(ct));

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
        services.AddSingleton<ITimerResolution, WindowsTimerResolution>();
        services.AddSingleton<IBackgroundDemoter, WindowsBackgroundDemoter>();
        services.AddSingleton<IGameExtras, WindowsGameExtras>();
        services.AddSingleton<IGameTerminator, WindowsGameTerminator>();
        services.AddSingleton<ITweakService, WindowsTweakService>();
        services.AddSingleton<IBackgroundCleanupService, WindowsBackgroundCleanupService>();
        services.AddSingleton<PnpDeviceLocator>();
        services.AddSingleton<IElevationBroker, ElevationBrokerClient>();

        services.AddSingleton<MttVddProvider>();
        services.AddSingleton<MockVirtualDisplayProvider>();
        services.AddSingleton<SelectingVirtualDisplayProvider>();
        services.AddSingleton<IVirtualDisplayProvider>(sp => sp.GetRequiredService<SelectingVirtualDisplayProvider>());
        services.AddSingleton<IDriverInstaller, VddDriverInstaller>();

        services.AddSingleton<IRecoveryService, RecoveryService>();

        services.AddSingleton<IGameLauncher, ProtocolUriLauncher>();
        services.AddSingleton<IGameLauncher, BootstrapperExeLauncher>();
        services.AddSingleton<IGameLauncher, ShortcutLauncher>();
        services.AddSingleton<IGameLauncher, CustomCommandLauncher>();
        services.AddSingleton<LaunchOrchestrator>();
        services.AddSingleton<GamePresenceService>();
        services.AddSingleton<GameWatchService>();
        services.AddSingleton<GpgLogReader>();
        services.AddSingleton<CrashSentinel>();
        services.AddSingleton<Optima.Core.Stats.CopsApiClient>();
        services.AddSingleton<Optima.App.Services.PlayerSwitcherService>();
        services.AddSingleton<Optima.Core.Discord.DiscordArtCatalog>();
        services.AddSingleton<Optima.App.Services.DiscordPresenceService>();
        // The desktop side of the OptimaBot link handshake: the app redeems the code the user typed with
        // the Critical Ops account it already has, and the bot is the only party that sees both halves.
        services.AddSingleton<Optima.Core.Linking.BotLinkClient>();
        services.AddSingleton(sp => new Optima.Core.Launch.CrashAutoRelaunchService(
            sp.GetRequiredService<SettingsService>(),
            profile => sp.GetRequiredService<PlayViewModel>().RelaunchAfterCrashAsync(profile),
            sp.GetRequiredService<ILogger<Optima.Core.Launch.CrashAutoRelaunchService>>()));
        services.AddSingleton<Optima.Core.Launch.SessionTweakService>();
        // BOOST: holds the chosen priority on the game outside Optima's own sessions; it reads the
        // settings on every pass, so a change on the page takes effect without a restart.
        services.AddSingleton(sp => new Optima.Core.Launch.PriorityGuardService(
            sp.GetRequiredService<IProcessMonitor>(),
            sp.GetRequiredService<IProcessOptimizer>(),
            () => sp.GetRequiredService<SettingsService>().Current?.BoostEffective(),
            () => sp.GetRequiredService<LaunchOrchestrator>().IsSessionActive,
            sp.GetRequiredService<ILogger<Optima.Core.Launch.PriorityGuardService>>()));
        services.AddSingleton(sp => new Optima.Core.Launch.TimerResolutionService(
            sp.GetRequiredService<ITimerResolution>(),
            sp.GetRequiredService<IProcessMonitor>(),
            sp.GetRequiredService<GamePresenceService>(),
            () => sp.GetRequiredService<SettingsService>().Current?.BoostEffective(),
            sp.GetRequiredService<ILogger<Optima.Core.Launch.TimerResolutionService>>()));
        services.AddSingleton(sp => new Optima.Core.Launch.BackgroundDemotionService(
            sp.GetRequiredService<IBackgroundDemoter>(),
            sp.GetRequiredService<IProcessOptimizer>(),
            sp.GetRequiredService<GamePresenceService>(),
            () => sp.GetRequiredService<SettingsService>().Current?.BoostEffective(),
            sp.GetRequiredService<JsonStore>(),
            System.IO.Path.Combine(paths.Root, "boost-demoted.json"),
            sp.GetRequiredService<ILogger<Optima.Core.Launch.BackgroundDemotionService>>()));
        services.AddSingleton(sp => new Optima.Core.Launch.GameExtrasService(
            sp.GetRequiredService<IGameExtras>(),
            sp.GetRequiredService<IProcessMonitor>(),
            sp.GetRequiredService<GamePresenceService>(),
            () => sp.GetRequiredService<SettingsService>().Current?.BoostEffective(),
            path => sp.GetRequiredService<SettingsService>().UpdateSettingsAsync(s => s with { BoostGamePath = path }),
            sp.GetRequiredService<JsonStore>(),
            System.IO.Path.Combine(paths.Root, "boost-coreparking.json"),
            sp.GetRequiredService<ILogger<Optima.Core.Launch.GameExtrasService>>()));
        services.AddSingleton<Optima.Core.News.CopsNewsService>();
        services.AddSingleton<Optima.App.Services.FirstRunFixService>();
        services.AddSingleton<Optima.App.Services.RepairService>();
        // The automatic enrichment and the Sessions page's manual refresh resolve the player the exact
        // same way; a second copy of this lambda is how the two would quietly drift apart.
        async Task<Optima.Core.Stats.CopsPlayerProfile?> FetchPlayerProfile(
            IServiceProvider provider, string? ign, long? accountId, CancellationToken ct)
        {
            var lookup = await provider.GetRequiredService<Optima.Core.Stats.CopsApiClient>()
                .LookupPlayerAsync(ign, accountId, ct).ConfigureAwait(false);
            return lookup.IsFound ? lookup.Profile : null;
        }

        services.AddSingleton(sp => new Optima.Core.Stats.SessionStatsEnricher(
            sp.GetRequiredService<GamePresenceService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ISessionStore>(),
            (ign, accountId, ct) => FetchPlayerProfile(sp, ign, accountId, ct),
            sp.GetRequiredService<ILogger<Optima.Core.Stats.SessionStatsEnricher>>()));

        // The "refresh stats" button on the Sessions page: re-asks the API for one finished session
        // until the numbers move.
        services.AddSingleton(sp => new Optima.Core.Stats.SessionStatsRefresher(
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<SettingsService>(),
            (ign, accountId, ct) => FetchPlayerProfile(sp, ign, accountId, ct),
            sp.GetRequiredService<ILogger<Optima.Core.Stats.SessionStatsRefresher>>()));

        services.AddSingleton<IPerformanceMonitor, HardwareMonitor>();
        services.AddSingleton<EtwMetricsProviderClient>();
        services.AddSingleton<HardwareStreamClient>();
        services.AddSingleton(sp => new StandbyCleanerService(
            sp.GetRequiredService<IElevationBroker>(),
            sp.GetRequiredService<GamePresenceService>(),
            () => sp.GetRequiredService<SettingsService>().Current?.BoostEffective(),
            sp.GetRequiredService<ILogger<StandbyCleanerService>>()));
        services.AddSingleton(_ => new MockMetricsProvider());
        // The cached snapshot is available long before anything resolves a metrics provider; the
        // factory must not block a thread on settings just to read one flag.
        services.AddSingleton<IPerformanceMetricsProvider>(sp =>
            sp.GetRequiredService<SettingsService>().Current?.UseMockMetricsProvider == true
                ? sp.GetRequiredService<MockMetricsProvider>()
                : sp.GetRequiredService<EtwMetricsProviderClient>());
        services.AddSingleton<ISessionStore, SqliteSessionStore>();
        services.AddSingleton<IGameWindowLocator, WindowsGameWindowLocator>();
        services.AddSingleton<IRemoteEndpointSource, WindowsEndpointDiscovery>();
        services.AddSingleton<INetworkQualityMonitor, NetworkQualityMonitor>();

        services.AddSingleton<IDiagnosticCheck, VirtualizationCheck>();
        services.AddSingleton<IDiagnosticCheck, WindowsHypervisorCheck>();
        services.AddSingleton<IDiagnosticCheck, GooglePlayGamesCheck>();
        services.AddSingleton<IDiagnosticCheck, CriticalOpsCheck>();
        services.AddSingleton<IDiagnosticCheck, VirtualDriverCheck>();
        services.AddSingleton<IDiagnosticCheck, RefreshRateCheck>();
        services.AddSingleton<IDiagnosticCheck, GpuDriverCheck>();
        services.AddSingleton<IDiagnosticCheck, DiskSpaceCheck>();
        services.AddSingleton<IDiagnosticCheck, AdminPermissionsCheck>();
        services.AddSingleton<IDiagnosticCheck, OptimaOverheadCheck>();

        services.AddSingleton<StatusViewModel>();
        services.AddSingleton<PlayerStatsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<PlayViewModel>();
        services.AddSingleton<PlayGuideViewModel>();
        services.AddSingleton<PerformanceViewModel>();
        services.AddSingleton<SessionsViewModel>();
        services.AddSingleton<GuidedBenchmarkViewModel>();
        services.AddSingleton<DisplayViewModel>();
        services.AddSingleton<CompViewModel>();
        services.AddSingleton<BoostViewModel>();
        services.AddSingleton<LegalViewModel>();
        services.AddSingleton<ChecksViewModel>();
        services.AddSingleton<LogStreamViewModel>();
        services.AddSingleton<CrashesViewModel>();
        services.AddSingleton<DebugViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DeveloperViewModel>();
        services.AddSingleton<NewsViewModel>();
        services.AddSingleton<UpdateLogViewModel>();
        services.AddSingleton<OverlayViewModel>();
    }
}
