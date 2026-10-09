# Changelog

Newest build first. This file ships next to Optima.exe and is rendered by the UPDATE LOG
page in the app, so keep the format: one `## date - title` heading per build, `-` bullets
under it, plain text, no em dashes.

## 2026-10-09 - 0.8.0: everything since 0.7.5

- This is the full list of what changed between 0.7.5 and 0.8.0, by area. The entries marked "Indev" further down are the same changes as they were made, build by build.
- INSTALL:
- Optima installs to Program Files\Optima now. The setup asks for administrator rights once, and that one prompt also covers the virtual display driver.
- Why: Optima's helper runs with administrator rights, and in a folder of your own any program could have replaced it or the driver beside it. In Program Files only an administrator can.
- A copy that an earlier setup put in your own folder is removed by this one, with its shortcuts and its entry in Add/Remove Programs. Settings, profiles and sessions are kept.
- Running the setup over an install you already have replaces it instead of merging into it. The old files are cleared first, so nothing a new build renamed or dropped is left behind.
- The first page of the setup names the version being replaced and says that your settings, profiles and session history are kept.
- Add/Remove Programs shows the new version after an upgrade, and the uninstaller keeps working.
- Fixed: the setup could stop with a runtime error ("An attempt was made to expand the app constant before it was initialized") before anything was installed.
- The setup only clears an install folder that holds Optima.
- The setup no longer closes a running Optima by itself. It waits a few seconds for it to close and says so when it does not.
- Optima is never started with administrator rights by its setup.
- Starting at sign-in is no longer a box in the setup. It is the switch in Settings and in the first-run wizard. An existing entry is pointed at the new folder the first time Optima starts.
- Installing the virtual display driver no longer puts a virtual monitor on the desktop straight away. The device is left off until a session or a test switches it on.
- Installing the driver over one that is already in place no longer reports a failure, and does not touch its state.
- A driver install that needs a restart is no longer reported as failed.
- UPDATES FROM INSIDE OPTIMA:
- At every start Optima asks GitHub once whether a newer release is out. When there is one, HOME says so at the top, with an Update button. "later" hides it until the next start.
- Update downloads the setup, checks it and runs it. Windows asks once for administrator approval, Optima closes, and it opens again when the setup is done.
- The setup is only run when it carries Optima's own signature. One that does not is deleted, and the notice says so.
- If you answer No to Windows, or the download fails, Optima keeps running as it is and the notice says why, with a link to the releases page.
- Update is refused while Critical Ops is running, because closing Optima would end your session.
- The UPDATES page has "check now". Settings, under GENERAL, has the switch that turns the check at start off.
- The UPDATES page opens at once: the newest entries are drawn first and the rest fill in below.
- GitHub is contacted for the update check and nothing else. The Legal page and the README list it.
- DISPLAY:
- The Display page is three steps now. 1, the driver: installed or not, with the button to install it. 2, whether the virtual display is used for Critical Ops, and at which resolution. 3, a test that shows the display for 15 seconds and puts everything back.
- New: "Check my setup". It tries the virtual display the way a launch does, one step at a time: the driver, Optima's helper, the display turning on, the resolution, and putting everything back. It stops at the first step that fails, says why and what to do, and where there is something to press it is on that line.
- New: "Reload driver", which the error guide always told you to press, exists now.
- The virtual display and its resolution are one choice for every profile, made on this page. They are no longer part of a profile. What you had selected is carried over.
- "Competitive 1080p240" and "Competitive 1440p165" are one profile now, "Competitive".
- Your own monitor stays the main screen while the virtual display is on, and the game opens on it as always.
- With the virtual display switched on and no driver installed, the game starts on your own screen and the session says so. It used to refuse to start.
- When the virtual display does not appear at launch, Optima reloads the display driver and tries once more before giving up.
- A new install has the virtual display on, at 1920 x 1080 and 240 Hz, when the driver was installed with it.
- "Restart needed" after a driver install is still shown after Optima was closed and opened again, until Windows has been restarted.
- An install or removal that fails says all of it on the page: what happened, why, and every way out.
- The page says what a virtual display is before it asks for a click. "Optima Virtualization" is called "Virtual display" everywhere, so it is not mistaken for the CPU's virtualization.
- The question about the driver when Optima closes has "do not ask again".
- The list of monitors is folded away at the bottom. The enable, disable, preset and custom buttons are gone: the test and the check do what they did, and put things back themselves.
- The stand-in display for developers is no longer used on a PC without the driver.
- Reloading the display driver while it is not running says so after 3 seconds, instead of raising an administrator prompt and then failing.
- The virtual display steps check five times as often, so a launch or a restore that uses one finishes a few hundred milliseconds sooner.
- MOTION AND LOOK:
- Moving between pages: the marker in the rail glides to the page you open, the page's sections rise in one after another, and the rail slides when you collapse or expand it.
- Buttons and chips fade into their hover and pressed states and go down a pixel when pressed.
- A checkbox fills and its mark is drawn. An expander's arrow turns. Lists, menus and tips open with a short rise.
- Notices in the corner slide in, fade out, and the ones stacked with them close the gap.
- Dialogs open with a short fade and grow into place, and their backdrop moves too. It used to be frozen.
- On DEBUG a line glides under the tab you choose, and the tab's content rises in.
- Meters and progress bars glide to each new value, and the number printed beside them counts along.
- Graphs flow to the next sample instead of jumping.
- On PLAY a finished step has its check drawn, and a marker moves down the launch steps to the one that is running.
- The backdrop is three pools of light in your accent colour, arranged differently for every page. They pour to the next arrangement when you change page, warm up while a session starts and turn red when a launch fails.
- Between those moments the backdrop is still. It used to drift all the time, which every panel on the page paid for.
- The bright line along the top of every panel is gone.
- New: the launch splash, built around the new mark. A silver square appears, four blades fly in and lock around it, a band of light crosses the mark and OPTIMA is set letter by letter. When the app is ready the centre opens onto the main window.
- The splash runs on a thread of its own. It used to stand still or stutter while the app was starting.
- The line under the name on the splash fills with the startup stages Optima is really going through.
- Everything that moves uses one curve and four lengths, the splash and the BOOST dial included. The dial's segments no longer overshoot when they lock in.
- Settings, Appearance: "animations" is a choice of three: as Windows says, always on, off. Your old setting is carried over.
- Nothing is animated while a game is running or while Optima is in the background.
- BOOST (NEW PAGE):
- New: the BOOST page, in the TUNE group (Alt+6). It gathers what Optima does while the game runs to keep it smooth.
- New: the master switch, a round dial at the top of the page. One click arms every feature ticked below; a second click switches them all off and puts back everything they changed. Boost starts switched off.
- New: the priority guard. The game keeps the priority you chose for as long as it runs, however it was started. If another program lowers it, it is put right within ten seconds. The page says what it is holding, since when and how often it had to correct it.
- The game priority choice lives on BOOST: Unchanged, Normal, AboveNormal or High, for every session, watch mode included.
- New: the memory cleaner, which does what ISLC does. While Critical Ops is on screen it empties Windows' standby list when free memory is below one threshold and the standby list is above another. Both thresholds are yours. The page shows live figures and has "purge now".
- New: timer resolution. While the game is on screen Optima holds a 1.0 ms or 0.5 ms system timer and lets go when the game leaves. The page reports what Windows actually applied and says how your Windows version treats the request.
- New, Windows 11 only: system-wide timer resolution requests, so the timer Optima holds reaches the game. It needs an administrator prompt and a restart.
- New: background programs. While the game is on screen, the programs on your list run at below-normal priority in Windows' efficiency mode. Nothing is closed, and each gets back exactly the priority it had.
- New: keep every processor core awake while the game is on screen, so a parked core does not cost a hitch when it wakes.
- New: ask Windows to run the game on the high-performance graphics adapter. It matters on a PC with two adapters.
- Windows, the game, Google Play Games and Optima itself are never touched by background demotion, whatever the list says.
- Everything on BOOST is off by default, and everything it changes is put back when the game leaves, when it is switched off, or at the next start after a crash.
- DEBUG (NEW PAGE):
- New: the DEBUG page, in place of DIAGNOSTICS and LOGS, with tabs: ISSUES, CHECKS, LOG, CRASHES and ERROR GUIDE. Alt+9 opens it, and so does the tray menu.
- New: ISSUES. Optima reads its own log as it is written and runs quick checks in the background, and lists whatever is wrong: once per problem, with what it is, why it happens, how to put it right, and its evidence.
- No error goes by unlisted. A failure Optima has a name for is listed under that name; any other is listed as unclassified with everything it carried.
- The rail shows a count on DEBUG when something needs attention, whatever page is open.
- "copy report" puts an issue on the clipboard as redacted text that stands on its own. "dismiss" takes it off the list until it happens again. "ignore" keeps it off for good, and IGNORED lists what was ignored.
- "scan now" runs every check. A check that fails opens an issue and a check that passes closes it.
- New: repairs. An issue Optima can do something about has buttons on its card, each saying what it changes before it changes it. A repair is proved by the check that raised the issue, not by its own word.
- New: repair by itself, at the top of ISSUES: "everything it can" (the default), "safe repairs only", or "off".
- A repair that interrupts says so before it runs: a notice counts down five seconds with a cancel button. Cancelling is remembered.
- The limits: nothing is repaired while a game is running, a repair that interrupts is not run with the window hidden, an administrator prompt appears at most once per issue per day, no more than six repairs an hour, and a repair that would make a choice for you is never run unasked.
- The first repairs: Google Play Games is started or restarted after a failed launch, the Windows hypervisor features can be enabled from the card, a restore that did not finish can be tried again, and a stale virtual display restore can be discarded.
- New: notices. Whatever Optima did without being asked is said in the corner of the window, or from the tray icon when the window is hidden, and never while a game is running.
- New: REPAIR HISTORY on ISSUES: every repair that was run, when, for which issue and how it went. It is kept across restarts.
- New checks: whether this PC offers the power plan the selected profile asks for, and a virtual display restore left pending by an old session.
- The quick checks run again at the start of every launch, before anything is changed. They never hold a launch up for more than three seconds.
- LOG: click a line to open its detail: the whole exception with its stack, the code Windows returned and Windows' own description of it, and the error guide's entry for the line's code.
- LOG: "copy report" copies a line as a report that stands on its own, with the build, the Windows version and the fifteen lines that led up to it.
- LOG: the export writes dates, full source names and whole exceptions. The filter also searches exceptions, and the footer says how many lines it is showing.
- Reports, exports, crash zips and the support archive mask the Windows user name, the machine name and user profile paths along with tokens.
- In a flood of log lines, warnings and errors are kept ahead of ordinary lines instead of being dropped with them.
- Optima notes how its last run ended. A crash is written down on the way out and repeated at the top of the next run's log. A run ended from outside, or by a power loss, is said too.
- An error inside the elevated helper reaches the log whole.
- The tools on CHECKS can no longer take Optima down.
- New in the log: one "Startup timeline" line per start, with the moments of that start in milliseconds.
- PLAY AND SESSIONS:
- Fixed: PLAY failed with "Unexpected error" on PCs that do not offer the High performance power plan, which is most recent laptops. The game now starts on the plan that was active, and PLAY says so under NOTICES.
- A failure in a step the game does not need (background cleanup, process tuning, writing the session to the history) is a notice on the session, not the end of it.
- An unexpected failure names the step the session was in and keeps the whole error under developer details.
- The error guide gained GPG_NOT_FOUND, UNEXPECTED, POWER_PLAN_UNAVAILABLE, POWER_PLAN_REFUSED, LAUNCH_STEP_SKIPPED and SESSION_NOT_SAVED.
- Exiting Optima while a session is running ends the session first: capture stops, everything is put back and the session is saved.
- The administrator prompt for frametime capture appears when PLAY is pressed, not on top of the game. A No is the answer for that launch and is not asked again.
- The window no longer freezes while an administrator prompt is open.
- After a session, each restore step is tried twice. Whatever still fails stays pending so the next start, or the button on the issue, can finish it.
- Fixed: with "relaunch after a crash" on, the relaunch ran while the crashed session was still putting the PC back, and PLAY could stay stuck on "Running".
- Fixed: ending the game yourself within five minutes of its start was taken for a crash, and the game was started again.
- Fixed: crash relaunch could relaunch forever.
- Fixed: a session could wait forever for the game to close while a window with the game's name in its title was open. The power plan, the display and the priorities were then never put back.
- Fixed: with watch mode on, closing the game could start a second session a moment later.
- Fixed: a session whose frametime capture was slow to stop was dropped as cancelled. It is saved with what it measured.
- Fixed: Game Bar off and fullscreen optimizations off for the session never applied. They do now, and they are put back at the next start if Optima was closed hard while the game ran.
- Fixed: Optima removed a "High performance" graphics preference you had set for the game in Windows yourself.
- Fixed: every Ultimate Performance apply created one more Windows power plan. Where Windows hides the plan, no hidden copy is left behind.
- Fixed: after a crash, recovery left the virtual display device enabled.
- Fixed: the ping shown during a session stopped updating on a steady connection.
- Fixed: "refresh stats" and "refresh matches" pressed while a game was running credited that game's matches to the session before it.
- Fixed: the weekly playtime wrapped at 24 hours and the session timer at 60 minutes.
- Fixed: a custom launch command with an unquoted path that has spaces in it did not start.
- Fixed: Restart Google Play Games could stop unrelated programs named client or Service.
- Fixed: the setup guide's Next button always said Finish and closed the guide, and a failed scan reported ready to play.
- The Sessions page no longer holds the window while it reads or writes the history. CSV and PDF export no longer load every session's fps graph.
- Coming back to Performance or Sessions keeps what you left: open rows, the selected session and the scroll position.
- The session graph draws a long session as its outline, with every spike kept.
- HOME AND ACCOUNTS:
- HOME is editable. "edit widgets" lists every card from every tab; drag a card onto HOME to add it, drag to reorder, drop it back to remove it. HOME holds at most 10, and the layout is remembered.
- Every account in the switcher at the top of the window has an X that takes it off the list.
- Fixed: tracking the same player twice showed them twice, and two players added quickly could lose the first.
- Fixed: the first-run finish could crash on HOME.
- The recent sessions widget on HOME builds only the rows that are in view.
- DISCORD AND OPTIMABOT:
- The Discord activity card is rebuilt around your rank: the app mark as the large image and your rank emblem beside it.
- Discord's status line says what you are doing, not only the app's name.
- The card is clickable: the game line opens the Critical Ops site and the artwork opens the Optima repository.
- New: "choose what is shown" in Settings under DISCORD. Start from Minimal, Standard or Full, or tick each fact yourself: name, rank tier, rank emblem, ranked record, rating and the session timer.
- The launcher card shows your name and rank while you browse, without waiting for a game session.
- The card recovers on its own: a rank or artwork that could not be read is retried in the background, and the rank refreshes when you switch accounts.
- Fixed: after Discord was restarted, the presence card stayed away until its text changed.
- Started in the tray, Optima does not connect to Discord until there is a card to show.
- The card's button reads Beta.
- New: OptimaBot, the Discord bot that goes with Optima. /searchplayer posts a stats card for any Critical Ops player, and /link gives you the code that links your Discord account to your game account.
- Linking is a code, not a login: run /link in Discord, paste the code into Settings under DISCORD. Optima never holds a Discord credential and the bot never sees a password.
- Linking goes through Optima's community bot with no address to type. Running your own bot still works through discordBotUrl in config.json.
- New: paste a channel webhook in Settings and the bot posts your new matches and rank changes there as one image. "send test image" checks the channel first.
- Fixed: closing the link window while OptimaBot was being asked left the account linked on Discord and not in Optima.
- CRITICAL OPS API:
- Every Critical Ops request Optima makes goes to the public API and nowhere else on that host. The client refuses any other address before a request is sent.
- Removed: the EXPLORE page. Its leaderboards read an address that is not part of the public API, and asking it can get an account banned.
- Fixed: batch player lookups cost about twice as many requests as needed.
- SPEED:
- A faster start, measured on a warm start of the development PC: the window is on screen after 0.60 s instead of 0.80 s, answers to input after 0.79 s instead of 1.34 s, and is filled in after 0.81 s instead of 1.94 s. A start uses about a third less processor time.
- Fixed: the window froze right after it appeared, for half a second on most starts and for about five seconds when Optima had not run for a few minutes.
- Fixed: every start saved the settings although nothing had changed, and everything that listens for a change then did its work twice.
- The window paints before the tray icon, the hotkeys and the background services are set up.
- Less work in the half minute after a start, which is when the game is being launched.
- Fixed: the COMP mouse meter, once started, kept receiving every mouse movement for as long as Optima ran, during a game too.
- With the window hidden or minimized, Optima no longer re-reads the display state every 10 seconds.
- The BOOST dial no longer redraws every frame while the window is unfocused, minimized or behind a game.
- Frametime capture reserves about 56 MB less memory while a game runs.
- Background demotion lists the running programs once per pass instead of once per listed program.
- SETTINGS AND SAVING:
- Fixed: a save that failed (a file held by a scanner, a full disk, a locked database) closed Optima with "an unexpected error". It now says what could not be saved and carries on.
- Fixed: settings and profiles could come back as defaults after Optima was killed in the middle of a save, or as an empty file after a power cut. The previous save is recovered instead.
- Fixed: a damaged settings file was replaced by defaults, and a later save overwrote its only backup.
- Fixed: two settings saves at the same moment could drop one of them.
- Saving settings waits out a file that another program holds open for a moment.
- Fixed: messages on the Settings bar were replaced by "Unsaved changes" the moment they appeared.
- Fixed: several Settings rows (tracked players, account id, accent, crash relaunch) saved wrongly or not at all.
- Fixed: the in-game name and account id boxes in Settings were a few pixels wide while empty.
- Fixed: clicking one of the session switches on PERFORMANCE while the page was loading saved the others as off.
- Fixed: an exported built-in profile could not be imported again. It comes in under its name with "(imported)" added.
- Fixed: importing a file that is not a profile renamed that file.
- Fixed: exporting to a file that is open elsewhere closed the app.
- WINDOW AND TRAY:
- Fixed: started in the tray, or closed to it, the launcher came up with focus after every game that watch mode had attached to.
- Fixed: a second start while Optima runs as administrator, or with a data folder that cannot be written, ended with no window and no message.
- Fixed: the first-run wizard and the setup guide were taller than a 720p or a scaled laptop screen. The main window never opens larger than the screen either.
- Fixed: every exit asked Windows about the display driver and waited however long it took. The question gives up after 5 seconds.
- Fixed: an error during startup showed its message underneath the splash.
- Fixed: issues you chose to ignore came back at every start.
- Fixed: Performance showed the GPU temperature on the CPU tile.
- The Debug item on the rail carries its count in the corner when the rail is collapsed.
- THE ELEVATED HELPER:
- The helper starts Windows tools from System32 only and gives each of them a time limit.
- It installs and removes no driver but the one Optima ships.
- It no longer ends on a request it cannot read.
- Fixed: the administrator prompt never led anywhere on a Windows account that is not an administrator itself.
- A declined administrator prompt is remembered for the run: nothing Optima does by itself raises it again.
- REMOVED:
- "HDR off for the session" on PERFORMANCE. It never changed anything.
- "live fps while the game runs" from the Discord card chooser. The card never showed one.
- The bot address row in Settings and in the link window.
- The EXPLORE page.
- The DIAGNOSTICS and LOGS pages, which are one page now: DEBUG.
- The enable, disable, preset and custom buttons on the Display page.
- NOT IN THIS RELEASE:
- Protected play (Optima Shield) is not part of 0.8.0. Entries below that mention it describe work that is not shipped yet.

