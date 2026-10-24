using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Stats;

namespace Optima.App.Services;

/// <summary>
/// Players saved in Optima: tracked again with one click from the PLAYER card in Settings or from
/// the title-bar switcher, without typing the name or id again.
/// </summary>
public sealed class PlayerSwitcherService
{
    private readonly SettingsService _settings;

    public PlayerSwitcherService(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>Saves a player, or updates the saved entry that is the same player.</summary>
    public async Task SaveAccountAsync(string ign, long? accountId, CancellationToken ct = default)
    {
        if (ign.Trim().Length == 0 && accountId is not > 0)
        {
            throw new InvalidOperationException("Track a player first.");
        }

        // Decided on the settings as they are when the save runs, not on a copy read beforehand: two
        // saves close together otherwise both start from the same list and the second drops the first.
        await _settings.UpdateSettingsAsync(s => s with
        {
            SavedAccounts = TrackedPlayerQuery.WithSaved(s.SavedAccounts, ign, accountId),
        }, ct);
    }

    /// <summary>
    /// Raised the moment a switch is asked for, before it is written: a Track lookup still running on
    /// the PLAYER card leaves a later choice alone, and a write held up by a busy config.json would
    /// tell it too late.
    /// </summary>
    public event Action? Switching;

    /// <summary>Makes a saved account the tracked player and returns its display name.</summary>
    public async Task<string> SwitchToAsync(PlayerAccount account, CancellationToken ct = default)
    {
        Switching?.Invoke();
        await _settings.UpdateSettingsAsync(s => s with
        {
            PlayerIgn = account.Ign,
            PlayerAccountId = account.AccountId,
        }, ct);
        return account.DisplayName;
    }

    public async Task RemoveAccountAsync(string key, CancellationToken ct = default)
    {
        await _settings.UpdateSettingsAsync(s => s with
        {
            SavedAccounts = s.SavedAccounts.Where(a => !string.Equals(a.Key, key, StringComparison.Ordinal)).ToList(),
        }, ct);
    }
}
