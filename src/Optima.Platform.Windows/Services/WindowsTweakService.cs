using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Ipc;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>
/// Applies the curated tweak catalog. Reading works non-elevated for both hives; writes go
/// direct for HKCU and through the elevated helper's ApplyTweakValues command for HKLM.
/// Before a tweak is first enabled, the actual current values are captured to
/// tweaks-original-values.json, and disable restores exactly those (falling back to the
/// documented Windows default when nothing was captured).
/// </summary>
public sealed class WindowsTweakService : ITweakService
{
    private readonly IElevationBroker _elevation;
    private readonly AppPaths _paths;
    private readonly JsonStore _store;
    private readonly ILogger<WindowsTweakService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsTweakService(
        IElevationBroker elevation,
        AppPaths paths,
        JsonStore store,
        ILogger<WindowsTweakService> logger)
    {
        _elevation = elevation;
        _paths = paths;
        _store = store;
        _logger = logger;
    }

    public Task<IReadOnlyList<TweakState>> GetStatesAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<TweakState>>(
            () => TweakCatalog.All.Select(t => new TweakState(t, Evaluate(t))).ToList(), ct);

    public async Task<TweakState> SetEnabledAsync(string tweakId, bool enable, CancellationToken ct = default)
    {
        var definition = TweakCatalog.Find(tweakId)
            ?? throw new ArgumentException($"Unknown tweak id '{tweakId}'.", nameof(tweakId));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var backups = await _store.LoadAsync<Dictionary<string, Dictionary<string, string?>>>(
                _paths.TweaksBackupFile, ct).ConfigureAwait(false) ?? [];

            // Capture the true originals before the first write, and persist them before
            // touching the registry so even a crash mid-apply leaves a usable restore point.
            if (enable && !backups.ContainsKey(tweakId))
            {
                backups[tweakId] = definition.Values.ToDictionary(TweakCatalog.ValueKey, ReadData);
                await _store.SaveAsync(_paths.TweaksBackupFile, backups, ct).ConfigureAwait(false);
            }

            var targets = definition.Values.ToDictionary(
                v => v,
                v => enable
                    ? (string?)v.EnabledData
                    : backups.TryGetValue(tweakId, out var captured) && captured.TryGetValue(TweakCatalog.ValueKey(v), out var original)
                        ? original
                        : v.DefaultData);

            foreach (var (value, data) in targets.Where(t => t.Key.Hive == TweakHive.CurrentUser))
            {
                WriteCurrentUser(value, data);
            }

            var machineTargets = targets.Where(t => t.Key.Hive == TweakHive.LocalMachine)
                .ToDictionary(t => TweakCatalog.ValueKey(t.Key), t => t.Value);
            if (machineTargets.Count > 0)
            {
                await ApplyElevatedAsync(tweakId, machineTargets, ct).ConfigureAwait(false);
            }

            // The restore point has served its purpose once the original values are back.
            if (!enable && backups.Remove(tweakId))
            {
                await _store.SaveAsync(_paths.TweaksBackupFile, backups, ct).ConfigureAwait(false);
            }

            _logger.LogInformation("Tweak '{Tweak}' {Action}", tweakId, enable ? "enabled" : "disabled");
            return new TweakState(definition, Evaluate(definition));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyElevatedAsync(string tweakId, Dictionary<string, string?> targets, CancellationToken ct)
    {
        if (!await _elevation.EnsureStartedAsync(ct).ConfigureAwait(false))
        {
            throw OptimaException.From("ELEVATION_DECLINED",
                "Administrator access is needed for this tweak.",
                "It writes machine-wide (HKLM) registry values, which only the elevated helper may do.",
                null,
                "Approve the administrator prompt when it appears");
        }

        var response = await _elevation.SendAsync(new IpcRequest
        {
            Command = IpcCommand.ApplyTweakValues,
            Args =
            {
                ["tweakId"] = tweakId,
                ["values"] = JsonSerializer.Serialize(targets),
            },
        }, ct).ConfigureAwait(false);

        if (!response.Success)
        {
            throw OptimaException.From("TWEAK_WRITE_FAILED",
                "Windows refused the tweak change.",
                response.Error);
        }
    }

    private static TweakStatus Evaluate(TweakDefinition definition)
    {
        var matches = definition.Values.Count(v =>
            string.Equals(ReadData(v), v.EnabledData, StringComparison.OrdinalIgnoreCase));
        return matches == definition.Values.Count ? TweakStatus.Enabled
            : matches == 0 ? TweakStatus.Disabled
            : TweakStatus.Mixed;
    }

    /// <summary>Current registry data as a string (DWORDs as unsigned decimal), or null when absent.</summary>
    private static string? ReadData(TweakValue value)
    {
        var baseKey = value.Hive == TweakHive.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;
        using var key = baseKey.OpenSubKey(value.KeyPath);
        return key?.GetValue(value.ValueName) switch
        {
            null => null,
            int i => unchecked((uint)i).ToString(CultureInfo.InvariantCulture),
            string s => s,
            var other => other.ToString(),
        };
    }

    private static void WriteCurrentUser(TweakValue value, string? data)
    {
        using var key = Registry.CurrentUser.CreateSubKey(value.KeyPath);
        if (data is null)
        {
            key.DeleteValue(value.ValueName, throwOnMissingValue: false);
        }
        else if (value.Kind == TweakValueKind.Dword)
        {
            key.SetValue(value.ValueName,
                unchecked((int)uint.Parse(data, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                RegistryValueKind.DWord);
        }
        else
        {
            key.SetValue(value.ValueName, data, RegistryValueKind.String);
        }
    }
}
