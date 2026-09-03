using System.Text;
using System.Text.RegularExpressions;
using Optima.Core.Abstractions;

namespace Optima.Platform.Windows.Probes;

/// <summary>Extracts protocol URIs from .lnk shortcuts by scanning the raw bytes for UTF-16 URI text.</summary>
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

        var text = Encoding.Unicode.GetString(bytes);
        var match = UriPattern().Match(text);
        if (match.Success)
        {
            return match.Value;
        }

        var ansi = Encoding.Latin1.GetString(bytes);
        match = UriPattern().Match(ansi);
        return match.Success ? match.Value : null;
    }

    public string? GetDisplayName(string shortcutPath)
        => Path.GetFileNameWithoutExtension(shortcutPath);
}
