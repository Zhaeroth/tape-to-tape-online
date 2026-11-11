# Development

How T2T Online is built, tested, checked and released, and the rules for changing it. T2T Online is a beta in active
development: this guide describes the code on `main`, and the [changelog](../CHANGELOG.md) says what each release
holds. Players start at the [README](../README.md).

## Architecture

1. **Host-authoritative listen server.** The host runs the real, unmodified couch co-op game.
2. **Friends are virtual Rewired controllers on the host** (raw pad state at 120 Hz, 8-frame redundancy, a playout
   buffer), or a virtual keyboard for keyboard-and-mouse players.
3. **Friends' games run "client mode":** gameplay switched off, the host's match drawn from the game's own UltimateReplay
   recorder bytes (60 Hz snapshots), with events, sounds, camera, HUD, time scale and menus mirrored. In the campaign a
   friend's game follows the host's run: checkpoints loaded with the game's own loader, the host's menus opened as real
   views with the host's data, and progress credited to the friend's own profile.
4. **Own skater, staged:** v1 (this release line) draws the newest snapshot; v2 will run a friend's own skating locally;
   v3 their own shots, passes, checks and pickups, validated by the host.
5. **Steam lobbies and ISteamNetworkingMessages** (the flat API), on three channels: realtime, events, bulk. Every
   connection goes through Steam's relays (ICE is switched off at start-up and read back) unless the player turns on
   `AllowDirectConnections`; only Steam friends the mod can confirm connect.

| Project | What it is |
|---|---|
| `src/T2TOnline.Core` | Everything that can be tested without the game: the protocol, the session, the lobby's logic, the stream format, the campaign's data and rules. net6.0, no Unity, no Steam. |
| `src/T2TOnline.Steam` | Bindings to Steamworks' flat API (`steam_api64.dll`, shipped with the game), with the managed wrappers the mod uses. |
| `src/T2TOnline.Plugin` | The MelonLoader plugin: start-up checks, Harmony patches, client mode, streaming, the campaign's views, the native lobby and the native screens (`NativeUi`: the Settings tab, the Backups screen, the problem report), the check tours and, for developers only, the F1 panel. Thin glue around the game; logic lives in Core. |
| `tests/` | xUnit tests for Core (run on net8.0 and net6.0) and Steam (net8.0). |
| `tools/` | The gate, the release script and the checks they run (`tools/checks`, `tools/gate`, `tools/package`). |

Code comments cite design decisions as tags such as `(D41)`: they refer to the maintainers' design records. A tag such
as `M3` or `M4` names the development stage a piece of code was built in, and `M5` the planned stage that runs a
friend's own skating locally.

## Requirements

- The .NET 8 SDK, **exactly 8.0.427**: `global.json` pins it with `rollForward` disabled, and CI installs exactly that, so
  every build uses the same compiler. The SDK builds the net6.0 plugin and the tests. The mod targets **net6.0**
  because MelonLoader 0.7.3, the newest official release, hosts .NET 6 (its runtime configuration asks for 6.0 at the
  latest minor, so each PC runs the newest 6.0.x it has), and a plugin can only use the runtime it is loaded into. The SDK, the
  target framework and MelonLoader move together, as one tested change, when MelonLoader's next official release
  ships; never an unofficial MelonLoader build.
- The .NET 6 runtime (for the Core tests' net6.0 run).
- Tape to Tape with MelonLoader 0.7.3 installed and launched once to its title screen, so MelonLoader has finished
  generating its interop assemblies (the README's installation steps cover that first launch).
- Python 3.10 or later for the checks in `tools/checks`.
- PowerShell 5.1 or later for the gate and the release script.
- NuGet packages (the test projects only; the shipped assemblies reference none) have lock files
  (`packages.lock.json`), restored in locked mode on CI: a package change includes its lock file. Dependabot proposes
  updates for NuGet packages and GitHub Actions monthly.

## Building

The plugin compiles against MelonLoader's assemblies and the interop assemblies MelonLoader generates from the game.
Point the build at your game folder once, in `Directory.Build.local.props` (ignored by git; copy
`Directory.Build.local.props.example`):

```xml
<Project>
  <PropertyGroup>
    <T2TGameDir Condition="'$(T2TGameDir)' == ''">C:\Program Files (x86)\Steam\steamapps\common\Tape to Tape</T2TGameDir>
  </PropertyGroup>
</Project>
```

(the condition keeps the order the tools use: the command line first, then the `T2T_GAME_DIR` environment variable, then
this file), or pass it on the command line:

```
dotnet build src/T2TOnline.Plugin -c Release -p:T2TGameDir="<game folder>"
```

Without `T2TGameDir` the build looks in `lib/game` (ignored by git). With only `MelonLoader/net6` present the plugin still
builds; code that needs the interop assemblies is compiled only when `MelonLoader/Il2CppAssemblies` exists (the
`HAS_INTEROP` symbol). Release builds map the repository's path to `/_/`, so the assemblies and debug symbols carry no
folder from the build machine.

### Installing a build by hand

A release installs as one folder, `Mods/T2TOnline` (MelonLoader 0.7.3 treats a subfolder of `Mods` that holds a
`manifest.json` as one mod, with its own `Plugins` and `UserLibs`):

| File | Goes to |
|---|---|
| `T2TOnline.dll` (+ `.pdb`) | `<game>/Mods/T2TOnline/Plugins/` |
| `T2TOnline.Core.dll` (+ `.pdb`) | `<game>/Mods/T2TOnline/UserLibs/` |
| `T2TOnline.Steam.dll` (+ `.pdb`) | `<game>/Mods/T2TOnline/UserLibs/` |
| `manifest.json` | `<game>/Mods/T2TOnline/` |

`manifest.json` is generated by the release script and holds only `name`, `version_number` (the release as written in
files, `x.y.z-beta`) and `description`: MelonLoader looks for the file and never reads it. The release also carries
`LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` in that folder, and `T2T Online - READ ME FIRST.txt` next to the game.

The mod doesn't depend on where its files are: nothing in it uses a plugin or library folder to find a file. A flat
install (`Plugins/` and `UserLibs/` of the game folder, as releases up to 0.10.0 used) loads the same way. Never leave
two copies where MelonLoader looks: the plugin's **install check** (below) names them, keeps online off and, when
Core or Steam isn't the build the plugin was made with, switches the plugin off.

The build never copies anything into the game folder by itself.

**The install check** (`Startup/InstallCheck.cs`, decided by the BCL-only `Startup/InstallLayout.cs`, which is linked into
the Core tests and run over in-memory folder trees) runs first in the plugin's constructor, before anything that loads
Core. It ports MelonLoader 0.7.3's folder rules and looks at every `T2TOnline*.dll` by assembly name, our PDBs by name
and the previous release's two root documents by hash. Outcomes: *clean*; *leftovers* (the build that runs is whole, but
other copies or old files are there): a warning, online off for the session and the game's own prompt naming each file
to delete, since the mod never deletes a mod's files from the game folder, its own old ones included; *mixed* (Core or Steam is not the plugin's build):
the plugin unregisters itself (no patches, nothing online, no session log), prints an error through MelonLoader's
logger and shows an English notice at the title. The Unity crash reporting and analytics blocks stay on for a mixed
install (`Startup/InertHousekeeping.cs`). `MelonOptionalDependencies` (generated at build time from the project's interop
references, checked by `zipcheck.py`) removes MelonLoader's first-launch "missing dependencies" warning for the mod and
leaves the generator's ordering rule below alone.

