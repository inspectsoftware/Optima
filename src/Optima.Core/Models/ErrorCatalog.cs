namespace Optima.Core.Models;

/// <summary>One entry of the error guide: what a code means and how to get past it.</summary>
public sealed record ErrorCatalogEntry(
    string Code,
    string Title,
    string WhatHappened,
    string WhyItHappens,
    IReadOnlyList<string> HowToFix);

/// <summary>
/// Every known Optima error code with a plain-language explanation, so the DEBUG page can show
/// one discrete guide instead of scattered messages. The tests assert this catalog stays in
/// step with every OptimaException code raised in the app.
/// </summary>
public static class ErrorCatalog
{
    public static IReadOnlyList<ErrorCatalogEntry> All { get; } =
    [
        new(
            "VDD_NO_DISPLAY",
            "The virtual display did not appear",
            "Optima enabled the virtual display driver, but Windows never attached a display to the desktop within 15 seconds.",
            "The driver device was enabled but its monitor output stayed parked. This is the most common virtual display fault; a settings reload almost always wakes it.",
            [
                "Open the Display page and press RELOAD DRIVER so the driver re-reads vdd_settings.xml",
                "Check vdd_settings.xml (default C:\\VirtualDisplayDriver\\vdd_settings.xml) has a monitor count of at least 1",
                "Reinstall the virtual display driver from the Display page",
            ]),
        new(
            "VDD_NOT_INSTALLED",
            "No virtual display driver was found",
            "Optima looked for the virtual display driver device and it is not present in Windows.",
            "The driver was never installed on this machine, or it was removed by hand or by a cleanup tool.",
            [
                "Install the driver from the Display page (one administrator prompt, no Device Manager needed)",
                "Check Device Manager under Display adapters if the install reports success but the device is missing",
            ]),
        new(
            "VDD_PIPE_FAILED",
            "The virtual display driver did not accept the reload",
            "Optima wrote RELOAD_DRIVER to the driver's control pipe, but the driver never acknowledged it.",
            "The driver service is loaded but not answering: it can be stuck, partially installed, or its control pipe is held by another program.",
            [
                "Restart the machine once, then press RELOAD DRIVER again",
                "Reinstall the virtual display driver from the Display page",
                "Check the log on the Debug page for the underlying pipe error",
            ]),
        new(
            "VDD_PIPE_DENIED",
            "Administrator access is needed to signal the driver",
            "Writing RELOAD_DRIVER to the driver's control pipe requires administrator rights and the elevated helper was not available.",
            "The UAC prompt was declined, or the elevated helper could not start.",
            [
                "Approve the administrator prompt when Optima asks for it",
                "Retry; the prompt only appears once per session",
            ]),
        new(
            "VDD_SETTINGS_LOCKED",
            "The driver settings file could not be updated",
            "Optima needs to edit vdd_settings.xml to apply the requested mode, and writing it requires administrator access.",
            "The file sits in a protected folder and no equivalent mode was available without an edit.",
            [
                "Pick a mode the driver already advertises (the Display page lists them)",
                "Run the request once more and approve the administrator prompt",
            ]),
        new(
            "VDD_MODE_NOT_APPLIED",
            "The display stayed at a different mode",
            "Windows accepted the mode change, but the display kept running at its own preferred mode instead of the requested one.",
            "The driver reverted to a mode it prefers; some driver builds do this for modes that were never loaded from its settings file.",
            [
                "Add the exact mode to vdd_settings.xml, then press RELOAD DRIVER on the Display page",
                "Pick the mode from the live list on the Display page instead of typing one",
            ]),
        new(
            "DEVICE_TOGGLE_FAILED",
            "Windows refused to change the virtual display device",
            "Optima asked the elevated helper to enable or disable the driver device and Windows refused the request.",
            "The device can be in a bad state (code 43, install half-finished) or another program holds it.",
            [
                "Check the device in Device Manager under Display adapters",
                "Reinstall the virtual display driver from the Display page",
            ]),
        new(
            "DISPLAY_ACCESS_DENIED",
            "Unable to change the display configuration",
            "Windows refused a display mode change Optima tried to apply.",
            "Another display-control utility was holding the display settings at the same moment.",
            [
                "Close other display-control utilities and try again",
                "Try the mode change once more; transient failures are common",
            ]),
        new(
            "DISPLAY_MODE_UNSUPPORTED",
            "That mode is not supported",
            "The requested resolution or refresh rate is not in the display's advertised mode list.",
            "The display (or driver, as configured) does not offer that mode on this machine.",
            [
                "Pick a mode from the list instead of typing a custom one",
                "For the virtual display, add the mode to vdd_settings.xml and press RELOAD DRIVER",
            ]),
        new(
            "DISPLAY_NOT_ACTIVE",
            "That display is not active",
            "The display Optima was asked to use has no current mode, so it cannot be part of the layout.",
            "The display was disabled, disconnected, or is a phantom entry that Windows no longer drives.",
            [
                "Enable the display on the Display page first",
                "Hide inactive displays on the Display page to drop phantom entries",
            ]),
        new(
            "DISPLAY_PRIMARY_FAILED",
            "Windows refused to change the primary display",
            "Optima tried to make a display the primary one and Windows rejected the change.",
            "Windows can refuse a primary switch while another display change is still settling.",
            [
                "Retry the request",
                "Change the primary display in Windows Settings once, then let Optima manage modes again",
            ]),
        new(
            "DISPLAY_RESTORE_FAILED",
            "Unable to restore the previous display layout",
            "After a session, Windows refused to re-apply the display layout that was saved before it started.",
            "The saved layout references a display that is no longer connected, or Windows is in a state where the topology cannot be applied exactly.",
            [
                "Reconnect any display that was attached when the session started",
                "Use the emergency-restore button on the Display page",
                "Set the layout by hand once; a later session will snapshot the new layout",
            ]),
        new(
            "ELEVATION_DECLINED",
            "Administrator access was declined",
            "Optima asked for the elevated helper for a system-level change and the prompt was declined or failed.",
            "Driver installs, driver device toggles, HKLM tweaks and ETW capture all need one approval; without it the affected feature skips that step.",
            [
                "Approve the administrator prompt and try again",
                "If no prompt appeared, check whether administrator prompts are suppressed by policy on this PC",
            ]),
        new(
            "TWEAK_WRITE_FAILED",
            "Windows refused the tweak change",
            "The elevated helper tried to write a performance tweak to the registry and Windows refused the write.",
            "Security software can block registry writes, or the value is owned by a policy.",
            [
                "Check the log on the Debug page for which value was refused",
                "Apply the tweak by hand if group policy manages it",
            ]),
        new(
            "GPG_NOT_FOUND",
            "Google Play Games was not found",
            "Optima looked for Google Play Games for PC before starting a session and did not find it.",
            "It is not installed, it was installed somewhere detection does not look, or an update moved it.",
            [
                "Install Google Play Games for PC from Google's site",
                "Set the Google Play Games folder under Settings, Path overrides, if it is installed somewhere unusual",
                "Run detection again from the Checks tab on the Debug page",
            ]),
        new(
            "POWER_PLAN_UNAVAILABLE",
            "This PC does not offer that power plan",
            "The profile asks for a power plan that Windows does not list on this PC. The session ran on the plan that was already active.",
            "A PC with Modern Standby (most recent laptops and many desktops) only offers Balanced and whatever plans its vendor added. Windows hides High performance and Ultimate Performance there and refuses to activate them.",
            [
                "Nothing needs repairing: the game starts and everything else in the profile is applied",
                "To stop the notice, use a profile whose power plan is Unchanged; the Performance page can save a copy of a built-in one",
                "Run powercfg /list in a terminal to see the plans this PC offers",
            ]),
        new(
            "POWER_PLAN_REFUSED",
            "Windows refused the power plan change",
            "Windows lists the plan the profile asks for, but would not make it active. The session ran on the plan that was already active.",
            "A company policy or a vendor power tool can own the active power plan and refuse a change from any other program.",
            [
                "Switch to the plan once in Windows' own power settings to see whether Windows allows it at all",
                "Check whether a vendor power or battery tool is managing the plan, and let it or Optima do it, not both",
                "Use a profile whose power plan is Unchanged if the plan is managed for you",
            ]),
        new(
            "LAUNCH_STEP_SKIPPED",
            "A step of the session was skipped",
            "One step that the game does not need in order to run failed, so the session carried on without it. The notice names the step and what Windows reported.",
            "The power plan, background cleanup and process tuning are improvements, not requirements. A failure in one of them used to stop the whole launch.",
            [
                "Read the notice on the Play page: it carries the exact error for the step",
                "Run the checks on the Debug page to verify the environment if the same step is skipped every time",
            ]),
        new(
            "SESSION_NOT_SAVED",
            "The session was not saved to the history",
            "The game ran and every setting was restored, but writing the session to sessions.db failed, so it is missing from the Sessions page.",
            "The history database was locked by another program, the disk is full, or the file is damaged.",
            [
                "Check free space on the drive that holds %LOCALAPPDATA%\\Optima",
                "Close other programs that may have sessions.db open",
                "Restore sessions.db from sessions.db.bak in the same folder if the file is damaged",
            ]),
        new(
            "UNEXPECTED",
            "Something went wrong during the session",
            "A step failed in a way Optima has no specific explanation for. Every temporary setting was restored.",
            "The error card names the step the session was in, and its developer details hold the full error, including the code Windows returned when a Windows call failed.",
            [
                "Open developer details on the error card and read the first lines: the phase and the Windows error",
                "Copy the developer details when reporting the problem; they are what makes it diagnosable",
                "Run the checks on the Debug page to verify the environment",
            ]),
        new(
            "GAME_NOT_FOUND",
            "Critical Ops is not installed in Google Play Games",
            "Detection could not find a playable Critical Ops install.",
            "The game is not installed, Google Play Games was updated and moved its data, or detection rules are stale.",
            [
                "Open Google Play Games and install Critical Ops",
                "Run detection again from the Checks tab on the Debug page",
                "Set the Google Play Games folder under Settings, Path overrides, if it is installed somewhere unusual",
            ]),
        new(
            "LAUNCH_FAILED",
            "Could not start Critical Ops",
            "Every launch strategy failed, and the session's temporary system changes were rolled back.",
            "Google Play Games may be signed out, outdated, or its bootstrapper refuses the launch URI.",
            [
                "Start Google Play Games manually and check it opens and is signed in",
                "Re-run detection from the Checks tab on the Debug page",
                "Configure a custom launch command in Settings as the last-resort strategy",
            ]),
        new(
            "GAME_START_TIMEOUT",
            "The game did not start within three minutes",
            "Google Play Games opened, but the game runtime never appeared, so the session was rolled back.",
            "The game can stall on a sign-in prompt, an update, or a first-time download.",
            [
                "Check Google Play Games for sign-in prompts or updates",
                "Try launching once from Google Play Games directly to clear any pending prompts",
            ]),
        new(
            "DRIVER_PACKAGE_MISSING",
            "No driver package is bundled with this build",
            "Optima installs a virtual display driver from its drivers folder, but that folder is empty or absent.",
            "This build was published without the bundled driver package, or the folder was deleted after publishing.",
            [
                "Re-publish or re-download the full build (the drivers folder must ship next to Optima.exe)",
                "Use the mock provider meanwhile; every display feature stays usable in simulation",
            ]),
        new(
            "DRIVER_INSTALL_FAILED",
            "The virtual display driver could not be installed",
            "Windows rejected the driver package during staging or device creation.",
            "The package signature may not be trusted on this machine, or the package targets a different Windows version.",
            [
                "Confirm the bundled driver package is digitally signed, since Windows refuses unsigned driver packages",
                "Check that the package targets 64-bit Windows 11",
                "See the log on the Debug page for the exact installer error",
            ]),
        new(
            "DRIVER_UNINSTALL_FAILED",
            "The virtual display driver could not be removed",
            "Windows refused to remove the driver device or package.",
            "The device was busy or the helper's remove request was refused mid-operation.",
            [
                "Close any program using the virtual display and retry",
                "Remove the device from Device Manager under Display adapters",
            ]),
        new(
            "OPTIMA_CRASHED",
            "Optima hit a fatal error",
            "An error reached the top of Optima without anything handling it, and Optima closed. Temporary system changes were rolled back first.",
            "This is a bug in Optima, not something wrong with the PC.",
            [
                "Open the issue's occurrences: the exception names the exact place",
                "Copy the report and send it; it is redacted",
                "Start Optima again; if a session was running, it offers to restore what was changed",
            ]),
        new(
            "PREVIOUS_RUN_CRASHED",
            "The previous run of Optima crashed",
            "The last time Optima ran it ended on a fatal error. It wrote the error down on the way out, and this run found it.",
            "A bug in Optima. The error is from the earlier run, so nothing in this run's log leads up to it.",
            [
                "Copy the report and send it: it carries the full error from the run that crashed",
                "If system settings look changed (display, power plan), accept the restore prompt at startup or use the emergency restore on the Display page",
            ]),
        new(
            "UNCLEAN_EXIT",
            "The previous run did not shut down normally",
            "Optima was running and then was not, without going through its exit: it was ended from Task Manager or by an installer, or the PC lost power or was reset.",
            "Nothing inside Optima failed, or there would be a recorded error. Whatever a session had changed at that moment could not be put back by that run.",
            [
                "Nothing to do if Optima was ended on purpose, for instance by installing a new build over it",
                "If a session was running, accept the restore prompt Optima shows at startup",
            ]),
        new(
            "BACKGROUND_TASK_FAILED",
            "A background task failed unnoticed",
            "Something Optima started in the background threw an error that nothing was waiting for. It was caught at the last moment and logged; the feature it belonged to may have stopped without saying so.",
            "A bug in Optima: a task was started and its result was never checked.",
            [
                "The occurrence names the method; the feature it belongs to is the one to distrust until Optima is restarted",
                "Copy the report and send it",
            ]),
        new(
            "RESTORE_STEP_FAILED",
            "A system setting could not be put back",
            "After a session Optima restores what it changed, one step at a time. One step failed. The others still ran.",
            "The thing to restore was gone or busy: a display that is no longer connected, a process that had already exited, a power plan Windows refused.",
            [
                "The occurrence says which setting; check that one by hand in Windows",
                "For the display layout, use the emergency restore on the Display page",
                "Restart the PC if a display stays in the wrong mode",
            ]),
        new(
            "SETTINGS_CORRUPT",
            "A settings file was damaged and set aside",
            "One of Optima's own files could not be read. It was renamed with a .corrupt suffix so nothing is lost, and Optima carried on with defaults in its place.",
            "The file was cut off while being written (a crash, a power loss, a full disk), or edited by hand into something that is not valid JSON.",
            [
                "Use restore settings backups on the Checks tab of the Debug page to put back the previous saved generation",
                "The damaged file sits next to the original, named .corrupt and a number, if it needs to be looked at",
            ]),
        new(
            "HELPER_MISSING",
            "The elevated helper is missing",
            "Optima.Watchdog.exe is not next to Optima.exe. Everything that needs administrator rights goes through it: the virtual display, frametime capture, machine-wide tweaks and the memory cleaner are unavailable without it.",
            "The install is incomplete, or security software removed the file.",
            [
                "Reinstall Optima from its setup",
                "Check whether security software quarantined Optima.Watchdog.exe, and restore it",
            ]),
        new(
            "VIRTUALIZATION_OFF",
            "Hardware virtualization is off",
            "The processor's virtualization support (Intel VT-x, AMD-V or SVM) is disabled in the firmware. Google Play Games cannot run the game without it.",
            "Many PCs ship with it switched off, and a firmware update can switch it off again.",
            [
                "Restart into the BIOS or UEFI setup and enable virtualization; the setup guide on the Play page walks through it",
                "This cannot be changed from inside Windows",
            ]),
        new(
            "HYPERVISOR_OFF",
            "No Windows hypervisor feature is on",
            "Neither Virtual Machine Platform, Windows Hypervisor Platform nor Hyper-V is enabled. Google Play Games needs one of them to run the game.",
            "They are optional Windows features and are off on a fresh install.",
            [
                "Run the setup wizard again from the Checks tab of the Debug page: it enables them with one administrator prompt",
                "Or turn on Virtual Machine Platform in Windows Features (OptionalFeatures.exe)",
                "Restart the PC afterwards; the features only take effect then",
            ]),
        new(
            "DISK_SPACE_LOW",
            "The system drive is nearly full",
            "Less than 3 GB is free on the drive Windows is installed on.",
            "Game updates and the emulator's disk image need room, and Windows itself misbehaves on a full drive.",
            [
                "Free up space on the system drive",
                "Storage in Windows Settings lists what is using it",
            ]),
        new(
            "VDD_RESTORE_PENDING",
            "A virtual display settings restore never ran",
            "A session changed the virtual display driver's settings file and left a marker naming the backup to put back. That restore has not happened, and the next virtual display session will carry it out without asking.",
            "The session ended without its restore: Optima was closed or crashed in the middle of it, or the restore could not write the file.",
            [
                "The Checks tab of the Debug page shows which backup would overwrite which file",
                "If the backup is what should be there, start a session with a virtual display profile and let the restore run",
                "To keep the settings file as it is now, delete backups\\vdd-settings.pending under %LOCALAPPDATA%\\Optima",
            ]),
        new(
            "UNCLASSIFIED",
            "An error Optima has no explanation for yet",
            "Something logged an error that no rule recognises. It is listed so that it does not go by unnoticed, with everything it carried.",
            "It is new or rare. Whether it matters depends on what stopped working.",
            [
                "Read the occurrence: the source names the part of Optima, the exception names the failure",
                "If something visibly stopped working, copy the report and send it",
                "Dismiss it if nothing is wrong; ignore it to never be shown it again",
            ]),
        new(
            "ISSUES_OVERFLOW",
            "More errors than the list can show",
            "More than fifty different unrecognised errors were logged in this run. The rest are counted here instead of being listed one by one.",
            "Something is failing in a loop, or on a broad front.",
            [
                "Start with the oldest issues on the list: the first failure is usually the cause of the rest",
                "Export the log from the Log tab of the Debug page for the full picture",
                "Restart Optima",
            ]),
    ];

    /// <summary>Case-insensitive lookup by code; null when unknown.</summary>
    public static ErrorCatalogEntry? Find(string code)
        => All.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The entry as plain text, ready for the clipboard or a support ticket.</summary>
    public static string FormatPlaintext(ErrorCatalogEntry entry)
    {
        var fixes = string.Join("\n", entry.HowToFix.Select(f => "- " + f));
        return "[" + entry.Code + "] " + entry.Title + "\n"
            + "What: " + entry.WhatHappened + "\n"
            + "Why: " + entry.WhyItHappens + "\n"
            + "How to fix:\n" + fixes;
    }
}
