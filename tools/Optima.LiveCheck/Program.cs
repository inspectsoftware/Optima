using Optima.Core.Models;
using Optima.Driver;
using Optima.Platform.Windows.Services;
using Microsoft.Extensions.Logging;

// Optima.LiveCheck — developer smoke-check for the display stack against real hardware.
// Non-destructive: captures the display topology first, applies one mode to the virtual display,
// then restores the captured topology and verifies the round-trip. Never touches physical panels.

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(o => o.SingleLine = true));
var displayService = new WindowsDisplayService(loggerFactory.CreateLogger<WindowsDisplayService>());

Console.WriteLine("== Displays ==");
var displays = await displayService.GetDisplaysAsync();
foreach (var display in displays)
{
    Console.WriteLine($"  {display.DeviceName,-14} {display.AdapterName,-40} {display.CurrentMode,-20} " +
        $"{(display.IsPrimary ? "primary" : display.IsActive ? "active" : "inactive")}");
}

var vdd = displays.FirstOrDefault(d => d.IsActive && d.AdapterName.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase));
if (vdd is null)
{
    Console.WriteLine("No active virtual display found — enable it first (Device Manager or the app).");
    return 2;
}

Console.WriteLine($"\n== Virtual display: {vdd.DeviceName} ({vdd.AdapterName}) ==");
Console.WriteLine($"Current mode: {vdd.CurrentMode}");

var supported = await displayService.GetSupportedModesAsync(vdd.DeviceName);
Console.WriteLine($"Supported modes: {supported.Count}");
foreach (var mode in supported.Take(12))
{
    Console.WriteLine($"  {mode}");
}

// Read-only look at the driver settings file.
if (File.Exists(Optima.Driver.Providers.MttVddProvider.DefaultSettingsPath))
{
    var doc = VddSettingsDocument.Load(Optima.Driver.Providers.MttVddProvider.DefaultSettingsPath);
    Console.WriteLine($"vdd_settings.xml: monitors={doc.MonitorCount}, gpu={doc.GpuFriendlyName}, " +
        $"advertised modes={doc.GetAdvertisedModes().Count}");
}

Console.WriteLine("\n== Topology snapshot ==");
var topology = await displayService.CaptureTopologyAsync();
Console.WriteLine($"Captured ({topology.Length} chars)");

DisplayMode[] candidates = [new(1920, 1080, 165), new(1920, 1080, 144), new(1920, 1080, 120), new(1280, 720, 60)];
var target = candidates.FirstOrDefault(c => supported.Contains(c) && c != vdd.CurrentMode);
if (target == default)
{
    target = supported.FirstOrDefault(m => m != vdd.CurrentMode);
}
if (target == default)
{
    Console.WriteLine("No alternative mode available to test with.");
    return 3;
}

Console.WriteLine($"\n== Applying {target} to {vdd.DeviceName} ==");
await displayService.ApplyModeAsync(vdd.DeviceName, target);
await Task.Delay(1500);

var afterApply = (await displayService.GetDisplaysAsync()).First(d => d.DeviceName == vdd.DeviceName);
Console.WriteLine($"Mode after apply: {afterApply.CurrentMode}");
var applied = afterApply.CurrentMode == target;
Console.WriteLine(applied ? "APPLY OK" : "APPLY MISMATCH");

Console.WriteLine("\n== Restoring topology ==");
await displayService.RestoreTopologyAsync(topology);
await Task.Delay(1500);

var afterRestore = (await displayService.GetDisplaysAsync()).First(d => d.DeviceName == vdd.DeviceName);
Console.WriteLine($"Mode after restore: {afterRestore.CurrentMode}");
var restored = afterRestore.CurrentMode == vdd.CurrentMode;
Console.WriteLine(restored ? "RESTORE OK" : "RESTORE MISMATCH");

if (applied && restored)
{
    Console.WriteLine("\nLIVE CHECK PASSED");
    return 0;
}
Console.WriteLine("\nLIVE CHECK FAILED");
return 1;