## The players' build and the native screens

**The players' build** is the mod with the `DevTools` setting off, which is the default. It has no F1 panel (the plugin
makes no panel, and none of the panel's input patches go in), no F7, Shift+F7, Ctrl+F5, Ctrl+F6 or F9, and no other
developer key; Insert still marks a moment. The log says which build runs, once, after the settings line:
`Developer tools: off (the players' build).` or `Developer tools: on (F1, F7, Shift+F7, Ctrl+F5, Ctrl+F6 and the check
tours).` With `DevTools` off the native lobby reads as on whatever `settings.cfg` says (a player has no other way to
host), and the log says so once: `Settings: NativeLobby reads as on: the players' build has no other way online.`

Core's `PlayersBuild` holds the rules and the English texts that say where each function lives ("Settings > Online >
Problem Report"), built from the same words the screens show, so a text and the row it names cannot drift apart.
`OnlineSettings/PlayerTextsTests` fails when any text a player can read (a word, a notice, a setting's description, a file
the mod writes for the player) names F1 or the panel. A new text a player can read names the game's own screens.

**The native screens** (`src/T2TOnline.Plugin/NativeUi`, with their rules and data in Core) are what a player uses. Each is
built from the game's own prefabs and navigation, with words in the game's 15 languages (`src/T2TOnline.Core/Lobby/LobbyWords.cs`,
each word's fit measured by `lobbywords_check.py`), and works by gamepad, keyboard and mouse.

- `NativeUiLayer` starts, on a game build the mod patches, before the runtime or the offline shell, and ticks every frame:
  the held prefabs, the shell the Backups screen opens in, the game's yes/no and notice prompts (`LobbyPrompts`). The
  screens therefore exist whatever online's state: online on, online off on a supported game build, and after the
  runtime stopped on an error (`SettingsState`). On a game build the mod does not patch, none of it is installed (online
  stays off and the corner notice and the held Insert are the only ways).
- `SettingsTab` grafts the **Online** tab into the game's Settings menu: one postfix on `SettingsMenu.InitData`, which
  the UI module's generic menu load calls on every open. The tab and its panel are clones of the menu's own tab and
  Gameplay panel; the tab array is grown with every reference written through the runtime's write barrier and held by
  strong GC handles; the graft is all or nothing (a failure leaves the game's own seven tabs and logs the step).
  `SettingsTabRows` makes the rows, and Core `SettingsRows` decides which rows show and which can be used (Direct
  Connections only when on or after Steam refused relay-only; Leave only for a friend and End only for the host in a
  session; Save Backups only at the title and out of a session).
- `BackupsScreen` is the restore screen over Settings (Core `BackupRows` builds its rows); a restore goes through
  `SaveBackups.TryRestore`, asks on the game's yes/no, backs up what is there first, and for saves ends in **Later** or
  **Quit**.
- `ReportFlow` makes the problem report (the same zip as the developer panel's button) and says where it is on the
  game's prompt; `ReportOfferWatch` and Core `ReportOffer` decide the offer at the title after a session that ended in an
  error, a runtime stop, or a launch that closed during an online game (a marker file, `report-offer.txt`, one offer per
  launch). Holding Insert for 2 seconds makes a report where the screens are not installed.
- The title's **Online** menu is `NativeLobby/OnlineGroup`: Host Lobby, Join Lobby, Online Settings, Report a Problem, Back.
- A following friend's **My Settings** (`Online/OnlineSession.MySettings.cs`, Core `MySettingsShortcut`) is a clone of the
  host's pause menu's own bottom shortcut (RT on a pad, Left Alt on a keyboard, or a click) and, on the campaign map,
  the map bar's own Menu entry (Start, Esc or a click); the press is held back from the host.
- At a campaign's start the host's seating (`Online/OnlineSession.TeamSeats.cs`, Core `TeamSeats`) remembers the host's
  players and every friend on the team in the game's own memory of controller sides before the game's own seating runs
  (a prefix on `TitleScreen.AssignDefaultControllers`, which only New Run and Continue run); the memory is put back
  when the run ends and when hosting stops. A friend who joins during a run is added to the game's team list on the map
  and plays from the next match. The game's solo squad is left to the game's own rule, and the game's team takes at most
  6 players.

## Tests

```
dotnet test tests/T2TOnline.Core.Tests -c Release
dotnet test tests/T2TOnline.Steam.Tests -c Release
```

The Core tests target net8.0 and net6.0, so `dotnet test` runs them twice, the second time on the runtime the game uses
(`-f net8.0` runs one). The tests need neither the game nor any file outside the repository, so GitHub runs both
projects on Windows for every push to `main` and every pull request (`.github/workflows/tests.yml`).

Some tests worth knowing: `Lobby/LobbyFlowTableTests` covers every row of the lobby's flow; `Lobby/LobbyLifeTests` runs
host and friend sessions over a fake network frame by frame; `Hooks/HookListsTests` reads the hook lists with the plugin's
own parsers (their source files are linked into the test project), so a line the start-up check would refuse fails here
first; `Protocol/WireLayoutTests` pins every wire layout byte for byte: when it fails, bump `ProtocolInfo.Version` (the
failure prints the new pins).

## The gate

`tools/gate/Invoke-Gate.ps1` is the one check every merge and every release passes. It has fourteen steps. Each runs on
its own and its own exit code decides:

| Step | Needs |
|---|---|
| Clean tree: everything committed (a note, or a failure with `-Release`) | git |
| The plugin built in Release with 0 warnings | the game folder |
| Core tests (both runtimes) | nothing |
| Steam tests | nothing |
| `tools/checks/hookcheck.py` on all four hook lists | a decompiled listing of the game build |
| `tools/checks/rowcheck`: every hook row resolved in MelonLoader's interop assemblies by the start-up check's own rules | the game folder |
| `tools/checks/uipartscheck.py`: every prefab part the lobby clones still exists in the game's bundles | the game folder |
| `tools/checks/lobbywords_check.py`: its own tests, then every lobby word has its glyphs and fits | the game's font metrics and terms |
| The installed game's hash is in `BuildFingerprint.TestedBuilds` | the game folder |
| Package checks (`tools/checks/zipcheck.py`): the READ ME template's markers, the pinned MelonLoader against `TestedMelonLoader`, the checker's own tests, and the plugin build's three assemblies and PDBs (versions, references, `/_/` source paths, `MelonOptionalDependencies`) | the plugin build |
| `tools/checks/forbidden_strings.py`: its own tests, then no forbidden text in the tree | a patterns file |
| `tools/checks/logcheck.py`: its own tests, then no log call writes personal data (see "Logging") | nothing |
| `tools/checks/rowuse`: its own tests, then every game member the built plugin refers to has a hook row and every row of `hooks.txt` and `menus_initdata.txt` is used (the allow file `tools/checks/rowuse_allow.txt` holds what the rules can't decide, each entry with its reason) | the game folder and the plugin build |
| `tools/checks/patchinstance.py`: its own tests, then no Harmony patch asks for `__instance` on a static game method | a decompiled listing of the game build |

Inputs come from parameters or environment variables:

| Input | Parameter | Environment | Otherwise |
|---|---|---|---|
| Game folder | `-GameDir` | `T2T_GAME_DIR` | `T2TGameDir` in `Directory.Build.local.props`, then Steam's own library records |
| Decompiled listing | `-ListingDir` | `T2T_LISTING_DIR` | step skipped |
| Font metrics and terms | `-GameDataDir` | `T2T_GAME_DATA` | step skipped |
| Forbidden patterns | `-PatternsFile` | | step skipped |

Without `-Release`, a step whose input is missing prints `SKIPPED` and says what to pass. With `-Release` every input is
required and the working tree must be clean. The gate ends with `GATE GREEN at <commit>` or the failed steps.

The listing, the font metrics and the terms are derived from the game and are not distributed. `lobbywords_check.py
--make-metrics` rebuilds the font metrics from an installed game.

## Releases

`tools/package/New-ReleasePackage.ps1` runs the gate with `-Release`, builds the plugin again from scratch, and writes
to `dist/` (or `-OutDir`):

- `T2TOnline-<version>.zip`: the one-folder layout above plus `T2T Online - READ ME FIRST.txt` (from
  `tools/package/READ ME FIRST.txt`). This is the public download.
- `T2TOnline-<version>-with-MelonLoader.zip`: the same plus the official MelonLoader 0.7.3 release, unmodified
  (pinned in `tools/package/melonloader.pin` with its SHA-256), for handing to players who haven't installed MelonLoader.
- A `.sha256` file beside each zip (`<hash>  <name>`), `T2TOnline-<version>-release-notes.txt` (each zip's SHA-256 and
  size, the commit, the SDK) and a line per zip in `packages.log` (version, file, commit, gate result, SDK, the game's
  fingerprint, SHA-256).

Every entry of both zips carries the release commit's UTC time, in sorted order with fixed attributes, so a build of
one commit gives the same zip. Each written zip is then read back by `tools/checks/zipcheck.py` (never the staging
folder), and any failed check stops the release:

- **entries**: an allow-list of exact names, ASCII with `/`, times, order and attributes;
- **size**: a budget per zip, and at most 25% over the previous release's public zip;
- **documents**: the READ ME (ASCII, CRLF, no `{{` left, the version in its first line, no text for the
  with-MelonLoader zip in the public variant), the manifest and the licence texts;
- **assemblies**: each version equals `VersionPrefix`-`VersionSuffix`, references resolve, PDBs hold only `/_/` source
  paths and no Source Link, `MelonOptionalDependencies` names every interop reference;
- **install**: the zip extracted over MelonLoader's own files, loaded by MelonLoader 0.7.3's folder rules (ported from its
  code): the plugin in the Plugins phase, Core and Steam in the UserLibs phase, nothing colliding;
- **update**: the previous release's public zip (`-PreviousZip`, kept outside `dist/`, which holds only the current
  release) extracted, then this one over it: the new copies win, and the files left behind are exactly the previous
  layout's, which the install check names;
- **melonloader**: the MelonLoader in the zip is the version the plugin is tested with.

Both zips are scanned with `forbidden_strings.py` (DLL and PDB contents included) before they get their real names, and
the plugin is built a second time: the zips must match byte for byte (`-SkipRebuildCheck` skips that for a quick run).
The script refuses a version that has already been packaged (`-Force` overrides); `-DryRun` runs the gate and the
package checks only; `-SkipUpdateCheck` is for a first release.

**Reproducibility.** Release builds are deterministic (`Directory.Build.props`: deterministic, source paths mapped to
`/_/`, no Source Link) and depend only on the pinned SDK. Anyone can rebuild Core and Steam from a release's tag and
compare them with the zip: `tools/package/Compare-ReleaseBuild.ps1 -Zip <public zip>` rebuilds both and compares each
DLL and PDB byte for byte (the workflow `.github/workflows/reproducible.yml` does it when a release is published). The
plugin compiles against the game's own interop assemblies, which only a PC with the game has, so it is not compared.

**Versions.** The one version is `VersionPrefix` and `VersionSuffix` in `Directory.Build.props`; bump `VersionPrefix`
for every package. The build writes it into MelonLoader's `MelonInfo`; the mod shows `x.y.z-beta` in the lobby, the
handshake and the log, and "x.y.z Beta" on screen (`ReleaseVersion.ForScreen`). `ProtocolInfo.Version` changes whenever a
wire layout changes, and a protocol change goes with a new minor version: a peer on another protocol is refused, one on
another release of the same protocol joins with a notice.

## Rules for changing the code

- **Nothing may throw into a game callback.** Catch, log, and switch online mode (or the failing module) off.
- **Network input is untrusted.** Decoders return false on bad data and never throw; every count and size is capped; a
  peer can't freeze, crash or exhaust another player's game.
- **A friend's run save is never written.** Friends load the host's checkpoint through the game's own loader, from the
  mod's own file, after the run file check (`RunFileTypes` reads only what the game's save writer writes). For the whole
  session a friend's run saves are skipped at the source, and the save guard lets through only the friend's own profile and
  stats after shared progress applies, plus their settings while their own Settings menu is open.
- **Crash reporting and analytics stay off while the mod is loaded** (`Startup/EarlyHousekeeping.cs`,
  `Startup/Housekeeping.cs`, `Startup/EarlyAnalytics.cs`, `Startup/EngineTelemetry.cs`,
  `Startup/CrashReporting.cs`). They start before Harmony is available, so they are blocked in `OnPreInitialization`.
  Unity's own exception reporter (`CrashReportHandler`) is switched off through its internal calls at start-up,
  again at Unity's own start-up callbacks (the engine's session start copies the build's setting back), checked every
  second and as the game closes, each time read back and logged; the game's unused plain-HTTP stats uploader
  (`Stats.BetaData::SendData`) is skipped. The analytics exception itself can't be patched (its methods return
  `UniTask`). Unity's native crash handler has no switch: the README says so. A mixed install keeps all of it on
  without Core.
- **Never patch a folded function.** IL2CPP folds identical small functions; patching one patches them all. `hookcheck.py`
  checks every target. For a folded per-frame method, disable its component instead. Never patch a generic method.
- **Never patch a method that takes or returns a small struct by value** that the interop wraps as a class
  (`Nullable<T>`, `CancellationToken`, `UniTask<T>`, any 1-, 2-, 4- or 8-byte struct holding a reference): MelonLoader
  0.7.3's Il2CppInterop detour reads it as a pointer and the game crashes at the first call. Patch a callee with reference
  parameters; calling such a method is fine. `hookcheck.py` refuses these ("STRUCT ARG").
- **A patch target must also run.** The C++ compiler inlines small private methods and keeps a copy nothing calls; a patch
  there installs silently and never fires. Read a new target's callers in the game's code before relying on it.
- **A patch of a static game method takes no `__instance`:** Harmony passes null for it. The gate's `patchinstance` step fails on one.
- **`src/T2TOnline.Plugin/Hooks/hooks.txt` is the single source of truth** for every game member the mod touches. Add
  every new hook there: the list is embedded, checked at start-up and checked offline per game build.
  The gate's `rowuse` step keeps it complete (a used member with no row, or a row nothing uses, fails it).
- **Logs hold no personal data** (see "Logging"): never a Steam name, SteamID, lobby ID, relay site, IP address, local
  time, folder path or player-written text in a log line, a bug report or one of the mod's own notes. Use the log
  tags, labels and `ModLog.SafeErrorText(ex)`; `logcheck.py` in the gate enforces it and `LogScrub` is the backstop.
- **A log line reads the same on every PC:** a number or a date in a log line goes through the invariant culture
  (`string.Create(CultureInfo.InvariantCulture, $"...{seconds:0.0} s")`, or `text.Append(CultureInfo.InvariantCulture,
  $"...")`), because the game's thread runs in the player's culture, where `1.5` can be `1,5`. Never change the thread's
  culture to get it: the game's own screens use it. `logcheck.py` fails on a decimal, group or date format with no
  provider, and on a float with no format in a log call.
- **The install check runs first in the plugin's constructor** and touches nothing that loads Core
  (`Startup/InstallCheck.cs`); the constructor, `OnPreInitialization` and `OnInitializeMelon` have a fixed order that
  needs a real launch to change.
- **Nothing may load an interop assembly before MelonLoader's generator runs.** MelonLoader's dependency sort loads every
  assembly a plugin references before the generator, which then can't replace a file in use, so a game update would stop
  the game from starting. The plugin's constructor runs before that and clears the old files whenever the generator's own
  rules say it will run (`Startup/StaleInterop.cs`); nothing on that path may touch an interop type. Every patch is made by
  hand (`[assembly: HarmonyDontPatchAll]`), so a Harmony patch attribute does nothing. A package with a different
  MelonLoader must be tested unzipped over the previous one.
- **Unknown game builds fail safe:** the game runs normally with online off.
- **Standards:** nullable reference types on, warnings are errors, .NET analyzers at `latest-recommended`, code style from
  `.editorconfig` enforced in the build, XML documentation on every public member. Testable logic goes in Core with tests.
  The mod runs inside a 60 fps game: no avoidable per-frame allocations, interop calls or logging.
- **Self-diagnosing builds:** the session log says what happened at each step a play test checks, on every PC, without
  per-frame spam.
- **Untrusted hosts.** A friend's game takes progress only from the hosting session it was welcomed into, bounds every
  stat by its own play with that host, and unlocks a shared achievement only for a match its own game played (at least `StatBounds.SecondsPerMatch` = 20 s of
  it, run moments' match included, judged by the kind of match it saw) or the run it followed (`Campaign/StatBounds.cs`, `Session/MomentInbox.cs`). A change
  there needs the tests of both.
- **Texts a player reads name the game's own screens**, never F1 or the panel (`PlayersBuild`, guarded by a Core test),
  and a function a player needs gets a native row or screen, not a developer key.
- **Public text:** comments, strings, test data, commit messages and documents describe the code and the game, never
  people, private machines or internal events. Example names are Alex, Sam, Riley and Casey.

## Logging

Each launch writes `UserData/T2TOnline/logs/session-<UTC time>.log`, and every line of it also goes to MelonLoader's
console and `MelonLoader/Latest.log` (minus TRACE), so the rule "Logs hold no personal data" holds for both. Three layers
keep personal data out:

1. **At the source:** lines name players and lobbies by tag, folders by label, times in UTC, content by length and a
   keyed hash, and exceptions through `SafeError` (`ModLog.SafeErrorText(ex)`: the type and a message with folders as
   labels, on one line of at most 160 characters). `tools/checks/logcheck.py` reads every log call and fails on a name, a
   SteamID64, a lobby ID, a folder path, a raw `ex.Message` (or a whole exception outside an error's line),
   player-written text, a local time, the tag key, an object's name (`.name`) its file hasn't vetted, or, anywhere in
   `src`, a read of a JSON member named like a name (`firstName`, `lastName`, `name`, `teamName`, `nickname`, ...: a
   value read from a run file reaches a line through a type's `ToString`, which no log call shows); its allow
   file (`tools/checks/logcheck_allow.txt`) lists the vetted exceptions with a reason each, and a count that no longer
   matches fails too, so the list only shrinks. It reads notices and the progress apply log too. A notice that names a
   player goes through `NotifyAbout` or `NotifyText` (the screen gets the name, the log the tag); Core code writes
   exceptions with `SafeError.Text(ex)`; a team in a log line goes through `TeamLogText` (a custom team is `a custom
   team (11 characters, #4be1)`; a game team keeps its name once the mod has read it from the game's own team lists,
   since every team's id, the game's included, is a GUID); a skater goes through `SkaterLogText.Of` (`skater <asset
   id>`); a host's toast or prompt text through `ShownTextLog` (a term by its key, anything else, which can hold a
   custom team's or skater's name, as its shape); anything else a host sends by one rule, `HostText` (an id, a key, a
   menu's or an element's name, a map point as it is when it has an identifier's shape, else `a key of N characters`;
   a text a player reads as its length or by the key it was made from), which the `ToString` of every Core type a host
   fills already applies; a backup's reason through `SaveBackupNames.ReasonForLog`. A
   skater's object name is player-written text (the game names it after the skater, and a custom skater's names are
   typed): the checker finds it, and a skater's `FullName`, read directly (`skater.name`, `_forwardData.name`,
   `team.logo.name`, `forward.FullName`), through the file's own string helpers that return it, and through a local of
   the calling method, one the call passes on by LINQ or a builder's `ToString` included. What it still can't see (a
   value passed on further, or a log helper it doesn't know) is covered by `LogScrub` for Steam names, IDs, folders and addresses, and
   for the custom teams', skaters', goalies' and logos' names the mod has met (registered where custom content enters
   the PC: `LogNames.NotePlayerText`); any other player-written text only by the call site itself.
2. **One scrubber before both sinks:** `ModLog` passes every line through Core's `LogScrub`, which replaces
   SteamID64s, lobby IDs, every name the mod has read from Steam, the known folders and other users' profile folders
   (labels `<game folder>`, `<Steam library>`, `<Steam>`, `%LOCALAPPDATA%\T2T Online`, `<user folder>`, `<folder>`), IP
   addresses (`<ip>`), `UTC+hh:mm` style offsets and the tag key, and cleans control characters. A line the scrubber can't
   check is left out and says so. At most 16 different names are kept per player (`LogScrub.MaxNamesPerPlayer`; a host
   renaming seats on every board can't grow the registry), and only the lobby's members' board names are registered. A
   name made of the log's own words, or that looks like a tag, is replaced only where it appears in quotes. The names
   of custom teams, skaters, goalies and logos the mod has met become `a custom name (11 characters, #4be1)`, never a
   player's tag (at most `LogScrub.MaxPlayerTexts`); one that is a single word, very short or made of the log's own
   words is replaced only where it appears in quotes.
3. **The bug report** cleans MelonLoader's and Unity's files the same way (see "Bug reports").

**Tags.** A player is `P#` and 6 hex digits (8 once two members of a lobby collide, which both PCs see alike): the first
bytes of `HMAC-SHA256(key, SteamID64)`. A lobby is `L#` and 6 digits from `HMAC-SHA256(key, lobby ID)`. The key is 16
random bytes per hosting session, which the host publishes as lobby data `t2t.logtag` (32 hex digits, kept across a
re-made lobby); a member accepts exactly that, else uses a key of its own launch and logs that tags won't match the
host's. Outside a lobby each PC uses its launch's key. The key is never logged. A plain hash would let anyone holding
one SteamID64 confirm its tag at once, and lobby IDs are guessable, hence the key.

**Reading a log.** The first line is the header: `T2T Online <version>, commit <12 hex>, protocol <n>, log format 2; UTC
date <date>.`, then `Game folder: ...` as a description, not a path (for example a Steam library on a fixed drive, outside the user
profile, ASCII, a character count), then the settings not at their defaults (`Settings not at their defaults: ...`). Once per lobby each PC writes `Log tags:
this PC is P#... in lobby L#..., host P#...`, and a `Log tags: P#...'s Steam name: ...` line per member that gives the
name's shape only (its length, scripts and any characters the fonts can't draw). To match two PCs' logs, find the
`Log tags:` lines: the host's `this PC is` tag is the friend's `host` tag, the lobby tag is the same on both, and a
friend's own `this PC is` tag is the tag the host's lines use for that friend. `P#??????` is a player the PC has no name
for yet. Link lines say `relayed` or `direct`, never a relay site; the ping location is a count. The date is written
again when a session passes midnight UTC. Log format 2 is what the header says: a log without it was written by a
build that logged Steam names, and the first start of 0.11.0 deletes such session logs once (one line), and a bug
report leaves such files out.

## After a game update

Online needs the exact tested game build, so every game update goes through this list before players can play online
again. The plugin's start-up check keeps the game playable meanwhile, with online off.

1. Launch the updated game once (it takes a few minutes): MelonLoader regenerates its interop assemblies. On a PC that
   builds the mod, run `dotnet build-server shutdown` first: the compiler server keeps the old interop files open, and the
   generator can't replace a file in use.
2. With a decompiled listing of the new build, run `hookcheck.py` on `hooks.txt`, `menus_initdata.txt`, `clientmode.txt`
   (its `[disable]` and `[presentation]` rows may be folded: those components are switched off or left alone, never
   patched) and `presentation.txt` (existence only). Fix anything missing, folded, generic or STRUCT ARG: patch a callee,
   or mark the row `called only` when the mod only calls it; engine members the game never calls are marked
   `# interop only`.
3. Run `rowcheck`, `uipartscheck.py`, and `lobbywords_check.py` after `--make-metrics` (a word that lost a glyph or got too
   wide fails). The Settings tab and the Backups screen clone the game's prefabs, so a changed prefab shows here first.
4. Check that every patched method still has a caller in the new build, and that the Steam library still exports every
   function `T2TOnline.Steam` binds (`[OptionalExport]` bindings may be absent).
5. Add the build's hashes to `BuildFingerprint.TestedBuilds` with its Steam build number (the `buildid` of the game's
   Steam app manifest: the untested-build notice reads it to say whether the game is newer or older than the mod's
   build), bump the version and release.
6. Launch the players' build once (`DevTools` off) and read its log: the mode line, `Native screens: on`, the Settings
   tab's `Settings menu: the Online tab is installed` (and, once Settings has been opened, `Settings menu: the Online
   tab is in (tab 8 of 8).`), and no key line. The check tours need `DevTools` on and run on top of that.

## The check tours

`NativeLobby/LobbyUiCheck` exercises the mod in the real game with nobody at the keys: the title, the lobby, the
native screens and, for the campaign tours, a hosted co-op run played by the game's own test walker. Core's
`UiCheckPlan` holds the steps and their order rules, `UiCheckRules` the layout flags, `TourStarts` where each step may
start. It is a developer tool: `DevTools` must be on, and tours are not part of the players' build.

**Start one.** Create `<game>/UserData/T2TOnline/dev/ui-check/run.txt`, whose first line names the tour and its
arguments (`campaign full seed=3`; the file is deleted when read, and the game quits when the tour ends), then launch
the game; or press **Ctrl+F12** at the title, out of a session, which runs `all` and leaves the game open. A line the
plan does not declare (an unknown tour, key or value) is refused with a sentence and nothing runs. Any press by a
person stops a tour and restores everything.

| Tour | Limit | What it proves, and its limit |
|---|---|---|
| `all` (a blank line) | 12 min | The title, the shell and its widgets, the screens on a fake session, languages and window shapes |
| `screens` | 12 min | Every screen on a fake session, in all 15 languages and four window shapes: the Online menu, the Join list, the lobby, Settings > Online in each state, the problem report (its prompts in the player's language, the offer after an error and the offer after a launch that closed during an online game among them; the ready prompt and the "closed" offer in every language the tour runs: no key, the same steps), the Backups screen. States a fake session cannot make (a friend's own menu, a host's End, Direct Connections) are drawn through a check-only rows context. `screens en` and `screens quick`: English only, and at one shape |
| `selfdrive` | 8 min | The session's flow as host through its actions: a real private lobby, a Quick Game started and left, the return, a New Run backed out of, a countdown cancelled, a failure, a problem report made through the report flow. Key `team=custom|last` (custom adds a step before the start and one after the return: the Quick Game is played on this PC's first custom team, set by the game's own Quick Game team call; its typed names are held in memory only, and this launch's session log is read for them: no line may hold one, and the cleaner's count line and the setup line's `a custom team (...)` must be there; only counts and line numbers are written. With no custom team the steps say they were skipped: a tour never makes one) |
| `padwalk` | 10 min | The screens pressed by a virtual pad alone, as a person would: the title, the lobby, Settings > Online, the Backups screen (read only), the problem report from both rows and from a match. Key `endonline=on|off` (on adds two last steps: in a lobby hosted by the pad, End Online Game and its End pressed, Settings still up over the title, the hosted co-op slot's run file let go by the call a restore makes (no restore runs); then at once the title's own New Run, answered only by B: Cancel on its question, or back out of its squad screen; a screen the step does not recognise gets no press and stops the tour) |
| `kbwalk` | 6 min | The same screens by a virtual keyboard made from this PC's own key maps, and by the mouse in the game's own mouse mode (every row's hit area is raycast; only what can be put back is clicked: the two toggles twice, Online Settings, the Backups screen's tabs and its Back, the lobby's mode glyphs) |
| `onepc` | 8 min | A friend really in a seat: this game as host, with a stand-in friend over Steam's loopback; includes the friend whose game cannot load the next match. Key `dc=on|off` (on adds a step with the friend in: Settings > Online's Direct Connections pressed by the pad, refused for the session with the game's notice to leave first and its log line, the setting unchanged. The row is shown through the check-only rows context, this lobby's own with Steam's relay refusal stood in: it proves the press, the rule with the session's real count of others, the notice and the line, not the row's showing after a real refusal or with the setting on) |
| `replay` | 8 min | A friend's match drawing on one PC: a Play Now match recorded, played back in place, then loaded fresh and played |
| `offline reason=steam`, `relay` or `error` | 5 min | The mod started as if online were off for that reason, or stopped as after an error: the Settings tab, a report, the Backups screen and the report offer in that state |
| `campaign act1`, `campaign full` | 15 min, 40 min | A hosted co-op run through act 1, or to its end, with the stand-in friend seated: the game's own walker plays the nodes (an event's own takeover screen, the Nurse's procedure screen, which the walker does not drive, is driven by the virtual pad while the walk waits), each match is ended through the game's own end of match, the run is left and continued once, a friend's drop makes the AI stand-in play, and the run is conceded or finished. Keys `seed=<n>`, `record=on|off`, and five keys that each add steps only when on (off by default, so the tour runs as without them): `kbhost=on` (after the first match: the map's menu is opened by the pad, read and closed, since any player drives that menu and the game asks nothing there; then on the map, with the tour's pad set aside, so that the host's own player is the keyboard alone, the stand-in friend's press must be refused and the map's cursor stay); `events=on` (after the first match: every kind of event the developer's event key opens, by its own call, each driven to its end by the pad; a signing or a loss must come of the picks, in the tour's own run); `late=on` (nobody is seated at the lobby; after the Continue the stand-in friend joins on the map with their own A and must be on the run's team, and their skater must play the next match); `sides=on` (before the drop's nodes: on the controller screen the friend sits out by their own press and the host's A saves it, the game's memory and team list are read, then the friend is put back the same way); `brawl=on` (in a match, an offer from F10's own call taken by the friend's LB+RB; in the drop match, LB+RB held through the hand-back must not take an open offer until let go and pressed again; a declined offer is a note; the brawl an accepted offer begins is waited out, and one still on after 60 s fails the step, is written out fighter by fighter and has the state it shares with later matches ended by the game's own calls before the match is ended; a step after the nodes reports both offers); with `brawl=on`, `brawlend=game|tour` (`tour`, the default, as without it; `game`: the fresh offer's match is ended by the forced win once its fight has begun, with the fight on and without the tour's own end of its state, as a Concede or Quit during a fight leaves it; the brawl step must then read the mod's guard acting at that match's unload, both of the game's fight flags off on the map, every player on the run's team on a skater in the next match (a game player not on the team, such as the PC's idle keyboard, is named and not counted), and no later brawl line saying a match ended during a brawl it didn't have; a run that doesn't show it (the offer declined, no fight begun, the map or the next match not reached) fails the step too; refused without `brawl=on`, and it adds no step). `kbhost=on` with `late=on` is refused: the keyboard host step needs the friend seated right after the first match. A step that cannot leave the game where the next one starts (an event screen the pad cannot leave, a screen over the map after the late join, the friend not back on the team) stops the tour |

A tour cannot see a friend's PC, a physical controller or keyboard, sound, or more than one friend. The check tours are
the mod's own acceptance test of its screens; they do not replace a session with real players.

**A tour never touches a player's data or reaches a person.** While a tour runs (`TourHolds`): the save guard refuses
every Easy Save write of the game's but the tour's own run file (`dev/ui-check/<stamp>/tour-run.es3`, which the campaign
tours use in place of a co-op slot) and every one-off allowance, and counts what it refused; achievements are not sent
to Steam; the mod's settings change in memory only, and Direct Connections cannot change at all; no backup is taken and
no restore runs; the mod's own records stay in memory; the tour's lobby is private (no rich presence) and admits only
the one-PC stand-in, every other join is refused, and no invite is sent nor Steam's invite window opened: a held invite
or a refused join fails the tour (`FAIL holds:`). A match a tour hosts is recorded, when `RecordOnlineMatches` is on,
into the tour's own folder. The game's save files (Easy Save's, their `.bac` copies, and the files of its `BracketData`
and `TeamDataModels` folders, which the save guard does not hold and no tour opens), the mod's data folder and
`settings.cfg` are hashed at the start and compared at the end, and a tour that logs an ERROR it did not stage fails. A
tour that played anything the game keeps, or ends away from the title, quits the game with every hold on, so the game's
own save at quit cannot write what the holds kept in memory (Core `TourEnd`). The game itself may rewrite `config.es3`
at start-up when it cannot get the display it wants, before the guard exists, and refused commits leave `*.es3.tmp`
files, which the game never reads.

**A tour never presses on a screen it did not mean to be on.** Every step declares where it starts (Core `TourStarts`:
press-to-start, the title's Main, another group, a screen a tour opens over the title, the lobby, a match, the map, and so
on), and before the step the tour reads where the game is. Elsewhere it waits up to 8 seconds for that place and, if it
does not come, makes no press and fails the step as "can't start". Two can't-starts in a row, a step that must end at
the title's Main and does not, or the game on a screen the tours never open **stop the tour**: no further step runs, and
the summary says
`STOPPED: <before|after> <step>: <why>; not run: <ids>; the game quits with every hold on.` Inside a step, a failed
expectation ends that step's presses, and every repeat or mash runs only while its own screen is on top.

**Results.** `dev/ui-check/<stamp>/summary.txt` (a line per step, `OK` or `FAILED`, then the totals, `Files: all N hashed
files are as they were at the start; the backups' M folder names too.`, what the holds refused and any `STOPPED:` line),
`flags.tsv` (the layout flags per screen, for diffing), a PNG and a text dump per screen with layout flags (`TEXT_OVER`,
`AUTOSIZE_MIN`, `CLIPPED`, `OVERLAP`, `NO_GLYPH`, ...), and `Lobby check: step <id> OK` lines in the session log. The
problem reports a tour makes go to the tour's own folder (`<stamp>/reports/`), never to the player's `reports/`, which
nothing prunes during a tour. The newest 5 tour folders are kept; older ones are removed at start-up.

**The folder can contain screenshots of your Steam friends list.** Don't share it (`dev/DO-NOT-SHARE.txt` says so). A
tour's own problem report is the only thing that leaves it: scan that zip with `forbidden_strings.py` like any other.

**Adding a step:** a row in `UiCheckPlan` and an iterator in `LobbyUiCheck`'s `Steps` table, its start in
`TourStarts` (an undeclared step never starts; `UiCheckPlanTests` holds the order rules and `TourGateTests` that every step
declares a start). A step only a key adds goes in that key's own plan (`UiCheckPlan.StepsFor`, `CampaignTour.StepsFor`), so
a tour run without the key runs exactly the steps it always did. A tour's press must be a real press (`PressButtonForCheck` or a virtual pad), not `Trigger`, which skips the
game's own press handling.

## Files the mod writes

Everything the mod keeps is in two folders of its own (plus MelonLoader's copies of the mod's log lines), each with a
`README.txt` that says what it holds and what deleting it loses (written at start-up when missing or different). Nothing
in either is sent anywhere. Every write of the mod's own is whole or not at all: a file is written as `<name>.t2t-part`
and moved into place, and a start-up sweep removes any `*.t2t-part` a stopped game left (a temporary whose finished file
is missing is kept: it may be the only copy). `settings.cfg` is the one exception: MelonLoader's preferences code
writes it, in place. A `settings.cfg` left without its `[T2TOnline]` section is made again with the defaults at the next
start, and one MelonLoader can't read gives the defaults for that session (the log says so). The mod writes nothing
else in the game folder, and never changes or deletes a file of the game. It touches MelonLoader's files in two cases
only: it removes the interop assemblies MelonLoader generated (`MelonLoader/Il2CppAssemblies`) when the generator's own
rules say it is about to remake them (`Startup/StaleInterop.cs`, see "Rules for changing the code"), and an update from
0.10.0 or earlier moves the mod's own section out of MelonLoader's shared `UserData/MelonPreferences.cfg`.

In the game folder, `<game>/UserData/T2TOnline/` (the mod's own; deleting it loses settings, logs and bug reports):
- `settings.cfg`: the mod's settings (see "Settings and keys").
- `README.txt`: what each file here holds.
- `logs/session-<UTC time>.log`: one log per launch, log format 2 (see "Logging"). At most 20 files, 20 MB each (a log is
  cut at its limit), 100 MB in all and 30 days, the newest 5 always kept, the oldest removed first at start-up. Start-up
  also deletes session logs that don't begin with a log-format-2 header.
- `reports/T2TOnline-report-<UTC time>.zip`: problem reports (Settings > Online > Problem Report, the Online menu's Report a Problem, or holding Insert), the newest 5 kept.
- `report-offer.txt`: written when a session ends in an error, the runtime stops, or the game closes during an online game (a kind, a reason and a UTC time, nothing else); it makes the game offer a problem report once at the next idle title.
- `relay-refused.txt`: written when Steam refused relay-only connections at a start (the UTC time only), removed when they hold again; it is what makes Settings > Online show the Direct Connections row.
- `campaign/` and `custom/`: files that last a session at most (a friend's copy of the host's run, the host's run-start
  preview, a host's custom logo written for the game's loader).
- `dev/`: developer output only (`DO-NOT-SHARE.txt`), pruned at start-up and on each write: `streams/*.t2tstream` (match
  recordings, F3 or an online host's matches with `RecordOnlineMatches`; the newest 20, at most 250 MB), `captures/*.t2tsnap`
  (snapshot captures; the newest 10) and `ui-check/` (the check tours, each in its own folder with its reports; the newest 5, and `run.txt` starts one). The old
  `streams/`, `captures/` and `ui-check/` beside `dev/` are moved into it once.

MelonLoader's own files hold the mod's lines too: `MelonLoader/Latest.log` and `MelonLoader/Logs/*.log` (stamped in local
time by MelonLoader; the mod's lines in them pass through the same scrubber). The mod doesn't manage them; a bug report
reads them, converts the stamps to UTC and cleans them.

In `%LOCALAPPDATA%\T2T Online\` (outside the game folder, so a reinstall keeps it):
- `README.txt`.
- `backups/<UTC time>Z-<role>/` (for example `20261105T100000Z-host`): the game's save files and every co-op slot that holds
  a file, with a SHA-256 manifest, taken before every session and at each Start after a game. The newest 10, the first of
  each of the last 7 days that have one, and the first ever taken (`first-backup.txt` marks it) are kept, and while
  `restore-journal.txt` is there, the backup it restores and the one taken before it. Folders from
  0.10.0 and earlier are named in local time (`yyyyMMdd-HHmmss-<role>`) and are read as local time at their date, or by
  their manifest's time; every order is by UTC instant. Restored from the Backups screen (Settings > Online > Save Backups); `restore-journal.txt` exists while a
  restore runs (a restore interrupted by the game closing is finished or undone at the next start, on every launch whether
  or not online is on; when a start that couldn't do that, a mixed install or a game version the mod can't patch, wrote the
  files since, what is there is backed up first, as a restore's own backup is), `steam-cloud-pending.txt` lists restored files Steam Cloud hasn't taken yet, and
  `restore-check.txt` lets the next start confirm a restore of your saves. `<name>.partial` is a backup being written.
- `coop/coop-1.es3` to `coop-3.es3`: a host's co-op save slots. The only copies of those runs. A `.bac` file beside one is
  the game's own leftover copy.
- `progress/`: `progress-ledger.txt` (what a friend's game applied from shared progress, so nothing counts twice, and each
  host's rolling 24-hour tally of the play it shared and the credit it was given, under a key), `ledger-history/` (dated
  copies of the ledger), `run-scopes.txt` (the per-run counters of each run file, put aside and brought back as the game's
  run file changes, on every launch the mod can patch, online or not), `shared-play.txt` (per hosting session, kept under
  a key: which matches of the host this PC played and whether it held the host's co-op run; names no host) and
  `host-key.bin` (32 random bytes made once, this PC's secret for those keys: never logged, sent or put in a bug report;
  deleting it makes the per-host limits start over and forgets the shared play recorded before).
- `history-logos/`: host logos a friend's run history shows (the newest 32, at most 48 MB).
- `keyboard-taken.txt`: on a keyboard player, only while a session holds their keyboard and mouse, so a crash can give
  them back at the next start.

The game's own save folder is the game's. The mod writes there only when a restore from `backups/` puts your saves back
(a restore's own `*.es3.t2t-part` temporaries go at the next start), and `.bac` files there are the game's.

## Settings and keys

Settings live in `<game>/UserData/T2TOnline/settings.cfg`, section `[T2TOnline]` (created with their defaults on first
launch: the nine player entries), and the player's are on Settings > Online (the rest are in the file only). 0.10.0 and earlier kept them in a `[T2TOnline]` section of
MelonLoader's shared `UserData/MelonPreferences.cfg`: the first start moves the values to the new file (the new file wins
if both exist) and removes the old section. Developer entries are hidden from MelonLoader's settings screens, saved only
when not at their default, and still read when added to the file by hand. The log header says which settings aren't at
their defaults (`ModSettings.NonDefaultSummary()`: names and values, never text from the file).

- **Play** (`ShareAchievements`, `ShareProgress`, `OnlineInput` and `AllowDirectConnections` have rows on Settings > Online;
  `CoopSlot` is the lobby's Co-op Save row): `ShareAchievements` (on), `FightGraceWindow` (on, host), `AiStandIn` (on, host: the AI plays a dropped
  friend's skater), `ShareProgress` (on: a campaign run played together counts on every player's own profile), `CoopSlot`
  (1, host: the co-op save slot, 1 to 3), `OnlineInput` (Auto: a connected gamepad, else keyboard and mouse; Gamepad;
  KeyboardMouse), `NurseRealScreen` (on, host: friends see the game's own Nurse procedure screen; off, as text),
  `NativeLobby` (on: the title's Online row and the lobby; off, which only counts with `DevTools` on, F7 and Steam joins run a session without them),
  `AllowDirectConnections` (off; its row, Direct Connections, shows only while it is on or after Steam refused
  relay-only connections: by default every connection is relayed and no address is shared; on,
  Steam may connect players directly and each shares its address with the others. Read when the game starts).
- **Developer** (hidden, off or 0): `ShowStatusBox` (a corner box with the session's state and every notice, and the F9
  network overlay), `DevTools` (the test keys below; read when the game starts, so a change takes effect at the next
  start), `RecordOnlineMatches`, `AllowUntestedBuild` (offline test tools on
  an untested game build; online stays off), `SnapshotMeasure`, `LoopbackDelayMs`, `PlaybackLinkDelayMs`,
  `PlaybackLinkJitterMs`, `PlaybackLinkLossPercent`, `SimulatedLagMs`, `SimulatedJitterMs`, `SimulatedLossPercent`.
- **Keys a player has:** Insert (marks a moment for a problem report: it writes `MARK n (...)` to the log and shows the
  game's toast "Moment n marked for a bug report."; holding it for 2 seconds makes a report where the native screens are
  not installed; it isn't one of the keys forwarded to the host for a keyboard friend). Everything else is on the game's
  own screens: the title's Online row, the lobby, Settings > Online.
- **DevTools keys** (developers only: with `DevTools` off none of them does anything, and the first press of one is
  logged once without naming it): F1 (the panel), F7 (join a friend who is hosting, or host; again to leave), Shift+F7
  (host), Ctrl+F6 (the one-PC test while hosting: a stand-in friend on this PC, played with your controller, or with your
  keyboard when no controller is connected), Ctrl+F5 (a simulated 15 s drop, as a friend), F9 (the network overlay, with
  `ShowStatusBox` too), F2, F3, F4, F8, F10 (a brawl offer), Ctrl+F10 (the next kind of event on the map), F11, Ctrl+F11
  (the run-start carrier's self-test), Ctrl+F12 (the lobby's check tour), Ctrl+F9. Keys act only while the game window is
  focused. While online is off, F1 still opens the panel and F7 puts the notice back on screen.
- **Keyboard players** join at the host's Start like a pad player; Q+E accepts a brawl offer (LB+RB on a pad); their keys
  stop while the game window isn't focused or the panel is open.
- **Joining from Steam:** Join Game on a hosting friend's name, or accepting their invite, joins as the Join list does, and
  starts the game first if it isn't running. While hosting or in a friend's game, the mod sets Steam rich presence: `connect`, a
  group and size, and the status `T2T Online: hosting a co-op game` or `T2T Online: in a friend's co-op game`, with the
  player count (it names nobody).
- **Relay only and friends only:** at start-up the mod sets Steam's `P2P_Transport_ICE_Enable` to 0 unless
  `AllowDirectConnections` is on, reads it back and logs `ICE: relay only (IP addresses are never shared).` (or the direct
  line). If Steam doesn't keep it, online is off for the session with a prompt. A friend whose link stays direct
  while relay only is set leaves the game, but only once every 5 s report for 20 s read it direct (one `session Connected,
  direct` line during a drop is a lost route, not a failure: Steam can drop a link's transport while the link stays up;
  `ICE: the link to P#... has been direct for N s ...` is the real thing); a host only logs a warning. The refusal's prompt
  and log line say to restart the game, because `AllowDirectConnections` is read at start-up. With `DevTools` on, each
  hosting or joining session asks Steam for its own per-connection ICE line (Steam's debug output, at Verbose for the P2P
  rendezvous, for the first connection only, then put back): `ICE: Steam's own line for this connection: "ICE failed 3998
  ICE not enabled by local user options": no ICE on it, so it is relay only, as set.` on both PCs (with relay only that is
  Steam's line; `P2P_Transport_ICE_Enable=...` appears only with direct connections allowed, and a WARN when ICE is on
  although relay only is set). `ICE: DevTools: Steam gave no ICE line for this session's connections ...` says nothing
  about relay only failing. Nothing of this runs with DevTools off. When Steam can't answer whether a player is a
  friend, the host refuses with reject reason `FriendshipUnconfirmed` (10) and the friend's game says so in its own
  prompt.

## The F1 panel (developer tools)

An IMGUI panel over the game (`Online/ModPanel.cs`), used with the mouse. It exists only with `DevTools` on: the players'
build has no panel, and every function a player needs is on the game's own screens (see "The players' build and the native
screens"). F1 opens it; F1, Esc or Close closes it. While it is open the game gets no keyboard or mouse (controllers
still play).

- **Online:** the session, its players and their ping; Host, Find games, Join, Leave; while hosting, Invite friends and the
  seats kept for friends who dropped.
- **Co-op slot:** the three slots with each one's squad, act, floor and save time.
- **Backups:** every backup with what it holds, Back up now (a backup by hand is a developer function; a player gets one
  before every online game), and Restore with a checklist (your saves, each co-op slot). What is chosen is backed up
  first and restored whole or not at all; restoring your saves needs a restart. Players restore from the Backups screen.
- **Settings:** the play settings above and the native lobby switch.
- **Controller:** a check of this PC's controller under your own button maps: a live grid of a standard pad's 14 buttons
  and 6 axes with the game actions each gives, trigger peaks against the game's 0.50 threshold, and how to fix a trigger
  that stops short. While hosting, each friend's controller and the actions it has sent. In a friend's game, "Change my
  controls" and "Your game's settings" open your own game's menus.
- **Help:** Bug reports first: **Make a bug report**, **Mark this moment**, **Open the logs folder** and **Open data
  folder**, then the keys, joining from Steam and playing on the keyboard. The tab scrolls a block at a time when it
  doesn't fit (the mouse wheel over it, the arrow keys, Page Up and Page Down, Home and End, or Up and Down on its last
  row).

## Bug reports

(`Diagnostics/BugReports.cs`, Core `BugReportBuilder`, `NativeUi/ReportFlow.cs`.) A report is asked for from Settings >
Online > Problem Report, the Online menu's Report a Problem, the offer at the title after an error, holding Insert for
2 seconds (only where the native screens are not installed) or, for developers, the panel's Help tab. It works whether
or not online is on, even while the game runs, and writes `reports/T2TOnline-report-<UTC time>.zip` on a background
thread (a temporary `.t2t-part` moved whole). When it is ready, `ReportFlow` shows the game's prompt: where the zip is
and where to post it (Core `ProjectLinks.IssuesShort`, the one place the address is written for a screen), with **Close**
and, only while the title is the active scene, **Show File**, which opens Explorer with the zip selected (so Explorer never
takes the focus from a match); in a match or elsewhere a one-button notice says where it is. The log says what it
offered: `Bug report: ready, shown on the game's prompt (Show File offered|not offered: in a match|not offered: away from
the title).` The report reads only
an allow-list (Core `ReportAllowList`):
the newest 3 session logs with a log-format-2 header, MelonLoader's newest 2 launch logs (left out when older than
format 2, which logged Steam names) with every time stamp converted from local time to UTC, and Unity's `Player.log` and
`Player-prev.log` with the graphics card reduced to its maker and driver. Every line goes through `LogScrub`. A file is cut
to its first 1 MiB and last 3 MiB (4 MiB at most), a report stops at 16 MiB, and a file left out says why. `report.txt`
lists the build, MelonLoader and OS versions, the settings not at their defaults, other mods and other mod loaders'
files, this PC's tags from its logs, the marks, each file's size and SHA-256, what was cleaned and what was left out
(including files the allow-list refused). The game's own lines that print a custom team's city, a skater's or a logo's
name (`Loaded custom team from team id: ...`, `Could not find logo: ...`, `Player ... was hit`, scene paths under
`Hockey/Teams/...`) are reduced to `<a name: N characters, #hash>` by Core `GameLogText`, in the report and at the source
in the log tap that copies the game's warnings and errors into the mod's log. Never read:
`progress\` (including `host-key.bin`), the data folder, settings, `dev\`, saves and backups, crash dumps. A mark (Insert,
or the panel's button) writes `MARK n (in a match | in a menu | at the title) at HH:mm:ss UTC: ...` and shows the game's
toast "Moment n marked for a bug report." (on a tested game build; otherwise a corner note). The cleaner also knows this
PC's own Steam name from sign-in (registered when Steam is signed in, every 30 seconds online and before every report),
so a report made before any lobby cleans it too; the name is never logged. The panel logs each click ("Panel: ...");
an error closes the panel and gives the input back.
