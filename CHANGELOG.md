# Changelog

All notable changes to T2T Online. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
versions follow [Semantic Versioning](https://semver.org/), from 0.9.2 on with a `-beta` pre-release tag. The
**protocol** is the network format: players can play together only on the same protocol, and a protocol change always
comes with a new minor version.

## [Unreleased]

## [0.12.2-beta] - 2026-11-11 (protocol 9)

The first public release: 0.12.0 Beta with its texts and documents made ready for the public. Nothing a player does
changes, and the protocol is still 9.

### Changed
- The notice for a player on another release of the same protocol says to install the same release (the host's). It
  told players to pass the zip on.
- The warning about another MelonLoader version names MelonLoader's official release as the one to install.
- The zip's READ ME says that the mod is a beta in active development, and names the one key the mod adds (Insert).
- The description of the `NurseRealScreen` setting in `settings.cfg` reads more plainly.
- The third-party notices in the zip say which of the mod's code follows MelonLoader's loading rules, and carry
  MelonLoader's notice.

### Fixed
- With developer tools on, the run-start self-test wrote a skater's first and last name to the log when a skater could
  not be read. It now writes the skater's id, as every other line does.

## [0.12.0-beta] - 2026-11-11 (protocol 9)

Not published: 0.12.2 Beta, which follows it, is the first public release. Everything a player does with the mod is
now on the game's own screens, so nobody needs the mod's panel or its keys. The protocol is still 9: players on this
release and on 0.11.x can play together, and the game shows a notice that everyone should have the same release.

### Added
- **Settings > Online**: an **Online** tab in the game's own Settings menu (the eighth, after Stats), built from the
  game's own rows, each with a tooltip: **Share Achievements**, **Share Progress**, **Play Online With** (**Auto**,
  **Controller** or **Keyboard**), **Save Backups**, **Problem Report**, and in an online game **Leave Online Game** (a
  friend) or **End Online Game** (the host), which both ask first. **Direct Connections** shows only while it is on, or
  after Steam refused to keep connections relayed on this PC; turning it on asks first and applies from the next start
  of the game. The tab opens from the title's Settings, the lobby (RT), the pause menu's Settings and the Online menu,
  and it works with online on, with online off and after online stopped on an error. It is not installed on a game
  version the mod does not support.
- Two rows on the title's **Online** menu: **Online Settings**, which opens Settings on the Online tab, and **Report a
  Problem**.
- **A Backups screen** (Settings > Online > Save Backups > View, from the title screen): your saves and your co-op runs
  as they were before each online game, newest first, with the time and the reason each was taken. A restore asks first
  and backs up what is there now; restoring your saves ends in **Later** or **Quit**, because the game has to restart.
- **A problem report in one press**, from Settings > Online or the Online menu, in a match too. The game's prompt says
  where the zip is and where to post it, and at the title screen offers **Show File**. After an online game that ended
  in an error, or a game closed during one, the game offers a report once at the title screen. On a game version the mod
  does not support, where these screens are not installed, holding Insert for 2 seconds makes the report.
- **My Settings** for a friend during a game: while the host's pause menu is up, a **My Settings** prompt (RT on a
  gamepad, Left Alt on a keyboard, or a click), and on the campaign map the map's own **Menu** entry (Start, Esc or a
  click), open the friend's own Settings, where **Leave Online Game** is.

### Changed
- **No panel and no keys for players.** The F1 panel, F7 and the other developer keys now exist only with the developer
  setting `DevTools` on, which is off by default. Insert still marks a moment for a problem report. Every notice,
  prompt, setting description and file the mod writes for a player names the game's own screens instead of the panel.
- The notice for a game version this release was not made for says how the game differs (newer, older, an update
  waiting in Steam, or files that differ from the supported version) and where releases are published.
- The lobby's look: the selected seat has the outline of the game's Settings tabs; **Invite Friends** sits under the
  mode and save rows and **Start** at the bottom of the column, in the same place in every mode; the band above the
  seats carries one status line, such as "Waiting for players"; one **Game Mode** prompt shows both LB and RB; and the
  start countdown is drawn over clear seats.