## 2026-10-09 - Indev: Optima moves

- Moving between pages: the marker in the rail glides to the page you open, the page's sections rise in one after another, and the rail slides when you collapse or expand it.
- Controls: buttons and chips fade into their hover and pressed states and go down a pixel when pressed, a checkbox fills and its mark is drawn, lists, menus and tips open with a short rise, an expander's arrow turns.
- Notices in the corner slide in, fade out, and the ones stacked with them close the gap. Dialogs open with a short fade and grow into place. On DEBUG a line glides under the tab you choose.
- Live numbers: meters and progress bars glide to each new value and the number printed beside them counts along, graphs flow to the next sample, a finished step on PLAY has its check drawn, and a marker moves down the launch steps to the one that is running.
- The backdrop is three pools of light in your accent colour, arranged differently for every page. They pour to the next arrangement when you change page, warm up while a session starts and turn red when a launch fails. Between those moments the backdrop is still, where it used to drift all the time.
- The bright line along the top of every panel is gone.
- The splash and the BOOST dial move on the same curve as everything else. The dial's segments no longer overshoot when they lock in.
- Settings, Appearance: "animations" is a choice of three now: as Windows says, always on, off. Your old setting is carried over. Nothing is animated while a game is running or while Optima is in the background, as before.
- With one of Optima's own dialogs open, the dialog and its backdrop move too. They used to be frozen.

