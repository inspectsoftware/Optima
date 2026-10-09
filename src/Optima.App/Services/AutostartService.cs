using System.IO;
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

    /// <summary>
    /// An entry that names an Optima.exe which is no longer there is pointed at this one. That is
    /// what an install that moved leaves behind: the setup that put Optima in Program Files removes
    /// the copy in the user's own folder, and the entry was written for that copy. An entry whose
    /// file still exists belongs to another install and is left alone.
    /// </summary>
    public static void RepointIfStale()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (run?.GetValue(ValueName) is not string value || Environment.ProcessPath is not { } exe)
            {
                return;
            }
            var target = value.TrimStart().StartsWith('"')
                ? value.TrimStart()[1..].Split('"')[0]
                : value.Split(" --", 2)[0].Trim();
            if (string.Equals(Path.GetFileName(target), "Optima.exe", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)
                && !File.Exists(target))
            {
                run.SetValue(ValueName, $"\"{exe}\" --tray");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            // Autostart is a convenience; a registry that says no is not a reason to fail a start.
        }
    }

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
