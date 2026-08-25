using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Configuration;

/// <summary>Manages launch profiles (§7/§8/§22): built-in presets + user profiles, import/export.</summary>
public sealed class ProfileService
{
    private readonly AppPaths _paths;
    private readonly JsonStore _store;
    private readonly ILogger<ProfileService> _logger;
    private List<LaunchProfile>? _profiles;

    public ProfileService(AppPaths paths, JsonStore store, ILogger<ProfileService> logger)
    {
        _paths = paths;
        _store = store;
        _logger = logger;
    }

    public static IReadOnlyList<LaunchProfile> BuiltInProfiles { get; } =
    [
        new LaunchProfile
        {
            Name = "Default",
            Description = "No system changes. Launches the game exactly as Google Play Games would.",
            IsBuiltIn = true,
            Display = new DisplayProfile { VirtualDisplay = false },
            Performance = new PerformanceProfile(),
        },
        new LaunchProfile
        {
            Name = "Balanced",
            Description = "Moderate optimization with minimal system changes: high-performance power plan and above-normal process priority.",
            IsBuiltIn = true,
            Display = new DisplayProfile { VirtualDisplay = false },
            Performance = new PerformanceProfile
            {
                PowerPlan = PowerPlanKind.HighPerformance,
                Priority = ProcessPriorityLevel.AboveNormal,
            },
        },
        new LaunchProfile
        {
            Name = "Competitive 1080p240",
            Description = "Latency-first: 1920x1080 @ 240 Hz virtual display, high process priority, power throttling off, high-performance power plan.",
            IsBuiltIn = true,
            Display = new DisplayProfile { VirtualDisplay = true, Width = 1920, Height = 1080, RefreshRate = 240 },
            Performance = new PerformanceProfile
            {
                PowerPlan = PowerPlanKind.HighPerformance,
                Priority = ProcessPriorityLevel.High,
                DisablePowerThrottling = true,
            },
        },
        new LaunchProfile
        {
            Name = "Competitive 1440p165",
            Description = "2560x1440 @ 165 Hz virtual display with the same latency-first system tuning.",
            IsBuiltIn = true,
            Display = new DisplayProfile { VirtualDisplay = true, Width = 2560, Height = 1440, RefreshRate = 165 },
            Performance = new PerformanceProfile
            {
                PowerPlan = PowerPlanKind.HighPerformance,
                Priority = ProcessPriorityLevel.High,
                DisablePowerThrottling = true,
            },
        },
    ];

    public async Task<IReadOnlyList<LaunchProfile>> GetProfilesAsync(CancellationToken ct = default)
    {
        if (_profiles is null)
        {
            var stored = await _store.LoadAsync<List<LaunchProfile>>(_paths.ProfilesFile, ct).ConfigureAwait(false) ?? [];
            // Built-ins always present and always current; user profiles follow.
            _profiles = BuiltInProfiles
                .Concat(stored.Where(p => !p.IsBuiltIn && BuiltInProfiles.All(b => b.Name != p.Name)))
                .ToList();
        }
        return _profiles;
    }

    public async Task<LaunchProfile> GetProfileAsync(string name, CancellationToken ct = default)
    {
        var profiles = await GetProfilesAsync(ct).ConfigureAwait(false);
        return profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? profiles[0];
    }

    public async Task SaveProfileAsync(LaunchProfile profile, CancellationToken ct = default)
    {
        if (profile.IsBuiltIn || BuiltInProfiles.Any(b => string.Equals(b.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Built-in profiles cannot be overwritten. Save under a new name.");
        }

        var profiles = (await GetProfilesAsync(ct).ConfigureAwait(false)).ToList();
        profiles.RemoveAll(p => !p.IsBuiltIn && string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        profiles.Add(profile);
        _profiles = profiles;
        await PersistAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Profile saved: {Profile}", profile.Name);
    }

    public async Task DeleteProfileAsync(string name, CancellationToken ct = default)
    {
        var profiles = (await GetProfilesAsync(ct).ConfigureAwait(false)).ToList();
        var removed = profiles.RemoveAll(p => !p.IsBuiltIn && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            _profiles = profiles;
            await PersistAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Profile deleted: {Profile}", name);
        }
    }

    /// <summary>Exports one profile as standalone JSON (§22).</summary>
    public async Task ExportProfileAsync(string name, string targetPath, CancellationToken ct = default)
    {
        var profile = await GetProfileAsync(name, ct).ConfigureAwait(false);
        await _store.SaveAsync(targetPath, profile with { IsBuiltIn = false }, ct).ConfigureAwait(false);
    }

    public async Task<LaunchProfile> ImportProfileAsync(string sourcePath, CancellationToken ct = default)
    {
        var profile = await _store.LoadAsync<LaunchProfile>(sourcePath, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"'{sourcePath}' does not contain a valid profile.");
        var imported = profile with { IsBuiltIn = false };
        await SaveProfileAsync(imported, ct).ConfigureAwait(false);
        return imported;
    }

    private Task PersistAsync(CancellationToken ct)
        => _store.SaveAsync(_paths.ProfilesFile, _profiles!.Where(p => !p.IsBuiltIn).ToList(), ct);
}
