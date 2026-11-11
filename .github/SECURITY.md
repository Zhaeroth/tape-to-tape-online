# Security policy

## Supported versions

Only the latest release gets fixes.

## Reporting a vulnerability

Please don't open a public issue for a security problem. Report it privately through GitHub's private vulnerability
reporting: open the repository's **Security** tab and choose **Report a vulnerability**.

Include the T2T Online version, what an attacker could do, and the steps to reproduce it. Don't attach a problem report or
logs from a version before 0.11.0: they hold Steam names. Fixes ship in a new release, with credit to the reporter if
they want it.

## Scope

T2T Online exchanges game data with the other players in a session, through Steam. In scope:

- anything another player can send that crashes, freezes or takes over a player's game;
- anything a host can make a friend's game write to the friend's progress, achievements or save files beyond what the
  friend's own play with that host explains (the friend's game bounds this; see the README's Privacy section);
- anything that writes to or damages a player's save files;
- anything that makes the mod write a Steam name, Steam ID, IP address, folder path or local time to a log or a problem
  report, or share an IP address with another player while relay-only connections are on;
- anything that sends data outside the session (the mod uploads nothing).

Out of the mod's control, and for their own developers: Tape to Tape itself; MelonLoader, including its calls to
`api.melonloader.com` and GitHub and its loading of every DLL in `Mods`, `Plugins` and `UserLibs`; Steam; and Unity's
native crash handler, which may send a report of a crash of the game itself to Unity (the mod can't switch it off).
Release zips are checked by SHA-256 digests (see the README's "Check your download"); T2T Online's files are not
code-signed.
