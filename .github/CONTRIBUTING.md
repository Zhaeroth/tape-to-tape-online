# Contributing to T2T Online

Thanks for helping out. Bug reports, translation fixes and ideas are all welcome, and so is code.

## Reporting a bug

Open an issue and choose **Bug report**. For a question about installing or playing, ask in
[Discussions](https://github.com/Zhaeroth/tape-to-tape-online/discussions) instead. The reports that get fixed fastest
include:

- the problem report zip: press Insert when it happens (the game says "Moment 1 marked for a bug report."), then in the
  game open **Settings > Online** and choose **Problem Report** > **Make Report** (or, on the title screen, **Online** >
  **Report a Problem**), and attach the zip it writes to `UserData\T2TOnline\reports\`. It holds cleaned logs, and no
  Steam names, Steam IDs, folder paths, IP addresses or local times;
- the T2T Online version (in the report's `report.txt`, and in the name of the zip you installed);
- whether you were hosting or joining, and how many players were in the session;
- which mark the problem is at, and a screenshot if it is on screen (not one that shows your Steam friends list).

Please don't attach raw log files (`MelonLoader\Latest.log`, a session log, `Player.log`): MelonLoader's and Unity's
logs hold folder paths with your Windows user name, and session logs of 0.10.0 or earlier hold Steam names. Only the
problem report zip is cleaned. If the game won't start, or has no Online tab in its Settings (hold Insert for 2 seconds
to make the zip there), say so and the maintainers will say what to send.

## Fixing a translation

The mod's screens are translated into the game's 15 languages, and the translations haven't been checked by native
speakers yet. Open an issue and choose **Translation fix**: it asks for the language, the screen, the text as it reads
now and what it should say.

## Suggesting a feature

Check the [roadmap](../README.md#roadmap) first, then open an issue and choose **Feature request**.

## Changing the code

[docs/DEVELOPMENT.md](../docs/DEVELOPMENT.md) covers the architecture, building, testing and the rules for changing the
code. Read its "Rules for changing the code" before your first change. In short:

- The plugin builds only against a local Tape to Tape install with MelonLoader 0.7.3, because it compiles against the
  interop assemblies MelonLoader generates from the game. The Core and Steam tests need neither, and they run on every
  pull request. The .NET SDK is pinned to 8.0.427 in `global.json` (CI installs exactly that), and NuGet packages have
  lock files (`packages.lock.json`) that CI restores in locked mode: a change to a package reference includes its lock
  file.
- Nothing the mod writes to a log, a problem report or its own notes may hold a Steam name or ID, lobby ID, IP address,
  folder path, local time or player-written text: use the log tags and labels. `tools/checks/logcheck.py` (in the gate)
  enforces it.
- Logic that can be tested without the game goes in `T2TOnline.Core`, with xUnit tests. The plugin stays a thin layer
  over the game.
- Nothing may throw into a game callback, network input is untrusted, and a friend's run save is never written.
- Warnings are errors, and every public member has XML documentation.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) (`fix(lobby): ...`,
  `feat(stream): ...`, `docs: ...`).
- Keep each pull request to one change, and say how you checked it. If it can only be checked in the game, say what you
  saw there.

By contributing, you agree that your contribution is licensed under the [MIT License](../LICENSE).