## 2026-10-09 - Indev: the "protected play" row is gone from Settings

- Removed: the "protected play" row under DISCORD in Settings, in every build.

## 2026-10-09 - Indev: an X in the account switcher

- New: every account in the switcher at the top of the window has an X that takes it off the list. The account that is active stays active; only its saved entry goes, and Settings saves it again whenever you want it back.

## 2026-10-09 - Indev: the Display page, in three steps, with a check that says what is wrong

- Changed: the Display page is three steps now. 1, the driver: installed or not, with the button to install it. 2, whether Critical Ops runs on the virtual display, and at which resolution. 3, a test that shows the display for 15 seconds and puts everything back.
- New: "Check my setup". It tries the virtual display the way a launch does, one step at a time: the driver, Optima's helper, the display turning on, the resolution, and putting everything back. It stops at the first step that fails, says why and what to do, and where there is something to press it is on that line. "Reload driver", which the error guide always told you to press, exists now.
- Changed: the virtual display and its resolution are one choice for every profile, made on this page. They are no longer part of a profile. What you had selected is carried over once: the profile's resolution becomes the choice, and "Competitive 1080p240" and "Competitive 1440p165" become the one profile "Competitive".
- Your own monitor stays the main screen while the virtual display is on, and the game opens on it as always. (An earlier build of this page made the virtual display the main screen for the session, and the game opened where it could not be seen. That is gone, switch and all.)
- Changed: with the virtual display switched on and no driver installed, the game starts on your own screen and the session says so. It used to refuse to start.
- A new install has the virtual display on, at 1920 x 1080 and 240 Hz, when the driver was installed with it. If you were playing on your own screen, you still are.
- "Restart needed" after a driver install is still shown after Optima was closed and opened again, until Windows has been restarted.
- An install or removal that fails says all of it on the page: what happened, why, and every way out.
- The page says what a virtual display is before it asks for a click. "Optima Virtualization" is called "Virtual display" everywhere, so it is not mistaken for the CPU's virtualization.
- The stand-in display for developers is no longer used on a PC without the driver. It showed a display that was "active" while nothing existed.
- The question about the driver when Optima closes has "do not ask again".
- The list of monitors is folded away at the bottom. The enable, disable, preset and custom buttons are gone: the test and the check do what they did, and put things back themselves.

