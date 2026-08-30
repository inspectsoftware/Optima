# Optima

A Windows bootstrapper and performance-management launcher for **Critical Ops** running through
**Google Play Games for PC**. Made by Aureum; see [LICENSE](LICENSE).

Optima is a completely separate desktop application. It never injects into the game,
never modifies game binaries or memory, and never touches game networking or authentication. It
orchestrates the *environment around* the game using documented Windows APIs: virtual display
configuration, power plans, process scheduling, external performance measurement, and restores
every temporary change when the game exits.

```
Open Optima → pick a profile → PLAY
  → virtual display configured (e.g. 1920x1080 @ 240 Hz)
  → Windows optimizations applied (power plan, priority, EcoQoS)
  → Critical Ops launches through Google Play Games
  → external FPS / frametime monitoring (ETW, PresentMon-style)
  → game closes → original system state restored → session statistics
```

## Interface

A liquid-glass UI: a deep blue-charcoal ground (or a warm paper ground in Light mode) with
translucent layered surfaces, soft radii, and one warm accent, Aureum gold by default. The
window sits on the Windows acrylic backdrop where available and falls back to a solid ground
where it is not. Headings and controls use the humanist face; data, metrics and labels stay
in Cascadia Mono so numbers keep their alignment and their voice.

- **Dark and Light themes plus a custom accent color**, chosen on the Settings page and applied
  live on save; every brush is resolved dynamically, so a theme switch repaints in place.
- **Custom window chrome** via `WindowChrome`. Native snap, resize and Aero Snap are preserved.
  The caption carries the Optima by Aureum wordmark, a breadcrumb, and a live session tag.
- **Sectioned navigation** (LAUNCH / MONITOR / CONFIGURE / SUPPORT) wired to real **Alt+1..0**
  shortcuts plus **Alt+D** for the developer page, with a glass pill and accent bar marking the
  active row from any navigation source.
- **Smooth accent meters** beside the numeric readouts, never instead of them.
- **Dot-leader rows** connecting every label to its value, from one shared row template.
- **Contrast floor of 4.5:1 (WCAG AA)** on the dimmest text in both palettes (dark: `#8089A0`
  on `#0B0D12` measures 5.5:1; light: `#6B675E` on `#F2F1EE` measures 4.7:1).
- Every WPF control is re-templated: scrollbars, ComboBox popups, ListBox selection, checkboxes,
  expanders and progress indicators. No stock Windows chrome leaks through, in either theme.
- The in-game overlay deliberately keeps its dark ground in both themes; it renders over live
  gameplay, where a light pane would be unreadable.

## Features

- **Detection**: Google Play Games install (registry → protocol handler → known folders →
  manual path), installed games via `googleplaygames://` shortcut scanning, hardware/OS inventory,
  virtualization state. All detection rules are configuration-driven
  (`%LOCALAPPDATA%\Optima\detection.json`) so Google Play Games updates can be absorbed
  without a rebuild.
- **Launching** uses a strategy chain: protocol URI → `Bootstrapper.exe "<uri>"` → game shortcut →
  user-defined command. First one that works wins.
- **Bundled driver install**: a virtual display driver package placed in `drivers/` travels
  inside the build and is installed on demand from the Display page: one administrator prompt,
  no Device Manager and no `devcon`. Optima stages the package with `pnputil`, creates the
  **root-enumerated device node** via SetupAPI (the step that package staging alone does not
  do, and the usual reason a manual install is needed), and writes a default `vdd_settings.xml`
  without overwriting an existing one. Uninstall reverses it. See [drivers/README.md](drivers/README.md).
- **Optima Virtualization** (virtual display control): provider abstraction with a full
  implementation for the bundled IddCx virtual display driver: non-destructive `vdd_settings.xml`
  editing with automatic backup, `RELOAD_DRIVER` over the driver's control pipe, device
  enable/disable through the elevated helper, Windows-side mode switching that is never written
  to the registry. A fully functional mock provider backs tests and machines without the driver.
  The driver itself is the open-source **Virtual Display Driver** by MikeTheTech (MIT licensed,
  see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)); the Windows device keeps its upstream
  name since the signed package ships unmodified.
- **Display list editing**: the attached-displays list supports custom names, manual ordering
  and hiding rows (including a one-click "hide inactive" filter for phantom 0x0 outputs). All
  cosmetic and Optima-only, persisted in `config.json`; Windows display config is untouched.
  Custom names follow the display across the whole UI, including the HOME `Display` readout,
  which tracks the display the game will actually render on (the virtual display when active).
