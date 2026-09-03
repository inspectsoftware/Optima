using Microsoft.Win32;

namespace Optima.App.Services;

/// <summary>
/// The launcher-owned start-with-Windows entry: one HKCU Run value, written on enable and deleted on disable, so
/// nothing lingers after the user turns it off.
/// </summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Optima";

    public static string? Apply(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled && Environment.ProcessPath is { } exe)
            {
                run.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
