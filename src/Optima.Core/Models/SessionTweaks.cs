namespace Optima.Core.Models;

/// <summary>
/// Session-scoped competitive tweaks: like profile settings, they apply when a session starts
/// and are restored when it ends (unlike the persistent catalog on PERFORMANCE). Each entry
/// mirrors a TweakDefinition so the existing registry engine can apply and restore it.
/// </summary>
public static class SessionTweakCatalog
{
    public static IReadOnlyList<TweakDefinition> All { get; } =
    [
        new()
        {
            Id = "session-gamebar-off",
            Name = "Xbox Game Bar off for the session",
            Category = "session",
            WhatItChanges = "Disables the Game Bar controller while the session runs (GameDVR_Enabled), restoring the previous value on exit.",
            PotentialBenefit = "The Win+G overlay cannot steal focus mid-match.",
            PotentialDownside = "Win+G and instant replay do not respond while the session runs.",
            Values =
            [
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"System\GameConfigStore", ValueName = "GameDVR_Enabled", Kind = TweakValueKind.Dword, EnabledData = "0", DefaultData = "1" },
            ],
        },
        new()
        {
            Id = "session-fse-off",
            Name = "Fullscreen optimizations off (session)",
            Category = "session",
            Risk = TweakRisk.Moderate,
            WhatItChanges = "Applies the global fullscreen-optimization opt-out only while the session runs, then restores it.",
            PotentialBenefit = "More consistent frame pacing for the duration of the session.",
            PotentialDownside = "Alt-Tab out of native fullscreen games can get slower until the session ends.",
            Values =
            [
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"System\GameConfigStore", ValueName = "GameDVR_FSEBehaviorMode", Kind = TweakValueKind.Dword, EnabledData = "2", DefaultData = null },
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"System\GameConfigStore", ValueName = "GameDVR_HonorUserFSEBehaviorMode", Kind = TweakValueKind.Dword, EnabledData = "1", DefaultData = null },
            ],
        },
    ];

    public static TweakDefinition? Find(string id)
        => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
}
