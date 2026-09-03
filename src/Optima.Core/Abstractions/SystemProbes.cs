namespace Optima.Core.Abstractions;

/// <summary>Thin, mockable probes over the OS surfaces detection touches.</summary>
public interface IRegistryProbe
{
    string? GetValue(string keyPath, string valueName);

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
    IReadOnlyList<(int Id, string Name)> GetProcesses();
}

/// <summary>Resolves .lnk shortcuts to their target/arguments/URI without COM in tests.</summary>
public interface IShortcutResolver
{
    string? ExtractUri(string shortcutPath);

    string? GetDisplayName(string shortcutPath);
}