## 2026-10-09 - Indev: Optima tells you when a newer version is out, and installs it

- New: at every start Optima asks GitHub once whether a newer release is out. When there is one, HOME says so at the top, with an Update button. "later" hides it until the next start.
- Update downloads the setup, checks it, and runs it. Windows asks once for administrator approval, Optima closes, and it opens again when the setup is done. Your settings stay as they are.
- The setup is only run when it carries Optima's own signature. One that does not is deleted, and the notice says so. If you answer No to Windows, or the download fails, Optima keeps running as it is and the notice says why, with a link to the releases page.
- Update is refused while Critical Ops is running: closing Optima would end your session.
- The UPDATES page has "check now". Settings, under GENERAL, has the switch that turns the check at start off.
- Changed: Optima contacts GitHub again, for this and nothing else. The list on the Legal page and in the README says so.
- For whoever releases it: installer.ps1 -Release builds the setup without Optima Shield, signs it, and stops when the signing key is not on the machine. The .sig file goes on the release beside the setup.

## 2026-10-09 - Indev: Optima installs to Program Files

- Changed: the setup now asks for administrator rights once and installs to Program Files\Optima. Optima's helper runs with administrator rights, and in the old place, a folder of your own, any program could have replaced it or the driver beside it. In Program Files only an administrator can.
- The same prompt covers the virtual display driver. There is no second prompt during setup.
- A copy that an earlier setup put in your own folder is removed by this one, with its shortcuts and its entry in Add/Remove Programs. Settings, profiles and sessions are not touched.
- Starting at sign-in is no longer a box in the setup. It is the switch in Settings and in the first-run wizard, as before. An existing entry is pointed at the new folder the first time Optima starts.
- Optima is never started with administrator rights by its setup.
- The setup no longer closes a running Optima by itself. It waits a few seconds for it to close and says so when it does not.

