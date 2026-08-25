using System.Diagnostics;
using Optima.Core.Abstractions;
using Microsoft.Win32;

namespace Optima.Platform.Windows.Probes;

/// <summary>Real registry access. Key paths use "HKLM\..." / "HKCU\..." prefixes.</summary>
public sealed class WindowsRegistryProbe : IRegistryProbe
{
    public string? GetValue(string keyPath, string valueName)
    {
        var (hive, subPath) = Split(keyPath);
        if (hive is null)
        {
            return null;
        }
        using var key = hive.OpenSubKey(subPath);
        return key?.GetValue(valueName)?.ToString();
    }

    public IReadOnlyList<string> GetSubKeyNames(string keyPath)
    {
        var (hive, subPath) = Split(keyPath);
        if (hive is null)
        {
            return [];
        }
        using var key = hive.OpenSubKey(subPath);
        return key?.GetSubKeyNames() ?? [];
    }

    private static (RegistryKey? Hive, string SubPath) Split(string keyPath)
    {
        var separator = keyPath.IndexOf('\\');
        if (separator < 0)
        {
            return (null, string.Empty);
        }

        var hive = keyPath[..separator].ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
            _ => null,
        };
        return (hive, keyPath[(separator + 1)..]);
    }
}

public sealed class WindowsFileSystemProbe : IFileSystemProbe
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string? ReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed class WindowsProcessProbe : IProcessProbe
{
    public IReadOnlyList<(int Id, string Name)> GetProcesses()
    {
        var result = new List<(int, string)>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                result.Add((process.Id, process.ProcessName));
            }
            finally
            {
                process.Dispose();
            }
        }
        return result;
    }
}
