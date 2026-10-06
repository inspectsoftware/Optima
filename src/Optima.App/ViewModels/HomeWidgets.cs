using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Optima.App.ViewModels;

/// <summary>One widget the HOME dashboard can show.</summary>
public sealed record HomeWidgetDefinition(string Id, string Title, string Tab);

/// <summary>
/// The widget catalog: every card a player can put on HOME, plus the layout HOME opens with. Ids are
/// what get persisted, so a widget keeps its meaning across builds and an id that no longer exists is
/// simply dropped when a saved layout is read back.
/// </summary>
public static class HomeWidgetCatalog
{
    /// <summary>HOME holds at most this many widgets; the edit panel refuses the eleventh.</summary>
    public const int MaxOnHome = 10;

    public static IReadOnlyList<HomeWidgetDefinition> All { get; } =
    [
        new("status", "Status", "HOME"),
        new("system", "System", "HOME"),
        new("performance", "Performance", "HOME"),
        new("launch", "Launch", "HOME"),
        new("player", "Player", "HOME"),
        new("friends", "Tracked players", "HOME"),
        new("sessions", "Recent sessions", "SESSIONS"),
        new("trends", "Profile trends", "SESSIONS"),
        new("display", "Displays", "DISPLAY"),
        new("comp", "Network and thermals", "COMP"),
        new("news", "Critical Ops news", "NEWS"),
        new("diagnostics", "Diagnostics", "DIAGNOSTICS"),
    ];

    /// <summary>What HOME has always shown, in force until the player edits the layout once.</summary>
    public static IReadOnlyList<string> Default { get; } =
        ["status", "system", "performance", "launch", "player", "friends"];

    public static HomeWidgetDefinition? Find(string id)
        => All.FirstOrDefault(definition => string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One catalog entry as the board and the edit panel see it. A rendered widget binds through
/// <see cref="Host"/>, so every widget template is written against the page's own view models, and
/// the same template works wherever the widget sits.
/// </summary>
public sealed partial class HomeWidgetItem : ObservableObject
{
    public HomeWidgetItem(HomeWidgetDefinition definition, HomeViewModel host)
    {
        Definition = definition;
        Host = host;
    }

    public HomeWidgetDefinition Definition { get; }

    public HomeViewModel Host { get; }

    [ObservableProperty]
    private bool _isOnHome;
}

/// <summary>One tab's row in the edit panel: the widgets that tab can contribute.</summary>
public sealed class HomeWidgetGroup(string tab, IEnumerable<HomeWidgetItem> items)
{
    public string Tab { get; } = tab;

    public ObservableCollection<HomeWidgetItem> Items { get; } = new(items);

    public string Caption => Tab == "HOME" ? "the widgets HOME owns" : "from the " + Tab + " tab";
}
