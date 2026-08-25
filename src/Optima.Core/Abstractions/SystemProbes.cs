namespace Optima.Core.Abstractions;

/// <summary>
/// Thin, mockable probes over the OS surfaces detection touches. Production implementations
/// live in Platform.Windows; tests supply fakes so rule evaluation stays unit-testable.
/// </summary>
public interface IRegistryProbe
{
    /// <summary>Reads a string value; path uses HKLM\... / HKCU\... prefixes. Null when missing.</summary>
    string? GetValue(string keyPath, string valueName);

    /// <summary>Subkey names of a key, empty when the key is missing.</summary>
    IReadOnlyList<string> GetSubKeyNames(string keyPath);
}

public interface IFileSystemProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IReadOnlyList<string> EnumerateFiles(string directory, string pattern);
    string? ReadAllText(string path);
}

public interface IProcessProbe
{
    /// <summary>Names (without extension) and ids of all running processes.</summary>
    IReadOnlyList<(int Id, string Name)> GetProcesses();
}

/// <summary>Resolves .lnk shortcuts to their target/arguments/URI without COM in tests.</summary>
public interface IShortcutResolver
{
    /// <summary>Extracts an embedded URI (e.g. googleplaygames://...) from a shortcut, or null.</summary>
    string? ExtractUri(string shortcutPath);

    string? GetDisplayName(string shortcutPath);
}
