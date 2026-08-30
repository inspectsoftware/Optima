# Changelog

Newest build first. This file ships next to Optima.exe and is rendered by the UPDATE LOG
page in the app, so keep the format: one `## date - title` heading per build, `-` bullets
under it, plain text, no em dashes.

## 2026-08-30 - The Optima Watchdog: presence, Discord, ranked stats, crash capture (v0.3.0)

- The Watchdog is now the app's always-on core: one lightweight presence loop watches
  the game and feeds everything else, and the watch-attach feature consumes it instead
  of running its own scan. The elevated helper was renamed to Optima.Watchdog and acts
  as the Watchdog's admin arm; the tray and settings now speak Watchdog language.
- Ranked session stats without touching the game: set your in-game name in Settings and
  Optima reads your PUBLIC Critical Ops profile from Critical Force's own public API
  before and after each run. The difference becomes the session's kills, deaths,
  assists and win/loss record, shown in the session detail. When a session contains
  exactly one decided match, it lands in the new MATCHES list automatically; everything
  else can be added or corrected by hand, and every row stays editable.
- Discord game activity: shows "Playing Critical Ops" with elapsed time while you play,
  through your local Discord client only. One-time setup in Settings (create a free
  Discord application, paste its ID); without it the feature stays dormant.
- Crash capture: when the game ends and Google Play Games' own logs carry failure
  markers, the Watchdog saves a crash bundle (plain-text timeline plus the relevant
  log excerpt, with minidumps referenced by name only). The DIAGNOSTICS page lists
  bundles and exports a redacted zip that is safe to share; a capture-now button
  grabs the current platform logs on demand.
- Start with Windows: an optional launcher-owned autostart entry starts the Watchdog
  minimized to the tray at sign-in, and removes itself when you turn it off.
- Session database schema v2: per-session stat deltas, game-version field and the new
  matches table, with the previous database backed up before migration.

## 2026-08-30 - Liquid glass redesign, themes, accents and the Aureum rebrand (v0.2.0)

- Complete visual redesign: a liquid-glass interface with a deep blue-charcoal ground,
  translucent layered surfaces, soft corners, and the new Aureum gold accent. The window
  now uses the Windows acrylic backdrop where available, with a solid fallback.
- Dark mode and Light mode: pick a theme on the Settings page under APPEARANCE. The
  switch applies the moment you save, no restart needed, and both palettes keep the
  4.5:1 WCAG AA contrast floor on the dimmest text.
- Accent customization: six presets (Aureum Gold, Frost, Mint, Rose, Violet, Slate) plus
  a custom hex field. Hover, pressed, glow and on-accent ink colors are derived
  automatically, and the ink always stays readable on any accent you pick.
- Rebrand: the app is now Optima by Aureum. The title bar carries the new wordmark and
  byline, and a LICENSE file (all rights reserved, source visible) now ships at the
  repository root.
- Navigation regrouped into LAUNCH, MONITOR, CONFIGURE and SUPPORT sections with a glass
  pill on the active row. Every page now has a shortcut: Alt+1 through Alt+0, plus Alt+D
  for the developer page, which previously had none.
- The status tags, meters and spinners were modernized: status values render as tinted
  chips, the block-character meters became smooth accent tracks, and the loading spinner
  is a rotating arc. Numbers still sit beside every meter, never replaced by it.
- Fixed a layout overflow on the HOME page where long hardware names could push the
  launch card and its PLAY button past the right edge of the window.
- The in-game FPS overlay keeps its dark, high-contrast ground in both themes on purpose,
  and picked up soft corners to match the new language.

## 2026-08-28 - Custom refresh rate fix and a real driver uninstall

- Custom mode fix: applying a custom mode (for example 1920x1080 @ 240 Hz) no longer
  lands on the driver's 999 Hz placeholder. Optima now checks what the driver actually
  advertises to Windows instead of trusting vdd_settings.xml, reloads the driver and
  waits until the mode really appears, then verifies the display settled at the
  requested mode and re-applies once if the driver reverted it. If the mode still does
  not stick, the app reports the actual mode instead of claiming success.
- Placeholder rates (999/9999 Hz) are filtered out of every mode list; valid refresh
  rates are capped at 500 Hz.
- Uninstall driver: the button on the Display page now asks for confirmation and
  performs a complete uninstall, removing both the device and the staged driver
  package from the Windows DriverStore behind one administrator prompt. The install
  banner returns afterwards so the driver can be reinstalled any time.
- The project source now lives on GitHub (private repository), with the built
  Optima.exe attached to each release.

## 2026-08-27 - Overlay, sessions, network, watch mode, guided benchmark

- FPS overlay: Alt+F10 (or automatic during sessions when enabled in Settings) shows a
  click-through, never-activating FPS/frametime readout over the borderless game, with
  configurable corner and opacity and an optional network line.
- FPS capture fix: the ETW trace no longer assumes the emulator process presents the
  frames; every game-related process is a candidate and the trace locks onto whichever
  one actually presents. A probe on the DEVELOPER page lists presenting processes when
  capture stays silent.
- SESSIONS page (Alt+0): trends over recent sessions as sparklines, full history with
  launch kind, network quality and config-change markers, and a per-session drill-down
  of the stored per-second FPS series. Session rows now record enabled tweak ids and a
  profile content hash; the database migrates automatically with a backup.
- Network quality: passive ping / jitter / loss during sessions, preferring the game's
  own endpoints and falling back to a reference host with an explicit label. Live on the
  SYSTEM page, stored per session.
- Watch mode (off by default): starting the game outside Optima applies the full
  selected profile and restores everything when the game exits. Never double-applies
  with PLAY and never triggers a surprise UAC prompt. Toggle in Settings or the tray.
- Guided benchmark on the SESSIONS page: A vs B over N alternating runs, drift-aborted
  when tweaks or profiles change mid-plan, with a per-run Welch verdict ahead of the
  pooled view.
- publish.ps1: one command refreshes publish\Optima.exe (stops running instances,
  cleans stale files, publishes the helper and the app in the right order).

## 2026-08-26 - Bundled driver install and the Optima rename

- The virtual display driver package travels inside the build and installs from the
  Display page: one administrator prompt, no Device Manager.
- The Display page no longer shows an install button when there is nothing to install.
- Project renamed to Optima; black monochrome terminal redesign of the whole UI;
  em dashes removed from source, UI text and docs.

## 2026-08-25 - Initial release

- Full initial implementation: detection, launch strategies, performance profiles,
  virtual display control, monitoring, benchmark mode, crash recovery, diagnostics and
  the elevated helper.
