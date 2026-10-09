using System.Windows;
using System.Windows.Controls;
using Optima.Core.Models;
using Optima.Core.Monitoring;

namespace Optima.App.Views;

/// <summary>
/// The presence chooser: which facts the Discord card carries, field by field, with the three
/// presets as starting points. It edits a working copy, so Cancel and the window's close button
/// both leave the stored choice untouched; only the OK button reports a <see cref="Result"/>.
/// </summary>
public partial class DiscordPresenceWindow : Window
{
    private readonly CheckBox[] _fields;

    /// <summary>The chosen options once accepted, or null when the dialog was dismissed.</summary>
    public DiscordPresenceOptions? Result { get; private set; }

    public DiscordPresenceWindow() : this(DiscordPresenceOptions.Standard)
    {
    }

    public DiscordPresenceWindow(DiscordPresenceOptions initial)
    {
        InitializeComponent();
        _fields = [NameCheck, RankCheck, EmblemCheck, RecordCheck, RatingCheck, TimerCheck];
        Apply(initial);

        // The live summary is the only place the chosen field set is spelled out, so it follows
        // every tick and every preset rather than waiting for OK.
        foreach (var field in _fields)
        {
            field.Checked += (_, _) => UpdateSummary();
            field.Unchecked += (_, _) => UpdateSummary();
        }
        StatusCombo.SelectionChanged += (_, _) => UpdateSummary();
    }

    private void Apply(DiscordPresenceOptions options)
    {
        NameCheck.IsChecked = options.ShowPlayerName;
        RankCheck.IsChecked = options.ShowRank;
        EmblemCheck.IsChecked = options.ShowRankEmblem;
        RecordCheck.IsChecked = options.ShowRankedRecord;
        RatingCheck.IsChecked = options.ShowRankedRating;
        TimerCheck.IsChecked = options.ShowElapsedTime;
        StatusCombo.SelectedIndex = options.StatusDisplay switch
        {
            PresenceStatusDisplay.Name => 0,
            PresenceStatusDisplay.State => 2,
            _ => 1,
        };
        UpdateSummary();
    }

    private DiscordPresenceOptions Current() => new()
    {
        ShowPlayerName = NameCheck.IsChecked == true,
        ShowRank = RankCheck.IsChecked == true,
        ShowRankEmblem = EmblemCheck.IsChecked == true,
        ShowRankedRecord = RecordCheck.IsChecked == true,
        ShowRankedRating = RatingCheck.IsChecked == true,
        ShowElapsedTime = TimerCheck.IsChecked == true,
        StatusDisplay = StatusCombo.SelectedIndex switch
        {
            0 => PresenceStatusDisplay.Name,
            2 => PresenceStatusDisplay.State,
            _ => PresenceStatusDisplay.Details,
        },
    };

    private void UpdateSummary()
    {
        var options = Current();
        var parts = new List<string>();
        if (options.ShowPlayerName)
        {
            parts.Add("your name");
        }
        if (options.ShowRank)
        {
            parts.Add("rank");
        }
        if (options.ShowRankEmblem)
        {
            parts.Add("rank emblem");
        }
        if (options.ShowRankedRecord)
        {
            parts.Add("record");
        }
        if (options.ShowRankedRating)
        {
            parts.Add("rating");
        }
        if (options.ShowElapsedTime)
        {
            parts.Add("session timer");
        }

        var fields = parts.Count == 0 ? "the game name only" : string.Join(", ", parts);
        var status = StatusCombo.SelectedIndex switch
        {
            0 => "the app name",
            2 => "your live status",
            _ => "what you are doing",
        };
        SummaryText.Text = $"Card carries {fields} · status line shows {status}";
    }

    private void OnPresetMinimal(object sender, RoutedEventArgs e) => Apply(DiscordPresenceOptions.Minimal);

    private void OnPresetStandard(object sender, RoutedEventArgs e) => Apply(DiscordPresenceOptions.Standard);

    private void OnPresetFull(object sender, RoutedEventArgs e) => Apply(DiscordPresenceOptions.Full);

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        Result = Current();
        DialogResult = true;
    }
}
