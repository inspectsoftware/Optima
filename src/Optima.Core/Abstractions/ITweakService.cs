using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>A tweak definition paired with its current registry state.</summary>
public sealed record TweakState(TweakDefinition Definition, TweakStatus Status);

/// <summary>Applies and reverts the curated Windows tweak catalog.</summary>
public interface ITweakService
{
    Task<IReadOnlyList<TweakState>> GetStatesAsync(CancellationToken ct = default);

    Task<TweakState> SetEnabledAsync(string tweakId, bool enable, CancellationToken ct = default);
}
