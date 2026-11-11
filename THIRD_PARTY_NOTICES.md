# Third-party notices

T2T Online's own code is under the MIT License (see `LICENSE`). The release zip holds only the mod's three assemblies,
their debug symbols and its documents, and no third-party binaries. One part of its code follows another project's
rules (see "Code written after MelonLoader's"). It runs on the components below, which keep their own licenses.

## MelonLoader 0.7.3 (required, installed separately)

T2T Online is a MelonLoader plugin and is tested with MelonLoader **0.7.3** only.

- Project: https://github.com/LavaGang/MelonLoader
- Release used: https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3 (`MelonLoader.x64.zip`)
- Copyright 2020 - 2022 Lava Gang. Licensed under the Apache License, Version 2.0 (the license file at the root of
  https://github.com/LavaGang/MelonLoader).

The standard T2T Online download does not include MelonLoader. Where a T2T Online package does include it, it is the
official 0.7.3 release, byte for byte and checked against its published SHA-256, with its own license, notice and readme
files in `MelonLoader/Documentation/`.

MelonLoader bundles third-party libraries under their own licenses. Its README lists them; at 0.7.3:

| Library | License | Source |
|---|---|---|
| Dobby | Apache-2.0 | https://github.com/jmpews/Dobby |
| plthook | BSD-2-Clause | https://github.com/kubo/plthook |
| Mono (Unity's fork) | Multiple | https://github.com/Unity-Technologies/mono |
| HarmonyX | MIT | https://github.com/BepInEx/HarmonyX |
| MonoMod | MIT | https://github.com/MonoMod/MonoMod |
| Mono.Cecil | MIT | https://github.com/jbevain/cecil |
| Newtonsoft.Json | MIT | https://github.com/JamesNK/Newtonsoft.Json |
| TinyJSON | MIT | https://github.com/pbhogan/TinyJSON |
| Tomlet | MIT | https://github.com/SamboyCoding/Tomlet |
| AsmResolver | MIT | https://github.com/Washi1337/AsmResolver |
| SharpZipLib | MIT | https://github.com/icsharpcode/SharpZipLib |
| Semver | MIT | https://github.com/maxhauser/semver |
| Illusion Plugin Architecture | MIT | https://github.com/Eusth/IPA |
| MuseDashModLoader | MIT | https://github.com/mo10/MuseDashModLoader |
| mgGif | MIT | https://github.com/gwaredd/mgGif |
| AssetsTools.NET | MIT | https://github.com/nesrak1/AssetsTools.NET |
| AssetRipper.VersionUtilities | MIT | https://github.com/AssetRipper/VersionUtilities |
| SteamFinder (Steam library parsing) | Credited in MelonLoader's README, which names no license for it | https://github.com/Umbranoxio/BeatSaberModInstaller |
| bHapticsLib | MIT | https://github.com/HerpDerpinstine/bHapticsLib |
| IndexRange | MIT | https://github.com/bgrainger/IndexRange |
| ValueTupleBridge | MIT | https://github.com/OrangeCube/MinimumAsyncBridge |
| WebSocketDotNet | MIT | https://github.com/SamboyCoding/WebSocketDotNet |
| Pastel | MIT | https://github.com/silkfire/Pastel |
| Il2CppInterop | LGPL-3.0 | https://github.com/BepInEx/Il2CppInterop |

Il2CppInterop's corresponding source for the version MelonLoader 0.7.3 uses is available from the Il2CppInterop
repository above and from MelonLoader's source at the v0.7.3 tag.

On its first launch MelonLoader downloads further tools under their own terms: Cpp2IL (MIT,
https://github.com/SamboyCoding/Cpp2IL), Unity runtime libraries (subject to the Unity Terms of Service), and the .NET
runtime (MIT, https://github.com/dotnet/runtime).

## Code written after MelonLoader's

The install check (`src/T2TOnline.Plugin/Startup/InstallLayout.cs`, in `T2TOnline.dll`) and the release check
(`tools/checks/zipcheck.py`) re-implement the rules by which MelonLoader 0.7.3 chooses the folders and assemblies it
loads (`MelonLoader.Melons.MelonFolderHandler` and `MelonPreprocessor`), written from a reading of MelonLoader's source
so that both give MelonLoader's own answer. MelonLoader is Copyright 2020 - 2022 Lava Gang, licensed under the Apache
License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0). Its notice: "This product contains software
(https://github.com/LavaGang/MelonLoader) developed by Lava Gang, licensed under the Apache-2.0 license."

## Build and test only (not shipped)

| Package | License |
|---|---|
| xUnit (`xunit`, `xunit.runner.visualstudio`) | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |

These are the packages the test projects reference. The packages they bring in with them are recorded, with their exact
versions, in each test project's `packages.lock.json`.

## The game and its platform

Tape to Tape is a game by Excellent Rectangle. T2T Online is an unofficial mod: it is not affiliated with, sponsored by
or endorsed by Excellent Rectangle, Lava Gang or Valve. It calls the Steamworks library that ships with the game and
redistributes nothing of the game or of Steam: `T2TOnline.Steam` declares the functions, structures and constants of the
Steamworks API it calls, by their names and layouts, and holds no code from the Steamworks SDK. Tape to Tape, Steam and
other names belong to their owners.
