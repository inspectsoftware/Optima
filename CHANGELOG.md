# Changelog

Newest build first. This file ships next to Optima.exe and is rendered by the UPDATE LOG
page in the app, so keep the format: one `## date - title` heading per build, `-` bullets
under it, plain text, no em dashes.

## 2026-10-06 - Indev: a launch that says what went wrong

- Fixed: PLAY failed with "Unexpected error" on PCs that do not offer the High performance power plan. A PC with Modern Standby (most recent laptops, some desktops) only lists Balanced and its vendor's own plans, and three of the four built-in profiles ask for High performance. Optima asked Windows for a plan it had just failed to find, Windows refused, and the launch stopped.
- The power plan is now chosen only from the plans Windows lists on the PC. When the profile's plan is not among them the game starts anyway, on the plan that was already active, and the Play page says so under NOTICES together with the plans the PC does offer.
- The same holds for the other steps the game does not need in order to run: background cleanup, process tuning and writing the session to the history. A failure in one of them is a notice on the session, not the end of it.
- An unexpected failure now names the step the session was in, and keeps the whole error under developer details on the error card: the code Windows returned, Windows' own description of it, and where in Optima it happened. That is the part to copy when reporting a problem.
- LOGS shows the Windows code on the error line as well, and no longer puts quotes around message text.
- The error guide gained the codes that had no entry, GPG_NOT_FOUND and UNEXPECTED, and the new ones: POWER_PLAN_UNAVAILABLE, POWER_PLAN_REFUSED, LAUNCH_STEP_SKIPPED and SESSION_NOT_SAVED.
- Ultimate Performance: where Windows hides the plan, Optima no longer leaves a hidden copy of it behind in the power settings.

## 2026-10-05 - Indev: no bot address to look at

- Removed: the bot address row in SETTINGS, and the same line in the link window. Linking goes through Optima's community bot, and nobody linking an account needs to see or type where that is.
- Running your own bot still works: set discordBotUrl in config.json. An address already saved there is kept.

## 2026-10-05 - Indev: a new splash, and one that actually plays

- New: the launch splash, built around the new mark. A silver square appears, the four blades fly in clockwise and lock around it, a band of light crosses the mark and OPTIMA is set letter by letter. When the app is ready the blades leave the way they came and the centre square opens onto the main window underneath.
- Fixed: the splash often stood still or stuttered. It ran on the same thread that starts the app, so it could only move in the gaps, and it switched its own animation off whenever Optima was not yet the foreground window, which at launch is always. It now runs on a thread of its own and follows only Windows' animation setting.
- The intro always plays in full, however fast startup was, and the line under the name fills with the startup stages Optima is really going through.
- With Windows animations off the splash shows the finished mark and name, still, and closes without motion.

## 2026-10-05 - Indev: the Boost dial

- New on BOOST: the master switch, a round dial at the top of the page. One click arms every feature ticked below, together; a second click switches them all off and puts back everything they changed.
- Clicking it plays the run: the gold ring comes apart into six segments, the two sets spin against each other while the centre counts the features arming, and they lock back into a violet-to-green ring that reads ENABLED. Switching off drains it back to gold.
- The run plays only for a click, and not at all while the game is running or with Windows animations off: then the dial simply changes state.
- Boost now starts switched off. The ticks under the dial are kept as they were and show STANDBY until it is switched on; in particular the priority guard, which used to run on its own, now waits for the dial. A session started with PLAY still applies the chosen priority either way.

## 2026-10-05 - Indev: Boost keeps cores awake and picks the fast graphics adapter

- New on BOOST: keep every processor core awake while the game is on screen. Windows parks idle cores and wakes them on demand, and the wake-up is a small hitch. The switch raises the active power plan's floor for the match and writes the plan's own value back when the game leaves; after a crash, the next start does. It changes nothing on High or Ultimate Performance, which already keep all cores awake.
- New on BOOST: ask Windows to run the game on the high-performance graphics adapter. It is the same per-app choice as Windows Settings, Display, Graphics, set for you once Optima has seen where the game is installed. It matters on a PC with two graphics adapters and does nothing on a PC with one.
- Both are off by default. Neither needs administrator rights, and neither touches the game's process.

## 2026-10-05 - Indev: Boost moves background programs out of the way