## 2026-10-09 - Indev: a build without Optima Shield says nothing about it

- A build made without Optima Shield no longer has a "protected play" row in Settings, and never shows the "What protected play does" screen at PLAY, also on a PC that was linked under an earlier build. Linking a Discord account works as before.
- For whoever builds it: publish.ps1 and installer.ps1 take -NoShield, which leaves the module out of the payload even when it is in vendor\shield.

## 2026-10-09 - Indev: the Discord card no longer offers an fps line

- Removed: "live fps while the game runs" from the Discord presence chooser. The card never showed one. The Minimal preset is now the game and the session timer.
- The card is no longer rebuilt every 15 seconds during a game for a number that was not there.

## 2026-10-09 - Indev: "HDR off for the session" is gone

- Removed: the "switch HDR off while the game runs" box on PERFORMANCE. It never changed anything. A stored choice for it is dropped the next time settings are saved.

## 2026-10-09 - Indev: a pass over everything before 0.8.0

- Fixed: with "relaunch after a crash" on, the relaunch ran while the crashed session was still putting the PC back. It was refused as "a session is already running", or it left PLAY stuck on "Running" until Optima was restarted. It now waits for the old session to finish.
- Fixed: ending the game yourself (Terminate, Ctrl+Alt+K, Cancel) within five minutes of its start was taken for a crash, and the game was started again.
- Fixed: a session could wait forever for the game to close while Optima's own setup guide, or any window with the game's name in its title, was open. The power plan, the display and the priorities were then never put back.
- Fixed: Optima removed a "High performance" graphics preference you had set for the game in Windows yourself, at every start, with its own switch off. It now only removes what it set, and keeps the other choices stored in the same Windows value.
- Fixed: Game Bar off and fullscreen optimizations off for the session stayed off for good when Optima was closed hard or crashed while the game ran. The next start puts them back.
- Fixed: "HDR off for the session" was reported as applied. It changes nothing in this build, and no longer says it did.
- Fixed: a save that failed (a file held by a scanner, a full disk, a locked database) closed Optima with "an unexpected error". It now says what could not be saved and carries on.
- Fixed: settings and profiles could come back as defaults after Optima was killed in the middle of a save, or as an empty file after a power cut. The previous save is recovered instead.
- Fixed: started in the tray, or closed to it, the launcher came up with focus after every game that watch mode had attached to.
- Fixed: after Discord was restarted, the presence card stayed away until its text changed.
- Fixed: a second start while Optima runs as administrator, or with a data folder that cannot be written, ended with no window and no message.
- Fixed: the first-run wizard and the setup guide were taller than a 720p or a scaled laptop screen, with their buttons under the taskbar. The main window never opens larger than the screen either.
- Fixed: the in-game name and account id boxes in Settings were a few pixels wide while empty.
- Fixed: messages on the Settings bar (a test result, an error, "account saved") were replaced by "Unsaved changes" the moment they appeared.
- Fixed: clicking one of the three session switches on PERFORMANCE while the page was loading saved the other two as off.
- Fixed: the ping shown during a session stopped updating on a steady connection, and the page then said it was not measuring.
- Fixed: "refresh stats" and "refresh matches" pressed while a game was running credited that game's matches to the session before it, and listed them a second time afterwards. Both now wait for the game to close.
- Fixed: an exported built-in profile could not be imported again. It comes in under its name with "(imported)" added.
- Fixed: a custom launch command with an unquoted path that has spaces in it, such as C:\Program Files\..., did not start.
- Fixed: a repair that had nothing to do marked its issue as repaired. A repair that was cancelled left its card on "repairing".
- Fixed: closing the link window while OptimaBot was being asked left the account linked on Discord and not in Optima.
- Fixed: tracking the same player twice showed them twice, and two players added quickly could lose the first.
- Fixed: the administrator prompt never led anywhere on a Windows account that is not an administrator itself.
- The elevated helper starts Windows tools from System32 only, gives each of them a time limit, installs and removes no driver but the one Optima ships, and no longer ends on a request it cannot read. A driver install that needs a restart is no longer reported as failed.
- The setup only clears an install folder that holds Optima.
- Building a dev payload no longer closes the installed Optima.

## 2026-10-08 - Indev: protected play keeps trying, and says when it is not running

