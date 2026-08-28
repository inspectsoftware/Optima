using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Optima.Core.Models;

namespace Optima.Core.Configuration;

/// <summary>
/// Content identity for a launch profile (§13). Profiles are identified by name everywhere
/// else, but names can be renamed and profiles edited in place, which silently rewrites what
/// past sessions meant. Session rows therefore store a hash of the settings that actually
/// affect a session: a renamed-but-identical profile trends together, and an edited profile
/// visibly breaks the trend.
/// </summary>
public static class LaunchProfileHasher
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>12 hex characters of SHA-256 over the profile's display + performance settings.</summary>
    public static string ComputeHash(LaunchProfile profile)
    {
        // Anonymous type fixes the property order, so serialization is canonical by construction.
        var canonical = new
        {
            display = new
            {
                virtualDisplay = profile.Display.VirtualDisplay,
                width = profile.Display.Width,
                height = profile.Display.Height,
                refreshRate = profile.Display.RefreshRate,
                makePrimary = profile.Display.MakePrimary,
            },
            performance = new
            {
                powerPlan = (int)profile.Performance.PowerPlan,
                priority = (int)profile.Performance.Priority,
                disablePowerThrottling = profile.Performance.DisablePowerThrottling,
                cpuAffinityMask = profile.Performance.CpuAffinityMask,
                cleanup = profile.Performance.CleanupProcessNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
            },
        };
        var json = JsonSerializer.Serialize(canonical, CanonicalOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
