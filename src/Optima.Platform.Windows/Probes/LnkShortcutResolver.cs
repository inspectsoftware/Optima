using System.Text;
using System.Text.RegularExpressions;
using Optima.Core.Abstractions;

namespace Optima.Platform.Windows.Probes;

/// <summary>
/// Extracts protocol URIs from .lnk shortcuts by scanning the raw bytes for UTF-16 URI text.
/// Google Play Games game shortcuts embed the launch URI (e.g. googleplaygames://launch/?id=...)
/// rather than a conventional target path, so WScript-style resolution returns nothing for them;
/// a byte scan is the dependable, dependency-free way to read these.
/// </summary>
public sealed partial class LnkShortcutResolver : IShortcutResolver
{
    [GeneratedRegex(@"[a-zA-Z][a-zA-Z0-9+.-]{2,30}://[\x20-\x7E]+?(?=[^\x20-\x7E]|$)")]
    private static partial Regex UriPattern();

    public string? ExtractUri(string shortcutPath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(shortcutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Shortcut string data is UTF-16LE; decode and pick the first URI-shaped run.
        var text = Encoding.Unicode.GetString(bytes);
        var match = UriPattern().Match(text);
        if (match.Success)
        {
            return match.Value;
        }

        // Some shortcuts store strings as ANSI, so fall back to a single-byte decode.
        var ansi = Encoding.Latin1.GetString(bytes);
        match = UriPattern().Match(ansi);
        return match.Success ? match.Value : null;
    }

    public string? GetDisplayName(string shortcutPath)
        => Path.GetFileNameWithoutExtension(shortcutPath);
}