- Fixed: when OptimaBot could not be reached as a session began, Optima Shield gave up after three tries. The session stayed unprotected under a message that said it would resume. Shield now keeps asking for as long as Optima runs, and starts reporting as soon as the bot answers.
- Fixed: one lost report could be recorded as a gap in a session. Reports now keep their pace whatever happened to the one before.
- New: if Optima Shield is not running during a session and starting it once more did not help, Optima says so, once, in the window or from the tray.
- A reason left behind by an earlier session, such as "OptimaBot did not answer", is no longer shown as this session's.
- The "What protected play does" screen at PLAY now comes before the administrator prompt, and only for a launch you started yourself.
- Fixed: a link that succeeded could lose its enrolment on this PC when the check right after it did not get through.
- A PC whose clock is wrong is told so when a session cannot start, instead of being told to update or link again.
- Pressing PLAY no longer waits on Shield being started.

## 2026-10-08 - Indev: what protected play does, on one screen, before it runs

- New: a screen called "What protected play does". It says what Optima Shield is, what it looks at, what it sends, what it never does and who sees what. You read it before you link a PC, and the "I have read this" button works once you have scrolled to the end.
- Optima Shield does not start on a PC whose player has not read the current text. When a later version looks at more, the screen comes back at the next PLAY with what changed, and Shield waits until it was read. Nothing shows it in the background.
- Linking a PC turns protected play on, so declining the screen is not linking.
- The Settings page and the README say the same things in fewer words, and the README no longer says that Optima sends the reports itself.
- The tray icon's tip reads "Optima · Shield running" while Shield is running.
- If security software removes Optima Shield, Optima says so: on the Settings page, and as an issue with what to do (restore the file, or reinstall).
- Installing over a running session, and uninstalling, ask Shield to send its last report and exit first.

## 2026-10-08 - Indev: protected play moves into Optima Shield

- Changed: protected play is now done by Optima Shield, a separate program installed beside Optima.exe. Optima starts it when you press PLAY and when watch mode finds the game running, and it stops when Optima closes. Optima itself no longer signs or sends the reports.
- Optima Shield is closed source and is not in Optima's public repository. A build of Optima made from source has no Shield, runs without protected play, and the Settings page says so.
- With administrator rights Shield covers more. PLAY asks for them in the same single prompt Optima already uses for frametime capture and the memory cleaner. Say no, or use a Windows account that is not an administrator, and Shield still runs: the session is recorded as reduced coverage. Nothing asks twice, and nothing asks in the background.
- Only a linked PC runs Shield. An account linked with an earlier build has to be linked once more from this PC: run /link in Discord and press "link account" in Settings.
- The Settings page, under DISCORD, shows what protected play is doing right now: protected with full or reduced coverage, not protected and why, or not running.
- What Shield sends in this build, about every 10 seconds while it runs: your Critical Ops account id, its own version, the Windows build number, whether it has administrator rights, and that it is running. No check looks at the PC yet.
- Link codes of up to 8 characters are accepted.
- Not changed: a session that is not protected still runs. Optima does not hold the game back or close it.

## 2026-10-08 - Indev: protected play is always on

- Changed: protected play is no longer a setting. The checkbox on the Settings page is gone and every session Optima runs is reported to OptimaBot. Being checked is part of using Optima, not something each player decides.
- The Settings page, under DISCORD, still says what is sent: your Critical Ops account id, the Optima version and a signed report every 10 seconds while a session runs. It also says whether this PC is enrolled.
- An account that is not linked, or was linked before protected play, cannot be reported yet. Each of its sessions says so when it starts, with what to do: run /link in Discord and press "link account" in Settings.
- Not changed: a session that cannot be reported still runs. Optima does not hold the game back or close it.

## 2026-10-08 - Indev: protected play, part 1: a session a tournament can check

- New: protected play, on the Settings page under DISCORD, off unless you turn it on. While a session started by Optima runs, Optima sends OptimaBot a signed report every 10 seconds. A tournament organizer reads the record with /verify in Discord, and you read your own with /protected.
- A record says who played, when and for how long, whether the reports ever stopped, and whether Optima was closed while the game was still running. Reports that stop for more than 30 seconds count as a gap.
- What it does not say yet: this build has no sensors that look for cheats. A record from it attests that Optima was running and reporting for the whole session, and the record itself says "presence only". The sensors come in the next builds and report through the same session.
- The reports are signed with a key that is made on this PC when you link your account and never leaves it. An account linked before this build has to be linked once more, from the PC that plays: run /link in Discord and press "link account" again.
- A session that could not be protected says so at once, in the window or from the tray, with the reason: the account is not linked, this PC is not enrolled, or OptimaBot did not answer. The game is never held back or closed over it.
- What is sent while it is on: your Critical Ops account id, the Optima version and the reports. Nothing is sent while it is off.
- Needs an OptimaBot that knows protected play. With an older bot a session reports itself as not protected.

## 2026-10-06 - Indev: lighter beside the game, and pages that open at once

- Fixed: the COMP mouse meter, once started, kept receiving every mouse movement for as long as Optima ran, during a game too. It now only listens while Optima is the window in front, and stops when you leave the COMP page.
- With the window hidden or minimized, Optima no longer re-reads the display state every 10 seconds; it reads it once when the window comes back.
- With the BOOST page open, the dial no longer redraws every frame while the window is unfocused, minimized or behind a game.
- Two timers that ticked once or twice a second for the whole run, to mark readings as stale, now only run while there are readings.
- Background demotion lists the running programs once per pass instead of once per listed program.
- Discord: started in the tray, Optima does not connect to Discord until there is a card to show. In a game the card is recomposed every 15 seconds as intended, not every second, and a failed artwork lookup is retried later instead of on every update.
- The Updates page opens at once: the newest entries are drawn first and the rest fill in below. It used to freeze for half a second or more.
- The Sessions page no longer holds the window while it reads or writes the history, and CSV and PDF export no longer load every session's fps graph to write numbers.
- Coming back to Performance or Sessions keeps what you left: open rows, the selected session and where the list was scrolled to, as long as nothing changed underneath.
- The session graph draws a long session as its outline, with every spike kept, instead of one point per second.
- The recent sessions widget on HOME builds only the rows that are in view.

