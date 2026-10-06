using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Optima.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>UPDATES page: the shipped changelog with the running build's identity. New builds arrive as installers.</summary>
public sealed partial class UpdateLogViewModel : ObservableObject
{
    private readonly ILogger<UpdateLogViewModel> _logger;
    private bool _loaded;

    public UpdateLogViewModel(ILogger<UpdateLogViewModel> logger)
    {
        _logger = logger;
    }

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

        LoadChangelog();
        return Task.CompletedTask;
    }

    private void LoadChangelog()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
            if (!File.Exists(path))
            {
                Status = "CHANGELOG.md was not found next to the executable";
                return;
            }
            foreach (var entry in ChangelogParser.Parse(File.ReadAllText(path)))
            {
                Entries.Add(entry);
            }
            if (Entries.Count == 0)
            {
                Status = "the changelog is empty";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the changelog failed");
            Status = "the changelog could not be read · see logs";
        }
    }
}