- New on BOOST: background programs. While Critical Ops is on screen, the programs on your list run at below-normal priority in Windows' efficiency mode, so they get processor time only when the game is not asking for it. Nothing is closed.
- The list starts with the usual heavy ones (browsers, game launchers, cloud sync) and is yours to edit, one process name per line. Voice and music apps are left out on purpose.
- Programs that start mid-game are caught within twenty seconds. When the game leaves, the switch is turned off or Optima closes, each program gets back exactly the priority it had; if Optima itself dies mid-game, the next start puts them back.
- Windows, the game, Google Play Games and Optima itself are never touched, whatever the list says.
- A process found at below-normal or idle priority is now restored to that, not rounded up to normal. Off by default.

## 2026-10-05 - Indev: Boost holds the timer, and knows which Windows it is on

- New on BOOST: timer resolution. While the game is on screen Optima holds a 1.0 ms or 0.5 ms system timer and lets go the moment the game leaves. The page reports what Windows actually applied, not what was asked for.
- Windows versions treat this differently, so the page says what yours does. Before Windows 10 version 2004 a request reaches every program, the game included. Windows 10 from 2004 to 22H2 keeps a request inside the program that made it and has no switch for that, so there the page says plainly that Optima's hold cannot reach the game. Windows 11 keeps it inside the program too, and can ignore the request of a program whose window is hidden.
- On Windows 11, Optima tells Windows to always honour the game's own timer request, and its own while it sits behind the game.
- New tweak, Windows 11 only: system-wide timer resolution requests. With it on, the timer Optima holds reaches the game. It is a machine-wide setting, needs the administrator prompt and a restart, and is listed on PERFORMANCE and switchable from BOOST.
- Off by default. A finer timer costs some battery life.

## 2026-10-05 - Indev: the public API only, and EXPLORE is gone

- Removed: the EXPLORE page. Its Elite, Ranked, Casual and Clans boards read /api/leaderboard on Critical Force's server, which is not part of the public API, and asking that side of the API can get an account banned. The public API has no leaderboards, so the page had nothing left to stand on.
- Every Critical Ops request Optima makes now goes to https://default.prod.copsapi.criticalforce.fi/api/public/ and nowhere else on that host. The client refuses any other address before a request is sent, so a later change cannot reach outside it by accident.
- Nothing else changes: your own stats, tracked players, session stats and the Discord card were already read from the public profile endpoint.
- BOOST is now Alt+6 and DISPLAY Alt+7, following the rail.

## 2026-10-05 - Indev: Boost cleans the standby list

- New on BOOST: the memory cleaner, which does what ISLC does. While Critical Ops is on screen it watches Windows' free memory and standby list, and empties the standby list when free memory is below one threshold and the standby list is above another. Both thresholds are yours; they start at ISLC's 1024 MB and 1024 MB.
- The page shows the live free and standby figures, how often it purged and how much the last purge released, and has a purge now button that works with the cleaner off.
- It only runs while the game is on screen, and it works on Windows' own file cache: no process has its memory touched, the game least of all.
- It needs the elevated helper. Switching it on, PLAY and purge now may each show one administrator prompt per Optima run. A game opened without Optima never causes a prompt: the page says the helper is missing and offers to start it.
- Off by default. A purge cannot be undone and has nothing to undo: Windows refills the cache as files are read again. The Legal page and the README now say so.

## 2026-10-05 - Indev: Boost, and a guard on the game's priority

- New: the BOOST page, in the TUNE group (Alt+O). It gathers what Optima does while the game runs to keep it smooth; this build brings its first part.
- New: the priority guard. The game keeps the priority you chose for as long as it runs, however it was started: opened straight from Google Play Games, restarted after a crash, or lowered by another program, it is put right within ten seconds. Until now that only held inside a session started with PLAY, and only for the one process that session began with.
- The guard covers every game process, picks up a changed choice at once, and says on the page what it is holding, since when and how often it had to correct it. It steps aside while an Optima session keeps the priority itself, and puts the original priority back when it is switched off or Optima closes.
- The game priority choice moved from SETTINGS to BOOST, next to the guard's switch. The stored setting is the same one, so nothing has to be chosen again.

## 2026-10-05 - Indev: updates come as installers

- Removed: the LAUNCHER panel on the Updates page, with its check, download and roll back buttons. New builds are delivered as installers, so the app no longer checks GitHub for releases and no longer contacts api.github.com at all. The page keeps the build line and the update log.
- The Legal page and the README list of everything Optima talks to no longer name GitHub, and now name the two Discord requests they had missed.

## 2026-10-05 - Indev: the audit pass