- **Kill switch**: one click (or **Ctrl+Alt+K** from anywhere, even in-game) hard-terminates the
  emulator process tree, no confirmation. **Alt+F9** toggles a floating, always-on-top log
  console that appears over the game without stealing focus.
- **FPS overlay**: **Alt+F10** (or automatically during sessions, when enabled in Settings)
  shows a small always-on-top FPS/frametime readout over the borderless game. The window is
  click-through and never activates, so it cannot interfere with input; data comes from the
  same external ETW trace as session statistics. Corner, opacity and an optional
  ping/jitter/loss line are configurable. Capture no longer assumes the emulator process
  presents the frames: every game-related process is a candidate and the trace reports
  whichever one actually presents, with a Developer-page probe that lists presenting
  processes when capture is silent.
- **Watch mode** (off by default): with Optima in the tray, starting Critical Ops outside
  Optima applies the full selected profile automatically (power plan, virtual display,
  process tuning) and restores everything when the game exits. Sessions are recorded like
  PLAY sessions; FPS capture joins only when the elevated helper is already running, so
  watch mode never causes a surprise UAC prompt. Toggle it from Settings or the tray menu.
- **Network quality**: passive ping/jitter/loss measurement during sessions, preferring the
  game's own remote endpoints (read from the connection table, nothing is intercepted) and
  falling back to a configurable reference host with an explicit "[ REF HOST ] link quality"
  label. Live readout on the SYSTEM page and optionally on the overlay; per-session averages
  land in history.
- **Windows tweaks**: a curated catalog of the widely published gaming tweaks (Game DVR off,
  Win32PrioritySeparation, SystemResponsiveness, network throttling, MMCSS games task, HAGS,
  mouse acceleration, accessibility hotkeys, and more), each with an individual on/off toggle
  on the Performance page. Unlike profile settings these persist until turned off. Every tweak
  shows what it changes, its benefit and its downside before you enable it; the original
  registry values are captured before the first write and restored on disable, and a
  "revert all" button undoes everything. HKLM writes go through the elevated helper's
  whitelist: the IPC payload carries only a catalog tweak id, never arbitrary registry paths.
- **Performance profiles**: Default / Balanced / Competitive presets plus custom profiles
  (power plan, process priority, CPU affinity, EcoQoS power throttling, opt-in background app
  cleanup). Every setting documents what it changes, its benefit, its downside, and whether it
  needs a restart or administrator rights. Profiles import/export as JSON.
- **Monitoring**: CPU/GPU/RAM dashboard (NVML for NVIDIA temperature/clocks, Windows GPU
  performance counters as fallback) and external FPS/frametime capture via an ETW present trace
  (DXGI provider, the PresentMon approach; nothing runs inside the game). Session history in
  SQLite with average / 1% low / 0.1% low FPS and P95/P99 frametimes.
- **SESSIONS page** (Alt+0): trends over recent sessions as inline sparklines, the full
  history table (with launch kind, network quality, and a `[ CFG ]` marker whenever the
  profile content or tweak set changed between sessions), and a per-session drill-down that
  renders the stored per-second FPS series. Session rows record the enabled tweak ids and a
  content hash of the profile, so a renamed profile trends together and an edited one
  visibly breaks the trend.
- **Benchmark mode** compares two profiles across sessions with a Welch-test noise guard:
  differences inside run-to-run noise are reported as *no real advantage*, not as gains.
- **Guided benchmark**: a wizard on the SESSIONS page runs "A vs B over N runs each" with
  alternating profiles, counts only runs that produced FPS data, refuses to continue if
  tweaks or profiles change mid-plan, and reports a per-run Welch verdict (each run's
  average FPS is one observation, with Welch-Satterthwaite degrees of freedom) ahead of the
  pooled per-second view, which overstates significance on autocorrelated frame data.
- **Safety**: a recovery snapshot is persisted to disk *before* any system change and updated as
  the session progresses. Crashes are detected on next start with a *Restore previous system
  settings* prompt; driver-settings edits carry their own on-disk crash marker. Display changes
  are restored from a CCD topology snapshot (exact restore first, tolerant retry second) and an
  emergency-restore button lives on the Display page.
- **Elevation**: the UI always runs non-elevated. A separate helper
  (`Optima.Watchdog.exe`) performs only whitelisted, argument-validated operations
  (display device toggle, driver pipe write, ETW session, bcdedit read) over a private,
  ACL-restricted named pipe with length-prefixed JSON frames.
- **Update log**: an in-app page that renders [CHANGELOG.md](CHANGELOG.md) (shipped next to
  the executable) plus the exact version and build timestamp of the running binary, so
  "which build am I on and what changed" has an answer inside the app. Add an entry to the
  changelog with each change; `publish.ps1` ships it automatically.
