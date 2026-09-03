using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Optima.Core.Models;

namespace Optima.Core.Configuration;

/// <summary>Content identity for a launch profile (§13).</summary>
public static class LaunchProfileHasher
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
    };

    public static string ComputeHash(LaunchProfile profile)
    {
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