- The prompts in the bottom bar of the lobby, the Join Lobby list and the Backups screen can be clicked with the mouse.
- "The host is in the settings" and "no new unlocks" show as the game's own toast instead of a text panel.
- The problem report's cleaner knows this PC's own Steam name as soon as Steam is signed in, so a report made before
  any lobby is cleaned of it too.
- **Logs and problem reports name less.** A skater is written as `skater <asset id>` (the game names a skater's object
  after the skater, and a custom skater's name is typed by its player). A team keeps its name only when it is one of
  the game's own; a custom team, skater, goalie or logo name is written as a length and a short code, in the game's
  toasts and prompts too (`{0} wins by mercy`). What another PC sends is written as it is only when it has the shape of
  one of the game's identifiers, and otherwise as its length, so a modified game cannot write its own text into another
  player's log. The cleaner also knows a Steam name in the form the lobby draws it, with the characters the game's fonts
  lack left out. A change of a run's team is written with each skater's id and number (it wrote first and last
  names), and a name read from a run file or a match's setup is written as a length and a short code.
- A number in a log reads the same on every PC (`0.25 s`, whatever the PC's decimal sign). A friend's link line is no
  longer written every 5 seconds: it is written when the link changed or got slower, and at a slower steady pace
  otherwise, and says how many reports it left out.
- **Direct Connections** can be turned on only while nobody else is in your online game (the game says "Leave the online
  game first."); it can be turned off at any time.
- The developer setting `DevTools` is read when the game starts: a change in `settings.cfg` applies from the next start.

### Fixed
- **In a campaign, a friend had no skater until the host opened the game's Controllers screen** and the friend moved
  onto the team. Now every player in the lobby is on the team when a campaign starts (New Run and Continue), and a
  friend who joins during a run plays from the next match. The Controllers screen still lets a player sit out, and that
  choice is kept for the rest of the session. The game's solo squad still plays one player.
- Changing the game mode twice in quick succession in the lobby (LB then RB, or two clicks) could count as one change.
- With the mouse in use and the pointer on nothing, the lobby put the cursor back on the host's seat.
- A question the game closed by itself, without an answer, could leave the mod waiting for that answer. It now counts
  as the question's safe answer.
- **New Run and Abandon Run right after an online game.** For a moment after hosting stopped, a host's run was put
  away or a co-op slot was restored, the title's buttons still stood for the run file from before: New Run could
  replace a run without asking, and Abandon Run could end the wrong run. A press in that moment now does nothing, as a
  press during the title's own transitions does, and works once the title has reloaded.
- A session that ended while a friend's game was waiting to load the host's match could, up to 90 seconds later, quit
  the next match that friend played, their own included.
- **Shared achievements.** A friend who dropped out of one match no longer misses an achievement earned again in a
  later match of the same session that they played to its end. And a campaign win is judged on a friend's PC by the
  game's own rule for the winner (the Tied Homie relic, the speedrun's goal target), not by the score alone.
- With the General Manager squad, the host's reroll of a node's rewards did not show on a friend's screen.
- In a long session with many custom teams, a friend's game could wait for a custom team, skater or logo that never
  came, and fail to load the match. The host now sends again what a friend's game no longer holds.
- A friend with 200 or more runs in their Run History lost the entry of a run played together.
- A co-op slot could not be restored right after **End Online Game** while Settings was still open.
- **A restore that is put right late loses nothing.** If the game stops in the middle of a restore, the next start
  finishes or undoes it. When that start could not (part of the mod from another version, or a game version this
  release does not support yet) and you played in between, a later start finished the restore over what you had
  played. Now what is on your PC is backed up first ("Before a restore" on the Backups screen). The backups an
  unfinished restore needs are never removed to make room for new ones, and a restore's backup of what it replaces is
  written to disk before anything is replaced.
- After restoring a backup taken while you were in a friend's game, the game could load an earlier run's per-run
  counters over the ones in the restored saves.
- A restore of your saves takes the record of shared progress that belongs to that backup, so progress a host shared
  is neither counted twice nor missed afterwards. The Backups screen lists its rows in the order they were taken, also
  in the hour a clock change repeats.
- A brawl's accept combo (LB+RB, or Q+E) held while a friend's skater was handed back to them accepted an offer that
  was still open.
- **A match conceded or quit during a brawl.** In the matches after it nobody controlled a skater, and a friend's
  LB+RB or Q+E was refused on every later brawl offer while the host's own was taken; on a friend's PC a brawl still on
  when their match ended left the talent and ability icons over the skaters hidden. This is the game's own fight
  state, which only a brawl's own end clears; the mod now clears it when a match is left.
- A friend's game that could not load the map or the title on the way back from a match left the loading screen up.
- If a friend's **My Settings** prompt could not be made, the map's **Menu** entry to their Settings went with it.
- A friend who closes their game during a session now leaves the host's lobby at once; before, the host could see
  them as dropped and keep their seat.
- The host's warning that a friend's connection is not relayed did not come for a friend who sent little.
- A host's run that a stopped game left in the mod's folder is removed at the next start, with online on or off.
- An error thrown by another mod's log handler could reach the game's own code through the mod's log calls.
- The game no longer logs an error each time a prompt in the bottom bar becomes clickable, and the lobby, the campaign
  screens and the stream do less work every frame.

### Security
- **The check of a host's run file is stricter.** Before a friend's game loads the host's run, it checks that the file
  holds only what a run save holds. The game's save reader accepts one form of text the check did not expect, which a
  modified host could have used to get a file past it. The check now refuses that form.
- **A host's run cannot break a friend's Run History page.** The entry a friend's game saves for a shared run is now
  checked first: an entry with no team is not saved, a time that is not a date becomes this PC's time, and a match
  against a team the friend's game does not have is left out. Its texts are cleaned like every other text a host sends.
  Before, a modified host could send an entry that made the page fail on it in every later launch.
- **With the host on keyboard and mouse, the host's screens answer only the host.** A friend's controller could move the
  cursor and confirm on the host's screens (the campaign map, for example) when the host had no gamepad. A host with a
  gamepad was not affected. (The pause menu and the campaign map's menu follow the game's couch rules for every host:
  see the README's known limitations.)
- **The host keeps a connection only for players it seats or is still deciding on.** A refused or removed player's
  connection is closed, and data that arrives on a channel the host does not read is discarded, so it can no longer
  pile up in the host's memory.
- A friend's game plays at most 16 of a host's direct sounds at once and 16 a second (a real match plays far fewer).
- A typed escape sequence in a Steam name can no longer be drawn as text markup in the game's prompts and toasts.

## [0.11.2-beta] - 2026-11-06 (protocol 9)

For the game's Nov 6 update. On the new game version, online play needs this release: earlier releases keep online off
on it, and the game plays normally.

### Changed
- Follows the new game version's run save, which no longer keeps a logo repository, and bounds the two stats it adds
  (the most injured skaters on the ice, and the best checking of a golden-helmet forward) in a friend's check of what a
  host credits.

### Fixed
- A friend's shared progress from a host is saved again on the new game version: the game now writes only the stats its
  own code marked as changed, so the host's credit to a friend (stats, and the stats of skaters, goalies, relics, talents
  and abilities) and the per-run counters would have stayed in memory after the first save of a session. The friend's
  log says what each batch wrote.
- In a host's campaign match, a friend's game no longer adds the friend's own pad player to the home team (the new game
  version does that for a campaign match with no players on the PC; on a friend's PC the host's players play).

## [0.11.1-beta] - 2026-11-06 (protocol 9)

The same protocol as 0.11.0: players on 0.11.0 and 0.11.1 play together, and the game shows a notice that both should
have the same release.

### Fixed
- Hosting or starting again with nothing played made a new save backup each time, and each one pushed an older,
  different backup out of the ten kept. The game saves its play-time counter on its own (a return to the title is
  enough), so the saves never looked the same twice. A backup that differs from the newest only in that counter is now
  the same backup, as the log says.

## [0.11.0-beta] - 2026-11-06 (protocol 9)

A new protocol and a new install layout: a friend on 0.10.x or earlier can't join a 0.11.0 host, or the other way round.
Everyone installs this release. The mod now lives in one folder, `Mods\T2TOnline`, so an update from 0.10.0 needs a step
by hand: the game names the old files to delete (see Changed).

### Added
- **Make a bug report** (F1 > Help): one zip of the newest logs, cleaned of Steam names and IDs, folder names, IP
  addresses and local times, in `UserData\T2TOnline\reports\`, with Explorer opened at it while the panel is open (the
  Help tab's **Show in Explorer** otherwise, so Explorer never takes the focus from a match). It holds no saves or
  settings, and its `report.txt` lists what is inside. Posting this zip replaces posting raw log files.
- **Mark this moment** (the Insert key, or F1 > Help): note when something goes wrong so a bug report shows where. The
  game shows its own "Moment n marked for a bug report." toast.
- An install check at start-up: it finds files of an older release left in the game folder (online stays off, and the
  game's own prompt names each file to delete) and a mix of two releases' files (the mod switches itself off, says so
  at the title and names the files).
- **Help > Open data folder**, and a `README.txt` in each of the mod's two data folders saying what it holds and what
  deleting it loses.
- `AllowDirectConnections` in the mod's settings file (off by default): see Security.
- The Help tab scrolls, so every block fits at any window size.

### Changed
- **One-folder install.** The zip extracts to `Mods\T2TOnline` (the mod, its licence and notices, and a
  `manifest.json`) and one `T2T Online - READ ME FIRST.txt`. Removing the mod is deleting that folder. From 0.10.0 or
  earlier, delete the old `T2TOnline*` files in `Plugins` and `UserLibs` (and `LICENSE` and `THIRD_PARTY_NOTICES.md`
  next to the game) when the game's prompt names them. MelonLoader no longer warns about missing dependencies for the
  mod on a first launch.
- **Settings have their own file**, `UserData\T2TOnline\settings.cfg`, moved there automatically from MelonLoader's
  shared `MelonPreferences.cfg` (your values are kept). Developer settings are hidden and only written when changed.
- **Logs name players by a tag** (such as `P#a0c1d2`, the same in every player's log of one lobby, new for each lobby)
  instead of Steam names, IDs and lobby IDs, say `relayed` instead of naming a relay site, and use UTC. A log keeps at
  most 20 files, 20 MB each, 100 MB in all and 30 days. **The session logs of 0.10.0 and earlier are deleted once, at
  the first start**, because they hold Steam names.
- Save backup folders are named in UTC (`20261105T100000Z-host`); older folders keep their names and are still kept and
  restored. A backup taken before following a host no longer records the host's name, and the record of shared play
  no longer holds a host's Steam ID.
- A friend's Steam status reads "T2T Online: in a friend's co-op game" with the player count, instead of naming the
  host. The host's reads "T2T Online: hosting a co-op game".
- Developer output (match recordings, snapshot captures and the check tours' pictures) is under `UserData\T2TOnline\dev\`
  with a notice not to share it, and is trimmed at every start (recordings: 20 files and 250 MB; captures: 10; tours: 5).
- The in-game panel's and the READ ME's advice for a problem is now the bug report button, not log files.
- A friend's shared achievements unlock only for a match its own game played (at least 20 seconds of it) or the run it
  followed, judged by what the friend's game saw, and a friend who drops out of a match before its end, or joins in its
  last seconds, doesn't get that match's shared achievements. They unlock about a second after the match's end shows.
- A restore of your saves that the game stopped in the middle of is finished (or undone) at the next start whether or
  not online is on, before the game reads its saves. On a game version the mod can't patch, where Steam Cloud may put
  older copies back over a restore, a notice at the title says so once (the restore can be redone), and a run's counters
  (this run and this act) follow a restore and a game closed on a co-op slot on every launch the mod can patch, online or
  not.

### Security
- Unity's exception reporter (Cloud Diagnostics) is switched off through its own settings at start-up, again when the
  engine's session starts, and as the game closes, and the game's unused plain-HTTP stats upload is skipped: the game
  sends no exception reports while the mod is loaded. Unity's native crash handler has no switch and may still report a
  crash of the game itself; the README says so.
- **Connections go through Steam's relays only**, so the players you connect to never learn your IP address. The mod sets
  this at start-up, reads it back and logs it; if Steam doesn't keep it, online is switched off with a prompt, and a
  friend whose link stays direct for 20 seconds leaves the game. `AllowDirectConnections` turns direct connections on
  for networks that block the relays, after a restart of the game (your address is then shared with the players you
  connect to).
- **Only confirmed Steam friends connect.** If Steam can't say whether someone is a friend, the join is refused with a
  prompt telling the player to restart Steam and the game (before, it went ahead).
- **A modified host can't do what a host shouldn't to a friend's account.** A friend's game takes progress only from the
  host's session it joined, a stat can't rise faster than the friend's own play with that host explains (and never more
  than 200,000 in 24 hours) or fall by more than the host added plus a little, a record can't beat what that play
  allows, and shared achievements are judged by the match the friend's own game saw.
- Custom team, skater and logo names appear in logs and bug reports only as a length and a short code, including the
  game's own log lines that print them.
- Log, bug report and backup notes are checked in the build (`logcheck.py`) so they can't start holding personal data.
- The release is checked and reproducible: zip entries in a fixed order with the commit's UTC time, a read-back of
  every zip, a test install and an update over the previous release, a `.sha256` beside each zip, GitHub Actions pinned
  by commit, and a locked .NET SDK (8.0.427) and NuGet lock files. The README says how to check a download.

## [0.10.0-beta] - 2026-11-04 (protocol 8)

A new protocol: a friend on 0.9.x can't join a 0.10.0 host, or the other way round. Everyone installs this release.

### Added
- At a run's end where the host unlocked nothing new, each friend who did now sees their own new-unlocks screen, with
  their own controller, before heading back to the title.
- When a friend's game can't load the host's match, it asks them with the game's own prompt whether to try again (it
  leaves and joins the same game again, with their seat) or leave. A pad alone is enough.

### Changed
- After each brawl, the host's and each friend's logs say which fight buttons the friend pressed and how each of the
  AI's punches met their fighter, and a friend's log names the slowest moment of its playback, so a report of a brawl or
  a stutter shows what happened.
- The release is now the mod alone, `T2TOnline-<version>.zip`; MelonLoader 0.7.3 is installed from its official release.
  The zip includes the license and third-party notices.
- The author field MelonLoader shows for the mod now names the project's creator.
- Release builds no longer embed the build machine's folder paths in the assemblies or debug symbols.

### Fixed
- On the campaign map, a friend now sees the match preview pop up when the host picks a boss, elite or challenge team,
  as the host does.
- The Nurse's procedure screen opens for real on a friend's screen and follows the host's presses, instead of a text
  panel.
- The squad selection screen shows its text on a friend's screen from the start of a new run, and a host menu that isn't
  ready the moment it opens gets its text to friends a moment later.
- Relic toasts the host's game shows as a match starts now show on friends' screens too, instead of being dropped.
- A lineup changed on the world map reaches friends at once: their game no longer fails the next match after the host
  swaps skaters, and a friend who joins mid-run gets the new lineup.
- Friends see the same skater details tab (stats, abilities, modifiers) as the host in the lineup and the run-start
  lineup, and the detailed stat breakdown while the left trigger is held there.
- Friends now get the "act 2 won" achievement when the host's team beats act 2's boss with them.
- At a run's end, each friend's new-unlocks screen shows everything their own profile unlocked during the run, from every
  session of it, the last match included, instead of saying they unlocked nothing new.
- In a boss match, a friend's teammates could stand frozen while only their own skater moved: a relic that gives the
  team a random talent picked a different one on each PC. Relics that give skaters a talent at random (Hot Cake, hot and
  cold) now give a friend's skaters the same talents as the host's, with the same effects.
- The golfer boss's defense pairs now rotate on a friend's screen as on the host's.
- After the cup cultists' boss turned a skater, a friend's in-match menu showed as plain text instead of the game's own
  screen; a cultist turned into its evil twin now turns on a friend's screen too.
- Frogs now match the host's on a friend's screen (a frog only one PC rolled at the match's start, or one the host's
  play made), with their ability and icon.
- A skater hit by a zap now turns into the same skater on a friend's screen, and back.
- A skater with the bench borrower talent now borrows the same talent on every PC.
- A friend's game no longer plays sound and crowd effects for a skater it can't show, or plays a transformation's or a
  frog's sound twice.
- A friend who joined a game already in progress from the Join list no longer has the Join list left open behind the
  host's game.
- When a friend's game can't load the host's match (campaign or Quick Game), its loading screen goes at once instead of
  staying up with the menus blocked; the host's AI plays that friend's skater meanwhile (their pad no longer moves a
  player they can't see), the host's match menus no longer pile up as text on their screen, and the run's end still
  shows if the run ends meanwhile.
- A friend who leaves no longer shows as "Joining" in the host's lobby for a moment afterwards.

## [0.9.3-beta] - 2026-11-02 (protocol 7)

### Fixed
- After a campaign run ended, the lobby's result line read "Run over: Act 0, Floor 0". It now shows the act and floor the
  run reached.
- Join Lobby's friends browser briefly polled for games while the player was hosting, at Start.

## [0.9.2-beta] - 2026-11-02 (protocol 7)

For the game's update of November 1, 2026 (Steam build 26372689).

### Changed
- Releases are named "0.9.x Beta".

### Fixed
- Any game update stopped the game from starting while the mod was installed. The mod now clears MelonLoader's outdated
  files before MelonLoader rebuilds them, so the first launch after an update works (it takes a few minutes).
- On a friend's screen, the ball in a ball-and-chain match froze after the first stoppage, and the chain was never drawn.
- The release check now refuses to package a build the installed game version hasn't been tested with.

## [0.9.1] - 2026-11-01 (protocol 7)

Security and reliability fixes across the mod.

### Fixed
- Join Lobby listed no lobby: a friend's hosted lobby never appeared in it.
- Backups also keep the first backup of each of the last 7 days, so a night of games no longer pushes out older backups.
- A friend who returns after the host went home keeps what they were credited.
- The invite notice now appears. Also fixed: an unplugged controller in the lobby, a late joiner's scoreboard, a false
  trigger warning, the Nurse event's cards in either order, panels flashing on friends after each game, the host's
  settings notice in each friend's language, and other small problems.

### Security
- A modified host could make a friend's game load data types it should never load. The run file check now accepts only
  what the game's own save writer produces.
- Message flood limits, size caps on every network message, and a host's stream held neutral in the lobby.

## [0.9.0] - 2026-11-01 (protocol 7)

### Added
- An **Online** row on the title screen with Host Lobby and Join Lobby, a friends browser, and a lobby built from the
  game's own screens: four player cards with avatars, a ready mark and signal bars, game mode and co-op save selectors, an
  invite list, a cancellable countdown, and a return to the lobby after every game. Works with a controller alone.
- Friends keep their own controller, game and saves until the host starts.
- The exhibition's controller selection screens are real on friends' screens.
- Notices use the game's own toasts and prompts, in each player's language.

## [0.8.0] - 2026-10-31 (protocol 6)

### Added
- Every screen of a campaign run is the real game screen on friends' PCs, in each friend's language: event dialogue, the
  Nurse's procedure screen, the controller screen, unlockables and run history, the relic bar, transitions and sounds.
- A friend can change their own controls and game settings during a session (F1).
- The host's prompts appear in each friend's own button bindings; controller vibration on friends.

## [0.7.0 - 0.7.3] - 2026-10-31 (protocol 5)

### Added
- Keyboard-and-mouse players, each player's own control remapping, and brawl offers for friends.
- Joining through Steam: invites, Join Game, and rich presence.
- Co-op save slots in every backup, a restore checklist, and restores that survive Steam Cloud.
- Custom teams, players and logos from the host appear on friends' screens.
- The F1 panel's controller check, element by element, with trigger calibration help.

## [0.1.0 - 0.6.3] - 2026-10-29 to 2026-10-31

Development builds: a friend's controller driving a player on the host's game over Steam, the live match stream to
friends' games, online exhibition matches with achievements for every player, and the co-op campaign with shared
progress, co-op save slots and backups.