- **Diagnostics**: virtualization/hypervisor, Google Play Games, Critical Ops, virtual driver,
  refresh rate, GPU driver, disk space, admin availability, each with status, reason, and a
  recommended fix. A hidden Developer page shows raw processes, resolved paths, driver
  capabilities and the active detection rules.

## Solution layout

```
Optima.slnx
src/
  Optima.Core             pure logic: models, interfaces, orchestrator, statistics,
                                    detection engine, recovery, IPC framing (no Windows deps)
  Optima.Platform.Windows Win32/WMI implementations: display (CDS + CCD), power,
                                    processes, probes, launchers, elevation broker
  Optima.Driver           virtual display providers (MttVdd, Mock) + settings editor
  Optima.Monitoring       hardware monitor, NVML, ETW metrics client, SQLite store
  Optima.Watchdog         the elevated helper (whitelisted commands, ETW host)
  Optima.App              WPF UI (MVVM, CommunityToolkit.Mvvm, Serilog, DI)
tests/
  Optima.Tests            xunit suite (statistics, detection, orchestrator, recovery,
                                    VDD settings editing, IPC framing, providers)
tools/
  Optima.LiveCheck        CLI smoke-check for the display stack on real hardware
```

## Building

Requires the **.NET 10 SDK** on Windows 10/11.

```bash
dotnet build
dotnet test
```

Run the app:

```bash
dotnet run --project src/Optima.App
```

### Publishing a self-contained build

One command produces (or refreshes) the runnable build:

```powershell
.\publish.ps1
```

`publish/Optima.exe` is the application; add `-Run` to start it right after. The script
stops any running instance (which would lock the files), cleans `publish/` so stale
binaries from earlier publishes cannot mix in, and publishes the elevated helper and then
the app into the same folder, in that order so the app's newer shared dependencies win.
Run it again after every change you want in `Optima.exe`; a build alone only updates
`bin/`, never the publish folder.

## Developing without the real game

Two developer conveniences make every game-dependent feature testable on any machine:

- **Mock FPS provider** (Settings → "mock fps provider", restart applies it): replaces the
  ETW trace with a deterministic synthetic feed, so the overlay, session statistics and the
  guided benchmark all run without the elevated helper or the game.
- **Fake game via detection overrides**: edit `%LOCALAPPDATA%\Optima\detection.json` and set
  `"emulatorProcessPatterns": ["^notepad$"]` and `"gameWindowTitlePattern": "Notepad"`.
  Launching Notepad then exercises watch mode attach/restore, the overlay lifecycle and
  session recording end to end; closing it restores everything.

## Data & configuration

Everything lives under `%LOCALAPPDATA%\Optima\`:

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
  (`vdd_settings.xml` + `MTTVirtualDisplayPipe` control pipe). Ship one in `drivers/` and Optima
  installs it for the user automatically; without one, the app runs with the mock provider and
  all display features remain usable in simulation.
- Driver packages must be signed by a publisher the machine trusts. Windows rejects unsigned
  packages, and Optima will not disable signature enforcement or install certificates for you.
- FPS/frametime capture and virtual-display device toggling need one UAC approval for the
  elevated helper; everything else runs non-elevated. If the prompt is declined the session
  continues without those features.
- Firmware virtualization (VT-x / AMD-V) must be enabled in BIOS/UEFI for Google Play Games
  itself. The diagnostics page explains this; the app never modifies firmware or boot settings.

## Security boundaries (by design)

No DLL injection, no game memory access, no binary/APK patching, no anti-cheat interference,
no packet manipulation, no gameplay automation, no process/debugger hiding. FPS measurement is
strictly external (ETW present events). Logs never contain tokens or credentials, and log export
runs an additional redaction pass.

Every outbound endpoint the app can contact, exhaustively:

- ICMP ping to the game's own connection endpoints (or the configured reference host) for the
  in-session network quality readout.
- `default.prod.copsapi.criticalforce.fi` - Critical Force's PUBLIC profile API, read-only,
  contacted only when an in-game name is configured in Settings (session stat deltas).
- `criticalopsgame.com` - the official updates page, read for the news feed and the
  game-updated banner; cached locally.
- `api.github.com` and GitHub release downloads - the launcher's own update check and
  packages.
- Discord, via LOCAL named-pipe IPC only (game activity), never over the network.

Crash bundles reference the platform's minidump names but never copy the dumps, and the
redacted export scrubs user paths and machine names before anything leaves the machine.
