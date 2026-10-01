using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Stats;

namespace Optima.App.Services;

/// <summary>
/// Saved Critical Ops identities: a main account plus alternates, switchable from the
/// title bar without retyping, and a friends list tracked on HOME.
/// </summary>
public sealed class PlayerSwitcherService
{
    private readonly SettingsService _settings;
    private readonly Optima.Core.Stats.CopsApiClient _api;

    public PlayerSwitcherService(SettingsService settings, Optima.Core.Stats.CopsApiClient api)
    {
        _settings = settings;
        _api = api;
    }

    public async Task<IReadOnlyList<PlayerAccount>> GetSavedAccountsAsync(CancellationToken ct = default)
        => (await _settings.GetSettingsAsync(ct)).SavedAccounts;

    /// <summary>Saves the current player fields as a named account (or updates an existing one).</summary>
    public async Task SaveCurrentAsAccountAsync(string label, CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync(ct);
        var ign = settings.PlayerIgn.Trim();
        if (ign.Length == 0 && settings.PlayerAccountId is not > 0)
        {
            throw new InvalidOperationException("Set an in-game name or account id first.");
        }

        var account = new PlayerAccount
        {
            Ign = ign,
            AccountId = settings.PlayerAccountId,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
        };
        var accounts = settings.SavedAccounts.ToList();
        var existing = accounts.FindIndex(a => a.Matches(ign, settings.PlayerAccountId));
        if (existing >= 0)
        {
            accounts[existing] = account with { Key = accounts[existing].Key };
        }
        else
        {
            accounts.Add(account);
        }
        await _settings.UpdateSettingsAsync(s => s with { SavedAccounts = accounts }, ct);
    }

    /// <summary>Switches the active player to a saved account and returns the new display name.</summary>
    public async Task<string> SwitchToAsync(PlayerAccount account, CancellationToken ct = default)
    {
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

    public async Task AddTrackedAsync(PlayerAccount account, CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync(ct);
        var tracked = settings.TrackedPlayers.ToList();
        if (tracked.Any(t => t.Matches(account.Ign, account.AccountId)))
        {
            return;
        }
        tracked.Add(account);
        await _settings.UpdateSettingsAsync(s => s with { TrackedPlayers = tracked }, ct);
    }

    public async Task RemoveTrackedAsync(string key, CancellationToken ct = default)
    {
        await _settings.UpdateSettingsAsync(s => s with
        {
            TrackedPlayers = s.TrackedPlayers.Where(t => !string.Equals(t.Key, key, StringComparison.Ordinal)).ToList(),
        }, ct);
    }

    /// <summary>Resolves every tracked friend for the HOME strip; failures degrade to a not-found row.</summary>
    public async Task<IReadOnlyList<TrackedPlayerRow>> GetTrackedRowsAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync(ct);
        var results = new List<(PlayerAccount, CopsLookupResult)>();
        foreach (var account in settings.TrackedPlayers)
        {
            CopsLookupResult lookup;
            try
            {
                lookup = await _api.LookupPlayerAsync(account.Ign, account.AccountId, ct);
            }
            catch (Exception)
            {
                lookup = CopsLookupResult.NotFoundPlayer;
            }
            results.Add((account, lookup));
        }
        return TrackedPlayerDigest.BuildRows(results);
    }
}
