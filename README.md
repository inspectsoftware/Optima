# COPS Bootstrapper

A Windows bootstrapper and performance-management launcher for **Critical Ops** running through
**Google Play Games for PC**.

COPS Bootstrapper is a completely separate desktop application. It never injects into the game,
never modifies game binaries or memory, and never touches game networking or authentication. It
orchestrates the *environment around* the game using documented Windows APIs: virtual display
configuration, power plans, process scheduling, external performance measurement — and restores
every temporary change when the game exits.

```
Open COPS Bootstrapper → pick a profile → PLAY
  → virtual display configured (e.g. 1920x1080 @ 240 Hz)
  → Windows optimizations applied (power plan, priority, EcoQoS)
  → Critical Ops launches through Google Play Games
  → external FPS / frametime monitoring (ETW, PresentMon-style)
  → game closes → original system state restored → session statistics
```

## Interface

A black, monochrome, restrained-terminal UI. White is the only accent; hue appears solely inside
status tags (`[ OK ]` / `[WARN]` / `[FAIL]`), which is roughly two percent of the pixels on screen.
Structure and data are set in Cascadia Mono and the proportional face is reserved for running prose,
so the app reads as a terminal without becoming hard to read where it actually explains something.

- **Custom window chrome** via `WindowChrome` — native snap, resize and Aero Snap are preserved.
  The caption carries a breadcrumb and a live `[ RUNNING mm:ss ]` session tag.
- **Numbered navigation** wired to real **Alt+1…9** shortcuts, with a `>` prompt marker on the
  active row that follows navigation from any source.
- **ASCII meters** (`████████░░░░`) beside — never instead of — the numeric readouts.
- **Dot-leader rows** connecting every label to its value, from one shared row template.
- **Contrast floor of 4.5:1 (WCAG AA)** on the dimmest text, verified against rendered pixels
  (`#757D88` on `#07080A` measures 4.81:1) so no gray is too dim to read.
- Every WPF control is re-templated — scrollbars, ComboBox popups, ListBox selection, checkboxes
  (`[X]` / `[ ]`), expanders and progress indicators. No stock Windows chrome leaks through.

## Features

- **Detection** — Google Play Games install (registry → protocol handler → known folders →
  manual path), installed games via `googleplaygames://` shortcut scanning, hardware/OS inventory,
  virtualization state. All detection rules are configuration-driven
  (`%LOCALAPPDATA%\COPSBootstrapper\detection.json`) so Google Play Games updates can be absorbed
  without a rebuild.
- **Launching** — a strategy chain: protocol URI → `Bootstrapper.exe "<uri>"` → game shortcut →
  user-defined command. First one that works wins.
- **Virtual display control** — provider abstraction with a full implementation for the IddCx
  "Virtual Display Driver" (MikeTheTech-style): non-destructive `vdd_settings.xml` editing with
  automatic backup, `RELOAD_DRIVER` over the driver's control pipe, device enable/disable through
  the elevated helper, Windows-side mode switching that is never written to the registry.
  A fully functional mock provider backs tests and machines without the driver.
- **Performance profiles** — Default / Balanced / Competitive presets plus custom profiles
  (power plan, process priority, CPU affinity, EcoQoS power throttling, opt-in background app
  cleanup). Every setting documents what it changes, its benefit, its downside, and whether it
  needs a restart or administrator rights. Profiles import/export as JSON.
- **Monitoring** — CPU/GPU/RAM dashboard (NVML for NVIDIA temperature/clocks, Windows GPU
  performance counters as fallback) and external FPS/frametime capture via an ETW present trace
  (DXGI provider — the PresentMon approach; nothing runs inside the game). Session history in
  SQLite with average / 1% low / 0.1% low FPS and P95/P99 frametimes.
- **Benchmark mode** — compare two profiles across sessions with a Welch-test noise guard:
  differences inside run-to-run noise are reported as *no real advantage*, not as gains.