- Fixed: every Ultimate Performance apply created one more Windows power plan; Optima now makes one copy and reuses it.
- Fixed: crash auto-relaunch could relaunch forever, because the relaunch itself reset the once-per-outage count.
- Fixed: after a crash, recovery left the virtual display device enabled; a driver that would not reload skipped the same step.
- Fixed: Restart Google Play Games could stop unrelated programs named client or Service; it now only touches processes that run from a Play Games folder.
- Fixed: the session tweaks (Game Bar off, fullscreen optimizations off) never applied. They do now, for the length of a session.
- Fixed: batch player lookups kept only the first profile of each answer, so a clan roster cost about twice as many requests as it has members.
- Fixed: two settings saves at the same moment could drop one of them; importing a file that is not a profile renamed that file.
- Fixed: the setup guide's Next button always said Finish and closed the guide; a failed scan reported ready to play.
- Fixed: the first-run finish could crash on HOME; several Settings rows (tracked players, account id, accent, crash relaunch) saved wrongly or not at all.
- Fixed: Explore drew the mode rows over their header and four clan lines with a broken format; Performance showed the GPU temperature on the CPU tile.
- Fixed: exporting to a file that is open elsewhere closed the app; the weekly playtime wrapped at 24 hours and the session timer at 60 minutes.
- Fixed in the build: the Inno Setup download address, the publish check that compared against the wrong folder, and a stale helper in runtime builds.
- Removed: the last hooks of the developer emulator launcher, two unused packages, unused theme resources and other dead code.

## 2026-10-04 - Indev: OptimaBot, and the stats card it draws

- New: linking is prefilled with Optima's community bot, so a fresh install runs /link, pastes the code into Settings and is done, with no address to type. Installs carrying the old built-in local address are moved to the community bot automatically. Running your own bot means replacing that address, and if a local one does not answer, the message now names the community bot's address.
- New: OptimaBot, the Discord bot that goes with Optima. `/searchplayer` posts a stats card for any
  Critical Ops player by name or account id, and `/link` hands you the code that links your Discord
  account to the game account Optima already tracks. It is a separate program with its own
  repository, so nothing here depends on it.
- Linking is a code handshake, not a login: run /link in Discord, the bot answers you privately with a
  code that works once and expires, and you type it into Settings under DISCORD, link account. Optima
  then sends the code with the account it already tracks, and the bot checks both halves before it
  writes a link: the code proves the Discord side, the public stats API proves the game side. The app
  never holds a Discord credential and the bot never sees a password.
- The new Settings rows hold the bot's address (prefilled with Optima's community bot, and a self-hosted bot replaces it) and the link
  itself, which shows as "linked to <player> as <tag>" once the bot confirms it. To undo it, run the
  bot's unlink command where the bot is hosted.
- The card is Optima's own, not a copy of anyone else's: a chamfered dark board, the player's rank
  colour as the whole card's accent (so a Platinum card is ice blue and a Gold card is amber), the
  app's Space Grotesk for the numbers with Inter for the small text, both shipped inside the bot so a
  card drawn on a Linux host looks like one drawn here.
- More than the cards you may have seen elsewhere: the rank emblem and the player's own rank colour,
  level and XP, the clan and when they joined it, the division inside the rank, the rating left to the
  next tier, the peak rank and the global position, this season against lifetime, casual and custom
  together, career totals for every mode, a per-season K/D trend with the best season called out, win
  rate by season, and the standing line that says whether the account carries a ban. A linked card also
  says so, which is a fact no typed-in name can offer.
- Nothing on the card is invented. Every number is derived from the public profile in one place, in
  the bot, where tests pin it against the numbers a comparable stats card published for the same
  profile, including the cases that used to be handled badly: a player still being placed is no longer
  given a division, and a ban object the API returns for accounts in clean standing is no longer read
  as a ban. The parsing and the rank ladder it reads live here, in Core.
- The bot is a separate, optional program in its own repository,
  github.com/inspectsoftware/OptimaBot: one command starts it, its README covers the token, the address
  the app should use and how to deploy it. Building Optima does not need it, and installing Optima does
  not install it.
- Optima Link tracking: paste a Discord channel webhook in Settings under DISCORD and, after linking, the
  bot posts your new matches (wins, losses and the K/D of those matches) and any rank change to that
  webhook as one image: the same card /searchplayer draws, with a band under it carrying what changed.
  The first check after linking only records a baseline, so nothing from before the link is
  announced, and a relink without a webhook turns tracking off.
- A send test image button sits beside the tracker webhook row: it asks the bot to draw a sample report
  and deliver it to the URL as typed, so the channel and the webhook itself can be checked before a
  match happens.
- Link codes now live five minutes instead of fifteen.
- The card carries Optima and its logo in the top middle, with the Discord invite discord.gg/6fzKxA75Nq
  beside it, on every card the bot draws, tracker posts included.
- The rank emblems are drawn with cubic sampling, so the 75 px source art no longer looks blocky at the
  size the card paints it.
