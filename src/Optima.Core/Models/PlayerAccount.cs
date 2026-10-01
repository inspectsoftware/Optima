namespace Optima.Core.Models;

/// <summary>
/// One saved player identity: an in-game name plus optional account id. Used for the saved
/// account switcher (main vs. alternates) and for the friends list on HOME.
/// </summary>
public sealed record PlayerAccount
{
    public string Key { get; init; } = Guid.NewGuid().ToString("N");
    public string Ign { get; init; } = string.Empty;
    public long? AccountId { get; init; }
    public string? Label { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Ign : Label;

    /// <summary>Whether this account is the one currently active in Settings.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive { get; set; }

    public bool Matches(string ign, long? accountId)
        => string.Equals(Ign.Trim(), ign?.Trim(), StringComparison.OrdinalIgnoreCase)
            && (AccountId is null || accountId is null || AccountId == accountId);
}
