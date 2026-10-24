namespace Optima.Core.Models;

/// <summary>
/// One saved player identity: an in-game name and, usually, an account id. Saved from the PLAYER
/// card in Settings and listed there and in the title-bar switcher.
/// </summary>
public sealed record PlayerAccount
{
    public string Key { get; init; } = Guid.NewGuid().ToString("N");
    public string Ign { get; init; } = string.Empty;
    public long? AccountId { get; init; }
    public string? Label { get; init; }

    /// <summary>The label, else the name, else the id: a player saved while the API was unreachable may only have the id.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(Label)
        ? Label
        : Ign.Length > 0 ? Ign : AccountId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Whether this account is the one currently active in Settings.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive { get; set; }

    /// <summary>
    /// Whether this is the same player. Two ids decide it on their own, since an id survives a rename
    /// and a name does not; without both, the names have to agree.
    /// </summary>
    public bool Matches(string ign, long? accountId)
        => AccountId is { } id && accountId is { } other
            ? id == other
            : Ign.Trim().Length > 0 && string.Equals(Ign.Trim(), ign?.Trim(), StringComparison.OrdinalIgnoreCase);
}
