using System.Diagnostics;
using Microsoft.Win32;
using Optima.Core.Abstractions;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>
/// Core parking through the documented power-setting API, and the per-app graphics preference
/// through the same registry value the Windows Settings app writes. Neither needs administrator rights.
/// </summary>
public sealed class WindowsGameExtras : IGameExtras
{
    private const string GpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string HighPerformance = "GpuPreference=2;";
    private const uint AllCores = 100;

    private readonly ILogger<WindowsGameExtras> _logger;

    public WindowsGameExtras(ILogger<WindowsGameExtras> logger)
    {
        _logger = logger;
    }

    public CoreParkingSnapshot? KeepCoresUnparked()
    {
        var scheme = PowerNative.GetActiveScheme();
        var (ac, dc) = PowerNative.ReadMinUnparkedCores(scheme);
        if (ac >= AllCores && dc >= AllCores)
        {
            return null;
        }
        PowerNative.WriteMinUnparkedCores(scheme, AllCores, AllCores);
        // A plan's values are only read when it becomes active; re-activating applies them now.
        PowerNative.SetActiveScheme(scheme);
        _logger.LogInformation("Core parking floor raised to 100% on plan {Scheme} (was {Ac}% on mains, {Dc}% on battery)", scheme, ac, dc);
        return new CoreParkingSnapshot(scheme, ac, dc);
    }

    public void RestoreCoreParking(CoreParkingSnapshot snapshot)
    {
        PowerNative.WriteMinUnparkedCores(snapshot.Scheme, snapshot.AcMinCoresPercent, snapshot.DcMinCoresPercent);
        if (PowerNative.GetActiveScheme() == snapshot.Scheme)
        {
            PowerNative.SetActiveScheme(snapshot.Scheme);
        }
        _logger.LogInformation("Core parking floor restored on plan {Scheme}", snapshot.Scheme);
    }

    public string? GetProcessPath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public bool IsGpuHighPerformance(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(GpuPreferencesKey);
        return key?.GetValue(executablePath) is string data
            && data.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase);
    }

    public void SetGpuHighPerformance(string executablePath, bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuPreferencesKey);
            key.SetValue(executablePath, HighPerformance, RegistryValueKind.String);
            _logger.LogInformation("Graphics preference set to High performance for {Path}", executablePath);
        }
        else if (IsGpuHighPerformance(executablePath))
        {
            using var key = Registry.CurrentUser.OpenSubKey(GpuPreferencesKey, writable: true);
            key?.DeleteValue(executablePath, throwOnMissingValue: false);
            _logger.LogInformation("Graphics preference cleared for {Path}", executablePath);
        }
    }
}
