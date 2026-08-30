using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Optima.App.ViewModels;

/// <summary>
/// LEGAL page: who makes Optima, exactly how it stays outside the game, everything it
/// reads and writes, and the license texts that ship with every build. The page states
/// facts about the code's behavior; it deliberately makes no promises about third-party
/// services or account standing.
/// </summary>
public sealed partial class LegalViewModel : ObservableObject
{
    [ObservableProperty] private string _licenseText = string.Empty;
    [ObservableProperty] private string _thirdPartyText = string.Empty;
    private bool _loaded;

    public Task InitializeAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return Task.CompletedTask;
        }
        _loaded = true;
        LicenseText = ReadShipped("LICENSE");
        ThirdPartyText = ReadShipped("THIRD-PARTY-NOTICES.md");
        return Task.CompletedTask;
    }

    private static string ReadShipped(string fileName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            return File.Exists(path) ? File.ReadAllText(path) : fileName + " was not found next to the executable.";
        }
        catch (IOException ex)
        {
            return fileName + " could not be read: " + ex.Message;
        }
    }
}
