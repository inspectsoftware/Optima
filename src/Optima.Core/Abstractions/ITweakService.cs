using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>A tweak definition paired with its current registry state.</summary>
public sealed record TweakState(TweakDefinition Definition, TweakStatus Status);

/// <summary>
/// Applies and reverts the curated Windows tweak catalog. Unlike the per-session profile
/// changes, tweaks are persistent toggles: they stay until disabled. Original registry
/// values are captured before the first write so disable restores what was actually there.
/// </summary>
public interface ITweakService
{
    Task<IReadOnlyList<TweakState>> GetStatesAsync(CancellationToken ct = default);

    /// <summary>Enables or disables one tweak by id. Throws <see cref="OptimaException"/> on failure.</summary>
    Task<TweakState> SetEnabledAsync(string tweakId, bool enable, CancellationToken ct = default);
}