## 2026-10-06 - Indev: an exit that finishes what the session started

- Fixed: exiting Optima while a session was running left the system as the session had set it (power plan, display, game priority), lost the session's record, and made the next start ask about a shutdown that had been a normal one. The exit now ends the session first: capture stops, everything is put back and the session is saved. It waits up to 10 seconds for that.
- Fixed: every exit first asked Windows about the display driver and waited for the answer however long it took; with Windows not answering, Optima could not be closed. The question now gives up after 5 seconds.
- Fixed: the session tweaks (HDR, Game Bar, fullscreen optimizations) were put back while Optima was already exiting, and could lose that race. The exit waits for them.
- Fixed: with watch mode on, closing the game could start a second session a moment later: the profile was applied and restored again, and a row of a few seconds was left in the history.
- Fixed: a session whose frametime capture was slow to stop was dropped as cancelled. It is saved with what it measured.
- Fixed: installing the display driver over one that is already in place reported a failure. Windows answers "already there" with a code Optima read as an error.
- Fixed: reloading the display driver while it is not running waited 3 seconds, raised an administrator prompt, waited 3 more and then failed. It now says so after the first 3.
- Changed: the administrator prompt for frametime capture appears when PLAY is pressed, not on top of the game as it comes up, and capture starts with the first frames. A No is the answer for that launch: it is not asked again a moment later.
- The window no longer freezes while an administrator prompt is open.
- Fixed: issues you chose to ignore came back at every start when they were raised by the first lines of the log, such as how the previous run ended. The ignore list is now read before those lines are.
- Fixed: an error during startup showed its message underneath the splash.
- Saving settings waits out a file that another program holds open for a moment, as a virus scanner does right after a write, instead of failing.
- Frametime capture reserves about 56 MB less memory while a game runs. The virtual display steps check five times as often, so a launch or a restore that uses one finishes a few hundred milliseconds sooner.

## 2026-10-06 - Indev: a faster start

- Faster: measured on a warm start of the development PC, the window is on screen after 0.60 s instead of 0.80 s, answers to input after 0.79 s instead of 1.34 s, and has its status rows, HOME and profiles filled in after 0.81 s instead of 1.94 s. A start uses about a third less processor time.
- Not changed: the splash still plays in full, so it leaves the screen at the same moment as before, about 2.5 s after the start. The app is now ready well before it does.
- Fixed: the window froze right after it appeared, for half a second on most starts and for about five seconds when Optima had not run for a few minutes. The live readings opened a Windows performance counter to work out a CPU clock value that no page showed. The counter is gone, and what is left of that setup no longer runs on the window's thread.
- Fixed: every start saved the settings although nothing had changed (the selected profile was "selected" again), and everything that listens for a settings change then did its work a second time: the display driver was probed twice, the player profile was fetched twice. A save that changes nothing is no longer a save.
- The status rows no longer wait for each other. The driver, virtualization and game checks ran one after the other, and HOME, the profiles and the watchdog waited behind them; they now run side by side.
- The window paints before the tray icon, the hotkeys and the background services are set up, instead of after.
- The splash no longer holds startup back until it is on screen, and the app no longer builds a generic host it never used (configuration files, a console logger, a Ctrl+C handler).
- Less work after the start: the runtime's profile-guided recompiling is off. It took over a second of processor time in the half minute after every start, which is when the game is being launched.
- New in the log: one "Startup timeline" line per start, with the moments of that start in milliseconds, so a slow start can be read off the log.
- For testing a build: with the environment variable OPTIMA_DATA_DIR set to a folder, Optima keeps its settings, sessions and logs there instead of in %LOCALAPPDATA%\Optima.

## 2026-10-06 - Indev: installing the driver no longer switches a display on

- Fixed: installing the virtual display driver put a virtual monitor on the desktop straight away, at setup and from the DISPLAY page alike, before any session asked for one. A newly created driver device comes up enabled and nothing switched it off. It is now left off after the install: a session with a virtual display profile, or enable on the DISPLAY page, switches it on as before.
- An install over a driver that is already present does not touch its state.

## 2026-10-06 - Indev: Optima goes further by itself, and says what it does

- Changed: repair by itself now starts on "everything it can". Optima still tries the safe repairs first; where they did not help, it goes on to the ones that interrupt something or need administrator rights. "safe repairs only" and "off" are one click away on ISSUES, and a choice you already made there is kept.
- New: a repair that interrupts says so before it runs. A notice in the corner of the window names what is about to happen and why, and counts down five seconds with a cancel button. Cancelling is remembered: Optima does not offer that repair again by itself, and its button stays on the card.
- New: notices. Whatever Optima repaired without being asked is said in the corner of the window, on whatever page is open, with a link to the details. With the window hidden in the tray it is said from the tray icon instead, and never while a game is running.
- New: REPAIR HISTORY on ISSUES. Every repair that was run, by Optima, by you or inside a launch: when, what, for which issue, how it went. It is kept across restarts.
- The limits are unchanged and apply to all of it: nothing is repaired while a game is running, a repair that interrupts is not run with the window hidden, an administrator prompt appears at most once per issue per day and not again after it was declined, and a repair that would make a choice for you is never run unasked.
- The Debug item on the rail carries its count in the corner when the rail is collapsed.

## 2026-10-06 - Indev: a launch that repairs its own display step

- New: when the virtual display does not appear at launch, Optima reloads the display driver and tries once more before giving up. It is the fix the error guide has always given for this fault, the driver's output parked with its device enabled, now done for you. If the second try fails too, the launch fails with the same error as before.
- New: the quick checks run again at the start of every launch, before anything is changed, so a problem the session is about to run into is on ISSUES before the session finds it. They never hold a launch up for more than three seconds, and never stop one.
- A declined administrator prompt is remembered for the run: nothing Optima does by itself raises the prompt again until you ask for it.
- A repair a launch ran is written into the repair history like any other.

