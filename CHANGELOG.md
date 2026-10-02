# Changelog

Newest build first. This file ships next to Optima.exe and is rendered by the UPDATE LOG
page in the app, so keep the format: one `## date - title` heading per build, `-` bullets
under it, plain text, no em dashes.

## 2026-10-02 - 0.7.5: the installer installs the virtual display driver

- The setup now installs the Optima virtual display driver as part of installing Optima: one
  administrator prompt once the files are in place, with the step listed on the task page so it
  can be unchecked. Before this the setup only copied the driver files next to Optima.exe and
  left the install to be found on the DISPLAY page, so a fresh Optima could come up with no
  virtual display at all. The app keeps its own Install driver button for anyone who skips the
  step, declines the prompt, or removes the driver later.
- Installing twice no longer doubles anything: a run over an existing install refreshes the
  staged driver package and leaves the display device alone, because a second device node would
  mean a second virtual display.
- The driver's settings file is written before its device node exists, so the driver has modes
  the first time it loads, and the app now repairs a missing settings file instead of leaving
  the driver advertising nothing.
- The setup refuses to build when the publish folder has no driver package or no elevated
  helper, so a driver-less installer cannot be published again.
- The version stays 0.7.5: this release only fixes the installer.

## 2026-10-01 - 0.7.5: leaderboards, accounts and a real installer

- Optima ships as an installer now: a per-user setup with the Start Menu shortcut, the
  optional autostart and a proper uninstaller, so the download is one .exe instead of a
  folder to unpack. Your data in %LOCALAPPDATA%\Optima is kept when you uninstall. The
  portable zip is still published, and it stays the artefact the in-app updater pulls.
- EXPLORE is the big new page: Critical Ops ranked, casual, elite and clan leaderboards
  plus a player lookup, straight from Critical Force's public API, with clan rosters that
  fill in as names appear on the boards.
- The icon rail is complete again. The EXPLORE row was drawing an empty slot because the
  icon set had no Explore glyph, so the data-driven lookup came back with nothing; it now
  shows a trophy on the same 24 px stroke grid as its neighbours.
- The rest of the September work is below, newest first: the account switcher in the title
  bar, the ranked badge and ladder, clan tags and friends on HOME, the developer emulator
  path, session tweaks and crash auto-relaunch, the weekly export to CSV or PDF, and a
  richer Discord card.

## 2026-09-27 - Indev: rank badge and a switching account bar

- The HOME player panel shows the ranked tier beside the name: a tier-colored badge with
  the tier, division and elo. The ladder is taken from Critical Force's own ranked page
  (Iron below Bronze, Spec Ops, Elite Ops) and resolves by matchmaking rating, with the
  API's tier index as cross-check; rank 0 means still calibrating. Divisions are derived
  from the rating in 25-point steps.
- The ACCOUNT bar at the top of the window now actually switches: picking an identity
  writes it and the HOME panel retargets immediately, replacing whatever was in Settings.

## 2026-09-27 - Indev: clan tag, friends auto-refresh, identity autosave

- The HOME player panel now shows the clan membership read straight from the public
  profile ([DK] Dawning Knights), no input needed. The API exposes the clan of the
  profile but no roster endpoint, so friends are still added by name, one by one.
- The friends strip refreshes the moment Settings changes instead of on the next visit.
- The player identity (in-game name and account id) is saved as it is typed, debounced,
  so closing Optima never loses it; the Save button remains for everything else.

## 2026-09-27 - Indev: accounts, friends, crash relaunch, session tweaks, weekly export

- Saved identities: store your main and alternates in Settings, Player ("save as account"),
  then switch between them from the switcher centered in the title bar. Switching updates
  every stat lookup at once.
- Friends tracker: add tracked players in Settings and their level, ranked record and win
  rate appear on HOME beside your own stats, ordered by wins.
- Crash auto-relaunch (off by default): when the game process dies on its own within five
  minutes of launch, Optima relaunches the same profile once. A normal quit is never
  touched.
- Session tweaks on PERFORMANCE: HDR off, Game Bar off and fullscreen optimizations off
  can now be session-scoped, applied when the game starts and restored when it ends,
  including after a crash or app exit.
- SESSIONS opens with a THIS WEEK card (sessions, playtime, W-L, average fps) and the
  history exports to CSV, or to a dependency-free PDF digest.

## 2026-09-27 - Indev: a richer Discord card

