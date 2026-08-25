namespace Optima.Core.Models;

/// <summary>Per-setting disclosure required by §8: what it changes, benefit, downside, restart/admin needs.</summary>
public sealed record SettingExplanation
{
    public required string SettingKey { get; init; }
    public required string Title { get; init; }
    public required string WhatItChanges { get; init; }
    public required string PotentialBenefit { get; init; }
    public required string PotentialDownside { get; init; }
    public bool RequiresRestart { get; init; }
    public bool RequiresAdministrator { get; init; }
}

/// <summary>Static catalog backing the profile editor UI.</summary>
public static class SettingExplanations
{
    public static IReadOnlyList<SettingExplanation> All { get; } =
    [
        new()
        {
            SettingKey = "powerPlan",
            Title = "Windows power plan",
            WhatItChanges = "Switches the active Windows power scheme (e.g. High Performance) for the session, restoring the previous plan when the game exits.",
            PotentialBenefit = "Prevents CPU frequency dips and core parking, improving frametime consistency.",
            PotentialDownside = "Higher power draw and heat; on laptops, noticeably shorter battery life.",
            RequiresRestart = false,
            RequiresAdministrator = false,
        },
        new()
        {
            SettingKey = "priority",
            Title = "Process priority",
            WhatItChanges = "Raises the scheduling priority of the Google Play Games emulator process while the game runs.",
            PotentialBenefit = "The game gets CPU time ahead of background apps, reducing stutter under load.",
            PotentialDownside = "Background tasks (downloads, recording, voice chat) may become less responsive.",
            RequiresRestart = false,
            RequiresAdministrator = false,
        },
        new()
        {
            SettingKey = "powerThrottling",
            Title = "Power throttling (EcoQoS)",
            WhatItChanges = "Opts the game processes out of Windows power throttling for the session.",
            PotentialBenefit = "Stops Windows from parking the game on efficiency cores or reducing its clocks.",
            PotentialDownside = "Slightly higher power use; mostly relevant on hybrid-core CPUs and laptops.",
            RequiresRestart = false,
            RequiresAdministrator = false,
        },
        new()
        {
            SettingKey = "cpuAffinity",
            Title = "CPU affinity",
            WhatItChanges = "Restricts the emulator process to a chosen set of CPU cores.",
            PotentialBenefit = "Can keep the game on fast physical cores and away from noisy neighbours.",
            PotentialDownside = "A wrong mask starves the game of cores and *reduces* performance. Leave at 0 (unchanged) unless you know your CPU topology.",
            RequiresRestart = false,
            RequiresAdministrator = false,
        },
        new()
        {
            SettingKey = "virtualDisplay",
            Title = "Virtual display",
            WhatItChanges = "Enables the installed virtual display driver and applies the profile's resolution and refresh rate to it for the session.",
            PotentialBenefit = "Unlocks refresh rates and resolutions your physical monitor does not expose (e.g. 240 Hz), letting the game render on a high-refresh target.",
            PotentialDownside = "A brief display flicker while modes are applied; if a game window opens on the virtual display you may need to move it back after the session (the bootstrapper restores the previous topology automatically).",
            RequiresRestart = false,
            RequiresAdministrator = true,
        },
        new()
        {
            SettingKey = "cleanupProcesses",
            Title = "Background application cleanup",
            WhatItChanges = "Closes the specific applications you listed (and only those) before launching the game.",
            PotentialBenefit = "Frees RAM, CPU and GPU cycles from overlays, updaters and RGB software.",
            PotentialDownside = "The closed applications stay closed, so anything unsaved in them is lost. Nothing is ever closed unless you explicitly added it to the list.",
            RequiresRestart = false,
            RequiresAdministrator = false,
        },
        new()
        {
            SettingKey = "frametimeCapture",
            Title = "Frametime capture (ETW)",
            WhatItChanges = "Starts an external Windows Event Tracing session that records the game's frame presentation statistics. Nothing is injected into the game.",
            PotentialBenefit = "Real FPS, 1% low and frametime percentiles on the dashboard and in session history.",
            PotentialDownside = "Requires the elevated helper (one UAC prompt); negligible CPU overhead.",
            RequiresRestart = false,
            RequiresAdministrator = true,
        },
    ];
}