## 2026-10-06 - Indev: Optima repairs what it safely can

- New on ISSUES: repairs. An issue Optima can do something about has buttons on its card, each saying what it changes before it changes it. What was done is written on the card, and a repaired issue stays on the list, marked REPAIRED, until it is dismissed.
- New: repair by itself, at the top of ISSUES. It starts on "safe repairs only": repairs that are reversible, need no administrator rights and interrupt nothing run on their own, and anything more waits for a click. "off" repairs nothing unasked. "everything it can" goes on from the safe repairs to the ones that interrupt or need administrator rights, where the safe ones did not help.
- What bounds it: nothing is repaired while a game is running. A repair that interrupts is not run with the window hidden. An administrator prompt appears at most once per issue per day and never over a hidden window. No more than six repairs an hour. A repair is tried once (a safe one twice, a minute apart), and what was tried is remembered across restarts.
- A repair is proved by the check that raised the issue, not by its own word. If the check still fails, the issue stays open and says so.
- The first repairs: after a failed launch Google Play Games is started if it is not running, and can be restarted from the card. Restarting closes a running game, so on a start timeout, where the platform is usually waiting on a sign-in, it is never run by itself. The Windows hypervisor features can be enabled from the card. A restore that did not finish can be tried again. A stale virtual display restore can be discarded, which keeps the settings file as it is; Optima never makes that choice for you.
- Fixed: a damaged settings file used to be replaced by defaults, and the next save but one then overwrote its only backup. A damaged file is now recovered from the backup of the previous save as it is read: one save is lost instead of the file.
- Fixed: after a session, "Settings restored" was logged and the restore snapshot deleted even when a step had failed. Each step is now tried twice, and whatever still fails stays pending, so the restore prompt at the next start, or the button on the issue, can finish it. Only what failed is kept; what was already put back is not put back a second time.

## 2026-10-06 - Indev: Optima notices what goes wrong

- New on DEBUG: ISSUES, the tab the page now opens on. Optima reads its own log as it is written and runs quick checks in the background shortly after it starts, and lists whatever is wrong: once per problem, however many log lines it wrote.
- Every issue says what it is, why it happens and how to put it right, taken from the error guide, and carries its evidence: the first occurrence and the latest five with their full errors, and the log lines that led up to it.
- No error goes by unlisted. A failure Optima has a name for is listed under that name; any other error is listed as unclassified, with everything it carried. A storm of different errors is capped at fifty, with the rest counted under one entry.
- The rail shows a count on DEBUG when something needs attention, whatever page is open. Notes, such as a power plan the PC does not offer, are listed without being counted.
- copy report puts an issue on the clipboard as redacted text that stands on its own. dismiss takes it off the list until it happens again. ignore keeps it off for good, and IGNORED lists what was ignored so it can be brought back.
- scan now runs every check. A check that fails opens an issue and a check that passes closes it, so fixing a problem and scanning clears it.
- New checks: whether this PC offers the power plan the selected profile asks for, said before the launch instead of after it; and a virtual display settings restore left pending by an old session, which would otherwise overwrite the settings file, silently, at the next one.
- Recognised by name: a fatal error in Optima, a previous run that crashed or was ended from outside, a background task that failed unnoticed, a system setting that could not be put back after a session, a damaged settings file, a missing helper, virtualization or the hypervisor switched off, a nearly full system drive, and every launch error.
- Optima only detects and lists here. It changes nothing on the PC.

## 2026-10-06 - Indev: Diagnostics and Logs are one page, Debug

- New: the DEBUG page, in place of DIAGNOSTICS and LOGS. The two answered halves of one question, what went wrong and why, from two rail items. DEBUG has them as tabs: CHECKS, LOG, CRASHES and ERROR GUIDE.
- CHECKS is the environment checks, with the repair buttons under them as TOOLS. LOG is the live log with its detail. CRASHES is the crash bundle list. ERROR GUIDE is the guide that sat folded above the log, now with room to read.
- Alt+9 opens DEBUG, and so does the tray menu. The floating console on Alt+F9 is unchanged.
- HOME's diagnostics widget and the setup wizard show the same checks as before.
- Every message that sent you to "the Logs page" or "the Diagnostics page" now names the place on DEBUG that holds what it means.
- The tools on CHECKS can no longer take Optima down: a tool that fails says so on the page and writes its error to the log.

## 2026-10-06 - Indev: every log line opens to its full error

- New on LOGS: click a line to open its detail. It shows what the one-line row leaves out: the whole exception with its stack, the code Windows returned and Windows' own description of it, the message's arguments, and the error guide's entry for the line's code, fix included. A line with more behind it carries a + at its right edge.
- New: copy report, in the detail. It copies the line as a report that stands on its own: the build and Windows version, the full error, the guide's entry, and the fifteen lines that led up to it. It is what to send when reporting a problem.
- The export now writes dates, full source names and whole exceptions. Before, it wrote the time of day and the one-line form only.
- Reports, the export and the error text in the detail are redacted more thoroughly: the Windows user name, the machine name and user profile paths are masked along with tokens. A stack trace is full of profile paths, so the token mask alone was no longer enough. Crash zips and the support archive use the same redactor.
- The filter also searches exceptions, stack traces included, and the footer says how many lines the filter is showing.
- Following new lines pauses while a detail is open, so the list does not walk away from the line being read.
- In a flood of log lines, warnings and errors are now kept ahead of ordinary lines instead of being dropped with them.
- New: Optima notes how its last run ended. If it crashed, the fatal error is written down on the way out and repeated at the top of the next run's log, where it can be opened and copied. If it was ended from outside or the PC lost power, the next run says so.
- An error inside the elevated helper now reaches the log whole, instead of as one line of it.
- The floating console (Alt+F9) shows the live log alone, without the error guide above it.

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
