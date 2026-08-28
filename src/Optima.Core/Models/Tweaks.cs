namespace Optima.Core.Models;

public enum TweakHive
{
    CurrentUser,
    LocalMachine,
}

public enum TweakValueKind
{
    Dword,
    String,
}

public enum TweakRisk
{
    Safe,
    Moderate,
}

/// <summary>Current on/off state of a tweak, read from the registry.</summary>
public enum TweakStatus
{
    Disabled,
    Enabled,
    /// <summary>Some of the tweak's values match the enabled state and some do not
    /// (usually another tweaker got there first). Enabling normalizes it.</summary>
    Mixed,
}

/// <summary>One registry value a tweak sets. All data is string-encoded (DWORDs as unsigned decimal).</summary>
public sealed record TweakValue
{
    public required TweakHive Hive { get; init; }
    public required string KeyPath { get; init; }
    public required string ValueName { get; init; }
    public required TweakValueKind Kind { get; init; }
    public required string EnabledData { get; init; }

    /// <summary>Windows default restored on disable when no original was captured. Null = delete the value.</summary>
    public string? DefaultData { get; init; }
}

/// <summary>
/// A single Windows tweak: what it writes, and the §8-style disclosure (what it changes,
/// benefit, downside) that the UI must show. The catalog below is the closed set the
/// elevated helper will write; nothing outside it can be applied.
/// </summary>
public sealed record TweakDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string WhatItChanges { get; init; }
    public required string PotentialBenefit { get; init; }
    public required string PotentialDownside { get; init; }
    public TweakRisk Risk { get; init; } = TweakRisk.Safe;
    public bool RequiresRestart { get; init; }
    public required IReadOnlyList<TweakValue> Values { get; init; }

    public bool RequiresElevation => Values.Any(v => v.Hive == TweakHive.LocalMachine);
}

/// <summary>
/// The curated Windows tweak catalog. These are the widely published gaming tweaks the major
/// tweaking utilities apply (registry keys and values are public knowledge; the texts here are
/// Optima's own). Every tweak is individually reversible: originals are captured before the
/// first write, and disable restores them (or the documented Windows default).
/// </summary>
public static class TweakCatalog
{
    /// <summary>Stable identity of one value inside a tweak, used in backups and over IPC.</summary>
    public static string ValueKey(TweakValue value) => $"{value.KeyPath}::{value.ValueName}";