- **Safety** — a recovery snapshot is persisted to disk *before* any system change and updated as
  the session progresses. Crashes are detected on next start with a *Restore previous system
  settings* prompt; driver-settings edits carry their own on-disk crash marker. Display changes
  are restored from a CCD topology snapshot (exact restore first, tolerant retry second) and an
  emergency-restore button lives on the Display page.
- **Elevation** — the UI always runs non-elevated. A separate helper
  (`COPSBootstrapper.Elevated.exe`) performs only whitelisted, argument-validated operations
  (display device toggle, driver pipe write, ETW session, bcdedit read) over a private,
  ACL-restricted named pipe with length-prefixed JSON frames.
- **Diagnostics** — virtualization/hypervisor, Google Play Games, Critical Ops, virtual driver,
  refresh rate, GPU driver, disk space, admin availability — each with status, reason, and a
  recommended fix. A hidden Developer page shows raw processes, resolved paths, driver
  capabilities and the active detection rules.

## Solution layout

```
COPSBootstrapper.slnx
src/
  COPSBootstrapper.Core             pure logic: models, interfaces, orchestrator, statistics,
                                    detection engine, recovery, IPC framing (no Windows deps)
  COPSBootstrapper.Platform.Windows Win32/WMI implementations: display (CDS + CCD), power,
                                    processes, probes, launchers, elevation broker
  COPSBootstrapper.Driver           virtual display providers (MttVdd, Mock) + settings editor
  COPSBootstrapper.Monitoring       hardware monitor, NVML, ETW metrics client, SQLite store
  COPSBootstrapper.Elevated         the elevated helper (whitelisted commands, ETW host)
  COPSBootstrapper.App              WPF UI (MVVM, CommunityToolkit.Mvvm, Serilog, DI)
tests/
  COPSBootstrapper.Tests            xunit suite (statistics, detection, orchestrator, recovery,
                                    VDD settings editing, IPC framing, providers)
tools/
  COPSBootstrapper.LiveCheck        CLI smoke-check for the display stack on real hardware
```

## Building

Requires the **.NET 10 SDK** on Windows 10/11.

```bash
dotnet build
dotnet test
```

Run the app:

```bash
dotnet run --project src/COPSBootstrapper.App
```

### Publishing a self-contained build

The app and the elevated helper must land in the same folder:

```bash
dotnet publish src/COPSBootstrapper.App -c Release -r win-x64 --self-contained -o publish
dotnet publish src/COPSBootstrapper.Elevated -c Release -r win-x64 --self-contained -o publish
```

`publish/CopsBootstrapper.exe` is the application.

## Data & configuration

Everything lives under `%LOCALAPPDATA%\COPSBootstrapper\`:

| File / folder    | Purpose                                                       |
|------------------|---------------------------------------------------------------|
| `config.json`    | app settings (selected profile, provider, log level, paths)   |
| `profiles.json`  | user-created launch profiles                                  |
| `detection.json` | detection-rule overrides (§ update resilience)                |
| `sessions.db`    | SQLite session history                                        |
| `logs/`          | rolling daily logs (14 days)                                  |
| `recovery/`      | pending-session snapshot for crash recovery                   |
| `backups/`       | driver settings backups + crash marker                        |

## Requirements & notes

- **Google Play Games for PC** with Critical Ops installed.
- For high-refresh virtual display modes: an IddCx virtual display driver
  (`vdd_settings.xml` + `MTTVirtualDisplayPipe` control pipe). Without one, the app runs with the
  mock provider and all display features remain usable in simulation.
- FPS/frametime capture and virtual-display device toggling need one UAC approval for the
  elevated helper; everything else runs non-elevated. If the prompt is declined the session
  continues without those features.
- Firmware virtualization (VT-x / AMD-V) must be enabled in BIOS/UEFI for Google Play Games
  itself — the diagnostics page explains this; the app never modifies firmware or boot settings.

## Security boundaries (by design)

No DLL injection, no game memory access, no binary/APK patching, no anti-cheat interference,
no packet manipulation, no gameplay automation, no process/debugger hiding. FPS measurement is
strictly external (ETW present events). Logs never contain tokens or credentials, and log export
runs an additional redaction pass.