- OptimaBot can be installed to a Discord account as well as to a server: its commands declare user
  install and the DM and group contexts, and they register globally, so /link and /searchplayer work in
  DMs and group chats too.

## 2026-10-03 - Indev: HOME is yours, with priority and Discord card choices

- HOME is editable now. The new "edit widgets" button on HOME opens EDIT WIDGETS, which lists every
  card from every tab grouped under the tab it comes from. Drag a card onto HOME to add it in the
  position you want, drag a card HOME already owns to reorder it, and drop a card back on the list
  (or press remove) to take it off. HOME holds at most 10; an eleventh is refused and says why.
- The layout is remembered as you change it and comes back exactly as you left it, including across
  restarts. Until you edit it once, HOME shows the six cards it has always shown, and emptying it is
  a choice it keeps rather than a reset.
- New setting: game priority for the Google Play Games process that runs Critical Ops. Unchanged
  lets the selected launch profile decide, as before; Normal, AboveNormal and High pin one priority
  for every session, including watchdog auto-attach, so a profile that names no priority can still
  get one from here.
- That priority is now held for the whole session rather than applied once at the start. While
  Critical Ops runs, Optima re-checks the Google Play Games process every ten seconds and puts the
  selected priority, the CPU affinity and the power throttling state back if anything changed them,
  so another program cannot quietly demote the game mid-match. The same keeper runs for a watchdog
  auto-attach, and it stops the moment the game exits so nothing is left pinned.
- A new Discord card fields window (Settings, DISCORD, "choose what is shown") decides exactly what
  the Discord activity carries. Start from Minimal, Standard (the name and rank card) or Full (adds
  rating), or tick each fact yourself: name, rank tier, rank emblem, ranked record, rating, live fps
  and the session timer. The status line is its own choice: the app name only, what you are doing, or
  the live status. The current selection is summarised next to the button, and the older card detail
  setting is still read so an existing config keeps the look it had.
- The Discord card's button now reads Beta instead of Private Beta, and the README carries Optima's
  Discord invite (https://discord.gg/bGuJ4tvsF7) in a Community section.
## 2026-10-03 - Indev: the setup replaces an existing install cleanly

- Fixed: the setup could stop with a runtime error ("An attempt was made to expand the app
  constant before it was initialized") as it opened, before anything was installed. The
  upgrade check was asking for the install folder on the wizard's first page, where that
  value does not exist yet. It now reads the previous install from the registry, which is
  available from the very first moment, so the wizard opens normally.

- Running the setup over an install you already have now replaces it instead of merging into
  it. The old files are cleared first, so anything a new build renamed or dropped cannot be
  left behind next to the new one, where a stale file could quietly be loaded instead.
- This fixes installs that had been upgraded a few times: the folder now ends up holding
  exactly the new build and nothing else. An install that had collected leftovers from
  earlier versions was carrying well over a hundred files that no longer belonged to it.
- The upgrade is stated up front: the first page names the version being replaced and says
  your settings, profiles and session history are kept. They live outside the install folder
  and are never touched, and uninstalling still leaves them alone.
- Add/Remove Programs shows the new version after the replacement, and the uninstaller keeps
  working, so an upgraded install can still be removed normally.

## 2026-10-03 - Indev: a richer, more modern Discord card

- The Discord activity card is rebuilt around your rank. The app mark stays as the large image and
  your rank emblem now rides beside it as the small image, so the card shows Gold 2 at a glance
  instead of a generic logo. The emblem is the same official media-kit art the rest of the app uses,
  and it follows you through the tiers as your rank moves.
- Discord's own status line now says what you are doing rather than only naming the app: friends see
  the game and your rank in the member list without opening your profile.
- The card is clickable. The game line opens the Critical Ops site and the artwork opens the Optima
  repository, so the card is a doorway instead of a dead end.
- A new card detail setting replaces the two on/off boxes: Minimal shows only the game and your fps,
  Standard adds your name, ranked record and rank emblem, and Full also shows your ranked rating.
  Presence is visible to every friend, so how much of your own numbers it carries is now one clear
  choice.
- The launcher card is no longer an empty shell: it shows your name and rank while you browse, and it
  no longer waits for a game session before it knows who you are.
- The card recovers on its own. A rank that could not be read, or artwork that failed to resolve, is
  retried in the background instead of sticking until the app restarts, and the rank refreshes when
  you switch accounts rather than showing the previous player's.

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
  bar, the ranked badge and ladder, clan tags and friends on HOME, session tweaks and crash
  auto-relaunch, the weekly export to CSV or PDF, and a richer Discord card.

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
