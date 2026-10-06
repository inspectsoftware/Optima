# Optima

A Windows launcher and performance companion for **Critical Ops** on **Google Play Games for PC**.
By Inspect Software; see [LICENSE](LICENSE).

Optima runs beside the game, never inside it: no injection, no memory access, no binary or
network tampering. It sets up the environment with documented Windows APIs and restores every
change when the game exits. One exception, opt-in: the Boost memory cleaner empties Windows'
standby file cache through the same system call RAMMap and ISLC use, which Windows does not
document and which has nothing to restore (the cache refills by itself). The optional 0.5 ms
timer step is likewise only reachable through an undocumented call; it is released on exit.

```
pick a profile → PLAY
  → virtual display (e.g. 1920x1080 @ 240 Hz)
  → power plan, priority, EcoQoS
  → Critical Ops starts through Google Play Games
  → external FPS / frametime capture (ETW)
  → game closes → everything restored → session saved
```

## Features

- **One-click sessions** with Default / Balanced / Competitive profiles or your own.
- **Optima Virtualization**: a bundled virtual display driver, installed by the setup with one
  administrator prompt (or any time from the Display page) for high-refresh modes the monitor
  cannot offer.
- **FPS overlay and session history**: external frametime capture, average / 1% / 0.1% lows,
  per-session network quality, trends and an A-vs-B benchmark that refuses to call noise a gain.
- **Watch mode**: start the game any way you like and Optima applies the profile from the tray.
- **Windows tweaks** with an on/off toggle each, originals captured and restored.
- **Kill switch** (Ctrl+Alt+K), floating log console (Alt+F9), overlay toggle (Alt+F10).
- **Debug page**: one place for what went wrong. Optima reads its own log as it is written and
  runs quick checks in the background, and lists every problem once as an issue, with what it
  means, how to put it right and its evidence; no error goes by unlisted. Under that: environment
  checks with their fixes
  (virtualization, platform, driver, refresh rate), a log where any line opens to its full error
  (the whole exception, the code Windows returned, the error guide's fix) and copies as a redacted
  report that stands on its own, crash bundles from the platform's own logs, the error guide, and a
  redacted support export.
- **Discord activity**, ranked session stats from the public Critical Ops profile API, and news.
- **OptimaBot**, the Discord bot that draws Critical Ops stats cards and links your Discord account
  to the game account Optima already tracks: `/link` here, a code into Settings, and `/searchplayer`
  from then on. It lives in its own repository,
  [inspectsoftware/OptimaBot](https://github.com/inspectsoftware/OptimaBot), so building or
  installing Optima never needs it.

## Downloading

The latest build is on the [releases page](https://github.com/inspectsoftware/Optima/releases).
Every release carries:

- `Optima-Setup-<version>.exe`, the installer. Per-user, so the wizard itself needs no
  administrator rights; it installs the Optima virtual display driver during setup (one UAC
  prompt, and you can uncheck that step), adds the Start Menu shortcut, offers the optional
  sign-in autostart, and uninstalls cleanly while keeping your data in `%LOCALAPPDATA%\Optima`.
  This is the one to take.
- `Optima-v<version>-win-x64.zip`, the same build as a portable folder. Unpack it anywhere and
  run `Optima.exe`.
- Source code (zip / tar.gz), the repository at that tag, for building it yourself.

Both binaries are unsigned, so Windows SmartScreen warns about an unknown publisher the first
time you run either of them.

## Building

Requires the .NET 10 SDK on Windows 10/11.

```bash
dotnet build
dotnet test
```

`.\publish.ps1` produces the runnable self-contained build in `publish/` (it stops a running
Optima first). Add `-Run` to start it.

`.\installer.ps1` packages that build into `artifacts/Optima-Setup-<version>.exe`, a per-user
installer (Start Menu shortcut, optional sign-in autostart, proper uninstaller; user data is
kept on uninstall). Setup also installs the bundled virtual display driver through
`Optima.Watchdog.exe --install-driver`, the same code path the Display page uses. Add `-Publish`
to republish first, `-Run` to launch the setup when done.

```
src/Optima.Core               logic, models, orchestrator, statistics (no Windows deps)
src/Optima.Platform.Windows   Win32/WMI: display, power, processes, elevation broker
src/Optima.Driver             virtual display providers
src/Optima.Monitoring         hardware monitor, ETW metrics client, SQLite store
src/Optima.Watchdog           the elevated helper (whitelisted commands only)
src/Optima.App                WPF UI
tests/Optima.Tests            xunit suite
```

Without the game: Settings > "mock fps provider" fakes the frametime feed, and
`%LOCALAPPDATA%\Optima\detection.json` with `"emulatorProcessPatterns": ["^notepad$"]` and
`"gameWindowTitlePattern": "Notepad"` lets Notepad stand in for the game.

## Community

Questions, builds and feedback: join the Optima Discord server at
<https://discord.gg/bGuJ4tvsF7>.

## Data

Everything lives under `%LOCALAPPDATA%\Optima\`: `config.json`, `profiles.json`,
`detection.json`, `sessions.db`, `logs/`, `recovery/`, `backups/`, `crashes/`, `health/`
(how the last run ended, the fatal error it ended on if there was one, and the issues you chose
to ignore).

## Security boundaries

The UI never runs elevated. `Optima.Watchdog.exe` performs only whitelisted, validated
operations over a private, ACL-restricted pipe. FPS measurement is an ETW present trace. The
app contacts, exhaustively: the game's own endpoints or a reference host (ICMP),
`default.prod.copsapi.criticalforce.fi` (public profile, only with an in-game name set),
`criticalopsgame.com` (news), `discord.com` (which presence artwork exists, only with presence on),
OptimaBot, the community Discord bot (account linking), and the local Discord client over IPC.
Updates arrive as installers; the app does not check for them.
Everything that leaves the app as text is redacted: the log export, a copied report, crash zips
and the support archive mask tokens, the Windows user name, the machine name and user profile
paths, and so does the log's detail pane on the Debug page. The log files on disk are the raw
originals and stay on the PC.

Pressing "link account" in Settings sends the link code and the player identity to OptimaBot, once,
and stores the answer. Nothing else is sent there, ever. Running your own bot: set `discordBotUrl`
in `config.json`; the app has no field for it.

Optima is an independent project, not affiliated with Critical Force Oy or Google LLC.
