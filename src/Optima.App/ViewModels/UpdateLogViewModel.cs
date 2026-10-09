using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Optima.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>UPDATES page: the shipped changelog with the running build's identity, and the update check.</summary>
public sealed partial class UpdateLogViewModel : ObservableObject
{
    private readonly ILogger<UpdateLogViewModel> _logger;
    private bool _loaded;

    public UpdateLogViewModel(ILogger<UpdateLogViewModel> logger, UpdateViewModel updates)
    {
        _logger = logger;
        Updates = updates;
    }

    /// <summary>The same update state HOME shows, with a button to ask again.</summary>
    public UpdateViewModel Updates { get; }

    public ObservableCollection<ChangelogEntry> Entries { get; } = [];

    [ObservableProperty] private string _buildInfo = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    public Task InitializeAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return Task.CompletedTask;
        }
        _loaded = true;

        var version = typeof(UpdateLogViewModel).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var exePath = Environment.ProcessPath;
        var built = exePath is not null && File.Exists(exePath)
            ? File.GetLastWriteTime(exePath).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "unknown";
        BuildInfo = $"version {version} · built {built}";

        // Not awaited: the first screen is in place when this returns, and navigation must not
        // wait for the rest of the history.
        _ = LoadChangelogAsync();
        return Task.CompletedTask;
    }

    /// <summary>
    /// How many entries are on the page before it is shown: enough to fill a tall window. Every
    /// entry is a glass panel around a few paragraphs of wrapped text, and laying out the whole
    /// log in one pass held the first open of this page for about half a second. The older builds
    /// follow one at a time whenever the app is idle, and the page ends up exactly as before.
    /// </summary>
    private const int FirstScreen = 6;

    private async Task LoadChangelogAsync()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
            if (!File.Exists(path))
            {
                Status = "CHANGELOG.md was not found next to the executable";
                return;
            }
            var entries = ChangelogParser.Parse(File.ReadAllText(path));
            if (entries.Count == 0)
            {
                Status = "the changelog is empty";
            }
            for (var i = 0; i < entries.Count; i++)
            {
                if (i >= FirstScreen)
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
                Entries.Add(entries[i]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the changelog failed");
            Status = "the changelog could not be read · see logs";
        }
    }
}