    public static TweakDefinition? Find(string id)
        => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    public static IReadOnlyList<TweakDefinition> All { get; } =
    [
        // ---------------------------------------------------------------- capture
        new()
        {
            Id = "game-dvr-off",
            Name = "Game DVR background recording off",
            Category = "capture",
            WhatItChanges = "Turns off the Xbox Game Bar background recording (Game DVR) that continuously captures gameplay so 'record that' can work.",
            PotentialBenefit = "Removes a constant encode and disk load while playing; one of the most common stutter sources.",
            PotentialDownside = "Win+G instant replay and background capture stop working until re-enabled.",
            Values =
            [
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"System\GameConfigStore", ValueName = "GameDVR_Enabled", Kind = TweakValueKind.Dword, EnabledData = "0", DefaultData = "1" },
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", ValueName = "AppCaptureEnabled", Kind = TweakValueKind.Dword, EnabledData = "0", DefaultData = "1" },
            ],
        },
        new()
        {
            Id = "fse-optimizations-off",
            Name = "Fullscreen optimizations off (global)",
            Category = "capture",
            Risk = TweakRisk.Moderate,
            WhatItChanges = "Tells Windows not to apply its fullscreen-optimization presentation layer to games, system-wide.",
            PotentialBenefit = "Some systems get more consistent frame pacing in native fullscreen games.",
            PotentialDownside = "Mostly affects native fullscreen titles; the Google Play Games emulator window benefits less. Alt-Tab out of native fullscreen games can get slower.",
            Values =
            [
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"System\GameConfigStore", ValueName = "GameDVR_FSEBehaviorMode", Kind = TweakValueKind.Dword, EnabledData = "2", DefaultData = null },
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"System\GameConfigStore", ValueName = "GameDVR_HonorUserFSEBehaviorMode", Kind = TweakValueKind.Dword, EnabledData = "1", DefaultData = null },
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"System\GameConfigStore", ValueName = "GameDVR_DXGIHonorFSEWindowsCompatible", Kind = TweakValueKind.Dword, EnabledData = "1", DefaultData = null },
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"System\GameConfigStore", ValueName = "GameDVR_EFSEFeatureFlags", Kind = TweakValueKind.Dword, EnabledData = "0", DefaultData = null },
            ],
        },

        // ------------------------------------------------------------- scheduling
        new()
        {
            Id = "game-mode-on",
            Name = "Windows Game Mode on",
            Category = "cpu scheduling",
            WhatItChanges = "Explicitly enables Windows Game Mode, which holds back Windows Update activity and driver installs while a game has focus.",
            PotentialBenefit = "Fewer background interruptions in the middle of a match.",
            PotentialDownside = "Game Mode is usually already on; the explicit value just removes the ambiguity.",
            Values =
            [
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"Software\Microsoft\GameBar", ValueName = "AutoGameModeEnabled", Kind = TweakValueKind.Dword, EnabledData = "1", DefaultData = null },
            ],
        },
        new()
        {
            Id = "priority-separation",
            Name = "Foreground CPU priority boost",
            Category = "cpu scheduling",
            Risk = TweakRisk.Moderate,
            WhatItChanges = "Sets Win32PrioritySeparation to 0x26 (38): the foreground application gets long, fixed CPU scheduling slices instead of the default short variable ones.",
            PotentialBenefit = "The focused game gets steadier CPU time against background processes.",
            PotentialDownside = "Background work slows down while any window is focused; heavy multitaskers may prefer the Windows default.",
            Values =
            [
                new() { Hive = TweakHive.LocalMachine, KeyPath = @"SYSTEM\CurrentControlSet\Control\PriorityControl", ValueName = "Win32PrioritySeparation", Kind = TweakValueKind.Dword, EnabledData = "38", DefaultData = "2" },
            ],
        },
        new()
        {
            Id = "system-responsiveness",
            Name = "Multimedia CPU reservation off",
            Category = "cpu scheduling",
            Risk = TweakRisk.Moderate,
            RequiresRestart = true,
            WhatItChanges = "Sets SystemResponsiveness to 0: the multimedia class scheduler stops reserving 20% of the CPU for background activity while games or audio run.",
            PotentialBenefit = "Game and audio threads can use the full CPU instead of 80% of it.",
            PotentialDownside = "Background tasks can starve under sustained full load; audio glitches are possible on very busy systems.",
            Values =
            [
                new() { Hive = TweakHive.LocalMachine, KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", ValueName = "SystemResponsiveness", Kind = TweakValueKind.Dword, EnabledData = "0", DefaultData = "20" },
            ],
        },
        new()
        {
            Id = "games-task-priority",
            Name = "MMCSS games task boost",
            Category = "cpu scheduling",
            Risk = TweakRisk.Moderate,
            WhatItChanges = "Raises the scheduling class the multimedia scheduler assigns to threads registered under its 'Games' task (priority, scheduling category and storage I/O priority).",
            PotentialBenefit = "Games that register with MMCSS get scheduled ahead of normal background work.",
            PotentialDownside = "Only affects games that actually register with MMCSS; many titles, and the Android emulator, do not.",
            Values =
            [
                new() { Hive = TweakHive.LocalMachine, KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", ValueName = "GPU Priority", Kind = TweakValueKind.Dword, EnabledData = "8", DefaultData = "8" },
                new() { Hive = TweakHive.LocalMachine, KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", ValueName = "Priority", Kind = TweakValueKind.Dword, EnabledData = "6", DefaultData = "2" },
                new() { Hive = TweakHive.LocalMachine, KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", ValueName = "Scheduling Category", Kind = TweakValueKind.String, EnabledData = "High", DefaultData = "Medium" },
                new() { Hive = TweakHive.LocalMachine, KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", ValueName = "SFIO Priority", Kind = TweakValueKind.String, EnabledData = "High", DefaultData = "Normal" },
            ],
        },
        new()
        {
            Id = "power-throttling-off",
            Name = "System-wide power throttling off",
            Category = "cpu scheduling",
            Risk = TweakRisk.Moderate,
            WhatItChanges = "Disables Windows EcoQoS power throttling for every process on the system, not just the game.",
            PotentialBenefit = "Nothing gets parked on efficiency cores or clocked down, including the emulator's helper processes the per-session profile toggle cannot reach.",
            PotentialDownside = "Noticeably shorter battery life on laptops. The launch profile already un-throttles the game itself per session.",
            Values =
            [
                new() { Hive = TweakHive.LocalMachine, KeyPath = @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", ValueName = "PowerThrottlingOff", Kind = TweakValueKind.Dword, EnabledData = "1", DefaultData = null },
            ],
        },

        // -------------------------------------------------------------------- gpu
        new()
        {
            Id = "hags-on",
            Name = "Hardware-accelerated GPU scheduling",
            Category = "gpu",
            Risk = TweakRisk.Moderate,
            RequiresRestart = true,
            WhatItChanges = "Sets HwSchMode to 2: the GPU manages its own work queue instead of the CPU-side scheduler batching for it.",
            PotentialBenefit = "Slightly lower render latency on modern NVIDIA/AMD GPUs; required for DLSS frame generation.",
            PotentialDownside = "Driver dependent: a few game/driver combinations stutter with it on. Turn it back off if a game misbehaves. Needs a restart.",
            Values =
            [
                new() { Hive = TweakHive.LocalMachine, KeyPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ValueName = "HwSchMode", Kind = TweakValueKind.Dword, EnabledData = "2", DefaultData = "1" },
            ],
        },

        // ---------------------------------------------------------------- network
        new()
        {
            Id = "network-throttling-off",
            Name = "Network throttling off",
            Category = "network",
            Risk = TweakRisk.Moderate,
            RequiresRestart = true,
            WhatItChanges = "Sets NetworkThrottlingIndex to 0xFFFFFFFF, disabling the multimedia network throttle that caps packet processing at 10 packets per millisecond while media plays.",
            PotentialBenefit = "Lower and steadier ping in online games on fast connections.",
            PotentialDownside = "The throttle exists to shield audio playback from network interrupt load; rare audio crackle is possible under heavy traffic.",
            Values =
            [
                new() { Hive = TweakHive.LocalMachine, KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", ValueName = "NetworkThrottlingIndex", Kind = TweakValueKind.Dword, EnabledData = "4294967295", DefaultData = "10" },
            ],
        },

        // ------------------------------------------------------------------ input
        new()
        {
            Id = "mouse-accel-off",
            Name = "Mouse acceleration off",
            Category = "input",
            WhatItChanges = "Turns off 'Enhance pointer precision' at the registry level: cursor distance depends only on how far the mouse physically moves.",
            PotentialBenefit = "Consistent aim; the same hand motion always covers the same distance.",
            PotentialDownside = "Takes effect at the next sign-in. The desktop cursor may feel slower until sensitivity is adjusted.",
            Values =
            [
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"Control Panel\Mouse", ValueName = "MouseSpeed", Kind = TweakValueKind.String, EnabledData = "0", DefaultData = "1" },
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"Control Panel\Mouse", ValueName = "MouseThreshold1", Kind = TweakValueKind.String, EnabledData = "0", DefaultData = "6" },
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"Control Panel\Mouse", ValueName = "MouseThreshold2", Kind = TweakValueKind.String, EnabledData = "0", DefaultData = "10" },
            ],
        },
        new()
        {
            Id = "accessibility-hotkeys-off",
            Name = "Accessibility hotkeys off",
            Category = "input",
            WhatItChanges = "Disables the Sticky Keys (5x Shift), Toggle Keys and Filter Keys activation shortcuts.",
            PotentialBenefit = "No accessibility popup stealing focus mid-game after rapid Shift presses.",
            PotentialDownside = "The features stay available in Windows Settings; only the surprise keyboard activation is off. Takes effect at the next sign-in.",
            Values =
            [
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"Control Panel\Accessibility\StickyKeys", ValueName = "Flags", Kind = TweakValueKind.String, EnabledData = "506", DefaultData = "510" },
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"Control Panel\Accessibility\ToggleKeys", ValueName = "Flags", Kind = TweakValueKind.String, EnabledData = "58", DefaultData = "62" },
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"Control Panel\Accessibility\Keyboard Response", ValueName = "Flags", Kind = TweakValueKind.String, EnabledData = "122", DefaultData = "126" },
            ],
        },

        // ------------------------------------------------------------------ shell
        new()
        {
            Id = "menu-show-delay-off",
            Name = "Instant menus",
            Category = "shell",
            WhatItChanges = "Removes the 400 ms delay Windows waits before opening menus.",
            PotentialBenefit = "The desktop feels snappier.",
            PotentialDownside = "Cosmetic only, and applies at the next sign-in. Menus can flash open while mousing across them.",
            Values =
            [
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"Control Panel\Desktop", ValueName = "MenuShowDelay", Kind = TweakValueKind.String, EnabledData = "0", DefaultData = "400" },
            ],
        },
        new()
        {
            Id = "transparency-off",
            Name = "Window transparency off",
            Category = "shell",
            WhatItChanges = "Disables the acrylic/transparency effects on the taskbar, Start menu and system surfaces.",
            PotentialBenefit = "Removes constant GPU compositing work, freeing a little GPU headroom.",
            PotentialDownside = "Windows looks flatter.",
            Values =
            [
                new() { Hive = TweakHive.CurrentUser, KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", ValueName = "EnableTransparency", Kind = TweakValueKind.Dword, EnabledData = "0", DefaultData = "1" },
            ],
        },
    ];
}
