# Changelog

Newest build first. This file ships next to Optima.exe and is rendered by the UPDATE LOG
page in the app, so keep the format: one `## date - title` heading per build, `-` bullets
under it, plain text, no em dashes.

## 2026-09-02 - Glass redesign, phase 1: directions and the renderer prototype

- v0.7.0 liquid-glass redesign starts on its own branch. Three design directions
  (Obsidian, Prism, HUD) live on the design canvas with a shared component strip;
  nothing in the shipped UI changes yet.
- Glass lab on the Developer page (also opens standalone with the "--glass-lab"
  switch): the new in-app glass renderer, a pixel shader that blurs and refracts the
  content beneath a panel with a chromatic fringe and a pointer-following highlight,
  over a drifting ambient field, with sliders for every input and a frame-rate readout.
  Drift and the light freeze when the window is not foreground and when Windows
  animation effects are off.

## 2026-09-02 - Driver reminder when Optima closes

- Closing Optima completely (the window's close button without "keep in tray", or EXIT in
  the tray menu) now warns that the virtual display driver is still installed and stays
  active in Windows. The pop-up offers "Keep driver", "Uninstall driver" (one
  administrator prompt, then Optima closes) or "Cancel" to stay in the app. Nothing is
  asked when the driver is not installed.
- If the removal fails (for example a declined administrator prompt), Optima says so and
  stays open instead of quietly leaving the driver behind.
- Hiding to the tray, crashes and Windows logoff never show the question.

## 2026-08-30 - Company attribution

- Optima is made by Aureum at Inspect Software: the assembly Company metadata,
  LICENSE copyright line, README and the LEGAL page now name Inspect Software as
  the company. The "by Aureum" byline is unchanged.

## 2026-08-30 - Discord card buttons

- The Discord activity card now carries two buttons for everyone who sees it:
  "Join Discord" opens the community invite and "Private Beta" opens the GitHub
  releases page. Note that Discord never shows your own buttons on your own
  card; other people see them.

## 2026-08-30 - Launch card layout fix

- The profile summary on HOME no longer runs under the PLAY button: the text
  column keeps 20px of separation and wraps onto a second line when needed.

## 2026-08-30 - Discord activity artwork

- The Discord activity card now shows the Optima mark (gold on black, 512px)
  instead of the blank placeholder. The image is served from the public repo,
  so it works without uploading art assets to the Discord application.

## 2026-08-30 - The glass terminal (v0.6.0)

- The dark theme is now a true black terminal behind real glass: neutral near-black
  ground with no blue tint, the window sheet at 80 percent so the acrylic backdrop
  genuinely bleeds through, brighter glass layers, and a specular rim on every card
  that catches light at the top edge. The solid fallback for machines without the
  backdrop is unchanged.
- Everything structural is monospace now: titles, navigation, buttons, labels and
  data all use Cascadia Mono. Long-form prose (news bodies, legal text) stays
  humanist for reading comfort.
- Split-radius shape language: glass chrome (window, cards, controls) keeps its soft
  corners while terminal data goes hard-edged; the status readouts are square
  badges instead of rounded pills.
- The light theme follows as a faithful paper-terminal inversion: warm paper ground
  with the same glass mechanics, black-alpha surfaces and a white specular rim.
- The in-game overlay ground is plain black-alpha; the overlay never gains blur or
  any effect that would cost frames over a running game.

## 2026-08-30 - News page, launcher presence on Discord, honest display status

- Critical Ops news gets its own NEWS page in the sidebar (Alt+N) with a refresh button.
  The UPDATES page keeps the launcher self-update and this changelog; the game-update
  banner on HOME is unchanged.
- Discord activity can now show while you sit in the launcher: "Optima Launcher /
  Browsing the launcher" with time elapsed, only while the window is on screen
  (minimized counts, hidden to the tray does not, autostart never broadcasts). A new
  Settings toggle, on by default, controls the launcher part separately from game
  activity; launching and in-game states still take priority.
- The HOME status line no longer presents the virtual display driver's parked
  999 Hz placeholder mode as if it were real; between sessions it now reads
  "idle on <display>".

## 2026-08-30 - Fix-everything setup, repair actions, the Comp page and Legal (v0.5.0)

- The first-run wizard now fixes things instead of just listing them: one consent runs
  every automatable fix (enabling the Windows hypervisor features through the
  administrator helper, opening the official Google Play Games download page), a restart
  is orchestrated when Windows asks for one and setup resumes by itself afterwards, and
  the one thing software cannot do, the BIOS virtualization toggle, gets an honest
  walkthrough instead of a fake button. The wizard ends with autostart (pre-checked),
  player name and Discord id, and can be re-run any time from DIAGNOSTICS.
- Repair, on the DIAGNOSTICS page: a Google Play Games heartbeat, a clean platform
  restart, re-detection for moved installs with cached paths cleared, quick links to the
  right Windows settings pages, one-click restore of Optima's own settings from the
  automatic backups (kept on every save now), and a redacted support archive with logs,
  diagnostics, the newest crash bundle and settings, scrubbed of user and machine names.
- Comp, a new page for gear checks: an ad-hoc ping test with jitter and loss, a wifi
  link readout, a raw-input mouse meter (polling rate, hardware counts and a DPI
  calculator that Windows pointer settings cannot skew), a key timing widget, the display
  scale, and live CPU/GPU temperatures streamed through the administrator helper via
  LibreHardwareMonitor. Every readout states its honest measurement limits. The stress
  test from the original wishlist stays deliberately unbuilt.
- Legal, a new page: what Optima is (made by Aureum, all rights reserved under the
  Optima holder), exactly how it stays outside the game, the exhaustive list of
  everything it talks to, and the shipped LICENSE and third-party notices rendered
  in-app. It promises behavior, never outcomes.
- Navigation grew to thirteen rows with COMP and LEGAL in place, and every page kept a
  keyboard shortcut.

## 2026-08-30 - Update center: launcher self-update, Critical Ops news, game-update banner (v0.4.0)

- The UPDATE LOG page grew into UPDATES: launcher self-update from the project's GitHub
  releases (check, download and restart, one-click rollback to the kept previous build),
  the official Critical Ops news feed, and the shipped changelog in one place.
- Critical Ops news, straight from criticalopsgame.com/updates: every entry as a card
  with its BETA/LIVE status and headline list, a keyword filter box, and a full-notes
  button that opens the official page in your browser. The feed is cached so the page
  still renders offline, and if the site changes shape the page says the feed is
  unavailable instead of guessing.
- Automatic game-update banner: Optima remembers the newest LIVE version from the
  official page and, when it changes, HOME shows a notice that the game updated and
  that the overlay, tracking and saved profiles may need a re-check. No hand-written
  feed anywhere; the site itself is the source.
- The updater treats the install folder as managed: applying an update mirrors the new
  build over it and keeps the previous build for rollback. Update checks degrade to
  "unavailable" while the repository has no public releases.
- Every outbound endpoint the app can contact is now listed exhaustively in the README
  security section.

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
  through your local Discord client only. Works out of the box through Optima's own
  registered Discord application; Settings can point it at a different application id,
  or clear the id to keep presence off entirely.
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