- The Discord activity stopped being a static caption and now shows what Optima actually
  knows. In game: "Critical Ops · 41W-23L" as the line, and "frosty · 141 fps" under it,
  with the elapsed time ticking. The ranked record comes from the public profile (per
  Discord's own guidance: short, actionable, no repetition) and the fps is live, pushed
  every 15 seconds so the card stays fresh without spamming. Launching shows the profile
  being applied ("applying Competitive"). All eight string shapes are unit-tested.## 2026-09-27 - Indev: launch splash

- A launch splash in the style of a certain gaming browser: a true-black card with the
  Optima mark breathing over an accent sweep line, status text cycling while the app
  builds, and the version tag. It holds for a beat so a fast start never flashes, then
  expands into the main window's exact bounds while dissolving, so the card reads as
  becoming the app. Tray autostart skips it entirely, and with the Windows animation
  setting off it closes as a static frame.

## 2026-09-27 - Indev: Performance absorbs System, with a live load visualizer

- The SYSTEM page is gone and PERFORMANCE now opens with a SYSTEM LOAD section: CPU, GPU
  and RAM each show the live percentage, an ASCII bar and a two-minute trace (one sample
  per second). While a game session runs, the section tag carries what the game itself
  is using, including the live fps readout when capture is on.
- Everything the SYSTEM page held moved here unchanged: hardware inventory, the
  virtualization facts, the passive network quality readout and the monitor list, all
  above the tweaks and profiles sections.
- Navigation renumbered: COMP is now 05 and Alt+5, and the sidebar lost a row.

## 2026-09-27 - Indev: player stats on Home, account id, save bar, error guide, exit and presence fixes

- HOME gained a PLAYER panel directly below LAUNCH: the current player's name, level,
  account id and season with ranked, casual and custom kills, deaths, assists, record
  and K/D, read live from Critical Force's public profile API. A refresh button sits on
  the panel and the answer carries a status line, so a silent empty panel is gone.
- Settings gained an account id field next to the in-game name. The id is checked first
  (exact, survives renames) and the name is the fallback, so either one enables stat
  lookups. The API was verified live during the build: name and id lookups both work,
  unknown players answer with a server error (now reported as "player not found" instead
  of a silent blank), and a mistyped id comes back as a scaffold account, which Optima
  detects and warns about instead of showing empty stats as real ones.
- Settings got an impossible-to-miss save: a pinned bar with "Unsaved changes" and a
  large Save settings button appears the moment anything differs from the last save,
  wherever the user happens to be scrolled. Saving confirms in place with [ OK ] and
  the bar fades out; the old SAVE section at the bottom was removed.
- Fixed: Optima sometimes failed to exit and the next start stacked a second instance.
  There is now a single-instance guard, and a second launch brings the running window
  back instead of starting a twin. Exit no longer hangs forever on a stuck background
  service; past five seconds it logs the stall and forces the exit.
- Fixed: Discord presence vanished when the window went to the tray. The launcher card
  now stays while Optima is hidden to the tray (the elapsed timer restarts on return);
  an autostart instance that was never shown still never broadcasts.
- LOGS gained an ERROR GUIDE: a collapsed panel listing every error code the app can
  raise (driver, display, elevation, tweaks, launch) with what happened, why, and the
  numbered fixes. RELOAD_DRIVER failures are explained there. A test keeps the guide in
  step with the code, so a new error without an entry fails the build.

## 2026-09-03 - 0.7.2: the busy indicator, fixed for real

- 0.7.1 still closed with the same error box as soon as a page with a busy indicator
  opened (Diagnostics, Display, the first-run setup): the replacement animation ran into
  a transform WPF had frozen. The indicator now builds its own transform. Verified on
  those pages and on a forced first run.

## 2026-09-03 - 0.7.1: first-run crash fix

- 0.7.0 crashed on start for anyone whose first-run setup was still pending ("Optima hit
  an unexpected error and will close", twice). The busy indicator's animation was wired
  to a template name that does not exist yet while the indicator is hidden. Fixed.

## 2026-09-03 - Attribution and housekeeping

- Optima is made by Inspect Software. The Legal page, the Discord card and the accent
  preset names no longer carry the old byline.
- README rewritten as a short feature summary.
- Source comments trimmed to the ones that explain a constraint or a non-obvious decision.

## 2026-09-03 - Optima 0.7.0: liquid glass, lighter beside the game

- New liquid glass design on every page: chamfered glass over a drifting ambient field,
  an icon rail (Alt+B collapses it), rounded window corners, glass dialogs and setup.
  Motion follows the Windows animation switch; Settings > Appearance opts out.
- Home launches, Play runs the session (steps, elapsed time, terminate), Performance
  edits profiles with every setting explained in place.
- Far less background load while you play: the status tick no longer runs WMI queries
  and full detection every ten seconds, process scans are cheap snapshots, the hardware
  monitor pauses while the window is hidden, and the frametime trace only listens to the
  game's own present events.
- Optima drops to below-normal priority while the game is on screen.
- Closing Optima completely asks whether to keep or uninstall the virtual display driver.

## 2026-08-30 - Company attribution

- Optima is made by Inspect Software: the assembly Company metadata,
  LICENSE copyright line, README and the LEGAL page now name Inspect Software as
  the company.

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
- Legal, a new page: what Optima is (made by Inspect Software, all rights reserved under the
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

## 2026-08-30 - Liquid glass redesign, themes, accents and the rebrand (v0.2.0)

- Complete visual redesign: a liquid-glass interface with a deep blue-charcoal ground,
  translucent layered surfaces, soft corners, and the new gold accent. The window
  now uses the Windows acrylic backdrop where available, with a solid fallback.
- Dark mode and Light mode: pick a theme on the Settings page under APPEARANCE. The
  switch applies the moment you save, no restart needed, and both palettes keep the
  4.5:1 WCAG AA contrast floor on the dimmest text.
- Accent customization: six presets (Gold, Frost, Mint, Rose, Violet, Slate) plus
  a custom hex field. Hover, pressed, glow and on-accent ink colors are derived
  automatically, and the ink always stays readable on any accent you pick.
- Rebrand: the title bar carries the new wordmark and
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
